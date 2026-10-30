using System.Text;
using System.Text.Json;
using Model.Agent;
using Model.EmailPush;
using Model.HourlyProduction;
using Model.WeChatClawBot;

namespace Service;

public sealed partial class HourlyProductionService
{
    private async Task ScheduleReportsAsync(List<HourlyProductionBoardEntity> rows, DateTime now, CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var board = Definition(row);
            var slots = Enumerable.Range(0, 49).SelectMany(h=>HourlyProductionRules.Slots(board,now.AddHours(-h))).Distinct()
                .Where(slot => slot.End.AddMinutes(board.Push.DelayMinutes) <= now &&
                    slot.End > now.AddHours(-board.Push.CatchUpHours) && slot.End >= row.UpdateTime)
                .OrderBy(slot => slot.Start);
            foreach (var slot in slots)
            {
                var key = HourlyProductionRules.Hash(board.Code + "\u001f" + slot.Start.ToString("O"));
                var report = await Db.Queryable<HourlyProductionReportEntity>().FirstAsync(x => x.ReportKey == key);
                if (report == null)
                {
                    var runtime = await BuildRuntimeAsync(board, slot.End, slot.Start);
                    // AI是当前时刻的建议，不混入此前小时的已冻结报表。
                    runtime.Advice = null;
                    report = new HourlyProductionReportEntity
                    {
                        ReportKey = key, BoardCode = board.Code, SlotStart = slot.Start,
                        ConfigVersion = board.Version, SnapshotJson = JsonSerializer.Serialize(runtime), CreatedAt = now
                    };
                    report.Id = await Db.Insertable(report).ExecuteReturnBigIdentityAsync();
                }
                if (!board.Push.Enabled) continue;
                var snapshot = JsonSerializer.Deserialize<HourlyBoardRuntime>(report.SnapshotJson)!;
                var content = FormatReport(snapshot, slot.Start, board.Push.LineCodes);
                foreach (var channel in new[] { "WeChat", "Email" })
                foreach (var recipient in (channel == "WeChat" ? board.Push.WeChatUserIds : board.Push.EmailUserIds).Distinct())
                {
                    var parts = channel == "WeChat" ? content.Chunk(7500).Select(chunk => new string(chunk)).ToArray() : [content];
                    for (var part = 0; part < parts.Length; part++)
                    {
                        var jobKey = HourlyProductionRules.Hash(report.ReportKey + "\u001f" + channel + "\u001f" + recipient + "\u001f" + part);
                        if (await Db.Queryable<HourlyProductionDispatchEntity>().AnyAsync(x => x.JobKey == jobKey)) continue;
                        await Db.Insertable(new HourlyProductionDispatchEntity
                        {
                            JobKey = jobKey, BoardCode = board.Code, ReportId = report.Id, Channel = channel,
                            RecipientId = recipient, OwnerId = row.UpdatedBy, AppCode = row.AppCode,
                            Content = parts.Length > 1 ? $"小时报表（{part + 1}/{parts.Length}）\n{parts[part]}" : parts[part], CreatedAt = now
                        }).ExecuteCommandAsync();
                    }
                }
            }
        }
    }

    private static string FormatReport(HourlyBoardRuntime runtime, DateTime slot, List<string> selectedLines)
    {
        var end = runtime.Lines.SelectMany(x => x.Processes).SelectMany(x => x.Points).First(x => x.Start == slot).End;
        var text = new StringBuilder($"{runtime.Name}\n{slot:MM-dd HH:mm}—{end:HH:mm}\n");
        foreach (var line in runtime.Lines.Where(x => selectedLines.Count == 0 || selectedLines.Contains(x.Code)))
        {
            var total = line.SummaryPoints.Any(x => x.Status != "Unbound") ? line.TotalOutput.ToString("0.##") : "未绑定";
            text.AppendLine($"【{line.Name}】班次累计 {total}，汇总口径：{line.SummaryProcessName}");
            if (line.LatestCompleted is { } latest)
            {
                var target = latest.Target.HasValue ? $"目标 {latest.Target:0.##}，达成率 {latest.Achievement:0.#}%" : "未设置目标";
                text.AppendLine($"线体本小时：产出 {latest.Output?.ToString("0.##") ?? "未绑定"}，返工 {latest.Rework?.ToString("0.##") ?? "—"}，{target}");
            }
            foreach (var process in line.Processes)
            {
                var point = process.Points.FirstOrDefault(x => x.Start == slot);
                if (point == null) continue;
                var value = point.Output.HasValue ? point.Output.Value.ToString("0.##") : "未绑定";
                var target = point.Target.HasValue ? $"目标 {point.Target:0.##}，达成 {point.Achievement:0.#}%" : "未设置目标";
                text.AppendLine($"{process.Name}：产出 {value}，返工 {point.Rework:0.##}，{target}" +
                    (point.Status == "Achieved" ? " ✓达标" : ""));
            }
        }
        return text.ToString();
    }

    private async Task DispatchReportsAsync(CancellationToken cancellationToken)
    {
        var now = HourlyProductionRules.Now;
        var jobs = await Db.Queryable<HourlyProductionDispatchEntity>()
            .Where(x => x.Status == "Pending" && x.Attempts < 10 && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(x => x.Id).Take(10).ToListAsync();
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var access = await applications.GetChannelAccessAsync(job.OwnerId, job.AppCode);
                Require(access, true);
                var (boardRow, _) = await GetAsync(access, job.BoardCode, false);
                var push = Definition(boardRow).Push;
                if (!push.Enabled || !(job.Channel == "Email" ? push.EmailUserIds : push.WeChatUserIds).Contains(job.RecipientId))
                    throw new UnauthorizedAccessException("小时报表推送已关闭，或接收人已从计划移除");
                // 这里只提交已有可靠队列，不在统计服务中调用微信或SMTP网络。
                object result = job.Channel == "Email"
                    ? await email.EnqueueAsync(access, new EmailEnqueueDto
                    {
                        RecipientUserIds = [job.RecipientId], Subject = "小时产量统计报表",
                        Body = job.Content, IdempotencyKey = job.JobKey, Confirm = true
                    }, "HourlyReport")
                    : await wechat.EnqueueAsync(access, new WeChatPushEnqueueDto
                    {
                        RecipientUserIds = [job.RecipientId], Content = job.Content, IdempotencyKey = job.JobKey,
                        ExternalTaskId = job.Id.ToString(), SourceLabel = "HourlyReport", Confirm = true
                    });
                job.Status = "Queued";
                job.QueueResultJson = JsonSerializer.Serialize(result);
                job.Error = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                job.Attempts++;
                job.Status = exception is UnauthorizedAccessException ? "Cancelled" : job.Attempts >= 10 ? "Failed" : "Pending";
                job.Error = exception.Message[..Math.Min(1000, exception.Message.Length)];
                job.NextAttemptAt = now.AddSeconds(Math.Min(900, 10 * Math.Pow(2, job.Attempts - 1)));
            }
            await Db.Updateable(job).ExecuteCommandAsync();
        }
    }

    /// <summary>独立后台循环调用；统计和读取接口从不等待模型请求。</summary>
    public async Task RefreshAdviceAsync(CancellationToken cancellationToken)
    {
        Ready();
        using var operation = DatabaseOperationScope.Begin(scope);
        var boards = await Db.Queryable<HourlyProductionBoardEntity>().ToListAsync();
        foreach (var row in boards)
        {
            var board = Definition(row);
            if (!board.AiEnabled) continue;
            var now = HourlyProductionRules.Now;
            var advice = await Db.Queryable<HourlyProductionAdviceEntity>().InSingleAsync(board.Code);
            if (advice == null)
            {
                advice = new HourlyProductionAdviceEntity { BoardCode = board.Code, AttemptedAt = now.AddDays(-1) };
                await Db.Insertable(advice).ExecuteCommandAsync();
            }
            var cutoff = now.AddMinutes(-board.AiIntervalMinutes);
            if (await Db.Updateable<HourlyProductionAdviceEntity>().SetColumns(x => x.AttemptedAt == now)
                    .Where(x => x.BoardCode == board.Code && x.AttemptedAt < cutoff).ExecuteCommandAsync() == 0) continue;
            try
            {
                var runtime = await BuildRuntimeAsync(board, now);
                runtime.Advice = null;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var reply = await model.GenerateReplyAsync(new AgentModelRequest
                {
                    AgentCode = board.AiAgentCode, UserId = AgentModelConfigurationScope.SystemOwnerUserId,
                    SystemPrompt = "你是产线小时统计分析助手。只根据提供的真实统计，比较所提供线体同工序的产出、返工、目标达成及衔接。未来时段、未设置目标、未绑定不可当成异常；统计中不能认定最终未达标。不执行控制。直接返回120字内中文结果建议，不要依据段落、不要Markdown、不要编造原因。无可用数据返回空字符串。",
                    UserMessage = JsonSerializer.Serialize(runtime)
                }, timeout.Token);
                advice.Content = reply.Success && !string.IsNullOrWhiteSpace(reply.Content) && reply.Content.Length <= 500
                    ? reply.Content.Trim() : null;
                advice.ExpiresAt = advice.Content == null ? null : now.AddMinutes(board.AiIntervalMinutes + 1);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                advice.Content = null;
                advice.ExpiresAt = null;
            }
            advice.AttemptedAt = now;
            await Db.Updateable(advice).ExecuteCommandAsync();
        }
    }
}

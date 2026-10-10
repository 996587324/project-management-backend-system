using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Model.EdgeGateway;
using SqlSugar;

namespace Service;

public sealed partial class EdgeDeviceCardPushService
{
    private static readonly SemaphoreSlim QueueSettingGate = new(1, 1);
    private const int MaximumMessageLength = 10000;

    public async Task<EdgeDeviceCardPushQueueSettingDto> GetQueueSettingAsync()
    {
        using var operation = DatabaseOperationScope.Begin(_dbScope);
        var setting = await GetOrCreateQueueSettingAsync();
        return new EdgeDeviceCardPushQueueSettingDto { IntervalSeconds = setting.IntervalSeconds };
    }

    public async Task SaveQueueSettingAsync(EdgeDeviceCardPushQueueSettingDto dto)
    {
        if (dto.IntervalSeconds is < 1 or > 86400)
            throw new InvalidOperationException("队列发送间隔必须为 1～86400 秒");
        using var operation = DatabaseOperationScope.Begin(_dbScope);
        await GetOrCreateQueueSettingAsync();
        // 不清空当前节流时间/租约，修改配置不能绕过正在进行的发送。
        await Db.Updateable<EdgeDeviceCardPushQueueSettingEntity>()
            .SetColumns(x => new EdgeDeviceCardPushQueueSettingEntity
            {
                IntervalSeconds = dto.IntervalSeconds, UpdateTime = DateTime.Now
            }).Where(x => x.Id == 1).ExecuteCommandAsync();
    }

    private async Task<EdgeDeviceCardPushQueueSettingEntity> GetOrCreateQueueSettingAsync()
    {
        await QueueSettingGate.WaitAsync();
        try
        {
            var existing = await Db.Queryable<EdgeDeviceCardPushQueueSettingEntity>().InSingleAsync(1);
            if (existing != null) return existing;
            var setting = new EdgeDeviceCardPushQueueSettingEntity();
            try
            {
                await Db.Insertable(setting).ExecuteCommandAsync();
            }
            catch
            {
                // 多进程首次访问：固定主键唯一，只容忍另一进程已插入的情况。
                var concurrent = await Db.Queryable<EdgeDeviceCardPushQueueSettingEntity>().InSingleAsync(1);
                if (concurrent != null) return concurrent;
                throw;
            }
            return setting;
        }
        finally
        {
            QueueSettingGate.Release();
        }
    }

    private async Task DispatchQueueBatchAsync(CancellationToken cancellationToken)
    {
        var setting = await GetOrCreateQueueSettingAsync();
        var now = DateTime.Now;
        if (setting.NextSendTime > now || setting.LeaseExpireTime > now) return;
        var matureBefore = now.AddSeconds(-setting.IntervalSeconds);
        var first = await Db.Queryable<EdgeDeviceCardPushTaskEntity>()
            .Where(x => (x.Status == "Pending" || x.Status == "Retrying") && x.NextAttemptTime <= now &&
                x.CreateTime <= matureBefore)
            .OrderBy(x => x.Id).FirstAsync();
        if (first == null) return;

        var owner = Guid.NewGuid().ToString("N");
        var claimed = await Db.Updateable<EdgeDeviceCardPushQueueSettingEntity>()
            .SetColumns(x => new EdgeDeviceCardPushQueueSettingEntity
            {
                LeaseOwner = owner, LeaseExpireTime = now.AddMinutes(10)
            }).Where(x => x.Id == 1 && (x.LeaseExpireTime == null || x.LeaseExpireTime <= now) &&
                (x.NextSendTime == null || x.NextSendTime <= now)).ExecuteCommandAsync();
        if (claimed == 0) return;

        var attemptedSend = false;
        try
        {
            // 一轮只发送一个接收人的一个合并包；不同用户、不同微信绑定绝不混包。
            var candidates = await Db.Queryable<EdgeDeviceCardPushTaskEntity>()
                .Where(x => (x.Status == "Pending" || x.Status == "Retrying") && x.NextAttemptTime <= now &&
                    x.CreateTime <= now && x.UserId == first.UserId && x.BindingIdentity == first.BindingIdentity)
                .OrderBy(x => x.Id).Take(50).ToListAsync();
            var prepared = new List<EdgeDeviceCardPushTaskEntity>();
            var length = 100; // 合并标题及分隔符的预留空间。
            foreach (var task in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (prepared.Count > 0 && length + task.Content.Length + 10 > MaximumMessageLength) break;
                if (!await PrepareTaskAsync(task)) continue;
                prepared.Add(task);
                length += task.Content.Length + 10;
            }
            if (prepared.Count == 0) return;
            cancellationToken.ThrowIfCancellationRequested();
            // 查询校验可能较慢；发送前续租，避免租约已被其他进程接管后仍向微信发包。
            var sendingAt = DateTime.Now;
            var sendingLease = sendingAt.AddMinutes(10);
            var renewed = await Db.Updateable<EdgeDeviceCardPushQueueSettingEntity>()
                .SetColumns(x => x.LeaseExpireTime == sendingLease)
                .Where(x => x.Id == 1 && x.LeaseOwner == owner && x.LeaseExpireTime > sendingAt)
                .ExecuteCommandAsync();
            if (renewed == 0)
            {
                foreach (var task in prepared)
                {
                    task.Status = "Unknown";
                    task.ErrorMessage = "发送租约已失效，请核实队列状态；本处理器未继续发送";
                    await CompleteTaskAsync(task);
                }
                return;
            }
            var ids = prepared.Select(x => x.Id).ToArray();
            await Db.Updateable<EdgeDeviceCardPushTaskEntity>()
                .SetColumns(x => x.LeaseExpireTime == sendingLease)
                .Where(x => ids.Contains(x.Id) && x.Status == "Processing").ExecuteCommandAsync();
            attemptedSend = true;
            await SendBatchAsync(prepared, cancellationToken);
        }
        finally
        {
            // 包含失败/超时的发送尝试也占用节流窗口，不能失败后立即高频重试。
            var interval = (await Db.Queryable<EdgeDeviceCardPushQueueSettingEntity>().InSingleAsync(1)).IntervalSeconds;
            var next = attemptedSend ? DateTime.Now.AddSeconds(interval) : setting.NextSendTime;
            await Db.Updateable<EdgeDeviceCardPushQueueSettingEntity>()
                .SetColumns(x => new EdgeDeviceCardPushQueueSettingEntity
                {
                    LeaseOwner = null, LeaseExpireTime = null, NextSendTime = next
                }).Where(x => x.Id == 1 && x.LeaseOwner == owner).ExecuteCommandAsync();
        }
    }

    private async Task SendBatchAsync(List<EdgeDeviceCardPushTaskEntity> tasks, CancellationToken cancellationToken)
    {
        var content = tasks.Count == 1 ? tasks[0].Content :
            $"【设备字段变更汇总】共 {tasks.Count} 条\n\n" + string.Join("\n\n────\n\n", tasks.Select(x => x.Content));
        var identity = JsonSerializer.Serialize(tasks.Select(x => new { x.Id, x.AttemptCount }));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        try
        {
            var first = tasks[0];
            var delivery = await _weChat.SendSystemMessageAsync(first.UserId, content,
                "field-batch:" + key, "batch:" + key, cancellationToken, first.BindingIdentity);
            foreach (var task in tasks)
            {
                task.DeliveryId = delivery.Id;
                task.Status = delivery.Status == "Sent" ? "Sent" : delivery.Status == "Failed" ? "Failed" : "Unknown";
                task.SentTime = delivery.SentTime;
                task.ErrorMessage = delivery.ErrorMessage;
                if (task.Status == "Failed" && task.AttemptCount < 3)
                {
                    task.Status = "Retrying";
                    task.NextAttemptTime = DateTime.Now.AddSeconds(task.AttemptCount == 1 ? 30 : 120);
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            foreach (var task in tasks)
            {
                task.Status = "Blocked";
                task.ErrorMessage = ex.Message;
            }
        }
        catch (Exception ex)
        {
            foreach (var task in tasks)
            {
                task.Status = "Unknown";
                task.ErrorMessage = ex.Message;
            }
        }
        // 保留每条原始变更和接收人的结果；同一包共享 DeliveryId，可从审计定位合并消息。
        foreach (var task in tasks) await CompleteTaskAsync(task);
    }
}

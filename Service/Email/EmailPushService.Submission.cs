using Model.EmailPush;
using Model.SystemApplications;
using Model.User;
using SqlSugar;

namespace Service;

public sealed partial class EmailPushService
{
    public async Task<EmailPage<EmailRecipientDto>> GetRecipientsAsync(
        ApplicationAccessContext access, string? keyword, int current, int size)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await RequireSenderAsync(access);
        return await QueryRecipientsAsync(keyword, current, size);
    }

    private async Task<EmailPage<EmailRecipientDto>> QueryRecipientsAsync(string? keyword, int current, int size)
    {
        current = Math.Max(1, current);
        size = Math.Clamp(size, 1, 100);
        keyword = keyword?.Trim();
        if (keyword?.Length > 128) throw new ArgumentException("搜索内容过长");
        var query = Db.Queryable<SysUserInfo>().Where(x => x.Enabled);
        if (!string.IsNullOrEmpty(keyword))
            query = query.Where(x => x.UserName.Contains(keyword) || x.NickName.Contains(keyword) || x.Email.Contains(keyword));
        RefAsync<int> total = 0;
        var users = await query.OrderBy(x => x.UserName).ToPageListAsync(current, size, total);
        return new EmailPage<EmailRecipientDto>
        {
            Current = current, Size = size, Total = total,
            Records = users.Select(user => new EmailRecipientDto
            {
                UserId = user.UserId, UserName = user.UserName, NickName = user.NickName,
                Email = user.Email, Available = IsValidAddress(user.Email)
            }).ToList()
        };
    }

    public async Task<EmailEnqueueResult> EnqueueAsync(ApplicationAccessContext access, EmailEnqueueDto dto, string source = "Manual")
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await RequireSenderAsync(access);
        return await EnqueueCoreAsync(access.UserId, access.AppCode, dto, source);
    }

    private async Task<EmailEnqueueResult> EnqueueCoreAsync(string createdBy, string appCode, EmailEnqueueDto dto, string source)
    {
        RequireReady();
        if (!dto.Confirm) throw new ArgumentException("发送邮件必须明确确认");
        if (string.IsNullOrWhiteSpace(dto.IdempotencyKey) || dto.IdempotencyKey.Length > 128)
            throw new ArgumentException("必须提供长度不超过 128 的幂等标识");
        if (string.IsNullOrWhiteSpace(dto.Subject) || dto.Subject.Length > 160 || dto.Subject.Contains('\r') || dto.Subject.Contains('\n'))
            throw new ArgumentException("邮件主题不能为空、不超过160字且不能包含换行");
        if (string.IsNullOrWhiteSpace(dto.Body) || dto.Body.Length > 100000)
            throw new ArgumentException("邮件正文不能为空且不得超过100000字");
        var toIds = dto.RecipientUserIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var ccIds = dto.CcUserIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Except(toIds, StringComparer.OrdinalIgnoreCase).ToArray();
        var ids = toIds.Concat(ccIds).ToArray();
        if (toIds.Length == 0 || ids.Length > 100 || ids.Any(x => x.Length > 64))
            throw new ArgumentException("至少选择一个收件用户，单次最多100个收件及抄送用户");
        var requestKey = Hash($"{appCode}:{createdBy}:{source}:{dto.IdempotencyKey}");
        var payloadHash = Hash(System.Text.Json.JsonSerializer.Serialize(new
        {
            to = toIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            cc = ccIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), subject = dto.Subject.Trim(), dto.Body
        }));
        var old = await Db.Queryable<EmailPushTaskEntity>().FirstAsync(x => x.RequestKey == requestKey);
        if (old != null) return ExistingTask(old, payloadHash);
        var setting = await Db.Queryable<EmailPushSettingEntity>().InSingleAsync(1);
        if (!setting.Enabled) throw new InvalidOperationException("邮件服务未启用，请联系管理员配置");
        var users = await Db.Queryable<SysUserInfo>().Where(x => ids.Contains(x.UserId)).ToListAsync();
        if (users.Count != ids.Length || users.Any(x => !x.Enabled || !IsValidAddress(x.Email)))
            throw new ArgumentException("存在已停用、不存在或邮箱无效的接收用户，请重新选择");
        var task = new EmailPushTaskEntity
        {
            RequestKey = requestKey, PayloadHash = payloadHash, CreatedBy = createdBy,
            AppCode = appCode, Source = source, Subject = dto.Subject.Trim(), Body = dto.Body
        };
        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            task.Id = await Db.Insertable(task).ExecuteReturnBigIdentityAsync();
            // One envelope per distinct mailbox prevents exposing other recipients and allows individual outcomes.
            var deliveries = ids.Select(id => users.Single(x => x.UserId.Equals(id, StringComparison.OrdinalIgnoreCase)))
                .DistinctBy(x => x.Email.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(user => new EmailPushDeliveryEntity
                {
                    TaskId = task.Id, RecipientUserId = user.UserId, Address = user.Email.Trim(),
                    RecipientKind = toIds.Contains(user.UserId, StringComparer.OrdinalIgnoreCase) ? "To" : "Cc"
                }).ToArray();
            await Db.Insertable(deliveries).ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess)
        {
            old = await Db.Queryable<EmailPushTaskEntity>().FirstAsync(x => x.RequestKey == requestKey);
            if (old != null) return ExistingTask(old, payloadHash);
            throw transaction.ErrorException ?? new InvalidOperationException("邮件任务保存失败");
        }
        return new EmailEnqueueResult { TaskId = task.Id };
    }

    private static EmailEnqueueResult ExistingTask(EmailPushTaskEntity task, string payloadHash)
    {
        if (task.PayloadHash != payloadHash) throw new ArgumentException("幂等标识已用于其他邮件内容，请重新生成");
        return new EmailEnqueueResult
        {
            TaskId = task.Id, Duplicate = true, Status = "Existing", Message = "该邮件已提交，本次未重复入队"
        };
    }

    public async Task<EmailPage<EmailRecordDto>> GetRecordsAsync(ApplicationAccessContext access, EmailRecordQuery input)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var administrator = await IsAdministratorAsync(access);
        return await QueryRecordsAsync(input, administrator ? null : access.UserId, administrator ? null : access.AppCode);
    }

    private async Task<EmailPage<EmailRecordDto>> QueryRecordsAsync(EmailRecordQuery input, string? fixedOwner, string? fixedApp)
    {
        RequireReady();
        var query = Db.Queryable<EmailPushDeliveryEntity>()
            .InnerJoin<EmailPushTaskEntity>((delivery, task) => delivery.TaskId == task.Id);
        if (fixedOwner != null) query = query.Where((delivery, task) => task.CreatedBy == fixedOwner);
        if (fixedApp != null) query = query.Where((delivery, task) => task.AppCode == fixedApp);
        var keyword = input.Keyword?.Trim();
        if (!string.IsNullOrWhiteSpace(input.CreatedBy))
            query = query.Where((delivery, task) => task.CreatedBy == input.CreatedBy);
        if (!string.IsNullOrWhiteSpace(input.AppCode))
            query = query.Where((delivery, task) => task.AppCode == input.AppCode);
        if (input.TaskId is { } taskId)
            query = query.Where((delivery, task) => task.Id == taskId);
        if (input.DeliveryId is { } deliveryId)
            query = query.Where((delivery, task) => delivery.Id == deliveryId);
        if (keyword?.Length > 128) throw new ArgumentException("搜索内容过长");
        if (!string.IsNullOrEmpty(keyword))
            query = query.Where((delivery, task) => delivery.Address.Contains(keyword) ||
                delivery.RecipientUserId.Contains(keyword) || task.Subject.Contains(keyword));
        if (!string.IsNullOrWhiteSpace(input.Status)) query = query.Where((delivery, task) => delivery.Status == input.Status);
        if (!string.IsNullOrWhiteSpace(input.Source)) query = query.Where((delivery, task) => task.Source == input.Source);
        if (input.StartTime.HasValue)
        {
            var start = input.StartTime.Value;
            query = query.Where((delivery, task) => task.CreateTime >= start);
        }
        if (input.EndTime.HasValue)
        {
            var end = input.EndTime.Value;
            query = query.Where((delivery, task) => task.CreateTime <= end);
        }
        RefAsync<int> total = 0;
        var current = Math.Max(1, input.Current);
        var size = Math.Clamp(input.Size, 1, 100);
        var rows = await query.OrderBy((delivery, task) => delivery.Id, OrderByType.Desc)
            .Select((delivery, task) => new EmailRecordDto
            {
                Id = delivery.Id, TaskId = task.Id, CreatedBy = task.CreatedBy, AppCode = task.AppCode,
                Source = task.Source, Subject = task.Subject, RecipientUserId = delivery.RecipientUserId,
                Address = delivery.Address, RecipientKind = delivery.RecipientKind, Status = delivery.Status,
                FromAddress = delivery.FromAddress, SettingVersion = delivery.SettingVersion,
                AttemptCount = delivery.AttemptCount, ErrorMessage = delivery.ErrorMessage,
                CreateTime = task.CreateTime, SentTime = delivery.SentTime
            }).ToPageListAsync(current, size, total);
        return new EmailPage<EmailRecordDto> { Records = rows, Current = current, Size = size, Total = total };
    }

    public async Task RetryAsync(ApplicationAccessContext access, long deliveryId, EmailRetryDto dto)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await RequireAdministratorAsync(access);
        RequireReady();
        if (!dto.Confirm) throw new ArgumentException("请确认重试发送");
        var row = await Db.Queryable<EmailPushDeliveryEntity>().InSingleAsync(deliveryId)
            ?? throw new ArgumentException("投递记录不存在");
        if (await Db.Queryable<EmailPushTaskEntity>().AnyAsync(x => x.Id == row.TaskId && x.Source == "WorkTask"))
            throw new UnauthorizedAccessException("工作任务消息由业务来源管理，不能通过通用发送记录重试");
        if (row.Status is not ("Failed" or "Unknown")) throw new ArgumentException("只能重试失败或结果待核实的投递");
        if (row.Status == "Unknown" && !dto.AcknowledgeDuplicateRisk)
            throw new ArgumentException("结果待核实的邮件可能已经发出，须明确接受重复投递风险");
        var now = DateTime.Now;
        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            var affected = await Db.Updateable<EmailPushDeliveryEntity>()
                .SetColumns(x => new EmailPushDeliveryEntity
                {
                    Status = "Pending", AttemptCount = 0, NextAttemptTime = now,
                    ErrorMessage = null, LeaseExpireTime = null, UpdateTime = now
                }).Where(x => x.Id == deliveryId && x.Status == row.Status).ExecuteCommandAsync();
            if (affected != 1) throw new InvalidOperationException("任务状态已变化，请刷新");
            await Db.Insertable(new EmailPushConfigurationAuditEntity
            {
                UserId = access.UserId, Action = "RetryDelivery",
                Summary = $"DeliveryId={deliveryId}; PreviousStatus={row.Status}; PreviousAttempts={row.AttemptCount}; DuplicateRiskAccepted={dto.AcknowledgeDuplicateRisk}"
            }).ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess) throw transaction.ErrorException ?? new InvalidOperationException("任务重试失败");
    }
}

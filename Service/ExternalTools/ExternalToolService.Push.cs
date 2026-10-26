using Model.EmailPush;
using Model.ExternalTools;
using System.Text.Json;

namespace Service;

public sealed partial class ExternalToolService
{
    private Interface.IWeChatPushQueueService RequireWeChatQueue() =>
        _weChatPushQueue ?? throw new InvalidOperationException("微信发送队列未注册，请联系管理员");

    private async Task<object?> ExecuteEmailToolAsync(string name, JsonElement args, ExternalToolCallerContext caller)
    {
        var email = _emailPushService ?? throw new InvalidOperationException("邮件推送服务未注册，请联系管理员");
        switch (name)
        {
            case "email.recipients.list":
                return await email.GetMcpRecipientsAsync(caller, GetOptionalString(args, "keyword"),
                    GetBoundedInt(args, "current", 1, 1, 1000), GetBoundedInt(args, "size", 20, 1, 100));
            case "email.channel-status":
                return await email.GetMcpAvailabilityAsync(caller);
            case "email.send":
                return await email.EnqueueByMcpAsync(caller, new EmailEnqueueDto
                {
                    RecipientUserIds = GetRequiredStringList(args, "recipientUserIds"),
                    CcUserIds = args.TryGetProperty("ccUserIds", out var cc) && cc.GetArrayLength() > 0
                        ? GetRequiredStringList(args, "ccUserIds") : [],
                    Subject = GetRequiredString(args, "subject"),
                    Body = args.GetProperty("body").GetString()!,
                    IdempotencyKey = GetRequiredString(args, "idempotencyKey"),
                    Confirm = GetOptionalBoolean(args, "confirm", false)
                });
            case "email.delivery-status":
                return await email.GetMcpRecordsAsync(caller, new EmailRecordQuery
                {
                    TaskId = OptionalPositiveLong(args, "taskId"), DeliveryId = OptionalPositiveLong(args, "deliveryId"),
                    Keyword = GetOptionalString(args, "keyword"), Status = GetOptionalString(args, "status"),
                    StartTime = GetOptionalDateTime(args, "startTime"), EndTime = GetOptionalDateTime(args, "endTime"),
                    Current = GetBoundedInt(args, "current", 1, 1, 1000), Size = GetBoundedInt(args, "size", 20, 1, 100)
                });
            case "email.retry":
                await email.RetryByMcpAsync(caller, GetRequiredLong(args, "deliveryId"), GetOptionalBoolean(args, "confirm", false));
                return new { queued = true, message = "邮件已重新入队，请查询投递结果" };
            default:
                throw new InvalidOperationException("未注册的邮件工具");
        }
    }

    private static long? OptionalPositiveLong(JsonElement args, string name) =>
        args.TryGetProperty(name, out _) ? GetRequiredLong(args, name) : null;

    private const string EmailRecipientsSchema = """
        {"type":"object","properties":{"keyword":{"type":"string","maxLength":128},"current":{"type":"integer","minimum":1,"maximum":1000},"size":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
        """;
    private const string EmailSendSchema = """
        {"type":"object","properties":{"recipientUserIds":{"type":"array","minItems":1,"maxItems":100,"items":{"type":"string","minLength":1,"maxLength":64}},"ccUserIds":{"type":"array","maxItems":100,"items":{"type":"string","minLength":1,"maxLength":64}},"subject":{"type":"string","minLength":1,"maxLength":160},"body":{"type":"string","minLength":1,"maxLength":100000},"idempotencyKey":{"type":"string","minLength":1,"maxLength":128},"confirm":{"type":"boolean","const":true}},"required":["recipientUserIds","subject","body","idempotencyKey","confirm"],"additionalProperties":false}
        """;
    private const string EmailDeliverySchema = """
        {"type":"object","properties":{"taskId":{"type":"integer","minimum":1},"deliveryId":{"type":"integer","minimum":1},"keyword":{"type":"string","maxLength":128},"status":{"type":"string","enum":["Pending","Processing","Sent","Failed","Unknown","Retrying","Cancelled"]},"startTime":{"type":"string","format":"date-time"},"endTime":{"type":"string","format":"date-time"},"current":{"type":"integer","minimum":1,"maximum":1000},"size":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
        """;
    private const string EmailRetrySchema = """
        {"type":"object","properties":{"deliveryId":{"type":"integer","minimum":1},"confirm":{"type":"boolean","const":true}},"required":["deliveryId","confirm"],"additionalProperties":false}
        """;
    private const string ClawBotQueueSchema = """
        {"type":"object","properties":{"taskId":{"type":"integer","minimum":1},"recipientCode":{"type":"string","minLength":1,"maxLength":64},"idempotencyKey":{"type":"string","minLength":1,"maxLength":128},"status":{"type":"string","enum":["Pending","Sending","Sent","Failed","Unknown","Cancelled"]},"current":{"type":"integer","minimum":1,"maximum":1000},"size":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
        """;
    private const string ClawBotQueueRetrySchema = """
        {"type":"object","properties":{"taskId":{"type":"integer","minimum":1},"deliveryId":{"type":"integer","minimum":1},"confirm":{"type":"boolean","const":true}},"required":["confirm"],"oneOf":[{"required":["taskId"]},{"required":["deliveryId"]}],"additionalProperties":false}
        """;
}

using Interface;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Model.EmailPush;

namespace Service;

public sealed class MailKitEmailTransport(IEmailSecretProtector secrets) : IEmailTransport
{
    public async Task TestAsync(EmailPushSettingEntity setting, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(setting.TimeoutSeconds));
        using var client = new SmtpClient();
        try
        {
            await ConnectAsync(client, setting, timeout.Token);
            await client.DisconnectAsync(true, timeout.Token);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // SMTP exception text can include addresses or server authentication details; do not expose it.
            throw new InvalidOperationException("SMTP 连接或认证失败，请核对地址、端口、TLS 方式、授权码和服务器网络");
        }
    }

    public async Task<EmailTransportResult> SendAsync(EmailPushSettingEntity setting, EmailPushTaskEntity task,
        EmailPushDeliveryEntity delivery, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(setting.TimeoutSeconds));
        using var client = new SmtpClient();
        var sending = false;
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(setting.FromName, setting.FromAddress));
            if (delivery.RecipientKind == "Cc") message.Cc.Add(MailboxAddress.Parse(delivery.Address));
            else message.To.Add(MailboxAddress.Parse(delivery.Address));
            message.Subject = task.Subject;
            message.Body = new TextPart("plain") { Text = task.Body };
            message.MessageId = $"email-push-{task.Id}-{delivery.Id}@{setting.FromAddress.Split('@').Last()}";
            await ConnectAsync(client, setting, timeout.Token);
            sending = true;
            await client.SendAsync(message, timeout.Token);
            // A disconnect failure after SMTP accepted the message must not turn success into an automatic retry.
            try { await client.DisconnectAsync(true, timeout.Token); }
            catch { }
            return new EmailTransportResult { Status = "Sent" };
        }
        catch (SmtpCommandException exception)
        {
            var code = (int)exception.StatusCode;
            return new EmailTransportResult
            {
                Status = code is >= 400 and < 500 ? "Retryable" : "Failed",
                Error = $"SMTP 服务器拒绝请求，状态码 {code}，请管理员核对"
            };
        }
        catch (AuthenticationException)
        {
            return new EmailTransportResult { Status = "Failed", Error = "邮件账户认证失败，请管理员检查授权码" };
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return new EmailTransportResult { Status = "Failed", Error = "邮件凭据无法解密，请管理员重新保存授权码" };
        }
        catch
        {
            return new EmailTransportResult
            {
                Status = sending ? "Unknown" : "Retryable",
                Error = sending ? "发送阶段连接中断或超时，结果待核实，未自动重发" : "SMTP 连接失败或超时，将按配置有限重试"
            };
        }
    }

    private async Task ConnectAsync(SmtpClient client, EmailPushSettingEntity setting, CancellationToken token)
    {
        var security = setting.Security switch
        {
            "StartTls" => SecureSocketOptions.StartTls,
            "SslOnConnect" => SecureSocketOptions.SslOnConnect,
            _ => throw new InvalidOperationException("不允许不安全的邮件连接方式")
        };
        client.Timeout = setting.TimeoutSeconds * 1000;
        await client.ConnectAsync(setting.Host, setting.Port, security, token);
        await client.AuthenticateAsync(setting.UserName,
            secrets.Unprotect(setting.ProtectedPassword ?? throw new InvalidOperationException("邮件授权码未配置")), token);
    }
}

using System.Net;
using System.Net.Mail;
using Interface;
using Microsoft.Extensions.Options;
using Model.Agent;

namespace Service;

public class EmailService : IEmailService
{
    private readonly EmailOptions _options;

    public EmailService(IOptions<EmailOptions> options)
    {
        _options = options.Value;
    }

    public async Task<AgentEmailSendResultDto> SendAsync(AgentEmailSendRequest request)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("邮件发送未启用，请先配置 EmailOptions:Enabled。");
        if (string.IsNullOrWhiteSpace(_options.Host))
            throw new InvalidOperationException("邮件服务器 Host 未配置。");
        if (string.IsNullOrWhiteSpace(_options.FromAddress))
            throw new InvalidOperationException("发件人 FromAddress 未配置。");

        var toEmails = NormalizeEmails(request.ToEmails);
        var ccEmails = NormalizeEmails(request.CcEmails);
        if (!toEmails.Any()) throw new InvalidOperationException("收件人不能为空。");
        if (string.IsNullOrWhiteSpace(request.Subject)) throw new InvalidOperationException("邮件主题不能为空。");
        if (string.IsNullOrWhiteSpace(request.Body)) throw new InvalidOperationException("邮件正文不能为空。");

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = request.Subject.Trim(),
            Body = request.Body,
            IsBodyHtml = false
        };

        foreach (var email in toEmails) message.To.Add(email);
        foreach (var email in ccEmails) message.CC.Add(email);

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.EnableSsl,
            Timeout = Math.Max(_options.TimeoutSeconds, 1) * 1000
        };

        if (!string.IsNullOrWhiteSpace(_options.UserName))
        {
            client.Credentials = new NetworkCredential(_options.UserName, _options.Password);
        }

        await client.SendMailAsync(message);
        return new AgentEmailSendResultDto
        {
            Success = true,
            Message = "邮件发送成功",
            ToEmails = toEmails,
            CcEmails = ccEmails,
            SendTime = DateTime.Now
        };
    }

    private static List<string> NormalizeEmails(IEnumerable<string> emails)
    {
        return emails
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(x =>
            {
                _ = new MailAddress(x);
                return x;
            })
            .ToList();
    }
}

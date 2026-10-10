using System.Security.Cryptography;
using System.Text;
using Model.WeChatClawBot;
using Newtonsoft.Json;

namespace Service;

/// <summary>任务固定到创建时的微信收件会话，不包含 bot token。</summary>
public static class WeChatClawBotBindingIdentity
{
    public static string Create(WeChatClawBotAccountEntity account) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonConvert.SerializeObject(new[] { account.BotId, account.ILinkUserId }))));
}

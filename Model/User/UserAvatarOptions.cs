namespace Model.User;

public sealed class UserAvatarOptions
{
    /// <summary>可配置绝对持久化目录；未配置时使用后端 data/user-avatars。</summary>
    public string StorageRoot { get; set; } = string.Empty;
}

public sealed record UserAvatarContent(byte[] Bytes, string ContentType);

using Model.User;

namespace Interface;

public interface IUserAvatarService
{
    Task<string?> GetFileKeyAsync(string userId, CancellationToken cancellationToken);
    Task<UserAvatarContent?> GetAsync(string userId, CancellationToken cancellationToken);
    Task<string> SaveAsync(string userId, byte[] bytes, CancellationToken cancellationToken);
    Task ResetAsync(string userId, CancellationToken cancellationToken);
}

using System.Collections.Concurrent;

namespace GatewayService.IntegrationTests;

/// <summary>
/// In-memory stand-in for DaprUserStore so these tests exercise the real HTTP pipeline
/// (JWT auth, role-based authorization, rate limiting, model binding) without needing a
/// live Dapr sidecar or Redis — that persistence layer is already covered by
/// DaprUserStoreTests (unit tier). Semantics mirror DaprUserStore closely enough for
/// these tests (case-insensitive email key, no ETag races to simulate).
/// </summary>
public class FakeUserStore : IUserStore
{
    private readonly ConcurrentDictionary<string, UserAccount> _accounts = new(StringComparer.OrdinalIgnoreCase);

    public Task<UserAccount?> FindByEmailAsync(string email)
    {
        _accounts.TryGetValue(Normalize(email), out var account);
        return Task.FromResult(account);
    }

    public Task<bool> TryRegisterAsync(UserAccount account)
        => Task.FromResult(_accounts.TryAdd(Normalize(account.Email), account));

    public Task<SetRoleResult> TrySetRoleAsync(string email, string role)
    {
        if (!_accounts.TryGetValue(Normalize(email), out var account))
            return Task.FromResult(SetRoleResult.NotFound);

        account.Role = role;
        return Task.FromResult(SetRoleResult.Updated);
    }

    public Task<ChangePasswordResult> TryChangePasswordAsync(string email, string currentPassword, string newPasswordHash)
    {
        if (!_accounts.TryGetValue(Normalize(email), out var account))
            return Task.FromResult(ChangePasswordResult.NotFound);

        if (!PasswordHasher.Verify(currentPassword, account.PasswordHash))
            return Task.FromResult(ChangePasswordResult.InvalidCurrentPassword);

        account.PasswordHash = newPasswordHash;
        return Task.FromResult(ChangePasswordResult.Updated);
    }

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();
}

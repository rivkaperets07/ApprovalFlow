using Dapr.Client;

/// <summary>A registered account: email (the login key), a PBKDF2 hash (never the plain
/// password), the role that governs what they can call (see AuthPolicies), and a display
/// name for JWT claims / the UI. Gateway-private — the other services never need to know
/// a human logged in, only that a request carries a role.</summary>
public class UserAccount
{
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>Outcome of an admin role change: distinct from a bool so the endpoint can
/// tell "no such account" (404) apart from "another write raced this one" (409) instead
/// of collapsing both into a single failure.</summary>
public enum SetRoleResult
{
    Updated,
    NotFound,
    Conflict
}

/// <summary>Outcome of a self-service password change. NotFound and
/// InvalidCurrentPassword are kept distinct here so the store's tests can assert each
/// path, but AuthEndpoints collapses them into the same 401 it uses for /login — the
/// same anti-enumeration reasoning applies (see ChangePasswordAsync).</summary>
public enum ChangePasswordResult
{
    Updated,
    NotFound,
    InvalidCurrentPassword,
    Conflict
}

public interface IUserStore
{
    Task<UserAccount?> FindByEmailAsync(string email);

    /// <summary>Creates the account if the email isn't taken. Returns false if it already
    /// exists — including the case where two registrations for the same email race each
    /// other and this one loses (see the ETag-conditional write below).</summary>
    Task<bool> TryRegisterAsync(UserAccount account);

    /// <summary>Admin-only role assignment (see AuthPolicies.Admin) — the only way an
    /// account becomes approver or admin, since self-service /register always creates a
    /// submitter.</summary>
    Task<SetRoleResult> TrySetRoleAsync(string email, string role);

    /// <summary>Self-service password change: proving the current password is the same
    /// proof of identity /login uses, so no admin and no separate token are involved.
    /// newPasswordHash is pre-hashed by the caller (see PasswordHasher) so this method
    /// stays pure state-store plumbing, same division of labor as TryRegisterAsync.</summary>
    Task<ChangePasswordResult> TryChangePasswordAsync(string email, string currentPassword, string newPasswordHash);
}

/// <summary>
/// Users live in the same Dapr state store as everything else (invoices, budgets, indexes)
/// — no separate database to stand up, and it's already the durable store this service
/// trusts. Keyed by normalized email so a lookup is a single point read, no index needed.
/// </summary>
public class DaprUserStore : IUserStore
{
    private readonly DaprClient _daprClient;

    public DaprUserStore(DaprClient daprClient)
    {
        _daprClient = daprClient;
    }

    public static string BuildKey(string email) => $"user-{email.Trim().ToLowerInvariant()}";

    public async Task<UserAccount?> FindByEmailAsync(string email)
        => await _daprClient.GetStateAsync<UserAccount?>(DaprComponents.StateStore, BuildKey(email));

    public async Task<bool> TryRegisterAsync(UserAccount account)
    {
        var key = BuildKey(account.Email);

        // The TrySaveStateAsync result matters (same lesson as DuplicateInvoiceGuard):
        // two concurrent registrations for the same email must not both succeed. The
        // ETag from the read makes the write conditional on nothing having appeared
        // between the check and the save.
        var (existing, etag) = await _daprClient.GetStateAndETagAsync<UserAccount?>(DaprComponents.StateStore, key);
        if (existing is not null)
            return false;

        return await _daprClient.TrySaveStateAsync(DaprComponents.StateStore, key, account, etag);
    }

    public async Task<SetRoleResult> TrySetRoleAsync(string email, string role)
    {
        var key = BuildKey(email);
        var (account, etag) = await _daprClient.GetStateAndETagAsync<UserAccount?>(DaprComponents.StateStore, key);
        if (account is null)
            return SetRoleResult.NotFound;

        account.Role = role;
        var saved = await _daprClient.TrySaveStateAsync(DaprComponents.StateStore, key, account, etag);
        return saved ? SetRoleResult.Updated : SetRoleResult.Conflict;
    }

    public async Task<ChangePasswordResult> TryChangePasswordAsync(string email, string currentPassword, string newPasswordHash)
    {
        var key = BuildKey(email);
        var (account, etag) = await _daprClient.GetStateAndETagAsync<UserAccount?>(DaprComponents.StateStore, key);
        if (account is null)
            return ChangePasswordResult.NotFound;

        if (!PasswordHasher.Verify(currentPassword, account.PasswordHash))
            return ChangePasswordResult.InvalidCurrentPassword;

        account.PasswordHash = newPasswordHash;
        var saved = await _daprClient.TrySaveStateAsync(DaprComponents.StateStore, key, account, etag);
        return saved ? ChangePasswordResult.Updated : ChangePasswordResult.Conflict;
    }
}

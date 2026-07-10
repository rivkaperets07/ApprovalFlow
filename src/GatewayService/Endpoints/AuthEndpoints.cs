using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

/// <summary>
/// Real credential-based sign-in: register an account (email + password), then log in with
/// those credentials to get a JWT. Replaces the earlier no-password /token, which let a
/// caller mint a token for any role by just naming it — that demonstrated the
/// authorization wiring but wasn't a login. Register/login/change-password are all
/// anonymous by definition — each one's own body proves identity (a fresh signup, a
/// password match, or a password match again), so none of them needs a bearer token.
/// User creation and role assignment are the two exceptions — both require an existing
/// admin's token, since both are how an account gets access it didn't grant itself.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/register", RegisterAsync);
        app.MapPost("/login", LoginAsync);
        app.MapPost("/change-password", ChangePasswordAsync);
        app.MapPost("/users", CreateUserAsync).RequireAuthorization(AuthPolicies.Admin);
        app.MapPut("/users/{email}/role", SetRoleAsync).RequireAuthorization(AuthPolicies.Admin);
    }

    // Every self-registered account starts as submitter, full stop — there's no Role in
    // this request at all, so there's nothing here for a caller to escalate. Getting an
    // approver or admin account requires an existing admin to grant it via SetRoleAsync
    // below; a fresh deployment's first admin comes from DemoUserSeeder.
    private static async Task<IResult> RegisterAsync([FromBody] RegisterRequest? request, IUserStore userStore, JwtSigningKey signingKey, ILogger<Program> logger)
    {
        var email = request?.Email?.Trim() ?? string.Empty;
        var password = request?.Password ?? string.Empty;

        if (!email.Contains('@'))
        {
            return Results.BadRequest(new { error = "A valid email is required." });
        }
        if (password.Length < 8)
        {
            return Results.BadRequest(new { error = "Password must be at least 8 characters." });
        }

        var account = new UserAccount
        {
            Email = email,
            PasswordHash = PasswordHasher.Hash(password),
            Role = "submitter",
            Name = string.IsNullOrWhiteSpace(request!.Name) ? email : request.Name!.Trim()
        };

        if (!await userStore.TryRegisterAsync(account))
        {
            return Results.Conflict(new { error = "An account with this email already exists." });
        }

        logger.LogInformation("Registered new submitter account for {Email}.", email);

        var token = JwtTokenIssuer.IssueToken(account.Role, account.Name, signingKey.Value);
        return Results.Ok(new { token, role = account.Role, name = account.Name });
    }

    private static async Task<IResult> LoginAsync([FromBody] LoginRequest? request, IUserStore userStore, JwtSigningKey signingKey)
    {
        var email = request?.Email?.Trim() ?? string.Empty;
        var password = request?.Password ?? string.Empty;

        var account = await userStore.FindByEmailAsync(email);

        // Same "invalid email or password" message either way — confirming which half was
        // wrong hands an attacker a free account-enumeration oracle.
        if (account is null || !PasswordHasher.Verify(password, account.PasswordHash))
        {
            return Results.Json(new { error = "Invalid email or password." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        // The role in the issued token always comes from the stored account, never from
        // the request — that's what makes this a real login instead of the old free
        // role-pick.
        var token = JwtTokenIssuer.IssueToken(account.Role, account.Name, signingKey.Value);
        return Results.Ok(new { token, role = account.Role, name = account.Name });
    }

    // Anonymous like /login, for the same reason: knowing the current password already
    // is the proof of identity, so a bearer token would be redundant, not additional
    // security. NotFound and InvalidCurrentPassword collapse into the same 401 message
    // as /login for the same anti-enumeration reason (see LoginAsync above).
    private static async Task<IResult> ChangePasswordAsync([FromBody] ChangePasswordRequest? request, IUserStore userStore, ILogger<Program> logger)
    {
        var email = request?.Email?.Trim() ?? string.Empty;
        var currentPassword = request?.CurrentPassword ?? string.Empty;
        var newPassword = request?.NewPassword ?? string.Empty;

        if (newPassword.Length < 8)
        {
            return Results.BadRequest(new { error = "New password must be at least 8 characters." });
        }

        var result = await userStore.TryChangePasswordAsync(email, currentPassword, PasswordHasher.Hash(newPassword));
        switch (result)
        {
            case ChangePasswordResult.NotFound:
            case ChangePasswordResult.InvalidCurrentPassword:
                return Results.Json(new { error = "Invalid email or password." }, statusCode: StatusCodes.Status401Unauthorized);
            case ChangePasswordResult.Conflict:
                return Results.Conflict(new { error = "Account was modified by another request — retry." });
        }

        logger.LogInformation("Password changed for {Email}.", email);
        return Results.Ok(new { email, message = "Password updated." });
    }

    // Admin-only account creation: onboard someone directly into approver/admin (a new
    // hire who needs approver access from day one, say) without a self-register-then-
    // promote round trip. Role is caller-supplied here — unlike RegisterAsync, that's safe
    // because the caller creating the account is already an admin, not the account itself
    // escalating its own access. No token is issued: the admin is provisioning an account
    // for someone else, not signing themselves in as them.
    private static async Task<IResult> CreateUserAsync([FromBody] CreateUserRequest? request, IUserStore userStore, ILogger<Program> logger)
    {
        var email = request?.Email?.Trim() ?? string.Empty;
        var password = request?.Password ?? string.Empty;
        var role = string.IsNullOrWhiteSpace(request?.Role) ? "submitter" : request.Role!.Trim().ToLowerInvariant();

        if (!email.Contains('@'))
        {
            return Results.BadRequest(new { error = "A valid email is required." });
        }
        if (password.Length < 8)
        {
            return Results.BadRequest(new { error = "Password must be at least 8 characters." });
        }
        if (!JwtTokenIssuer.ValidRoles.Contains(role))
        {
            return Results.BadRequest(new { error = $"Role must be one of: {string.Join(", ", JwtTokenIssuer.ValidRoles)}." });
        }

        var account = new UserAccount
        {
            Email = email,
            PasswordHash = PasswordHasher.Hash(password),
            Role = role,
            Name = string.IsNullOrWhiteSpace(request!.Name) ? email : request.Name!.Trim()
        };

        if (!await userStore.TryRegisterAsync(account))
        {
            return Results.Conflict(new { error = "An account with this email already exists." });
        }

        logger.LogInformation("Admin created account for {Email} with role {Role}.", email, role);
        return Results.Ok(new { email, role = account.Role, name = account.Name });
    }

    // The only self-service path to approver/admin: an existing admin promotes (or demotes)
    // an already-registered account by email. Deliberately a separate admin-gated call
    // rather than a field on /register, so granting an elevated role via a self-registered
    // account always leaves an audit trail (logged below, attributable to whichever admin's
    // token called this) instead of being whatever a new account's own signup form happened
    // to submit. CreateUserAsync above covers the other path — an admin provisioning a
    // brand-new account with its role already set.
    private static async Task<IResult> SetRoleAsync([FromRoute] string email, [FromBody] SetRoleRequest? request, IUserStore userStore, ILogger<Program> logger)
    {
        var role = request?.Role?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!JwtTokenIssuer.ValidRoles.Contains(role))
        {
            return Results.BadRequest(new { error = $"Role must be one of: {string.Join(", ", JwtTokenIssuer.ValidRoles)}." });
        }

        var result = await userStore.TrySetRoleAsync(email, role);
        switch (result)
        {
            case SetRoleResult.NotFound:
                return Results.NotFound(new { email, message = "No account with this email." });
            case SetRoleResult.Conflict:
                return Results.Conflict(new { email, error = "Account was modified by another request — retry." });
        }

        logger.LogInformation("Set role of {Email} to {Role}.", email, role);
        return Results.Ok(new { email, role });
    }
}

/// <summary>Body for <c>POST /register</c>. No Role field on purpose — every self-service
/// registration is a submitter (see RegisterAsync).</summary>
public class RegisterRequest
{
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? Name { get; set; }
}

/// <summary>Body for <c>POST /login</c>.</summary>
public class LoginRequest
{
    public string? Email { get; set; }
    public string? Password { get; set; }
}

/// <summary>Body for <c>POST /change-password</c>. Anonymous like /login — supplying
/// CurrentPassword is what proves this is really the account owner.</summary>
public class ChangePasswordRequest
{
    public string? Email { get; set; }
    public string? CurrentPassword { get; set; }
    public string? NewPassword { get; set; }
}

/// <summary>Body for <c>PUT /users/{email}/role</c> — admin-only.</summary>
public class SetRoleRequest
{
    public string? Role { get; set; }
}

/// <summary>Body for <c>POST /users</c> — admin-only. Role defaults to submitter (same as
/// self-registration) if omitted, but unlike <c>RegisterRequest</c> the admin caller may
/// set it directly to approver or admin.</summary>
public class CreateUserRequest
{
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? Name { get; set; }
    public string? Role { get; set; }
}

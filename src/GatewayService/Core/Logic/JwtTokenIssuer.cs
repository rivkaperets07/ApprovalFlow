using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

/// <summary>

/// Self-signed JWT issuance — no external identity provider required. One symmetric
/// key, three roles — submitter / approver / admin
/// — with admin implying both of the others via AuthPolicies below. Called only after
/// AuthEndpoints has already verified the caller's identity (register/login); this class
/// just signs the token for whatever role the verified account holds.
/// NOTE: Currently configured with a long expiration (8 hours) and self-signed tokens
/// to prioritize development velocity and simplify local verification/integration testing.
/// TO-DO (Production Readiness): 
/// 1. Shorten expiration to 15-60 minutes.
/// 2. Implement Refresh Token rotation logic (HttpOnly Cookie-based).
/// 3. Integrate with an Identity Provider (IdP) instead of local signing.
/// Avoids over-engineering at this stage to focus on core saga/idempotency logic.
/// </summary>
public static class JwtTokenIssuer
{
    public const string Issuer = "approvalflow-gateway";
    public const string Audience = "approvalflow";

    public static readonly string[] ValidRoles = ["submitter", "approver", "admin"];

    public static string IssueToken(string role, string name, string signingKey)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, name),
            new Claim(ClaimTypes.Role, role)
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(8),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

/// <summary>Role → endpoint mapping in one place, so the posture is auditable at a
/// glance: submitters drive their own submissions, approvers work the escalation queue
/// and the dashboards, admin can do everything.</summary>
public static class AuthPolicies
{
    public const string Submitter = "submitter-or-admin";
    public const string Approver = "approver-or-admin";

    /// <summary>Strictly admin — unlike the two above, there's no "-or-admin" combining
    /// here because this already <em>is</em> the admin-only surface (granting elevated
    /// roles). Used only by <c>PUT /users/{'{'}email{'}'}/role</c>.</summary>
    public const string Admin = "admin-only";
}

/// <summary>The JWT signing key, resolved once in Program.cs (config value, falling back
/// to the checked-in local-demo default) and registered as a singleton so AuthEndpoints
/// can issue tokens without recomputing — or fighting IConfiguration — for a value that
/// never changes after startup.</summary>
public record JwtSigningKey(string Value);

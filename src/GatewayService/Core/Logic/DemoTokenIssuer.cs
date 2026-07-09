using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// N1: self-signed JWT issuance for the demo (the assignment explicitly allows a
/// self-signed JWT). One symmetric key, three roles — submitter / approver / admin —
/// with admin implying both of the others via AuthPolicies below. There is no user
/// database or password check on purpose: this demonstrates AuthN/AuthZ wiring
/// (who may call which endpoint), not identity management, which would need a real
/// IdP to be anything more than theater.
/// </summary>
public static class DemoTokenIssuer
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
}

/// <summary>Body for <c>POST /token</c>: pick a role (and optionally a display name) and
/// get a signed demo JWT back.</summary>
public class TokenRequest
{
    public string? Role { get; set; }
    public string? Name { get; set; }
}

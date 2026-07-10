using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace GatewayService.IntegrationTests;

/// <summary>
/// Proves the real ASP.NET Core authentication/authorization pipeline — JwtBearer
/// validation plus the AuthPolicies role policies registered in GatewayService.cs —
/// actually enforces the role model end-to-end, not just that PolicyEngine-adjacent unit
/// tests believe it does. AuthorizationMiddleware rejects an unauthorized/wrong-role
/// request before the endpoint handler ever runs, so these tests are safe to point at
/// endpoints that would otherwise need a live Dapr sidecar (submit, escalations) — a
/// 401/403 response never reaches that code.
/// </summary>
public class AuthorizationTests : IClassFixture<GatewayApiFactory>
{
    private readonly GatewayApiFactory _factory;

    public AuthorizationTests(GatewayApiFactory factory)
    {
        _factory = factory;
    }

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@example.com";

    private async Task<string> RegisterAndGetTokenAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/register", new { Email = UniqueEmail(), Password = "correct-horse-battery" });
        var body = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        return body.GetProperty("token").GetString()!;
    }

    private async Task<string> SeedAdminAndGetTokenAsync(HttpClient client)
    {
        var email = UniqueEmail();
        _factory.UserStore.TryRegisterAsync(new UserAccount
        {
            Email = email,
            PasswordHash = PasswordHasher.Hash("admin-password-1"),
            Role = "admin",
            Name = "Test Admin"
        }).GetAwaiter().GetResult();

        var response = await client.PostAsJsonAsync("/login", new { Email = email, Password = "admin-password-1" });
        var body = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        return body.GetProperty("token").GetString()!;
    }

    private static void UseBearer(HttpClient client, string token)
        => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    [Fact]
    public async Task AdminOnlyEndpoint_NoToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/users", new { Email = UniqueEmail(), Password = "correct-horse-battery" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminOnlyEndpoint_SubmitterToken_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        var submitterToken = await RegisterAndGetTokenAsync(client);
        UseBearer(client, submitterToken);

        var response = await client.PostAsJsonAsync("/users", new { Email = UniqueEmail(), Password = "correct-horse-battery" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminOnlyEndpoint_AdminToken_Succeeds()
    {
        var client = _factory.CreateClient();
        var adminToken = await SeedAdminAndGetTokenAsync(client);
        UseBearer(client, adminToken);

        var response = await client.PostAsJsonAsync("/users", new { Email = UniqueEmail(), Password = "correct-horse-battery", Role = "approver" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ApproverOnlyEndpoint_SubmitterToken_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        var submitterToken = await RegisterAndGetTokenAsync(client);
        UseBearer(client, submitterToken);

        var response = await client.GetAsync("/escalations");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SubmitterOnlyEndpoint_UnauthenticatedRequest_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/submit", new { Vendor = "Acme", TotalAmount = 10 });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminToken_SatisfiesBothSubmitterAndApproverPolicies()
    {
        // "admin implies both" (AuthPolicies) means an admin token must clear the
        // authorization check on both a submitter-only and an approver-only endpoint.
        // Asserting != 401/403 rather than == 200 on purpose: these routes call into
        // Dapr-backed state this factory doesn't fake, so a downstream 5xx is expected
        // and irrelevant here — only whether the *authorization* layer let it through.
        var client = _factory.CreateClient();
        var adminToken = await SeedAdminAndGetTokenAsync(client);
        UseBearer(client, adminToken);

        var escalationsResponse = await client.GetAsync("/escalations");
        var submitResponse = await client.PostAsJsonAsync("/submit", new { Vendor = "Acme", TotalAmount = 10 });

        Assert.NotEqual(HttpStatusCode.Unauthorized, escalationsResponse.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, escalationsResponse.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, submitResponse.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, submitResponse.StatusCode);
    }
}

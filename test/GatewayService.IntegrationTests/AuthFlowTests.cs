using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GatewayService.IntegrationTests;

/// <summary>
/// Register → login → change-password through the real HTTP pipeline (JWT issuance,
/// PBKDF2 hashing, 401/409 responses) — the integration tier between AuthEndpoints'
/// handler logic (exercised directly nowhere, since it's static) and DaprUserStoreTests'
/// unit coverage of the persistence layer these handlers call into.
/// </summary>
public class AuthFlowTests : IClassFixture<GatewayApiFactory>
{
    private readonly GatewayApiFactory _factory;

    public AuthFlowTests(GatewayApiFactory factory)
    {
        _factory = factory;
    }

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@example.com";

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    [Fact]
    public async Task Register_NewAccount_ReturnsTokenForSubmitterRole()
    {
        var client = _factory.CreateClient();
        var email = UniqueEmail();

        var response = await client.PostAsJsonAsync("/register", new { Email = email, Password = "correct-horse-battery", Name = "Test User" });
        var body = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));
        Assert.Equal("submitter", body.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Register_DuplicateEmail_ReturnsConflict()
    {
        var client = _factory.CreateClient();
        var email = UniqueEmail();
        await client.PostAsJsonAsync("/register", new { Email = email, Password = "correct-horse-battery" });

        var response = await client.PostAsJsonAsync("/register", new { Email = email, Password = "another-password" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Register_ShortPassword_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/register", new { Email = UniqueEmail(), Password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_CorrectCredentials_ReturnsToken()
    {
        var client = _factory.CreateClient();
        var email = UniqueEmail();
        await client.PostAsJsonAsync("/register", new { Email = email, Password = "correct-horse-battery" });

        var response = await client.PostAsJsonAsync("/login", new { Email = email, Password = "correct-horse-battery" });
        var body = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();
        var email = UniqueEmail();
        await client.PostAsJsonAsync("/register", new { Email = email, Password = "correct-horse-battery" });

        var response = await client.PostAsJsonAsync("/login", new { Email = email, Password = "wrong-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_UnknownEmail_ReturnsSameUnauthorizedAsWrongPassword()
    {
        // Anti-enumeration: AuthEndpoints deliberately returns the same 401 for "no such
        // account" and "wrong password" so a caller can't use this to probe which emails
        // are registered. Assert the status code, not the body — this test's job is to
        // catch a regression that starts distinguishing the two cases, not to pin exact
        // wording.
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/login", new { Email = UniqueEmail(), Password = "whatever123" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_CorrectCurrentPassword_AllowsLoginWithNewPassword()
    {
        var client = _factory.CreateClient();
        var email = UniqueEmail();
        await client.PostAsJsonAsync("/register", new { Email = email, Password = "correct-horse-battery" });

        var changeResponse = await client.PostAsJsonAsync("/change-password", new { Email = email, CurrentPassword = "correct-horse-battery", NewPassword = "new-correct-horse" });
        Assert.Equal(HttpStatusCode.OK, changeResponse.StatusCode);

        var oldLoginResponse = await client.PostAsJsonAsync("/login", new { Email = email, Password = "correct-horse-battery" });
        var newLoginResponse = await client.PostAsJsonAsync("/login", new { Email = email, Password = "new-correct-horse" });

        Assert.Equal(HttpStatusCode.Unauthorized, oldLoginResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newLoginResponse.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();
        var email = UniqueEmail();
        await client.PostAsJsonAsync("/register", new { Email = email, Password = "correct-horse-battery" });

        var response = await client.PostAsJsonAsync("/change-password", new { Email = email, CurrentPassword = "totally-wrong", NewPassword = "new-correct-horse" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

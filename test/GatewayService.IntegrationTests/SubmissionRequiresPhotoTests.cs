using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace GatewayService.IntegrationTests;

/// <summary>
/// dev-branch extension (docs/adr/008-receipt-photo-submission.md): a receipt photo is
/// now the only way to submit, and typed Vendor/TotalAmount are rejected outright rather
/// than merely ignored. Both checks in SubmissionEndpoints.SubmitAsync run before any Dapr
/// call, so - same reasoning as AuthorizationTests - these are safe to exercise here
/// without a live Dapr sidecar.
/// </summary>
public class SubmissionRequiresPhotoTests : IClassFixture<GatewayApiFactory>
{
    private readonly GatewayApiFactory _factory;

    public SubmissionRequiresPhotoTests(GatewayApiFactory factory)
    {
        _factory = factory;
    }

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@example.com";

    private async Task<HttpClient> SubmitterClientAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/register", new { Email = UniqueEmail(), Password = "correct-horse-battery" });
        var body = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        var token = body.GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task MissingReceiptPhoto_ReturnsBadRequest()
    {
        var client = await SubmitterClientAsync();

        var response = await client.PostAsJsonAsync("/submit", new { TrackingId = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PhotoWithTypedVendor_ReturnsBadRequest()
    {
        var client = await SubmitterClientAsync();

        var response = await client.PostAsJsonAsync("/submit", new
        {
            TrackingId = Guid.NewGuid().ToString(),
            ReceiptImageDataUri = "data:image/png;base64,OCR:CloudSoft Inc|180|",
            Vendor = "CloudSoft Inc"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PhotoWithTypedTotalAmount_ReturnsBadRequest()
    {
        var client = await SubmitterClientAsync();

        var response = await client.PostAsJsonAsync("/submit", new
        {
            TrackingId = Guid.NewGuid().ToString(),
            ReceiptImageDataUri = "data:image/png;base64,OCR:CloudSoft Inc|180|",
            TotalAmount = 180
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PhotoAloneWithNoTypedVendorOrAmount_PassesValidation()
    {
        // This factory doesn't fake DaprClient, so a genuinely accepted submission would
        // still fail past validation trying to reach a real sidecar - the point here is
        // only that it gets *past* the 400 checks (a 5xx from the unreachable Dapr sidecar
        // is expected and irrelevant), proving the photo-alone shape is accepted.
        var client = await SubmitterClientAsync();

        var response = await client.PostAsJsonAsync("/submit", new
        {
            TrackingId = Guid.NewGuid().ToString(),
            ReceiptImageDataUri = "data:image/png;base64,OCR:CloudSoft Inc|180|"
        });

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

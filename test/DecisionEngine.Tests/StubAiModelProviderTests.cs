using DecisionEngine.Ai;
using Microsoft.Extensions.Configuration;

public class StubAiModelProviderTests
{
    private static IConfiguration BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["VendorDirectory:CloudSoft Inc"] = "SaaS",
            ["VendorDirectory:Delta Airlines"] = "Travel",
        })
        .Build();

    private static InvoicePayload Invoice(string vendor, string? notes = null, string category = "") => new()
    {
        TrackingId = Guid.NewGuid().ToString(),
        Vendor = vendor,
        TotalAmount = 100m,
        Category = category,
        Notes = notes
    };

    [Fact]
    public async Task KnownVendor_IsClassifiedWithHighConfidence_RegardlessOfNotes()
    {
        var provider = new StubAiModelProvider(BuildConfig());

        var result = await provider.AnalyzeAsync(Invoice("CloudSoft Inc", notes: "totally unrelated text"), default);

        Assert.Equal("SaaS", result.SuggestedCategory);
        Assert.True(result.ConfidenceScore >= 0.95);
    }

    [Fact]
    public async Task UnknownVendor_FallsBackToKeywordMatch_WithMediumConfidence()
    {
        var provider = new StubAiModelProvider(BuildConfig());

        var result = await provider.AnalyzeAsync(Invoice("Some Random Co", notes: "monthly cloud subscription"), default);

        Assert.Equal("SaaS", result.SuggestedCategory);
        Assert.InRange(result.ConfidenceScore, 0.80, 0.95);
    }

    [Fact]
    public async Task NoVendorMatch_NoKeywordMatch_IsOther_WithLowConfidence()
    {
        var provider = new StubAiModelProvider(BuildConfig());

        var result = await provider.AnalyzeAsync(Invoice("Totally Unknown Vendor", notes: "gibberish xyz"), default);

        Assert.Equal("Other", result.SuggestedCategory);
        // Deliberately below the 0.80 default MinConfidence so an unclassifiable
        // invoice escalates instead of being silently auto-approved as "Other".
        Assert.True(result.ConfidenceScore < 0.80);
    }

    [Fact]
    public async Task KnownVendor_Travel_StillExtractsTripId()
    {
        var provider = new StubAiModelProvider(BuildConfig());

        var result = await provider.AnalyzeAsync(Invoice("Delta Airlines", notes: "TripId: TRIP-42"), default);

        Assert.Equal("Travel", result.SuggestedCategory);
        Assert.Equal("TRIP-42", result.LinkedTripId);
    }
}

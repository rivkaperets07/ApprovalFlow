using DecisionEngine.Ai;

public class StubAiModelProviderTests
{
    private static InvoicePayload Invoice(string vendor, string? notes = null, List<LineItem>? lineItems = null) => new()
    {
        TrackingId = Guid.NewGuid().ToString(),
        Vendor = vendor,
        TotalAmount = 100m,
        Notes = notes,
        LineItems = lineItems
    };

    [Fact]
    public async Task NotesMatchTheGivenCategory_IsHighConfidence()
    {
        var provider = new StubAiModelProvider();

        var result = await provider.AnalyzeAsync(Invoice("CloudSoft Inc", notes: "monthly cloud subscription"), "SaaS", default);

        Assert.True(result.ConfidenceScore >= 0.95);
    }

    [Fact]
    public async Task LineItemDescriptions_AreAlsoCheckedForCoherence()
    {
        // The signal doesn't have to come from Notes — a line item description that matches
        // the given category is just as good (this is the new LineItems input this provider
        // didn't have before).
        var provider = new StubAiModelProvider();
        var invoice = Invoice("CloudSoft Inc", notes: null, lineItems: [new LineItem("Annual software license", 100m)]);

        var result = await provider.AnalyzeAsync(invoice, "SaaS", default);

        Assert.True(result.ConfidenceScore >= 0.95);
    }

    [Fact]
    public async Task NoKeywordSignal_IsNeutralConfidence()
    {
        var provider = new StubAiModelProvider();

        var result = await provider.AnalyzeAsync(Invoice("CloudSoft Inc", notes: "as discussed last week"), "SaaS", default);

        Assert.InRange(result.ConfidenceScore, 0.80, 0.95);
    }

    [Fact]
    public async Task NotesContradictTheGivenCategory_IsLowConfidence()
    {
        // The category is already trusted (from the vendor directory) — this provider's
        // job is to flag when the content doesn't actually match it, not to reclassify.
        var provider = new StubAiModelProvider();

        var result = await provider.AnalyzeAsync(Invoice("CloudSoft Inc", notes: "flight and hotel for the conference"), "SaaS", default);

        Assert.True(result.ConfidenceScore < 0.80);
    }

    [Fact]
    public async Task Travel_StillExtractsTripId()
    {
        var provider = new StubAiModelProvider();

        var result = await provider.AnalyzeAsync(Invoice("Delta Airlines", notes: "TripId: TRIP-42"), "Travel", default);

        Assert.Equal("TRIP-42", result.LinkedTripId);
    }

    [Fact]
    public async Task Travel_NoExplicitTripId_FallsBackToTrackingIdDerivedOne()
    {
        var provider = new StubAiModelProvider();
        var invoice = Invoice("Delta Airlines", notes: "flight to conference");

        var result = await provider.AnalyzeAsync(invoice, "Travel", default);

        Assert.Equal($"TRIP-{invoice.TrackingId}", result.LinkedTripId);
    }
}

using Dapr.Client;
using Moq;
using Xunit;

public class RecentSubmissionGuardTests
{
    [Fact]
    public async Task FirstSubmission_IsNotADuplicate_AndClaimsTheContentKey()
    {
        var daprMock = new Mock<DaprClient>();
        daprMock.Setup(c => c.GetStateAndETagAsync<string?>("statestore", It.IsAny<string>(), null, null, default))
            .ReturnsAsync(((string?)null, ""));
        daprMock.Setup(c => c.TrySaveStateAsync("statestore", It.IsAny<string>(), "TRK-1", "", null, It.IsAny<IReadOnlyDictionary<string, string>>(), default))
            .ReturnsAsync(true);

        var result = await RecentSubmissionGuard.TryClaimAsync(daprMock.Object, "statestore", "CloudSoft Inc", 350m, "SaaS", "Monthly renewal", "TRK-1");

        Assert.Null(result);
        daprMock.Verify(
            c => c.TrySaveStateAsync("statestore", RecentSubmissionGuard.BuildKey("CloudSoft Inc", 350m, "SaaS", "Monthly renewal"), "TRK-1", "", null,
                It.Is<IReadOnlyDictionary<string, string>>(m => m["ttlInSeconds"] == "60"), default),
            Times.Once);
    }

    [Fact]
    public async Task LosingTheClaimRace_ReturnsTheWinnersTrackingId()
    {
        // Two identical submissions land at the same instant: both read "unclaimed", only
        // one ETag write wins. The loser must re-read and short-circuit on the winner's
        // TrackingId — not proceed as if its claim had succeeded (the old behavior, which
        // ignored the TrySaveStateAsync result and let both through).
        var daprMock = new Mock<DaprClient>();
        daprMock.SetupSequence(c => c.GetStateAndETagAsync<string?>("statestore", It.IsAny<string>(), null, null, default))
            .ReturnsAsync(((string?)null, ""))
            .ReturnsAsync(("TRK-1", "etag-1"));
        daprMock.Setup(c => c.TrySaveStateAsync("statestore", It.IsAny<string>(), "TRK-2", "", null, It.IsAny<IReadOnlyDictionary<string, string>>(), default))
            .ReturnsAsync(false);

        var result = await RecentSubmissionGuard.TryClaimAsync(daprMock.Object, "statestore", "CloudSoft Inc", 350m, "SaaS", "Monthly renewal", "TRK-2");

        Assert.Equal("TRK-1", result);
    }

    [Fact]
    public async Task SameContentWithinWindow_ReturnsOriginalTrackingId()
    {
        // No InvoiceNumber, no reused TrackingId — the case GLOBAL-DUP and the TrackingId
        // check both miss, which is exactly what this guard exists to catch.
        var daprMock = new Mock<DaprClient>();
        daprMock.Setup(c => c.GetStateAndETagAsync<string?>("statestore", It.IsAny<string>(), null, null, default))
            .ReturnsAsync(("TRK-1", "etag-1"));

        var result = await RecentSubmissionGuard.TryClaimAsync(daprMock.Object, "statestore", "CloudSoft Inc", 350m, "SaaS", "Monthly renewal", "TRK-2");

        Assert.Equal("TRK-1", result);
        daprMock.Verify(
            c => c.TrySaveStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), null, It.IsAny<IReadOnlyDictionary<string, string>>(), default),
            Times.Never);
    }

    [Fact]
    public void BuildKey_DifferentAmount_ProducesDifferentKey()
    {
        var key1 = RecentSubmissionGuard.BuildKey("CloudSoft Inc", 350m, "SaaS", "note");
        var key2 = RecentSubmissionGuard.BuildKey("CloudSoft Inc", 351m, "SaaS", "note");

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void BuildKey_DifferentVendor_ProducesDifferentKey()
    {
        var key1 = RecentSubmissionGuard.BuildKey("CloudSoft Inc", 350m, "SaaS", "note");
        var key2 = RecentSubmissionGuard.BuildKey("Northwind Cloud Services", 350m, "SaaS", "note");

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void BuildKey_IsCaseAndWhitespaceInsensitive()
    {
        var key1 = RecentSubmissionGuard.BuildKey("  CloudSoft Inc  ", 350m, "  SaaS  ", "  Monthly renewal  ");
        var key2 = RecentSubmissionGuard.BuildKey("cloudsoft inc", 350.00m, "saas", "monthly renewal");

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void BuildKey_NullCategoryAndNotes_DoesNotThrow()
    {
        var key = RecentSubmissionGuard.BuildKey("CloudSoft Inc", 350m, null, null);

        Assert.Contains("cloudsoft inc", key);
    }
}

using Dapr.Client;
using Moq;
using Xunit;

public class FraudGuardTests
{
    [Fact]
    public async Task FirstSubmission_IsNotADuplicate_AndGetsRecorded()
    {
        var daprMock = new Mock<DaprClient>();
        daprMock.Setup(c => c.GetStateAndETagAsync<DateTimeOffset?>("statestore", It.IsAny<string>(), null, null, default))
            .ReturnsAsync(((DateTimeOffset?)null, ""));

        var now = DateTimeOffset.UtcNow;
        var result = await FraudGuard.IsLikelyDuplicateAsync(daprMock.Object, "statestore", "ACME Corp", 500m, now);

        Assert.False(result);
        daprMock.Verify(c => c.TrySaveStateAsync("statestore", FraudGuard.BuildKey("ACME Corp", 500m), now, "", null, null, default), Times.Once);
    }

    [Fact]
    public async Task SameVendorAndAmount_WithinWindow_IsBlocked()
    {
        var daprMock = new Mock<DaprClient>();
        var thirtyMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-30);
        daprMock.Setup(c => c.GetStateAndETagAsync<DateTimeOffset?>("statestore", It.IsAny<string>(), null, null, default))
            .ReturnsAsync(((DateTimeOffset?)thirtyMinutesAgo, "etag-1"));

        var result = await FraudGuard.IsLikelyDuplicateAsync(daprMock.Object, "statestore", "ACME Corp", 500m, DateTimeOffset.UtcNow);

        Assert.True(result);
        daprMock.Verify(c => c.TrySaveStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string>(), null, null, default), Times.Never);
    }

    [Fact]
    public async Task SameVendorAndAmount_OutsideWindow_IsAllowed_AndRefreshesTheTimestamp()
    {
        var daprMock = new Mock<DaprClient>();
        var twentyFiveHoursAgo = DateTimeOffset.UtcNow.AddHours(-25);
        daprMock.Setup(c => c.GetStateAndETagAsync<DateTimeOffset?>("statestore", It.IsAny<string>(), null, null, default))
            .ReturnsAsync(((DateTimeOffset?)twentyFiveHoursAgo, "etag-1"));

        var now = DateTimeOffset.UtcNow;
        var result = await FraudGuard.IsLikelyDuplicateAsync(daprMock.Object, "statestore", "ACME Corp", 500m, now);

        Assert.False(result);
        daprMock.Verify(c => c.TrySaveStateAsync("statestore", FraudGuard.BuildKey("ACME Corp", 500m), now, "etag-1", null, null, default), Times.Once);
    }

    [Fact]
    public void BuildKey_IsCaseAndWhitespaceInsensitiveOnVendor()
    {
        var key1 = FraudGuard.BuildKey("  ACME Corp  ", 500m);
        var key2 = FraudGuard.BuildKey("acme corp", 500.00m);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void BuildKey_DifferentAmounts_ProduceDifferentKeys()
    {
        var key1 = FraudGuard.BuildKey("ACME Corp", 500m);
        var key2 = FraudGuard.BuildKey("ACME Corp", 501m);

        Assert.NotEqual(key1, key2);
    }
}

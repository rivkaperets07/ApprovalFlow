using Dapr.Client;
using DecisionEngine.Core.Logic;
using Moq;

public class DecisionAttemptGuardTests
{
    private const string Store = "statestore";
    private const string AttemptId = "attempt-1";

    private static Mock<DaprClient> DaprMock(bool alreadyDecided = false, bool claimInFlight = false, bool winsEtagRace = true)
    {
        var mock = new Mock<DaprClient>();
        mock.Setup(c => c.GetStateAsync<bool>(Store, DecisionAttemptGuard.BuildDecidedKey(AttemptId), null, null, default))
            .ReturnsAsync(alreadyDecided);
        mock.Setup(c => c.GetStateAndETagAsync<bool>(Store, DecisionAttemptGuard.BuildClaimKey(AttemptId), null, null, default))
            .ReturnsAsync((claimInFlight, "etag-0"));
        mock.Setup(c => c.TrySaveStateAsync(Store, DecisionAttemptGuard.BuildClaimKey(AttemptId), true, "etag-0", null, null, default))
            .ReturnsAsync(winsEtagRace);
        return mock;
    }

    [Fact]
    public async Task FreshAttempt_WinsTheClaim()
    {
        var dapr = DaprMock();

        Assert.True(await DecisionAttemptGuard.TryBeginAsync(dapr.Object, Store, AttemptId));

        dapr.Verify(c => c.TrySaveStateAsync(Store, DecisionAttemptGuard.BuildClaimKey(AttemptId), true, "etag-0", null, null, default), Times.Once);
    }

    [Fact]
    public async Task RedeliveryOfDecidedAttempt_IsSkipped()
    {
        var dapr = DaprMock(alreadyDecided: true);

        Assert.False(await DecisionAttemptGuard.TryBeginAsync(dapr.Object, Store, AttemptId));

        // Once the permanent marker is seen, nothing should try to (re)claim the attempt.
        dapr.Verify(c => c.TrySaveStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), null, null, default), Times.Never);
    }

    [Fact]
    public async Task ConcurrentDeliveryHoldingTheClaim_IsSkipped()
    {
        var dapr = DaprMock(claimInFlight: true);

        Assert.False(await DecisionAttemptGuard.TryBeginAsync(dapr.Object, Store, AttemptId));
    }

    [Fact]
    public async Task LosingTheEtagRace_IsSkipped()
    {
        // Both deliveries read "unclaimed", but only one ETag-conditional write can win —
        // the loser must not proceed to evaluate (this is the double-increment window).
        var dapr = DaprMock(winsEtagRace: false);

        Assert.False(await DecisionAttemptGuard.TryBeginAsync(dapr.Object, Store, AttemptId));
    }

    [Fact]
    public async Task MarkDecided_WritesThePermanentMarker()
    {
        var dapr = new Mock<DaprClient>();

        await DecisionAttemptGuard.MarkDecidedAsync(dapr.Object, Store, AttemptId);

        dapr.Verify(c => c.SaveStateAsync(Store, DecisionAttemptGuard.BuildDecidedKey(AttemptId), true, null, null, default), Times.Once);
    }

    [Fact]
    public async Task ReleaseClaim_DeletesOnlyTheClaimKey()
    {
        var dapr = new Mock<DaprClient>();

        await DecisionAttemptGuard.ReleaseClaimAsync(dapr.Object, Store, AttemptId);

        dapr.Verify(c => c.DeleteStateAsync(Store, DecisionAttemptGuard.BuildClaimKey(AttemptId), null, null, default), Times.Once);
    }

    [Fact]
    public void DecidedAndClaimKeys_NeverCollide()
    {
        Assert.NotEqual(DecisionAttemptGuard.BuildDecidedKey(AttemptId), DecisionAttemptGuard.BuildClaimKey(AttemptId));
    }
}

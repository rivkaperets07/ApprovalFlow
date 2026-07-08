using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;
using Moq;
using Xunit;

public class GatewaySubmissionTests
{
    [Fact]
    public async Task HasBeenSubmittedAsync_ReturnsTrue_WhenStateFlagExists()
    {
        var daprMock = new Mock<IDaprStateClient>();
        daprMock.Setup(c => c.GetStateAsync<bool>(GatewayStateKeys.StateStoreName, "invoice-TEST-002-submitted", It.IsAny<ConsistencyMode?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

        var store = new DaprSubmissionStore(daprMock.Object);
        var result = await store.HasBeenSubmittedAsync("TEST-002");

        Assert.True(result);
        daprMock.Verify(c => c.GetStateAsync<bool>(GatewayStateKeys.StateStoreName, "invoice-TEST-002-submitted", It.IsAny<ConsistencyMode?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkSubmittedAsync_CallsSaveStateAsync_WithExpectedKey()
    {
        var daprMock = new Mock<IDaprStateClient>();
        daprMock.Setup(c => c.SaveStateAsync(GatewayStateKeys.StateStoreName, "invoice-TEST-004-submitted", true, It.IsAny<StateOptions?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var store = new DaprSubmissionStore(daprMock.Object);
        await store.MarkSubmittedAsync("TEST-004");

        daprMock.Verify(c => c.SaveStateAsync(GatewayStateKeys.StateStoreName, "invoice-TEST-004-submitted", true, It.IsAny<StateOptions?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetSubmittedKey_FormatsTrackingIdCorrectly()
    {
        var key = GatewayStateKeys.GetSubmittedKey("TEST-005");

        Assert.Equal("invoice-TEST-005-submitted", key);
    }
}

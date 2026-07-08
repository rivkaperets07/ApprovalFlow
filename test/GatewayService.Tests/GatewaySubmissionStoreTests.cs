using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;
using Moq;
using Xunit;

public class GatewaySubmissionStoreTests
{
    [Fact]
    public async Task HasBeenSubmittedAsync_ReturnsFalse_WhenFlagIsNotSet()
    {
        var daprMock = new Mock<IDaprStateClient>();
        daprMock.Setup(c => c.GetStateAsync<bool>(GatewayStateKeys.StateStoreName, "invoice-TEST-001-submitted", It.IsAny<ConsistencyMode?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

        var store = new DaprSubmissionStore(daprMock.Object);
        var result = await store.HasBeenSubmittedAsync("TEST-001");

        Assert.False(result);
        daprMock.Verify(c => c.GetStateAsync<bool>(GatewayStateKeys.StateStoreName, "invoice-TEST-001-submitted", It.IsAny<ConsistencyMode?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkSubmittedAsync_SavesFlagToStateStore()
    {
        var daprMock = new Mock<IDaprStateClient>();
        daprMock.Setup(c => c.SaveStateAsync(GatewayStateKeys.StateStoreName, "invoice-TEST-002-submitted", true, It.IsAny<StateOptions?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var store = new DaprSubmissionStore(daprMock.Object);
        await store.MarkSubmittedAsync("TEST-002");

        daprMock.Verify(c => c.SaveStateAsync(GatewayStateKeys.StateStoreName, "invoice-TEST-002-submitted", true, It.IsAny<StateOptions?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}

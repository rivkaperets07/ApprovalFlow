using Xunit;

public class InvoiceNotifierTests
{
    [Fact]
    public async Task WaitThenPublish_DeliversThePublishedPayload()
    {
        var notifier = new InvoiceNotifier();

        var waitTask = notifier.WaitForDecisionAsync("TRK-1", CancellationToken.None);
        notifier.Publish("TRK-1", new { Status = "Approved" });

        var result = await waitTask;

        Assert.Equal("Approved", result.GetType().GetProperty("Status")!.GetValue(result));
    }

    [Fact]
    public async Task PublishThenWait_StillDeliversThePayload()
    {
        // Race guard: a decision that lands before the client ever opens the
        // notification channel must not be lost.
        var notifier = new InvoiceNotifier();

        notifier.Publish("TRK-2", new { Status = "Escalated" });
        var result = await notifier.WaitForDecisionAsync("TRK-2", CancellationToken.None);

        Assert.Equal("Escalated", result.GetType().GetProperty("Status")!.GetValue(result));
    }

    [Fact]
    public async Task Cancellation_StopsTheWaitWithoutDeliveringAnything()
    {
        var notifier = new InvoiceNotifier();
        using var cts = new CancellationTokenSource();

        var waitTask = notifier.WaitForDecisionAsync("TRK-3", cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask);
    }

    [Fact]
    public async Task DifferentTrackingIds_DoNotInterfereWithEachOther()
    {
        var notifier = new InvoiceNotifier();

        var waitA = notifier.WaitForDecisionAsync("TRK-A", CancellationToken.None);
        var waitB = notifier.WaitForDecisionAsync("TRK-B", CancellationToken.None);
        notifier.Publish("TRK-B", new { Status = "Rejected" });
        notifier.Publish("TRK-A", new { Status = "Approved" });

        var resultA = await waitA;
        var resultB = await waitB;

        Assert.Equal("Approved", resultA.GetType().GetProperty("Status")!.GetValue(resultA));
        Assert.Equal("Rejected", resultB.GetType().GetProperty("Status")!.GetValue(resultB));
    }
}

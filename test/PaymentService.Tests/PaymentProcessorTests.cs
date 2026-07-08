using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class PaymentProcessorTests
{
    [Fact]
    public async Task SuccessfulPayment_IsProcessed()
    {
        var budget = new BudgetService();
        var gateway = new PaymentGateway();
        var processor = new PaymentProcessor(budget, gateway);
        var logger = NullLogger.Instance;

        var invoice = new InvoicePayload { TrackingId = "TST-01", Vendor = "ACME", TotalAmount = 100m, Category = "IT" };

        var result = await processor.ProcessAsync(invoice, logger);

        Assert.True(result.Success);
        Assert.Equal("Payment completed successfully.", result.Message);
    }

    [Fact]
    public async Task DuplicatePayment_IsIgnored()
    {
        var budget = new BudgetService();
        var gateway = new PaymentGateway();
        var processor = new PaymentProcessor(budget, gateway);
        var logger = NullLogger.Instance;

        var invoice = new InvoicePayload { TrackingId = "TST-02", Vendor = "ACME", TotalAmount = 50m, Category = "Ops" };

        var first = await processor.ProcessAsync(invoice, logger);
        var second = await processor.ProcessAsync(invoice, logger);

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Equal("Duplicate request ignored.", second.Message);
    }

    [Fact]
    public async Task FailedPayment_ReleasesBudget()
    {
        var budget = new BudgetService();
        var failing = new FailingGateway();
        var processor = new PaymentProcessor(budget, failing);
        var logger = NullLogger.Instance;

        var invoice = new InvoicePayload { TrackingId = "TST-03", Vendor = "BadVendor", TotalAmount = 75m, Category = "Ops" };

        var result = await processor.ProcessAsync(invoice, logger);

        Assert.False(result.Success);
        Assert.Equal("Payment failed. Reserved budget released.", result.Message);
    }

    [Fact]
    public async Task RetryAfterFailure_IsAllowedToSucceed()
    {
        // A failed payment must not be permanently locked out: only a *completed*
        // payment should dedupe. The claim taken during processing must be released
        // even when the gateway fails, so a legitimate retry can go through.
        var budget = new BudgetService();
        var gateway = new FlakyGateway(failuresBeforeSuccess: 1);
        var processor = new PaymentProcessor(budget, gateway);
        var logger = NullLogger.Instance;

        var invoice = new InvoicePayload { TrackingId = "TST-04", Vendor = "ACME", TotalAmount = 60m, Category = "Ops" };

        var first = await processor.ProcessAsync(invoice, logger);
        var retry = await processor.ProcessAsync(invoice, logger);

        Assert.False(first.Success);
        Assert.Equal("Payment failed. Reserved budget released.", first.Message);
        Assert.True(retry.Success);
        Assert.Equal("Payment completed successfully.", retry.Message);
    }

    [Fact]
    public async Task ConcurrentDuplicateDeliveries_OnlyOneSucceeds()
    {
        // Regression test for the idempotency race: Dapr pub/sub is at-least-once, so
        // two deliveries of the same "invoice.approved" event can arrive concurrently.
        // Before the fix, both could pass a check-then-act "processed" lookup and both
        // pay. The atomic claim must let exactly one of them through.
        var budget = new BudgetService();
        var gateway = new DelayedGateway();
        var processor = new PaymentProcessor(budget, gateway);
        var logger = NullLogger.Instance;

        var invoice1 = new InvoicePayload { TrackingId = "TST-05", Vendor = "ACME", TotalAmount = 40m, Category = "Ops" };
        var invoice2 = new InvoicePayload { TrackingId = "TST-05", Vendor = "ACME", TotalAmount = 40m, Category = "Ops" };

        var task1 = processor.ProcessAsync(invoice1, logger);
        var task2 = processor.ProcessAsync(invoice2, logger);
        var results = await Task.WhenAll(task1, task2);

        Assert.Single(results, r => r.Success);
        Assert.Single(results, r => !r.Success && r.Message == "Duplicate request ignored.");
    }
}

class FailingGateway : IPaymentGateway
{
    public Task<bool> ExecuteBankTransferAsync(string vendor, decimal amount) => Task.FromResult(false);
}

class FlakyGateway : IPaymentGateway
{
    private readonly int _failuresBeforeSuccess;
    private int _attempts;

    public FlakyGateway(int failuresBeforeSuccess) => _failuresBeforeSuccess = failuresBeforeSuccess;

    public Task<bool> ExecuteBankTransferAsync(string vendor, decimal amount)
    {
        _attempts++;
        return Task.FromResult(_attempts > _failuresBeforeSuccess);
    }
}

class DelayedGateway : IPaymentGateway
{
    public async Task<bool> ExecuteBankTransferAsync(string vendor, decimal amount)
    {
        await Task.Delay(100);
        return true;
    }
}

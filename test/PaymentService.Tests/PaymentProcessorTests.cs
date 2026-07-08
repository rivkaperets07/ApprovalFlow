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
}

class FailingGateway : IPaymentGateway
{
    public Task<bool> ExecuteBankTransferAsync(string vendor, decimal amount) => Task.FromResult(false);
}

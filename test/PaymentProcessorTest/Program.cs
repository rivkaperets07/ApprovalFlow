using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

class Program
{
    public static async Task Main()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var fakeLogger = loggerFactory.CreateLogger<Program>();

        var budget = new BudgetService();
        var gateway = new PaymentGateway();
        var processor = new PaymentProcessor(budget, gateway);

        var invoice = new InvoicePayload { TrackingId = "INV-TEST-1", Vendor = "ACME", TotalAmount = 500, Category = "IT" };

        Console.WriteLine("Running first processing (should succeed)...");
        var result1 = await processor.ProcessAsync(invoice, fakeLogger);
        Console.WriteLine($"Result1: {result1.Success} - {result1.Message}");

        Console.WriteLine("Running duplicate processing (should be ignored)...");
        var result2 = await processor.ProcessAsync(invoice, fakeLogger);
        Console.WriteLine($"Result2: {result2.Success} - {result2.Message}");

        // Now simulate a payment failure by using a custom gateway
        var failingGateway = new FailingPaymentGateway();
        var processor2 = new PaymentProcessor(budget, failingGateway);
        var invoice2 = new InvoicePayload { TrackingId = "INV-TEST-2", Vendor = "BadVendor", TotalAmount = 200, Category = "Ops" };

        Console.WriteLine("Running processing with failing gateway (should compensate)...");
        var result3 = await processor2.ProcessAsync(invoice2, fakeLogger);
        Console.WriteLine($"Result3: {result3.Success} - {result3.Message}");
    }
}

public class FailingPaymentGateway : IPaymentGateway
{
    public Task<bool> ExecuteBankTransferAsync(string vendor, decimal amount) => Task.FromResult(false);
}

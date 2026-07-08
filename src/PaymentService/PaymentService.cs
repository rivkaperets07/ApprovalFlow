using Dapr;
using Dapr.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);

var daprHttpEndpoint = builder.Configuration.GetValue<string>("DAPR_HTTP_ENDPOINT", "http://localhost:3500");
builder.Services.AddDaprClient(client => client.UseHttpEndpoint(daprHttpEndpoint));
builder.Services.AddSingleton<IBudgetService, BudgetService>();
builder.Services.AddSingleton<IPaymentGateway, PaymentGateway>();
builder.Services.AddSingleton<IPaymentProcessor, PaymentProcessor>();

var app = builder.Build();

app.UseCloudEvents();
app.MapSubscribeHandler();

app.MapPost("/process-payment", [Topic(PaymentConstants.PUBSUB_NAME, PaymentConstants.APPROVED_TOPIC)] async (
    [FromBody] InvoicePayload invoice,
    IPaymentProcessor paymentProcessor,
    DaprClient daprClient,
    ILogger<Program> logger) =>
{
    if (invoice is null)
    {
        logger.LogWarning("Received null invoice payload.");
        return Results.BadRequest();
    }

    invoice.TrackingId ??= Guid.NewGuid().ToString();
    logger.LogInformation("{CorrelationId} Payment request received for invoice {TrackingId} amount {Amount:C}.", invoice.TrackingId, invoice.TrackingId, invoice.TotalAmount);

    var result = await paymentProcessor.ProcessAsync(invoice, logger, daprClient);
    return Results.Ok(result);
});

app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

app.Run();

public interface IPaymentProcessor
{
    Task<PaymentResult> ProcessAsync(InvoicePayload invoice, ILogger logger, DaprClient? daprClient = null);
}

public class PaymentProcessor : IPaymentProcessor
{
    private readonly IBudgetService _budgetService;
    private readonly IPaymentGateway _paymentGateway;
    private readonly ConcurrentDictionary<string, bool> _processedInvoices = new();

    // fallback in-memory reservations for tests when Dapr client is not provided
    private readonly ConcurrentDictionary<string, decimal> _reservations = new();

    public PaymentProcessor(IBudgetService budgetService, IPaymentGateway paymentGateway)
    {
        _budgetService = budgetService;
        _paymentGateway = paymentGateway;
    }

    public async Task<PaymentResult> ProcessAsync(InvoicePayload invoice, ILogger logger, DaprClient? daprClient = null)
    {
        var trackingId = invoice.TrackingId;
        if (string.IsNullOrEmpty(trackingId))
        {
            trackingId = Guid.NewGuid().ToString();
            invoice.TrackingId = trackingId;
        }

        // Idempotency: prefer durable state when Dapr is available
        if (daprClient != null)
        {
            var processedKey = $"invoice-{trackingId}-processed";
            var processed = await daprClient.GetStateAsync<bool>(PaymentConstants.StateStoreName, processedKey);
            if (processed)
            {
                logger.LogWarning("{CorrelationId} Duplicate payment request ignored for invoice {TrackingId}.", trackingId, trackingId);
                return new PaymentResult(trackingId, false, "Duplicate request ignored.");
            }
        }
        else
        {
            if (!_processedInvoices.TryAdd(trackingId, true))
            {
                logger.LogWarning("{CorrelationId} Duplicate payment request ignored for invoice {TrackingId}.", trackingId, trackingId);
                return new PaymentResult(trackingId, false, "Duplicate request ignored.");
            }
        }

        logger.LogInformation("{CorrelationId} Reserving budget for invoice {TrackingId}.", trackingId, trackingId);
        var reserved = await _budgetService.ReserveBudgetAsync(invoice.Category, invoice.TotalAmount);
        if (!reserved)
        {
            logger.LogWarning("{CorrelationId} Insufficient budget for invoice {TrackingId}.", trackingId, trackingId);
            return new PaymentResult(trackingId, false, "Insufficient budget. Payment aborted.");
        }

        // Persist reservation when using Dapr so we can recover or compensate later
        if (daprClient != null)
        {
            var reservationKey = $"invoice-{trackingId}-reservation";
            await daprClient.SaveStateAsync(PaymentConstants.StateStoreName, reservationKey, invoice.TotalAmount);
        }
        else
        {
            _reservations[trackingId] = invoice.TotalAmount;
        }

        try
        {
            logger.LogInformation("{CorrelationId} Executing transfer for invoice {TrackingId}.", trackingId, trackingId);
            var paymentSuccessful = await _paymentGateway.ExecuteBankTransferAsync(invoice.Vendor, invoice.TotalAmount);
            if (paymentSuccessful)
            {
                // mark processed in durable store if available
                if (daprClient != null)
                {
                    var processedKey = $"invoice-{trackingId}-processed";
                    await daprClient.SaveStateAsync(PaymentConstants.StateStoreName, processedKey, true);
                    var reservationKey = $"invoice-{trackingId}-reservation";
                    await daprClient.DeleteStateAsync(PaymentConstants.StateStoreName, reservationKey);
                }
                else
                {
                    _reservations.TryRemove(trackingId, out _);
                }
                logger.LogInformation("{CorrelationId} Payment completed for invoice {TrackingId}.", trackingId, trackingId);
                return new PaymentResult(trackingId, true, "Payment completed successfully.");
            }
            logger.LogWarning("{CorrelationId} Payment failed for invoice {TrackingId}, compensating budget.", trackingId, trackingId);
            await _budgetService.ReleaseBudgetAsync(invoice.Category, invoice.TotalAmount);

            // cleanup reservation
                if (daprClient != null)
                {
                    var reservationKey = $"invoice-{trackingId}-reservation";
                    await daprClient.DeleteStateAsync(PaymentConstants.StateStoreName, reservationKey);
                }
            else
            {
                _reservations.TryRemove(trackingId, out _);
            }

            return new PaymentResult(trackingId, false, "Payment failed. Reserved budget released.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{CorrelationId} Unexpected failure while processing invoice {TrackingId}.", trackingId, trackingId);
            await _budgetService.ReleaseBudgetAsync(invoice.Category, invoice.TotalAmount);
            return new PaymentResult(trackingId, false, "Unexpected payment error. Reserved budget released.");
        }
    }
}

public interface IBudgetService
{
    Task<bool> ReserveBudgetAsync(string category, decimal amount);
    Task ReleaseBudgetAsync(string category, decimal amount);
}

public class BudgetService : IBudgetService
{
    public Task<bool> ReserveBudgetAsync(string category, decimal amount)
    {
        return Task.FromResult(true);
    }

    public Task ReleaseBudgetAsync(string category, decimal amount)
    {
        return Task.CompletedTask;
    }
}

public interface IPaymentGateway
{
    Task<bool> ExecuteBankTransferAsync(string vendor, decimal amount);
}

public class PaymentGateway : IPaymentGateway
{
    public Task<bool> ExecuteBankTransferAsync(string vendor, decimal amount)
    {
        return Task.FromResult(true);
    }
}

public class InvoicePayload
{
    public string? TrackingId { get; set; }
    public string Vendor { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? Status { get; set; }
}

public record PaymentResult(string TrackingId, bool Success, string Message);


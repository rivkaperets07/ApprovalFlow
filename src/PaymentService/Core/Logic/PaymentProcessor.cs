using Microsoft.Extensions.Logging;

public interface IPaymentProcessor
{
    Task<PaymentResult> ProcessAsync(InvoicePayload invoice);
}

/// <summary>
/// Saga coordinator for a single payment (ADR-002): claim → reserve → transfer, with a
/// compensating budget release on failure. Idempotency is TrackingId-keyed and lives in
/// IPaymentStateStore — a short-lived claim so two concurrent deliveries can't both
/// proceed, plus a permanent "processed" record written only after a real success so a
/// failed attempt can retry. This class is pure coordination: no Dapr types, no storage
/// branching — that's the state store's job.
/// </summary>
public class PaymentProcessor : IPaymentProcessor
{
    private readonly IBudgetService _budgetService;
    private readonly IPaymentGateway _paymentGateway;
    private readonly IPaymentStateStore _stateStore;
    private readonly ILogger<PaymentProcessor> _logger;

    public PaymentProcessor(IBudgetService budgetService, IPaymentGateway paymentGateway, IPaymentStateStore stateStore, ILogger<PaymentProcessor> logger)
    {
        _budgetService = budgetService;
        _paymentGateway = paymentGateway;
        _stateStore = stateStore;
        _logger = logger;
    }

    public async Task<PaymentResult> ProcessAsync(InvoicePayload invoice)
    {
        var trackingId = invoice.TrackingId;
        if (string.IsNullOrEmpty(trackingId))
        {
            trackingId = Guid.NewGuid().ToString();
            invoice.TrackingId = trackingId;
        }

        // Permanent dedup: a completed payment never runs twice, no matter how many times
        // the event is redelivered.
        if (await _stateStore.IsProcessedAsync(trackingId))
        {
            _logger.LogWarning("Duplicate payment request ignored for invoice {TrackingId}.", trackingId);
            return new PaymentResult(trackingId, false, "Duplicate request ignored.");
        }

        // Atomic claim: two concurrent deliveries of the same event race here, but only
        // one can win it. This is what the old check-then-act GetStateAsync<bool> lookup
        // did not guarantee, and is what let a redelivered event pay twice.
        if (!await _stateStore.TryClaimAsync(trackingId))
        {
            _logger.LogWarning("Duplicate payment request ignored for invoice {TrackingId}.", trackingId);
            return new PaymentResult(trackingId, false, "Duplicate request ignored.");
        }

        try
        {
            _logger.LogInformation("Reserving budget for invoice {TrackingId}.", trackingId);
            var reserved = await _budgetService.ReserveBudgetAsync(invoice.Category, invoice.TotalAmount);
            if (!reserved)
            {
                _logger.LogWarning("Insufficient budget for invoice {TrackingId}.", trackingId);
                return new PaymentResult(trackingId, false, "Insufficient budget. Payment aborted.");
            }

            // Persist the reservation so a crash mid-saga leaves evidence to compensate.
            await _stateStore.SaveReservationAsync(trackingId, invoice.TotalAmount);

            try
            {
                _logger.LogInformation("Executing transfer for invoice {TrackingId}.", trackingId);
                var paymentSuccessful = await _paymentGateway.ExecuteBankTransferAsync(invoice.Vendor, invoice.TotalAmount);
                if (paymentSuccessful)
                {
                    await _stateStore.MarkProcessedAsync(trackingId);
                    await _stateStore.ClearReservationAsync(trackingId);
                    _logger.LogInformation("Payment completed for invoice {TrackingId}.", trackingId);
                    return new PaymentResult(trackingId, true, "Payment completed successfully.");
                }

                _logger.LogWarning("Payment failed for invoice {TrackingId}, compensating budget.", trackingId);
                await _budgetService.ReleaseBudgetAsync(invoice.Category, invoice.TotalAmount);
                await _stateStore.ClearReservationAsync(trackingId);
                return new PaymentResult(trackingId, false, "Payment failed. Reserved budget released.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure while processing invoice {TrackingId}.", trackingId);
                await _budgetService.ReleaseBudgetAsync(invoice.Category, invoice.TotalAmount);
                return new PaymentResult(trackingId, false, "Unexpected payment error. Reserved budget released.");
            }
        }
        finally
        {
            // Always release the claim, even on failure, so a legitimate retry of a
            // failed (not completed) payment can be processed later instead of being
            // permanently locked out.
            await _stateStore.ReleaseClaimAsync(trackingId);
        }
    }
}

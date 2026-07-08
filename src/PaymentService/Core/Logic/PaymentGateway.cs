public interface IPaymentGateway
{
    Task<bool> ExecuteBankTransferAsync(string vendor, decimal amount);
}

public class PaymentGateway : IPaymentGateway
{
    // No real bank integration exists yet, so failures are simulated the same way test
    // payment gateways commonly do it (e.g. Stripe's test card numbers): a vendor name
    // containing "FailBank" always fails, so the Saga compensation path (ADR-002) can be
    // demonstrated end-to-end without a real banking dependency. Documented in the README
    // and used by the INV-1012 fixture / verify script.
    public Task<bool> ExecuteBankTransferAsync(string vendor, decimal amount)
    {
        var simulatedFailure = vendor.Contains("FailBank", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(!simulatedFailure);
    }
}

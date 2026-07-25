using DecisionEngine.Core.Models;

namespace DecisionEngine.Ai;

/// <summary>
/// Deterministic, no-network stand-in for a real vision fraud check - the default provider
/// and the one CI/tests always use. Same documented-fiction "magic string" idiom as
/// PaymentGateway's "FailBank" marker and StubReceiptOcrExtractor's "BLURRY-RECEIPT": a
/// "FAKE-RECEIPT" marker anywhere in the photo string simulates a fabricated-looking
/// receipt; anything else is treated as genuine. Not real fraud detection - just enough to
/// exercise the GLOBAL-RECEIPT-FRAUD path deterministically without a live model.
/// </summary>
public class StubReceiptFraudDetector : IReceiptFraudDetector
{
    private const string FakeReceiptMarker = "FAKE-RECEIPT";

    public Task<ReceiptFraudCheckResult> CheckAsync(string receiptImageDataUri, CancellationToken cancellationToken)
    {
        var isFake = receiptImageDataUri.Contains(FakeReceiptMarker, StringComparison.OrdinalIgnoreCase);
        var result = isFake
            ? new ReceiptFraudCheckResult { Verdict = ReceiptGenuinenessVerdict.Suspicious, Confidence = 0.90, Reasoning = "Stub fraud check: photo matched the FAKE-RECEIPT fixture marker." }
            : new ReceiptFraudCheckResult { Verdict = ReceiptGenuinenessVerdict.Genuine, Confidence = 0.95, Reasoning = "Stub fraud check: no fabrication marker found." };

        return Task.FromResult(result);
    }
}

using DecisionEngine.Core.Models;

namespace DecisionEngine.Ai;

/// <summary>
/// Judges whether a receipt photo looks genuine or fabricated - nothing else. Deliberately
/// narrow: field extraction is IReceiptOcrExtractor's job (a local library, not AI), kept
/// entirely separate so each component has exactly one responsibility. This interface never
/// returns a vendor, an amount, or anything that could feed a ceiling check - only a
/// genuineness verdict, a confidence, and why. Like IAiModelProvider, it only ever produces
/// a suggestion; PolicyEngine (via InvoiceEndpoints) is what decides what happens next, and
/// a Suspicious verdict can only ever push toward Escalated, never toward Approved.
/// </summary>
public interface IReceiptFraudDetector
{
    Task<ReceiptFraudCheckResult> CheckAsync(string receiptImageDataUri, CancellationToken cancellationToken);
}

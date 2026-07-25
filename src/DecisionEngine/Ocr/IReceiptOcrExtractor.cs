using DecisionEngine.Core.Models;

namespace DecisionEngine.Ocr;

/// <summary>
/// Reads a receipt photo and fills in the same fields a submitter would otherwise have
/// typed - Vendor and TotalAmount at minimum, LineItems and Currency best-effort. Plain
/// text/image processing, not an AI call: no network, no reasoning, just "what does this
/// image say." Genuineness judgment (is the photo fabricated) is a separate, narrower
/// concern - see IReceiptFraudDetector - so this interface never returns an opinion on
/// that, only what it could (or couldn't) read.
/// </summary>
public interface IReceiptOcrExtractor
{
    ReceiptExtractionResult Extract(string receiptImageDataUri);
}

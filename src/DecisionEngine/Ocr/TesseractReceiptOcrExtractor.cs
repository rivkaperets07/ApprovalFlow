using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using DecisionEngine.Core.Models;

namespace DecisionEngine.Ocr;

/// <summary>
/// Real OCR, shelling out to the `tesseract` CLI (installed in the Dockerfile via
/// `apt-get install tesseract-ocr`) rather than binding its native library directly -
/// simpler and far less fragile inside a container than chasing libtesseract/TESSDATA_PREFIX
/// paths across Tesseract versions, at the cost of a process-spawn per photo (acceptable:
/// this only runs once per submission, same cost class as the Groq HTTP call next to it).
///
/// Tesseract only transcribes pixels to text - it has no idea which line is the vendor or
/// the total, so a small set of plain heuristics does that part (not an AI call): the first
/// non-blank line is the vendor; the dollar amount nearest a "total" keyword (or the
/// largest one found, if no such keyword appears) is the total; lines shaped like
/// "description ... $amount" before that line become LineItems; any of the more common
/// non-$ currency symbols anywhere marks Currency. An unconfident vendor/amount read fails
/// closed (Succeeded = false) rather than guessing - a messy real-world receipt is the
/// expected failure mode, not an edge case to work around.
/// </summary>
public class TesseractReceiptOcrExtractor : IReceiptOcrExtractor
{
    private static readonly Regex DataUriPattern = new(@"^data:image/(?<ext>\w+);base64,(?<data>.+)$", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex DollarAmountPattern = new(@"\$\s?(\d{1,6}(?:,\d{3})*(?:\.\d{2})?)", RegexOptions.Compiled);
    // Fallback for real receipts that print a raw number with no "$" at all - common on
    // plain US POS printouts (confirmed live against several real photos tonight: a
    // vendor/total pair this clearly readable to a human still failed OCR outright
    // because neither line ever had a literal "$"). Requires an exact two-decimal-place
    // number specifically - distinctive enough not to be confused with a table number,
    // order id, or quantity that happens to sit on the same line as a "total" keyword.
    private static readonly Regex PlainAmountPattern = new(@"(?<!\d)(\d{1,6}(?:,\d{3})*\.\d{2})(?!\d)", RegexOptions.Compiled);
    // Two leading groups absorb OCR noise before the description ever starts, both optional:
    // a stray quote/apostrophe-like glyph (Tesseract intermittently transcribes one at the
    // very start of a line - a rendering/antialiasing artifact, not tied to any real
    // character on the receipt), then a quantity prefix ("1 Coffee", "2x Lunch" - real POS
    // receipts print quantity before the description far more often than not). Without
    // these the description's own ^[A-Za-z] anchor rejected the line outright, silently
    // dropping it from the itemized breakdown even though the receipt has one.
    // Description cap raised 40 -> 80: a real invoice/receipt line item description regularly
    // runs longer than 40 characters (e.g. "Cloud software subscription - Enterprise tier" is
    // 47) - the tighter cap silently rejected the whole line, indistinguishable from there
    // being no line item at all.
    // Comma added to the description charset: modifiers like "House Wine, glass" or
    // "Whiskey Neat, double" are common on real menus/receipts, and without it the comma
    // itself broke the match, silently dropping the whole line the same way the 40-char
    // cap and quote artifact did.
    private static readonly Regex LineItemPattern = new(@"^(?:['""`‘’]\s*)?(?:\d+\s*[xX]?\s+)?(?<description>[A-Za-z][A-Za-z0-9 ',&/\-]{2,80}?)\s{2,}\$?\s?(?<amount>\d{1,6}(?:\.\d{2})?)\s*$", RegexOptions.Compiled);
    private static readonly (string Symbol, string Code)[] CurrencySymbols = [("€", "EUR"), ("£", "GBP"), ("¥", "JPY")];
    private static readonly string[] PremiumTravelKeywords = ["first class", "business class", "premium cabin", "premium economy"];

    public ReceiptExtractionResult Extract(string receiptImageDataUri)
    {
        var match = DataUriPattern.Match(receiptImageDataUri);
        if (!match.Success)
        {
            return new ReceiptExtractionResult { Succeeded = false, FailureReason = "Not a recognizable data:image/... URI." };
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"receipt-{Guid.NewGuid():N}.{match.Groups["ext"].Value}");
        try
        {
            File.WriteAllBytes(tempFile, Convert.FromBase64String(match.Groups["data"].Value));
            var text = RunTesseract(tempFile);
            return ParseReceiptText(text);
        }
        catch (Exception ex)
        {
            return new ReceiptExtractionResult { Succeeded = false, FailureReason = $"OCR failed: {ex.Message}" };
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    private static string RunTesseract(string imagePath)
    {
        // preserve_interword_spaces=1: without it, Tesseract's plain-stdout output collapses
        // any column gap down to a single space regardless of how wide it actually is on the
        // page - silently defeating LineItemPattern's \s{2,} on every real receipt, since a
        // vendor/amount column gap would never survive as more than one space.
        using var process = Process.Start(new ProcessStartInfo("tesseract", $"\"{imagePath}\" stdout -c preserve_interword_spaces=1")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start the tesseract process.");

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static ReceiptExtractionResult ParseReceiptText(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0)
        {
            return new ReceiptExtractionResult { Succeeded = false, FailureReason = "Tesseract returned no readable text." };
        }

        var vendor = lines[0];

        var totalLine = lines.FirstOrDefault(l => l.Contains("total", StringComparison.OrdinalIgnoreCase) && (DollarAmountPattern.IsMatch(l) || PlainAmountPattern.IsMatch(l)));
        var amountSource = totalLine ?? lines.OrderByDescending(l => AllAmountsIn(l).DefaultIfEmpty(0m).Max()).FirstOrDefault();
        var amountMatch = amountSource is not null ? BestAmountMatch(amountSource) : Match.Empty;

        if (string.IsNullOrWhiteSpace(vendor) || !amountMatch.Success)
        {
            return new ReceiptExtractionResult { Succeeded = false, FailureReason = "Could not confidently identify both a vendor and a total amount." };
        }

        var currency = CurrencySymbols.FirstOrDefault(c => text.Contains(c.Symbol, StringComparison.Ordinal)).Code;

        var lineItems = lines
            .TakeWhile(l => l != totalLine)
            .Select(l => LineItemPattern.Match(l))
            .Where(m => m.Success)
            .Select(m => new LineItem(m.Groups["description"].Value.Trim(), ParseAmount(m.Groups["amount"].Value)))
            .ToList();

        // TRAVEL-03 (docs/policy.md): first/business-class is always human-reviewed. This
        // is a fact printed on the ticket itself (unlike TripId - see InvoicePayload.TripId
        // - which is business context the submitter must still supply), so it belongs in
        // OCR the same way alcohol-only detection belongs in PolicyEngine's LineItems check.
        var isPremiumTravel = PremiumTravelKeywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        return new ReceiptExtractionResult
        {
            Succeeded = true,
            Vendor = vendor,
            TotalAmount = ParseAmount(amountMatch.Groups[1].Value),
            Currency = currency,
            LineItems = lineItems.Count > 0 ? lineItems : null,
            IsPremiumTravel = isPremiumTravel
        };
    }

    // Dollar-prefixed amounts are the more confident signal, so they win whenever a line
    // has both (never expected in practice, but keeps the preference explicit rather than
    // relying on match order).
    private static Match BestAmountMatch(string line)
    {
        var dollar = DollarAmountPattern.Match(line);
        return dollar.Success ? dollar : PlainAmountPattern.Match(line);
    }

    private static IEnumerable<decimal> AllAmountsIn(string line)
    {
        foreach (Match m in DollarAmountPattern.Matches(line)) yield return ParseAmount(m.Groups[1].Value);
        foreach (Match m in PlainAmountPattern.Matches(line)) yield return ParseAmount(m.Groups[1].Value);
    }

    private static decimal ParseAmount(string raw) =>
        decimal.Parse(raw.Replace(",", string.Empty), NumberStyles.Number, CultureInfo.InvariantCulture);
}

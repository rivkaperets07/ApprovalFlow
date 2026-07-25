using DecisionEngine.Ocr;

public class StubReceiptOcrExtractorTests
{
    [Fact]
    public void BlurryMarker_ExtractionFails()
    {
        var extractor = new StubReceiptOcrExtractor();

        var result = extractor.Extract("data:image/png;base64,BLURRY-RECEIPT");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public void NoMarkerAtAll_ExtractionFails()
    {
        // A real photo (no fixture marker) is exactly what the real
        // TesseractReceiptOcrExtractor is for - the stub has nothing to read.
        var extractor = new StubReceiptOcrExtractor();

        var result = extractor.Extract("data:image/png;base64,not-a-fixture-at-all");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void OcrMarker_ExtractsVendorAmountLineItemsAndCurrency()
    {
        var extractor = new StubReceiptOcrExtractor();

        var result = extractor.Extract(
            "data:image/png;base64,OCR:AdBoost Media|1800|EUR|Q3 sponsorship package:1200;Digital advertising campaign:600");

        Assert.True(result.Succeeded);
        Assert.Equal("AdBoost Media", result.Vendor);
        Assert.Equal(1800m, result.TotalAmount);
        Assert.Equal("EUR", result.Currency);
        Assert.NotNull(result.LineItems);
        Assert.Equal(2, result.LineItems!.Count);
        Assert.Contains(result.LineItems, i => i.Description == "Q3 sponsorship package" && i.Amount == 1200m);
        Assert.Contains(result.LineItems, i => i.Description == "Digital advertising campaign" && i.Amount == 600m);
    }

    [Fact]
    public void OcrMarkerWithoutLineItemsOrCurrency_LeavesThemNull()
    {
        var extractor = new StubReceiptOcrExtractor();

        var result = extractor.Extract("data:image/png;base64,OCR:CloudSoft Inc|180|");

        Assert.True(result.Succeeded);
        Assert.Equal("CloudSoft Inc", result.Vendor);
        Assert.Equal(180m, result.TotalAmount);
        Assert.Null(result.Currency);
        Assert.Null(result.LineItems);
    }

    [Fact]
    public void OcrMarkerWithUnparsableAmount_ExtractionFails()
    {
        var extractor = new StubReceiptOcrExtractor();

        var result = extractor.Extract("data:image/png;base64,OCR:CloudSoft Inc|not-a-number|");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void PremiumMarker_SetsIsPremiumTravel()
    {
        var extractor = new StubReceiptOcrExtractor();

        var result = extractor.Extract("data:image/png;base64,OCR:Delta Airlines|1200||Business class fare:1200|PREMIUM");

        Assert.True(result.Succeeded);
        Assert.True(result.IsPremiumTravel);
    }

    [Fact]
    public void NoPremiumMarker_LeavesIsPremiumTravelFalse()
    {
        var extractor = new StubReceiptOcrExtractor();

        var result = extractor.Extract("data:image/png;base64,OCR:Delta Airlines|300||Economy fare:300");

        Assert.True(result.Succeeded);
        Assert.False(result.IsPremiumTravel);
    }
}

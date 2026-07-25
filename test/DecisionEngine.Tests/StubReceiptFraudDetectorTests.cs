using DecisionEngine.Ai;
using DecisionEngine.Core.Models;

public class StubReceiptFraudDetectorTests
{
    [Fact]
    public async Task FakeReceiptMarker_IsSuspicious()
    {
        var detector = new StubReceiptFraudDetector();

        var result = await detector.CheckAsync("data:image/png;base64,FAKE-RECEIPT OCR:Acme Supplies|50|", default);

        Assert.Equal(ReceiptGenuinenessVerdict.Suspicious, result.Verdict);
        Assert.NotEmpty(result.Reasoning);
    }

    [Fact]
    public async Task NoMarker_IsGenuine()
    {
        var detector = new StubReceiptFraudDetector();

        var result = await detector.CheckAsync("data:image/png;base64,OCR:CloudSoft Inc|180|", default);

        Assert.Equal(ReceiptGenuinenessVerdict.Genuine, result.Verdict);
    }
}

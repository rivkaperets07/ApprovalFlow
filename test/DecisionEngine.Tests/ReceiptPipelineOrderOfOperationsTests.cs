using Dapr.Client;
using DecisionEngine.Ai;
using DecisionEngine.Core.Logic;
using DecisionEngine.Core.Models;
using DecisionEngine.Endpoints;
using DecisionEngine.Ocr;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// docs/adr/008-receipt-photo-submission.md's central safety claim is the *order* OCR,
/// the fraud check, and the ordinary AI/PolicyEngine pipeline run in - not just that each
/// component works in isolation (StubReceiptOcrExtractorTests / StubReceiptFraudDetectorTests
/// already cover that). These tests exercise InvoiceEndpoints.EvaluateAndPublishAsync
/// directly (made `internal` + InternalsVisibleTo for exactly this purpose) with mocked
/// dependencies, proving the wiring itself - not just the pieces - enforces the two
/// invariants ADR 008 depends on: an unreadable photo never reaches the fraud check or the
/// AI, and a suspicious photo never reaches PolicyEngine.
/// </summary>
public class ReceiptPipelineOrderOfOperationsTests
{
    private static readonly ILogger<Program> Logger = new LoggerFactory().CreateLogger<Program>();

    private static InvoicePayload Invoice(string receiptImageDataUri) => new()
    {
        TrackingId = Guid.NewGuid().ToString(),
        ReceiptImageDataUri = receiptImageDataUri
    };

    private static Mock<DaprClient> DaprMock()
    {
        var mock = new Mock<DaprClient>();
        mock.Setup(c => c.ExecuteStateTransactionAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<StateTransactionRequest>>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    private static PolicyEngine BuildPolicyEngine(DaprClient daprClient)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GlobalGuardrails:RiskThreshold"] = "5000",
                ["GlobalGuardrails:DefaultMinConfidence"] = "0.80",
                ["GlobalGuardrails:FxMaxAmount"] = "1000",
                ["ExpensePolicies:Other:MaxAmount"] = "100",
                ["ExpensePolicies:Other:MinConfidence"] = "0.80",
                ["VendorDirectory:CloudSoft Inc"] = "SaaS",
                ["ExpensePolicies:SaaS:MaxAmount"] = "500",
                ["ExpensePolicies:SaaS:MinConfidence"] = "0.80",
            })
            .Build();
        return new PolicyEngine(config, daprClient);
    }

    [Fact]
    public async Task UnreadablePhoto_NeedsInfo_NeverCallsFraudDetectorOrAiProvider()
    {
        var daprMock = DaprMock();
        var ocrExtractor = new StubReceiptOcrExtractor(); // BLURRY-RECEIPT fails deterministically
        var fraudDetector = new Mock<IReceiptFraudDetector>(MockBehavior.Strict);
        var aiProvider = new Mock<IAiModelProvider>(MockBehavior.Strict);
        var policyEngine = BuildPolicyEngine(daprMock.Object);
        var invoice = Invoice("data:image/png;base64,BLURRY-RECEIPT");

        var result = await InvoiceEndpoints.EvaluateAndPublishAsync(
            invoice, invoice.TrackingId!, daprMock.Object, aiProvider.Object, ocrExtractor, fraudDetector.Object, policyEngine, Logger);

        Assert.False(result.Approved);
        Assert.Equal(InvoiceStatus.NeedsInfo, invoice.Status);
        Assert.Equal(DecidedBy.System, invoice.DecidedBy);
        Assert.Contains("GLOBAL-RECEIPT-UNREADABLE", result.Reason);
        // MockBehavior.Strict: any call to either would throw before Verify even runs.
        fraudDetector.VerifyNoOtherCalls();
        aiProvider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SuspiciousPhoto_Escalates_NeverReachesPolicyEngine()
    {
        var daprMock = DaprMock();
        var ocrExtractor = new StubReceiptOcrExtractor();
        var fraudDetector = new StubReceiptFraudDetector(); // FAKE-RECEIPT marker -> Suspicious
        var aiProvider = new Mock<IAiModelProvider>(MockBehavior.Strict);
        var policyEngine = BuildPolicyEngine(daprMock.Object);
        var invoice = Invoice("data:image/png;base64,FAKE-RECEIPT OCR:CloudSoft Inc|180|");

        var result = await InvoiceEndpoints.EvaluateAndPublishAsync(
            invoice, invoice.TrackingId!, daprMock.Object, aiProvider.Object, ocrExtractor, fraudDetector, policyEngine, Logger);

        Assert.False(result.Approved);
        Assert.Equal(InvoiceStatus.Escalated, invoice.Status);
        Assert.Equal(DecidedBy.System, invoice.DecidedBy);
        Assert.Contains("GLOBAL-RECEIPT-FRAUD", result.Reason);
        Assert.Equal("Suspicious", invoice.ReceiptVerificationVerdict);
        // Never auto-rejected, never approved - a Suspicious verdict only ever escalates
        // (confirmed explicitly with the project owner, see ADR 008).
        aiProvider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GenuinePhoto_OcrFieldsFlowIntoTheOrdinaryPipelineUnchanged()
    {
        var daprMock = DaprMock();
        var ocrExtractor = new StubReceiptOcrExtractor();
        var fraudDetector = new StubReceiptFraudDetector(); // no marker -> Genuine
        var aiProvider = new Mock<IAiModelProvider>();
        aiProvider.Setup(a => a.AnalyzeAsync(It.IsAny<InvoicePayload>(), "SaaS", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiAnalysisResult { ConfidenceScore = 0.95, Reasoning = "test", PolicyRulesCited = [] });
        var policyEngine = BuildPolicyEngine(daprMock.Object);
        // GLOBAL-RECEIPT requires an itemized breakdown over $25 - included here (via the
        // OCR marker's line-items segment) so this test exercises the AI path this test is
        // actually about, rather than tripping a guardrail fast-reject first.
        var invoice = Invoice("data:image/png;base64,OCR:CloudSoft Inc|80||Cloud subscription:80");

        var result = await InvoiceEndpoints.EvaluateAndPublishAsync(
            invoice, invoice.TrackingId!, daprMock.Object, aiProvider.Object, ocrExtractor, fraudDetector, policyEngine, Logger);

        // OCR wrote straight into the same fields a typed submission would have used -
        // PolicyEngine never learns the invoice came from a photo (ADR 008 / M12).
        Assert.Equal("CloudSoft Inc", invoice.Vendor);
        Assert.Equal(80m, invoice.TotalAmount);
        Assert.Equal("Genuine", invoice.ReceiptVerificationVerdict);
        aiProvider.Verify(a => a.AnalyzeAsync(It.IsAny<InvoicePayload>(), "SaaS", It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(result.Approved);
    }
}

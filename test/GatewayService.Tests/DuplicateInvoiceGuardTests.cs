using Dapr.Client;
using Moq;
using Xunit;

public class DuplicateInvoiceGuardTests
{
    [Fact]
    public async Task FirstSubmission_IsNotADuplicate_AndGetsRecorded()
    {
        var daprMock = new Mock<DaprClient>();
        daprMock.Setup(c => c.GetStateAndETagAsync<bool?>("statestore", It.IsAny<string>(), null, null, default))
            .ReturnsAsync(((bool?)null, ""));

        var result = await DuplicateInvoiceGuard.IsDuplicateAsync(daprMock.Object, "statestore", "CloudSoft Inc", "INV-4471", 350m);

        Assert.False(result);
        daprMock.Verify(c => c.TrySaveStateAsync("statestore", DuplicateInvoiceGuard.BuildKey("CloudSoft Inc", "INV-4471", 350m), true, "", null, null, default), Times.Once);
    }

    [Fact]
    public async Task SameVendorInvoiceNumberAndTotal_IsRejectedAsDuplicate()
    {
        var daprMock = new Mock<DaprClient>();
        daprMock.Setup(c => c.GetStateAndETagAsync<bool?>("statestore", It.IsAny<string>(), null, null, default))
            .ReturnsAsync(((bool?)true, "etag-1"));

        var result = await DuplicateInvoiceGuard.IsDuplicateAsync(daprMock.Object, "statestore", "CloudSoft Inc", "INV-4471", 350m);

        Assert.True(result);
        daprMock.Verify(c => c.TrySaveStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(), null, null, default), Times.Never);
    }

    [Fact]
    public async Task NoExpiry_SameCombinationYearsLater_IsStillADuplicate()
    {
        // Unlike GLOBAL-FRAUD's 24h window, GLOBAL-DUP never expires — an invoice number
        // should never legitimately repeat for the same vendor and amount, no matter how
        // long ago it was first seen.
        var daprMock = new Mock<DaprClient>();
        daprMock.Setup(c => c.GetStateAndETagAsync<bool?>("statestore", It.IsAny<string>(), null, null, default))
            .ReturnsAsync(((bool?)true, "etag-1"));

        var result = await DuplicateInvoiceGuard.IsDuplicateAsync(daprMock.Object, "statestore", "CloudSoft Inc", "INV-4471", 350m);

        Assert.True(result);
    }

    [Fact]
    public void BuildKey_DifferentInvoiceNumber_ProducesDifferentKey()
    {
        var key1 = DuplicateInvoiceGuard.BuildKey("CloudSoft Inc", "INV-4471", 350m);
        var key2 = DuplicateInvoiceGuard.BuildKey("CloudSoft Inc", "INV-4472", 350m);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void BuildKey_DifferentVendor_ProducesDifferentKey()
    {
        // Two different vendors happening to reuse the same invoice number/amount are not
        // the same duplicate.
        var key1 = DuplicateInvoiceGuard.BuildKey("CloudSoft Inc", "INV-4471", 350m);
        var key2 = DuplicateInvoiceGuard.BuildKey("Northwind Cloud Services", "INV-4471", 350m);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void BuildKey_IsCaseAndWhitespaceInsensitive()
    {
        var key1 = DuplicateInvoiceGuard.BuildKey("  CloudSoft Inc  ", "  inv-4471  ", 350m);
        var key2 = DuplicateInvoiceGuard.BuildKey("cloudsoft inc", "INV-4471", 350.00m);

        Assert.Equal(key1, key2);
    }
}

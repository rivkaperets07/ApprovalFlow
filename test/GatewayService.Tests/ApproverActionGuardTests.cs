using ApprovalFlow.Contracts;
using Xunit;

public class ApproverActionGuardTests
{
    [Theory]
    [InlineData(InvoiceStatus.Escalated)]
    [InlineData(InvoiceStatus.NeedsInfo)]
    public void ItemsAwaitingAHuman_CanBeActedOn(string status)
    {
        Assert.True(ApproverActionGuard.CanActOn(status));
    }

    // Pending: still racing the DecisionEngine. Approved: invoice.approved already fired —
    // re-approving would double-publish it, and this is the path that used to let a Travel
    // trip total be counted twice (request-info on an Approved invoice → provide-info →
    // full re-evaluation). Rejected/Duplicate: terminal for the audit trail.
    [Theory]
    [InlineData(InvoiceStatus.Pending)]
    [InlineData(InvoiceStatus.Approved)]
    [InlineData(InvoiceStatus.Rejected)]
    [InlineData(InvoiceStatus.Duplicate)]
    [InlineData(null)]
    [InlineData("")]
    public void InFlightOrFinalItems_CannotBeActedOn(string? status)
    {
        Assert.False(ApproverActionGuard.CanActOn(status));
    }
}

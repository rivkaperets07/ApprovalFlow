namespace ApprovalFlow.Contracts;

/// <summary>DecisionEngine's <c>GET /vendors</c> response shape, proxied by the Gateway.</summary>
public record VendorEntry(string Vendor, string Category);

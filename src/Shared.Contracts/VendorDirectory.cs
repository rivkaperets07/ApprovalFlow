using Microsoft.Extensions.Configuration;

namespace ApprovalFlow.Contracts;

/// <summary>
/// The one way to read the bind-mounted vendor-directory.json config section as "the set
/// of known vendors". GLOBAL-FRAUD (Gateway) and GLOBAL-VENDOR (PolicyEngine) must agree
/// on membership semantics — same trim, same case-insensitivity — or a vendor could be
/// "known" to one guardrail and "brand-new" to the other; each previously had its own
/// copy of this LINQ, which is exactly how that drift starts.
/// </summary>
public static class VendorDirectory
{
    public const string SectionName = "VendorDirectory";

    public static IReadOnlySet<string> LoadKnownVendors(IConfiguration config)
        => config.GetSection(SectionName).GetChildren()
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

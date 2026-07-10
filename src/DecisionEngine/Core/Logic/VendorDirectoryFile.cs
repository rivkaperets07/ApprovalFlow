using System.Text.Json;
using System.Text.Json.Nodes;

namespace DecisionEngine.Core.Logic;

/// <summary>
/// Read/write access to the same vendor-directory.json IConfiguration loads with
/// reloadOnChange (DecisionEngine.cs) — a write here is picked up by the next
/// PolicyEngine evaluation with no restart, the same mechanism ADR 004 already relies on
/// for policies.json. A static lock serializes concurrent admin writes within this
/// process (the only writer of this file) so two overlapping POST /vendors calls can't
/// interleave and corrupt the JSON.
/// </summary>
public static class VendorDirectoryFile
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    /// <summary>
    /// Adds a new vendor, or updates the category of an existing one. Matches an existing
    /// entry case-insensitively so re-adding "cloudsoft inc" corrects CloudSoft Inc's
    /// category instead of creating a duplicate that differs only by case (GLOBAL-VENDOR
    /// and GLOBAL-FRAUD's known-vendor checks are already case-insensitive; the directory
    /// itself should not silently grow case-variant duplicates).
    /// </summary>
    public static async Task<VendorEntry> AddOrUpdateAsync(string path, string vendor, string category)
    {
        await WriteLock.WaitAsync();
        try
        {
            var json = await File.ReadAllTextAsync(path);
            var root = JsonNode.Parse(json)!.AsObject();
            var directory = root["VendorDirectory"]!.AsObject();

            var existingKey = directory.Select(kv => kv.Key)
                .FirstOrDefault(k => string.Equals(k, vendor, StringComparison.OrdinalIgnoreCase));
            if (existingKey is not null)
            {
                directory.Remove(existingKey);
            }
            directory[vendor] = category;

            await AtomicFileWriter.WriteAsync(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return new VendorEntry(vendor, category);
        }
        finally
        {
            WriteLock.Release();
        }
    }
}

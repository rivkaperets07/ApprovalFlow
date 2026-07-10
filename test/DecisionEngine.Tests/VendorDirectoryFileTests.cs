using DecisionEngine.Core.Logic;
using System.Text.Json.Nodes;
using Xunit;

public class VendorDirectoryFileTests
{
    private static string WriteTempDirectory(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vendor-directory-{Guid.NewGuid()}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task AddOrUpdateAsync_NewVendor_AddsEntry()
    {
        var path = WriteTempDirectory("""{ "VendorDirectory": { "CloudSoft Inc": "SaaS" } }""");
        try
        {
            var result = await VendorDirectoryFile.AddOrUpdateAsync(path, "Acme Cloud Co", "SaaS");

            Assert.Equal("Acme Cloud Co", result.Vendor);
            Assert.Equal("SaaS", result.Category);

            var saved = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            var directory = saved["VendorDirectory"]!.AsObject();
            Assert.Equal("SaaS", directory["CloudSoft Inc"]!.GetValue<string>());
            Assert.Equal("SaaS", directory["Acme Cloud Co"]!.GetValue<string>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AddOrUpdateAsync_ExistingVendor_UpdatesCategoryInPlace()
    {
        var path = WriteTempDirectory("""{ "VendorDirectory": { "CloudSoft Inc": "SaaS", "Dell Technologies": "Hardware" } }""");
        try
        {
            await VendorDirectoryFile.AddOrUpdateAsync(path, "CloudSoft Inc", "Marketing");

            var saved = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            var directory = saved["VendorDirectory"]!.AsObject();
            Assert.Equal("Marketing", directory["CloudSoft Inc"]!.GetValue<string>());
            Assert.Equal("Hardware", directory["Dell Technologies"]!.GetValue<string>());
            Assert.Equal(2, directory.Count); // updated in place, not duplicated
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AddOrUpdateAsync_ExistingVendorDifferentCase_MatchesCaseInsensitively()
    {
        var path = WriteTempDirectory("""{ "VendorDirectory": { "CloudSoft Inc": "SaaS" } }""");
        try
        {
            var result = await VendorDirectoryFile.AddOrUpdateAsync(path, "cloudsoft inc", "Marketing");

            Assert.Equal("cloudsoft inc", result.Vendor);

            var saved = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            var directory = saved["VendorDirectory"]!.AsObject();
            // The old-cased key is gone, replaced by the newly-submitted casing — no
            // duplicate entries differing only by case.
            Assert.False(directory.ContainsKey("CloudSoft Inc"));
            Assert.Equal("Marketing", directory["cloudsoft inc"]!.GetValue<string>());
            Assert.Single(directory);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

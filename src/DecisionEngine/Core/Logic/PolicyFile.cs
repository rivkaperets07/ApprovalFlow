namespace DecisionEngine.Core.Logic;

/// <summary>
/// Read/write access to the same policies.json IConfiguration loads with
/// reloadOnChange (ADR 004) — a write here is picked up by the next PolicyEngine
/// evaluation with no restart. Kept as a plain string in and out rather than round-
/// tripping through GlobalGuardrailsConfig/PolicyConfig: deserializing into those types
/// and re-serializing would silently drop any field an admin's edit adds that isn't in
/// the current C# shape yet. Structural validation (right top-level sections present) is
/// the caller's job — PolicyEngine's own fallbacks and hard-coded gate logic are what
/// keep a structurally-thin-but-wrong edit from becoming unsafe, not this file.
/// </summary>
public static class PolicyFile
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public static Task<string> ReadAsync(string path) => File.ReadAllTextAsync(path);

    public static async Task WriteAsync(string path, string json)
    {
        await WriteLock.WaitAsync();
        try
        {
            await AtomicFileWriter.WriteAsync(path, json);
        }
        finally
        {
            WriteLock.Release();
        }
    }
}

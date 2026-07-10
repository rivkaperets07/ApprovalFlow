namespace DecisionEngine.Core.Logic;

/// <summary>
/// Same-volume atomic file replace: write to a sibling temp file, then File.Move over the
/// target (a rename at the OS level, not a truncate-in-place). Used by PolicyFile and
/// VendorDirectoryFile so a reload triggered by IConfiguration's own reloadOnChange watcher
/// — running in the same process, on its own thread, not coordinated by either class's
/// write lock — can never observe a half-written file. A plain File.WriteAllTextAsync
/// truncates then writes, leaving a window where a reload mid-write hits invalid JSON.
/// </summary>
public static class AtomicFileWriter
{
    public static async Task WriteAsync(string path, string content)
    {
        var tempPath = $"{path}.tmp";
        await File.WriteAllTextAsync(tempPath, content);
        File.Move(tempPath, path, overwrite: true);
    }
}

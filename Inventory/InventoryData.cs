using System.Text.Json.Serialization;

namespace FilesizeChecker.Inventory;

internal enum FileStatus
{
    Recorded,
    New,
    Missing,
    Changed,
    Unavailable
}

internal sealed record FileEntry(string Path, long Bytes, DateTime ModifiedUtc, DateTime CreatedUtc,
    FileAttributes Attributes)
{
    [JsonIgnore]
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";

    [JsonIgnore]
    public string Name => System.IO.Path.GetFileName(Path);

    [JsonIgnore]
    public string Extension => System.IO.Path.GetExtension(Path);

    public string ChangesSince(FileEntry previous)
    {
        var changes = new List<string>(4);
        if (Bytes != previous.Bytes)
            changes.Add($"Größe: {previous.Bytes:N0} → {Bytes:N0} Bytes");
        if (ModifiedUtc != previous.ModifiedUtc)
            changes.Add($"Geändert: {previous.ModifiedUtc.ToLocalTime():G} → {ModifiedUtc.ToLocalTime():G}");
        if (CreatedUtc != previous.CreatedUtc)
            changes.Add($"Erstellt: {previous.CreatedUtc.ToLocalTime():G} → {CreatedUtc.ToLocalTime():G}");
        if (Attributes != previous.Attributes)
            changes.Add($"Attribute: {previous.Attributes} → {Attributes}");
        return string.Join(" · ", changes);
    }
}

internal sealed record FileResult(FileEntry File, FileStatus Status, string Details = "");

internal sealed record ScanIssue(string Path, string Reason);

internal sealed record RootInventory(string Root, bool HasBaseline, IReadOnlyList<FileResult> Files,
    IReadOnlyList<ScanIssue> Issues);

internal sealed record InventorySnapshot(DateTime CompletedUtc, IReadOnlyList<RootInventory> Roots);

internal sealed record ScanProgress(string Folder, int Files);

internal sealed record StorageRow(string Group, int Count, long Bytes);

internal static class InventoryPaths
{
    public static StringComparer Comparer { get; } = StringComparer.OrdinalIgnoreCase;

    public static bool IsWithin(string path, string folder) => Comparer.Equals(path, folder)
        || path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}

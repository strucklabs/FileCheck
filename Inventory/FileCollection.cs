namespace FilesizeChecker.Inventory;

internal sealed record CollectionInput(FileEntry File, string Evidence);

internal sealed class CollectedFile(FileEntry file)
{
    public FileEntry File { get; } = file;
    public HashSet<string> Evidence { get; } = new(StringComparer.Ordinal);
    public string Summary => string.Join(" · ", Evidence);
}

internal sealed class FileCollection
{
    private readonly Dictionary<string, CollectedFile> files = new(InventoryPaths.Comparer);

    public int Count => files.Count;
    public IEnumerable<CollectedFile> Items => files.Values;

    public (int Added, bool Changed) Add(IEnumerable<CollectionInput> items)
    {
        int added = 0;
        bool changed = false;
        foreach (CollectionInput input in items)
        {
            if (!files.TryGetValue(input.File.Path, out CollectedFile? entry))
            {
                files[input.File.Path] = entry = new CollectedFile(input.File);
                added++;
            }
            changed |= entry.Evidence.Add(input.Evidence);
        }
        return (added, changed);
    }

    public int Remove(IEnumerable<string> paths) => paths.Count(files.Remove);
}

internal sealed record CollectionImport(CollectionInput[] Files, int Folders, IReadOnlyList<ScanIssue> Issues)
{
    public static CollectionImport ReadExplorerFiles(string[] paths)
    {
        var entries = new List<CollectionInput>();
        var issues = new List<ScanIssue>();
        int folders = 0;
        foreach (string path in paths.Distinct(InventoryPaths.Comparer))
        {
            try
            {
                if (!Path.IsPathFullyQualified(path))
                    throw new ArgumentException("Ein absoluter Dateipfad ist erforderlich.");
                string fullPath = Path.GetFullPath(path);
                if (File.GetAttributes(fullPath).HasFlag(FileAttributes.Directory))
                {
                    folders++;
                    continue;
                }
                entries.Add(new CollectionInput(FileVersions.ReadEntry(fullPath), "Windows-Explorer"));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                issues.Add(new ScanIssue(path, error.Message));
            }
        }
        return new CollectionImport([.. entries], folders, issues);
    }
}

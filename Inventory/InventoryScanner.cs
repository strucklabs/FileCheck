using System.Diagnostics;

namespace FilesizeChecker.Inventory;

internal static class InventoryScanner
{
    public static InventorySnapshot Scan(IReadOnlyList<string> roots, InventorySnapshot? previous,
        IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var previousRoots = (previous?.Roots ?? []).ToDictionary(root => root.Root, InventoryPaths.Comparer);
        var result = new List<RootInventory>(roots.Count);
        var reportInterval = Stopwatch.StartNew();
        int files = 0;

        foreach (string root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            previousRoots.TryGetValue(root, out RootInventory? baseline);
            var lastKnown = new Dictionary<string, FileEntry>(InventoryPaths.Comparer);
            foreach (FileResult entry in baseline?.Files ?? [])
            {
                if (entry.Status != FileStatus.Missing)
                    lastKnown[entry.File.Path] = entry.File;
            }

            var rows = new List<FileResult>();
            var issues = new Dictionary<string, ScanIssue>(InventoryPaths.Comparer);
            var folders = new Stack<DirectoryInfo>();
            folders.Push(new DirectoryInfo(root));
            bool readDirectory = false;

            while (folders.TryPop(out DirectoryInfo? folder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reportInterval.ElapsedMilliseconds >= 150)
                {
                    progress?.Report(new ScanProgress(folder.FullName, files));
                    reportInterval.Restart();
                }

                var contents = new List<FileSystemInfo>();
                try
                {
                    foreach (FileSystemInfo item in folder.EnumerateFileSystemInfos())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        contents.Add(item);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    issues[folder.FullName] = new ScanIssue(folder.FullName, error.Message);
                    continue;
                }

                readDirectory = true;
                foreach (FileSystemInfo item in contents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (item is DirectoryInfo subfolder)
                        {
                            if (subfolder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            {
                                issues[subfolder.FullName] = new ScanIssue(subfolder.FullName,
                                    "Verknüpfter Ordner wurde übersprungen.");
                            }
                            else
                            {
                                folders.Push(subfolder);
                            }
                            continue;
                        }

                        var file = (FileInfo)item;
                        var current = new FileEntry(file.FullName, file.Length, file.LastWriteTimeUtc,
                            file.CreationTimeUtc, file.Attributes);
                        if (lastKnown.Remove(current.Path, out FileEntry? old))
                        {
                            string changes = current.ChangesSince(old);
                            rows.Add(changes.Length > 0
                                ? new FileResult(current, FileStatus.Changed, changes)
                                : new FileResult(current, FileStatus.Recorded));
                        }
                        else
                        {
                            rows.Add(new FileResult(current, baseline?.HasBaseline == true
                                ? FileStatus.New : FileStatus.Recorded));
                        }
                        files++;
                        if (reportInterval.ElapsedMilliseconds >= 150)
                        {
                            progress?.Report(new ScanProgress(folder.FullName, files));
                            reportInterval.Restart();
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        issues[item.FullName] = new ScanIssue(item.FullName, error.Message);
                    }
                }
            }

            foreach (FileEntry old in lastKnown.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ScanIssue? issue = FindIssue(old.Path, issues);
                rows.Add(issue is null
                    ? new FileResult(old, FileStatus.Missing)
                    : new FileResult(old, FileStatus.Unavailable, issue.Reason));
            }

            result.Add(new RootInventory(root, readDirectory || baseline?.HasBaseline == true,
                rows, [.. issues.Values]));
            progress?.Report(new ScanProgress(root, files));
        }

        return new InventorySnapshot(DateTime.UtcNow, result);
    }

    private static ScanIssue? FindIssue(string path, Dictionary<string, ScanIssue> issues)
    {
        if (issues.Count == 0)
            return null;

        for (string? parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
        {
            if (issues.TryGetValue(parent, out ScanIssue? issue))
                return issue;
        }
        return null;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesizeChecker.Inventory;

internal enum TransferPhase { Ready, Staging, Staged, BackingUp, BackedUp, Publishing, Published, Removing, Finished }
internal enum RunDirection { Forward, Undo }

internal sealed record ApprovedFile(string Source, string Target, WindowsFileStamp Input, WindowsFileStamp? OriginalTarget,
    ulong TargetVolume, bool Direct, long CopyBytes);

internal sealed record FileRunHeader(Guid Id, DateTime CreatedUtc, bool Move, ApprovedFile[] Files,
    string[] Skipped, RunDirection Direction = RunDirection.Forward);

internal sealed class FileTransfer
{
    public TransferPhase Phase { get; set; }
    public string? Stage { get; set; }
    public WindowsFileStamp? StagedFile { get; set; }
    public WindowsFileStamp? Output { get; set; }
    public List<string> InterruptedCopies { get; set; } = [];
}

internal sealed class FileRunEntry
{
    public FileTransfer Forward { get; set; } = new();
    public FileTransfer Reverse { get; set; } = new();
    public WindowsFileStamp? Backup { get; set; }
    public long? BackupBytes { get; set; }
    public bool RemovingCopy { get; set; }
    public bool CopyRemoved { get; set; }
    public bool RestoringBackup { get; set; }
    public bool Undone { get; set; }
    public bool ReleasingBackup { get; set; }
    public bool BackupReleased { get; set; }
    public string? Error { get; set; }
}

internal sealed class FileRun(FileRunHeader header, FileRunEntry[] entries)
{
    public FileRunHeader Header { get; set; } = header;
    public FileRunEntry[] Entries { get; } = entries;
    public bool HasOpenChanges => Entries.Any(entry => entry.Forward.Phase is not (TransferPhase.Ready or TransferPhase.Finished));
    public bool CanContinue => Header.Direction == RunDirection.Forward
        && Entries.Any(entry => entry.Forward.Phase != TransferPhase.Finished);
    public bool CanUndo => Entries.All(entry => entry.Forward.Phase is TransferPhase.Ready or TransferPhase.Staging
        or TransferPhase.Staged or TransferPhase.Finished) && Entries.Any(entry => entry.Forward.Phase == TransferPhase.Finished
        && !entry.Undone && !entry.BackupReleased && !entry.ReleasingBackup);
    public bool CanRelease => !HasOpenChanges && Entries.Any(entry => entry.Backup is not null && !entry.Undone
        && !entry.BackupReleased && !entry.RestoringBackup && entry.Reverse.Phase == TransferPhase.Ready && !entry.RemovingCopy);

    public string BackupPath(int index) => Artifact(Header.Files[index].Target, index, "backup");
    public string Artifact(string target, int index, string role) => Path.Combine(Path.GetDirectoryName(target)!,
        $".filesize-checker-{Header.Id:N}-{index}-{role}");
}

[JsonSerializable(typeof(FileRunHeader))]
[JsonSerializable(typeof(FileRunEntry))]
internal partial class FileRunJson : JsonSerializerContext;

internal sealed class FileRunJournal(string dataFolder)
{
    private string DirectoryPath => Path.Combine(dataFolder, "sort-runs");
    private string RunFolder(Guid id) => Path.Combine(DirectoryPath, id.ToString("N"));

    public FileRun Create(bool move, ApprovedFile[] files, string[] skipped)
    {
        var header = new FileRunHeader(Guid.NewGuid(), DateTime.UtcNow, move, files, skipped);
        var run = new FileRun(header, Enumerable.Range(0, files.Length).Select(_ => new FileRunEntry()).ToArray());
        SaveHeader(run);
        return run;
    }

    public void SaveHeader(FileRun run) => SaveWithRetry(() => LocalJsonFile.Save(Path.Combine(RunFolder(run.Header.Id), "run.json"),
        run.Header, FileRunJson.Default.FileRunHeader));

    public void SaveEntry(FileRun run, int index) => SaveWithRetry(() => LocalJsonFile.Save(EntryPath(run.Header.Id, index),
        run.Entries[index], FileRunJson.Default.FileRunEntry));

    private static void SaveWithRetry(Action save)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                save();
                return;
            }
            catch (Exception error) when (attempt < 4 && (error is UnauthorizedAccessException
                || (error is IOException && (error.HResult & 0xFFFF) is 5 or 32 or 33)))
            {
                Thread.Sleep(25 << attempt);
            }
        }
    }

    private string EntryPath(Guid run, int index) => Path.Combine(RunFolder(run), $"{index}.json");

    public FileRun[] Load(List<string> errors)
    {
        if (!Directory.Exists(DirectoryPath))
            return [];
        var runs = new List<FileRun>();
        foreach (string folder in Directory.EnumerateDirectories(DirectoryPath))
        {
            try
            {
                FileRunHeader? header = LocalJsonFile.Load(Path.Combine(folder, "run.json"), FileRunJson.Default.FileRunHeader);
                if (header is null)
                    continue;
                if (!InventoryPaths.Comparer.Equals(folder, RunFolder(header.Id)))
                    throw new InvalidDataException("Laufkennung passt nicht zum Protokollordner.");
                var entries = new FileRunEntry[header.Files.Length];
                for (int index = 0; index < entries.Length; index++)
                    entries[index] = LocalJsonFile.Load(EntryPath(header.Id, index), FileRunJson.Default.FileRunEntry) ?? new();
                runs.Add(new FileRun(header, entries));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                errors.Add($"{folder} · {error.Message}");
            }
        }
        return runs.OrderByDescending(run => run.Header.CreatedUtc).ToArray();
    }
}

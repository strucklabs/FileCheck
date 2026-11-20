using Microsoft.Win32.SafeHandles;
using static FilesizeChecker.Inventory.WindowsFileOperations;

namespace FilesizeChecker.Inventory;

internal sealed record FileRunPreflight(ApprovedFile[] Entries, string[] Skipped, ScanIssue[] Issues);
internal sealed record FileRunProgress(int Index, int Total, string Path, string Phase);
internal sealed record FileRunOutcome(bool Complete, string Status);
internal sealed record BackupRelease(int Index, string Path, long Bytes);

internal sealed class FileRunService(FileRunJournal journal)
{
    public static FileRunPreflight Preflight(IEnumerable<PlanRow> plan, bool move)
    {
        PlanRow[] rows = plan.ToArray();
        var entries = new List<ApprovedFile>();
        var issues = new List<ScanIssue>();
        var sourcePaths = rows.Where(row => !row.Excluded).Select(row => row.Source.Path).ToHashSet(InventoryPaths.Comparer);
        var targets = new HashSet<string>(InventoryPaths.Comparer);
        var space = new Dictionary<ulong, (long Required, long Free, string Folder)>();
        foreach (PlanRow row in rows.Where(row => !row.Excluded))
        {
            try
            {
                if (row.Blocked || row.Target.Length == 0)
                    throw new IOException(row.Details.Length == 0 ? "Ziel ist blockiert." : row.Details);
                if (row.Replace && row.Replacement is null)
                    throw new IOException("Der geprüfte Stand der zu ersetzenden Datei fehlt. Aktualisiere die Vorschau.");
                if (sourcePaths.Contains(row.Target) || !targets.Add(row.Target))
                    throw new IOException($"Ziel ist eine Quelle oder wird mehrfach verwendet: {row.Target}");

                using SafeFileHandle source = OpenFile(row.Source.Path, delete: move);
                WindowsFileStamp input = ReadStamp(source, row.Source.Path);
                if (input.Bytes != row.Source.Bytes || input.Written != row.Source.ModifiedUtc.ToFileTimeUtc()
                    || input.Created != row.Source.CreatedUtc.ToFileTimeUtc() || input.Attributes != row.Source.Attributes)
                    throw new IOException("Quelle hat sich seit dem Inventarscan geändert. Scanne den Bestand erneut.");
                (ulong volume, long free, string folder) = CheckDestination(row.Target);
                WindowsFileStamp? target = VerifyOriginalTarget(row.Target, row.Replacement);
                if (target is not null && target.Identity == input.Identity)
                    throw new IOException("Quelle und Ziel verweisen auf dieselbe Datei.");
                bool direct = move && volume == input.Identity.Volume;
                long bytes = direct ? 0 : CopySpace(row.Source.Path);
                var previous = space.GetValueOrDefault(volume, (0L, free, folder));
                space[volume] = (checked(previous.Item1 + bytes), Math.Min(previous.Item2, free), folder);
                entries.Add(new ApprovedFile(row.Source.Path, row.Target, input, target, volume, direct, bytes));
            }
            catch (Exception error) when (Expected(error))
            {
                issues.Add(new ScanIssue(row.Source.Path, error.Message));
            }
        }

        var sourceIds = entries.Select(entry => entry.Input.Identity).ToHashSet();
        foreach (ApprovedFile entry in entries)
        {
            if (entry.OriginalTarget is not null && sourceIds.Contains(entry.OriginalTarget.Identity))
                issues.Add(new ScanIssue(entry.Target, "Zieldatei ist über einen Hardlink mit einer Quelle des Plans verbunden."));
        }
        if (move)
        {
            foreach (IGrouping<FileIdentity, ApprovedFile> links in entries.GroupBy(entry => entry.Input.Identity).Where(group => group.Skip(1).Any()))
            {
                foreach (ApprovedFile link in links)
                    issues.Add(new ScanIssue(link.Source, "Mehrere Hardlinks derselben Datei stehen im Verschiebeplan. Wähle pro Lauf einen Pfad."));
            }
        }

        foreach ((long required, long free, string folder) in space.Values)
        {
            if (required > free)
                issues.Add(new ScanIssue(folder, $"Zu wenig freier Speicher. Benötigt {InventoryLabels.Size(required)}, verfügbar {InventoryLabels.Size(free)}."));
        }

        return new FileRunPreflight([.. entries], rows.Where(row => row.Excluded).Select(row => row.Source.Path).ToArray(), [.. issues]);
    }

    private static WindowsFileStamp? VerifyOriginalTarget(string path, WindowsFileStamp? expected)
    {
        if (expected is null)
        {
            if (Exists(path))
                throw new IOException($"Ziel ist inzwischen belegt: {path}");
            return null;
        }

        using SafeFileHandle target = OpenFile(path, delete: true);
        return Require(target, path, expected);
    }

    public FileRunOutcome Execute(FileRun run, bool undo, IProgress<FileRunProgress>? progress, CancellationToken pause)
    {
        if (undo && !run.CanUndo)
            return new FileRunOutcome(false, "Zuerst den teilweise abgeschlossenen Vorgang im Protokoll klären.");
        if (!undo && run.Header.Direction == RunDirection.Undo)
            return new FileRunOutcome(false, "Für diesen Lauf wurde bereits Rückgängig gestartet.");
        if (undo && run.Header.Direction != RunDirection.Undo)
        {
            run.Header = run.Header with { Direction = RunDirection.Undo };
            journal.SaveHeader(run);
        }

        int count = run.Entries.Length;
        for (int position = 0; position < count; position++)
        {
            if (pause.IsCancellationRequested)
                return new FileRunOutcome(false, "Lauf wurde nach der letzten Datei pausiert.");
            int index = undo ? count - position - 1 : position;
            ApprovedFile file = run.Header.Files[index];
            FileRunEntry entry = run.Entries[index];
            if (undo ? entry.Forward.Phase != TransferPhase.Finished || entry.Undone || entry.BackupReleased || entry.ReleasingBackup
                : entry.Forward.Phase == TransferPhase.Finished)
                continue;

            try
            {
                entry.Error = null;
                progress?.Report(new FileRunProgress(position + 1, count, file.Source, undo ? "Rückgängig" : "Datei wird verarbeitet"));
                if (undo)
                    Undo(run, index, file, entry);
                else
                    Transfer(run, index, file.Source, file.Target, file.Input, file.OriginalTarget,
                        file.TargetVolume, run.Header.Move, file.Direct, entry.Forward, "vorwärts");
            }
            catch (Exception error) when (Expected(error))
            {
                entry.Error = error.Message;
                journal.SaveEntry(run, index);
                return new FileRunOutcome(false, $"Pausiert · {error.Message}");
            }
        }

        return new FileRunOutcome(true, undo ? "Rückgängig für alle verfügbaren Einträge abgeschlossen." : "Dateivorgänge abgeschlossen.");
    }

    private void Transfer(FileRun run, int index, string source, string target, WindowsFileStamp input,
        WindowsFileStamp? originalTarget, ulong targetVolume, bool move, bool direct, FileTransfer transfer, string role)
    {
        FileRunEntry entry = run.Entries[index];
        if (transfer.Phase == TransferPhase.Ready)
        {
            using (SafeFileHandle handle = OpenFile(source, delete: move))
                Require(handle, source, input);
            VerifyOriginalTarget(target, originalTarget);
            if (CheckDestination(target).Volume != targetVolume)
                throw new IOException($"Ziellaufwerk hat sich geändert: {target}");
            SetPhase(run, index, transfer, direct ? TransferPhase.Staged : TransferPhase.Staging);
        }

        if (transfer.Phase == TransferPhase.Staging)
        {
            EnsureDestination(target, targetVolume);
            using SafeFileHandle handle = OpenFile(source);
            Require(handle, source, input);
            if (transfer.Stage is null)
            {
                transfer.Stage = run.Artifact(target, index, $"{role}-{Guid.NewGuid():N}.tmp");
                journal.SaveEntry(run, index);
            }

            string stage = transfer.Stage;
            if (Exists(stage) && !SameContent(source, stage))
            {
                transfer.InterruptedCopies.Add(stage);
                stage = transfer.Stage = run.Artifact(target, index, $"{role}-{Guid.NewGuid():N}.tmp");
                journal.SaveEntry(run, index);
            }

            if (!Exists(stage))
            {
                var volume = CheckDestination(target);
                if (CopySpace(source) > volume.Free)
                    throw new IOException($"Nicht genug freier Speicher für die Zielkopie: {target}");
                Copy(source, stage);
            }
            FlushCopy(stage);
            using (SafeFileHandle checkStage = OpenFile(stage, delete: true))
            {
                if (!SameContent(source, stage))
                    throw new IOException($"Die temporäre Kopie stimmt nicht mit der Quelle überein. Quelle bleibt erhalten: {source}");
                RestoreCreationTime(checkStage, stage, input.Created);
            }
            using (SafeFileHandle checkStage = OpenFile(stage))
                transfer.StagedFile = ReadStamp(checkStage, stage);
            Require(handle, source, input);
            SetPhase(run, index, transfer, TransferPhase.Staged);
        }

        if (transfer.Phase == TransferPhase.Staged)
        {
            using (SafeFileHandle current = OpenFile(source, delete: move))
                Require(current, source, input);
            VerifyOriginalTarget(target, originalTarget);
            SetPhase(run, index, transfer, originalTarget is null ? TransferPhase.BackedUp : TransferPhase.BackingUp);
        }

        if (transfer.Phase == TransferPhase.BackingUp)
        {
            string backup = run.BackupPath(index);
            if (!Exists(backup))
            {
                using SafeFileHandle oldTarget = OpenFile(target, delete: true);
                Require(oldTarget, target, originalTarget!);
                Rename(oldTarget, backup);
            }
            entry.Backup = ConfirmRenamed(backup, originalTarget!);
            entry.BackupBytes = ContentBytes(backup);
            SetPhase(run, index, transfer, TransferPhase.BackedUp);
        }

        if (transfer.Phase == TransferPhase.BackedUp)
        {
            using SafeFileHandle current = OpenFile(source, delete: move);
            Require(current, source, input);
            if (Exists(target))
                throw new IOException($"Ziel ist inzwischen belegt: {target}");
            SetPhase(run, index, transfer, TransferPhase.Publishing);
        }

        if (transfer.Phase == TransferPhase.Publishing)
        {
            string payload = direct ? source : transfer.Stage!;
            WindowsFileStamp expected = direct ? input : transfer.StagedFile!;
            if (!Exists(target))
            {
                EnsureDestination(target, targetVolume);
                using SafeFileHandle handle = OpenFile(payload, delete: true);
                Require(handle, payload, expected);
                Rename(handle, target);
            }

            if (Exists(payload))
                throw new IOException($"Ziel und Vorgängerpfad sind gleichzeitig belegt. Vorgang wird angehalten: {target}");
            transfer.Output = ConfirmRenamed(target, expected);
            SetPhase(run, index, transfer, TransferPhase.Published);
        }

        if (transfer.Phase == TransferPhase.Published)
        {
            if (move && !direct)
            {
                RemoveVerifiedSource(run, index, source, target, input, transfer);
            }
            else
                SetPhase(run, index, transfer, TransferPhase.Finished);
        }

        if (transfer.Phase == TransferPhase.Removing)
        {
            RequireVolume(source, input.Identity.Volume);
            if (Exists(source))
                RemoveVerifiedSource(run, index, source, target, input, transfer);
            using SafeFileHandle copied = OpenFile(target);
            Require(copied, target, transfer.Output!);
            if (Exists(source))
                throw new IOException($"Verifizierte Zielkopie ist vorhanden. Quelle konnte nicht entfernt werden: {source}");
            SetPhase(run, index, transfer, TransferPhase.Finished);
        }
    }

    private void RemoveVerifiedSource(FileRun run, int index, string source, string target,
        WindowsFileStamp input, FileTransfer transfer)
    {
        using SafeFileHandle original = OpenFile(source, delete: true);
        Require(original, source, input);
        using SafeFileHandle copied = OpenFile(target);
        Require(copied, target, transfer.Output!);
        bool verified = SameContent(source, target, () =>
        {
            Require(original, source, input);
            Require(copied, target, transfer.Output!);
            SetPhase(run, index, transfer, TransferPhase.Removing);
            Delete(original, source);
        });
        if (!verified)
            throw new IOException($"Endgültiges Ziel stimmt nicht mit der Quelle überein: {target}");
    }

    private void Undo(FileRun run, int index, ApprovedFile file, FileRunEntry entry)
    {
        string backup = run.BackupPath(index);
        if (entry.Backup is not null && !entry.RestoringBackup)
        {
            using SafeFileHandle handle = OpenFile(backup);
            Require(handle, backup, entry.Backup);
        }

        if (run.Header.Move && entry.Reverse.Phase != TransferPhase.Finished)
        {
            if (entry.Reverse.Phase == TransferPhase.Ready && Exists(file.Source))
                throw new IOException($"Ursprünglicher Quellpfad ist inzwischen belegt: {file.Source}");
            Transfer(run, index, file.Target, file.Source, entry.Forward.Output!, null,
                file.Input.Identity.Volume, move: true, file.Direct, entry.Reverse, "rückwärts");
        }
        if (!run.Header.Move && !entry.CopyRemoved)
        {
            RequireVolume(file.Target, file.TargetVolume);
            if (Exists(file.Target))
            {
                using SafeFileHandle copy = OpenFile(file.Target, delete: true);
                Require(copy, file.Target, entry.Forward.Output!);
                entry.RemovingCopy = true;
                journal.SaveEntry(run, index);
                Delete(copy, file.Target);
            }
            else if (!entry.RemovingCopy)
                throw new IOException($"Die Zieldatei wurde seit der Ausführung entfernt: {file.Target}");

            if (Exists(file.Target))
                throw new IOException($"Kopie konnte nicht entfernt werden: {file.Target}");
            entry.CopyRemoved = true;
            entry.RemovingCopy = false;
            journal.SaveEntry(run, index);
        }

        if (entry.Backup is not null)
        {
            RequireVolume(file.Target, file.TargetVolume);
            if (!entry.RestoringBackup)
            {
                if (Exists(file.Target))
                    throw new IOException($"Zielpfad für die Sicherung ist inzwischen belegt: {file.Target}");
                entry.RestoringBackup = true;
                journal.SaveEntry(run, index);
            }
            if (Exists(backup))
            {
                using SafeFileHandle handle = OpenFile(backup, delete: true);
                Require(handle, backup, entry.Backup);
                Rename(handle, file.Target);
            }
            ConfirmRenamed(file.Target, entry.Backup);
        }
        entry.Undone = true;
        entry.RestoringBackup = false;
        journal.SaveEntry(run, index);
    }

    public BackupRelease[] BackupsForRelease(FileRun run)
    {
        if (!run.CanRelease)
            return [];
        return run.Entries.Select((entry, index) => (entry, index))
            .Where(item => item.entry.Backup is not null && !item.entry.BackupReleased && !item.entry.Undone
                && !item.entry.RestoringBackup && item.entry.Reverse.Phase == TransferPhase.Ready && !item.entry.RemovingCopy)
            .Select(item => new BackupRelease(item.index, run.BackupPath(item.index), item.entry.BackupBytes ?? item.entry.Backup!.Bytes)).ToArray();
    }

    public FileRunOutcome ReleaseBackups(FileRun run, BackupRelease[] approved)
    {
        foreach (BackupRelease backup in approved)
        {
            FileRunEntry entry = run.Entries[backup.Index];
            try
            {
                RequireVolume(backup.Path, entry.Backup!.Identity.Volume);
                if (Exists(backup.Path))
                {
                    using SafeFileHandle handle = OpenFile(backup.Path, delete: true);
                    Require(handle, backup.Path, entry.Backup!);
                    entry.ReleasingBackup = true;
                    journal.SaveEntry(run, backup.Index);
                    Delete(handle, backup.Path);
                }
                else if (!entry.ReleasingBackup)
                    throw new IOException($"Sicherung wurde außerhalb des Tools entfernt: {backup.Path}");
                if (Exists(backup.Path))
                    throw new IOException($"Sicherung konnte nicht entfernt werden: {backup.Path}");
                entry.BackupReleased = true;
                entry.ReleasingBackup = false;
                entry.Error = null;
                journal.SaveEntry(run, backup.Index);
            }
            catch (Exception error) when (Expected(error))
            {
                entry.Error = error.Message;
                journal.SaveEntry(run, backup.Index);
                return new FileRunOutcome(false, error.Message);
            }
        }
        return new FileRunOutcome(true, $"{approved.Length:N0} Sicherungen freigegeben.");
    }

    public static string Inspect(FileRun run, int index)
    {
        FileRunEntry entry = run.Entries[index];
        ApprovedFile file = run.Header.Files[index];
        if (entry.Forward.Phase is TransferPhase.Ready or TransferPhase.Finished
            && (entry.Reverse.Phase == TransferPhase.Ready || entry.Undone)
            && !entry.RemovingCopy && !entry.RestoringBackup && !entry.ReleasingBackup
            && (!entry.CopyRemoved || entry.Undone) && entry.Forward.InterruptedCopies.Count == 0 && entry.Reverse.InterruptedCopies.Count == 0)
            return entry.Error ?? (entry.Backup is not null && !entry.BackupReleased && !entry.Undone
                ? $"Sicherung: {run.BackupPath(index)}" : "");

        var paths = new List<string>
        {
            $"Quelle {PathState(file.Source)}", $"Ziel {PathState(file.Target)}"
        };
        if (entry.Backup is not null || entry.Forward.Phase == TransferPhase.BackingUp)
            paths.Add($"Sicherung {PathState(run.BackupPath(index))} ({run.BackupPath(index)})");
        foreach (string stage in new[] { entry.Forward.Stage, entry.Reverse.Stage }.OfType<string>())
            paths.Add($"Temporäre Kopie {PathState(stage)} ({stage})");
        foreach (string orphan in entry.Forward.InterruptedCopies.Concat(entry.Reverse.InterruptedCopies))
            paths.Add($"Unterbrochene Kopie {PathState(orphan)} ({orphan})");
        if (entry.Error is not null)
            paths.Insert(0, entry.Error);
        return string.Join(" · ", paths);
    }

    private static string PathState(string path)
    {
        try { return Exists(path) ? "vorhanden" : "nicht vorhanden"; }
        catch (Exception error) when (Expected(error)) { return "nicht prüfbar"; }
    }

    private void SetPhase(FileRun run, int index, FileTransfer transfer, TransferPhase phase)
    {
        TransferPhase previous = transfer.Phase;
        transfer.Phase = phase;
        try { journal.SaveEntry(run, index); }
        catch
        {
            transfer.Phase = previous;
            throw;
        }
    }

    private static void EnsureDestination(string target, ulong volume)
    {
        if (CheckDestination(target).Volume != volume)
            throw new IOException($"Ziellaufwerk hat sich geändert: {target}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (CheckDestination(target).Volume != volume)
            throw new IOException($"Der erstellte Zielordner liegt auf einem anderen Laufwerk: {target}");
    }

    private static void RequireVolume(string path, ulong volume)
    {
        if (CheckVolume(path) != volume)
            throw new IOException($"Das Laufwerk hat sich geändert: {path}");
    }

    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or OverflowException;
}

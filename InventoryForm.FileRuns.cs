using FilesizeChecker.Inventory;

namespace FilesizeChecker;

internal sealed partial class InventoryForm
{
    private readonly TabPage runTab = new("Sortierläufe");
    private readonly ComboBox chooseRun = CreateDropdown(313);
    private readonly Button resumeRun = CreateButton("Fortsetzen");
    private readonly Button undoRun = CreateButton("Rückgängig …");
    private readonly Button pauseRun = CreateButton("Nach Datei pausieren");
    private readonly Button releaseBackups = CreateButton("Sicherungen freigeben …");
    private readonly DataGridView runGrid = CreateGrid();
    private readonly Label runStatus = CreateLabel("Hier erscheinen ausgeführte Sortierläufe.", Muted);
    private readonly List<FileRun> fileRuns = [];
    private CancellationTokenSource? fileRunPause;
    private bool fileRunBusy;

    private FileRunJournal Journal => new(store.DataFolder);
    private FileRun? SelectedRun => (chooseRun.SelectedItem as RunItem)?.Run;

    private sealed record RunItem(FileRun Run)
    {
        public override string ToString() => $"{Run.Header.CreatedUtc.ToLocalTime():g} · "
            + $"{(Run.Header.Move ? "Verschieben" : "Kopieren")} · {Run.Header.Files.Length:N0} Dateien";
    }

    private TabPage BuildFileRunTab()
    {
        runTab.BackColor = Color.White;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10, 8, 10, 8)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(Flow(chooseRun, resumeRun, undoRun, pauseRun, releaseBackups), 0, 0);
        runStatus.AutoSize = false;
        runStatus.AutoEllipsis = true;
        runStatus.Dock = DockStyle.Fill;
        layout.Controls.Add(runStatus, 0, 1);

        AddColumn(runGrid, "Status", 174);
        AddColumn(runGrid, "Quelldatei", 164, fill: true);
        AddColumn(runGrid, "Zielname", 164, fill: true);
        AddColumn(runGrid, "Zielordner", 240, fill: true);
        AddColumn(runGrid, "Hinweis", 230, fill: true);
        layout.Controls.Add(runGrid, 0, 2);
        runTab.Controls.Add(layout);

        chooseRun.SelectedIndexChanged += (_, _) => RenderFileRun();
        resumeRun.Click += async (_, _) =>
        {
            if (SelectedRun is { } selected)
                await RunFilesAsync(selected, undo: false);
        };
        undoRun.Click += async (_, _) => await ConfirmUndoAsync();
        pauseRun.Click += (_, _) =>
        {
            fileRunPause?.Cancel();
            pauseRun.Enabled = false;
            runStatus.Text = "Der Lauf pausiert nach der aktuellen Datei …";
        };
        releaseBackups.Click += async (_, _) => await ConfirmBackupReleaseAsync();
        return runTab;
    }

    private void LoadFileRuns()
    {
        var errors = new List<string>();
        try
        {
            fileRuns.AddRange(Journal.Load(errors));
            RefreshRunChoices();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            errors.Add(error.Message);
        }
        if (errors.Count > 0)
            runStatus.Text = $"Protokoll konnte teilweise nicht geladen werden · {errors[0]}";
    }

    private void RefreshRunChoices(Guid? selected = null)
    {
        chooseRun.BeginUpdate();
        chooseRun.Items.Clear();
        foreach (FileRun run in fileRuns)
            chooseRun.Items.Add(new RunItem(run));
        if (fileRuns.Count > 0)
        {
            int index = selected is null ? 0 : fileRuns.FindIndex(run => run.Header.Id == selected);
            chooseRun.SelectedIndex = Math.Max(0, index);
        }
        chooseRun.EndUpdate();
        RenderFileRun();
    }

    private void RenderFileRun(string? outcome = null)
    {
        if (fileRunBusy)
            return;
        runGrid.Rows.Clear();
        if (SelectedRun is not { } run)
        {
            runStatus.Text = "Hier erscheinen ausgeführte Sortierläufe.";
            UpdateFileRunActions();
            return;
        }

        for (int index = 0; index < run.Entries.Length; index++)
        {
            FileRunEntry entry = run.Entries[index];
            ApprovedFile file = run.Header.Files[index];
            string status = EntryStatus(entry, run.Header.Direction);
            string detail = FileRunService.Inspect(run, index);
            int rowIndex = runGrid.Rows.Add(status, Path.GetFileName(file.Source), Path.GetFileName(file.Target),
                Path.GetDirectoryName(file.Target) ?? "", detail);
            DataGridViewRow row = runGrid.Rows[rowIndex];
            row.Cells[1].ToolTipText = file.Source;
            row.Cells[2].ToolTipText = file.Target;
            row.Cells[3].ToolTipText = file.Target;
            row.Cells[4].ToolTipText = detail;
            if (entry.Error is not null)
                row.Cells[0].Style.ForeColor = Color.Maroon;
        }
        foreach (string path in run.Header.Skipped)
        {
            int index = runGrid.Rows.Add("Übersprungen", Path.GetFileName(path), "", "", "");
            runGrid.Rows[index].Cells[1].ToolTipText = path;
        }
        BackupRelease[] backups = new FileRunService(Journal).BackupsForRelease(run);
        int remaining = run.Entries.Count(entry => entry.Forward.Phase != TransferPhase.Finished);
        string state = run.Header.Direction == RunDirection.Undo ? $"{remaining:N0} nicht ausgeführt" : $"{remaining:N0} ausstehend";
        string detailText = outcome ?? (run.HasOpenChanges ? "Unterbrochener Vorgang geprüft, Details in der Liste" : "");
        string detailSuffix = detailText.Length == 0 ? "" : $" · {detailText}";
        runStatus.Text = $"{state} · {backups.Length:N0} Sicherungen ({InventoryLabels.Size(backups.Sum(backup => backup.Bytes))}){detailSuffix}";
        UpdateFileRunActions();
    }

    private static string EntryStatus(FileRunEntry entry, RunDirection direction)
    {
        if (entry.Undone)
            return "Rückgängig gemacht";
        if (entry.BackupReleased || entry.ReleasingBackup)
            return "Rückgängig entfällt";
        if (entry.Reverse.Phase != TransferPhase.Ready || entry.RemovingCopy || entry.CopyRemoved || entry.RestoringBackup)
            return "Rücknahme teilweise ausgeführt";
        if (direction == RunDirection.Undo && entry.Forward.Phase != TransferPhase.Finished)
            return "Nicht ausgeführt";
        return entry.Forward.Phase switch
        {
            TransferPhase.Finished => "Abgeschlossen",
            TransferPhase.Ready when entry.Error is not null => "Blockiert",
            TransferPhase.Ready => "Ausstehend",
            _ => "Teilweise abgeschlossen"
        };
    }

    private void UpdateFileRunActions()
    {
        FileRun? selected = SelectedRun;
        bool idle = !fileRunBusy && scanning is null && duplicateRun is null && !buildingPlan;
        chooseRun.Enabled = !fileRunBusy && fileRuns.Count > 0;
        resumeRun.Enabled = idle && selected?.CanContinue == true;
        undoRun.Enabled = idle && selected?.CanUndo == true;
        undoRun.Text = selected?.Header.Direction == RunDirection.Undo ? "Rücknahme fortsetzen …" : "Rückgängig …";
        pauseRun.Enabled = fileRunPause?.IsCancellationRequested == false;
        releaseBackups.Enabled = idle && selected?.CanRelease == true;
    }

    private async Task ApprovePlanAsync()
    {
        if (fileRunBusy || activePlan is not { } request || planRows.Length == 0)
            return;
        PlanRow[] approvedRows = planRows;
        FileRunPreflight preflight;
        fileRunBusy = true;
        planStatus.Text = "Dateien, Ziele, Rechte und freier Speicher werden geprüft …";
        UpdateActions();
        try
        {
            preflight = await Task.Run(() => FileRunService.Preflight(approvedRows, request.Move));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            planStatus.Text = error.Message;
            return;
        }
        finally
        {
            fileRunBusy = false;
            UpdateActions();
        }

        if (activePlan != request || planRows != approvedRows)
            return;
        if (preflight.Issues.Length > 0)
        {
            planStatus.Text = $"Vorabprüfung · {preflight.Issues.Length:N0} blockiert. Korrigiere die Einträge oder schließe sie aus.";
            ReviewFileRun("Vorabprüfung", planStatus.Text, ["Pfad", "Grund"],
                preflight.Issues.Select(issue => new[] { issue.Path, issue.Reason }), null);
            return;
        }
        if (preflight.Entries.Length == 0)
            return;

        string action = request.Move ? "Verschieben" : "Kopieren";
        int replaced = preflight.Entries.Count(file => file.OriginalTarget is not null);
        string summary = $"{preflight.Entries.Length:N0} Dateien · {replaced:N0} Ziele werden ersetzt und gesichert · "
            + $"{preflight.Skipped.Length:N0} Einträge übersprungen";
        planStatus.Text = "Vorabprüfung abgeschlossen. Bitte bestätige die Dateivorgänge.";
        if (!ReviewFileRun($"{action} freigeben", summary, ["Quelldatei", "Quellordner", "Zielname", "Zielordner", "Aktion"],
            preflight.Entries.Select(file => new[]
            {
                Path.GetFileName(file.Source), Path.GetDirectoryName(file.Source) ?? "", Path.GetFileName(file.Target),
                Path.GetDirectoryName(file.Target) ?? "", file.OriginalTarget is null ? action : $"{action} und ersetzen"
            }), action))
            return;

        try
        {
            FileRun run = Journal.Create(request.Move, preflight.Entries, preflight.Skipped);
            fileRuns.Insert(0, run);
            RefreshRunChoices(run.Header.Id);
            ((TabControl)runTab.Parent!).SelectedTab = runTab;
            await RunFilesAsync(run, undo: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            planStatus.Text = $"Lauf konnte nicht protokolliert werden · {error.Message}";
        }
    }

    private async Task ConfirmUndoAsync()
    {
        if (SelectedRun is not { } run || !run.CanUndo || fileRunBusy)
            return;
        var entries = run.Header.Files.Where((_, index) =>
        {
            FileRunEntry entry = run.Entries[index];
            return entry.Forward.Phase == TransferPhase.Finished && !entry.Undone && !entry.BackupReleased && !entry.ReleasingBackup;
        }).ToArray();
        string action = run.Header.Move ? "An den ursprünglichen Ort zurückverschieben" : "Erstellte Kopie entfernen";
        if (ReviewFileRun("Rückgängig", $"{entries.Length:N0} abgeschlossene Dateivorgänge werden zurückgenommen. Vorhandene Sicherungen werden wiederhergestellt.",
            ["Quelle des Sortierlaufs", "Ziel des Sortierlaufs", "Rücknahme"],
            entries.Select(file => new[] { file.Source, file.Target, action }), "Rückgängig"))
            await RunFilesAsync(run, undo: true);
    }

    private async Task RunFilesAsync(FileRun run, bool undo)
    {
        if (fileRunBusy || scanning is not null || duplicateRun is not null)
            return;
        using var pause = new CancellationTokenSource();
        fileRunPause = pause;
        fileRunBusy = true;
        UpdateActions();
        var progress = new Progress<FileRunProgress>(status =>
        {
            if (fileRunPause == pause && !pause.IsCancellationRequested)
                runStatus.Text = $"{status.Phase} · {status.Index:N0} / {status.Total:N0} · {status.Path}";
        });
        string result;
        try
        {
            var service = new FileRunService(Journal);
            FileRunOutcome outcome = await Task.Run(() => service.Execute(run, undo, progress, pause.Token));
            result = outcome.Status;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            result = $"Lauf angehalten · {error.Message}";
        }
        finally
        {
            fileRunPause = null;
            fileRunBusy = false;
            UpdateActions();
        }
        ClearSortPlan();
        RefreshDuplicateSource();
        RenderFileRun(result);
        UpdateActions();
        scanStatus.Text = "Dateibestand kann sich geändert haben. Scanne das Inventar bei Bedarf erneut.";
    }

    private async Task ConfirmBackupReleaseAsync()
    {
        if (SelectedRun is not { } run || fileRunBusy)
            return;
        var service = new FileRunService(Journal);
        BackupRelease[] backups = service.BackupsForRelease(run);
        if (backups.Length == 0 || !ReviewFileRun("Sicherungen freigeben",
            $"Lauf {run.Header.CreatedUtc.ToLocalTime():g} · {InventoryLabels.Size(backups.Sum(backup => backup.Bytes))}. Für diese Einträge entfällt Rückgängig dauerhaft.",
            ["Sicherung", "Ersetzte Zieldatei"], backups.Select(backup => new[] { backup.Path, run.Header.Files[backup.Index].Target }),
            "Sicherungen löschen"))
            return;

        fileRunBusy = true;
        runStatus.Text = "Bestätigte Sicherungen werden freigegeben …";
        UpdateActions();
        string result;
        try
        {
            result = (await Task.Run(() => service.ReleaseBackups(run, backups))).Status;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            result = $"Freigabe angehalten · {error.Message}";
        }
        finally
        {
            fileRunBusy = false;
            UpdateActions();
        }
        RenderFileRun(result);
    }

    private HashSet<string> CurrentRunArtifacts()
    {
        var paths = new HashSet<string>(InventoryPaths.Comparer);
        foreach (FileRun run in fileRuns)
        {
            for (int index = 0; index < run.Entries.Length; index++)
            {
                FileRunEntry entry = run.Entries[index];
                if ((entry.Backup is not null || entry.Forward.Phase == TransferPhase.BackingUp)
                    && !entry.BackupReleased && !entry.Undone)
                    paths.Add(run.BackupPath(index));
                foreach (FileTransfer transfer in new[] { entry.Forward, entry.Reverse })
                {
                    if (transfer.Stage is not null && transfer.Phase <= TransferPhase.Publishing)
                        paths.Add(transfer.Stage);
                    paths.UnionWith(transfer.InterruptedCopies);
                }
            }
        }
        return paths;
    }

    private bool ReviewFileRun(string title, string summary, string[] columns, IEnumerable<string[]> rows, string? confirmation)
    {
        using var dialog = new Form
        {
            Text = title, ClientSize = new Size(915, 490), StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, Font = Font, Padding = new Padding(12)
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(CreateLabel(summary, Ink), 0, 0);
        DataGridView grid = CreateGrid();
        foreach (string column in columns)
            AddColumn(grid, column, 240, fill: true);
        foreach (string[] row in rows)
        {
            int index = grid.Rows.Add(row.Cast<object>().ToArray());
            for (int column = 0; column < row.Length; column++)
                grid.Rows[index].Cells[column].ToolTipText = row[column];
        }
        layout.Controls.Add(grid, 0, 1);
        var close = CreateButton(confirmation is null ? "Schließen" : "Abbrechen");
        close.DialogResult = DialogResult.Cancel;
        dialog.CancelButton = close;
        if (confirmation is null)
            layout.Controls.Add(Flow(close), 0, 2);
        else
        {
            var confirm = CreateButton(confirmation, primary: true);
            confirm.DialogResult = DialogResult.OK;
            layout.Controls.Add(Flow(close, confirm), 0, 2);
        }
        dialog.Controls.Add(layout);
        return dialog.ShowDialog(this) == DialogResult.OK;
    }
}

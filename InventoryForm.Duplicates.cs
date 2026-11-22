using FilesizeChecker.Inventory;

namespace FilesizeChecker;

internal sealed partial class InventoryForm
{
    private readonly Button startDuplicates = CreateButton("Duplikatsuche starten", primary: true);
    private readonly Button pauseDuplicates = CreateButton("Pausieren");
    private readonly Button cancelDuplicates = CreateButton("Abbrechen");
    private readonly CheckBox rebuildHashes = new() { Text = "Alle Prüfwerte neu berechnen", AutoSize = true, Margin = new Padding(10, 6, 8, 3) };
    private readonly Label duplicateStatus = CreateLabel("Für die Suche zuerst einen Ordner scannen.", Muted);
    private readonly Label duplicatePath = CreateLabel("", Muted);
    private readonly ProgressBar duplicateProgress = new() { Width = 170, Height = 19, Margin = new Padding(3, 6, 8, 3) };
    private readonly FileDragGrid duplicateGrid = CreateGrid();
    private readonly DataGridView duplicateIssues = CreateGrid();
    private readonly TabPage duplicateIssueTab = new("Prüfhinweise");
    private FileEntry[] duplicateCandidates = [];
    private CancellationTokenSource? duplicateRun;
    private bool duplicateAbortRequested;

    private TabPage BuildDuplicateTab()
    {
        var tab = new TabPage("Duplikate") { BackColor = Color.White };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10, 8, 10, 8)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(Flow(startDuplicates, pauseDuplicates, cancelDuplicates, rebuildHashes, duplicateProgress), 0, 0);
        duplicateStatus.AutoSize = false;
        duplicateStatus.AutoEllipsis = true;
        duplicateStatus.Margin = new Padding(2, 0, 8, 0);
        duplicateStatus.TextAlign = ContentAlignment.MiddleLeft;
        duplicateStatus.Dock = DockStyle.Fill;
        layout.Controls.Add(duplicateStatus, 0, 1);
        duplicatePath.AutoSize = false;
        duplicatePath.AutoEllipsis = true;
        duplicatePath.Margin = new Padding(2, 0, 8, 0);
        duplicatePath.TextAlign = ContentAlignment.MiddleLeft;
        duplicatePath.Dock = DockStyle.Fill;
        layout.Controls.Add(duplicatePath, 0, 2);

        AddColumn(duplicateGrid, "Gruppe ziehen", 113);
        AddColumn(duplicateGrid, "Dateiname", 220, fill: true);
        AddColumn(duplicateGrid, "Ordner", 350, fill: true);
        AddColumn(duplicateGrid, "Größe", 110);
        AddColumn(duplicateGrid, "Freigebbar je Gruppe", 157);
        AddColumn(duplicateGrid, "Hinweis", 155);
        AddColumn(duplicateIssues, "Dateipfad", 340, fill: true);
        AddColumn(duplicateIssues, "Hinweis", 350, fill: true);
        var resultTab = new TabPage("Byteidentische Dateien");
        resultTab.Controls.Add(duplicateGrid);
        duplicateIssueTab.Controls.Add(duplicateIssues);
        var resultTabs = new TabControl { Dock = DockStyle.Fill };
        resultTabs.TabPages.Add(resultTab);
        resultTabs.TabPages.Add(duplicateIssueTab);
        layout.Controls.Add(resultTabs, 0, 3);
        tab.Controls.Add(layout);

        startDuplicates.Click += async (_, _) => await SearchDuplicatesAsync();
        pauseDuplicates.Click += (_, _) => RequestDuplicateStop(abort: false);
        cancelDuplicates.Click += (_, _) => RequestDuplicateStop(abort: true);
        return tab;
    }

    private void RefreshDuplicateSource()
    {
        HashSet<string> artifacts = CurrentRunArtifacts();
        duplicateCandidates = DuplicateSearch.FindCandidates(allFiles
            .Where(row => IsPresent(row) && !artifacts.Contains(row.File.Path)).Select(row => row.File));
        duplicateGrid.Rows.Clear();
        duplicateIssues.Rows.Clear();
        duplicateIssueTab.Text = "Prüfhinweise";
        duplicateProgress.Value = 0;
        duplicatePath.Text = "";
        startDuplicates.Text = "Duplikatsuche starten";
        duplicateStatus.Text = snapshot is null ? "Für die Suche zuerst einen Ordner scannen."
            : $"{duplicateCandidates.Length:N0} Dateien mit mindestens einem gleich großen Kandidaten.";
    }

    private void UpdateDuplicateActions()
    {
        bool idle = scanning is null && duplicateRun is null && nameRun is null && imageRun is null && videoRun is null && !fileRunBusy;
        startDuplicates.Enabled = idle && duplicateCandidates.Length > 0;
        rebuildHashes.Enabled = idle;
        pauseDuplicates.Enabled = duplicateRun?.IsCancellationRequested == false;
        cancelDuplicates.Enabled = pauseDuplicates.Enabled;
    }

    private void RequestDuplicateStop(bool abort)
    {
        duplicateAbortRequested = abort;
        duplicateRun?.Cancel();
        UpdateDuplicateActions();
        duplicateStatus.Text = abort ? "Abbruch angefordert · Der laufende Lesezugriff wird beendet."
            : "Pause angefordert · Der laufende Lesezugriff wird beendet.";
    }

    private async Task SearchDuplicatesAsync()
    {
        if (scanning is not null || duplicateRun is not null || nameRun is not null || imageRun is not null || videoRun is not null || fileRunBusy || duplicateCandidates.Length == 0)
            return;

        using var cancellation = new CancellationTokenSource();
        duplicateRun = cancellation;
        duplicateAbortRequested = false;
        bool force = rebuildHashes.Checked;
        var candidates = duplicateCandidates;
        duplicateStatus.Text = "Duplikatsuche läuft …";
        duplicatePath.Text = "";
        duplicateProgress.Value = 0;
        duplicateGrid.Rows.Clear();
        duplicateIssues.Rows.Clear();
        duplicateIssueTab.Text = "Prüfhinweise";
        UpdateActions();
        var progress = new Progress<DuplicateProgress>(update =>
        {
            if (duplicateRun != cancellation || cancellation.IsCancellationRequested)
                return;
            double fraction = update.Length == 0 ? 0 : (double)update.Position / update.Length;
            duplicateProgress.Value = update.Total == 0 ? 0 : Math.Min(100, (int)((update.Processed + fraction) * 100 / update.Total));
            duplicateStatus.Text = $"{update.Phase} · {update.Processed:N0} / {update.Total:N0} · "
                + $"{update.BytesRead / (double)(1L << 30):N2} GiB gelesen · Ø {update.BytesPerSecond / (1 << 20):N1} MiB/s";
            duplicatePath.Text = update.Path;
        });

        try
        {
            var service = new DuplicateSearch(Path.Combine(store.DataFolder, "duplicate-hashes.json"));
            DuplicateResult result = await Task.Run(() => service.Run(candidates, force, progress, cancellation.Token));
            duplicatePath.Text = "";
            if (duplicateAbortRequested)
            {
                duplicateStatus.Text = $"Abgebrochen · {result.BytesRead / (double)(1L << 30):N2} GiB gelesen";
                duplicateProgress.Value = 0;
                startDuplicates.Text = "Duplikatsuche starten";
            }
            else
            {
                bool paused = result.Paused || cancellation.IsCancellationRequested;
                PresentDuplicateResult(result with { Paused = paused });
                if (paused)
                {
                    rebuildHashes.Checked = false;
                    startDuplicates.Text = "Suche fortsetzen";
                }
                else
                {
                    startDuplicates.Text = "Duplikatsuche starten";
                    duplicateProgress.Value = 100;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            duplicatePath.Text = "";
            startDuplicates.Text = "Duplikatsuche starten";
            duplicateStatus.Text = $"Die Suche wurde beendet · {error.Message}";
        }
        finally
        {
            duplicateRun = null;
            UpdateActions();
        }
    }

    private void PresentDuplicateResult(DuplicateResult result)
    {
        for (int index = 0; index < result.Groups.Count; index++)
        {
            DuplicateGroup group = result.Groups[index];
            for (int row = 0; row < group.Files.Count; row++)
            {
                DuplicateMember member = group.Files[row];
                string label = member.Stamp is null ? "Dateikennung unbekannt"
                    : member.Stamp.Links > 1 ? $"Hardlink ({member.Stamp.Links:N0} Pfade)" : "";
                int item = duplicateGrid.Rows.Add($"{CollectionGrip}  {index + 1:N0}", member.File.Name, member.File.Folder, InventoryLabels.Size(member.File.Bytes),
                    row == 0 ? InventoryLabels.Size(group.ReclaimableBytes) : "", label);
                duplicateGrid.Rows[item].Tag = new CollectedDuplicate(group, member, index + 1);
                duplicateGrid.Rows[item].Cells[0].ToolTipText = "Ziehe die Gruppennummer, um alle Dateien der Gruppe zu sammeln.";
                duplicateGrid.Rows[item].Cells[2].ToolTipText = member.File.Path;
            }
        }
        foreach (ScanIssue error in result.Issues)
            duplicateIssues.Rows.Add(error.Path, error.Reason);
        duplicateIssueTab.Text = $"Prüfhinweise ({result.Issues.Count:N0})";
        string state = result.Paused ? "Pausiert" : "Abgeschlossen";
        string count = result.Groups.Count == 1 ? "1 Gruppe" : $"{result.Groups.Count:N0} Gruppen";
        duplicateStatus.Text = $"{state} · {count} mit byteidentischen Dateien · "
            + $"{InventoryLabels.Size(result.Groups.Sum(group => group.ReclaimableBytes))} potenziell freigebbar · "
            + $"{result.BytesRead / (double)(1L << 30):N2} GiB gelesen";
        duplicatePath.Text = $"Wiederverwendet · SHA-256-Prüfwerte {result.Reused:N0} · Bytevergleiche {result.ReusedComparisons:N0}";
    }
}

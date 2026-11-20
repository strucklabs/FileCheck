using System.Text.Json;
using FilesizeChecker.Inventory;

namespace FilesizeChecker;

internal sealed partial class InventoryForm : Form
{
    private static readonly Color Ink = Color.FromArgb(24, 38, 50);
    private static readonly Color Muted = Color.FromArgb(75, 91, 105);
    private static readonly Color Surface = Color.FromArgb(243, 246, 249);
    private static readonly Color Accent = Color.FromArgb(20, 88, 113);

    private readonly InventoryStore store;
    private readonly ListBox roots = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button addRoot = CreateButton("Ordner hinzufügen");
    private readonly Button removeRoot = CreateButton("Entfernen");
    private readonly Button scan = CreateButton("Scan starten", primary: true);
    private readonly Button cancel = CreateButton("Abbrechen");
    private readonly Button export = CreateButton("CSV exportieren");
    private readonly CheckBox[] statusFilters = Enum.GetValues<FileStatus>()
        .Select(CreateStatusFilter).ToArray();
    private readonly TextBox nameFilter = new() { PlaceholderText = "Name oder Pfad suchen", Width = 208 };
    private readonly ComboBox folderFilter = CreateDropdown(208);
    private readonly ComboBox extensionFilter = CreateDropdown(126);
    private readonly ComboBox breakdownFilter = CreateDropdown(155);
    private readonly NumericUpDown minSize = CreateSizeFilter();
    private readonly NumericUpDown maxSize = CreateSizeFilter();
    private readonly DateTimePicker fromDate = CreateDateFilter();
    private readonly DateTimePicker untilDate = CreateDateFilter();
    private readonly DataGridView files = CreateGrid(virtualMode: true);
    private readonly DataGridView byType = CreateGrid();
    private readonly DataGridView byFolder = CreateGrid();
    private readonly DataGridView issues = CreateGrid();
    private readonly Label scanStatus = CreateLabel("Ordner auswählen, um das Inventar zu erstellen.", Muted);
    private readonly Label storageSummary = CreateLabel("0 Dateien · 0 B", Ink);
    private readonly Label fileCount = CreateLabel("0 Treffer", Muted);
    private readonly TabPage issueTab = new("Scan-Hinweise");

    private InventorySnapshot? snapshot;
    private List<FileResult> allFiles = [];
    private List<FileResult> visibleFiles = [];
    private CancellationTokenSource? scanning;
    private bool refreshingFilters;
    private bool storeReadable = true;
    private int sortColumn = 1;
    private bool ascending = true;

    public InventoryForm(InventoryStore store)
    {
        this.store = store;
        Text = "Filesize-Checker";
        Font = new Font("Segoe UI", 9.5f);
        ForeColor = Ink;
        BackColor = Surface;
        ClientSize = new Size(1260, 790);
        MinimumSize = new Size(1040, 680);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;

        ConfigureGrids();
        BuildLayout();
        WireEvents();
        RefreshSnapshot();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1,
            Padding = new Padding(20, 12, 20, 9), BackColor = Color.White
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = CreateLabel("Filesize-Checker", Ink);
        title.Font = new Font(Font.FontFamily, 20f, FontStyle.Bold);
        title.Margin = new Padding(0);
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(Flow(scan, cancel, export), 1, 0);
        layout.Controls.Add(header, 0, 0);

        var sourcePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2,
            Padding = new Padding(20, 6, 20, 6), Margin = new Padding(0)
        };
        sourcePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sourcePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourcePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sourcePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sourcePanel.Controls.Add(CreateLabel("Scan-Ordner", Ink), 0, 0);
        sourcePanel.Controls.Add(roots, 0, 1);
        var folderButtons = Flow(addRoot, removeRoot);
        folderButtons.Margin = new Padding(8, 0, 0, 0);
        sourcePanel.Controls.Add(folderButtons, 1, 1);
        layout.Controls.Add(sourcePanel, 0, 1);

        var tabControl = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(20, 5, 20, 0) };
        var inventoryTab = new TabPage("Inventar") { BackColor = Color.White };
        var inventoryLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10, 7, 10, 4)
        };
        inventoryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inventoryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inventoryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inventoryLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        inventoryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inventoryLayout.Controls.Add(Flow(statusFilters), 0, 0);
        inventoryLayout.Controls.Add(Flow(nameFilter, folderFilter, extensionFilter,
            CreateLabel("Min. MiB", Muted), minSize, CreateLabel("Max. MiB (0 = alle)", Muted), maxSize), 0, 1);
        var resetFilters = CreateButton("Filter zurücksetzen");
        resetFilters.Click += (_, _) => ResetFilters();
        inventoryLayout.Controls.Add(Flow(CreateLabel("Geändert ab", Muted), fromDate,
            CreateLabel("bis", Muted), untilDate, resetFilters), 0, 2);
        inventoryLayout.Controls.Add(files, 0, 3);
        inventoryLayout.Controls.Add(fileCount, 0, 4);
        inventoryTab.Controls.Add(inventoryLayout);
        tabControl.TabPages.Add(inventoryTab);

        var storageTab = new TabPage("Speicheranalyse") { BackColor = Color.White };
        var storageLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(10, 6, 10, 10)
        };
        storageLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 39));
        storageLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 61));
        storageLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        storageLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        storageLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        storageSummary.Font = new Font(Font.FontFamily, 14f, FontStyle.Bold);
        storageLayout.Controls.Add(storageSummary, 0, 0);
        storageLayout.SetColumnSpan(storageSummary, 2);
        breakdownFilter.Items.AddRange(["Nach Dateityp", "Nach Größenklasse"]);
        breakdownFilter.SelectedIndex = 0;
        breakdownFilter.SelectedIndexChanged += (_, _) => RefreshStorageBreakdown(breakdownFilter.SelectedIndex == 1);
        var largestFiles = CreateButton("Größte Dateien anzeigen");
        largestFiles.Click += (_, _) =>
        {
            ResetFilters();
            statusFilters.Single(filter => (FileStatus)filter.Tag! == FileStatus.Missing).Checked = false;
            statusFilters.Single(filter => (FileStatus)filter.Tag! == FileStatus.Unavailable).Checked = false;
            SetSort(4, false);
            tabControl.SelectedTab = inventoryTab;
        };
        storageLayout.Controls.Add(Flow(breakdownFilter), 0, 1);
        storageLayout.Controls.Add(Flow(CreateLabel("Größte Ordner einschließlich Unterordnern", Muted), largestFiles), 1, 1);
        storageLayout.Controls.Add(byType, 0, 2);
        storageLayout.Controls.Add(byFolder, 1, 2);
        storageTab.Controls.Add(storageLayout);
        tabControl.TabPages.Add(storageTab);
        tabControl.TabPages.Add(BuildDuplicateTab());
        tabControl.TabPages.Add(BuildSortPlanTab());
        tabControl.TabPages.Add(BuildFileRunTab());

        issueTab.Padding = new Padding(8);
        issueTab.Controls.Add(issues);
        tabControl.TabPages.Add(issueTab);
        layout.Controls.Add(tabControl, 0, 2);

        scanStatus.AutoSize = false;
        scanStatus.AutoEllipsis = true;
        scanStatus.Dock = DockStyle.Fill;
        scanStatus.Height = 24;
        scanStatus.Margin = new Padding(20, 2, 20, 3);
        layout.Controls.Add(scanStatus, 0, 3);
    }

    private void ConfigureGrids()
    {
        AddColumn(files, "Status", 118);
        AddColumn(files, "Dateiname", 200, fill: true);
        AddColumn(files, "Ordner", 170, fill: true);
        AddColumn(files, "Typ", 106);
        AddColumn(files, "Größe", 105);
        AddColumn(files, "Geändert", 134);
        AddColumn(files, "Details", 110, fill: true);
        files.Columns[sortColumn].HeaderCell.SortGlyphDirection = SortOrder.Ascending;

        foreach (DataGridView grid in new[] { byType, byFolder })
        {
            AddColumn(grid, grid == byType ? "Gruppe" : "Ordner", 130, fill: true);
            AddColumn(grid, "Dateien", 70);
            AddColumn(grid, "Größe", 99);
        }

        AddColumn(issues, "Pfad", 290, fill: true);
        AddColumn(issues, "Hinweis", 290, fill: true);
    }

    private void WireEvents()
    {
        Shown += (_, _) =>
        {
            LoadFileRuns();
            LoadSnapshot();
            LoadSortRules();
        };
        FormClosing += (_, args) =>
        {
            if (scanning is null && duplicateRun is null && !fileRunBusy)
                return;
            scanning?.Cancel();
            duplicateRun?.Cancel();
            fileRunPause?.Cancel();
            args.Cancel = true;
            scanStatus.Text = "Die laufende Suche wird angehalten. Das Fenster kann danach geschlossen werden.";
        };
        addRoot.Click += (_, _) => AddRoot();
        removeRoot.Click += (_, _) =>
        {
            if (roots.SelectedIndex >= 0)
                roots.Items.RemoveAt(roots.SelectedIndex);
            UpdateActions();
        };
        roots.SelectedIndexChanged += (_, _) => UpdateActions();
        scan.Click += async (_, _) => await ScanAsync();
        cancel.Click += (_, _) =>
        {
            scanning?.Cancel();
            cancel.Enabled = false;
            scanStatus.Text = "Scan wird abgebrochen …";
        };
        export.Click += (_, _) => ExportCsv();

        foreach (CheckBox filter in statusFilters)
        {
            filter.CheckedChanged += (_, _) =>
            {
                UpdateStatusFilterAppearance(filter);
                ApplyFilters();
            };
        }
        nameFilter.TextChanged += (_, _) => ApplyFilters();
        folderFilter.SelectedIndexChanged += (_, _) => ApplyFilters();
        extensionFilter.SelectedIndexChanged += (_, _) => ApplyFilters();
        minSize.ValueChanged += (_, _) => ApplyFilters();
        maxSize.ValueChanged += (_, _) => ApplyFilters();
        fromDate.ValueChanged += (_, _) => ApplyFilters();
        untilDate.ValueChanged += (_, _) => ApplyFilters();

        files.CellValueNeeded += (_, args) =>
        {
            if (args.RowIndex >= visibleFiles.Count)
                return;
            FileResult result = visibleFiles[args.RowIndex];
            FileEntry file = result.File;
            args.Value = args.ColumnIndex switch
            {
                0 => InventoryLabels.Status(result.Status),
                1 => file.Name,
                2 => file.Folder,
                3 => ExtensionLabel(file.Extension),
                4 => InventoryLabels.Size(file.Bytes),
                5 => file.ModifiedUtc.ToLocalTime().ToString("g"),
                6 => result.Details,
                _ => null
            };
        };
        files.CellToolTipTextNeeded += (_, args) =>
        {
            if (args.RowIndex < 0 || args.RowIndex >= visibleFiles.Count)
                return;
            FileResult result = visibleFiles[args.RowIndex];
            args.ToolTipText = $"{result.File.Path}\n{result.File.Bytes:N0} Bytes · {result.File.Attributes}\n"
                + $"Erstellt {result.File.CreatedUtc.ToLocalTime():G}"
                + (result.Details.Length == 0 ? "" : $"\n{result.Details}");
        };
        files.ColumnHeaderMouseClick += (_, args) => SetSort(args.ColumnIndex,
            sortColumn == args.ColumnIndex ? !ascending : args.ColumnIndex != 4);
    }

    private void LoadSnapshot()
    {
        try
        {
            snapshot = store.Load();
            if (snapshot is not null)
            {
                roots.Items.AddRange(snapshot.Roots.Select(root => root.Root).Cast<object>().ToArray());
                ShowCompletedScan();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            storeReadable = false;
            scanStatus.Text = "Das gespeicherte Inventar konnte nicht geladen werden.";
            ShowError("Inventar konnte nicht geladen werden. Die gespeicherte Datei wurde beibehalten.", error);
        }
        RefreshSnapshot();
    }

    private void AddRoot()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Ordner oder Laufwerk für das Inventar auswählen",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;

        string selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(picker.SelectedPath));
        foreach (string root in roots.Items.Cast<string>())
        {
            if (InventoryPaths.IsWithin(selected, root) || InventoryPaths.IsWithin(root, selected))
            {
                MessageBox.Show(this, $"Der Scan-Ordner überschneidet sich mit {root}.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        roots.SelectedIndex = roots.Items.Add(selected);
        UpdateActions();
    }

    private async Task ScanAsync()
    {
        var scanRoots = roots.Items.Cast<string>().ToArray();
        if (scanRoots.Length == 0 || scanning is not null || duplicateRun is not null || fileRunBusy || !storeReadable)
            return;

        using var cancellation = new CancellationTokenSource();
        scanning = cancellation;
        UpdateActions();
        scanStatus.Text = "Scan läuft …";
        var progress = new Progress<ScanProgress>(update =>
        {
            if (scanning == cancellation && !cancellation.IsCancellationRequested)
                scanStatus.Text = $"{update.Files:N0} Dateien erfasst · {update.Folder}";
        });
        InventorySnapshot? previous = snapshot;

        try
        {
            snapshot = await Task.Run(() =>
            {
                InventorySnapshot completed = InventoryScanner.Scan(scanRoots, previous, progress, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                store.Save(completed);
                return completed;
            });
            RefreshSnapshot();
            ShowCompletedScan();
        }
        catch (OperationCanceledException)
        {
            scanStatus.Text = "Scan abgebrochen. Der letzte gespeicherte Stand bleibt verfügbar.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            scanStatus.Text = "Der Scan konnte nicht gespeichert werden.";
            ShowError("Der neue Scan konnte nicht gespeichert werden.", error);
        }
        finally
        {
            scanning = null;
            UpdateActions();
        }
    }

    private void RefreshSnapshot()
    {
        allFiles = snapshot?.Roots.SelectMany(root => root.Files).ToList() ?? [];
        foreach (CheckBox filter in statusFilters)
        {
            FileStatus status = (FileStatus)filter.Tag!;
            filter.Text = $"{InventoryLabels.Status(status)} ({allFiles.Count(file => file.Status == status):N0})";
        }

        refreshingFilters = true;
        FillDropdown(folderFilter, "Alle Ordner", allFiles.Select(file => file.File.Folder));
        FillDropdown(extensionFilter, "Alle Dateitypen", allFiles.Select(file => ExtensionLabel(file.File.Extension)));
        refreshingFilters = false;
        ApplyFilters();

        issues.Rows.Clear();
        int issueCount = 0;
        foreach (RootInventory root in snapshot?.Roots ?? [])
        {
            foreach (ScanIssue issue in root.Issues)
            {
                string message = InventoryPaths.Comparer.Equals(root.Root, issue.Path)
                    ? $"Scan-Ordner nicht erreichbar · {issue.Reason}"
                    : $"Scan unvollständig · {issue.Reason}";
                issues.Rows.Add(issue.Path, message);
                issueCount++;
            }
        }
        issueTab.Text = $"Scan-Hinweise ({issueCount:N0})";
        RefreshStorage();
        RefreshDuplicateSource();
        ClearSortPlan();
        UpdateActions();
    }

    private void ShowCompletedScan()
    {
        if (snapshot is null)
            return;
        int issueCount = snapshot.Roots.Sum(root => root.Issues.Count);
        string details = issueCount == 0 ? "Abgeschlossen" : $"Unvollständig · {issueCount:N0} Hinweise";
        scanStatus.Text = $"Letzter Scan {snapshot.CompletedUtc.ToLocalTime():g} · {details}";
    }

    private void ApplyFilters()
    {
        if (refreshingFilters)
            return;
        var statuses = statusFilters.Where(filter => filter.Checked).Select(filter => (FileStatus)filter.Tag!).ToHashSet();
        string text = nameFilter.Text.Trim();
        string? folder = folderFilter.SelectedIndex > 0 ? (string?)folderFilter.SelectedItem : null;
        string? extension = extensionFilter.SelectedIndex > 0 ? (string?)extensionFilter.SelectedItem : null;
        decimal min = minSize.Value * 1_048_576;
        decimal max = maxSize.Value * 1_048_576;
        visibleFiles = allFiles.Where(result =>
        {
            FileEntry file = result.File;
            DateTime date = file.ModifiedUtc.ToLocalTime().Date;
            return (statuses.Count == 0 || statuses.Contains(result.Status))
                && (text.Length == 0 || file.Path.Contains(text, StringComparison.OrdinalIgnoreCase))
                && (folder is null || InventoryPaths.IsWithin(file.Folder, folder))
                && (extension is null || InventoryPaths.Comparer.Equals(ExtensionLabel(file.Extension), extension))
                && file.Bytes >= min && (max == 0 || file.Bytes <= max)
                && (!fromDate.Checked || date >= fromDate.Value.Date)
                && (!untilDate.Checked || date <= untilDate.Value.Date);
        }).ToList();

        SortFiles();
        files.RowCount = visibleFiles.Count;
        files.Invalidate();
        fileCount.Text = $"{visibleFiles.Count:N0} von {allFiles.Count:N0} Einträgen";
        export.Enabled = visibleFiles.Count > 0;
    }

    private void ResetFilters()
    {
        refreshingFilters = true;
        foreach (CheckBox filter in statusFilters)
            filter.Checked = false;
        nameFilter.Clear();
        folderFilter.SelectedIndex = 0;
        extensionFilter.SelectedIndex = 0;
        minSize.Value = 0;
        maxSize.Value = 0;
        fromDate.Checked = false;
        untilDate.Checked = false;
        refreshingFilters = false;
        ApplyFilters();
    }

    private void SetSort(int column, bool increasing)
    {
        files.Columns[sortColumn].HeaderCell.SortGlyphDirection = SortOrder.None;
        sortColumn = column;
        ascending = increasing;
        files.Columns[column].HeaderCell.SortGlyphDirection = ascending ? SortOrder.Ascending : SortOrder.Descending;
        SortFiles();
        files.Invalidate();
    }

    private void SortFiles()
    {
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        visibleFiles.Sort((left, right) =>
        {
            int comparison = sortColumn switch
            {
                0 => comparer.Compare(InventoryLabels.Status(left.Status), InventoryLabels.Status(right.Status)),
                1 => comparer.Compare(left.File.Name, right.File.Name),
                2 => comparer.Compare(left.File.Folder, right.File.Folder),
                3 => comparer.Compare(left.File.Extension, right.File.Extension),
                4 => left.File.Bytes.CompareTo(right.File.Bytes),
                5 => left.File.ModifiedUtc.CompareTo(right.File.ModifiedUtc),
                _ => comparer.Compare(left.Details, right.Details)
            };
            int ordered = ascending ? comparison : -comparison;
            return ordered != 0 ? ordered : InventoryPaths.Comparer.Compare(left.File.Path, right.File.Path);
        });
    }

    private void RefreshStorage()
    {
        FileEntry[] present = allFiles.Where(IsPresent).Select(result => result.File).ToArray();
        storageSummary.Text = $"{present.Length:N0} erfasste Dateien · {InventoryLabels.Size(present.Sum(file => file.Bytes))}";
        RefreshStorageBreakdown(breakdownFilter.SelectedIndex == 1);
        var folders = new Dictionary<string, StorageRow>(InventoryPaths.Comparer);
        foreach (RootInventory root in snapshot?.Roots ?? [])
        {
            foreach (FileResult result in root.Files.Where(IsPresent))
            {
                FileEntry file = result.File;
                for (string? path = file.Folder; path is not null && InventoryPaths.IsWithin(path, root.Root);
                     path = Path.GetDirectoryName(path))
                {
                    folders.TryGetValue(path, out StorageRow? total);
                    folders[path] = new StorageRow(path, (total?.Count ?? 0) + 1, (total?.Bytes ?? 0) + file.Bytes);
                }
            }
        }

        FillStorageGrid(byFolder, folders.Values.OrderByDescending(row => row.Bytes).ThenBy(row => row.Group));
    }

    private void RefreshStorageBreakdown(bool sizes)
    {
        IEnumerable<StorageRow> groups = allFiles.Where(IsPresent).Select(result => result.File)
            .GroupBy(file => sizes ? InventoryLabels.SizeClass(file.Bytes) : ExtensionLabel(file.Extension), InventoryPaths.Comparer)
            .Select(group => new StorageRow(group.Key, group.Count(), group.Sum(file => file.Bytes)))
            .OrderByDescending(row => row.Bytes).ThenBy(row => row.Group);
        FillStorageGrid(byType, groups);
    }

    private void ExportCsv()
    {
        using var picker = new SaveFileDialog
        {
            Filter = "CSV-Datei (*.csv)|*.csv",
            FileName = $"Dateiinventar-{DateTime.Today:yyyy-MM-dd}.csv",
            Title = "Gefiltertes Inventar exportieren"
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            InventoryStore.ExportCsv(picker.FileName, visibleFiles);
            scanStatus.Text = $"{visibleFiles.Count:N0} Einträge exportiert · {picker.FileName}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowError("Das Inventar konnte nicht exportiert werden.", error);
        }
    }

    private void UpdateActions()
    {
        bool idle = scanning is null && duplicateRun is null && !fileRunBusy;
        addRoot.Enabled = idle;
        roots.Enabled = idle;
        removeRoot.Enabled = idle && roots.SelectedIndex >= 0;
        scan.Enabled = idle && storeReadable && roots.Items.Count > 0;
        cancel.Enabled = !idle && scanning?.IsCancellationRequested == false;
        export.Enabled = visibleFiles.Count > 0;
        UpdateDuplicateActions();
        UpdateSortPlanActions();
        UpdateFileRunActions();
    }

    private void ShowError(string heading, Exception error) => MessageBox.Show(this, $"{heading}\n\n{error.Message}",
        Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static bool IsPresent(FileResult result) => result.Status is not (FileStatus.Missing or FileStatus.Unavailable);

    private static string ExtensionLabel(string extension) => extension.Length == 0 ? "Ohne Endung" : extension.ToLowerInvariant();

    private static void FillStorageGrid(DataGridView grid, IEnumerable<StorageRow> rows)
    {
        grid.Rows.Clear();
        foreach (StorageRow row in rows)
            grid.Rows.Add(row.Group, row.Count.ToString("N0"), InventoryLabels.Size(row.Bytes));
    }

    private static void FillDropdown(ComboBox box, string first, IEnumerable<string> values)
    {
        string? previous = box.SelectedItem as string;
        box.BeginUpdate();
        box.Items.Clear();
        box.Items.Add(first);
        box.Items.AddRange(values.Distinct(InventoryPaths.Comparer).Order(StringComparer.CurrentCultureIgnoreCase)
            .Cast<object>().ToArray());
        box.SelectedItem = previous;
        if (box.SelectedIndex < 0)
            box.SelectedIndex = 0;
        box.EndUpdate();
    }

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(0) };
        flow.Controls.AddRange(controls);
        return flow;
    }

    private static Label CreateLabel(string text, Color color) => new()
    {
        Text = text, ForeColor = color, AutoSize = true, Margin = new Padding(2, 6, 8, 4)
    };

    private static CheckBox CreateStatusFilter(FileStatus status)
    {
        var filter = new CheckBox
        {
            Appearance = Appearance.Button, AutoSize = true, Tag = status,
            FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
            MinimumSize = new Size(0, 30), Margin = new Padding(2, 2, 6, 4)
        };
        UpdateStatusFilterAppearance(filter);
        return filter;
    }

    private static void UpdateStatusFilterAppearance(CheckBox filter)
    {
        bool highContrast = SystemInformation.HighContrast;
        Color selected = highContrast ? SystemColors.Highlight : Accent;
        Color idle = highContrast ? SystemColors.Window : Color.White;
        filter.BackColor = filter.Checked ? selected : idle;
        filter.ForeColor = filter.Checked
            ? (highContrast ? SystemColors.HighlightText : Color.White)
            : (highContrast ? SystemColors.WindowText : Ink);
        filter.FlatAppearance.BorderColor = filter.Checked ? selected
            : (highContrast ? SystemColors.WindowText : Color.FromArgb(166, 184, 197));
        filter.FlatAppearance.CheckedBackColor = selected;
        filter.FlatAppearance.MouseOverBackColor = highContrast ? filter.BackColor
            : filter.Checked ? Color.FromArgb(16, 73, 96) : Surface;
        filter.FlatAppearance.MouseDownBackColor = filter.FlatAppearance.MouseOverBackColor;
    }

    private static Button CreateButton(string text, bool primary = false)
    {
        var button = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 2, 8, 2), FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Color.White,
            ForeColor = primary ? Color.White : Ink, Margin = new Padding(2, 2, 6, 2) };
        button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(209, 219, 227);
        return button;
    }

    private static ComboBox CreateDropdown(int width) => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };

    private static NumericUpDown CreateSizeFilter() => new()
    {
        Width = 88, Minimum = 0, Maximum = 1_000_000_000, DecimalPlaces = 2, ThousandsSeparator = true
    };

    private static DateTimePicker CreateDateFilter() => new()
    {
        Format = DateTimePickerFormat.Short, ShowCheckBox = true, Checked = false, Width = 126
    };

    private static DataGridView CreateGrid(bool virtualMode = false) => new()
    {
        Dock = DockStyle.Fill, AutoGenerateColumns = false, VirtualMode = virtualMode, ReadOnly = true,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle, GridColor = Color.FromArgb(229, 235, 241),
        EnableHeadersVisualStyles = false, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Surface, ForeColor = Ink,
            Padding = new Padding(4) },
        DefaultCellStyle = new DataGridViewCellStyle { ForeColor = Ink, SelectionBackColor = Color.FromArgb(210, 233, 242),
            SelectionForeColor = Ink, Padding = new Padding(4, 0, 4, 0) },
        AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(249, 251, 253) },
        RowTemplate = new DataGridViewRow { Height = 27 }
    };

    private static void AddColumn(DataGridView grid, string heading, int width, bool fill = false) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = heading, Width = width, MinimumWidth = fill ? 80 : 28,
            AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
            FillWeight = width, SortMode = grid.VirtualMode
                ? DataGridViewColumnSortMode.Programmatic : DataGridViewColumnSortMode.NotSortable
        });
}

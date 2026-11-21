using System.Runtime.InteropServices;
using FilesizeChecker.Inventory;

namespace FilesizeChecker;

internal sealed partial class InventoryForm
{
    private const string InternalCollectionDrop = "CheckTools.FilesizeChecker.Collection";
    private const int CollectedPlanScope = 3;
    private static readonly Color DropIdle = Color.FromArgb(230, 243, 247);
    private static readonly Color DropHover = Color.FromArgb(198, 227, 236);

    private readonly FileCollection collectedFiles = new();
    private readonly Panel fileDrop = new() { Dock = DockStyle.Fill, Margin = new Padding(0), BorderStyle = BorderStyle.FixedSingle };
    private readonly Label collectionStatus = CreateLabel("Zeilen, Gruppen oder Dateien aus dem Explorer", Muted);
    private readonly Button viewCollection = CreateButton("Sammlung ansehen …");
    private readonly Button useCollection = CreateButton("In Sortierplan übernehmen", primary: true);
    private CollectionInput[]? internalCollectionDrag;
    private bool importingExplorerFiles;

    private sealed record CollectedDuplicate(DuplicateGroup Group, DuplicateMember Member, int Number);

    private Control BuildCollectionBar()
    {
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(20, 7, 20, 3)
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var dropText = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12, 5, 8, 3)
        };
        dropText.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dropText.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Label dropTitle = CreateLabel("Dateien hierher ziehen", Ink);
        dropTitle.Font = new Font(Font, FontStyle.Bold);
        dropTitle.Margin = new Padding(0, 1, 0, 3);
        dropText.Controls.Add(dropTitle, 0, 0);
        collectionStatus.AutoSize = false;
        collectionStatus.AutoEllipsis = true;
        collectionStatus.Dock = DockStyle.Fill;
        collectionStatus.Margin = new Padding(0, 0, 4, 1);
        dropText.Controls.Add(collectionStatus, 0, 1);
        fileDrop.BackColor = DropIdle;
        fileDrop.AccessibleName = "Dateisammlung für den Sortierplan";
        fileDrop.Controls.Add(dropText);
        bar.Controls.Add(fileDrop, 0, 0);
        var actions = Flow(viewCollection, useCollection);
        actions.Margin = new Padding(10, 11, 0, 0);
        bar.Controls.Add(actions, 1, 0);

        foreach (Control surface in new Control[] { fileDrop, dropText, dropTitle, collectionStatus })
        {
            surface.AllowDrop = true;
            surface.DragEnter += OnCollectionDragOver;
            surface.DragOver += OnCollectionDragOver;
            surface.DragLeave += (_, _) => fileDrop.BackColor = DropIdle;
            surface.DragDrop += async (_, args) => await ReceiveCollectionDropAsync(args);
        }
        viewCollection.Click += (_, _) => ShowCollection();
        useCollection.Click += (_, _) => OpenCollectionPlan();
        return bar;
    }

    private void WireCollectionSources()
    {
        files.ResolveDrag = (_, _, selection) => selection.Where(index => index >= 0 && index < visibleFiles.Count)
            .Select(index => visibleFiles[index]).Where(IsPresent)
            .Select(row => new CollectionInput(row.File, $"Inventar · {InventoryLabels.Status(row.Status)}"
                + (row.Details.Length > 0 ? $" · {row.Details}" : ""))).ToArray();
        duplicateGrid.ResolveDrag = (row, column, selection) => DuplicateDragItems(row, column, selection);
        nameFamilies.ResolveDrag = (row, _, _) => nameFamilies.Rows[row].Tag is NameFamily family
            ? family.Members.Select(member => NameDragItem(member, family.Name)).ToArray() : [];
        nameMembers.ResolveDrag = (_, _, selection) => selection.Select(index => nameMembers.Rows[index].Tag)
            .OfType<NameMatch>().Select(member => NameDragItem(member, SelectedNameFamily?.Name)).ToArray();
        foreach (FileDragGrid grid in new[] { files, duplicateGrid, nameFamilies, nameMembers })
            grid.StartFileDrag = entries => StartCollectionDrag(grid, entries);
    }

    private CollectionInput[] DuplicateDragItems(int row, int column, int[] selection)
    {
        if (column == 0 && duplicateGrid.Rows[row].Tag is CollectedDuplicate group)
            return group.Group.Files.Select(member => DuplicateDragItem(member, group.Number)).ToArray();

        return selection.Select(index => duplicateGrid.Rows[index].Tag).OfType<CollectedDuplicate>()
            .Select(item => DuplicateDragItem(item.Member, item.Number)).ToArray();
    }

    private static CollectionInput DuplicateDragItem(DuplicateMember member, int number) => new(member.File,
        $"Byteidentisch · Duplikatgruppe {number:N0}" + (member.Stamp?.Links > 1 ? " · Hardlink" : ""));

    private CollectionInput NameDragItem(NameMatch member, string? family)
    {
        familyVersions.TryGetValue(member.File.Path, out VersionReport? report);
        string group = family is null ? "Namensvergleich" : $"Namensgruppe «{family}»";
        string version = member.Name.Version is null ? "" : $" · Namensversion {member.Name.Version}";
        string metadata = string.IsNullOrEmpty(report?.InternalSummary) ? "" : $" · {report.InternalSummary}";
        return new CollectionInput(member.File, $"{group} · {member.Reason} ({member.Score} %){version}{metadata}");
    }

    private void StartCollectionDrag(Control source, CollectionInput[] entries)
    {
        if (entries.Length == 0 || !CanAcceptCollectionDrop)
            return;
        var data = new DataObject();
        data.SetDataAsJson(InternalCollectionDrop, true);
        internalCollectionDrag = entries;
        try
        {
            source.DoDragDrop(data, DragDropEffects.Copy);
        }
        catch (ExternalException error)
        {
            SetCollectionMessage($"Ziehen fehlgeschlagen · {error.Message}");
        }
        finally
        {
            internalCollectionDrag = null;
            fileDrop.BackColor = DropIdle;
        }
    }

    private bool CanAcceptCollectionDrop => !fileRunBusy && !importingExplorerFiles;

    private bool IsInternalCollectionDrag(IDataObject? data) => internalCollectionDrag is not null
        && data?.GetDataPresent(InternalCollectionDrop, autoConvert: false) == true;

    private void OnCollectionDragOver(object? sender, DragEventArgs args)
    {
        bool supported = CanAcceptCollectionDrop && (args.AllowedEffect & DragDropEffects.Copy) != 0
            && (IsInternalCollectionDrag(args.Data) || args.Data?.GetDataPresent(DataFormats.FileDrop, autoConvert: false) == true);
        args.Effect = supported ? DragDropEffects.Copy : DragDropEffects.None;
        fileDrop.BackColor = supported ? DropHover : DropIdle;
    }

    private async Task ReceiveCollectionDropAsync(DragEventArgs args)
    {
        fileDrop.BackColor = DropIdle;
        if (!CanAcceptCollectionDrop || (args.AllowedEffect & DragDropEffects.Copy) == 0)
        {
            args.Effect = DragDropEffects.None;
            return;
        }

        if (IsInternalCollectionDrag(args.Data) && internalCollectionDrag is { } items)
        {
            args.Effect = DragDropEffects.Copy;
            await AddToCollectionAsync(items);
            return;
        }

        string[]? paths;
        try
        {
            paths = args.Data?.GetData(DataFormats.FileDrop, autoConvert: false) as string[];
        }
        catch (Exception error) when (error is ExternalException or InvalidOperationException)
        {
            SetCollectionMessage($"Explorer-Auswahl konnte nicht gelesen werden · {error.Message}");
            return;
        }
        if (paths is not { Length: > 0 })
        {
            args.Effect = DragDropEffects.None;
            return;
        }

        args.Effect = DragDropEffects.Copy;
        importingExplorerFiles = true;
        SetCollectionMessage("Explorer-Auswahl wird geprüft …");
        UpdateCollectionActions();
        UpdateSortPlanActions();
        try
        {
            CollectionImport imported = await Task.Run(() => CollectionImport.ReadExplorerFiles(paths));
            if (!IsDisposed && !Disposing)
            {
                string folders = imported.Folders == 0 ? "" : $" · {imported.Folders:N0} Ordner übersprungen";
                string issues = imported.Issues.Count switch
                {
                    0 => "",
                    1 => " · 1 Pfad nicht erreichbar",
                    _ => $" · {imported.Issues.Count:N0} Pfade nicht erreichbar"
                };
                await AddToCollectionAsync(imported.Files, folders + issues);
            }
        }
        finally
        {
            importingExplorerFiles = false;
            if (!IsDisposed && !Disposing)
            {
                UpdateCollectionActions();
                UpdateSortPlanActions();
            }
        }
    }

    private async Task AddToCollectionAsync(CollectionInput[] entries, string feedback = "")
    {
        HashSet<string> artifacts = CurrentRunArtifacts();
        CollectionInput[] eligible = entries.Where(entry => !artifacts.Contains(entry.File.Path)).ToArray();
        (int added, bool changed) = collectedFiles.Add(eligible);
        string addedMessage = added > 0 ? $"{added:N0} hinzugefügt" : changed ? "Befunde ergänzt" : "";
        if (entries.Length > eligible.Length)
            feedback += $" · {entries.Length - eligible.Length:N0} App-Sicherungen oder temporäre Dateien übersprungen";
        SetCollectionMessage((addedMessage + feedback).Trim(' ', '·'));
        UpdateCollectionActions();
        UpdateSortPlanActions();
        if (changed)
            await UpdateCollectedSortPlanAsync();
    }

    private void SetCollectionMessage(string message)
    {
        string count = collectedFiles.Count == 1 ? "1 Datei gesammelt" : $"{collectedFiles.Count:N0} Dateien gesammelt";
        collectionStatus.Text = message.Length > 0 ? $"{count} · {message}"
            : collectedFiles.Count == 0 ? "Zeilen, Gruppen oder Dateien aus dem Explorer" : count;
    }

    private void UpdateCollectionActions()
    {
        bool available = collectedFiles.Count > 0 && CanAcceptCollectionDrop;
        viewCollection.Enabled = available;
        useCollection.Enabled = available;
    }

    private void OpenCollectionPlan()
    {
        if (collectedFiles.Count == 0 || fileRunBusy)
            return;
        ((TabControl)sortPlanTab.Parent!).SelectedTab = sortPlanTab;
        planScope.SelectedIndex = CollectedPlanScope;
        if (activePlan is null)
            planStatus.Text = $"{collectedFiles.Count:N0} gesammelte Dateien · Ziel und Ablageart wählen.";
        planDestination.Focus();
    }

    private void ShowCollection()
    {
        using var dialog = new Form
        {
            Text = "Gesammelte Dateien", ClientSize = new Size(930, 515), MinimumSize = new Size(770, 380),
            StartPosition = FormStartPosition.CenterParent, Font = Font, Padding = new Padding(10), MinimizeBox = false
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        DataGridView grid = CreateGrid();
        AddColumn(grid, "Dateiname", 230, fill: true);
        AddColumn(grid, "Ordner", 265, fill: true);
        AddColumn(grid, "Größe", 95);
        AddColumn(grid, "Herkunft und Befunde", 310, fill: true);
        layout.Controls.Add(grid, 0, 0);
        var remove = CreateButton("Markierte entfernen");
        var go = CreateButton("In Sortierplan übernehmen", primary: true);
        var close = CreateButton("Schließen");
        go.DialogResult = DialogResult.OK;
        close.DialogResult = DialogResult.Cancel;
        dialog.CancelButton = close;
        layout.Controls.Add(Flow(remove, go, close), 0, 1);
        dialog.Controls.Add(layout);

        dialog.Shown += (_, _) => grid.ClearSelection();
        grid.SelectionChanged += (_, _) => remove.Enabled = grid.SelectedRows.Count > 0;
        remove.Click += async (_, _) =>
        {
            string[] selection = grid.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Tag).OfType<string>().ToArray();
            int removed = collectedFiles.Remove(selection);
            SetCollectionMessage(removed > 0 ? $"{removed:N0} entfernt" : "");
            UpdateCollectionActions();
            UpdateSortPlanActions();
            FillRows();
            if (removed > 0)
                await UpdateCollectedSortPlanAsync();
        };
        FillRows();
        if (dialog.ShowDialog(this) == DialogResult.OK)
            OpenCollectionPlan();

        void FillRows()
        {
            grid.Rows.Clear();
            foreach (CollectedFile entry in collectedFiles.Items.OrderBy(item => item.File.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.File.Path, InventoryPaths.Comparer))
            {
                int index = grid.Rows.Add(entry.File.Name, entry.File.Folder, InventoryLabels.Size(entry.File.Bytes), entry.Summary);
                DataGridViewRow row = grid.Rows[index];
                row.Tag = entry.File.Path;
                row.Cells[0].ToolTipText = entry.File.Path;
                row.Cells[3].ToolTipText = entry.Summary;
            }
            grid.ClearSelection();
            remove.Enabled = false;
            go.Enabled = collectedFiles.Count > 0;
        }
    }
}

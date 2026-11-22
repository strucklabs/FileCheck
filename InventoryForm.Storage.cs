using FilesizeChecker.Inventory;

namespace FilesizeChecker;

internal sealed partial class InventoryForm
{
    private enum StorageGroup { All, Type, Size, Folder }

    private sealed record StorageSelection(StorageGroup Group, string Name)
    {
        public string Description => Group == StorageGroup.Folder ? $"{Name} einschließlich Unterordnern" : Name;

        public bool Contains(FileEntry file) => Group switch
        {
            StorageGroup.Type => InventoryPaths.Comparer.Equals(ExtensionLabel(file.Extension), Name),
            StorageGroup.Size => InventoryLabels.SizeClass(file.Bytes) == Name,
            StorageGroup.Folder => InventoryPaths.IsWithin(file.Folder, Name),
            _ => true
        };
    }

    private void WireStorageGrid(DataGridView grid, TabPage inventoryTab)
    {
        var showColumn = new DataGridViewButtonColumn
        {
            HeaderText = "",
            Text = "Dateien anzeigen",
            UseColumnTextForButtonValue = true,
            Width = 123,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None
        };
        grid.Columns.Add(showColumn);
        grid.CellContentClick += (_, args) =>
        {
            if (args.ColumnIndex == showColumn.Index && args.RowIndex >= 0
                && grid.Rows[args.RowIndex].Tag is StorageSelection selection)
                OpenStorageInventory(inventoryTab, selection);
        };

        var menu = FileCheckTheme.CreateContextMenu();
        var showFiles = new ToolStripMenuItem("Dateien anzeigen");
        var collectGroup = new ToolStripMenuItem();
        menu.Items.AddRange([showFiles, collectGroup]);
        grid.ContextMenuStrip = menu;
        grid.Disposed += (_, _) => menu.Dispose();

        int? clickedRow = null;
        StorageSelection? selected = null;
        CollectionInput[] groupFiles = [];
        grid.CellMouseDown += (_, args) =>
        {
            if (args.Button == MouseButtons.Right)
                clickedRow = args.RowIndex;
        };
        menu.Opening += (_, args) =>
        {
            int row = clickedRow ?? grid.CurrentRow?.Index ?? -1;
            clickedRow = null;
            selected = row >= 0 && row < grid.Rows.Count ? grid.Rows[row].Tag as StorageSelection : null;
            if (selected is null)
            {
                args.Cancel = true;
                return;
            }

            HashSet<string> artifacts = CurrentRunArtifacts();
            groupFiles = allFiles.Where(result => IsPresent(result) && selected.Contains(result.File)
                    && !artifacts.Contains(result.File.Path))
                .DistinctBy(result => result.File.Path, InventoryPaths.Comparer)
                .Select(result => new CollectionInput(result.File, $"Speicheranalyse · {selected.Description}"))
                .ToArray();
            string count = groupFiles.Length == 1 ? "1 Datei" : $"Alle {groupFiles.Length:N0} Dateien";
            string scope = selected.Group == StorageGroup.Folder ? " einschließlich Unterordnern" : "";
            collectGroup.Text = $"{count}{scope} sammeln";
            collectGroup.Enabled = groupFiles.Length > 0 && CanAcceptCollectionDrop;
        };
        showFiles.Click += (_, _) =>
        {
            if (selected is not null)
                OpenStorageInventory(inventoryTab, selected);
        };
        collectGroup.Click += async (_, _) =>
        {
            if (CanAcceptCollectionDrop)
                await AddToCollectionAsync(groupFiles);
        };
    }

    private void OpenStorageInventory(TabPage inventoryTab, StorageSelection selection)
    {
        ResetFilters(selection);
        SetSort(4, false);
        ((TabControl)inventoryTab.Parent!).SelectedTab = inventoryTab;
    }

    private static void FillStorageGrid(DataGridView grid, IEnumerable<StorageRow> rows, StorageGroup group)
    {
        grid.Rows.Clear();
        foreach (StorageRow row in rows)
        {
            int index = grid.Rows.Add(row.Group, row.Count.ToString("N0"), InventoryLabels.Size(row.Bytes));
            grid.Rows[index].Tag = new StorageSelection(group, row.Group);
        }
    }
}

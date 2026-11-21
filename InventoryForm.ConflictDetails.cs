using FilesizeChecker.Inventory;

namespace FilesizeChecker;

internal sealed partial class InventoryForm
{
    private readonly TableLayoutPanel planConflictDetails = new()
    {
        Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 2, RowCount = 1, Margin = new Padding(3, 3, 3, 0), Visible = false
    };
    private readonly TextBox conflictSourcePath = ConflictPathField("Quellpfad");
    private readonly TextBox conflictTargetPath = ConflictPathField("Zielpfad des Konflikts");
    private readonly TextBox conflictOtherSource = ConflictPathField("Quelle des konkurrierenden Planeintrags", wrap: false);
    private readonly Label conflictSourceMetadata = CreateLabel("", Muted);
    private readonly Label conflictTargetMetadata = CreateLabel("", Muted);

    private Control BuildSortConflictDetails()
    {
        planConflictDetails.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        planConflictDetails.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        planConflictDetails.Controls.Add(ConflictSide("Quelle", conflictSourcePath, conflictSourceMetadata), 0, 0);
        planConflictDetails.Controls.Add(ConflictSide("Ziel des Konflikts", conflictTargetPath, conflictTargetMetadata, conflictOtherSource), 1, 0);
        conflictOtherSource.Visible = false;
        return planConflictDetails;
    }

    private static TableLayoutPanel ConflictSide(string title, TextBox path, Label metadata, TextBox? otherSource = null)
    {
        var side = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = otherSource is null ? 3 : 4, BackColor = Surface,
            Padding = new Padding(7, 1, 7, 3), Margin = new Padding(0, 1, 4, 1)
        };
        for (int i = 0; i < side.RowCount; i++)
            side.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        side.Controls.Add(CreateLabel(title, Ink), 0, 0);
        side.Controls.Add(path, 0, 1);
        side.Controls.Add(metadata, 0, 2);
        if (otherSource is not null)
            side.Controls.Add(otherSource, 0, 3);
        return side;
    }

    private static TextBox ConflictPathField(string label, bool wrap = true) => new()
    {
        AccessibleName = label, ReadOnly = true, Dock = DockStyle.Fill, BackColor = Color.White,
        Multiline = wrap, WordWrap = wrap, Height = wrap ? 42 : 24,
        ScrollBars = wrap ? ScrollBars.Vertical : ScrollBars.None
    };

    private void UpdateSortConflictDetails(PlanRow[] selected)
    {
        if (selected.Length != 1 || selected[0].ConflictDetails is not { } target)
        {
            planConflictDetails.Visible = false;
            return;
        }

        FileEntry source = selected[0].Source;
        conflictSourcePath.Text = source.Path;
        conflictSourceMetadata.Text = ConflictFileMetadata(source);
        conflictTargetPath.Text = target.Path;
        bool planned = target.File is { } plannedFile && !InventoryPaths.Comparer.Equals(plannedFile.Path, target.Path);
        conflictTargetMetadata.Text = target.File is { } existing
            ? (planned ? "Geplante Datei · " : "") + ConflictFileMetadata(existing)
            : target.Details;
        conflictOtherSource.Text = planned ? $"Quelle im Plan: {target.File!.Path}" : "";
        conflictOtherSource.Visible = planned;
        planConflictDetails.Visible = true;
    }

    private static string ConflictFileMetadata(FileEntry file) => $"{file.Bytes:N0} Bytes · geändert {file.ModifiedUtc.ToLocalTime():g}";
}

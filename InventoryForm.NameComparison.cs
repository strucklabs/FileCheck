using FilesizeChecker.Inventory;

namespace FilesizeChecker;

internal sealed partial class InventoryForm
{
    private readonly ComboBox nameSensitivity = CreateDropdown(132);
    private readonly Button findNames = CreateButton("Namensgruppen suchen", primary: true);
    private readonly Button stopNames = CreateButton("Abbrechen");
    private readonly Button readFamilyVersions = CreateButton("Versionen der Gruppe prüfen");
    private readonly Button inspectNameSelection = CreateButton("Markierte Datei prüfen");
    private readonly Button compareNameSelection = CreateButton("Zwei Markierte vergleichen");
    private readonly Button openVersionFile = CreateButton("Datei prüfen …");
    private readonly Button openVersionComparison = CreateButton("Zwei Dateien vergleichen …");
    private readonly FileDragGrid nameFamilies = CreateGrid();
    private readonly FileDragGrid nameMembers = CreateGrid();
    private readonly Label namesStatus = CreateLabel("Suche im Inventar nach Varianten eines Dateinamens.", Muted);
    private readonly Label familyStatus = CreateLabel("Wähle eine Namensgruppe.", Muted);
    private readonly Dictionary<string, VersionReport> familyVersions = new(InventoryPaths.Comparer);
    private CancellationTokenSource? nameRun;
    private bool fillingNameGrid;

    private sealed record VersionComparison(VersionReport Left, VersionReport? Right, VersionOrder? Order, ContentComparison? Content);

    private NameFamily? SelectedNameFamily => nameFamilies.CurrentRow?.Tag as NameFamily;

    private TabPage BuildNameComparisonTab()
    {
        var tab = new TabPage("Namen und Versionen") { BackColor = Color.White };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(10, 8, 10, 7)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 74));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        nameSensitivity.Items.AddRange(["Streng", "Ausgewogen", "Breit"]);
        nameSensitivity.SelectedIndex = (int)SimilarityLevel.Balanced;
        var toolbar = Flow(CreateLabel("Namensähnlichkeit", Muted), nameSensitivity, findNames, stopNames,
            openVersionFile, openVersionComparison);
        layout.Controls.Add(toolbar, 0, 0);
        layout.SetColumnSpan(toolbar, 2);
        layout.Controls.Add(CreateLabel("Namensgruppen", Ink), 0, 1);
        layout.Controls.Add(Flow(readFamilyVersions, inspectNameSelection, compareNameSelection), 1, 1);

        nameFamilies.MultiSelect = false;
        AddColumn(nameFamilies, "Namensstamm", 178, fill: true);
        AddColumn(nameFamilies, "Dateien", 73);
        AddColumn(nameMembers, "Dateiname", 190, fill: true);
        AddColumn(nameMembers, "Ähnlichkeit", 92);
        AddColumn(nameMembers, "Namensbeleg", 165, fill: true);
        AddColumn(nameMembers, "Version im Namen", 135);
        AddColumn(nameMembers, "Interne Angaben", 150, fill: true);
        layout.Controls.Add(nameFamilies, 0, 2);
        layout.Controls.Add(nameMembers, 1, 2);
        foreach (Label label in new[] { namesStatus, familyStatus })
        {
            label.AutoSize = false;
            label.AutoEllipsis = true;
            label.Dock = DockStyle.Fill;
            label.Height = 23;
        }
        layout.Controls.Add(namesStatus, 0, 3);
        layout.Controls.Add(familyStatus, 1, 3);
        tab.Controls.Add(layout);

        findNames.Click += async (_, _) => await SearchNamesAsync();
        stopNames.Click += (_, _) =>
        {
            nameRun?.Cancel();
            UpdateNameActions();
        };
        nameFamilies.CurrentCellChanged += (_, _) =>
        {
            if (!fillingNameGrid)
                RenderNameFamily();
        };
        nameMembers.SelectionChanged += (_, _) => UpdateNameActions();
        readFamilyVersions.Click += async (_, _) => await ReadFamilyVersionsAsync();
        inspectNameSelection.Click += async (_, _) => await InspectSelectedNamesAsync(compare: false);
        compareNameSelection.Click += async (_, _) => await InspectSelectedNamesAsync(compare: true);
        openVersionFile.Click += async (_, _) => await PickVersionFilesAsync(compare: false);
        openVersionComparison.Click += async (_, _) => await PickVersionFilesAsync(compare: true);
        return tab;
    }

    private bool NameJobAvailable => nameRun is null && scanning is null && duplicateRun is null && imageRun is null && videoRun is null && !fileRunBusy;

    private void UpdateNameActions()
    {
        bool idle = NameJobAvailable;
        findNames.Enabled = idle && allFiles.Any(IsPresent);
        nameSensitivity.Enabled = idle;
        stopNames.Enabled = nameRun?.IsCancellationRequested == false;
        readFamilyVersions.Enabled = idle && SelectedNameFamily is not null;
        inspectNameSelection.Enabled = idle && nameMembers.SelectedRows.Count == 1;
        compareNameSelection.Enabled = idle && nameMembers.SelectedRows.Count == 2;
        openVersionFile.Enabled = openVersionComparison.Enabled = idle;
        nameFamilies.Enabled = idle;
        nameMembers.Enabled = idle;
    }

    private async Task<T?> RunNameJobAsync<T>(Func<CancellationToken, T> work) where T : class
    {
        if (!NameJobAvailable)
            return null;
        using var cancellation = new CancellationTokenSource();
        nameRun = cancellation;
        UpdateActions();
        try
        {
            return await Task.Run(() => work(cancellation.Token), cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            familyStatus.Text = "Prüfung abgebrochen.";
            return null;
        }
        catch (Exception error) when (FileVersions.IsReadError(error))
        {
            familyStatus.Text = FileVersions.DescribeReadError(error);
            return null;
        }
        finally
        {
            nameRun = null;
            UpdateActions();
        }
    }

    private void ClearNameComparison()
    {
        familyVersions.Clear();
        fillingNameGrid = true;
        nameFamilies.Rows.Clear();
        fillingNameGrid = false;
        nameMembers.Rows.Clear();
        namesStatus.Text = "Suche im Inventar nach Varianten eines Dateinamens.";
        familyStatus.Text = "Wähle eine Namensgruppe.";
        UpdateNameActions();
    }

    private async Task SearchNamesAsync()
    {
        HashSet<string> artifacts = CurrentRunArtifacts();
        FileEntry[] source = allFiles.Where(result => IsPresent(result) && !artifacts.Contains(result.File.Path))
            .Select(result => result.File).ToArray();
        SimilarityLevel level = (SimilarityLevel)nameSensitivity.SelectedIndex;
        namesStatus.Text = "Suche läuft …";
        familyStatus.Text = $"Vergleiche die Namen von {source.Length:N0} Dateien.";
        NameFamily[]? found = await RunNameJobAsync(token => NameSimilarity.Find(source, level, token));
        if (found is null)
        {
            namesStatus.Text = "Suche beendet.";
            return;
        }

        familyVersions.Clear();
        fillingNameGrid = true;
        nameFamilies.Rows.Clear();
        foreach (NameFamily family in found)
        {
            int index = nameFamilies.Rows.Add(family.Name, family.Members.Count.ToString("N0"));
            nameFamilies.Rows[index].Tag = family;
            nameFamilies.Rows[index].Cells[0].ToolTipText = $"{family.Name}\nZiehe die Gruppe, um alle Dateien zu sammeln.";
        }
        if (nameFamilies.Rows.Count > 0)
            nameFamilies.CurrentCell = nameFamilies.Rows[0].Cells[0];
        fillingNameGrid = false;
        namesStatus.Text = found.Length == 1 ? "1 Namensgruppe" : $"{found.Length:N0} Namensgruppen";
        RenderNameFamily();
    }

    private void RenderNameFamily()
    {
        var wasSelected = nameMembers.SelectedRows.Cast<DataGridViewRow>().Select(row => (row.Tag as NameMatch)?.File.Path)
            .OfType<string>().ToHashSet(InventoryPaths.Comparer);
        nameMembers.Rows.Clear();
        if (SelectedNameFamily is not { } family)
        {
            familyStatus.Text = "Wähle eine Namensgruppe.";
            UpdateNameActions();
            return;
        }

        NameMatch[] members = family.Members.ToArray();
        bool inspected = members.All(member => familyVersions.ContainsKey(member.File.Path));
        int ordered = inspected ? OrderByComparableVersions(members) : 0;
        foreach (NameMatch member in members)
        {
            familyVersions.TryGetValue(member.File.Path, out VersionReport? report);
            string evidence = report?.InternalSummary ?? "";
            string discrepancy = VersionOrdering.FilenameDifference(report);
            string clue = member.Reason + (member.Name.Marker is null ? "" : $" · {member.Name.Marker}");
            int index = nameMembers.Rows.Add(member.File.Name, $"{member.Score}%", clue,
                member.Name.Version ?? "", evidence.Length > 0 ? evidence : report?.Issue ?? "");
            DataGridViewRow row = nameMembers.Rows[index];
            row.Tag = member;
            row.Cells[0].ToolTipText = member.File.Path;
            row.Cells[2].ToolTipText = clue + (member.Name.Date is null ? "" : $" · Datum im Namen {member.Name.Date}");
            row.Cells[4].ToolTipText = report is null ? "" : string.Join('\n', report.Evidence.Select(item => $"{item.Label}: {item.Value}")
                .Concat(new[] { report.Issue, discrepancy }.OfType<string>().Where(text => text.Length > 0)));
            if (discrepancy.Length > 0)
                row.Cells[4].Style.ForeColor = Color.Maroon;
        }
        nameMembers.ClearSelection();
        foreach (DataGridViewRow row in nameMembers.Rows)
        {
            if (row.Tag is NameMatch member && wasSelected.Contains(member.File.Path))
                row.Selected = true;
        }
        familyStatus.Text = ordered == members.Length ? "Vergleichbare Versionen von niedrig nach hoch sortiert."
            : ordered > 0 ? $"{ordered:N0} Dateien nach vergleichbaren Versionen sortiert · {members.Length - ordered:N0} offen."
            : inspected ? "Reihenfolge offen. Markiere zwei Dateien, um die Versionsbelege gegenüberzustellen."
            : $"{members.Length:N0} Dateien · Markiere eine oder zwei Dateien für eine Einzelprüfung oder einen Vergleich.";
        UpdateNameActions();
    }

    private int OrderByComparableVersions(NameMatch[] members)
    {
        if (members.Length > 50)
            return 0;
        int[] eligible = Enumerable.Range(0, members.Length).Where(index =>
        {
            VersionReport report = familyVersions[members[index].File.Path];
            return VersionOrdering.Compare(report, report).Direction == 0;
        }).ToArray();
        if (eligible.Length < 2)
            return 0;
        var comparisons = new int[members.Length, members.Length];
        var ranks = new int[members.Length];
        for (int first = 0; first < eligible.Length; first++)
        {
            for (int second = first + 1; second < eligible.Length; second++)
            {
                int left = eligible[first];
                int right = eligible[second];
                VersionOrder order = VersionOrdering.Compare(familyVersions[members[left].File.Path], familyVersions[members[right].File.Path]);
                if (order.Direction is not int direction)
                    return 0;
                comparisons[left, right] = direction;
                comparisons[right, left] = -direction;
                if (direction > 0)
                    ranks[left]++;
                else if (direction < 0)
                    ranks[right]++;
            }
        }
        int[] sorted = eligible.OrderBy(index => ranks[index]).ToArray();
        for (int left = 0; left < sorted.Length; left++)
        {
            for (int right = left + 1; right < sorted.Length; right++)
            {
                if (comparisons[sorted[left], sorted[right]] > 0)
                    return 0;
            }
        }
        NameMatch[] original = [.. members];
        int position = 0;
        foreach (int index in sorted.Concat(Enumerable.Range(0, members.Length).Except(eligible)))
            members[position++] = original[index];
        return eligible.Length;
    }

    private async Task ReadFamilyVersionsAsync()
    {
        if (SelectedNameFamily is not { } family)
            return;
        familyStatus.Text = "Versionsangaben werden gelesen …";
        VersionReport[]? reports = await RunNameJobAsync(token =>
        {
            var results = new List<VersionReport>();
            foreach (NameMatch member in family.Members)
            {
                token.ThrowIfCancellationRequested();
                results.Add(FileVersions.Inspect(member.File));
            }
            return results.ToArray();
        });
        if (reports is null)
            return;
        foreach (VersionReport report in reports)
            familyVersions[report.File.Path] = report;
        RenderNameFamily();
    }

    private async Task InspectSelectedNamesAsync(bool compare)
    {
        FileEntry[] selected = nameMembers.SelectedRows.Cast<DataGridViewRow>().OrderBy(row => row.Index)
            .Select(row => row.Tag).OfType<NameMatch>().Select(match => match.File).ToArray();
        if (selected.Length == (compare ? 2 : 1))
            await InspectFilesAsync(selected[0], compare ? selected[1] : null);
    }

    private async Task PickVersionFilesAsync(bool compare)
    {
        using var dialog = new OpenFileDialog { CheckFileExists = true, Multiselect = false, RestoreDirectory = true };
        dialog.Title = compare ? "Erste Datei zum Vergleich wählen" : "Datei zur Versionsprüfung wählen";
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        string left = dialog.FileName;
        string? right = null;
        if (compare)
        {
            dialog.FileName = "";
            dialog.InitialDirectory = Path.GetDirectoryName(left) ?? "";
            dialog.Title = "Zweite Datei zum Vergleich wählen";
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            right = dialog.FileName;
        }
        try
        {
            await InspectFilesAsync(FileVersions.ReadEntry(left), right is null ? null : FileVersions.ReadEntry(right));
        }
        catch (Exception error) when (FileVersions.IsReadError(error))
        {
            ShowError("Die gewählte Datei ist nicht erreichbar.", error);
        }
    }

    private async Task InspectFilesAsync(FileEntry first, FileEntry? second)
    {
        familyStatus.Text = second is null ? "Versionsangaben werden geprüft …" : "Versionen und Inhalt werden verglichen …";
        VersionComparison? result = await RunNameJobAsync(token =>
        {
            VersionReport left = FileVersions.Inspect(first);
            if (second is null)
                return new VersionComparison(left, null, null, null);
            token.ThrowIfCancellationRequested();
            VersionReport right = FileVersions.Inspect(second);
            return new VersionComparison(left, right, VersionOrdering.Compare(left, right), DocumentComparison.Compare(first, second, token));
        });
        if (result is null)
            return;
        familyVersions[result.Left.File.Path] = result.Left;
        if (result.Right is not null)
            familyVersions[result.Right.File.Path] = result.Right;
        RenderNameFamily();
        ShowVersionComparison(result);
    }

    private void ShowVersionComparison(VersionComparison result)
    {
        using var dialog = new Form
        {
            Text = result.Right is null ? "Versionsprüfung" : "Dateiversionen und Inhalt",
            ClientSize = new Size(1_010, 650), MinimumSize = new Size(820, 510),
            StartPosition = FormStartPosition.CenterParent, Font = Font, Padding = new Padding(10), MinimizeBox = false
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        string heading = result.Right is null ? result.Left.File.Path
            : $"Links  {result.Left.File.Path}\nRechts  {result.Right.File.Path}";
        Label pathLabel = CreateLabel(heading, Ink);
        pathLabel.MaximumSize = new Size(966, 0);
        layout.Controls.Add(pathLabel, 0, 0);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        DataGridView versions = CreateGrid();
        AddColumn(versions, "Beleg", 230, fill: true);
        AddColumn(versions, result.Right is null ? "Angabe" : "Links", 330, fill: true);
        if (result.Right is not null)
            AddColumn(versions, "Rechts", 330, fill: true);
        var keys = result.Left.Evidence.Concat(result.Right?.Evidence ?? []).Select(item => item.Label).Distinct();
        foreach (string key in keys)
            AddComparisonRow(versions, key, EvidenceValue(result.Left, key), EvidenceValue(result.Right, key));
        AddComparisonRow(versions, "Dateisystem-Zeitstempel · geändert", result.Left.File.ModifiedUtc.ToLocalTime().ToString("g"),
            result.Right?.File.ModifiedUtc.ToLocalTime().ToString("g") ?? "");
        if (result.Left.Issue is not null || result.Right?.Issue is not null)
            AddComparisonRow(versions, "Lesehinweis", result.Left.Issue ?? "", result.Right?.Issue ?? "");
        string leftNote = VersionOrdering.FilenameDifference(result.Left);
        string rightNote = VersionOrdering.FilenameDifference(result.Right);
        if (leftNote.Length > 0 || rightNote.Length > 0)
            AddComparisonRow(versions, "Abweichende Versionsangaben", leftNote, rightNote);
        if (result.Order is not null && result.Order.Evidence.Count > 0)
            AddComparisonRow(versions, "Numerischer Vergleich", string.Join('\n', result.Order.Evidence), "");
        string status = result.Order?.Summary ?? (result.Left.InternalSummary.Length > 0 ? "Versionsangaben aus der Datei gelesen."
            : "Keine auslesbare interne Versionsangabe gefunden.");
        tabs.TabPages.Add(ComparisonPage("Versionen", status, versions));
        if (result.Content is { } content)
        {
            DataGridView changes = CreateGrid();
            AddColumn(changes, "Position", 153, fill: true);
            AddColumn(changes, "Befund", 107);
            AddColumn(changes, "Links", 285, fill: true);
            AddColumn(changes, "Rechts", 285, fill: true);
            foreach (ContentDifference change in content.Differences)
                AddComparisonRow(changes, change.Location, change.Change, change.Left, change.Right);
            tabs.TabPages.Add(ComparisonPage("Inhaltsunterschiede", content.Summary, changes));
        }
        layout.Controls.Add(tabs, 0, 1);
        var close = CreateButton("Schließen");
        close.DialogResult = DialogResult.Cancel;
        dialog.CancelButton = close;
        layout.Controls.Add(Flow(close), 0, 2);
        dialog.Controls.Add(layout);
        dialog.ShowDialog(this);
    }

    private static string EvidenceValue(VersionReport? report, string label) => report is null ? "" :
        string.Join(" · ", report.Evidence.Where(item => item.Label == label).Select(item => item.Value));

    private static TabPage ComparisonPage(string title, string summary, DataGridView grid)
    {
        var page = new TabPage(title) { BackColor = Color.White };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(7) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Label description = CreateLabel(summary, Ink);
        description.MaximumSize = new Size(940, 0);
        layout.Controls.Add(description, 0, 0);
        layout.Controls.Add(grid, 0, 1);
        page.Controls.Add(layout);
        return page;
    }

    private static void AddComparisonRow(DataGridView grid, params string[] cells)
    {
        string[] display = cells.Take(grid.Columns.Count).Select(value => (value.Length > 480 ? value[..480] + "…" : value)
            .ReplaceLineEndings(" ↵ ")).ToArray();
        int index = grid.Rows.Add(display.Cast<object>().ToArray());
        for (int col = 0; col < display.Length; col++)
            grid.Rows[index].Cells[col].ToolTipText = cells[col].Length > 2_500 ? cells[col][..2_500] + "…" : cells[col];
    }
}

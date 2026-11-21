using System.Text.Json;
using FilesizeChecker.Inventory;

namespace FilesizeChecker;

internal sealed partial class InventoryForm
{
    private readonly TextBox planDestination = new() { Width = 400, PlaceholderText = "Absoluter Zielordner" };
    private readonly ComboBox planScope = CreateDropdown(192);
    private readonly ComboBox planMode = CreateDropdown(112);
    private readonly CheckBox[] planCriteria =
    [
        CreateCriterion("Dateityp", SortCriteria.Type), CreateCriterion("Datum", SortCriteria.Date),
        CreateCriterion("Größenklasse", SortCriteria.Size), CreateCriterion("Namensmuster", SortCriteria.Pattern),
        CreateCriterion("Namensähnlichkeit", SortCriteria.NameFamily)
    ];
    private readonly ComboBox planDate = CreateDropdown(145);
    private readonly ComboBox planMissingDate = CreateDropdown(191);
    private readonly TextBox planPattern = new() { Width = 120, PlaceholderText = "z. B. *.pdf" };
    private readonly CheckBox planFuzzyNames = CreateCriterion("Erweiterte Namensähnlichkeit", SortCriteria.FuzzyNames);
    private readonly ComboBox planRuleName = new() { Width = 220, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button previewPlan = CreateButton("Vorschau erstellen", primary: true);
    private readonly Button executePlan = CreateButton("Prüfen und ausführen …");
    private readonly Button excludeRows = CreateButton("Aus Plan ausschließen");
    private readonly Button includeRows = CreateButton("Wieder aufnehmen");
    private readonly Button changePlanFolder = CreateButton("Ziel ändern …");
    private readonly ComboBox resolveAction = CreateDropdown(151);
    private readonly TextBox resolvePrefix = new() { Width = 95, PlaceholderText = "Präfix" };
    private readonly TextBox resolveSuffix = new() { Width = 95, PlaceholderText = "Suffix" };
    private readonly Button resolveSelected = CreateButton("Auf Auswahl anwenden");
    private readonly Button resolveAll = CreateButton("Auf alle Konflikte anwenden");
    private readonly DataGridView planGrid = CreateGrid();
    private readonly Label planStatus = CreateLabel("Wähle Ziel und Sortierkriterien für die Vorschau.", Muted);
    private readonly Dictionary<string, PlanChoice> planChoices = new(InventoryPaths.Comparer);
    private SortRule[] savedRules = [];
    private PlanRow[] planRows = [];
    private SortPlanInput? activePlan;
    private bool buildingPlan;

    private sealed record SortPlanInput(FileEntry[] Files, string Destination, SortRule Rule, bool Move);

    private TabPage BuildSortPlanTab()
    {
        var tab = new TabPage("Sortierplan") { BackColor = Color.White };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(10, 7, 10, 5)
        };
        for (int row = 0; row < 6; row++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        planScope.Items.AddRange(["Gesamtes Inventar", "Gefiltertes Inventar", "Markierte Inventarzeilen"]);
        planScope.SelectedIndex = 0;
        planMode.Items.AddRange(["Kopieren", "Verschieben"]);
        planMode.SelectedIndex = 0;
        var browse = CreateButton("Ordner wählen …");
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { ShowNewFolderButton = false, Description = "Zielordner wählen" };
            if (picker.ShowDialog(this) == DialogResult.OK)
                planDestination.Text = picker.SelectedPath;
        };
        layout.Controls.Add(Flow(planDestination, browse, planScope, planMode), 0, 0);
        planCriteria[0].Checked = true;
        layout.Controls.Add(Flow([CreateLabel("Reihenfolge der Unterordner", Muted), .. planCriteria]), 0, 1);
        planDate.Items.AddRange(["Änderungsdatum", "Erstellungsdatum"]);
        planDate.SelectedIndex = 0;
        planMissingDate.Items.AddRange(["Ordner «Ohne Datum»", "Anderen Zeitstempel verwenden"]);
        planMissingDate.SelectedIndex = 0;
        layout.Controls.Add(Flow(CreateLabel("Datumsquelle", Muted), planDate, planMissingDate,
            CreateLabel("Namensmuster", Muted), planPattern, planFuzzyNames), 0, 2);

        planRuleName.AccessibleName = "Name einer Sortierregel";
        var saveRule = CreateButton("Regel speichern");
        saveRule.Click += (_, _) => SaveSortRule();
        planRuleName.SelectionChangeCommitted += (_, _) =>
        {
            if (planRuleName.SelectedIndex >= 0)
                ApplyRule(savedRules[planRuleName.SelectedIndex]);
        };
        layout.Controls.Add(Flow(CreateLabel("Sortierregel", Muted), planRuleName, saveRule, previewPlan, executePlan), 0, 3);
        layout.Controls.Add(Flow(excludeRows, includeRows, changePlanFolder), 0, 4);

        resolveAction.Items.AddRange(["Überspringen", "Ersetzen", "Beide behalten", "Präfix / Suffix"]);
        resolveAction.SelectedIndex = 0;
        layout.Controls.Add(Flow(CreateLabel("Namenskonflikte", Muted), resolveAction, resolvePrefix, resolveSuffix,
            resolveSelected, resolveAll), 0, 5);
        AddColumn(planGrid, "Quelldatei", 195, fill: true);
        AddColumn(planGrid, "Zielname", 195, fill: true);
        AddColumn(planGrid, "Zielordner", 195, fill: true);
        AddColumn(planGrid, "Gruppe", 105);
        AddColumn(planGrid, "Geplante Aktion", 139);
        AddColumn(planGrid, "Hinweis", 150, fill: true);
        layout.Controls.Add(planGrid, 0, 6);
        planStatus.MaximumSize = new Size(950, 0);
        layout.Controls.Add(planStatus, 0, 7);
        tab.Controls.Add(layout);

        planDestination.TextChanged += (_, _) => ClearSortPlan();
        planScope.SelectedIndexChanged += (_, _) => ClearSortPlan();
        planMode.SelectedIndexChanged += (_, _) => ClearSortPlan();
        planDate.SelectedIndexChanged += (_, _) => ClearSortPlan();
        planMissingDate.SelectedIndexChanged += (_, _) => ClearSortPlan();
        planPattern.TextChanged += (_, _) => ClearSortPlan();
        planFuzzyNames.CheckedChanged += (_, _) => ClearSortPlan();
        foreach (CheckBox criterion in planCriteria)
            criterion.CheckedChanged += (_, _) => ClearSortPlan();
        resolveAction.SelectedIndexChanged += (_, _) => UpdateSortPlanActions();
        planGrid.SelectionChanged += (_, _) => UpdateSortPlanActions();
        previewPlan.Click += async (_, _) => await StartSortPreviewAsync();
        executePlan.Click += async (_, _) => await ApprovePlanAsync();
        excludeRows.Click += async (_, _) => await ChangePlanRowsAsync(SelectedPlanRows(), choice => choice with { Excluded = true });
        includeRows.Click += async (_, _) => await ChangePlanRowsAsync(SelectedPlanRows(), choice => choice with
        {
            Excluded = false,
            Collision = choice.Collision == CollisionAction.Skip ? CollisionAction.Pending : choice.Collision
        });
        changePlanFolder.Click += async (_, _) => await SetPlanFolderAsync();
        resolveSelected.Click += async (_, _) => await ResolveConflictsAsync(SelectedPlanRows());
        resolveAll.Click += async (_, _) => await ResolveConflictsAsync(planRows);
        return tab;
    }

    private static CheckBox CreateCriterion(string text, SortCriteria criterion) => new()
    {
        Text = text, AutoSize = true, Tag = criterion, Margin = new Padding(3, 6, 9, 4)
    };

    private SortRule CurrentRule() => new(planRuleName.Text.Trim(),
        planCriteria.Where(box => box.Checked).Aggregate(SortCriteria.None, (combined, box) => combined | (SortCriteria)box.Tag!)
            | (planCriteria[4].Checked && planFuzzyNames.Checked ? SortCriteria.FuzzyNames : SortCriteria.None),
        (SortDate)planDate.SelectedIndex, (MissingDate)planMissingDate.SelectedIndex, planPattern.Text.Trim());

    private string SortRulesPath => Path.Combine(store.DataFolder, "sort-rules.json");

    private void LoadSortRules()
    {
        try
        {
            savedRules = LocalJsonFile.Load(SortRulesPath, SortRulesJson.Default.SortRuleArray) ?? [];
            RefreshRuleNames("");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            planStatus.Text = $"Sortierregeln konnten nicht geladen werden · {error.Message}";
        }
    }

    private void SaveSortRule()
    {
        SortRule rule = CurrentRule();
        if (rule.Name.Length == 0 || rule.Criteria == SortCriteria.None
            || (rule.Criteria.HasFlag(SortCriteria.Pattern) && rule.Pattern.Length == 0))
        {
            planStatus.Text = "Gib einen Regelnamen und mindestens ein vollständiges Sortierkriterium an.";
            return;
        }

        SortRule[] updated = savedRules.Where(item => !InventoryPaths.Comparer.Equals(item.Name, rule.Name))
            .Append(rule).OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        try
        {
            LocalJsonFile.Save(SortRulesPath, updated, SortRulesJson.Default.SortRuleArray);
            savedRules = updated;
            RefreshRuleNames(rule.Name);
            planStatus.Text = $"Regel «{rule.Name}» gespeichert.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            planStatus.Text = $"Sortierregel konnte nicht gespeichert werden · {error.Message}";
        }
    }

    private void RefreshRuleNames(string name)
    {
        planRuleName.Items.Clear();
        planRuleName.Items.AddRange(savedRules.Select(rule => (object)rule.Name).ToArray());
        planRuleName.Text = name;
    }

    private void ApplyRule(SortRule rule)
    {
        foreach (CheckBox box in planCriteria)
            box.Checked = rule.Criteria.HasFlag((SortCriteria)box.Tag!);
        planFuzzyNames.Checked = rule.Criteria.HasFlag(SortCriteria.FuzzyNames);
        planDate.SelectedIndex = (int)rule.Date;
        planMissingDate.SelectedIndex = (int)rule.MissingDate;
        planPattern.Text = rule.Pattern;
    }

    private Task StartSortPreviewAsync()
    {
        IEnumerable<FileResult> source = planScope.SelectedIndex switch
        {
            1 => visibleFiles,
            2 => files.SelectedRows.Cast<DataGridViewRow>().Where(row => row.Index < visibleFiles.Count)
                .OrderBy(row => row.Index).Select(row => visibleFiles[row.Index]),
            _ => allFiles
        };
        HashSet<string> artifacts = CurrentRunArtifacts();
        FileEntry[] entries = source.Where(row => IsPresent(row) && !artifacts.Contains(row.File.Path)).Select(row => row.File)
            .DistinctBy(file => file.Path, InventoryPaths.Comparer)
            .OrderBy(file => file.Path, InventoryPaths.Comparer).ToArray();
        if (entries.Length == 0)
        {
            planStatus.Text = "Im gewählten Bereich gibt es keine erreichbaren Dateien.";
            return Task.CompletedTask;
        }
        planChoices.Clear();
        activePlan = new SortPlanInput(entries, planDestination.Text.Trim(), CurrentRule(), planMode.SelectedIndex == 1);
        return RefreshSortPreviewAsync();
    }

    private async Task RefreshSortPreviewAsync()
    {
        if (activePlan is not { } request || buildingPlan)
            return;
        buildingPlan = true;
        var choices = new Dictionary<string, PlanChoice>(planChoices, InventoryPaths.Comparer);
        var selected = SelectedPlanRows().Select(row => row.Source.Path).ToHashSet(InventoryPaths.Comparer);
        planStatus.Text = "Vorschau wird erstellt …";
        UpdateSortPlanActions();

        try
        {
            PlanRow[] result = await Task.Run(() => SortPlanning.Create(request.Files, request.Destination,
                request.Rule, request.Move, choices));
            if (activePlan != request || IsDisposed)
                return;
            planRows = result;
            planGrid.Rows.Clear();
            foreach (PlanRow row in result)
            {
                int index = planGrid.Rows.Add(row.Source.Name, Path.GetFileName(row.Target), Path.GetDirectoryName(row.Target) ?? "",
                    row.Group, row.Action, row.Details);
                DataGridViewRow item = planGrid.Rows[index];
                item.Tag = row;
                item.Cells[0].ToolTipText = $"{row.Source.Path}\n{InventoryLabels.Size(row.Source.Bytes)} · geändert {row.Source.ModifiedUtc.ToLocalTime():g}";
                item.Cells[1].ToolTipText = row.Target;
                item.Cells[2].ToolTipText = row.Target;
                item.Cells[5].ToolTipText = row.Details;
                if (row.Blocked)
                    item.Cells[4].Style.ForeColor = Color.Maroon;
            }
            planGrid.ClearSelection();
            foreach (DataGridViewRow item in planGrid.Rows)
            {
                if (item.Tag is PlanRow row && selected.Contains(row.Source.Path))
                    item.Selected = true;
            }
            int blocked = result.Count(row => row.Blocked);
            int excluded = result.Count(row => row.Excluded);
            planStatus.Text = $"Vorschau · {result.Length - blocked - excluded:N0} geplant · {blocked:N0} offen oder blockiert · {excluded:N0} übersprungen";
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            if (activePlan == request && !IsDisposed)
            {
                ClearSortPlan();
                planStatus.Text = error.Message;
            }
        }
        finally
        {
            buildingPlan = false;
            if (!IsDisposed)
                UpdateSortPlanActions();
        }
    }

    private PlanRow[] SelectedPlanRows() => planGrid.SelectedRows.Cast<DataGridViewRow>()
        .Select(row => row.Tag).OfType<PlanRow>().ToArray();

    private Task ChangePlanRowsAsync(IEnumerable<PlanRow> rows, Func<PlanChoice, PlanChoice> update)
    {
        foreach (PlanRow row in rows)
            planChoices[row.Source.Path] = update(planChoices.GetValueOrDefault(row.Source.Path) ?? new PlanChoice());
        return RefreshSortPreviewAsync();
    }

    private async Task SetPlanFolderAsync()
    {
        PlanRow[] selection = SelectedPlanRows();
        if (selection.Length == 0)
            return;
        using var dialog = new Form
        {
            Text = "Zielordner ändern", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(610, 86), FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, Font = Font
        };
        var folder = new TextBox
        {
            Location = new Point(12, 10), Width = 584,
            Text = Path.GetDirectoryName(selection[0].Target) ?? planDestination.Text
        };
        var ok = CreateButton("Übernehmen", primary: true);
        ok.Location = new Point(12, 44);
        ok.DialogResult = DialogResult.OK;
        dialog.AcceptButton = ok;
        dialog.Controls.AddRange([folder, ok]);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await ChangePlanRowsAsync(selection, choice => choice with
            {
                Folder = folder.Text.Trim(), Collision = CollisionAction.Pending
            });
    }

    private Task ResolveConflictsAsync(IEnumerable<PlanRow> rows)
    {
        var action = (CollisionAction)(resolveAction.SelectedIndex + 1);
        if (action == CollisionAction.Affixes && resolvePrefix.Text.Length == 0 && resolveSuffix.Text.Length == 0)
        {
            planStatus.Text = "Gib ein Präfix oder Suffix an.";
            return Task.CompletedTask;
        }
        string prefix = resolvePrefix.Text;
        string suffix = resolveSuffix.Text;
        return ChangePlanRowsAsync(rows.Where(row => row.Collision), choice => choice with
        {
            Collision = action, Prefix = prefix, Suffix = suffix
        });
    }

    private void ClearSortPlan()
    {
        activePlan = null;
        planChoices.Clear();
        planRows = [];
        planGrid.Rows.Clear();
        planStatus.Text = "Wähle Ziel und Sortierkriterien für die Vorschau.";
        UpdateSortPlanActions();
    }

    private void UpdateSortPlanActions()
    {
        bool usable = !buildingPlan && !fileRunBusy && activePlan is not null;
        PlanRow[] selected = usable ? SelectedPlanRows() : [];
        previewPlan.Enabled = !buildingPlan && !fileRunBusy && scanning is null && allFiles.Any(IsPresent);
        executePlan.Enabled = usable && scanning is null && duplicateRun is null && nameRun is null && planRows.Any(row => !row.Excluded);
        changePlanFolder.Enabled = selected.Any(row => !row.Excluded);
        excludeRows.Enabled = selected.Any(row => !row.Excluded);
        includeRows.Enabled = selected.Any(row => row.Excluded);
        resolveSelected.Enabled = selected.Any(row => row.Collision);
        resolveAll.Enabled = usable && planRows.Any(row => row.Collision);
        resolvePrefix.Enabled = resolveSuffix.Enabled = !buildingPlan && resolveAction.SelectedIndex == 3;
        planDate.Enabled = planMissingDate.Enabled = planCriteria[1].Checked;
        planPattern.Enabled = planCriteria[3].Checked;
        planFuzzyNames.Enabled = planCriteria[4].Checked;
    }
}

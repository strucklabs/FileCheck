using System.Text.Json;
using FilesizeChecker.Inventory;

namespace FilesizeChecker;

internal sealed partial class InventoryForm
{
    private readonly TabPage sortPlanTab = new("Sortierplan") { BackColor = Color.White };
    private readonly TextBox planDestination = new() { Width = 400, PlaceholderText = "Absoluter Zielordner" };
    private readonly ComboBox planScope = CreateDropdown(192);
    private readonly ComboBox planMode = CreateDropdown(112);
    private readonly CheckBox directDestination = new()
    {
        Text = "Direkt in den Zielordner", AutoSize = true, Margin = new Padding(3, 6, 9, 4)
    };
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
    private readonly Button resolveAll = CreateButton("Auf offene Konflikte anwenden");
    private readonly CheckBox showPlanIssues = new()
    {
        Text = "Offen oder blockiert (0)", AutoSize = true, Margin = new Padding(10, 6, 6, 3)
    };
    private readonly DataGridView planGrid = CreateGrid();
    private readonly Label planStatus = CreateLabel("Wähle Ziel und Sortierkriterien für die Vorschau.", Muted);
    private readonly Dictionary<string, PlanChoice> planChoices = new(InventoryPaths.Comparer);
    private SortRule[] savedRules = [];
    private PlanRow[] planRows = [];
    private SortPlanInput? activePlan;
    private bool buildingPlan;

    private sealed record SortPlanInput(FileEntry[] Files, string Destination, SortRule Rule, bool Move,
        Dictionary<string, string>? CollectionEvidence);

    private TabPage BuildSortPlanTab()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new Padding(10, 7, 10, 5)
        };
        for (int row = 0; row < 6; row++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        planScope.Items.AddRange(["Gesamtes Inventar", "Gefiltertes Inventar", "Markierte Inventarzeilen", "Gesammelte Dateien"]);
        planScope.SelectedIndex = 0;
        planMode.Items.AddRange(["Kopieren", "Verschieben"]);
        planMode.SelectedIndex = 0;
        var browse = CreateButton("Ordner wählen …");
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog
            {
                Description = "Zielordner wählen",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };
            if (picker.ShowDialog(this) == DialogResult.OK)
                planDestination.Text = picker.SelectedPath;
        };
        layout.Controls.Add(Flow(planDestination, browse, planScope, planMode), 0, 0);
        planCriteria[0].Checked = true;
        layout.Controls.Add(Flow([directDestination, CreateLabel("Unterordner nach", Muted), .. planCriteria]), 0, 1);
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
        layout.Controls.Add(Flow(excludeRows, includeRows, changePlanFolder, showPlanIssues), 0, 4);

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
        AddColumn(planGrid, "Herkunft und Befunde", 165, fill: true);
        planGrid.Columns[6].Visible = false;
        layout.Controls.Add(planGrid, 0, 6);
        layout.Controls.Add(BuildSortConflictDetails(), 0, 7);
        planStatus.MaximumSize = new Size(950, 0);
        layout.Controls.Add(planStatus, 0, 8);
        sortPlanTab.Controls.Add(layout);

        planDestination.TextChanged += (_, _) => ClearSortPlan();
        planScope.SelectedIndexChanged += (_, _) => ClearSortPlan();
        planMode.SelectedIndexChanged += (_, _) => ClearSortPlan();
        directDestination.CheckedChanged += (_, _) => ClearSortPlan();
        planDate.SelectedIndexChanged += (_, _) => ClearSortPlan();
        planMissingDate.SelectedIndexChanged += (_, _) => ClearSortPlan();
        planPattern.TextChanged += (_, _) => ClearSortPlan();
        planFuzzyNames.CheckedChanged += (_, _) => ClearSortPlan();
        foreach (CheckBox criterion in planCriteria)
            criterion.CheckedChanged += (_, _) => ClearSortPlan();
        resolveAction.SelectedIndexChanged += (_, _) => UpdateSortPlanActions();
        planGrid.SelectionChanged += (_, _) => UpdateSortPlanActions();
        showPlanIssues.CheckedChanged += (_, _) => RenderSortPlan();
        previewPlan.Click += async (_, _) => await StartSortPreviewAsync();
        executePlan.Click += async (_, _) => await ApprovePlanAsync();
        excludeRows.Click += async (_, _) => await ChangePlanRowsAsync(SelectedPlanRows(), choice => choice with { Excluded = true });
        includeRows.Click += async (_, _) => await ChangePlanRowsAsync(SelectedPlanRows(), choice => choice with
        {
            Excluded = false,
            Collision = choice.Collision == CollisionAction.Skip ? CollisionAction.Pending : choice.Collision,
            ConflictTarget = choice.Collision == CollisionAction.Skip ? null : choice.ConflictTarget
        });
        changePlanFolder.Click += async (_, _) => await SetPlanFolderAsync();
        resolveSelected.Click += async (_, _) => await ResolveConflictsAsync(SelectedPlanRows());
        resolveAll.Click += async (_, _) => await ResolveConflictsAsync(planRows.Where(row => row.IsOpenConflict));
        return sortPlanTab;
    }

    private static CheckBox CreateCriterion(string text, SortCriteria criterion) => new()
    {
        Text = text, AutoSize = true, Tag = criterion, Margin = new Padding(3, 6, 9, 4)
    };

    private SortRule CurrentRule() => new(planRuleName.Text.Trim(),
        directDestination.Checked ? SortCriteria.None : planCriteria.Where(box => box.Checked)
            .Aggregate(SortCriteria.None, (combined, box) => combined | (SortCriteria)box.Tag!)
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
        if (rule.Name.Length == 0)
        {
            planStatus.Text = "Gib einen Regelnamen an.";
            return;
        }
        if ((!directDestination.Checked && rule.Criteria == SortCriteria.None)
            || (rule.Criteria.HasFlag(SortCriteria.Pattern) && rule.Pattern.Length == 0))
        {
            planStatus.Text = "Wähle ein vollständiges Sortierkriterium oder die direkte Ablage.";
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
        directDestination.Checked = rule.Criteria == SortCriteria.None;
        foreach (CheckBox box in planCriteria)
            box.Checked = rule.Criteria.HasFlag((SortCriteria)box.Tag!);
        planFuzzyNames.Checked = rule.Criteria.HasFlag(SortCriteria.FuzzyNames);
        planDate.SelectedIndex = (int)rule.Date;
        planMissingDate.SelectedIndex = (int)rule.MissingDate;
        planPattern.Text = rule.Pattern;
    }

    private Task StartSortPreviewAsync()
    {
        if (planScope.SelectedIndex == CollectedPlanScope && importingExplorerFiles)
            return Task.CompletedTask;
        SortRule rule = CurrentRule();
        if (rule.Criteria == SortCriteria.None && !directDestination.Checked)
        {
            planStatus.Text = "Wähle ein Sortierkriterium oder «Direkt in den Zielordner».";
            return Task.CompletedTask;
        }
        IEnumerable<FileResult> inventorySource = planScope.SelectedIndex switch
        {
            1 => visibleFiles,
            2 => files.SelectedRows.Cast<DataGridViewRow>().Where(row => row.Index < visibleFiles.Count)
                .OrderBy(row => row.Index).Select(row => visibleFiles[row.Index]),
            _ => allFiles
        };
        CollectedFile[]? collection = planScope.SelectedIndex == CollectedPlanScope ? collectedFiles.Items.ToArray() : null;
        IEnumerable<FileEntry> source = collection is null ? inventorySource.Where(IsPresent).Select(row => row.File)
            : collection.Select(entry => entry.File);
        return SetSortPreviewSourceAsync(source, planDestination.Text.Trim(), rule, planMode.SelectedIndex == 1, collection);
    }

    private Task UpdateCollectedSortPlanAsync()
    {
        if (planScope.SelectedIndex != CollectedPlanScope || activePlan is not { CollectionEvidence: not null } request)
            return Task.CompletedTask;

        CollectedFile[] collection = collectedFiles.Items.ToArray();
        return SetSortPreviewSourceAsync(collection.Select(entry => entry.File), request.Destination, request.Rule, request.Move, collection);
    }

    private Task SetSortPreviewSourceAsync(IEnumerable<FileEntry> source, string destination, SortRule rule, bool move,
        CollectedFile[]? collection)
    {
        HashSet<string> artifacts = CurrentRunArtifacts();
        // Vorhandene Einträge beanspruchen ihre Zielnamen vor neu gesammelten Dateien.
        var previousOrder = (activePlan?.Files ?? []).Select((file, index) => (file.Path, index))
            .ToDictionary(item => item.Path, item => item.index, InventoryPaths.Comparer);
        FileEntry[] entries = source.Where(file => !artifacts.Contains(file.Path))
            .DistinctBy(file => file.Path, InventoryPaths.Comparer)
            .OrderBy(file => previousOrder.GetValueOrDefault(file.Path, int.MaxValue))
            .ThenBy(file => file.Path, InventoryPaths.Comparer).ToArray();
        if (entries.Length == 0)
        {
            ClearSortPlan();
            planStatus.Text = collection is { Length: 0 } ? "Die Sammlung ist leer."
                : "Im gewählten Bereich gibt es keine erreichbaren Dateien.";
            return Task.CompletedTask;
        }

        var currentPaths = entries.Select(file => file.Path).ToHashSet(InventoryPaths.Comparer);
        foreach (string path in planChoices.Keys.Where(path => !currentPaths.Contains(path)).ToArray())
            planChoices.Remove(path);
        var evidence = collection?.ToDictionary(entry => entry.File.Path, entry => entry.Summary, InventoryPaths.Comparer);
        activePlan = new SortPlanInput(entries, destination, rule, move, evidence);
        return RefreshSortPreviewAsync();
    }

    private async Task RefreshSortPreviewAsync()
    {
        if (activePlan is null || buildingPlan)
            return;
        buildingPlan = true;
        planStatus.Text = "Vorschau wird erstellt …";
        UpdateSortPlanActions();

        try
        {
            while (activePlan is { } request && !IsDisposed)
            {
                var choices = new Dictionary<string, PlanChoice>(planChoices, InventoryPaths.Comparer);
                try
                {
                    PlanRow[] result = await Task.Run(() => SortPlanning.Create(request.Files, request.Destination,
                        request.Rule, request.Move, choices));
                    if (activePlan != request || IsDisposed)
                        continue;
                    planRows = result;
                    RememberConflictChoices(result);
                    RenderSortPlan();
                }
                catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    if (activePlan != request || IsDisposed)
                        continue;
                    ClearSortPlan();
                    planStatus.Text = error.Message;
                }
                break;
            }
        }
        finally
        {
            buildingPlan = false;
            if (!IsDisposed)
                UpdateSortPlanActions();
        }
    }

    private void RememberConflictChoices(IEnumerable<PlanRow> result)
    {
        foreach (PlanRow row in result)
        {
            if (!planChoices.TryGetValue(row.Source.Path, out PlanChoice? choice) || choice.Collision == CollisionAction.Pending)
                continue;
            if (row.Replace)
                planChoices[row.Source.Path] = choice with { ExpectedReplacement = row.Replacement };
            else if (row.ConflictTarget is not null && !row.NeedsAttention && !row.Excluded
                && choice.Collision is CollisionAction.KeepBoth or CollisionAction.Affixes)
                planChoices[row.Source.Path] = choice with { ResolvedTarget = row.Target };
            else if (row.IsOpenConflict && choice.Collision == CollisionAction.Replace && choice.ExpectedReplacement is null)
                planChoices[row.Source.Path] = choice with { Collision = CollisionAction.Pending, ConflictTarget = null };
        }
    }

    private void RenderSortPlan()
    {
        if (activePlan is not { } request || planRows.Length == 0)
            return;
        var selected = SelectedPlanRows().Select(row => row.Source.Path).ToHashSet(InventoryPaths.Comparer);
        int blocked = planRows.Count(row => row.NeedsAttention);
        int excluded = planRows.Count(row => row.Excluded);
        planGrid.Rows.Clear();
        planGrid.Columns[6].Visible = request.CollectionEvidence is not null;
        showPlanIssues.Text = $"Offen oder blockiert ({blocked:N0})";
        foreach (PlanRow row in planRows.Where(row => !showPlanIssues.Checked || row.NeedsAttention))
        {
            string evidence = request.CollectionEvidence?.GetValueOrDefault(row.Source.Path) ?? "";
            int index = planGrid.Rows.Add(row.Source.Name, Path.GetFileName(row.Target), Path.GetDirectoryName(row.Target) ?? "",
                row.Group, row.Action, row.Details, evidence);
            DataGridViewRow item = planGrid.Rows[index];
            item.Tag = row;
            item.Cells[0].ToolTipText = $"{row.Source.Path}\n{InventoryLabels.Size(row.Source.Bytes)} · geändert {row.Source.ModifiedUtc.ToLocalTime():g}";
            item.Cells[1].ToolTipText = row.Target;
            item.Cells[2].ToolTipText = row.Target;
            item.Cells[5].ToolTipText = row.Details;
            item.Cells[6].ToolTipText = evidence;
            if (row.NeedsAttention)
                item.Cells[4].Style.ForeColor = Color.Maroon;
        }
        planGrid.ClearSelection();
        foreach (DataGridViewRow item in planGrid.Rows)
        {
            if (item.Tag is PlanRow row && selected.Contains(row.Source.Path))
                item.Selected = true;
        }
        string view = showPlanIssues.Checked ? " · Nur offene oder blockierte Zeilen sichtbar" : "";
        planStatus.Text = $"Vorschau · {planRows.Length - blocked - excluded:N0} geplant · {blocked:N0} offen oder blockiert · {excluded:N0} übersprungen{view}";
        UpdateSortPlanActions();
    }

    private PlanRow[] SelectedPlanRows() => planGrid.SelectedRows.Cast<DataGridViewRow>()
        .Select(row => row.Tag).OfType<PlanRow>().ToArray();

    private Task ChangePlanRowsAsync(IEnumerable<PlanRow> rows, Func<PlanChoice, PlanChoice> update)
        => ChangePlanRowsAsync(rows, (_, choice) => update(choice));

    private Task ChangePlanRowsAsync(IEnumerable<PlanRow> rows, Func<PlanRow, PlanChoice, PlanChoice> update)
    {
        foreach (PlanRow row in rows)
            planChoices[row.Source.Path] = update(row, planChoices.GetValueOrDefault(row.Source.Path) ?? new PlanChoice());
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
                Folder = folder.Text.Trim(), Collision = CollisionAction.Pending,
                ConflictTarget = null, ResolvedTarget = null, ExpectedReplacement = null
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
        return ChangePlanRowsAsync(rows.Where(row => row.Collision), (row, choice) => choice with
        {
            Collision = action, Prefix = prefix, Suffix = suffix, ConflictTarget = row.ConflictTarget,
            ResolvedTarget = null, ExpectedReplacement = action == CollisionAction.Replace ? row.Replacement : null
        });
    }

    private void ClearSortPlan()
    {
        activePlan = null;
        planChoices.Clear();
        planRows = [];
        planGrid.Rows.Clear();
        showPlanIssues.Checked = false;
        showPlanIssues.Text = "Offen oder blockiert (0)";
        planGrid.Columns[6].Visible = planScope.SelectedIndex == CollectedPlanScope;
        planStatus.Text = "Wähle Ziel und Sortierkriterien für die Vorschau.";
        UpdateSortPlanActions();
    }

    private void UpdateSortPlanActions()
    {
        bool usable = !buildingPlan && !fileRunBusy && activePlan is not null;
        PlanRow[] selected = usable ? SelectedPlanRows() : [];
        UpdateSortConflictDetails(selected);
        bool hasSource = planScope.SelectedIndex == CollectedPlanScope
            ? collectedFiles.Count > 0 && !importingExplorerFiles : allFiles.Any(IsPresent);
        previewPlan.Enabled = !buildingPlan && !fileRunBusy && scanning is null && hasSource;
        previewPlan.Text = activePlan is null ? "Vorschau erstellen" : "Vorschau aktualisieren";
        executePlan.Enabled = usable && scanning is null && duplicateRun is null && nameRun is null
            && (planScope.SelectedIndex != CollectedPlanScope || !importingExplorerFiles)
            && planRows.Any(row => !row.Excluded) && !planRows.Any(row => row.NeedsAttention);
        showPlanIssues.Enabled = usable && planRows.Length > 0;
        changePlanFolder.Enabled = selected.Any(row => !row.Excluded);
        excludeRows.Enabled = selected.Any(row => !row.Excluded);
        includeRows.Enabled = selected.Any(row => row.Excluded);
        resolveSelected.Enabled = selected.Any(row => row.Collision);
        resolveAll.Enabled = usable && planRows.Any(row => row.IsOpenConflict);
        resolvePrefix.Enabled = resolveSuffix.Enabled = !buildingPlan && resolveAction.SelectedIndex == 3;
        foreach (CheckBox criterion in planCriteria)
            criterion.Enabled = !directDestination.Checked;
        planDate.Enabled = planMissingDate.Enabled = !directDestination.Checked && planCriteria[1].Checked;
        planPattern.Enabled = !directDestination.Checked && planCriteria[3].Checked;
        planFuzzyNames.Enabled = !directDestination.Checked && planCriteria[4].Checked;
    }
}

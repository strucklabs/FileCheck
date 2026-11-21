using System.Text.Json;
using FilesizeChecker.Inventory;

namespace FilesizeChecker;

internal sealed partial class InventoryForm
{
    private readonly Button searchImageGroups = CreateButton("Bildgruppen suchen", primary: true);
    private readonly Button searchReferenceImage = CreateButton("Referenzbild suchen …");
    private readonly Button compareImages = CreateButton("Bilder direkt vergleichen …");
    private readonly Button selectImageFolders = CreateButton("Ordner auswählen …");
    private readonly Button stopImageSearch = CreateButton("Abbrechen");
    private readonly Button collectFilteredImages = CreateButton("Gefilterte Bilder sammeln");
    private readonly Button changeImageExclusion = CreateButton("Markierte Treffer ausschließen");
    private readonly CheckBox showExcludedImages = new()
    {
        Text = "Ausgeschlossene Treffer anzeigen", AutoSize = true, Margin = new Padding(8, 8, 3, 3)
    };
    private readonly NumericUpDown minSimilarImages = new()
    {
        Width = 68, Maximum = int.MaxValue, ThousandsSeparator = true, Margin = new Padding(3, 5, 10, 3),
        AccessibleName = "Mindestzahl visuell ähnlicher Treffer je Gruppe"
    };
    private readonly CheckBox[] imageFilters =
    [
        CreateFilterToggle("Referenzbilder", ImageRelation.Reference),
        CreateFilterToggle("Pixelgleich", ImageRelation.PixelEqual),
        CreateFilterToggle("Visuell ähnlich", ImageRelation.Similar),
        CreateFilterToggle("Kein Bildtreffer", ImageRelation.Different)
    ];
    private readonly CheckBox widerImageSearch = new() { Text = "Weitere ähnliche Treffer", AutoSize = true, Margin = new Padding(8, 8, 5, 3) };
    private readonly Label imageStatus = CreateLabel("Wähle eine Bildsuche oder Bilder für den Direktvergleich.", Muted);
    private readonly Label imageScopeStatus = CreateLabel("Alle Scan-Ordner", Muted);
    private readonly Label filteredImageStatus = CreateLabel("", Muted);
    private readonly FileDragGrid imageGroups = CreateGrid();
    private readonly FileDragGrid imageMembers = CreateGrid();
    private readonly DataGridView imageIssues = CreateGrid();
    private readonly TabPage imageIssueTab = new("Prüfhinweise");
    private readonly ImagePreviewStrip imagePreview = new();
    private string[]? selectedImageFolders;
    private TabPage imageComparisonTab = null!;
    private CancellationTokenSource? imageRun;
    private IReadOnlyList<ImageGroup> imageResultGroups = [];
    private HashSet<ImageRelation> activeImageFilters = [];
    private HashSet<ImageExclusion>? imageExclusions;
    private int filteredImageCount;
    private bool showingImageGroups;
    private bool showingDirectImageResults;
    private bool imageResultWider;
    private CheckBox NoImageMatchFilter => imageFilters.Single(filter => (ImageRelation)filter.Tag! == ImageRelation.Different);

    private TabPage BuildImageComparisonTab()
    {
        var tab = new TabPage("Bildvergleich") { BackColor = Color.White };
        imageComparisonTab = tab;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10, 8, 10, 8) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(Flow(searchImageGroups, searchReferenceImage, compareImages, stopImageSearch), 0, 0);
        layout.Controls.Add(Flow(selectImageFolders, widerImageSearch, imageScopeStatus), 0, 1);
        imageStatus.AutoSize = false;
        imageStatus.AutoEllipsis = true;
        imageStatus.Dock = DockStyle.Fill;
        imageStatus.TextAlign = ContentAlignment.MiddleLeft;
        imageStatus.Margin = new Padding(3, 0, 3, 0);
        layout.Controls.Add(imageStatus, 0, 2);

        imageGroups.MultiSelect = false;
        imageMembers.MultiSelect = true;
        AddColumn(imageGroups, "Referenzbild", 155, fill: true);
        AddColumn(imageGroups, "Dateien", 68);
        AddColumn(imageMembers, "Dateiname", 150, fill: true);
        AddColumn(imageMembers, "Format", 66);
        AddColumn(imageMembers, "Maße", 95);
        AddColumn(imageMembers, "Dateigröße", 94);
        AddColumn(imageMembers, "Befund", 120);
        AddColumn(imageIssues, "Dateipfad", 350, fill: true);
        AddColumn(imageIssues, "Hinweis", 350, fill: true);

        var results = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5 };
        results.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 29));
        results.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 71));
        results.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        results.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        results.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        results.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        results.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        var filters = Flow([CreateLabel("Befund", Muted), .. imageFilters, showExcludedImages]);
        NoImageMatchFilter.Visible = false;
        results.Controls.Add(filters, 0, 0);
        results.SetColumnSpan(filters, 2);
        var groupActions = Flow(CreateLabel("Mind. visuell ähnliche Treffer je Gruppe", Muted), minSimilarImages,
            collectFilteredImages, filteredImageStatus);
        results.Controls.Add(groupActions, 0, 1);
        results.SetColumnSpan(groupActions, 2);
        results.Controls.Add(imageGroups, 0, 2);
        results.Controls.Add(imageMembers, 1, 2);
        var imageActions = Flow(changeImageExclusion);
        results.Controls.Add(imageActions, 0, 3);
        results.SetColumnSpan(imageActions, 2);
        results.Controls.Add(imagePreview, 0, 4);
        results.SetColumnSpan(imagePreview, 2);

        var resultTab = new TabPage("Bildtreffer");
        resultTab.Controls.Add(results);
        imageIssueTab.Controls.Add(imageIssues);
        var resultTabs = new TabControl { Dock = DockStyle.Fill };
        resultTabs.TabPages.AddRange([resultTab, imageIssueTab]);
        layout.Controls.Add(resultTabs, 0, 3);
        tab.Controls.Add(layout);

        searchImageGroups.Click += async (_, _) => await SearchImagesAsync(ImageCandidates(), null, direct: false);
        searchReferenceImage.Click += async (_, _) => await PickImageComparisonAsync(direct: false);
        compareImages.Click += async (_, _) => await PickImageComparisonAsync(direct: true);
        selectImageFolders.Click += (_, _) => ChooseImageFolders();
        foreach (CheckBox filter in imageFilters)
        {
            filter.CheckedChanged += (_, _) =>
            {
                UpdateStatusFilterAppearance(filter);
                activeImageFilters = imageFilters.Where(item => item.Checked).Select(item => (ImageRelation)item.Tag!).ToHashSet();
                if (!showingImageGroups)
                    ApplyImageFilters();
            };
        }
        minSimilarImages.ValueChanged += (_, _) => ApplyImageFilters();
        showExcludedImages.CheckedChanged += (_, _) => ApplyImageFilters();
        changeImageExclusion.Click += (_, _) => ChangeSelectedImageExclusions();
        collectFilteredImages.Click += async (_, _) =>
        {
            if (!showExcludedImages.Checked && CanAcceptCollectionDrop && filteredImageCount > 0)
                await AddToCollectionAsync(ImageGroupsMeetingMinimum().SelectMany(ImageGroupItems).ToArray());
        };
        stopImageSearch.Click += (_, _) =>
        {
            imageRun?.Cancel();
            imageStatus.Text = "Abbruch angefordert · Laufende Bildprüfung wird beendet.";
            UpdateImageActions();
        };
        imageGroups.CurrentCellChanged += (_, _) =>
        {
            if (!showingImageGroups)
                ShowImageMembers();
        };
        imageMembers.CurrentCellChanged += (_, _) =>
        {
            if (!showingImageGroups)
                SelectImagePreview();
        };
        imageMembers.SelectionChanged += (_, _) => UpdateImageExclusionAction();
        imagePreview.ImageActivated += path =>
        {
            DataGridViewRow? row = imageMembers.Rows.Cast<DataGridViewRow>()
                .FirstOrDefault(row => row.Tag is ImageMatch match && InventoryPaths.Comparer.Equals(match.Image.File.Path, path));
            if (row is not null)
            {
                imageMembers.ClearSelection();
                imageMembers.CurrentCell = row.Cells[0];
                row.Selected = true;
            }
        };
        imagePreview.ReferenceRequested += SetImageReference;
        return tab;
    }

    private bool ImageJobAvailable => scanning is null && duplicateRun is null && nameRun is null && imageRun is null && !fileRunBusy;

    private void UpdateImageActions()
    {
        bool idle = ImageJobAvailable;
        bool hasInventory = allFiles.Any(result => IsPresent(result) && ImageComparison.Supports(result.File));
        searchImageGroups.Enabled = searchReferenceImage.Enabled = idle && hasInventory;
        compareImages.Enabled = idle;
        selectImageFolders.Enabled = idle && snapshot?.Roots.Count > 0;
        widerImageSearch.Enabled = idle;
        stopImageSearch.Enabled = imageRun?.IsCancellationRequested == false;
    }

    private FileEntry[] ImageCandidates()
    {
        HashSet<string> artifacts = CurrentRunArtifacts();
        return allFiles.Where(result => IsPresent(result) && ImageComparison.Supports(result.File)
                && !artifacts.Contains(result.File.Path)
                && (selectedImageFolders is null || selectedImageFolders.Any(folder => InventoryPaths.IsWithin(result.File.Folder, folder))))
            .Select(result => result.File).ToArray();
    }

    private void ChooseImageFolders()
    {
        string[] folders = snapshot?.Roots.Select(root => root.Root).Concat(allFiles
                .Where(result => IsPresent(result) && ImageComparison.Supports(result.File)).Select(result => result.File.Folder))
            .Distinct(InventoryPaths.Comparer).Order(InventoryPaths.Comparer).ToArray() ?? [];
        using var picker = new Form
        {
            Text = "Ordner für den Bildvergleich", StartPosition = FormStartPosition.CenterParent,
            Size = new Size(700, 410), MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Font
        };
        using var options = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true };
        foreach (string folder in folders)
            options.Items.Add(folder, selectedImageFolders?.Contains(folder, InventoryPaths.Comparer) == true);
        var accept = CreateButton("Auswahl übernehmen", primary: true);
        accept.DialogResult = DialogResult.OK;
        accept.Enabled = options.CheckedItems.Count > 0;
        options.ItemCheck += (_, args) => accept.Enabled = options.CheckedItems.Count
            + (args.NewValue == CheckState.Checked ? 1 : -1) > 0;
        var all = CreateButton("Alle Scan-Ordner");
        all.DialogResult = DialogResult.Yes;
        var cancelPicker = CreateButton("Abbrechen");
        cancelPicker.DialogResult = DialogResult.Cancel;
        picker.AcceptButton = accept;
        picker.CancelButton = cancelPicker;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(CreateLabel("Ausgewählte Ordner werden einschließlich ihrer Unterordner durchsucht.", Ink), 0, 0);
        layout.Controls.Add(options, 0, 1);
        layout.Controls.Add(Flow(accept, all, cancelPicker), 0, 2);
        picker.Controls.Add(layout);
        DialogResult result = picker.ShowDialog(this);
        if (result == DialogResult.OK)
            selectedImageFolders = options.CheckedItems.Cast<string>().ToArray();
        else if (result == DialogResult.Yes)
            selectedImageFolders = null;
        imageScopeStatus.Text = selectedImageFolders is null ? "Alle Scan-Ordner" : $"{selectedImageFolders.Length:N0} ausgewählte Ordner";
    }

    private async Task PickImageComparisonAsync(bool direct)
    {
        using var picker = new OpenFileDialog
        {
            Filter = ImageComparison.FileFilter, CheckFileExists = true, Multiselect = direct,
            Title = direct ? "Bilder für den Direktvergleich auswählen" : "Referenzbild auswählen"
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;
        string[] paths = picker.FileNames;
        bool explicitReference = !direct || paths.Length == 1;
        if (direct && paths.Length == 1)
        {
            picker.Title = "Weitere Bilder auswählen";
            picker.FileName = "";
            if (picker.ShowDialog(this) != DialogResult.OK)
                return;
            paths = paths.Concat(picker.FileNames).Distinct(InventoryPaths.Comparer).ToArray();
            if (paths.Length < 2)
            {
                imageStatus.Text = "Wähle mindestens zwei unterschiedliche Dateipfade.";
                return;
            }
        }

        try
        {
            FileEntry? reference = explicitReference ? FileVersions.ReadEntry(paths[0]) : null;
            FileEntry[] source = direct ? paths.Skip(explicitReference ? 1 : 0).Select(FileVersions.ReadEntry).ToArray() : ImageCandidates();
            await SearchImagesAsync(source, reference, direct);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            imageStatus.Text = $"Datei konnte nicht gelesen werden · {error.Message}";
        }
    }

    private async Task SearchImagesAsync(FileEntry[] source, FileEntry? reference, bool direct)
    {
        if (!ImageJobAvailable)
            return;
        ClearImageResults(preserveDirectFilter: direct);
        if (source.Length == 0)
        {
            imageStatus.Text = "Keine unterstützten Bilder im ausgewählten Bestand.";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        imageRun = cancellation;
        bool wider = widerImageSearch.Checked;
        imageStatus.Text = "Bildvergleich läuft …";
        UpdateActions();
        var progress = new Progress<ImageProgress>(update =>
        {
            if (imageRun == cancellation && !cancellation.IsCancellationRequested)
                imageStatus.Text = $"{update.Phase} · {update.Done:N0} von {update.Total:N0}";
        });
        try
        {
            var comparison = new ImageComparison(Path.Combine(store.DataFolder, "image-features-v1.json"));
            ImageSearchResult result = await Task.Run(() => comparison.Run(source, reference, direct, wider, progress, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            imageResultWider = wider;
            RenderImageResults(result, direct, reference is not null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            imageStatus.Text = "Bildvergleich abgebrochen.";
        }
        finally
        {
            imageRun = null;
            UpdateActions();
        }
    }

    private void ClearImageResults(bool preserveDirectFilter = false)
    {
        imageResultGroups = [];
        showingDirectImageResults = false;
        filteredImageCount = 0;
        filteredImageStatus.Text = "";
        UpdateImageCollectionAction();
        showingImageGroups = true;
        NoImageMatchFilter.Visible = false;
        if (!preserveDirectFilter)
            NoImageMatchFilter.Checked = false;
        imageGroups.Rows.Clear();
        imageMembers.Rows.Clear();
        showingImageGroups = false;
        UpdateImageExclusionAction();
        imageIssues.Rows.Clear();
        imageIssueTab.Text = "Prüfhinweise";
        imageStatus.Text = "Wähle eine Bildsuche oder Bilder für den Direktvergleich.";
        imagePreview.SetItems([]);
        minSimilarImages.Enabled = !showExcludedImages.Checked;
    }

    private void RenderImageResults(ImageSearchResult result, bool direct, bool referenceSearch)
    {
        ScanIssue? exclusionIssue = LoadImageExclusions();
        imageResultGroups = result.Groups;
        showingDirectImageResults = direct;
        NoImageMatchFilter.Visible = direct;
        ApplyImageFilters();
        foreach (ScanIssue issue in result.Issues)
            imageIssues.Rows.Add(issue.Path, issue.Reason);
        if (exclusionIssue is not null)
            imageIssues.Rows.Add(exclusionIssue.Path, exclusionIssue.Reason);
        imageIssueTab.Text = $"Prüfhinweise ({imageIssues.Rows.Count:N0})";
        List<ImageMatch>? comparisons = result.Groups.FirstOrDefault()?.Matches;
        string summary = direct ? comparisons is { Count: 1 } ? ImageComparison.RelationLabel(comparisons[0].Relation)
            : $"{comparisons?.Count ?? 0:N0} Vergleiche mit dem Referenzbild"
            : referenceSearch ? $"{comparisons?.Count ?? 0:N0} Treffer zum Referenzbild"
            : $"{result.Groups.Count:N0} Bildgruppen";
        imageStatus.Text = $"{summary} · {result.Examined:N0} Bilder geprüft · {result.Reused:N0} Merkmale wiederverwendet";
    }

    private void ApplyImageFilters(ImageGroup? preferredGroup = null, string? preferredImagePath = null)
    {
        var selectedGroup = preferredGroup ?? imageGroups.CurrentRow?.Tag as ImageGroup;
        var selectedMember = imageMembers.CurrentRow?.Tag as ImageMatch;
        int selectedRow = -1;
        filteredImageCount = 0;
        showingImageGroups = true;
        imageGroups.Rows.Clear();
        foreach (ImageGroup group in ImageGroupsMeetingMinimum())
        {
            int count = FilteredImageMembers(group).Count();
            if (count == 0 && (!showingDirectImageResults || showExcludedImages.Checked))
                continue;
            filteredImageCount += count;
            int row = imageGroups.Rows.Add(group.Reference.File.Name, count.ToString("N0"));
            imageGroups.Rows[row].Tag = group;
            imageGroups.Rows[row].Cells[0].ToolTipText = group.Reference.File.Path;
            if (ReferenceEquals(group, selectedGroup))
                selectedRow = row;
        }
        if (selectedRow >= 0)
            imageGroups.CurrentCell = imageGroups.Rows[selectedRow].Cells[0];
        showingImageGroups = false;
        ShowImageMembers(selectedRow >= 0 ? preferredImagePath ?? selectedMember?.Image.File.Path : null);
        filteredImageStatus.Text = imageResultGroups.Count == 0 ? "" : (filteredImageCount, showExcludedImages.Checked) switch
        {
            (0, true) => "Keine ausgeschlossenen Treffer",
            (_, true) => $"{filteredImageCount:N0} ausgeschlossene Treffer",
            (0, false) => "Keine Bilder für diesen Filter",
            (1, false) => "1 Bild in der Auswahl",
            _ => $"{filteredImageCount:N0} Bilder in der Auswahl"
        };
        foreach (CheckBox filter in imageFilters)
            filter.Enabled = !showExcludedImages.Checked;
        minSimilarImages.Enabled = !showExcludedImages.Checked && !showingDirectImageResults;
        UpdateImageCollectionAction();
    }

    private void UpdateImageCollectionAction() =>
        collectFilteredImages.Enabled = !showExcludedImages.Checked && filteredImageCount > 0 && CanAcceptCollectionDrop;

    private IEnumerable<ImageGroup> ImageGroupsMeetingMinimum()
    {
        int minimum = (int)minSimilarImages.Value;
        return minimum == 0 || showExcludedImages.Checked || showingDirectImageResults ? imageResultGroups
            : imageResultGroups.Where(group => group.Matches.Count(match => match.Relation == ImageRelation.Similar
                && !IsImageExcluded(group, match)) >= minimum);
    }

    private IEnumerable<ImageMatch> FilteredImageMembers(ImageGroup group)
    {
        if (!showExcludedImages.Checked && ImageFilterIncludes(ImageRelation.Reference))
            yield return new ImageMatch(group.Reference, ImageRelation.Reference);
        foreach (ImageMatch match in group.Matches)
        {
            bool excluded = IsImageExcluded(group, match);
            if (showExcludedImages.Checked ? excluded : !excluded
                && (showingDirectImageResults || match.Relation != ImageRelation.Different) && ImageFilterIncludes(match.Relation))
                yield return match;
        }
    }

    private bool ImageFilterIncludes(ImageRelation relation) => activeImageFilters.Count == 0 || activeImageFilters.Contains(relation);

    private void ShowImageMembers(string? preferredPath = null)
    {
        showingImageGroups = true;
        imageMembers.Rows.Clear();
        if (imageGroups.CurrentRow?.Tag is ImageGroup group)
        {
            int selected = -1;
            foreach (ImageMatch match in FilteredImageMembers(group))
            {
                int row = AddImageMember(match, showExcludedImages.Checked ? "Ausgeschlossen"
                    : ImageComparison.RelationLabel(match.Relation));
                if (InventoryPaths.Comparer.Equals(match.Image.File.Path, preferredPath))
                    selected = row;
            }
            if (selected < 0 && ImageFilterIncludes(ImageRelation.Reference) && imageMembers.Rows.Count > 1)
                selected = 1;
            if (selected >= 0)
                imageMembers.CurrentCell = imageMembers.Rows[selected].Cells[0];
        }
        showingImageGroups = false;
        UpdateImageExclusionAction();
        ShowImagePreviewStrip();
    }

    private int AddImageMember(ImageMatch match, string relation)
    {
        ComparedImage image = match.Image;
        int row = imageMembers.Rows.Add(image.File.Name, image.Features.Format,
            $"{image.Features.Width} × {image.Features.Height}", InventoryLabels.Size(image.File.Bytes), relation);
        imageMembers.Rows[row].Tag = match;
        imageMembers.Rows[row].Cells[0].ToolTipText = image.File.Path;
        imageMembers.Rows[row].Cells[4].ToolTipText = match.Relation switch
        {
            ImageRelation.PixelEqual => "Gleiche Bildmaße und Pixelprüfsumme nach Normalisierung.",
            ImageRelation.Similar => "Ähnliches Seitenverhältnis, Helligkeitsmuster und ähnliche Farben.",
            _ => relation
        };
        return row;
    }

    private void ShowImagePreviewStrip()
    {
        if (imageGroups.CurrentRow?.Tag is not ImageGroup group)
        {
            imagePreview.SetItems([]);
            return;
        }
        HashSet<string> visible = imageMembers.Rows.Cast<DataGridViewRow>().Select(row => row.Tag).OfType<ImageMatch>()
            .Select(match => match.Image.File.Path).ToHashSet(InventoryPaths.Comparer);
        var items = new List<ImagePreviewItem> { Item(group.Reference, "Referenz", canSetReference: false) };
        foreach (ImageMatch match in group.Matches)
        {
            if (showExcludedImages.Checked == IsImageExcluded(group, match))
                items.Add(Item(match.Image, showExcludedImages.Checked ? "Ausgeschlossen" : ImageComparison.RelationLabel(match.Relation),
                    canSetReference: !showExcludedImages.Checked));
        }
        imagePreview.SetItems(items.ToArray());
        SelectImagePreview();

        ImagePreviewItem Item(ComparedImage image, string label, bool canSetReference) =>
            new(image, $"{label} · {image.File.Name}", FormatImageDetails(image), !visible.Contains(image.File.Path), canSetReference);
    }

    private void SelectImagePreview() => imagePreview.SelectImage((imageMembers.CurrentRow?.Tag as ImageMatch)?.Image.File.Path);

    private static string FormatImageDetails(ComparedImage image)
    {
        ImageFeatures features = image.Features;
        var details = new List<string> { $"{features.Width} × {features.Height} px", features.Format, InventoryLabels.Size(image.File.Bytes) };
        if (features.Technical is { } technical)
        {
            details.Add(ImageComparison.CompressionLabel(technical.Compression));
            if (technical.HasAlpha)
                details.Add("Alphakanal");
            if (technical.OrientationAdjusted)
                details.Add("Ausrichtung korrigiert");
        }
        details.Add($"Geändert {image.File.ModifiedUtc.ToLocalTime():d}");
        return string.Join(" · ", details);
    }

    private void SetImageReference(string path)
    {
        if (showExcludedImages.Checked || imageGroups.CurrentRow?.Tag is not ImageGroup group
            || group.Matches.FirstOrDefault(match => InventoryPaths.Comparer.Equals(match.Image.File.Path, path)) is not { } selected)
            return;

        ImageGroup updated = ImageComparison.ChangeReference(group, selected.Image, imageResultWider);
        imageResultGroups = imageResultGroups.Select(item => ReferenceEquals(item, group) ? updated : item).ToArray();
        ApplyImageFilters(updated, selected.Image.File.Path);
        int matching = updated.Matches.Count(match => match.Relation != ImageRelation.Different && !IsImageExcluded(updated, match));
        imageStatus.Text = $"{updated.Reference.File.Name} ist das neue Referenzbild · {matching:N0} passende Bilder";
    }

    private void WireImageReferenceMenu()
    {
        ContextMenuStrip menu = imageMembers.ContextMenuStrip!;
        var setReference = new ToolStripMenuItem("Als Referenzbild verwenden");
        menu.Items.Add(setReference);
        int? clickedRow = null;
        string? referencePath = null;
        imageMembers.CellMouseDown += (_, args) =>
        {
            if (args.Button == MouseButtons.Right)
                clickedRow = args.RowIndex;
        };
        menu.Opening += (_, _) =>
        {
            int row = clickedRow ?? imageMembers.CurrentRow?.Index ?? -1;
            clickedRow = null;
            referencePath = !showExcludedImages.Checked && row >= 0 && row < imageMembers.Rows.Count
                && imageMembers.Rows[row].Tag is ImageMatch { Relation: not ImageRelation.Reference } match
                ? match.Image.File.Path : null;
            setReference.Visible = referencePath is not null;
        };
        setReference.Click += (_, _) =>
        {
            if (referencePath is string path)
                SetImageReference(path);
        };
    }

    private CollectionInput[] ImageGroupItems(ImageGroup? group) => group is null || showExcludedImages.Checked ? []
        : FilteredImageMembers(group).Select(ImageCollectionItem).ToArray();

    private CollectionInput[] ImageMemberItems(int[] selection) => showExcludedImages.Checked ? []
        : selection.Select(index => imageMembers.Rows[index].Tag).OfType<ImageMatch>().Select(ImageCollectionItem).ToArray();

    private string ImageExclusionPath => Path.Combine(store.DataFolder, "image-exclusions-v1.json");

    private ScanIssue? LoadImageExclusions()
    {
        if (imageExclusions is not null)
            return null;
        try
        {
            imageExclusions = (LocalJsonFile.Load(ImageExclusionPath, ImageExclusionsJson.Default.ImageExclusionArray) ?? [])
                .ToHashSet(ImageExclusion.Comparer);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ScanIssue(ImageExclusionPath, $"Ausschlüsse konnten nicht geladen werden. {error.Message}");
        }
    }

    private bool IsImageExcluded(ImageGroup group, ImageMatch match) => match.Relation == ImageRelation.Similar
        && imageExclusions?.Contains(ImageExclusion.For(group, match)) == true;

    private IEnumerable<ImageMatch> SelectedSimilarImages() => imageMembers.SelectedRows.Cast<DataGridViewRow>()
        .Select(row => row.Tag).OfType<ImageMatch>().Where(match => match.Relation == ImageRelation.Similar);

    private void UpdateImageExclusionAction()
    {
        changeImageExclusion.Text = showExcludedImages.Checked ? "Markierte Treffer wieder einblenden" : "Markierte Treffer ausschließen";
        changeImageExclusion.Enabled = !showingImageGroups && imageExclusions is not null && SelectedSimilarImages().Any();
    }

    private void ChangeSelectedImageExclusions()
    {
        if (imageExclusions is null || imageGroups.CurrentRow?.Tag is not ImageGroup group)
            return;
        ImageExclusion[] selected = SelectedSimilarImages().Select(match => ImageExclusion.For(group, match)).ToArray();
        if (selected.Length == 0)
            return;
        var updated = new HashSet<ImageExclusion>(imageExclusions, ImageExclusion.Comparer);
        if (showExcludedImages.Checked)
            updated.ExceptWith(selected);
        else
            updated.UnionWith(selected);
        try
        {
            LocalJsonFile.Save(ImageExclusionPath, updated.ToArray(), ImageExclusionsJson.Default.ImageExclusionArray);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            imageStatus.Text = $"Ausschlüsse konnten nicht gespeichert werden · {error.Message}";
            return;
        }
        imageExclusions = updated;
        ApplyImageFilters();
    }

    private static CollectionInput ImageCollectionItem(ImageMatch match) =>
        new(match.Image.File, $"Bildvergleich · {ImageComparison.RelationLabel(match.Relation)}");

    private void WireInventoryImageCommands()
    {
        ContextMenuStrip menu = files.ContextMenuStrip!;
        var useReference = new ToolStripMenuItem("Als Referenzbild suchen");
        var compareSelection = new ToolStripMenuItem("Markierte Bilder direkt vergleichen");
        menu.Items.AddRange([useReference, compareSelection]);
        FileEntry[] selected = [];
        menu.Opening += (_, _) =>
        {
            FileResult[] rows = files.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Index).Order()
                .Where(index => index >= 0 && index < visibleFiles.Count).Select(index => visibleFiles[index]).ToArray();
            selected = rows.Select(result => result.File).ToArray();
            bool available = ImageJobAvailable && rows.All(IsPresent) && selected.All(ImageComparison.Supports);
            useReference.Visible = available && selected.Length == 1;
            compareSelection.Visible = available && selected.Length >= 2;
        };
        useReference.Click += async (_, _) =>
        {
            if (selected.Length == 1)
            {
                ((TabControl)imageComparisonTab.Parent!).SelectedTab = imageComparisonTab;
                await SearchImagesAsync(ImageCandidates(), selected[0], direct: false);
            }
        };
        compareSelection.Click += async (_, _) =>
        {
            if (selected.Length >= 2)
            {
                ((TabControl)imageComparisonTab.Parent!).SelectedTab = imageComparisonTab;
                await SearchImagesAsync(selected, reference: null, direct: true);
            }
        };
    }
}

using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using FilesizeChecker.Inventory;

namespace FilesizeChecker;

internal sealed partial class InventoryForm
{
    private const int VideoPreviewWidth = 192, VideoPreviewHeight = 108;
    private static readonly string VideoIdleText =
        $"Mögliche Duplikate mit gleicher Dateigröße und bis zu {VideoComparison.ToleranceSeconds} s Laufzeitunterschied suchen.";
    private readonly Button videoFind = CreateButton("Videogruppen suchen", primary: true);
    private readonly Button videoFolders = CreateButton("Ordner auswählen …");
    private readonly Button videoStop = CreateButton("Abbrechen");
    private readonly Button videoCheckExact = CreateButton("Exakte Kopien prüfen");
    private readonly Button videoCompareContent = CreateButton("Inhalt vergleichen");
    private readonly Button videoCollect = CreateButton("Gefilterte Videos sammeln");
    private readonly Button videoSetReference = CreateButton("Als Referenz verwenden");
    private readonly Button videoExclude = CreateButton("Markierte Treffer ausschließen");
    private readonly ComboBox videoFilter = CreateDropdown(148);
    private readonly CheckBox showExcludedVideos = new() { Text = "Ausgeschlossene Treffer anzeigen", AutoSize = true, Margin = new Padding(8, 8, 3, 3) };
    private readonly Label videoScope = CreateLabel("Alle Scan-Ordner", Muted);
    private readonly Label videoStatus = CreateLabel(VideoIdleText, Muted);
    private readonly FileDragGrid videoGroups = CreateGrid();
    private readonly FileDragGrid videoMembers = CreateGrid();
    private readonly DataGridView videoNotes = CreateGrid();
    private readonly TabPage videoNotesTab = new("Prüfhinweise");
    private readonly ImagePreviewStrip videoPreview = new("Als Referenzvideo verwenden", "Horizontal scrollbare Videovorschau");
    private VideoGroup[] videoResults = [];
    private HashSet<VideoPair>? videoExclusions;
    private CancellationTokenSource? videoRun;
    private string[]? videoScanFolders;
    private bool changingVideoRows;
    private enum VideoFilter { All, References, Matches }
    private VideoFilter ActiveVideoFilter => (VideoFilter)Math.Max(0, videoFilter.SelectedIndex);
    private VideoGroup? SelectedVideoGroup => videoGroups.CurrentRow?.Tag as VideoGroup;
    private VideoDuration? SelectedVideo => videoMembers.CurrentRow?.Tag as VideoDuration;

    private TabPage BuildVideoComparisonTab()
    {
        var tab = new TabPage("Videovergleich") { BackColor = Color.White };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10, 8, 10, 8) };
        for (int i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(Flow(videoFind, videoStop, videoCheckExact, videoCompareContent, videoCollect), 0, 0);
        layout.Controls.Add(Flow(videoFolders, videoScope), 0, 1);
        layout.Controls.Add(videoStatus, 0, 2);

        videoGroups.MultiSelect = false;
        videoMembers.MultiSelect = true;
        AddColumn(videoGroups, "Referenzvideo", 190, fill: true);
        AddColumn(videoGroups, "Videos", 65);
        AddColumn(videoMembers, "Dateiname", 190, fill: true);
        AddColumn(videoMembers, "Laufzeit", 118);
        AddColumn(videoMembers, "Dateigröße", 94);
        AddColumn(videoMembers, "Befund", 135);
        AddColumn(videoNotes, "Dateipfad", 250, fill: true);
        AddColumn(videoNotes, "Hinweis", 380, fill: true);

        videoFilter.Items.AddRange(["Alle Befunde", "Referenzen", "Mögliche Duplikate"]);
        videoFilter.SelectedIndex = 0;
        var results = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        results.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        results.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        results.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        results.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        results.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        var actions = Flow(videoFilter, showExcludedVideos, videoSetReference, videoExclude);
        results.Controls.Add(actions, 0, 0);
        results.SetColumnSpan(actions, 2);
        results.Controls.Add(videoGroups, 0, 1);
        results.Controls.Add(videoMembers, 1, 1);
        results.Controls.Add(videoPreview, 0, 2);
        results.SetColumnSpan(videoPreview, 2);
        var resultTab = new TabPage("Videotreffer");
        resultTab.Controls.Add(results);
        videoNotesTab.Controls.Add(videoNotes);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.AddRange([resultTab, videoNotesTab]);
        layout.Controls.Add(tabs, 0, 3);
        tab.Controls.Add(layout);

        videoFind.Click += async (_, _) => await SearchVideosAsync();
        videoCheckExact.Click += async (_, _) => await CheckSelectedVideoCopiesAsync();
        videoCompareContent.Click += (_, _) => ShowSelectedVideoComparison();
        videoFolders.Click += (_, _) => SelectVideoFolders();
        videoStop.Click += (_, _) =>
        {
            videoRun?.Cancel();
            videoStatus.Text = "Abbruch angefordert …";
            UpdateVideoActions();
        };
        videoFilter.SelectedIndexChanged += (_, _) => ShowVideoGroups();
        showExcludedVideos.CheckedChanged += (_, _) => ShowVideoGroups();
        videoGroups.CurrentCellChanged += (_, _) => { if (!changingVideoRows) ShowVideoMembers(); };
        videoMembers.CurrentCellChanged += (_, _) =>
        {
            if (changingVideoRows) return;
            videoPreview.SelectImage(SelectedVideo?.File.Path);
            UpdateVideoResultActions();
        };
        videoMembers.SelectionChanged += (_, _) => { if (!changingVideoRows) UpdateVideoResultActions(); };
        videoPreview.ImageActivated += path =>
        {
            foreach (DataGridViewRow row in videoMembers.Rows)
            {
                if (row.Tag is VideoDuration video && InventoryPaths.Comparer.Equals(video.File.Path, path))
                {
                    videoMembers.ClearSelection();
                    videoMembers.CurrentCell = row.Cells[0];
                    row.Selected = true;
                    break;
                }
            }
        };
        videoPreview.ReferenceRequested += SetVideoReference;
        videoSetReference.Click += (_, _) => { if (SelectedVideo is { } video) SetVideoReference(video.File.Path); };
        videoExclude.Click += (_, _) => ChangeVideoExclusions();
        videoCollect.Click += async (_, _) =>
        {
            if (CanAcceptCollectionDrop && !showExcludedVideos.Checked)
                await AddToCollectionAsync(videoResults.SelectMany(VideoGroupItems).ToArray());
        };
        return tab;
    }

    private void UpdateVideoActions()
    {
        bool available = ImageJobAvailable;
        videoFind.Enabled = available && allFiles.Any(result => IsPresent(result) && VideoComparison.Supports(result.File));
        videoFolders.Enabled = available && snapshot?.Roots.Count > 0;
        videoStop.Enabled = videoRun?.IsCancellationRequested == false;
        UpdateVideoResultActions();
    }

    private void UpdateVideoResultActions()
    {
        bool excluded = showExcludedVideos.Checked;
        bool canReview = ImageJobAvailable && videoMembers.SelectedRows.Count >= 2;
        videoCheckExact.Enabled = videoCompareContent.Enabled = canReview;
        videoFilter.Enabled = !excluded;
        videoCollect.Enabled = !excluded && CanAcceptCollectionDrop && videoGroups.Rows.Count > 0;
        videoSetReference.Enabled = !excluded && SelectedVideoGroup is { } group && SelectedVideo is { } selected
            && selected != group.Reference;
        videoExclude.Text = excluded ? "Markierte wieder einblenden" : "Markierte ausschließen";
        videoExclude.Enabled = videoExclusions is not null && SelectedVideoGroup is { } current
            && SelectedVideoMatches(current).Any();
    }

    private FileEntry[] VideoCandidates()
    {
        HashSet<string> artifacts = CurrentRunArtifacts();
        return allFiles.Where(row => IsPresent(row) && VideoComparison.Supports(row.File) && !artifacts.Contains(row.File.Path)
            && (videoScanFolders is null || videoScanFolders.Any(root => InventoryPaths.IsWithin(row.File.Path, root))))
            .Select(row => row.File).DistinctBy(file => file.Path, InventoryPaths.Comparer).ToArray();
    }

    private void SelectVideoFolders()
    {
        using var dialog = new Form { Text = "Scan-Ordner für den Videovergleich", Font = Font,
            StartPosition = FormStartPosition.CenterParent, Size = new Size(650, 350), ShowInTaskbar = false,
            MinimizeBox = false, MaximizeBox = false };
        var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true };
        foreach (RootInventory root in snapshot?.Roots ?? [])
            list.Items.Add(root.Root, videoScanFolders?.Contains(root.Root, InventoryPaths.Comparer) ?? true);
        var use = CreateButton("Auswahl übernehmen", primary: true);
        var cancel = CreateButton("Abbrechen");
        use.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        dialog.AcceptButton = use;
        dialog.CancelButton = cancel;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(list, 0, 0);
        layout.Controls.Add(Flow(use, cancel), 0, 1);
        dialog.Controls.Add(layout);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        videoScanFolders = list.CheckedItems.Count == list.Items.Count ? null : list.CheckedItems.Cast<string>().ToArray();
        videoScope.Text = videoScanFolders is null ? "Alle Scan-Ordner" : $"{videoScanFolders.Length:N0} ausgewählte Scan-Ordner";
    }


    private async Task SearchVideosAsync()
    {
        if (!ImageJobAvailable)
            return;
        ClearVideoResults();
        FileEntry[] source = VideoCandidates();
        if (source.Length == 0)
        {
            videoStatus.Text = "Keine MP4-Dateien in der Auswahl.";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        videoRun = cancellation;
        UpdateActions();
        videoStatus.Text = "Laufzeiten werden gelesen …";
        var watch = Stopwatch.StartNew();
        var progress = new Progress<int>(done =>
        {
            if (videoRun == cancellation && !cancellation.IsCancellationRequested)
                videoStatus.Text = $"Laufzeiten · {done:N0} von {source.Length:N0} · {watch.Elapsed.TotalSeconds:N0} s";
        });
        try
        {
            VideoResult result = await Task.Run(() => VideoComparison.Run(source, progress, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            videoResults = result.Groups;
            LoadVideoExclusions(result.Issues);
            foreach (ScanIssue issue in result.Issues)
                videoNotes.Rows.Add(issue.Path, issue.Reason);
            videoNotesTab.Text = $"Prüfhinweise ({result.Issues.Count:N0})";
            ShowVideoGroups();
            videoStatus.Text = $"{result.Checked:N0} Laufzeiten gelesen · {result.Groups.Length:N0} Gruppen"
                + $" · gleiche Dateigröße · bis {VideoComparison.ToleranceSeconds} s Laufzeitunterschied · {watch.Elapsed.TotalSeconds:N1} s";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { videoStatus.Text = "Laufzeitsuche abgebrochen."; }
        catch (Exception error) when (error is IOException or InvalidDataException or DllNotFoundException or EntryPointNotFoundException)
        {
            videoStatus.Text = $"Laufzeitsuche nicht verfügbar · {error.Message}";
        }
        finally { videoRun = null; UpdateActions(); }
    }


    private void ClearVideoResults()
    {
        videoResults = [];
        videoNotes.Rows.Clear();
        videoNotesTab.Text = "Prüfhinweise";
        videoStatus.Text = VideoIdleText;
        ShowVideoGroups();
    }

    private IReadOnlyList<VideoDuration> FilteredVideos(VideoGroup group)
    {
        var matches = group.Videos.Where(video => video != group.Reference && IsVideoExcluded(group, video) == showExcludedVideos.Checked).ToArray();
        if (showExcludedVideos.Checked || matches.Length == 0)
            return matches;

        return ActiveVideoFilter switch
        {
            VideoFilter.References => [group.Reference],
            VideoFilter.Matches => matches,
            _ => [group.Reference, .. matches]
        };
    }

    private void ShowVideoGroups(VideoGroup? preferred = null)
    {
        VideoGroup? selectedGroup = preferred ?? SelectedVideoGroup;
        changingVideoRows = true;
        videoGroups.Rows.Clear();
        foreach (VideoGroup group in videoResults)
        {
            int count = FilteredVideos(group).Count;
            if (count == 0)
                continue;
            int index = videoGroups.Rows.Add(group.Reference.File.Name, count.ToString("N0"));
            DataGridViewRow row = videoGroups.Rows[index];
            row.Tag = group;
            row.Cells[0].ToolTipText = group.Reference.File.Path;
            if (ReferenceEquals(group, selectedGroup))
                videoGroups.CurrentCell = row.Cells[0];
        }
        changingVideoRows = false;
        ShowVideoMembers();
    }

    private void ShowVideoMembers()
    {
        string? selectedPath = SelectedVideo?.File.Path;
        changingVideoRows = true;
        videoMembers.Rows.Clear();
        if (SelectedVideoGroup is not { } group)
        {
            videoPreview.SetPreviewItems([]);
        }
        else
        {
            IReadOnlyList<VideoDuration> shown = FilteredVideos(group);
            foreach (VideoDuration video in shown)
            {
                string label = VideoFinding(group, video);
                int index = videoMembers.Rows.Add(video.File.Name, VideoTime(video.Ticks), InventoryLabels.Size(video.File.Bytes), label);
                DataGridViewRow row = videoMembers.Rows[index];
                row.Tag = video;
                row.Cells[0].ToolTipText = video.File.Path;
                if (InventoryPaths.Comparer.Equals(video.File.Path, selectedPath))
                    videoMembers.CurrentCell = row.Cells[0];
            }
            var shownPaths = shown.Select(video => video.File.Path).ToHashSet(InventoryPaths.Comparer);
            var preview = group.Videos.Where(video => video != group.Reference && IsVideoExcluded(group, video) == showExcludedVideos.Checked)
                .Prepend(group.Reference).Select(video =>
                {
                    bool visible = shownPaths.Contains(video.File.Path);
                    return new PreviewStripItem(video, video.File.Path, $"{VideoFinding(group, video)} · {video.File.Name}",
                        $"{VideoTime(video.Ticks)} · {InventoryLabels.Size(video.File.Bytes)}", !visible,
                        visible && video != group.Reference && !showExcludedVideos.Checked,
                        token => PreviewBitmap(VideoComparison.ReadThumbnail(video, VideoPreviewWidth, VideoPreviewHeight, token)));
                }).ToArray();
            videoPreview.SetPreviewItems(preview);
            videoPreview.SelectImage(SelectedVideo?.File.Path);
        }

        changingVideoRows = false;
        UpdateVideoResultActions();
    }

    private string VideoFinding(VideoGroup group, VideoDuration video)
    {
        if (video == group.Reference)
            return "Referenz";
        if (IsVideoExcluded(group, video))
            return "Ausgeschlossen";
        return VideoReviewFinding(group.Reference, video) ?? "Mögliches Duplikat";
    }

    private void SetVideoReference(string path)
    {
        if (showExcludedVideos.Checked || SelectedVideoGroup is not { } group)
            return;
        VideoDuration? reference = group.Videos.FirstOrDefault(video => InventoryPaths.Comparer.Equals(video.File.Path, path));
        if (reference is null || reference == group.Reference || IsVideoExcluded(group, reference))
            return;
        VideoGroup updated = group with { Reference = reference };
        videoResults = videoResults.Select(item => ReferenceEquals(group, item) ? updated : item).ToArray();
        ShowVideoGroups(updated);
    }

    private string VideoExclusionPath => Path.Combine(store.DataFolder, "video-metadata-exclusions-v1.json");

    private void LoadVideoExclusions(List<ScanIssue> issues)
    {
        if (videoExclusions is not null)
            return;
        try
        {
            videoExclusions = (LocalJsonFile.Load(VideoExclusionPath, VideoExclusionsJson.Default.VideoPairArray) ?? []).ToHashSet();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            issues.Add(new ScanIssue(VideoExclusionPath, $"Ausschlüsse konnten nicht geladen werden. {error.Message}"));
        }
    }

    private bool IsVideoExcluded(VideoGroup group, VideoDuration video) =>
        video != group.Reference && videoExclusions is { Count: > 0 } && videoExclusions.Contains(VideoPair.For(group.Reference, video));

    private IEnumerable<VideoDuration> SelectedVideoMatches(VideoGroup group) =>
        videoMembers.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Tag).OfType<VideoDuration>()
            .Where(video => video != group.Reference);

    private void ChangeVideoExclusions()
    {
        if (videoExclusions is null || SelectedVideoGroup is not { } group)
            return;
        VideoPair[] pairs = SelectedVideoMatches(group).Select(video => VideoPair.For(group.Reference, video)).ToArray();
        if (pairs.Length == 0)
            return;
        var updated = new HashSet<VideoPair>(videoExclusions);
        if (showExcludedVideos.Checked)
            updated.ExceptWith(pairs);
        else
            updated.UnionWith(pairs);
        try
        {
            LocalJsonFile.Save(VideoExclusionPath, updated.ToArray(), VideoExclusionsJson.Default.VideoPairArray);
            videoExclusions = updated;
            ShowVideoGroups(group);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            videoStatus.Text = $"Ausschlüsse konnten nicht gespeichert werden · {error.Message}";
        }
    }

    private static Bitmap PreviewBitmap(byte[] pixels, int width = VideoPreviewWidth, int height = VideoPreviewHeight)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        try
        {
            BitmapData memory = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
            try
            {
                for (int row = 0; row < height; row++)
                    Marshal.Copy(pixels, row * width * 3, memory.Scan0 + row * memory.Stride, width * 3);
            }
            finally { bitmap.UnlockBits(memory); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static string VideoTime(long ticks)
    {
        var value = TimeSpan.FromTicks(ticks);
        string fraction = $"{ticks % TimeSpan.TicksPerSecond:0000000}".TrimEnd('0');
        return $"{ticks / TimeSpan.TicksPerHour:00}:{value.Minutes:00}:{value.Seconds:00}"
            + (fraction.Length == 0 ? "" : $".{fraction}");
    }


    private CollectionInput[] VideoGroupItems(VideoGroup? group) =>
        group is null || showExcludedVideos.Checked ? [] : FilteredVideos(group).Select(video => VideoCollectionItem(group, video)).ToArray();

    private CollectionInput[] VideoMemberItems(int[] indices) =>
        showExcludedVideos.Checked || SelectedVideoGroup is not { } group ? []
            : indices.Select(index => videoMembers.Rows[index].Tag).OfType<VideoDuration>()
                .Select(video => VideoCollectionItem(group, video)).ToArray();

    private CollectionInput VideoCollectionItem(VideoGroup group, VideoDuration video) =>
        new(video.File, $"Videovergleich · {VideoFinding(group, video)}");
}

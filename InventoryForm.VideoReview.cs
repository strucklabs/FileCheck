using System.ComponentModel;
using FilesizeChecker.Inventory;

namespace FilesizeChecker;

internal sealed partial class InventoryForm
{
    private const int ReviewFrameWidth = 384, ReviewFrameHeight = 216;
    private readonly Dictionary<VideoPair, VideoReviewDecision> videoReviewDecisions = [];
    private enum VideoReviewDecision { Open, Equal, Different }

    private VideoDuration[] SelectedVideos() => videoMembers.SelectedRows.Cast<DataGridViewRow>()
        .OrderBy(row => row.Index).Select(row => row.Tag).OfType<VideoDuration>().ToArray();

    private string? VideoReviewFinding(VideoDuration reference, VideoDuration video) =>
        videoReviewDecisions.GetValueOrDefault(VideoPair.For(reference, video)) switch
        {
            VideoReviewDecision.Equal => "Visuell als gleich bestätigt",
            VideoReviewDecision.Different => "Visuell als unterschiedlich bestätigt",
            _ => null
        };

    private async Task CheckSelectedVideoCopiesAsync()
    {
        VideoDuration[] selected = SelectedVideos();
        if (!ImageJobAvailable || selected.Length < 2)
            return;

        FileEntry[] files = selected.Select(video => video.File).ToArray();
        using var cancellation = new CancellationTokenSource();
        videoRun = cancellation;
        UpdateActions();
        videoStatus.Text = "Markierte Videos werden auf exakte Kopien geprüft …";
        try
        {
            FileEntry[] candidates = DuplicateSearch.FindCandidates(files);
            var service = new DuplicateSearch(Path.Combine(store.DataFolder, "duplicate-hashes.json"));
            var progress = new Progress<DuplicateProgress>(item =>
            {
                if (videoRun == cancellation && !cancellation.IsCancellationRequested)
                    videoStatus.Text = $"Exakte Kopien · {item.Phase} · {item.Processed:N0}/{item.Total:N0}";
            });
            DuplicateResult result = await Task.Run(() => service.Run(candidates, false, progress, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            videoStatus.Text = $"{result.Groups.Count:N0} Gruppen exakter Kopien · {result.Issues.Count:N0} Prüfhinweise";
            ShowVideoExactResults(files, result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            videoStatus.Text = "Prüfung auf exakte Kopien abgebrochen.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            videoStatus.Text = $"Exakte Kopien konnten nicht geprüft werden · {error.Message}";
        }
        finally { videoRun = null; UpdateActions(); }
    }

    private void ShowVideoExactResults(FileEntry[] files, DuplicateResult result)
    {
        using var dialog = CreateVideoReviewDialog("Exakte Kopien", new Size(850, 520));
        var grid = CreateGrid();
        grid.Name = "videoExactResults";
        AddColumn(grid, "Datei", 255, fill: true);
        AddColumn(grid, "Ergebnis", 270, fill: true);
        var reported = new HashSet<string>(InventoryPaths.Comparer);
        for (int i = 0; i < result.Groups.Count; i++)
        {
            foreach (DuplicateMember member in result.Groups[i].Files)
            {
                AddResult(member.File.Name, $"Bytegleiche Kopien · Gruppe {i + 1}", member.File.Path);
                reported.Add(member.File.Path);
            }
        }
        foreach (ScanIssue issue in result.Issues)
        {
            AddResult(Path.GetFileName(issue.Path), $"Prüfhinweis · {issue.Reason}", issue.Path);
            reported.Add(issue.Path);
        }
        foreach (FileEntry file in files.Where(file => !reported.Contains(file.Path)))
            AddResult(file.Name, "Kein bytegleicher Partner in der Auswahl", file.Path);

        var done = CreateButton("Schließen");
        done.DialogResult = DialogResult.OK;
        dialog.AcceptButton = dialog.CancelButton = done;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(CreateLabel($"{files.Length:N0} markierte Videos · {result.Groups.Count:N0} Gruppen exakter Kopien", Ink), 0, 0);
        layout.Controls.Add(grid, 0, 1);
        layout.Controls.Add(Flow(done), 0, 2);
        dialog.Controls.Add(layout);
        dialog.ShowDialog(this);

        void AddResult(string name, string finding, string path)
        {
            int index = grid.Rows.Add(name, finding);
            grid.Rows[index].Cells[0].ToolTipText = path;
            grid.Rows[index].Cells[1].ToolTipText = finding;
        }
    }

    private void ShowSelectedVideoComparison()
    {
        VideoDuration[] selected = SelectedVideos();
        if (!ImageJobAvailable || selected.Length < 2)
            return;

        VideoDuration reference = SelectedVideoGroup is { } group && selected.Contains(group.Reference)
            ? group.Reference : selected[0];
        ShowVideoContentComparison(reference, selected.Where(video => video != reference).ToArray());
        ShowVideoMembers();
        changingVideoRows = true;
        videoMembers.ClearSelection();
        var paths = selected.Select(video => video.File.Path).ToHashSet(InventoryPaths.Comparer);
        foreach (DataGridViewRow row in videoMembers.Rows)
            row.Selected = row.Tag is VideoDuration video && paths.Contains(video.File.Path);
        changingVideoRows = false;
        UpdateVideoResultActions();
    }

    private void ShowVideoContentComparison(VideoDuration reference, VideoDuration[] candidates)
    {
        const int cardWidth = 274, gap = 8, pitch = cardWidth + gap;
        using var tips = new ToolTip();
        using var dialog = CreateVideoReviewDialog("Videoinhalt vergleichen", new Size(1120, 760));
        using var cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        var referenceCard = new VideoReviewCard(reference, true, tips) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, gap, 0) };
        var viewport = new Panel { Name = "videoReviewViewport", Dock = DockStyle.Fill, Margin = new Padding(0) };
        var scroll = new HScrollBar
        {
            Name = "videoReviewScroll", AccessibleName = "Vergleichsvideos horizontal scrollen", Dock = DockStyle.Fill,
            SmallChange = pitch, Margin = new Padding(0)
        };
        var visible = new Dictionary<int, VideoReviewCard>();
        var recentFrames = new Dictionary<int, VideoFrame[]>();
        var frameOrder = new Queue<int>();
        var summary = CreateLabel("", Muted);
        var close = CreateButton("Schließen");
        close.DialogResult = DialogResult.Cancel;
        dialog.CancelButton = close;

        var preview = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 2 };
        preview.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, pitch));
        preview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        preview.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        preview.RowStyles.Add(new RowStyle(SizeType.Absolute, SystemInformation.HorizontalScrollBarHeight));
        preview.Controls.Add(referenceCard, 0, 0);
        preview.Controls.Add(viewport, 1, 0);
        preview.Controls.Add(scroll, 1, 1);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(CreateLabel("Jedes Vergleichsvideo einzeln gegen die Referenz beurteilen.", Ink), 0, 0);
        layout.Controls.Add(preview, 0, 1);
        layout.Controls.Add(Flow(close, summary), 0, 2);
        dialog.Controls.Add(layout);

        bool shown = false, loading = false;
        CancellationTokenSource? currentRequest = null;
        int loadingIndex = -1;
        int decided = candidates.Count(video => videoReviewDecisions.ContainsKey(VideoPair.For(reference, video)));
        UpdateSummary();

        scroll.ValueChanged += (_, _) => LayoutCards();
        viewport.SizeChanged += (_, _) => UpdateScrollbar();
        viewport.MouseWheel += (_, e) => scroll.Value = Math.Clamp(scroll.Value - e.Delta * pitch / 120,
            scroll.Minimum, Math.Max(scroll.Minimum, scroll.Maximum - scroll.LargeChange + 1));
        dialog.Shown += (_, _) => { shown = true; UpdateScrollbar(); };
        dialog.FormClosing += (_, _) => cancellation.Cancel();
        dialog.ShowDialog(this);
        cancellation.Cancel();

        void UpdateScrollbar()
        {
            if (!shown || token.IsCancellationRequested)
                return;
            int page = Math.Max(1, viewport.ClientSize.Width);
            int width = candidates.Length * pitch - gap;
            scroll.Maximum = Math.Max(0, width - 1);
            scroll.LargeChange = Math.Min(page, width);
            scroll.Visible = width > page;
            scroll.Value = Math.Min(scroll.Value, Math.Max(0, width - page));
            LayoutCards();
        }

        void LayoutCards()
        {
            if (!shown || token.IsCancellationRequested || viewport.ClientSize.Width <= 0)
                return;
            int offset = scroll.Value;
            int first = offset / pitch;
            int end = Math.Min(candidates.Length, (offset + viewport.ClientSize.Width + pitch - 1) / pitch);
            if (loadingIndex >= 0 && (loadingIndex < first || loadingIndex >= end))
                currentRequest?.Cancel();

            viewport.SuspendLayout();
            try
            {
                foreach (int index in visible.Keys.Where(index => index < first || index >= end).ToArray())
                {
                    VideoReviewCard card = visible[index];
                    visible.Remove(index);
                    card.Dispose();
                }
                for (int index = first; index < end; index++)
                {
                    if (!visible.TryGetValue(index, out VideoReviewCard? card))
                    {
                        VideoDuration video = candidates[index];
                        VideoPair key = VideoPair.For(reference, video);
                        card = new VideoReviewCard(video, false, tips) { Name = $"videoReviewCandidate{index}", TabIndex = index };
                        card.SetDecision(videoReviewDecisions.GetValueOrDefault(key));
                        card.DecisionChanged += decision => SetDecision(key, decision);
                        visible.Add(index, card);
                        viewport.Controls.Add(card);
                    }
                    card.SetBounds(index * pitch - offset, 0, cardWidth, viewport.ClientSize.Height);
                }
            }
            finally { viewport.ResumeLayout(); }
            if (!loading)
                _ = LoadVisibleAsync();
        }

        async Task LoadVisibleAsync()
        {
            loading = true;
            try
            {
                if (!referenceCard.Attempted)
                    await LoadCardAsync(referenceCard, token);
                while (!token.IsCancellationRequested)
                {
                    var next = visible.OrderBy(item => item.Key).FirstOrDefault(item => !item.Value.Attempted);
                    if (next.Value is not { } card)
                        break;
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
                    currentRequest = request;
                    loadingIndex = next.Key;
                    try { await LoadCardAsync(card, request.Token, next.Key); }
                    finally
                    {
                        loadingIndex = -1;
                        currentRequest = null;
                    }
                }
            }
            finally { loading = false; }
        }

        async Task LoadCardAsync(VideoReviewCard card, CancellationToken request, int index = -1)
        {
            card.Attempted = true;
            try
            {
                if (!recentFrames.TryGetValue(index, out VideoFrame[]? frames))
                {
                    frames = await Task.Run(() => VideoComparison.ReadReviewFrames(
                        card.Video, ReviewFrameWidth, ReviewFrameHeight, request), request);
                    request.ThrowIfCancellationRequested();
                    if (index >= 0)
                    {
                        recentFrames.Add(index, frames);
                        frameOrder.Enqueue(index);
                        if (frameOrder.Count > 12)
                            recentFrames.Remove(frameOrder.Dequeue());
                    }
                }
                request.ThrowIfCancellationRequested();
                card.ShowFrames(frames, referenceCard.Ready);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception error) when (IsVideoReviewError(error))
            {
                if (!request.IsCancellationRequested)
                    card.ShowError(error.Message);
            }
        }

        void SetDecision(VideoPair key, VideoReviewDecision decision)
        {
            bool wasDecided = videoReviewDecisions.ContainsKey(key);
            if (decision == VideoReviewDecision.Open)
            {
                if (videoReviewDecisions.Remove(key))
                    decided--;
            }
            else
            {
                videoReviewDecisions[key] = decision;
                if (!wasDecided)
                    decided++;
            }
            UpdateSummary();
        }

        void UpdateSummary() => summary.Text = $"{decided:N0} von {candidates.Length:N0} Vergleichen entschieden";
    }

    private sealed class VideoReviewCard : TableLayoutPanel
    {
        private static readonly string[] positions = ["Anfang", "Mitte", "Ende"];
        private readonly PictureBox[] pictures = new PictureBox[3];
        private readonly Label[] times = new Label[3];
        private readonly Label status = CreateLabel("Vorschau wird geladen …", Muted);
        private readonly ComboBox? choice;
        private readonly Label title;
        private readonly ToolTip tips;
        public VideoDuration Video { get; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Attempted { get; set; }
        public bool Ready { get; private set; }
        public event Action<VideoReviewDecision>? DecisionChanged;

        public VideoReviewCard(VideoDuration video, bool isReference, ToolTip tips)
        {
            Video = video;
            this.tips = tips;
            ColumnCount = 1;
            RowCount = 6;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            BackColor = Color.White;
            BorderStyle = BorderStyle.FixedSingle;
            Padding = new Padding(5);
            RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            for (int i = 0; i < positions.Length; i++)
                RowStyles.Add(new RowStyle(SizeType.Percent, 100f / positions.Length));
            RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            if (isReference)
                Name = "videoReviewReference";
            AccessibleName = isReference ? $"Referenzvideo {video.File.Name}" : video.File.Name;
            title = new Label
            {
                Dock = DockStyle.Fill, Text = isReference ? $"Referenz · {video.File.Name}" : video.File.Name,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = new Padding(4, 0, 2, 0),
                BackColor = isReference ? FileCheckTheme.SelectionBackground : Surface,
                ForeColor = isReference ? FileCheckTheme.SelectionForeground : Ink
            };
            tips.SetToolTip(title, video.File.Path);
            Controls.Add(title, 0, 0);
            for (int i = 0; i < positions.Length; i++)
            {
                var frame = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(0, 2, 0, 2) };
                frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                Label label = CreateLabel(positions[i], Ink);
                label.Margin = new Padding(3, 1, 3, 2);
                var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Surface };
                times[i] = label;
                pictures[i] = picture;
                frame.Controls.Add(label, 0, 0);
                frame.Controls.Add(picture, 0, 1);
                Controls.Add(frame, 0, i + 1);
            }
            status.AutoSize = false;
            status.AutoEllipsis = true;
            status.Dock = DockStyle.Fill;
            status.Margin = new Padding(2, 2, 2, 0);
            Controls.Add(status, 0, 4);

            if (!isReference)
            {
                choice = CreateDropdown(190);
                choice.Dock = DockStyle.Fill;
                choice.Name = "videoReviewDecision";
                choice.AccessibleName = $"Sichtentscheidung für {video.File.Name}";
                choice.Items.AddRange(["Offen", "Identisch", "Nicht identisch"]);
                choice.SelectedIndex = 0;
                choice.Enabled = false;
                choice.SelectedIndexChanged += (_, _) => DecisionChanged?.Invoke((VideoReviewDecision)choice.SelectedIndex);
                Controls.Add(choice, 0, 5);
            }
        }

        public void SetDecision(VideoReviewDecision value)
        {
            if (choice is not null)
                choice.SelectedIndex = (int)value;
        }

        public void ShowFrames(VideoFrame[] frames, bool referenceReady)
        {
            for (int i = 0; i < frames.Length; i++)
            {
                pictures[i].Image = PreviewBitmap(frames[i].Pixels, ReviewFrameWidth, ReviewFrameHeight);
                times[i].Text = $"{positions[i]} · {VideoTime(frames[i].Ticks)}";
            }
            Ready = true;
            status.Text = "";
            if (choice is not null)
                choice.Enabled = referenceReady;
        }

        public void ShowError(string error)
        {
            status.Text = "Vorschau nicht verfügbar";
            tips.SetToolTip(status, error);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed)
            {
                tips.SetToolTip(title, null);
                tips.SetToolTip(status, null);
                foreach (PictureBox? picture in pictures)
                    picture?.Image?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private Form CreateVideoReviewDialog(string title, Size preferred)
    {
        Size available = Screen.FromControl(this).WorkingArea.Size;
        return new Form
        {
            Text = title, Font = Font, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false,
            Size = new Size(Math.Min(preferred.Width, available.Width), Math.Min(preferred.Height, available.Height)),
            MinimizeBox = false, BackColor = Color.White
        };
    }

    private static bool IsVideoReviewError(Exception error) => error is IOException or UnauthorizedAccessException
        or InvalidDataException or DllNotFoundException or EntryPointNotFoundException or ArgumentException or OverflowException;
}

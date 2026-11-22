using System.Drawing.Imaging;
using FilesizeChecker.Inventory;

namespace FilesizeChecker;

internal sealed record ImagePreviewItem(ComparedImage Image, string Title, string Details, bool Muted, bool CanSetReference);
internal sealed record PreviewStripItem(object Source, string Path, string Title, string Details, bool Muted,
    bool CanSetReference, Func<CancellationToken, Bitmap> ReadThumbnail);

internal sealed class ImagePreviewStrip : Control
{
    private const int CardWidth = 220;
    private const int Gap = 8;
    private const int Step = CardWidth + Gap;
    private readonly HScrollBar scrollBar = new() { Dock = DockStyle.Bottom, Visible = false, SmallChange = Step };
    private readonly ContextMenuStrip previewMenu = FileCheckTheme.CreateContextMenu();
    private readonly SemaphoreSlim thumbnailGate = new(1, 1);
    private readonly Dictionary<int, Thumbnail> thumbnails = [];
    private PreviewStripItem[] items = [];
    private CancellationTokenSource? loading;
    private int selectedIndex = -1;
    private int offset;
    private string? referenceCandidate;

    private sealed record Thumbnail(Bitmap? Bitmap, string? Error);

    public event Action<string>? ImageActivated;
    public event Action<string>? ReferenceRequested;

    public ImagePreviewStrip(string referenceLabel = "Als Referenzbild verwenden", string accessibleName = "Horizontal scrollbare Bildvorschau")
    {
        Dock = DockStyle.Fill;
        MinimumSize = new Size(0, 110);
        DoubleBuffered = true;
        BackColor = SystemColors.Window;
        AccessibleName = accessibleName;
        Controls.Add(scrollBar);
        scrollBar.ValueChanged += (_, _) =>
        {
            offset = scrollBar.Value;
            QueueThumbnails();
            Invalidate();
        };
        var setReference = new ToolStripMenuItem(referenceLabel);
        previewMenu.Items.Add(setReference);
        previewMenu.Opening += (_, args) => args.Cancel = referenceCandidate is null;
        previewMenu.Closed += (_, _) => referenceCandidate = null;
        previewMenu.ItemClicked += (_, _) =>
        {
            if (referenceCandidate is string path)
                ReferenceRequested?.Invoke(path);
        };
        ContextMenuStrip = previewMenu;
    }

    public void SetItems(ImagePreviewItem[] value) =>
        SetPreviewItems(value.Select(item => new PreviewStripItem(item.Image, item.Image.File.Path, item.Title, item.Details,
            item.Muted, item.CanSetReference, token => ReadImageThumbnail(item.Image, token))).ToArray());

    public void SetPreviewItems(PreviewStripItem[] value)
    {
        bool sameFiles = items.Length == value.Length
            && items.Zip(value).All(pair => ReferenceEquals(pair.First.Source, pair.Second.Source));
        if (!sameFiles)
        {
            StopLoading();
            foreach (Thumbnail thumbnail in thumbnails.Values)
                thumbnail.Bitmap?.Dispose();
            thumbnails.Clear();
            selectedIndex = -1;
        }
        items = value;
        UpdateScrollbar(sameFiles ? offset : 0);
        QueueThumbnails();
        Invalidate();
    }

    public void SelectImage(string? path)
    {
        selectedIndex = Array.FindIndex(items, item => !item.Muted && InventoryPaths.Comparer.Equals(item.Path, path));
        if (selectedIndex >= 0)
        {
            int left = Gap + selectedIndex * Step;
            int right = left + CardWidth + Gap;
            if (left < offset || ClientSize.Width < CardWidth + Gap)
                SetOffset(left - Gap);
            else if (right > offset + ClientSize.Width)
                SetOffset(right - ClientSize.Width);
        }
        QueueThumbnails();
        Invalidate();
    }

    private Rectangle VisibleCard(int index) => new(Gap + index * Step - offset, 3,
        CardWidth, Math.Max(0, ClientSize.Height - (scrollBar.Visible ? scrollBar.Height : 0) - 7));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        (int start, int end) = VisibleRange();
        for (int index = start; index < end; index++)
            DrawCard(e.Graphics, index);
    }

    private void DrawCard(Graphics graphics, int index)
    {
        Rectangle bounds = VisibleCard(index);
        if (bounds.Height < 72)
            return;

        PreviewStripItem item = items[index];
        bool selected = index == selectedIndex;
        Color accent = FileCheckTheme.SelectionBackground;
        Color background = selected ? accent
            : item.Muted ? SystemColors.Control : SystemColors.Window;
        Color text = selected ? FileCheckTheme.SelectionForeground
            : item.Muted ? SystemColors.GrayText : SystemColors.WindowText;
        using var fill = new SolidBrush(background);
        using var border = new Pen(selected ? accent : SystemColors.ControlDark, selected ? 2 : 1);
        graphics.FillRectangle(fill, bounds);
        graphics.DrawRectangle(border, Rectangle.Inflate(bounds, -1, -1));

        var title = new Rectangle(bounds.X + 7, bounds.Y + 5, bounds.Width - 14, 20);
        var details = new Rectangle(title.X, title.Bottom, title.Width, Math.Min(50, bounds.Height / 2));
        TextRenderer.DrawText(graphics, item.Title, Font, title, text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(graphics, item.Details, Font, details, text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

        var area = new Rectangle(title.X, details.Bottom + 3, title.Width, bounds.Bottom - details.Bottom - 9);
        if (area.Height <= 0)
            return;
        if (thumbnails.TryGetValue(index, out Thumbnail? thumbnail) && thumbnail.Bitmap is { } bitmap)
        {
            double scale = Math.Min((double)area.Width / bitmap.Width, (double)area.Height / bitmap.Height);
            int width = Math.Max(1, (int)(bitmap.Width * scale));
            int height = Math.Max(1, (int)(bitmap.Height * scale));
            var target = new Rectangle(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
            if (item.Muted)
            {
                using var attributes = new ImageAttributes();
                attributes.SetColorMatrix(new ColorMatrix(
                [
                    [0.30f, 0.30f, 0.30f, 0f, 0f],
                    [0.59f, 0.59f, 0.59f, 0f, 0f],
                    [0.11f, 0.11f, 0.11f, 0f, 0f],
                    [0f, 0f, 0f, 0.42f, 0f],
                    [0f, 0f, 0f, 0f, 1f]
                ]));
                graphics.DrawImage(bitmap, target, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attributes);
            }
            else
            {
                graphics.DrawImage(bitmap, target);
            }
        }
        else
        {
            TextRenderer.DrawText(graphics, thumbnail?.Error ?? "Vorschau wird geladen …", Font, area, text,
                TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int index = Math.Max(0, (e.X + offset - Gap) / Step);
        PreviewStripItem? item = index < items.Length && VisibleCard(index).Contains(e.Location) ? items[index] : null;
        if (e.Button == MouseButtons.Right)
            referenceCandidate = item?.CanSetReference == true ? item.Path : null;
        if (e.Button == MouseButtons.Left && item is { Muted: false })
            ImageActivated?.Invoke(item.Path);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (scrollBar.Visible)
        {
            SetOffset(offset - e.Delta * Step / 120);
        }
        else
        {
            base.OnMouseWheel(e);
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateScrollbar(offset);
        QueueThumbnails();
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
            QueueThumbnails();
        else
            StopLoading();
    }

    private void UpdateScrollbar(int desiredOffset)
    {
        int totalWidth = items.Length == 0 ? 0 : Gap + items.Length * Step;
        int pageWidth = Math.Max(1, ClientSize.Width);
        scrollBar.Maximum = Math.Max(0, totalWidth - 1);
        scrollBar.LargeChange = pageWidth;
        scrollBar.Visible = totalWidth > pageWidth;
        SetOffset(desiredOffset);
    }

    private void SetOffset(int value) =>
        scrollBar.Value = Math.Clamp(value, 0, Math.Max(0, scrollBar.Maximum - scrollBar.LargeChange + 1));

    private (int Start, int End) VisibleRange() =>
        (Math.Min(items.Length, offset / Step),
            Math.Min(items.Length, (offset + ClientSize.Width + Step - 1) / Step));

    private void QueueThumbnails()
    {
        if (!IsHandleCreated || !Visible || IsDisposed || Disposing || items.Length == 0)
            return;
        _ = LoadVisibleThumbnailsAsync();
    }

    private void StopLoading()
    {
        CancellationTokenSource? pending = loading;
        loading = null;
        pending?.Cancel();
    }

    private async Task LoadVisibleThumbnailsAsync()
    {
        StopLoading();
        (int first, int last) = VisibleRange();
        foreach (int index in thumbnails.Keys.Where(index => index < first - 2 || index >= last + 2).ToArray())
        {
            thumbnails[index].Bitmap?.Dispose();
            thumbnails.Remove(index);
        }

        using var cancellation = new CancellationTokenSource();
        loading = cancellation;
        try
        {
            await Task.Delay(75, cancellation.Token);
            await thumbnailGate.WaitAsync(cancellation.Token);
            try
            {
                for (int index = first; index < last; index++)
                {
                    if (thumbnails.ContainsKey(index))
                        continue;
                    cancellation.Token.ThrowIfCancellationRequested();
                    PreviewStripItem item = items[index];
                    Thumbnail thumbnail = await Task.Run(() => ReadThumbnail(item, cancellation.Token));
                    if (cancellation.IsCancellationRequested)
                    {
                        thumbnail.Bitmap?.Dispose();
                        break;
                    }
                    thumbnails[index] = thumbnail;
                    Invalidate(VisibleCard(index));
                }
            }
            finally
            {
                thumbnailGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (loading == cancellation)
                loading = null;
        }
    }

    private static Bitmap ReadImageThumbnail(ComparedImage image, CancellationToken token)
    {
        byte[] preview = ImageComparison.ReadPreview(image, token);
        using var stream = new MemoryStream(preview);
        using var bitmap = Image.FromStream(stream);
        return new Bitmap(bitmap);
    }

    private static Thumbnail ReadThumbnail(PreviewStripItem item, CancellationToken token)
    {
        try
        {
            return new Thumbnail(item.ReadThumbnail(token), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException
            or DllNotFoundException or EntryPointNotFoundException or OverflowException)
        {
            return new Thumbnail(null, error.Message);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopLoading();
            previewMenu.Dispose();
            foreach (Thumbnail thumbnail in thumbnails.Values)
                thumbnail.Bitmap?.Dispose();
            thumbnails.Clear();
        }
        base.Dispose(disposing);
    }
}

using System.ComponentModel;
using FilesizeChecker.Inventory;

namespace FilesizeChecker;

internal sealed class FileDragGrid : DataGridView
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<int, int, int[], CollectionInput[]>? ResolveDrag { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Action<CollectionInput[]>? StartFileDrag { get; set; }

    private Point dragStart;
    private CollectionInput[]? pendingDrag;

    protected override void OnCellPainting(DataGridViewCellPaintingEventArgs e)
    {
        // WinForms highlights the current column in full-row selection without marking the header itself selected.
        if (!EnableHeadersVisualStyles && e.RowIndex == -1 && e.ColumnIndex >= 0
            && SelectionMode == DataGridViewSelectionMode.FullRowSelect
            && CurrentCell is { Selected: true } cell && cell.ColumnIndex == e.ColumnIndex
            && e.CellStyle is { } style)
            style.ForeColor = style.SelectionForeColor;

        base.OnCellPainting(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        pendingDrag = null;
        if (ResolveDrag is not null && e.Button == MouseButtons.Left)
        {
            HitTestInfo hit = HitTest(e.X, e.Y);
            if (hit.Type == DataGridViewHitTestType.Cell)
            {
                int[] selected = Rows[hit.RowIndex].Selected ? SelectedRows.Cast<DataGridViewRow>()
                    .Select(row => row.Index).Order().ToArray() : [hit.RowIndex];
                pendingDrag = ResolveDrag(hit.RowIndex, hit.ColumnIndex, selected);
                dragStart = e.Location;
            }
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && pendingDrag is { Length: > 0 })
        {
            Size threshold = SystemInformation.DragSize;
            var bounds = new Rectangle(dragStart.X - threshold.Width / 2, dragStart.Y - threshold.Height / 2,
                threshold.Width, threshold.Height);
            if (!bounds.Contains(e.Location))
            {
                CollectionInput[] drag = pendingDrag;
                pendingDrag = null;
                StartFileDrag?.Invoke(drag);
                return;
            }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        pendingDrag = null;
        base.OnMouseUp(e);
    }
}

namespace AIHubRouter.WinForms;

internal sealed class BufferedDataGridView : DataGridView
{
    private const int DividerGripWidth = 5;
    private const int MaximumManualColumnWidth = 2000;
    private bool _smoothRendering = true;
    private DataGridViewColumn? _resizeLeftColumn;
    private DataGridViewColumn? _resizeRightColumn;
    private int _resizeStartX;
    private int _resizeLeftStartWidth;
    private int _resizeRightStartWidth;

    public BufferedDataGridView()
    {
        ApplyRenderingStyle();
    }

    public void SetSmoothRendering(bool enabled)
    {
        if (_smoothRendering == enabled)
        {
            return;
        }

        _smoothRendering = enabled;
        ApplyRenderingStyle();
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        ApplyRenderingStyle();
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left &&
            TryGetColumnsAroundDivider(eventArgs.Location, out var leftColumn, out var rightColumn))
        {
            _resizeLeftColumn = leftColumn;
            _resizeRightColumn = rightColumn;
            _resizeStartX = eventArgs.X;
            _resizeLeftStartWidth = leftColumn.Width;
            _resizeRightStartWidth = rightColumn.Width;

            SetManualWidth(leftColumn, _resizeLeftStartWidth);
            SetManualWidth(rightColumn, _resizeRightStartWidth);
            Capture = true;
            Cursor = Cursors.VSplit;
            return;
        }

        base.OnMouseDown(eventArgs);
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        if (_resizeLeftColumn is not null && _resizeRightColumn is not null)
        {
            var requestedDelta = eventArgs.X - _resizeStartX;
            var minimumDelta = Math.Max(_resizeLeftColumn.MinimumWidth, 24) - _resizeLeftStartWidth;
            var maximumDelta = _resizeRightStartWidth - Math.Max(_resizeRightColumn.MinimumWidth, 24);
            var delta = Math.Clamp(requestedDelta, minimumDelta, maximumDelta);
            var leftWidth = Math.Min(_resizeLeftStartWidth + delta, MaximumManualColumnWidth);
            var rightWidth = Math.Min(_resizeRightStartWidth - delta, MaximumManualColumnWidth);

            _resizeLeftColumn.Width = leftWidth;
            _resizeRightColumn.Width = rightWidth;
            Cursor = Cursors.VSplit;
            return;
        }

        base.OnMouseMove(eventArgs);
        Cursor = TryGetColumnsAroundDivider(eventArgs.Location, out _, out _)
            ? Cursors.VSplit
            : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        if (_resizeLeftColumn is not null)
        {
            EndColumnResize();
            return;
        }

        base.OnMouseUp(eventArgs);
    }

    protected override void OnMouseCaptureChanged(EventArgs eventArgs)
    {
        base.OnMouseCaptureChanged(eventArgs);
        if (!Capture && _resizeLeftColumn is not null)
        {
            EndColumnResize();
        }
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        if (_resizeLeftColumn is null)
        {
            Cursor = Cursors.Default;
        }
    }

    private void ApplyRenderingStyle()
    {
        DoubleBuffered = _smoothRendering;
        ResizeRedraw = _smoothRendering;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, _smoothRendering);
        SetStyle(ControlStyles.AllPaintingInWmPaint, _smoothRendering);
        UpdateStyles();
    }

    private bool TryGetColumnsAroundDivider(
        Point location,
        out DataGridViewColumn leftColumn,
        out DataGridViewColumn rightColumn)
    {
        leftColumn = null!;
        rightColumn = null!;
        if (!AllowUserToResizeColumns || !ColumnHeadersVisible || location.Y < 0 || location.Y > ColumnHeadersHeight)
        {
            return false;
        }

        var visibleColumns = Columns
            .Cast<DataGridViewColumn>()
            .Where(column => column.Visible)
            .OrderBy(column => column.DisplayIndex)
            .ToArray();

        for (var index = 0; index < visibleColumns.Length - 1; index++)
        {
            var left = visibleColumns[index];
            var right = visibleColumns[index + 1];
            if (left.Resizable == DataGridViewTriState.False || right.Resizable == DataGridViewTriState.False)
            {
                continue;
            }

            var rectangle = GetColumnDisplayRectangle(left.Index, cutOverflow: true);
            if (rectangle.Width > 0 && Math.Abs(location.X - rectangle.Right) <= DividerGripWidth)
            {
                leftColumn = left;
                rightColumn = right;
                return true;
            }
        }

        return false;
    }

    private static void SetManualWidth(DataGridViewColumn column, int width)
    {
        if (column.AutoSizeMode != DataGridViewAutoSizeColumnMode.None)
        {
            column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        }

        column.Width = width;
    }

    private void EndColumnResize()
    {
        _resizeLeftColumn = null;
        _resizeRightColumn = null;
        Capture = false;
        Cursor = Cursors.Default;
        Invalidate();
    }
}

namespace FctAggregator;

public abstract class BoardCardBase : Panel
{
    public const string DragFormat = "FctAggregator.BoardCard";

    public abstract string ColumnKey { get; }

    public event Action<BoardCardBase>? ActivateRequested;

    public event Action<BoardCardBase>? PreviewRequested;

    public event Action<BoardCardBase, Point>? ContextRequested;

    protected static readonly ToolTip Tip = new() { AutoPopDelay = 15000, InitialDelay = 600, ReshowDelay = 200 };

    protected bool Hover { get; private set; }

    private bool _pressed;
    private bool _dragged;
    private bool _suppressClick;
    private Point _origin;
    private System.Windows.Forms.Timer? _clickTimer;

    internal bool PreviewPending => _clickTimer?.Enabled == true;

    protected BoardCardBase()
    {
        Height = 80;
        Margin = new Padding(4, 3, 4, 3);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public static BoardCardBase? FromDrag(DragEventArgs e)
    {
        try
        {
            if (e.Data == null || !e.Data.GetDataPresent(DragFormat)) return null;
            return e.Data.GetData(DragFormat) as BoardCardBase;
        }
        catch { return null; }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        Hover = true; Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        Hover = false; _pressed = false; Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        CancelPreviewTimer();
        if (e.Button == MouseButtons.Right)
        {
            _pressed = false;
            _suppressClick = true;
            Focus();
            ContextRequested?.Invoke(this, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        _dragged = false;
        _origin = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed || e.Button != MouseButtons.Left) return;
        var dz = SystemInformation.DragSize;
        if (Math.Abs(e.X - _origin.X) < dz.Width && Math.Abs(e.Y - _origin.Y) < dz.Height) return;
        _pressed = false;
        _dragged = true;
        var data = new DataObject();
        data.SetData(DragFormat, this);
        DoDragDrop(data, DragDropEffects.Move);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var wasPressed = _pressed;
        _pressed = false;

        if (_suppressClick) { _suppressClick = false; return; }
        if (e.Button != MouseButtons.Left || _dragged || !wasPressed) return;
        if (!ClientRectangle.Contains(e.Location)) return;

        _clickTimer ??= new System.Windows.Forms.Timer();
        _clickTimer.Interval = Math.Max(120, SystemInformation.DoubleClickTime + 20);
        _clickTimer.Tick -= OnClickTimerTick;
        _clickTimer.Tick += OnClickTimerTick;
        _clickTimer.Start();
    }

    private void OnClickTimerTick(object? sender, EventArgs e)
    {
        CancelPreviewTimer();
        PreviewRequested?.Invoke(this);
    }

    private void CancelPreviewTimer() => _clickTimer?.Stop();

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        CancelPreviewTimer();
        _suppressClick = true;
        if (e.Button == MouseButtons.Left) ActivateRequested?.Invoke(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _clickTimer != null)
        {
            _clickTimer.Stop();
            _clickTimer.Tick -= OnClickTimerTick;
            _clickTimer.Dispose();
            _clickTimer = null;
        }
        base.Dispose(disposing);
    }

    protected static string ShortTime(string? ts) => TimeUtil.Short(ts);
}

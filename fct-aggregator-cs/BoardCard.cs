namespace FctAggregator;

/// <summary>
/// 看板卡片基类。统一处理 hover / 起拖阈值 / 双击 / 右键，子类只负责 <see cref="Control.OnPaint"/>。
///
/// 看板上现在有两种卡片：
///   · <see cref="MaintenanceCard"/> —— 已入库的维修记录（可跨列拖动改状态）
///   · <see cref="TodoCard"/>        —— 尚未确认的不良（待办列专属，拖出去 = 确认并置为目标状态）
///
/// 拖拽数据统一用 <see cref="DragFormat"/> 这个自定义格式承载，
/// 否则 `GetData(typeof(MaintenanceCard))` 不会命中子类/兄弟类，拖放会静默失效。
/// </summary>
public abstract class BoardCardBase : Panel
{
    /// <summary>拖拽剪贴板格式名（进程内传对象引用）。</summary>
    public const string DragFormat = "FctAggregator.BoardCard";

    /// <summary>卡片当前所在列的状态 key。</summary>
    public abstract string ColumnKey { get; }

    /// <summary>双击卡片（维修记录=编辑 / 待办=确认问题）。</summary>
    public event Action<BoardCardBase>? ActivateRequested;

    /// <summary>右键卡片（参数为卡片内坐标）。</summary>
    public event Action<BoardCardBase, Point>? ContextRequested;

    /// <summary>全卡片共用一个 ToolTip —— 每张卡各建一个会产生上百个未释放的组件。</summary>
    protected static readonly ToolTip Tip = new() { AutoPopDelay = 15000, InitialDelay = 600, ReshowDelay = 200 };

    protected bool Hover { get; private set; }

    private bool _pressed;
    private Point _origin;

    protected BoardCardBase()
    {
        Height = 80;
        Margin = new Padding(4, 3, 4, 3);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    /// <summary>从拖拽事件里取回卡片对象（取不到返回 null）。</summary>
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
        // 卡片是自绘的 Panel, 不会自己跳右键菜单; 同时右键不能走拖拽分支
        if (e.Button == MouseButtons.Right)
        {
            _pressed = false;
            Focus();
            ContextRequested?.Invoke(this, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        _origin = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed || e.Button != MouseButtons.Left) return;
        // 必须过拖动阈值才起拖, 否则单击即触发拖拽、双击会失灵
        var dz = SystemInformation.DragSize;
        if (Math.Abs(e.X - _origin.X) < dz.Width && Math.Abs(e.Y - _origin.Y) < dz.Height) return;
        _pressed = false;
        var data = new DataObject();
        data.SetData(DragFormat, this);
        DoDragDrop(data, DragDropEffects.Move);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left) ActivateRequested?.Invoke(this);
    }

    /// <summary>时间显示: 今天只显示 "今天 HH:mm"，其余显示 MM-DD HH:mm。</summary>
    protected static string ShortTime(string? ts)
    {
        if (string.IsNullOrWhiteSpace(ts)) return "—";
        if (!DateTime.TryParse(ts, out var dt)) return ts.Length > 16 ? ts[..16] : ts;
        return dt.Date == DateTime.Today ? dt.ToString("今天 HH:mm") : dt.ToString("MM-dd HH:mm");
    }
}

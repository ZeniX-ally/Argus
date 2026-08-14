using System.Drawing.Drawing2D;

namespace FctAggregator;

/// <summary>
/// 侧边导航的一项（自绘：左侧激活条 + 文字 + 可选红色角标）。
/// 不用 Button —— Button 的 FlatStyle 在深色底上边框/焦点框很难看干净。
/// </summary>
public sealed class NavButton : Control
{
    public int PageIndex { get; }

    private bool _active;
    public bool Active
    {
        get => _active;
        set { if (_active != value) { _active = value; Invalidate(); } }
    }

    private int _badge;
    /// <summary>右侧红色角标数字（0 = 不显示）。</summary>
    public int Badge
    {
        get => _badge;
        set { if (_badge != value) { _badge = value; Invalidate(); } }
    }

    private bool _hover;

    public NavButton(string text, int pageIndex)
    {
        PageIndex = pageIndex;
        Text = text;
        Height = 42;
        Dock = DockStyle.Top;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = ClientRectangle;

        var bg = _active ? Theme.NavActive : _hover ? Theme.NavHover : Theme.NavBg;
        using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);

        // 激活态左侧青色亮条（SCADA 风：主色 + 微光）
        if (_active)
        {
            using (var b = new SolidBrush(Theme.Primary))
                g.FillRectangle(b, new Rectangle(0, 0, 4, r.Height));
            using (var glow = new SolidBrush(Color.FromArgb(36, Theme.Primary)))
                g.FillRectangle(glow, new Rectangle(4, 0, 6, r.Height));
        }

        var fore = _active ? Theme.NavTextActive : Theme.NavText;
        TextRenderer.DrawText(g, Text, _active ? Theme.BodyBold : Theme.Body,
            new Rectangle(18, 0, r.Width - 60, r.Height), fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (_badge > 0)
        {
            var txt = _badge > 99 ? "99+" : _badge.ToString();
            var w = Math.Max(20, TextRenderer.MeasureText(txt, Theme.Tiny).Width + 12);
            var br = new Rectangle(r.Width - w - 12, (r.Height - 18) / 2, w, 18);
            using (var path = Theme.Rounded(br, 9))
            using (var b = new SolidBrush(Theme.Danger))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, txt, Theme.Tiny, br, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}

/// <summary>
/// KPI 卡片（自绘）：标题 + 大数字 + 副说明，左侧一条强调色。
/// 主页顶部一排就是它。深色工业监控风：大数字偏大、强调色发光感。
/// </summary>
public sealed class KpiCard : Control
{
    private readonly Color _accent;
    private string _value = "—";
    private string _sub = "";
    private bool _big;

    public KpiCard(string title, Color accent, bool big = true)
    {
        Text = title;
        _accent = accent;
        _big = big;
        DoubleBuffered = true;
        BackColor = Theme.Bg;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
    }

    public void Set(string value, string sub = "")
    {
        if (_value == value && _sub == sub) return;
        _value = value; _sub = sub;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = new Rectangle(0, 0, Width, Height);
        Theme.DrawCard(g, r, _accent);

        // 标题：浅灰，右上角小圆点（状态灯感）
        using (var dot = new SolidBrush(_accent))
            g.FillEllipse(dot, Width - 22, 11, 7, 7);
        using (var glow = new Pen(Color.FromArgb(80, _accent), 1.5f))
            g.DrawEllipse(glow, Width - 24, 9, 11, 11);
        TextRenderer.DrawText(g, Text, Theme.Small,
            new Rectangle(14, 8, Width - 40, 16), Theme.TextSub,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // 大数字：垂直居中放在「标题下方 ~ 副标题上方」的可用空间里，绝不与标题/副标题重叠
        var numFont = _big ? Theme.Number : Theme.NumberSmall;
        var subH = _sub.Length > 0 ? 18 : 0;                       // 副标题预留高度
        var availTop = 28;                                          // 标题区(8..24)下方
        var availBot = Height - (subH > 0 ? 20 : 6);                // 副标题区(Height-20..)上方
        var availH = Math.Max(20, availBot - availTop);
        var numRect = new Rectangle(12, availTop, Width - 20, availH);
        using (var glow = new SolidBrush(Color.FromArgb(26, _accent)))
            g.FillRectangle(glow, new Rectangle(12, availTop, Width - 24, availH));
        TextRenderer.DrawText(g, _value, numFont, numRect, _accent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPadding);

        if (_sub.Length > 0)
            TextRenderer.DrawText(g, _sub, Theme.Small,
                new Rectangle(14, Height - 20, Width - 20, 16), Theme.TextFaint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>顶栏右侧的状态胶囊条（自绘，一次画好几个）。</summary>
public sealed class ChipBar : Control
{
    private List<(string text, Color color)> _chips = new();

    public ChipBar()
    {
        DoubleBuffered = true;
        BackColor = Theme.Surface;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
    }

    public void SetChips(List<(string text, Color color)> chips)
    {
        if (_chips.Count == chips.Count)
        {
            bool same = true;
            for (int i = 0; i < chips.Count; i++)
                if (_chips[i].text != chips[i].text || _chips[i].color != chips[i].color) { same = false; break; }
            if (same) return;
        }
        _chips = chips;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
        // 从右往左排，保证最重要的（第一个）永远可见
        int x = Width - 4;
        var y = (Height - 22) / 2;
        for (int i = _chips.Count - 1; i >= 0; i--)
        {
            var (text, color) = _chips[i];
            var w = TextRenderer.MeasureText(text, Theme.Small).Width + 20;
            x -= w + 6;
            if (x < 0) break;
            Theme.DrawChip(g, new Point(x, y), text, color);
        }
    }
}

/// <summary>
/// 自绘标题栏（v3.0.1）：替换 Windows 系统标题栏，炭黑与侧栏一体（高级简约）。
/// 左侧为拖拽区（拖动/双击最大化），右侧三个控制按钮（最小化 / 最大化还原 / 关闭）。
/// 窗口边缘拖拽缩放由 MainForm.WndProc 的 WM_NCHITTEST 处理，本控件不管。
/// </summary>
public sealed class TitleBar : Control
{
    private const int BtnW = 46;
    private enum Hit { None, Min, Max, Close }
    private Hit _hit = Hit.None;
    private bool _maximized;

    /// <summary>窗口控制事件（由 MainForm 接线）。</summary>
    public event Action? MinimizeRequested;
    public event Action? MaximizeRequested;
    public event Action? CloseRequested;

    public TitleBar()
    {
        Height = Theme.TitleBarHeight;
        Dock = DockStyle.Top;
        BackColor = Theme.NavBg;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
    }

    /// <summary>窗口最大化状态变化时调用（按钮图标 □ ↔ ❐）。</summary>
    public void SetMaximized(bool maximized)
    {
        if (_maximized != maximized) { _maximized = maximized; Invalidate(); }
    }

    private Hit HitAt(int x) =>
        x >= Width - BtnW ? Hit.Close :
        x >= Width - BtnW * 2 ? Hit.Max :
        x >= Width - BtnW * 3 ? Hit.Min : Hit.None;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = HitAt(e.X);
        if (h != _hit) { _hit = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hit != Hit.None) { _hit = Hit.None; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        switch (HitAt(e.X))
        {
            case Hit.Min: MinimizeRequested?.Invoke(); break;
            case Hit.Max: MaximizeRequested?.Invoke(); break;
            case Hit.Close: CloseRequested?.Invoke(); break;
            default:
                // 空白区：交给系统开始窗口拖动（同时双击会自动切最大化）
                if (e.Button == MouseButtons.Left)
                {
                    var form = FindForm();
                    if (form != null)
                    {
                        Win32.ReleaseCapture();
                        Win32.SendMessage(form.Handle, Win32.WM_NCLBUTTONDOWN, (IntPtr)Win32.HTCAPTION, IntPtr.Zero);
                    }
                }
                break;
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (HitAt(e.X) == Hit.None) MaximizeRequested?.Invoke();   // 空白区双击 = 最大化/还原
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(Theme.NavBg)) g.FillRectangle(b, ClientRectangle);

        // 三个控制按钮（从右到左：关闭 ✕ / 最大化 □ / 最小化 ─，与 HitAt 点击映射一致）
        // ⚠ 用矢量绘制而非字体字形：─□✕ 是不同宽度的字形，渲染大小不一致；
        //   矢量统一图标框（关闭 10×10 叉、最大化 12×10 矩形、最小化 12px 横线）。
        DrawBtn(g, 0, Hit.Close);
        DrawBtn(g, 1, Hit.Max);
        DrawBtn(g, 2, Hit.Min);

        // 底部细分隔线
        using var pen = new Pen(Theme.Border);
        g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }

    /// <summary>绘制一个控制按钮：hover 背景 + 统一尺寸的矢量图标。</summary>
    private void DrawBtn(Graphics g, int idx, Hit h)
    {
        var x = Width - BtnW * (idx + 1);
        var r = new Rectangle(x, 0, BtnW, Height);
        if (_hit == h)
        {
            using var bg = new SolidBrush(h == Hit.Close ? Theme.Danger : Theme.NavHover);
            g.FillRectangle(bg, r);
        }
        var color = (_hit == h && h == Hit.Close) ? Color.White : Theme.NavText;
        using var pen = new Pen(color, 1.5f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        int cx = r.X + r.Width / 2;
        int cy = r.Height / 2;
        switch (h)
        {
            case Hit.Min:   // 最小化：一条 12px 横线
                g.DrawLine(pen, cx - 6, cy, cx + 6, cy);
                break;
            case Hit.Max when _maximized:   // 还原：两个错位小矩形
                g.DrawRectangle(pen, cx - 7, cy - 3, 9, 8);
                g.DrawRectangle(pen, cx - 3, cy - 7, 9, 8);
                break;
            case Hit.Max:   // 最大化：12×10 空心矩形
                g.DrawRectangle(pen, cx - 6, cy - 5, 12, 10);
                break;
            case Hit.Close:   // 关闭：10×10 叉
                g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
                g.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5);
                break;
        }
    }
}

/// <summary>Win32 P/Invoke（自绘标题栏用：拖动窗口 / 边缘缩放）。</summary>
internal static class Win32
{
    public const int WM_NCLBUTTONDOWN = 0xA1;
    public const int HTCAPTION = 0x2;
    /// <summary>窗口从中心展开（开启动画「圆点放大成主页面」用）。</summary>
    public const int AW_CENTER = 0x0010;
    public const int AW_ACTIVATE = 0x00020000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool AnimateWindow(IntPtr hWnd, int time, int flags);
}

/// <summary>
/// 工具页宿主（v2.9.1）—— 把工具的 <see cref="Form"/> **内嵌**到主窗口内容区，
/// 而不是弹一个独立窗口。
///
/// 做法是 WinForms 的者用法：<c>TopLevel = false</c> 之后 Form 就能当普通子控件用。
/// 好处是四个工具的代码**一行不用改**（它们都是绝对坐标布局的大表单窗口，
/// 改写成 UserControl 得重排上百个控件）。
///
/// 两个要点：
///   · <b>惰加载</b>：第一次切到该页才构造，启动不被四个大窗口拖慢；
///   · <b>AutoScroll</b>：工具自带 <c>MinimumSize</c>（最大的 1040×790），主窗口被拉小时
///     靠滚动条兑，而不是把控件压死。
/// </summary>
public sealed class ToolHost : Panel
{
    private readonly Func<Form> _factory;
    private readonly string _label;
    private Label? _error;

    /// <summary>已内嵌的工具窗口（尚未初始化时为 null）。</summary>
    public Form? Embedded { get; private set; }

    public ToolHost(string label, Func<Form> factory)
    {
        _label = label;
        _factory = factory;
        AutoScroll = true;
        BackColor = Theme.Bg;
        Dock = DockStyle.Fill;
        Visible = false;
    }

    /// <summary>确保工具已内嵌（首次切页时调）。失败时就地显示错误，不抛。</summary>
    public bool Ensure()
    {
        if (Embedded != null && !Embedded.IsDisposed) return true;

        // 工具自己把自己 Close() 掉了的情况：清掉残局重建
        if (Embedded != null) { try { Controls.Remove(Embedded); } catch { } Embedded = null; }
        if (_error != null) { Controls.Remove(_error); _error.Dispose(); _error = null; }

        try
        {
            var f = _factory();
            f.TopLevel = false;                          // ← 关键：Form 当子控件用
            f.FormBorderStyle = FormBorderStyle.None;    // 去掉自己的标题栏/边框
            f.Dock = DockStyle.Fill;
            Theme.Apply(f);                              // v2.9.2：刷成与主程序一致的风格（不动布局）
            Controls.Add(f);
            f.Show();
            Embedded = f;
            Logger.Info($"[工具] {_label} 已内嵌到主窗口（{f.MinimumSize.Width}×{f.MinimumSize.Height} 最小尺寸）");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[工具] {_label} 内嵌失败: {ex.GetType().Name} {ex.Message}");
            _error = new Label
            {
                Dock = DockStyle.Top, Height = 80, Padding = new Padding(18, 18, 18, 0),
                ForeColor = Theme.Danger, Font = Theme.Body,
                Text = $"{_label} 加载失败：\n{ex.Message}\n\n（可先用命令行单独跑：Argus.exe 子命令）",
            };
            Controls.Add(_error);
            return false;
        }
    }
}

/// <summary>
/// 白卡片容器：标题栏 + 内容区（<see cref="Content"/>），面板里所有分区都用它。
/// 子控件请加到 <see cref="Content"/> 上，不要直接加到本体（会盖住标题）。
/// </summary>
public sealed class SectionPanel : Panel
{
    /// <summary>内容区（已留好内边距）。</summary>
    public Panel Content { get; }

    private readonly int _titleHeight;
    private string _hint = "";

    /// <summary>标题右侧的浅色补充说明。</summary>
    public string Hint
    {
        get => _hint;
        set { if (_hint != value) { _hint = value; Invalidate(); } }
    }

    public SectionPanel(string title, int titleHeight = 34)
    {
        Text = title;
        _titleHeight = string.IsNullOrEmpty(title) ? 8 : titleHeight;
        BackColor = Theme.Bg;
        DoubleBuffered = true;
        Padding = new Padding(0);
        SetStyle(ControlStyles.ResizeRedraw, true);

        Content = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            Padding = new Padding(10, 2, 10, 10),
        };
        var head = new Panel { Dock = DockStyle.Top, Height = _titleHeight, BackColor = Theme.Surface };
        Controls.Add(Content);
        Controls.Add(head);
        // 标题文字由本体 OnPaint 画（少一个控件句柄）
        head.Paint += (_, e) =>
        {
            if (string.IsNullOrEmpty(Text)) return;
            TextRenderer.DrawText(e.Graphics, Text, Theme.SectionTitle,
                new Rectangle(12, 0, head.Width - 20, head.Height), Theme.TextMain,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (_hint.Length > 0)
            {
                var tw = TextRenderer.MeasureText(Text, Theme.SectionTitle).Width;
                TextRenderer.DrawText(e.Graphics, _hint, Theme.Small,
                    new Rectangle(16 + tw, 0, head.Width - tw - 24, head.Height), Theme.TextFaint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        };
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 外面留 Margin 的灰底 + 里面白圆角卡
        using (var b = new SolidBrush(Theme.Bg)) e.Graphics.FillRectangle(b, ClientRectangle);
        Theme.DrawCard(e.Graphics, ClientRectangle);
    }
}

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

        // 激活态左侧亮条
        if (_active)
            using (var b = new SolidBrush(Color.White))
                g.FillRectangle(b, new Rectangle(0, 0, 3, r.Height));

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
/// 主页顶部一排就是它。
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

        TextRenderer.DrawText(g, Text, Theme.Small,
            new Rectangle(14, 8, Width - 20, 16), Theme.TextSub,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        TextRenderer.DrawText(g, _value, _big ? Theme.Number : Theme.NumberSmall,
            new Rectangle(12, _big ? 24 : 22, Width - 20, _big ? 34 : 28), _accent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPadding);

        if (_sub.Length > 0)
            TextRenderer.DrawText(g, _sub, Theme.Small,
                new Rectangle(14, Height - 22, Width - 20, 16), Theme.TextFaint,
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
                Text = $"{_label} 加载失败：\n{ex.Message}\n\n（可先用命令行单独跑：FCT-Aggregator.exe 子命令）",
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

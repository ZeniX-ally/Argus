namespace FctAggregator;

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
        BackColor = Theme.Surface;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public void Set(string value, string sub = "")
    {
        if (_value == value && _sub == sub) return;
        _value = value;
        _sub = sub;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.DrawCard(g, ClientRectangle, null);
        using (var accent = new SolidBrush(_accent))
            g.FillRectangle(accent, 0, 10, 3, Math.Max(12, Height - 20));

        TextRenderer.DrawText(g, Text, Theme.Small,
            new Rectangle(14, 8, Width - 24, 16), Theme.TextSub,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        var numFont = _big ? Theme.Number : Theme.NumberSmall;
        var subH = _sub.Length > 0 ? 18 : 0;
        var availTop = 28;
        var availBot = Height - (subH > 0 ? 20 : 6);
        var availH = Math.Max(20, availBot - availTop);
        var numRect = new Rectangle(12, availTop, Width - 20, availH);
        TextRenderer.DrawText(g, _value, numFont, numRect, Theme.TextMain,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPadding);

        if (_sub.Length > 0)
            TextRenderer.DrawText(g, _sub, Theme.Small,
                new Rectangle(14, Height - 20, Width - 20, 16), Theme.TextFaint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

public sealed class ChipBar : Control
{
    private List<(string text, Color color)> _chips = new();

    public ChipBar()
    {
        DoubleBuffered = true;
        BackColor = Theme.Surface;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
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

public sealed class ToolHost : Panel
{
    private readonly Func<Form> _factory;
    private readonly string _label;
    private Label? _error;

    public Form? Embedded { get; private set; }

    public bool IsReady => Embedded != null && !Embedded.IsDisposed;

    public ToolHost(string label, Func<Form> factory)
    {
        _label = label;
        _factory = factory;
        AutoScroll = true;
        BackColor = Theme.Bg;
        Dock = DockStyle.Fill;
        Visible = false;
    }

    public bool Ensure()
    {
        if (IsReady) { ActivateTool(); return true; }

        if (Embedded != null) { try { Controls.Remove(Embedded); } catch { } Embedded = null; }
        if (_error != null) { Controls.Remove(_error); _error.Dispose(); _error = null; }

        try
        {
            var f = _factory();
            f.TopLevel = false;
            f.FormBorderStyle = FormBorderStyle.None;
            f.Dock = DockStyle.Fill;
            Theme.Apply(f);
            Controls.Add(f);
            Visible = true;
            f.Show();
            Embedded = f;
            Logger.Info($"[工具] {_label} 已内嵌（{f.MinimumSize.Width}×{f.MinimumSize.Height} 最小尺寸）");
            ActivateTool();
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

    public void ActivateTool()
    {
        if (Embedded != null) { try { Embedded.Visible = true; } catch { } }
    }

    public void DeactivateTool()
    {
        if (Embedded != null) { try { Embedded.Visible = false; } catch { } }
    }
}

public sealed class SectionPanel : Panel
{
    public Panel Content { get; }

    private readonly int _titleHeight;
    private string _hint = "";
    private readonly ToolTip _tip = new() { AutoPopDelay = 15000, InitialDelay = 500, ReshowDelay = 200 };

    public string Hint
    {
        get => _hint;
        set { if (_hint != value) { _hint = value; Invalidate(); SyncTip(); } }
    }

    private void SyncTip() =>
        // UI-10：标题/提示被 EndEllipsis 截断后，悬停可看全
        _tip.SetToolTip(this, (string.IsNullOrEmpty(_hint) ? Text : $"{Text} — {_hint}").Trim());

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
            Padding = new Padding(Theme.Gap, 2, Theme.Gap, Theme.Gap),
        };
        var head = new Panel { Dock = DockStyle.Top, Height = _titleHeight, BackColor = Theme.Surface };
        Controls.Add(Content);
        Controls.Add(head);
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
        SyncTip();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using (var b = new SolidBrush(Theme.Bg)) e.Graphics.FillRectangle(b, ClientRectangle);
        Theme.DrawCard(e.Graphics, ClientRectangle);
    }
}

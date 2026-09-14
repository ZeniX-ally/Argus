using System.Drawing.Drawing2D;

namespace FctAggregator;

public static class Theme
{

    public static Color Bg => SystemColors.Control;
    public static Color Surface => SystemColors.Window;
    public static Color SurfaceHi => SystemColors.ControlLight;
    public static Color NavBg => SystemColors.Control;
    public static Color NavHover => SystemColors.ControlLight;
    public static Color NavActive => SystemColors.ControlLight;
    public static Color NavText => SystemColors.ControlText;
    public static Color NavTextActive => SystemColors.HighlightText;

    public static Color Border => SystemColors.ControlDark;
    public static Color BorderHi => SystemColors.ControlDarkDark;
    public static Color BorderDark => SystemColors.ControlDark;
    public static Color TextMain => SystemColors.ControlText;
    public static Color TextSub => SystemColors.GrayText;
    public static Color TextFaint => SystemColors.GrayText;
    /// <summary>次要灰字（#6D6D6D，白底对比度 ≈5.2:1，满足 WCAG AA 4.5:1）。
    /// 旧的 140/150 灰只有 3.0~3.4:1，产线灯光 + 年长操作员下读不清。</summary>
    public static Color TextMuted => Color.FromArgb(109, 109, 109);

    public static Color Primary => SystemColors.Highlight;
    public static Color PrimaryDim => SystemColors.Highlight;
    public static Color Success => Color.FromArgb(16, 137, 62);
    public static Color Warning => Color.FromArgb(151, 108, 0);
    public static Color Danger => Color.FromArgb(198, 40, 40);
    public static Color Info => TextSub;
    public static Color Neutral => TextSub;

    public static Color AltRowA => Color.FromArgb(250, 250, 250);
    public static Color AltRowB => Color.FromArgb(245, 245, 245);
    public static Color RowHover => Color.FromArgb(240, 240, 240);
    public static Color GridHeaderBg => Color.FromArgb(240, 240, 240);
    public static Color CardBorder => Color.FromArgb(185, 185, 185);

    public static Color ToolDarkBg      => Color.FromArgb(38, 38, 38);
    public static Color ToolDarkFg      => SystemColors.Window;
    public static Color ToolSummary     => Color.FromArgb(200, 16, 46);
    public static Color ToolHighlight   => Color.FromArgb(250, 235, 238);
    public static Color ToolFixed      => Color.FromArgb(26, 26, 26);
    public static Color ToolDim         => TextMuted;
    public static Color ToolLine        => Color.FromArgb(170, 170, 170);
    public static Color ToolNodeDir     => Color.FromArgb(200, 16, 46);
    public static Color ToolNodeFile    => Color.FromArgb(60, 60, 60);
    public static Color ToolGray        => Color.Gray;
    public static Color ToolAltRow      => Color.FromArgb(247, 247, 247);
    public static Color ToolLogBg       => Color.FromArgb(20, 20, 20);
    public static Color ToolLogFg       => Color.FromArgb(230, 230, 230);

    public const int InputLabelWidth = 96;
    public const int InputFieldWidth = 800;
    public const int BrowseWidth    = 40;
    public const int BrowseHeight  = 24;
    public const int SmallBtnWidth  = 100;
    public const int MediumBtnWidth = 128;
    public const int ActionBtnHeight= 32;
    public const int InputHeight    = 24;
    public const int InputGap       = 32;
    public const int ControlLeft    = 112;
    public const int LabelTopOffset = 3;

    private const string Family = "Microsoft YaHei UI";
    public static readonly Font PageTitle = new(Family, 13F, FontStyle.Bold);
    public static readonly Font SectionTitle = new(Family, 10F, FontStyle.Bold);
    public static readonly Font Body = new(Family, 9F);
    public static readonly Font BodyBold = new(Family, 9F, FontStyle.Bold);
    public static readonly Font Small = new(Family, 8.25F);
    /// <summary>最小号字体：7.5pt 在产线灯光下偏小，提到 8pt（仅用于非关键信息）。</summary>
    public static readonly Font Tiny = new(Family, 8F, FontStyle.Bold);
    public static readonly Font Number = new(Family, 20F, FontStyle.Bold);
    public static readonly Font NumberSmall = new(Family, 15F, FontStyle.Bold);
    public static readonly Font NumberMid = new(Family, 16F, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 9F);

    public const int NavWidth = 0;
    public const int TopBarHeight = 34;
    public const int StatusBarHeight = 26;
    public const int TitleBarHeight = 0;
    public const int Radius = 0;
    public const int Gap = 10;

    public static GraphicsPath Rounded(Rectangle r, int radius = Radius) => MaintenanceDraw.Rounded(r, radius);

    public static void DrawCard(Graphics g, Rectangle r, Color? accent = null, bool hover = false)
    {
        if (r.Width <= 2 || r.Height <= 2) return;
        g.SmoothingMode = SmoothingMode.Default;
        var rect = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
        using (var b = new SolidBrush(Surface)) g.FillRectangle(b, rect);
        using var p = new Pen(hover ? BorderHi : Border);
        g.DrawRectangle(p, rect);
    }

    public static int DrawChip(Graphics g, Point at, string text, Color color, Font? font = null)
    {
        font ??= Small;
        var w = TextRenderer.MeasureText(text, font).Width + 22;
        var r = new Rectangle(at.X, at.Y, w, 22);
        using (var dot = new SolidBrush(color))
            g.FillEllipse(dot, r.X + 3, r.Y + 7, 7, 7);
        TextRenderer.DrawText(g, text, font,
            new Rectangle(r.X + 16, r.Y, r.Width - 18, r.Height), color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        return w;
    }

    public const string PrimaryTag = "primary";

    /// <summary>窗体统一 DPI 基准（各 Form 构造函数里调一次）。
    /// 不设的话 AutoScaleMode 默认 Inherit → 非 100% 缩放下行高不足、文字被截断；96dpi 基准在 100% 缩放下缩放系数为 1，机台零视觉变化。</summary>
    public static void ApplyDpi(Form f)
    {
        f.AutoScaleDimensions = new SizeF(96F, 96F);
        f.AutoScaleMode = AutoScaleMode.Dpi;
    }

    /// <summary>主操作/高亮态按钮上色。`FlatStyle.System` 下 `BackColor` 被系统主题吞掉（primary 与普通按钮长得一样），
    /// 必须切 Flat + 关掉 `UseVisualStyleBackColor` 才生效；`Tag=PrimaryTag` 标记"自管底色"，让 `Apply` 不把它改回 System。</summary>
    public static void SetButtonPrimary(Button b, bool primary, int width = -1)
    {
        b.Tag = PrimaryTag;
        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.BorderColor = Border;
        b.BackColor = primary ? PrimaryDim : Surface;
        b.ForeColor = primary ? Color.White : TextMain;
        b.Cursor = Cursors.Hand;
        if (width > 0) b.Width = width;
    }

    public static Button MakeButton(string text, int width = 88, bool primary = false)
    {
        var b = new Button
        {
            Text = text, Width = width, Height = 30,
            Font = Body, Cursor = Cursors.Hand, Margin = new Padding(0, 3, 6, 3),
            FlatStyle = FlatStyle.System,
            BackColor = primary ? PrimaryDim : Surface,
            ForeColor = primary ? Color.White : TextMain,
        };
        if (primary) SetButtonPrimary(b, true);
        return b;
    }

    public const string SkipTag = "no-theme";

    public static void Apply(Control root, bool isPageRoot = true)
    {
        if (root == null || IsSkipped(root)) return;

        switch (root)
        {
            case Form f when isPageRoot:
                f.BackColor = Bg;
                f.ForeColor = TextMain;
                f.Font = Body;
                break;
            case GroupBox gb:
                gb.BackColor = Surface;
                gb.ForeColor = TextMain;
                break;
            case Button b:
                StyleButton(b);
                // UI-9：无障碍名（读屏/辅助技术），缺省用按钮文本
                if (string.IsNullOrEmpty(b.AccessibleName)) b.AccessibleName = b.Text;
                break;
            case TextBox tb:
                tb.BorderStyle = BorderStyle.FixedSingle;
                if (!tb.ReadOnly) tb.BackColor = Surface;
                tb.ForeColor = TextMain;
                if (string.IsNullOrEmpty(tb.AccessibleName)) tb.AccessibleName = tb.PlaceholderText;
                break;
            case ComboBox cb:
                cb.FlatStyle = FlatStyle.System;
                cb.BackColor = Surface;
                cb.ForeColor = TextMain;
                break;
            case CheckBox or RadioButton:
                root.ForeColor = TextMain;
                root.BackColor = Color.Transparent;
                break;
            case Label lb:
                if (IsDefaultish(lb.ForeColor)) lb.ForeColor = TextMain;
                lb.BackColor = Color.Transparent;
                lb.AutoEllipsis = true;   // UI-10：截断处给"…"，而不是硬切
                if (string.IsNullOrEmpty(lb.AccessibleName)) lb.AccessibleName = lb.Text;
                break;
            case ListView lv:
                lv.BorderStyle = BorderStyle.FixedSingle;
                lv.BackColor = Surface;
                lv.ForeColor = TextMain;
                break;
            case DataGridView dg:
                StyleGrid(dg);
                break;
            case Panel p:
                if (IsDefaultish(p.BackColor)) p.BackColor = isPageRoot ? Bg : Surface;
                break;
            case TabControl or TabPage:
                root.BackColor = Surface;
                root.ForeColor = TextMain;
                break;
        }

        foreach (Control c in root.Controls) Apply(c, isPageRoot: false);
    }

    private static void StyleButton(Button b)
    {
        b.Cursor = Cursors.Hand;
        // 自管底色的按钮（primary / 状态高亮 / 语义色）必须保持 Flat：System 风格会吞掉 BackColor
        bool selfPainted = (b.Tag as string) == PrimaryTag || !IsDefaultish(b.BackColor);
        if (!selfPainted)
        {
            b.FlatStyle = FlatStyle.System;
            return;
        }
        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.BorderSize = 1;
    }

    private static void StyleGrid(DataGridView dg)
    {
        dg.BorderStyle = BorderStyle.FixedSingle;
        dg.BackgroundColor = Bg;
        dg.GridColor = Border;
        dg.EnableHeadersVisualStyles = false;
        dg.ColumnHeadersDefaultCellStyle.BackColor = SurfaceHi;
        dg.ColumnHeadersDefaultCellStyle.ForeColor = TextSub;
        dg.ColumnHeadersDefaultCellStyle.Font = BodyBold;
        dg.ColumnHeadersDefaultCellStyle.SelectionBackColor = SurfaceHi;
        dg.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextSub;
        dg.DefaultCellStyle.BackColor = Surface;
        dg.DefaultCellStyle.ForeColor = TextMain;
        dg.DefaultCellStyle.SelectionBackColor = SystemColors.Highlight;
        dg.DefaultCellStyle.SelectionForeColor = Color.White;
        dg.AlternatingRowsDefaultCellStyle.BackColor = AltRowA;
        dg.RowHeadersDefaultCellStyle.BackColor = GridHeaderBg;
        dg.RowHeadersDefaultCellStyle.ForeColor = TextFaint;
    }

    private static bool IsSkipped(Control c) =>
        (c.Tag as string) == SkipTag
        || c is NavButton or KpiCard or ChipBar or SectionPanel or ToolHost
        || c is BoardCardBase or MaintenanceBoard
        || c is RichTextBox
        || c is ProgressBar
        || c.GetType().Name == "WaveformPanel";

    private static bool IsDefaultish(Color c) =>
        c == SystemColors.Control || c == SystemColors.ControlText ||
        c == SystemColors.Window || c == SystemColors.WindowText ||
        c == Color.Empty || c == Color.Transparent ||
        c == Color.Black || c == Color.White;
}

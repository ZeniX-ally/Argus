using System.Drawing.Drawing2D;

namespace FctAggregator;

/// <summary>
/// 全局视觉规范（v2.8.0 GUI 重构）。
///
/// 之前每个面板各写各的颜色字号，改一处得翻五个文件。这里统一成一份「设计令牌」：
/// 颜色 / 字体 / 圆角 / 间距 + 几个绘制小工具，所有面板都从这里取。
/// </summary>
public static class Theme
{
    // ==================== 颜色 ====================
    /// <summary>工作区底色（极浅冷灰，衬托白色卡片；高级简约风）。</summary>
    public static readonly Color Bg = Color.FromArgb(247, 248, 250);
    /// <summary>卡片/面板底色。</summary>
    public static readonly Color Surface = Color.White;
    /// <summary>侧边导航底色（炭黑，中性克制的深底）。</summary>
    public static readonly Color NavBg = Color.FromArgb(16, 20, 24);
    public static readonly Color NavHover = Color.FromArgb(28, 34, 40);
    public static readonly Color NavActive = Color.FromArgb(30, 36, 42);
    public static readonly Color NavText = Color.FromArgb(150, 158, 166);
    public static readonly Color NavTextActive = Color.White;

    public static readonly Color Border = Color.FromArgb(231, 234, 240);
    public static readonly Color TextMain = Color.FromArgb(15, 23, 42);
    public static readonly Color TextSub = Color.FromArgb(100, 116, 139);
    public static readonly Color TextFaint = Color.FromArgb(160, 174, 192);

    // 唯一强调色 + 降饱和语义色（高级简约：彩色收敛，只留必要的语义）
    public static readonly Color Primary = Color.FromArgb(37, 99, 235);
    public static readonly Color Success = Color.FromArgb(22, 163, 74);
    public static readonly Color Warning = Color.FromArgb(217, 119, 6);
    public static readonly Color Danger = Color.FromArgb(220, 38, 38);
    public static readonly Color Info = Color.FromArgb(13, 148, 136);
    public static readonly Color Neutral = Color.FromArgb(100, 116, 139);

    // ==================== 字体 ====================
    private const string Family = "Microsoft YaHei UI";
    public static readonly Font PageTitle = new(Family, 13F, FontStyle.Bold);
    public static readonly Font SectionTitle = new(Family, 10F, FontStyle.Bold);
    public static readonly Font Body = new(Family, 9F);
    public static readonly Font BodyBold = new(Family, 9F, FontStyle.Bold);
    public static readonly Font Small = new(Family, 8.25F);
    public static readonly Font Tiny = new(Family, 7.5F, FontStyle.Bold);
    /// <summary>KPI 大数字。</summary>
    public static readonly Font Number = new(Family, 21F, FontStyle.Bold);
    public static readonly Font NumberSmall = new(Family, 15F, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 9F);

    // ==================== 尺寸 ====================
    public const int NavWidth = 168;
    public const int TopBarHeight = 54;
    public const int StatusBarHeight = 26;
    /// <summary>自绘标题栏高度（v3.0.1：去掉系统标题栏，改炭黑自绘，与侧栏一体）。</summary>
    public const int TitleBarHeight = 34;
    public const int Radius = 6;
    public const int Gap = 10;

    // ==================== 绘制小工具 ====================
    public static GraphicsPath Rounded(Rectangle r, int radius = Radius) => MaintenanceDraw.Rounded(r, radius);

    /// <summary>画一张白卡片（圆角 + 描边，可选顶部强调色条）。</summary>
    public static void DrawCard(Graphics g, Rectangle r, Color? accent = null, bool hover = false)
    {
        if (r.Width <= 2 || r.Height <= 2) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
        using var path = Rounded(rect);
        using (var b = new SolidBrush(Surface)) g.FillPath(b, path);
        using (var p = new Pen(hover ? Primary : Border)) g.DrawPath(p, path);
        if (accent != null)
        {
            // 左侧 3px 强调条（用裁剪保证不出圆角）
            var old = g.Clip;
            g.SetClip(path);
            using var ab = new SolidBrush(accent.Value);
            g.FillRectangle(ab, new Rectangle(rect.X, rect.Y, 3, rect.Height));
            g.Clip = old;
        }
    }

    /// <summary>画一个状态胶囊（顶栏用）。返回占用宽度。</summary>
    public static int DrawChip(Graphics g, Point at, string text, Color color, Font? font = null)
    {
        font ??= Small;
        var w = TextRenderer.MeasureText(text, font).Width + 20;
        var r = new Rectangle(at.X, at.Y, w, 22);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Rounded(r, 11))
        {
            using var b = new SolidBrush(Color.FromArgb(24, color));
            g.FillPath(b, path);
            using var p = new Pen(Color.FromArgb(70, color));
            g.DrawPath(p, path);
        }
        // 左侧小圆点
        using (var dot = new SolidBrush(color))
            g.FillEllipse(dot, r.X + 8, r.Y + 8, 6, 6);
        TextRenderer.DrawText(g, text, font,
            new Rectangle(r.X + 17, r.Y, r.Width - 20, r.Height), color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        return w;
    }

    /// <summary>统一风格的按钮（工具栏用）。</summary>
    public static Button MakeButton(string text, int width = 88, bool primary = false)
    {
        var b = new Button
        {
            Text = text, Width = width, Height = 30, FlatStyle = FlatStyle.Flat,
            Font = Body, Cursor = Cursors.Hand, Margin = new Padding(0, 3, 6, 3),
            BackColor = primary ? Primary : Surface,
            ForeColor = primary ? Color.White : TextMain,
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = primary ? Primary : Border;
        b.FlatAppearance.BorderSize = 1;
        return b;
    }

    // ==================== 递归上主题（v2.9.2） ====================

    /// <summary>把控件的 <see cref="Control.Tag"/> 设为它，<see cref="Apply"/> 就不动这个控件（及其子树）。</summary>
    public const string SkipTag = "no-theme";

    /// <summary>
    /// 把一棵控件树刷成统一风格（颜色 / 字体 / 按钮 / 输入框 / 表格）。
    ///
    /// 为什么要递归刷而不是逐个面板重写：四个内嵌工具都是上百个**绝对坐标**控件的大表单，
    /// 重写等于重排界面；递归刷只改外观。
    ///
    /// 铁律：**绝不碰布局** —— 不改 Bounds / Dock / Anchor / Padding / Margin，
    /// 也不改子控件自己显式设过的 Font（粗体标题之类保留）。
    /// 自检里有一条断言会比对刷前刷后所有控件的 Bounds。
    ///
    /// 下面这些自绘/已调好的控件不递归进去（刷了反而难看）。
    /// </summary>
    public static void Apply(Control root, bool isPageRoot = true)
    {
        if (root == null || IsSkipped(root)) return;

        switch (root)
        {
            case Form f when isPageRoot:
                f.BackColor = Bg;
                f.ForeColor = TextMain;
                f.Font = Body;                     // 显式定住：内嵌/独立两种模式下字体一致
                break;
            case GroupBox gb:
                gb.BackColor = Surface;
                gb.ForeColor = TextMain;
                break;
            case Button b:
                StyleButton(b);
                break;
            case TextBox tb:
                tb.BorderStyle = BorderStyle.FixedSingle;
                if (!tb.ReadOnly) tb.BackColor = Surface;
                break;
            case ComboBox cb:
                cb.FlatStyle = FlatStyle.Flat;
                cb.BackColor = Surface;
                break;
            case CheckBox or RadioButton:
                root.ForeColor = TextMain;
                root.BackColor = Color.Transparent;
                break;
            case Label lb:
                // 已经刻意染过色的标签（红/绿灯、警告语）不动
                if (IsDefaultish(lb.ForeColor)) lb.ForeColor = TextMain;
                lb.BackColor = Color.Transparent;
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
                // 已经自己指定过非默认色的面板不动（比如待办看板的列背景）
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
        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        b.Cursor = Cursors.Hand;
        // 工具里已经给“主按钮”涂了颜色（绿底/蓝底）的，只统一边框不改底色
        if (IsDefaultish(b.BackColor))
        {
            b.BackColor = Surface;
            b.ForeColor = TextMain;
            b.FlatAppearance.BorderColor = Border;
        }
        else
        {
            b.FlatAppearance.BorderColor = ControlPaint.Dark(b.BackColor, 0.05f);
        }
        b.FlatAppearance.BorderSize = 1;
    }

    private static void StyleGrid(DataGridView dg)
    {
        dg.BorderStyle = BorderStyle.FixedSingle;
        dg.BackgroundColor = Bg;
        dg.GridColor = Border;
        dg.EnableHeadersVisualStyles = false;          // 不关它，表头颜色改不动
        dg.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(238, 242, 247);
        dg.ColumnHeadersDefaultCellStyle.ForeColor = TextMain;
        dg.ColumnHeadersDefaultCellStyle.Font = BodyBold;
        dg.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(238, 242, 247);
        dg.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextMain;
        dg.DefaultCellStyle.BackColor = Surface;
        dg.DefaultCellStyle.ForeColor = TextMain;
        dg.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 232, 246);
        dg.DefaultCellStyle.SelectionForeColor = TextMain;
        dg.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 251, 253);
        dg.RowHeadersDefaultCellStyle.BackColor = Color.FromArgb(244, 246, 249);
        dg.RowHeadersDefaultCellStyle.ForeColor = TextSub;
    }

    /// <summary>自绘 / 已调好的控件：不刷，也不递归进去。</summary>
    private static bool IsSkipped(Control c) =>
        (c.Tag as string) == SkipTag
        || c is NavButton or KpiCard or ChipBar or SectionPanel or ToolHost
        || c is BoardCardBase or MaintenanceBoard
        || c is RichTextBox        // 日志窗自己着色
        || c is ProgressBar
        || c.GetType().Name == "WaveformPanel";   // TDMS 波形图自绘

    /// <summary>是不是“没人改过”的系统默认色（只有这种才能被覆盖）。</summary>
    private static bool IsDefaultish(Color c) =>
        c == SystemColors.Control || c == SystemColors.ControlText ||
        c == SystemColors.Window || c == SystemColors.WindowText ||
        c == Color.Empty || c == Color.Transparent ||
        c == Color.Black || c == Color.White ||
        c.ToArgb() == Color.FromArgb(240, 240, 240).ToArgb();
}

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
    // ==================== 颜色（v3.4.12 亮色版：暗色可读性差，用户拍板回亮色） ====================
    /// <summary>工作区底色（浅灰）。</summary>
    public static readonly Color Bg = Color.FromArgb(243, 244, 246);
    /// <summary>卡片/面板底色（白）。</summary>
    public static readonly Color Surface = Color.White;
    /// <summary>更暗一档（悬停/强调区，浅灰）。</summary>
    public static readonly Color SurfaceHi = Color.FromArgb(232, 234, 240);
    /// <summary>侧边导航底色（白，与内容区分靠边框与激活色）。</summary>
    public static readonly Color NavBg = Color.White;
    public static readonly Color NavHover = Color.FromArgb(240, 242, 246);
    public static readonly Color NavActive = Color.FromArgb(226, 236, 252);
    public static readonly Color NavText = Color.FromArgb(75, 85, 99);
    public static readonly Color NavTextActive = Color.FromArgb(26, 115, 232);

    public static readonly Color Border = Color.FromArgb(209, 213, 219);
    public static readonly Color BorderHi = Color.FromArgb(156, 163, 175);
    public static readonly Color TextMain = Color.FromArgb(31, 41, 55);
    public static readonly Color TextSub = Color.FromArgb(75, 85, 99);
    public static readonly Color TextFaint = Color.FromArgb(107, 114, 128);

    // 亮色基调：标准蓝主色 + 标准语义色（白底上对比度达标，可读性优先）
    public static readonly Color Primary = Color.FromArgb(26, 115, 232);      // 主蓝
    public static readonly Color PrimaryDim = Color.FromArgb(21, 87, 178);    // 深一档（激活条/主按钮）
    public static readonly Color Success = Color.FromArgb(22, 163, 74);       // 绿
    public static readonly Color Warning = Color.FromArgb(217, 119, 6);       // 琥珀
    public static readonly Color Danger = Color.FromArgb(220, 38, 38);        // 红
    public static readonly Color Info = Color.FromArgb(8, 145, 178);          // 青
    public static readonly Color Neutral = Color.FromArgb(107, 114, 128);

    // ==================== 字体 ====================
    private const string Family = "Microsoft YaHei UI";
    public static readonly Font PageTitle = new(Family, 13F, FontStyle.Bold);
    public static readonly Font SectionTitle = new(Family, 10F, FontStyle.Bold);
    public static readonly Font Body = new(Family, 9F);
    public static readonly Font BodyBold = new(Family, 9F, FontStyle.Bold);
    public static readonly Font Small = new(Family, 8.25F);
    public static readonly Font Tiny = new(Family, 7.5F, FontStyle.Bold);
    /// <summary>KPI 大数字（监控风：偏大、醒目）。</summary>
    public static readonly Font Number = new(Family, 24F, FontStyle.Bold);
    public static readonly Font NumberSmall = new(Family, 17F, FontStyle.Bold);
    /// <summary>统计明细的大数字（介于 KPI 与正文之间）。</summary>
    public static readonly Font NumberMid = new(Family, 19F, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 9F);

    // ==================== 尺寸 ====================
    public const int NavWidth = 168;
    public const int TopBarHeight = 54;
    public const int StatusBarHeight = 26;
    /// <summary>自绘标题栏高度（v3.0.1：去掉系统标题栏，改炭黑自绘，与侧栏一体）。</summary>
    public const int TitleBarHeight = 34;
    public const int Radius = 8;
    public const int Gap = 10;

    // ==================== 绘制小工具 ====================
    public static GraphicsPath Rounded(Rectangle r, int radius = Radius) => MaintenanceDraw.Rounded(r, radius);

    /// <summary>画一张浅色卡片（圆角 + 描边 + 可选顶部强调色条）。
    /// 卡片底色 <see cref="Surface"/>（白），hover 时边框加深到 <see cref="BorderHi"/>；
    /// accent 为强调色时左侧 3px 亮条（状态灯式的彩色指示）。</summary>
    public static void DrawCard(Graphics g, Rectangle r, Color? accent = null, bool hover = false)
    {
        if (r.Width <= 2 || r.Height <= 2) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
        using var path = Rounded(rect);
        using (var b = new SolidBrush(Surface)) g.FillPath(b, path);
        using (var p = new Pen(hover ? BorderHi : Border)) g.DrawPath(p, path);
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

    /// <summary>画一个状态胶囊（顶栏用）。返回占用宽度。亮色版：浅色半透明底 + 彩色边框 + 实心圆点。</summary>
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
            using var p = new Pen(Color.FromArgb(110, color));
            g.DrawPath(p, path);
        }
        // 左侧实心圆点 + 淡光晕（亮色下 36 alpha 的光晕刚好可见）
        using (var dot = new SolidBrush(color))
            g.FillEllipse(dot, r.X + 8, r.Y + 7, 7, 7);
        using (var glow = new Pen(Color.FromArgb(48, color), 1.5f))
            g.DrawEllipse(glow, r.X + 6, r.Y + 5, 11, 11);
        TextRenderer.DrawText(g, text, font,
            new Rectangle(r.X + 18, r.Y, r.Width - 20, r.Height), color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        return w;
    }

    /// <summary>统一风格的按钮（工具栏用）。亮色版：白底 + 灰边框，主按钮主蓝填充。</summary>
    public static Button MakeButton(string text, int width = 88, bool primary = false)
    {
        var b = new Button
        {
            Text = text, Width = width, Height = 30, FlatStyle = FlatStyle.Flat,
            Font = Body, Cursor = Cursors.Hand, Margin = new Padding(0, 3, 6, 3),
            BackColor = primary ? PrimaryDim : Surface,
            ForeColor = primary ? Color.White : TextMain,
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = primary ? Primary : BorderHi;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = primary ? Primary : SurfaceHi;
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
                tb.ForeColor = TextMain;
                break;
            case ComboBox cb:
                cb.FlatStyle = FlatStyle.Flat;
                cb.BackColor = Surface;
                cb.ForeColor = TextMain;
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
        // 工具里已经给"主按钮"涂了颜色的（绿底/蓝底），只统一边框不改底色
        if (IsDefaultish(b.BackColor))
        {
            b.BackColor = Surface;
            b.ForeColor = TextMain;
            b.FlatAppearance.BorderColor = BorderHi;
        }
        else
        {
            b.FlatAppearance.BorderColor = ControlPaint.Dark(b.BackColor, 0.15f);
        }
        b.FlatAppearance.MouseOverBackColor = SurfaceHi;
        b.FlatAppearance.BorderSize = 1;
    }

    private static void StyleGrid(DataGridView dg)
    {
        dg.BorderStyle = BorderStyle.FixedSingle;
        dg.BackgroundColor = Bg;
        dg.GridColor = Border;
        dg.EnableHeadersVisualStyles = false;          // 不关它，表头颜色改不动
        dg.ColumnHeadersDefaultCellStyle.BackColor = SurfaceHi;
        dg.ColumnHeadersDefaultCellStyle.ForeColor = TextSub;
        dg.ColumnHeadersDefaultCellStyle.Font = BodyBold;
        dg.ColumnHeadersDefaultCellStyle.SelectionBackColor = SurfaceHi;
        dg.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextSub;
        dg.DefaultCellStyle.BackColor = Surface;
        dg.DefaultCellStyle.ForeColor = TextMain;
        dg.DefaultCellStyle.SelectionBackColor = Color.FromArgb(207, 227, 255);
        dg.DefaultCellStyle.SelectionForeColor = Color.FromArgb(21, 87, 178);
        dg.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 248, 250);
        dg.RowHeadersDefaultCellStyle.BackColor = Color.FromArgb(238, 240, 243);
        dg.RowHeadersDefaultCellStyle.ForeColor = TextFaint;
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

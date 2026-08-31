using System.Drawing.Drawing2D;

namespace FctAggregator;

/// <summary>
/// 全局视觉规范（v2.8.0 GUI 重构；v3.6.0 回归纯系统原生）。
///
/// 之前是「Gallery Noir」黑红监控风：墨黑侧栏 / 自绘标题栏 / 大数字 KPI / 圆角发光。
/// v3.6.0 起按产线要求回归 <b>Windows 原版风格</b>：
/// 所有颜色映射回 <see cref="SystemColors"/>，标题栏用系统标准，控件用系统默认外观。
///
/// 本类型是「设计令牌」单点：颜色 / 字体 / 尺寸 + 几个绘制小工具，所有面板都从这里取。
/// ⚠ 字段名与方法签名保持 v2.8.0 以来不变 —— 各面板上百处引用不用逐个改，只改这里。
/// 语义映射保留：正常/在线=系统高亮蓝、失败/告急=系统红、注意=中性灰、提示=系统灰文本。
/// </summary>
public static class Theme
{
    // ==================== 颜色（纯系统原生：全部来自 SystemColors） ====================
    /// <summary>工作区底色（系统 3D 面板灰）。</summary>
    public static readonly Color Bg = SystemColors.Control;
    /// <summary>卡片/面板底色（系统窗口白）。</summary>
    public static readonly Color Surface = SystemColors.Window;
    /// <summary>更暗一档（悬停/强调区）。</summary>
    public static readonly Color SurfaceHi = SystemColors.ControlLight;
    /// <summary>导航/标题底色 —— 原生风格用系统控件底色，不做深色块。</summary>
    public static readonly Color NavBg = SystemColors.Control;
    public static readonly Color NavHover = SystemColors.ControlLight;
    public static readonly Color NavActive = SystemColors.ControlLight;
    public static readonly Color NavText = SystemColors.ControlText;
    public static readonly Color NavTextActive = SystemColors.HighlightText;

    public static readonly Color Border = SystemColors.ControlDark;
    public static readonly Color BorderHi = SystemColors.ControlDarkDark;
    /// <summary>深色表面上的分隔线 —— 原生浅色场景基本不用，保留为系统描边色。</summary>
    public static readonly Color BorderDark = SystemColors.ControlDark;
    public static readonly Color TextMain = SystemColors.ControlText;
    public static readonly Color TextSub = SystemColors.GrayText;
    public static readonly Color TextFaint = SystemColors.GrayText;

    // 状态色：主操作/激活 = 系统高亮蓝，失败/告急 = 系统红，正常/在线 = 系统绿系。
    // 原生风格下不强作“唯一彩色”，但保持语义分明。
    public static readonly Color Primary = SystemColors.Highlight;        // 主操作 → 系统蓝
    public static readonly Color PrimaryDim = SystemColors.Highlight;     // 主按钮底
    public static readonly Color Success = Color.FromArgb(16, 137, 62);   // 正常/在线 → 系统绿
    public static readonly Color Warning = Color.FromArgb(151, 108, 0);   // 注意 → 琥珀
    public static readonly Color Danger = Color.FromArgb(198, 40, 40);    // 失败/告急 → 系统红
    public static readonly Color Info = SystemColors.GrayText;            // 提示 → 灰文本
    public static readonly Color Neutral = SystemColors.GrayText;

    // ==================== 遗留工具兼容色（硬编码集中管理） ====================
    public static readonly Color ToolDarkBg      = Color.FromArgb(38, 38, 38);      // 深色表头底色
    public static readonly Color ToolDarkFg      = SystemColors.Window;             // 深色表头白字
    public static readonly Color ToolSummary      = Color.FromArgb(200, 16, 46);     // 摘要强调红
    public static readonly Color ToolHighlight     = Color.FromArgb(250, 235, 238);  // 浅红高亮行
    public static readonly Color ToolFixed         = Color.FromArgb(26, 26, 26);     // 固定深灰文字
    public static readonly Color ToolDim           = Color.FromArgb(140, 140, 140);  // 禁用/次要文字
    public static readonly Color ToolLine          = Color.FromArgb(170, 170, 170);  // 分隔线灰
    public static readonly Color ToolNodeDir       = Color.FromArgb(200, 16, 46);    // 目录树目录节点
    public static readonly Color ToolNodeFile      = Color.FromArgb(60, 60, 60);     // 目录树文件节点
    public static readonly Color ToolGray          = Color.Gray;                     // 非数值通道/跳过
    public static readonly Color ToolAltRow        = Color.FromArgb(247, 247, 247); // 交替行底色
    public static readonly Color ToolLogBg         = Color.FromArgb(20, 20, 20);     // 日志背景深色
    public static readonly Color ToolLogFg         = Color.FromArgb(230, 230, 230);  // 日志浅灰文字

    // ==================== 布局常量（间距 / 控件尺寸） ====================
    public const int InputLabelWidth = 96;       // 输入区标签宽度
    public const int InputFieldWidth = 800;      // 输入框宽度
    public const int BrowseWidth    = 40;        // 浏览按钮宽
    public const int BrowseHeight   = 24;        // 浏览按钮高
    public const int SmallBtnWidth  = 100;       // 小按钮宽
    public const int MediumBtnWidth = 128;       // 中按钮宽
    public const int ActionBtnHeight= 32;        // 操作按钮高
    public const int InputHeight    = 24;        // 标准输入框高
    public const int InputGap       = 32;        // 输入行垂直间距
    public const int ControlLeft    = 112;        // 第一列控件左偏移
    public const int LabelTopOffset = 3;         // 标签相对输入框的垂直微调

    // ==================== 字体（保留原命名，全部基于系统 UI 字体） ====================
    // Windows 原生一般用 Segoe UI / Microsoft YaHei UI；保持原有字族避免字号排版跳动。
    private const string Family = "Microsoft YaHei UI";
    public static readonly Font PageTitle = new(Family, 13F, FontStyle.Bold);
    public static readonly Font SectionTitle = new(Family, 10F, FontStyle.Bold);
    public static readonly Font Body = new(Family, 9F);
    public static readonly Font BodyBold = new(Family, 9F, FontStyle.Bold);
    public static readonly Font Small = new(Family, 8.25F);
    public static readonly Font Tiny = new(Family, 7.5F, FontStyle.Bold);
    /// <summary>KPI 大数字 —— 原生风格收敛回正文大号，不再“监控风”。</summary>
    public static readonly Font Number = new(Family, 20F, FontStyle.Bold);
    public static readonly Font NumberSmall = new(Family, 15F, FontStyle.Bold);
    /// <summary>统计明细的大数字。</summary>
    public static readonly Font NumberMid = new(Family, 16F, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 9F);

    // ==================== 尺寸（原生风格：标题栏用系统默认，这里保留常量但主窗体不再强制自绘高度） ====================
    public const int NavWidth = 0;            // 原生：无侧栏，置 0
    public const int TopBarHeight = 34;
    public const int StatusBarHeight = 26;
    /// <summary>自绘标题栏高度 —— 原生风格不再自绘标题栏，保留常量仅供兼容（实际不再占用客户区）。</summary>
    public const int TitleBarHeight = 0;
    public const int Radius = 0;              // 原生：无圆角
    public const int Gap = 10;

    // ==================== 绘制小工具（保留签名，原生观感） ====================

    /// <summary>原生风格无圆角路径：直接返回矩形路径（Radius 恒为 0，兼容旧调用）。</summary>
    public static GraphicsPath Rounded(Rectangle r, int radius = Radius) => MaintenanceDraw.Rounded(r, radius);

    /// <summary>
    /// 原生风格卡片：系统窗口底色 + 系统描边（无圆角、无强调色条）。
    /// accent / hover 参数保留以便旧调用不变，但不渲染装饰性亮条。
    /// </summary>
    public static void DrawCard(Graphics g, Rectangle r, Color? accent = null, bool hover = false)
    {
        if (r.Width <= 2 || r.Height <= 2) return;
        g.SmoothingMode = SmoothingMode.Default;
        var rect = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
        using (var b = new SolidBrush(Surface)) g.FillRectangle(b, rect);
        using var p = new Pen(hover ? BorderHi : Border);
        g.DrawRectangle(p, rect);
    }

    /// <summary>
    /// 原生风格状态胶囊（顶栏用）：系统窗口底色 + 彩色文字 + 彩色圆点，无半透明发光。
    /// 返回占用宽度。
    /// </summary>
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

    /// <summary>
    /// 原生风格按钮：使用系统默认按钮外观（不强制 Flat），主按钮用系统高亮色。
    /// </summary>
    public static Button MakeButton(string text, int width = 88, bool primary = false)
    {
        var b = new Button
        {
            Text = text, Width = width, Height = 30,
            Font = Body, Cursor = Cursors.Hand, Margin = new Padding(0, 3, 6, 3),
            BackColor = primary ? PrimaryDim : Surface,
            ForeColor = primary ? Color.White : TextMain,
            FlatStyle = FlatStyle.System,       // ← 原生：系统绘制按钮
        };
        return b;
    }

    // ==================== 递归上主题（保留行为：只刷外观，绝不碰布局） ====================

    /// <summary>把控件的 <see cref="Control.Tag"/> 设为它，<see cref="Apply"/> 就不动这个控件（及其子树）。</summary>
    public const string SkipTag = "no-theme";

    /// <summary>
    /// 把一棵控件树刷成统一原生风格（颜色 / 字体 / 按钮 / 输入框 / 表格）。
    ///
    /// ⚠ 铁律不变：**绝不碰布局** —— 不改 Bounds / Dock / Anchor / Padding / Margin。
    /// 原生风格下大部分控件用系统默认即可，这里只纠正少数偏离系统的配色，并让内嵌工具页面观感一致。
    /// 自检里有一条断言会比对刷前刷后所有控件的 Bounds（布局不被破坏）。
    /// </summary>
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
                break;
            case TextBox tb:
                tb.BorderStyle = BorderStyle.FixedSingle;
                if (!tb.ReadOnly) tb.BackColor = Surface;
                tb.ForeColor = TextMain;
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
        // 原生：默认系统按钮外观；主按钮（已涂色）保留其底色
        b.FlatStyle = FlatStyle.System;
        b.Cursor = Cursors.Hand;
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
        dg.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 250, 250);
        dg.RowHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 240, 240);
        dg.RowHeadersDefaultCellStyle.ForeColor = TextFaint;
    }

    /// <summary>自绘 / 已调好的控件：不刷，也不递归进去。</summary>
    private static bool IsSkipped(Control c) =>
        (c.Tag as string) == SkipTag
        || c is NavButton or KpiCard or ChipBar or SectionPanel or ToolHost
        || c is BoardCardBase or MaintenanceBoard
        || c is RichTextBox
        || c is ProgressBar
        || c.GetType().Name == "WaveformPanel";

    /// <summary>是不是“没人改过”的系统默认色（只有这种才能被覆盖）。</summary>
    private static bool IsDefaultish(Color c) =>
        c == SystemColors.Control || c == SystemColors.ControlText ||
        c == SystemColors.Window || c == SystemColors.WindowText ||
        c == Color.Empty || c == Color.Transparent ||
        c == Color.Black || c == Color.White ||
        c.ToArgb() == Color.FromArgb(240, 240, 240).ToArgb();
}

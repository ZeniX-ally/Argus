using System.Drawing.Drawing2D;

namespace FctAggregator;

/// <summary>
/// 全局视觉规范（v2.8.0 GUI 重构；v3.6.0 回归纯系统原生；v3.23.0 暗黑模式；v3.26.0 移除暗黑回归纯浅色）。
///
/// v3.26.0 起按产线要求移除暗黑模式：全部颜色令牌固定为 <b>Windows 原版风格</b>（浅色），
/// 去掉亮/暗双值切换与 config.ui_theme（残留配置键自动忽略，无需手工清理）。
///
/// 本类型是「设计令牌」单点：颜色 / 字体 / 尺寸 + 几个绘制小工具，所有面板都从这里取。
/// ⚠ 字段名与方法签名保持 v2.8.0 以来不变 —— 各面板上百处引用不用逐个改，只改这里。
/// </summary>
public static class Theme
{
    // ==================== 颜色令牌（固定浅色：系统原生）====================

    /// <summary>工作区底色。系统 3D 面板灰。</summary>
    public static Color Bg => SystemColors.Control;
    /// <summary>卡片/面板底色。系统窗口白。</summary>
    public static Color Surface => SystemColors.Window;
    /// <summary>更亮一档（悬停/强调区）。ControlLight。</summary>
    public static Color SurfaceHi => SystemColors.ControlLight;
    /// <summary>导航/标题底色 —— 系统控件底色。</summary>
    public static Color NavBg => SystemColors.Control;
    public static Color NavHover => SystemColors.ControlLight;
    public static Color NavActive => SystemColors.ControlLight;
    public static Color NavText => SystemColors.ControlText;
    public static Color NavTextActive => SystemColors.HighlightText;

    public static Color Border => SystemColors.ControlDark;
    public static Color BorderHi => SystemColors.ControlDarkDark;
    /// <summary>深色表面上的分隔线 —— 保留语义。</summary>
    public static Color BorderDark => SystemColors.ControlDark;
    /// <summary>正文文字。系统控件黑。</summary>
    public static Color TextMain => SystemColors.ControlText;
    /// <summary>次要文字。系统灰文本。</summary>
    public static Color TextSub => SystemColors.GrayText;
    public static Color TextFaint => SystemColors.GrayText;

    // 状态色：主操作/激活 = 蓝，失败/告急 = 红，正常/在线 = 绿，注意 = 琥珀。
    public static Color Primary => SystemColors.Highlight;
    public static Color PrimaryDim => SystemColors.Highlight;
    public static Color Success => Color.FromArgb(16, 137, 62);
    public static Color Warning => Color.FromArgb(151, 108, 0);
    public static Color Danger => Color.FromArgb(198, 40, 40);
    public static Color Info => TextSub;
    public static Color Neutral => TextSub;

    // ==================== 表格/列表辅助令牌（收编散落硬编码）====================

    /// <summary>交替行 A（#FAFAFA）。</summary>
    public static Color AltRowA => Color.FromArgb(250, 250, 250);
    /// <summary>交替行 B（#F5F5F5）。</summary>
    public static Color AltRowB => Color.FromArgb(245, 245, 245);
    /// <summary>行悬停底色（#F0F0F0）。</summary>
    public static Color RowHover => Color.FromArgb(240, 240, 240);
    /// <summary>表格行头底色（#F0F0F0）。</summary>
    public static Color GridHeaderBg => Color.FromArgb(240, 240, 240);
    /// <summary>卡片边框（#B9B9B9）。</summary>
    public static Color CardBorder => Color.FromArgb(185, 185, 185);

    // ==================== 遗留工具兼容色（硬编码集中管理） ====================
    public static Color ToolDarkBg      => Color.FromArgb(38, 38, 38);                    // 深色表头底色
    public static Color ToolDarkFg      => SystemColors.Window;                           // 深色表头白字
    public static Color ToolSummary     => Color.FromArgb(200, 16, 46);                   // 摘要强调红
    public static Color ToolHighlight   => Color.FromArgb(250, 235, 238);                 // 浅红高亮行
    public static Color ToolFixed      => Color.FromArgb(26, 26, 26);                    // 固定强调文字
    public static Color ToolDim         => Color.FromArgb(140, 140, 140);                 // 禁用/次要文字
    public static Color ToolLine        => Color.FromArgb(170, 170, 170);                 // 分隔线灰
    public static Color ToolNodeDir     => Color.FromArgb(200, 16, 46);                   // 目录树目录节点
    public static Color ToolNodeFile    => Color.FromArgb(60, 60, 60);                    // 目录树文件节点
    public static Color ToolGray        => Color.Gray;                                    // 非数值通道/跳过
    public static Color ToolAltRow      => Color.FromArgb(247, 247, 247);                 // 交替行底色
    public static Color ToolLogBg       => Color.FromArgb(20, 20, 20);                    // 日志背景深色
    public static Color ToolLogFg       => Color.FromArgb(230, 230, 230);                 // 日志浅灰文字

    // ==================== 布局常量（间距 / 控件尺寸） ====================
    public const int InputLabelWidth = 96;       // 输入区标签宽度
    public const int InputFieldWidth = 800;      // 输入框宽度
    public const int BrowseWidth    = 40;        // 浏览按钮宽
    public const int BrowseHeight  = 24;         // 浏览按钮高
    public const int SmallBtnWidth  = 100;       // 小按钮宽
    public const int MediumBtnWidth = 128;       // 中按钮宽
    public const int ActionBtnHeight= 32;         // 操作按钮高
    public const int InputHeight    = 24;        // 标准输入框高
    public const int InputGap       = 32;        // 输入行垂直间距
    public const int ControlLeft    = 112;        // 第一列控件左偏移
    public const int LabelTopOffset = 3;         // 标签相对输入框的垂直微调

    // ==================== 字体（保留原命名，全部基于系统 UI 字体） ====================
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

    // ==================== 绘制小工具（保留签名） ====================

    /// <summary>原生风格无圆角路径：直接返回矩形路径（Radius 恒为 0，兼容旧调用）。</summary>
    public static GraphicsPath Rounded(Rectangle r, int radius = Radius) => MaintenanceDraw.Rounded(r, radius);

    /// <summary>
    /// 原生风格卡片：面板底色 + 描边（无圆角、无强调色条）；暗黑下用深色面板 + 深色描边。
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
    /// 原生风格状态胶囊（顶栏用）：面板底色 + 彩色文字 + 彩色圆点，无半透明发光。
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
    /// 按钮：系统默认外观（FlatStyle.System）。
    /// </summary>
    public static Button MakeButton(string text, int width = 88, bool primary = false)
    {
        var b = new Button
        {
            Text = text, Width = width, Height = 30,
            Font = Body, Cursor = Cursors.Hand, Margin = new Padding(0, 3, 6, 3),
            FlatStyle = FlatStyle.System,   // 系统绘制按钮
            BackColor = primary ? PrimaryDim : Surface,
            ForeColor = primary ? Color.White : TextMain,
        };
        return b;
    }

    // ==================== 递归上主题（保留行为：只刷外观，绝不碰布局） ====================

    /// <summary>把控件的 <see cref="Control.Tag"/> 设为它，<see cref="Apply"/> 就不动这个控件（及其子树）。</summary>
    public const string SkipTag = "no-theme";

    /// <summary>
    /// 把一棵控件树刷成统一风格（颜色 / 字体 / 按钮 / 输入框 / 表格）。
    ///
    /// ⚠ 铁律不变：**绝不碰布局** —— 不改 Bounds / Dock / Anchor / Padding / Margin。
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
        b.FlatStyle = FlatStyle.System;   // 系统绘制按钮
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
        dg.AlternatingRowsDefaultCellStyle.BackColor = AltRowA;
        dg.RowHeadersDefaultCellStyle.BackColor = GridHeaderBg;
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

    /// <summary>
    /// 是不是“没人改过/由本类管理”的颜色（只有这种才能被覆盖）。
    /// </summary>
    private static bool IsDefaultish(Color c) =>
        c == SystemColors.Control || c == SystemColors.ControlText ||
        c == SystemColors.Window || c == SystemColors.WindowText ||
        c == Color.Empty || c == Color.Transparent ||
        c == Color.Black || c == Color.White;
}

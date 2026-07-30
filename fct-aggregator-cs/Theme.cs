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
    /// <summary>工作区底色（浅灰，衬托白色卡片）。</summary>
    public static readonly Color Bg = Color.FromArgb(243, 245, 249);
    /// <summary>卡片/面板底色。</summary>
    public static readonly Color Surface = Color.White;
    /// <summary>侧边导航底色（深蓝灰）。</summary>
    public static readonly Color NavBg = Color.FromArgb(28, 39, 54);
    public static readonly Color NavHover = Color.FromArgb(40, 54, 73);
    public static readonly Color NavActive = Color.FromArgb(46, 104, 168);
    public static readonly Color NavText = Color.FromArgb(196, 206, 219);
    public static readonly Color NavTextActive = Color.White;

    public static readonly Color Border = Color.FromArgb(223, 228, 235);
    public static readonly Color TextMain = Color.FromArgb(30, 41, 57);
    public static readonly Color TextSub = Color.FromArgb(98, 112, 132);
    public static readonly Color TextFaint = Color.FromArgb(150, 160, 175);

    public static readonly Color Primary = Color.FromArgb(37, 99, 165);
    public static readonly Color Success = Color.FromArgb(34, 134, 88);
    public static readonly Color Warning = Color.FromArgb(202, 138, 4);
    public static readonly Color Danger = Color.FromArgb(197, 48, 48);
    public static readonly Color Info = Color.FromArgb(13, 138, 145);
    public static readonly Color Neutral = Color.FromArgb(113, 128, 150);

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
}

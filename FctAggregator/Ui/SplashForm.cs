using System.Diagnostics;
using System.Drawing.Imaging;

namespace FctAggregator;

/// <summary>
/// 开启动画 v4（2026-08-01）——为「丝滑 / 高帧率」优化：
///   · **mipmap 预缩放**：构造时把 1408×768 源图预缩成 6 档（1/1, 1/2 … 1/32），
///     缩小阶段从「目标尺寸 ≥ mip 宽」的最小档取帧，不再每帧从大源图缩放（单帧成本 <1ms）
///   · **Bilinear 快速插值**（HighQualityBicubic 最慢，换 Bilinear 视觉几乎无差）
///   · **保持阶段静止不重绘**（0.7~5.0s 画面不变）：自驱动循环只在渐强/缩小阶段跑，
///     保持结束用线程池定时器唤醒一次进入缩小 —— 总重绘 ~500 帧，CPU 可控
///   · 帧率不限制：OnPaint 自驱动（画完立即 Invalidate 下一帧），受绘制耗时+显示刷新率决定
///   · 窗口分辨率 = 图片原始分辨率（1:1，超屏按比例缩）；双缓冲无闪烁
///   · 动画完成后保持最小圆点画面，由主流程在主窗口展开后隐藏（无缝过渡）
/// 时间线：0~0.7s 渐强浮现 → 0.7~5.0s 保持 → 5.0~5.6s 缩小成圆点 → 保持圆点
/// </summary>
public class SplashForm : Form
{
    // ==================== 动画时间线（秒）====================
    public const double FadeInSec = 0.7;
    public const double HoldUntilSec = 5.0;
    public const double ShrinkSec = 0.6;
    public const double TotalSec = HoldUntilSec + ShrinkSec;

    /// <summary>素材原始尺寸（assets/splash/argus_splash.png，1408×768）。</summary>
    public const int ImgW = 1408, ImgH = 768;

    /// <summary>资源名以此结尾即认为是开启动画素材（与 csproj EmbeddedResource 匹配口径一致）。</summary>
    public const string ResourceSuffix = "argus_splash.png";

    /// <summary>从嵌入资源加载开启动画图；失败返回 null（此时跳过动画，不阻塞启动）。</summary>
    public static Image? TryLoadEmbedded()
    {
        try
        {
            var asm = typeof(SplashForm).Assembly;
            var name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;
            using var s = asm.GetManifestResourceStream(name);
            return s != null ? Image.FromStream(s) : null;
        }
        catch (Exception ex) { Logger.Warning($"读开启动画素材失败: {ex.Message}"); return null; }
    }

    private readonly Image _img;
    private readonly Stopwatch _sw = new();
    private readonly Size _full;
    private bool _finished;

    // 缓存绘制资源（避免每帧 new GDI 对象导致闪烁/抖动）
    private readonly SolidBrush _bg;
    private readonly ColorMatrix _cm = new();
    private readonly ImageAttributes _attr = new();

    /// <summary>mipmap 档（按目标绘制宽 ≥ mip 宽的最小档取帧，缩小阶段不碰大源图）。</summary>
    private readonly List<(int minW, Bitmap bmp)> _mips = new();

    /// <summary>保持阶段结束的唤醒定时器（线程池，避免 5s 静止期空转重绘）。</summary>
    private System.Threading.Timer? _holdTimer;

    /// <summary>动画是否结束（主流程轮询用）。完成后保持最小圆点画面，不自动关闭。</summary>
    public bool Finished => _finished;

    /// <summary>动画完成事件（外部在主窗口展开后隐藏本窗口）。</summary>
    public event Action? Completed;

    public SplashForm(Image img)
    {
        _img = img;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        ShowInTaskbar = false;
        // ⚠ 双缓冲：大图逐帧缩放重绘，不开双缓冲会闪烁
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        // 窗口分辨率 = 图片原始分辨率（1:1）；超过屏幕工作区时按比例缩小（如 1366×768 机台）
        var screen = Screen.PrimaryScreen!.WorkingArea;
        double ratio = Math.Min(1.0, Math.Min((double)screen.Width / ImgW, (double)screen.Height / ImgH));
        _full = new Size(Math.Max(200, (int)(ImgW * ratio)), Math.Max(110, (int)(ImgH * ratio)));
        Size = _full;

        _bg = new SolidBrush(Color.White);
        BuildMipmaps();
    }

    /// <summary>预缩放源图成 6 档 mipmap（缩小阶段取小图，单帧绘制 <1ms）。</summary>
    private void BuildMipmaps()
    {
        int w = Math.Max(4, ClientSize.Width);
        int h = Math.Max(2, ClientSize.Height);
        while (w >= 4)
        {
            var bmp = new Bitmap(_img, new Size(w, h));   // 高质量一次性缩放（构造期，非每帧）
            _mips.Add((w, bmp));
            w /= 2;
            h = Math.Max(1, h / 2);
        }
        _mips.Reverse();   // 小 → 大
    }

    /// <summary>启动动画（自驱动渲染循环开始）。</summary>
    public void Start()
    {
        _sw.Restart();
        Show();
        BringToFront();
        Invalidate();   // 触发渐强阶段第一帧；OnPaint 末尾自驱动续帧
    }

    // ==================== 缓动 ====================
    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    private static double EaseInCubic(double t) => t * t * t;

    /// <summary>按经过秒数计算动画状态。纯函数（自检直接断言关键时间点）。</summary>
    /// <param name="elapsed">经过秒数。</param>
    /// <returns>(透明度 0~1, 图片缩放比例)。透明度由窗口 Opacity（DWM 合成，免费）承担，图片绘制恒 alpha=1 以提速。</returns>
    public static (double opacity, double scale) Compute(double elapsed)
    {
        if (elapsed < FadeInSec)                       // 渐强：透明度 EaseOut（scale 恒 1，全尺寸覆盖不 Clear，帧率拉满）
            return (EaseOutCubic(elapsed / FadeInSec), 1.0);
        if (elapsed < HoldUntilSec) return (1.0, 1.0); // 保持
        var k = Math.Min(1.0, (elapsed - HoldUntilSec) / ShrinkSec);
        var s = EaseInCubic(k);                        // 缩小：EaseIn 加速收拢
        return (1.0 - s, 1.0 - 0.95 * s);              // 缩到 5% + 渐隐到 0
    }

    /// <summary>取目标绘制宽对应的 mipmap（选 minW ≥ 目标宽 的最小档，避免放大模糊）。</summary>
    private Bitmap PickMip(int targetW)
    {
        Bitmap best = _mips[^1].bmp;
        foreach (var (minW, bmp) in _mips)
        {
            if (minW >= targetW) return bmp;
            best = bmp;
        }
        return best;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;   // 快速插值（Bicubic 最慢）
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var elapsed = _sw.IsRunning ? _sw.Elapsed.TotalSeconds : 0;
        var (opacity, scale) = Compute(elapsed);
        Opacity = Math.Clamp(opacity, 0.02, 1.0);   // 透明度交给 DWM 合成（免费），图片绘制恒 alpha=1

        if (scale >= 0.999)
        {
            // 渐强 / 保持：全尺寸图直接覆盖整个窗口（不 Clear，省一次全尺寸填充 → 帧率拉满）
            var mip = _mips[^1].bmp;
            g.DrawImage(mip, ClientRectangle, 0, 0, mip.Width, mip.Height, GraphicsUnit.Pixel);
        }
        else if (opacity > 0.001)
        {
            // 缩小阶段：白底 + mip 小图缩放绘制（<1ms/帧）
            g.FillRectangle(_bg, ClientRectangle);
            var dw = Math.Max(1, (int)(ClientSize.Width * scale));
            var dh = Math.Max(1, (int)(ClientSize.Height * scale));
            var dest = new Rectangle((ClientSize.Width - dw) / 2, (ClientSize.Height - dh) / 2, dw, dh);
            var mip = PickMip(dw);
            g.DrawImage(mip, dest, 0, 0, mip.Width, mip.Height, GraphicsUnit.Pixel);
        }

        if (elapsed < FadeInSec || elapsed >= HoldUntilSec)
        {
            // 渐强 / 缩小阶段：自驱动连续重绘（帧率不限制）
            if (elapsed < TotalSec) Invalidate();
            else if (!_finished)
            {
                _finished = true;
                Completed?.Invoke();
            }
        }
        else if (elapsed < HoldUntilSec)
        {
            // 保持阶段：画面静止不空转 —— 用线程池定时器在 5.0s 唤醒一次进入缩小
            _holdTimer ??= new System.Threading.Timer(_ =>
            {
                try { BeginInvoke(() => Invalidate()); } catch { }
            }, null, (int)((HoldUntilSec - elapsed) * 1000), System.Threading.Timeout.Infinite);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _holdTimer?.Dispose();
            foreach (var (_, bmp) in _mips) bmp.Dispose();
            _mips.Clear();
            _bg.Dispose();
            _attr.Dispose();
            _img.Dispose();   // SplashForm 拥有图片所有权
        }
        base.Dispose(disposing);
    }
}

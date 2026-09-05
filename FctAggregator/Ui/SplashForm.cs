using System.Diagnostics;
using System.Drawing.Imaging;

namespace FctAggregator;

public class SplashForm : Form
{
    public const double FadeInSec = 0.7;
    public const double HoldUntilSec = 5.0;
    public const double ShrinkSec = 0.6;
    public const double TotalSec = HoldUntilSec + ShrinkSec;

    public const int ImgW = 1408, ImgH = 768;

    public const string ResourceSuffix = "argus_splash.png";

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

    private readonly SolidBrush _bg;
    private readonly ColorMatrix _cm = new();
    private readonly ImageAttributes _attr = new();

    private readonly List<(int minW, Bitmap bmp)> _mips = new();

    private System.Threading.Timer? _holdTimer;

    public bool Finished => _finished;

    public event Action? Completed;

    public SplashForm(Image img)
    {
        _img = img;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        var screen = Screen.PrimaryScreen!.WorkingArea;
        double ratio = Math.Min(1.0, Math.Min((double)screen.Width / ImgW, (double)screen.Height / ImgH));
        _full = new Size(Math.Max(200, (int)(ImgW * ratio)), Math.Max(110, (int)(ImgH * ratio)));
        Size = _full;

        _bg = new SolidBrush(Color.White);
        BuildMipmaps();
    }

    private void BuildMipmaps()
    {
        int w = Math.Max(4, ClientSize.Width);
        int h = Math.Max(2, ClientSize.Height);
        while (w >= 4)
        {
            var bmp = new Bitmap(_img, new Size(w, h));
            _mips.Add((w, bmp));
            w /= 2;
            h = Math.Max(1, h / 2);
        }
        _mips.Reverse();
    }

    public void Start()
    {
        _sw.Restart();
        Show();
        BringToFront();
        Invalidate();
    }

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    private static double EaseInCubic(double t) => t * t * t;

    public static (double opacity, double scale) Compute(double elapsed)
    {
        if (elapsed < FadeInSec)
            return (EaseOutCubic(elapsed / FadeInSec), 1.0);
        if (elapsed < HoldUntilSec) return (1.0, 1.0);
        var k = Math.Min(1.0, (elapsed - HoldUntilSec) / ShrinkSec);
        var s = EaseInCubic(k);
        return (1.0 - s, 1.0 - 0.95 * s);
    }

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
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var elapsed = _sw.IsRunning ? _sw.Elapsed.TotalSeconds : 0;
        var (opacity, scale) = Compute(elapsed);
        Opacity = Math.Clamp(opacity, 0.02, 1.0);

        if (scale >= 0.999)
        {
            var mip = _mips[^1].bmp;
            g.DrawImage(mip, ClientRectangle, 0, 0, mip.Width, mip.Height, GraphicsUnit.Pixel);
        }
        else if (opacity > 0.001)
        {
            g.FillRectangle(_bg, ClientRectangle);
            var dw = Math.Max(1, (int)(ClientSize.Width * scale));
            var dh = Math.Max(1, (int)(ClientSize.Height * scale));
            var dest = new Rectangle((ClientSize.Width - dw) / 2, (ClientSize.Height - dh) / 2, dw, dh);
            var mip = PickMip(dw);
            g.DrawImage(mip, dest, 0, 0, mip.Width, mip.Height, GraphicsUnit.Pixel);
        }

        if (elapsed < FadeInSec || elapsed >= HoldUntilSec)
        {
            if (elapsed < TotalSec) Invalidate();
            else if (!_finished)
            {
                _finished = true;
                Completed?.Invoke();
            }
        }
        else if (elapsed < HoldUntilSec)
        {
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
            _img.Dispose();
        }
        base.Dispose(disposing);
    }
}

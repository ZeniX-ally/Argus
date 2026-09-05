using System.Drawing.Drawing2D;

namespace FctTdmsViewer;

public sealed class WaveformPanel : Panel
{
    public sealed class Series
    {
        public string Name = "";
        public double[] Data = Array.Empty<double>();
        public double Increment;
        public Color Color;
    }

    private readonly List<Series> _series = new();
    private static readonly Color[] Palette =
    {
        Color.FromArgb(20, 20, 20), Color.FromArgb(200, 16, 46),
        Color.FromArgb(120, 120, 120), Color.FromArgb(143, 11, 32),
        Color.FromArgb(179, 179, 179), Color.FromArgb(89, 89, 89),
        Color.FromArgb(228, 88, 108), Color.FromArgb(60, 60, 60),
    };

    private const int PadL = 68, PadR = 14, PadT = 14, PadB = 40;

    private double _x0, _x1;
    private Point? _mouse;
    private Point? _dragStart;
    private double _dragX0, _dragX1;

    public WaveformPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        MouseMove += (_, e) =>
        {
            _mouse = e.Location;
            if (_dragStart.HasValue)
            {
                int dx = e.X - _dragStart.Value.X;
                double span = _dragX1 - _dragX0;
                double perPx = span / Math.Max(1, PlotW);
                double shift = -dx * perPx;
                _x0 = _dragX0 + shift;
                _x1 = _dragX1 + shift;
                ClampView();
            }
            Invalidate();
        };
        MouseLeave += (_, _) => { _mouse = null; Invalidate(); };
        MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragStart = e.Location;
                _dragX0 = _x0; _dragX1 = _x1;
                Cursor = Cursors.SizeWE;
            }
        };
        MouseUp += (_, _) => { _dragStart = null; Cursor = Cursors.Default; };
        MouseWheel += (_, e) =>
        {
            if (MaxLen == 0) return;
            double center = _x0 + (_x1 - _x0) * (e.X - PadL) / Math.Max(1, PlotW);
            double factor = e.Delta > 0 ? 0.8 : 1.25;
            double half = (_x1 - _x0) * factor / 2;
            if (half < 2) half = 2;
            _x0 = center - half;
            _x1 = center + half;
            ClampView();
            Invalidate();
        };
        DoubleClick += (_, _) => { ResetView(); Invalidate(); };
    }

    private int PlotW => Math.Max(1, Width - PadL - PadR);
    private int PlotH => Math.Max(1, Height - PadT - PadB);
    private int MaxLen => _series.Count == 0 ? 0 : _series.Max(s => s.Data.Length);

    public void SetSeries(IEnumerable<(string name, double[] data, double inc)> items)
    {
        _series.Clear();
        int i = 0;
        foreach (var (name, data, inc) in items)
        {
            _series.Add(new Series
            {
                Name = name, Data = data, Increment = inc,
                Color = Palette[i % Palette.Length],
            });
            i++;
        }
        ResetView();
        Invalidate();
    }

    public void Clear()
    {
        _series.Clear();
        Invalidate();
    }

    public void ResetView()
    {
        _x0 = 0;
        _x1 = Math.Max(1, MaxLen - 1);
    }

    private void ClampView()
    {
        int n = MaxLen;
        if (n == 0) return;
        double span = _x1 - _x0;
        if (span > n - 1) { _x0 = 0; _x1 = n - 1; return; }
        if (_x0 < 0) { _x0 = 0; _x1 = span; }
        if (_x1 > n - 1) { _x1 = n - 1; _x0 = n - 1 - span; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        if (_series.Count == 0 || MaxLen == 0)
        {
            using var f = new Font("微软雅黑", 10F);
            TextRenderer.DrawText(g, "在左侧勾选通道以显示波形\n（滚轮缩放 · 拖拽平移 · 双击复位）",
                f, new Rectangle(0, 0, Width, Height), Color.Silver,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        double ymin = double.MaxValue, ymax = double.MinValue;
        foreach (var s in _series)
        {
            int a = (int)Math.Floor(_x0), b = (int)Math.Ceiling(_x1);
            for (int i = Math.Max(0, a); i <= Math.Min(s.Data.Length - 1, b); i++)
            {
                var v = s.Data[i];
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                if (v < ymin) ymin = v;
                if (v > ymax) ymax = v;
            }
        }
        if (ymin > ymax) { ymin = 0; ymax = 1; }
        if (Math.Abs(ymax - ymin) < 1e-12) { ymin -= 0.5; ymax += 0.5; }
        double pad = (ymax - ymin) * 0.08;
        ymin -= pad; ymax += pad;

        var plot = new Rectangle(PadL, PadT, PlotW, PlotH);
        using var axisPen = new Pen(Color.FromArgb(140, 140, 140));
        using var gridPen = new Pen(Color.FromArgb(232, 232, 232));
        using var font = new Font("Consolas", 8F);

        for (int i = 0; i <= 5; i++)
        {
            int y = plot.Bottom - i * plot.Height / 5;
            g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            double val = ymin + (ymax - ymin) * i / 5;
            TextRenderer.DrawText(g, FormatNum(val), font,
                new Rectangle(0, y - 8, PadL - 6, 16), Color.DimGray,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
        double inc = _series[0].Increment;
        for (int i = 0; i <= 6; i++)
        {
            int x = plot.Left + i * plot.Width / 6;
            g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
            double idx = _x0 + (_x1 - _x0) * i / 6;
            string lbl = inc > 0 ? $"{idx * inc:F2}s" : $"{idx:F0}";
            TextRenderer.DrawText(g, lbl, font,
                new Rectangle(x - 40, plot.Bottom + 4, 80, 16), Color.DimGray,
                TextFormatFlags.HorizontalCenter);
        }
        g.DrawRectangle(axisPen, plot);

        foreach (var s in _series)
        {
            if (s.Data.Length < 1) continue;
            using var pen = new Pen(s.Color, 1.6f);
            var pts = new List<PointF>();
            int a = Math.Max(0, (int)Math.Floor(_x0));
            int b = Math.Min(s.Data.Length - 1, (int)Math.Ceiling(_x1));
            int stride = Math.Max(1, (b - a + 1) / Math.Max(1, plot.Width));
            for (int i = a; i <= b; i += stride)
            {
                var v = s.Data[i];
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                pts.Add(new PointF(XToPx(i, plot), YToPx(v, ymin, ymax, plot)));
            }
            if (pts.Count == 1)
                g.FillEllipse(new SolidBrush(s.Color), pts[0].X - 2.5f, pts[0].Y - 2.5f, 5, 5);
            else if (pts.Count > 1)
                g.DrawLines(pen, pts.ToArray());
        }

        using var lf = new Font("微软雅黑", 8.5F);
        int ly = plot.Top + 4;
        foreach (var s in _series)
        {
            using var b2 = new SolidBrush(s.Color);
            g.FillRectangle(b2, plot.Left + 8, ly + 4, 18, 3);
            TextRenderer.DrawText(g, s.Name, lf, new Point(plot.Left + 30, ly - 1), s.Color);
            ly += 16;
            if (ly > plot.Bottom - 16) break;
        }

        if (_mouse.HasValue && plot.Contains(_mouse.Value))
        {
            int mx = _mouse.Value.X;
            using var cross = new Pen(Color.FromArgb(120, 200, 60, 60)) { DashStyle = DashStyle.Dash };
            g.DrawLine(cross, mx, plot.Top, mx, plot.Bottom);
            int idx = (int)Math.Round(_x0 + (_x1 - _x0) * (mx - plot.Left) / plot.Width);
            var lines = new List<string>();
            string xl = inc > 0 ? $"t={idx * inc:F3}s (#{idx})" : $"#{idx}";
            lines.Add(xl);
            foreach (var s in _series)
            {
                if (idx < 0 || idx >= s.Data.Length) continue;
                lines.Add($"{Trunc(s.Name, 26)} = {FormatNum(s.Data[idx])}");
                var py = YToPx(s.Data[idx], ymin, ymax, plot);
                using var bb = new SolidBrush(s.Color);
                g.FillEllipse(bb, XToPx(idx, plot) - 3, py - 3, 6, 6);
            }
            var text = string.Join("\n", lines);
            var sz = TextRenderer.MeasureText(text, font);
            int bx = mx + 12, by = plot.Top + 8;
            if (bx + sz.Width + 10 > plot.Right) bx = mx - sz.Width - 18;
            using var bg = new SolidBrush(Color.FromArgb(238, 255, 255, 255));
            using var bp = new Pen(Color.FromArgb(180, 180, 180));
            var box = new Rectangle(bx, by, sz.Width + 10, sz.Height + 8);
            g.FillRectangle(bg, box);
            g.DrawRectangle(bp, box);
            TextRenderer.DrawText(g, text, font,
                new Rectangle(bx + 5, by + 4, sz.Width, sz.Height), Color.Black,
                TextFormatFlags.Left);
        }
    }

    private float XToPx(double idx, Rectangle plot)
        => (float)(plot.Left + (idx - _x0) / Math.Max(1e-9, _x1 - _x0) * plot.Width);

    private static float YToPx(double v, double ymin, double ymax, Rectangle plot)
        => (float)(plot.Bottom - (v - ymin) / Math.Max(1e-12, ymax - ymin) * plot.Height);

    private static string FormatNum(double v)
    {
        double a = Math.Abs(v);
        if (a != 0 && (a < 1e-3 || a >= 1e6)) return v.ToString("0.###e+0");
        return v.ToString("0.####");
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}

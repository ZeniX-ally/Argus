using System.Drawing.Drawing2D;

namespace FctAggregator;

/// <summary>PASS 偏离折线：纵轴 |z|，横轴每条 PASS（共享时间序）。</summary>
public sealed class SignalDeviationChart : Control
{
    private List<string> _passLabels = new();
    private List<DeviationSeries> _series = new();
    private string? _emptyHint;
    private string? _statusHint;
    private int _hoverSeries = -1;
    private int _hoverPass = -1;

    public SignalDeviationChart()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Surface;
        Height = 320;
    }

    public void SetData(List<string> passLabels, List<DeviationSeries> series, string? emptyHint = null, string? statusHint = null)
    {
        _passLabels = passLabels ?? new List<string>();
        _series = series ?? new List<DeviationSeries>();
        _emptyHint = series is null or { Count: 0 } ? emptyHint : null;
        _statusHint = series is { Count: > 0 } ? statusHint : null;
        _hoverSeries = _hoverPass = -1;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!TryHit(e.X, e.Y, out var si, out var pi)) { ClearHover(); return; }
        if (_hoverSeries != si || _hoverPass != pi) { _hoverSeries = si; _hoverPass = pi; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        ClearHover();
    }

    private void ClearHover()
    {
        if (_hoverSeries == -1) return;
        _hoverSeries = _hoverPass = -1;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        Theme.DrawCard(g, ClientRectangle, null);

        TextRenderer.DrawText(g, "PASS 偏离趋势（纵轴 |z|，横轴每条 PASS）", Theme.BodyBold,
            new Rectangle(16, 10, 420, 22), Theme.TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        var sub = "相对正常态模型；虚线 z=3 / z=6";
        if (_series.Count > 0)
            sub += $"  ·  {_series.Count} 条测项 / {_passLabels.Count} 条 PASS";
        if (!string.IsNullOrWhiteSpace(_statusHint))
            sub += $"  ·  {_statusHint}";
        TextRenderer.DrawText(g, sub, Theme.Tiny,
            new Rectangle(16, 30, Width - 32, 16), Theme.TextFaint, TextFormatFlags.EndEllipsis);

        int chartLeft = 48, chartRight = Width - 24, chartTop = 52, chartBottom = Height - 42;
        int chartW = chartRight - chartLeft, chartH = chartBottom - chartTop;
        if (chartW < 40 || chartH < 40) return;

        if (_series.Count == 0 || _passLabels.Count == 0)
        {
            var hint = string.IsNullOrWhiteSpace(_emptyHint)
                ? "暂无数据——需 PASS 测量 + ready 正常态模型（先回填历史）"
                : _emptyHint!;
            TextRenderer.DrawText(g, hint, Theme.Body,
                new Rectangle(chartLeft, chartTop + 24, chartW, 56), Theme.TextFaint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            return;
        }

        double maxZ = 3.0;
        foreach (var ser in _series)
            foreach (var (_, pt) in ser.Points)
                if (pt.Z > maxZ) maxZ = pt.Z;
        maxZ = Math.Max(6.5, Math.Ceiling(maxZ));

        using var gridPen = new Pen(Color.FromArgb(38, Theme.TextFaint), 1f) { DashStyle = DashStyle.Dot };
        for (int i = 0; i <= 4; i++)
        {
            float y = chartBottom - chartH * (i / 4f);
            g.DrawLine(gridPen, chartLeft, y, chartRight, y);
            double zv = maxZ * (i / 4.0);
            TextRenderer.DrawText(g, zv.ToString("F1"), Theme.Tiny,
                new Rectangle(4, (int)y - 8, chartLeft - 8, 16), Theme.TextFaint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        DrawThreshold(g, chartLeft, chartRight, chartTop, chartH, 3.0, maxZ, Theme.Warning);
        DrawThreshold(g, chartLeft, chartRight, chartTop, chartH, 6.0, maxZ, Theme.Danger);

        using var axisPen = new Pen(Color.FromArgb(80, Theme.TextFaint), 1f);
        g.DrawLine(axisPen, chartLeft, chartBottom, chartRight, chartBottom);
        g.DrawLine(axisPen, chartLeft, chartTop, chartLeft, chartBottom);

        int nPass = Math.Max(1, _passLabels.Count);
        float xSlot = (float)chartW / Math.Max(1, nPass - 1);

        DrawLegend(g, chartRight, chartTop);

        float lineW = _series.Count switch { <= 12 => 2f, <= 40 => 1.5f, _ => 1f };
        int dotR = _series.Count <= 40 ? 2 : 1;
        bool drawDots = _series.Count <= 60;
        for (int si = 0; si < _series.Count; si++)
        {
            var ser = _series[si];
            var col = ColorFromHex(DeviationSeriesBuilder.SeriesColorHex(ser.TestName));
            var pts = new List<PointF>();
            foreach (var (xi, p) in ser.Points)
            {
                float x = nPass <= 1 ? chartLeft : chartLeft + xi * xSlot;
                float y = chartBottom - (float)(p.Z / maxZ * chartH);
                pts.Add(new PointF(x, y));
            }
            if (pts.Count == 0) continue;
            int alpha = _series.Count > 40 ? 120 : 200;
            using var linePen = new Pen(Color.FromArgb(alpha, col), si == _hoverSeries ? lineW + 1f : lineW);
            if (pts.Count == 1)
            {
                using var singleBrush = new SolidBrush(col);
                g.FillEllipse(singleBrush, pts[0].X - 3, pts[0].Y - 3, 6, 6);
            }
            else
                g.DrawLines(linePen, pts.ToArray());
            if (drawDots)
            {
                using var dotBrush = new SolidBrush(col);
                foreach (var pt in pts)
                    g.FillEllipse(dotBrush, pt.X - dotR, pt.Y - dotR, dotR * 2, dotR * 2);
            }
        }

        int labelStep = nPass <= 12 ? 1 : Math.Max(1, nPass / 10);
        for (int i = 0; i < nPass; i += labelStep)
        {
            float x = nPass <= 1 ? chartLeft : chartLeft + i * xSlot;
            var lbl = i < _passLabels.Count ? _passLabels[i] : "";
            TextRenderer.DrawText(g, lbl, Theme.Tiny,
                new Rectangle((int)x - 28, chartBottom + 4, 56, 16), Theme.TextFaint,
                TextFormatFlags.HorizontalCenter);
        }

        if (_hoverSeries >= 0 && _hoverPass >= 0 && TryGetPoint(_hoverSeries, _hoverPass, out var hp))
        {
            float hx = nPass <= 1 ? chartLeft : chartLeft + hp.PassIndex * xSlot;
            float hy = chartBottom - (float)(hp.Point.Z / maxZ * chartH);
            using var tipBg = new SolidBrush(Color.FromArgb(240, 30, 34, 42));
            using var tipPen = new Pen(Color.FromArgb(120, Theme.Primary));
            var tip = $"{hp.Point.TestName}\n{hp.Point.Ts}\n值 {hp.Point.Value:F4}  |z| {hp.Point.Z:F2}";
            var sz = TextRenderer.MeasureText(tip, Theme.Small);
            var rect = new Rectangle((int)hx + 8, (int)hy - sz.Height - 8, sz.Width + 12, sz.Height + 8);
            if (rect.Right > Width - 8) rect.X = (int)hx - rect.Width - 8;
            g.FillRectangle(tipBg, rect);
            g.DrawRectangle(tipPen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            TextRenderer.DrawText(g, tip, Theme.Small, rect, Theme.TextMain,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    private void DrawLegend(Graphics g, int chartRight, int chartTop)
    {
        if (_series.Count == 0) return;
        int legX = chartRight - 200;
        int legY = chartTop + 4;
        const int maxShow = 8;
        if (_series.Count > maxShow)
        {
            TextRenderer.DrawText(g, $"共 {_series.Count} 条测项", Theme.Tiny,
                new Rectangle(legX, legY, 190, 16), Theme.TextSub, TextFormatFlags.Right);
            legY += 18;
            TextRenderer.DrawText(g, "（悬停折线查看详情）", Theme.Tiny,
                new Rectangle(legX, legY, 190, 16), Theme.TextFaint, TextFormatFlags.Right);
            return;
        }
        for (int si = 0; si < _series.Count; si++)
        {
            var col = ColorFromHex(DeviationSeriesBuilder.SeriesColorHex(_series[si].TestName));
            using var lb = new SolidBrush(col);
            g.FillRectangle(lb, legX, legY + si * 16, 8, 8);
            var shortName = _series[si].TestName.Length > 20 ? _series[si].TestName[..20] + "…" : _series[si].TestName;
            TextRenderer.DrawText(g, shortName, Theme.Tiny,
                new Rectangle(legX + 12, legY + si * 16 - 2, 178, 14), Theme.TextSub,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    private static void DrawThreshold(Graphics g, int left, int right, int top, int h, double z, double maxZ, Color col)
    {
        float y = top + h - (float)(z / maxZ * h);
        using var pen = new Pen(Color.FromArgb(140, col), 1f) { DashStyle = DashStyle.Dash };
        g.DrawLine(pen, left, y, right, y);
    }

    private bool TryHit(int mx, int my, out int seriesIndex, out int passIndex)
    {
        seriesIndex = passIndex = -1;
        int chartLeft = 48, chartRight = Width - 24, chartTop = 52, chartBottom = Height - 42;
        if (mx < chartLeft || mx > chartRight || my < chartTop || my > chartBottom) return false;
        int nPass = Math.Max(1, _passLabels.Count);
        float xSlot = (float)(chartRight - chartLeft) / Math.Max(1, nPass - 1);
        double maxZ = 6.5;
        foreach (var ser in _series)
            foreach (var (_, pt) in ser.Points)
                if (pt.Z > maxZ) maxZ = pt.Z;
        maxZ = Math.Max(6.5, Math.Ceiling(maxZ));
        int chartH = chartBottom - chartTop;
        float best = 14f;
        for (int si = 0; si < _series.Count; si++)
        {
            foreach (var (xi, pt) in _series[si].Points)
            {
                float x = nPass <= 1 ? chartLeft : chartLeft + xi * xSlot;
                float y = chartBottom - (float)(pt.Z / maxZ * chartH);
                float d = (mx - x) * (mx - x) + (my - y) * (my - y);
                if (d < best * best) { best = (float)Math.Sqrt(d); seriesIndex = si; passIndex = xi; }
            }
        }
        return seriesIndex >= 0;
    }

    private bool TryGetPoint(int si, int passIdx, out (int PassIndex, PassDeviationPoint Point) hp)
    {
        hp = default;
        if (si < 0 || si >= _series.Count) return false;
        foreach (var p in _series[si].Points)
            if (p.PassIndex == passIdx) { hp = p; return true; }
        return false;
    }

    private static Color ColorFromHex(string hex) => ColorTranslator.FromHtml(hex);
}

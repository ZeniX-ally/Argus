using System.Drawing.Drawing2D;

namespace FctAggregator;

/// <summary>
/// 分析页趋势图：横轴 = 用户选定时间范围（自动分桶），纵轴 = 相对规格带位置（0=下限，1=上限）。
/// 多测项各自按自己的限值归一化后叠加，限值即纵轴的 0/1 两条参考线；单边限按窗口数据量程合成。
/// </summary>
public sealed class MeasurementTrendChart : Control
{
    private TrendResult? _data;
    private List<TrendSeries> _series = new();
    private string? _emptyHint;
    private string? _statusHint;
    private int _hoverSeries = -1;
    private int _hoverBucket = -1;

    public MeasurementTrendChart()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Surface;
        Height = 320;
    }

    public void SetData(TrendResult? data, List<TrendSeries> series, string? emptyHint = null, string? statusHint = null)
    {
        _data = data;
        _series = series ?? new List<TrendSeries>();
        _emptyHint = series is null or { Count: 0 } ? emptyHint : null;
        _statusHint = series is { Count: > 0 } ? statusHint : null;
        _hoverSeries = _hoverBucket = -1;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!TryHit(e.X, e.Y, out var si, out var bi)) { ClearHover(); return; }
        if (_hoverSeries != si || _hoverBucket != bi) { _hoverSeries = si; _hoverBucket = bi; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        ClearHover();
    }

    private void ClearHover()
    {
        if (_hoverSeries == -1) return;
        _hoverSeries = _hoverBucket = -1;
        Invalidate();
    }

    /// <summary>桶宽文案。</summary>
    public static string FormatBucketSpan(long seconds)
    {
        if (seconds < 3600) return $"{seconds / 60} 分钟";
        if (seconds < 86400) return $"{seconds / 3600} 小时";
        if (seconds % 86400 == 0) return $"{seconds / 86400} 天";
        return $"{seconds / 3600} 小时";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        Theme.DrawCard(g, ClientRectangle, null);

        TextRenderer.DrawText(g, "测项趋势（纵轴 = 相对规格带位置：0=下限，1=上限）", Theme.BodyBold,
            new Rectangle(16, 10, 520, 22), Theme.TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        var sub = _data == null
            ? ""
            : $"{_data.From:MM-dd} ~ {_data.ToExclusive.AddDays(-1):MM-dd} · 桶宽 {FormatBucketSpan(_data.BucketSeconds)}" +
              $" · 已绘 {_series.Count}/{_data.Series.Count} 条";
        if (_data != null && _data.SingleSidedCount > 0)
            sub += $" · 含 {_data.SingleSidedCount} 个单边限测项（缺失端按窗口数据量程合成）";
        if (!string.IsNullOrWhiteSpace(_data?.Hint)) sub += "  ·  " + _data!.Hint;
        if (!string.IsNullOrWhiteSpace(_statusHint)) sub += "  ·  " + _statusHint;
        TextRenderer.DrawText(g, sub, Theme.Tiny,
            new Rectangle(16, 30, Width - 32, 16), Theme.TextFaint, TextFormatFlags.EndEllipsis);

        int chartLeft = 56, chartRight = Width - 24, chartTop = 54, chartBottom = Height - 42;
        int chartW = chartRight - chartLeft, chartH = chartBottom - chartTop;
        if (chartW < 40 || chartH < 40) return;

        if (_series.Count == 0 || _data == null || _data.BucketCount <= 0)
        {
            var hint = _emptyHint
                       ?? (string.IsNullOrWhiteSpace(_data?.Hint)
                           ? "暂无数据——需 PASS 测量（配置 analyze_collect_pass）且测项有限值；先回填历史。"
                           : _data!.Hint!);
            TextRenderer.DrawText(g, hint, Theme.Body,
                new Rectangle(chartLeft, chartTop + 24, chartW, 56), Theme.TextFaint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            return;
        }

        int bucketCount = _data.BucketCount;
        // 纵轴：保证 0（下限）与 1（上限）都在视野内，再按数据极值向外留 5% 余量
        double lo = 0, hi = 1;
        foreach (var ser in _series)
        {
            if (ser.MinNorm < lo) lo = ser.MinNorm;
            if (ser.MaxNorm > hi) hi = ser.MaxNorm;
        }
        double pad = Math.Max(0.05, (hi - lo) * 0.05);
        lo -= pad;
        hi += pad;

        float YOf(double norm) => chartBottom - (float)((norm - lo) / (hi - lo) * chartH);
        float XOf(int bucket) => bucketCount <= 1 ? chartLeft : chartLeft + (float)bucket / (bucketCount - 1) * chartW;

        // 合格带 [0, 1]
        float yHigh = YOf(1), yLow = YOf(0);
        using (var band = new SolidBrush(Color.FromArgb(26, Theme.Success)))
            g.FillRectangle(band, chartLeft, Math.Min(yHigh, yLow), chartW, Math.Abs(yLow - yHigh));

        using var gridPen = new Pen(Color.FromArgb(38, Theme.TextFaint), 1f) { DashStyle = DashStyle.Dot };
        for (int i = 0; i <= 4; i++)
        {
            float y = chartBottom - chartH * (i / 4f);
            g.DrawLine(gridPen, chartLeft, y, chartRight, y);
            double v = lo + (hi - lo) * (i / 4.0);
            TextRenderer.DrawText(g, v.ToString("F1"), Theme.Tiny,
                new Rectangle(4, (int)y - 8, chartLeft - 8, 16), Theme.TextFaint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        DrawLimitLine(g, chartLeft, chartRight, yLow, "下限", Theme.Danger);
        DrawLimitLine(g, chartLeft, chartRight, yHigh, "上限", Theme.Danger);

        using (var axisPen = new Pen(Color.FromArgb(80, Theme.TextFaint), 1f))
        {
            g.DrawLine(axisPen, chartLeft, chartBottom, chartRight, chartBottom);
            g.DrawLine(axisPen, chartLeft, chartTop, chartLeft, chartBottom);
        }

        DrawLegend(g, chartRight, chartTop);

        float lineW = _series.Count switch { <= 12 => 2f, <= 40 => 1.5f, _ => 1f };
        int dotR = _series.Count <= 40 ? 2 : 1;
        bool drawDots = _series.Count <= 60 && bucketCount <= 400;
        for (int si = 0; si < _series.Count; si++)
        {
            var ser = _series[si];
            var col = ColorFromHex(DeviationSeriesBuilder.SeriesColorHex(ser.TestName));
            int alpha = _series.Count > 40 ? 120 : 200;
            using var linePen = new Pen(Color.FromArgb(alpha, col), si == _hoverSeries ? lineW + 1f : lineW);
            using var dotBrush = new SolidBrush(col);

            // 空桶不能插值连过去，按连续桶号切段分别绘制
            var seg = new List<PointF>();
            int prevBucket = int.MinValue;
            foreach (var p in ser.Points)
            {
                if (seg.Count > 0 && p.Bucket != prevBucket + 1)
                {
                    FlushSeg(g, linePen, dotBrush, seg, drawDots, dotR);
                    seg.Clear();
                }
                seg.Add(new PointF(XOf(p.Bucket), YOf(p.Norm)));
                prevBucket = p.Bucket;
            }
            FlushSeg(g, linePen, dotBrush, seg, drawDots, dotR);
        }

        int labelStep = bucketCount <= 12 ? 1 : Math.Max(1, bucketCount / 10);
        for (int b = 0; b < bucketCount; b += labelStep)
        {
            var lbl = MeasurementTrendBuilder.FormatBucketLabel(_data.From, b, _data.BucketSeconds);
            TextRenderer.DrawText(g, lbl, Theme.Tiny,
                new Rectangle((int)XOf(b) - 32, chartBottom + 4, 64, 16), Theme.TextFaint,
                TextFormatFlags.HorizontalCenter);
        }

        if (_hoverSeries >= 0 && _hoverBucket >= 0)
            DrawTooltip(g, XOf(_hoverBucket), YOf(FindPoint(_hoverSeries, _hoverBucket)!.Norm));
    }

    private static void FlushSeg(Graphics g, Pen pen, Brush dotBrush, List<PointF> seg, bool drawDots, int dotR)
    {
        if (seg.Count == 0) return;
        if (seg.Count == 1)
        {
            g.FillEllipse(dotBrush, seg[0].X - 3, seg[0].Y - 3, 6, 6);
            return;
        }
        g.DrawLines(pen, seg.ToArray());
        if (drawDots)
            foreach (var pt in seg)
                g.FillEllipse(dotBrush, pt.X - dotR, pt.Y - dotR, dotR * 2, dotR * 2);
    }

    private static void DrawLimitLine(Graphics g, int left, int right, float y, string label, Color col)
    {
        using var pen = new Pen(Color.FromArgb(150, col), 1f) { DashStyle = DashStyle.Dash };
        g.DrawLine(pen, left, y, right, y);
        TextRenderer.DrawText(g, label, Theme.Tiny,
            new Rectangle(right - 34, (int)y - 16, 34, 14), col,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    private void DrawLegend(Graphics g, int chartRight, int chartTop)
    {
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
            var name = _series[si].TestName;
            var shortName = name.Length > 20 ? name[..20] + "…" : name;
            TextRenderer.DrawText(g, shortName, Theme.Tiny,
                new Rectangle(legX + 12, legY + si * 16 - 2, 178, 14), Theme.TextSub,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    private TrendPoint? FindPoint(int si, int bucket)
    {
        if (si < 0 || si >= _series.Count) return null;
        foreach (var p in _series[si].Points)
            if (p.Bucket == bucket) return p;
        return null;
    }

    private void DrawTooltip(Graphics g, float x, float y)
    {
        var ser = _series[_hoverSeries];
        var p = FindPoint(_hoverSeries, _hoverBucket);
        if (p == null) return;
        var when = MeasurementTrendBuilder.BucketTime(_data!.From, p.Bucket, _data.BucketSeconds);
        var unit = string.IsNullOrWhiteSpace(ser.Unit) ? "" : " " + ser.Unit;
        var pos = p.Norm >= 1
            ? $"{p.Norm:F2}（超上限 {(p.Norm - 1):F2}×带宽）"
            : p.Norm <= 0
                ? $"{p.Norm:F2}（低于下限 {(-p.Norm):F2}×带宽）"
                : $"{p.Norm:F2}（带内）";
        var band = $"限值 {Fmt(ser.BandLow)}{unit} ~ {Fmt(ser.BandHigh)}{unit}" +
                   (ser.IsSingleSided ? "（单边合成）" : "");
        var tip = $"{when:MM-dd HH:mm}  {ser.TestName}\n" +
                  $"段内均值 {Fmt(p.MeanValue)}{unit}  ·  {p.N} 个样本\n" +
                  $"{band}\n相对位置 {pos}";

        var sz = TextRenderer.MeasureText(tip, Theme.Small);
        var rect = new Rectangle((int)x + 8, (int)y - sz.Height - 8, sz.Width + 12, sz.Height + 8);
        if (rect.Right > Width - 8) rect.X = (int)x - rect.Width - 8;
        if (rect.Y < 4) rect.Y = 4;
        using var bg = new SolidBrush(Color.FromArgb(240, 30, 34, 42));
        using var pen = new Pen(Color.FromArgb(120, Theme.Primary));
        g.FillRectangle(bg, rect);
        g.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        TextRenderer.DrawText(g, tip, Theme.Small, rect, Theme.TextMain,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static string Fmt(double v)
        => Math.Abs(v) >= 1000 || (Math.Abs(v) > 0 && Math.Abs(v) < 0.001)
            ? v.ToString("0.####E+0")
            : v.ToString("0.####");

    private bool TryHit(int mx, int my, out int seriesIndex, out int bucketIndex)
    {
        seriesIndex = bucketIndex = -1;
        if (_data == null || _series.Count == 0) return false;
        int chartLeft = 56, chartRight = Width - 24, chartTop = 54, chartBottom = Height - 42;
        if (mx < chartLeft || mx > chartRight || my < chartTop || my > chartBottom) return false;

        int bucketCount = _data.BucketCount;
        float xSlot = bucketCount <= 1 ? 0 : (float)(chartRight - chartLeft) / (bucketCount - 1);
        int slot = xSlot <= 0 ? 0 : (int)Math.Round((mx - chartLeft) / xSlot);
        slot = Math.Clamp(slot, 0, bucketCount - 1);

        // 优先命中该桶上的任意曲线点
        for (int si = 0; si < _series.Count; si++)
        {
            var p = FindPoint(si, slot);
            if (p != null) { seriesIndex = si; bucketIndex = slot; return true; }
        }
        // 该桶无数据时，命中最近的一个桶
        int best = int.MaxValue;
        for (int si = 0; si < _series.Count; si++)
        {
            foreach (var p in _series[si].Points)
            {
                int d = Math.Abs(p.Bucket - slot);
                if (d < best) { best = d; seriesIndex = si; bucketIndex = p.Bucket; }
            }
        }
        return seriesIndex >= 0 && best <= 2;
    }

    private static Color ColorFromHex(string hex) => ColorTranslator.FromHtml(hex);
}

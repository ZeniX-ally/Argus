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
    private int _hoverX = -1;
    private string _paintSig = "";
    private bool _hasSig;

    public int PaintGeneration { get; private set; }
    public string LastPaintSig => _paintSig;

    public MeasurementTrendChart()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Surface;
        Height = 320;
    }

    public void SetData(TrendResult? data, List<TrendSeries> series, string? emptyHint = null, string? statusHint = null)
    {
        series ??= new List<TrendSeries>();
        var storedEmpty = series.Count == 0 ? emptyHint : null;
        var storedStatus = series.Count > 0 ? statusHint : null;
        var sig = TrendPaintSig(data, series, storedEmpty, storedStatus);
        if (_hasSig && sig == _paintSig) return;
        _hasSig = true;
        _paintSig = sig;
        PaintGeneration++;
        _data = data;
        _series = series;
        _emptyHint = storedEmpty;
        _statusHint = storedStatus;
        _hoverSeries = _hoverBucket = _hoverX = -1;
        Invalidate();
    }

    private static string TrendPaintSig(TrendResult? data, List<TrendSeries> series, string? emptyHint, string? statusHint)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(emptyHint).Append('\u0001').Append(statusHint).Append('\u0001');
        if (data != null)
            sb.Append(data.From.Ticks).Append(',').Append(data.ToExclusive.Ticks).Append(',')
                .Append(data.BucketSeconds).Append(',').Append(data.BucketCount);
        foreach (var s in series)
        {
            sb.Append('|').Append(s.TestName).Append(':').Append(s.Points.Count).Append('/')
                .Append(s.FailPoints.Count).Append('@').Append(s.Samples);
            foreach (var p in s.Points)
                sb.Append('#').Append(p.Bucket).Append(',').Append(p.MeanValue.ToString("R")).Append(',').Append(p.N);
            foreach (var p in s.FailPoints)
                sb.Append('!').Append(p.Bucket).Append(',').Append(p.MeanValue.ToString("R")).Append(',').Append(p.N);
        }
        return sb.ToString();
    }

    /// <summary>绘图区（与 OnPaint 同口径，避免两处硬编码漂移）。</summary>
    private (int Left, int Right, int Top, int Bottom) PlotRect()
        => (56, Width - 24, 54, Height - 42);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (l, r, t, b) = PlotRect();
        if (e.X < l || e.X > r || e.Y < t || e.Y > b) { ClearHover(); return; }
        // 垂直线以鼠标为准；数据命中失败时仍保留垂直线（只是不弹 tooltip）
        TryHit(e.X, e.Y, out var si, out var bi);
        if (_hoverX != e.X || _hoverSeries != si || _hoverBucket != bi)
        {
            _hoverX = e.X; _hoverSeries = si; _hoverBucket = bi;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        ClearHover();
    }

    private void ClearHover()
    {
        if (_hoverX < 0 && _hoverSeries == -1) return;
        _hoverSeries = _hoverBucket = _hoverX = -1;
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

    /// <summary>
    /// 纵轴：规格带 [0,1] + **仅 PASS** 极值。FAIL 掉坑不撑轴，否则 PASS 挤成一条线。
    /// </summary>
    public static (double Lo, double Hi) AxisRange(IReadOnlyList<TrendSeries>? series)
    {
        double lo = 0, hi = 1;
        if (series != null)
        {
            foreach (var ser in series)
            {
                if (ser.Points.Count == 0) continue;
                if (ser.PassMinNorm < lo) lo = ser.PassMinNorm;
                if (ser.PassMaxNorm > hi) hi = ser.PassMaxNorm;
            }
        }
        double pad = Math.Max(0.05, (hi - lo) * 0.05);
        return (lo - pad, hi + pad);
    }

    /// <summary>
    /// 横轴绘图区间：有点太靠窗口一端时缩到数据（两侧各留 1 桶）。覆盖 ≥60% 则用全窗口。
    /// </summary>
    public static (int FromBucket, int ToBucket) PlotBucketRange(IReadOnlyList<TrendSeries>? series, int bucketCount)
    {
        if (bucketCount <= 1) return (0, Math.Max(0, bucketCount - 1));
        int last = bucketCount - 1;
        int minB = int.MaxValue, maxB = int.MinValue;
        if (series != null)
        {
            foreach (var ser in series)
            {
                foreach (var p in ser.Points)
                {
                    if (p.Bucket < minB) minB = p.Bucket;
                    if (p.Bucket > maxB) maxB = p.Bucket;
                }
            }
        }
        if (minB == int.MaxValue) return (0, last);
        minB = Math.Max(0, minB - 1);
        maxB = Math.Min(last, maxB + 1);
        if (maxB - minB + 1 >= bucketCount * 0.6) return (0, last);
        return (minB, maxB);
    }

    /// <summary>
    /// GDI+ 坐标钳位：NaN/Inf/超大值会让 DrawEllipse/DrawLines 抛 OverflowException（红叉）。
    /// </summary>
    public static float ClampPlotCoord(double v, float min, float max, float fallback)
    {
        if (!double.IsFinite(v)) return fallback;
        if (v < min) return min;
        if (v > max) return max;
        return (float)v;
    }

    /// <summary>
    /// 纵轴口径：所有曲线共享同一规格带时直接用**实际值**（选单条测项时必然如此），
    /// 否则只能退回归一化相对位置（各测项量纲/量程不同，共用一个实际值轴没有意义）。
    /// </summary>
    public static bool UseActualValueAxis(List<TrendSeries>? series)
        => series is { Count: > 0 }
           && series.All(s => s.BandLow == series[0].BandLow && s.BandHigh == series[0].BandHigh);

    /// <summary>纵轴刻度文案：actual=true 时把相对位置反算回实际值，否则输出相对位置本身。</summary>
    public static string FormatAxisValue(double norm, TrendSeries series, bool actual)
        => actual
            ? Fmt(series.BandLow + norm * (series.BandHigh - series.BandLow))
            : norm.ToString("F1");

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        Theme.DrawCard(g, ClientRectangle, null);

        bool actualAxis = UseActualValueAxis(_series);
        string axisUnit = _series.Count > 0 && !string.IsNullOrWhiteSpace(_series[0].Unit) ? " " + _series[0].Unit : "";
        var title = actualAxis
            ? $"测项趋势（纵轴 = 实际值{axisUnit}；红虚线 = 规格上限 / 下限）"
            : "测项趋势（纵轴 = 规格带相对位置：0=下限，1=上限）";
        TextRenderer.DrawText(g, title, Theme.BodyBold,
            new Rectangle(16, 10, 620, 22), Theme.TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        var sub = _data == null
            ? ""
            : $"{_data.From:MM-dd} ~ {_data.ToExclusive.AddDays(-1):MM-dd} · 桶宽 {FormatBucketSpan(_data.BucketSeconds)}" +
              $" · 已绘 {_series.Count} 条";
        if (_data != null && _data.SingleSidedCount > 0)
            sub += $" · 含 {_data.SingleSidedCount} 个单边限测项（缺失端按窗口数据量程合成）";
        if (_data != null && _data.HasAnyFail)
            sub += $" · 虚线 = FAIL（{_data.FailSamplesTotal} 个失败样本）";
        if (!string.IsNullOrWhiteSpace(_data?.Hint)) sub += "  ·  " + _data!.Hint;
        if (!string.IsNullOrWhiteSpace(_statusHint)) sub += "  ·  " + _statusHint;
        TextRenderer.DrawText(g, sub, Theme.Tiny,
            new Rectangle(16, 30, Width - 32, 16), Theme.TextFaint, TextFormatFlags.EndEllipsis);

        var (chartLeft, chartRight, chartTop, chartBottom) = PlotRect();
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

        try { PaintPlot(g, actualAxis, chartLeft, chartRight, chartTop, chartBottom, chartW, chartH); }
        catch (Exception ex)
        {
            Logger.Warning($"[分析] 趋势图绘制失败: {ex.Message}");
            try { g.ResetClip(); } catch { }
            TextRenderer.DrawText(g, "趋势图绘制失败，详情见日志。", Theme.Body,
                new Rectangle(chartLeft, chartTop + 24, chartW, 40), Theme.Danger,
                TextFormatFlags.HorizontalCenter);
        }
    }

    private void PaintPlot(Graphics g, bool actualAxis, int chartLeft, int chartRight, int chartTop, int chartBottom, int chartW, int chartH)
    {
        var (lo, hi) = AxisRange(_series);
        var (x0, x1) = PlotBucketRange(_series, _data!.BucketCount);
        int xSpan = Math.Max(1, x1 - x0);

        float YOf(double norm) => ClampPlotCoord(
            !(hi > lo) || !double.IsFinite(norm) ? double.NaN : chartBottom - (norm - lo) / (hi - lo) * chartH,
            chartTop - 4, chartBottom + 4, (chartTop + chartBottom) / 2f);
        float XOf(int bucket) => ClampPlotCoord(
            chartLeft + (double)(bucket - x0) / xSpan * chartW,
            chartLeft - 4, chartRight + 4, chartLeft);

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
            TextRenderer.DrawText(g, FormatAxisValue(v, _series[0], actualAxis), Theme.Tiny,
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

        var plotClip = new Rectangle(chartLeft, chartTop, chartW, chartH);
        g.SetClip(plotClip);
        float lineW = _series.Count switch { <= 12 => 2f, <= 40 => 1.5f, _ => 1f };
        for (int si = 0; si < _series.Count; si++)
        {
            var ser = _series[si];
            var col = ColorFromHex(DeviationSeriesBuilder.SeriesColorHex(ser.TestName));
            int alpha = _series.Count > 40 ? 120 : 200;
            float w = si == _hoverSeries ? lineW + 1f : lineW;

            // PASS：实线（只连线、不画散点——点一多反而看不清趋势）
            using (var linePen = new Pen(Color.FromArgb(alpha, col), w))
                DrawSeriesLine(g, linePen, ser.Points, XOf, YOf);

            // FAIL：同一测项颜色的虚线，便于一眼对上"是哪条线在失败"
            if (ser.HasFail)
            {
                using var failPen = new Pen(Color.FromArgb(alpha, col), w) { DashStyle = DashStyle.Dash };
                DrawSeriesLine(g, failPen, ser.FailPoints, XOf, YOf);
            }
        }
        g.ResetClip();

        int labelStep = Math.Max(1, xSpan / 10);
        for (int b = x0; b <= x1; b += labelStep)
        {
            var lbl = MeasurementTrendBuilder.FormatBucketLabel(_data.From, b, _data.BucketSeconds);
            TextRenderer.DrawText(g, lbl, Theme.Tiny,
                new Rectangle((int)XOf(b) - 32, chartBottom + 4, 64, 16), Theme.TextFaint,
                TextFormatFlags.HorizontalCenter);
        }

        // 鼠标纵向参考线（以鼠标 X 为准，不吸附桶位）
        if (_hoverX >= chartLeft && _hoverX <= chartRight)
        {
            using var crossPen = new Pen(Color.FromArgb(150, Theme.TextSub), 1f) { DashStyle = DashStyle.Dot };
            g.DrawLine(crossPen, _hoverX, chartTop, _hoverX, chartBottom);
        }

        // 命中点高亮环 + tooltip（锚在鼠标 X 旁、数据点高度）
        if (_hoverSeries >= 0 && _hoverBucket >= 0 && FindAnyPoint(_hoverSeries, _hoverBucket) is { } hp)
        {
            float px = XOf(hp.Bucket);
            float py = Math.Clamp(YOf(hp.Norm), chartTop, chartBottom);
            using var ringBrush = new SolidBrush(Theme.Surface);
            using var ringPen = new Pen(ColorFromHex(DeviationSeriesBuilder.SeriesColorHex(_series[_hoverSeries].TestName)), 2f);
            g.FillEllipse(ringBrush, px - 4f, py - 4f, 8f, 8f);
            g.DrawEllipse(ringPen, px - 4f, py - 4f, 8f, 8f);
            DrawTooltip(g, _hoverX, py);
        }
    }

    /// <summary>画一条折线：空桶不插值（按连续桶号切段）；孤点画个极小标记，避免整段凭空消失。</summary>
    private static void DrawSeriesLine(Graphics g, Pen pen, List<TrendPoint> points,
        Func<int, float> xOf, Func<double, float> yOf)
    {
        if (points.Count == 0) return;
        var seg = new List<PointF>();
        int prevBucket = int.MinValue;
        foreach (var p in points)
        {
            if (seg.Count > 0 && p.Bucket != prevBucket + 1)
            {
                FlushSeg(g, pen, seg);
                seg.Clear();
            }
            seg.Add(new PointF(xOf(p.Bucket), yOf(p.Norm)));
            prevBucket = p.Bucket;
        }
        FlushSeg(g, pen, seg);
    }

    private static void FlushSeg(Graphics g, Pen pen, List<PointF> seg)
    {
        if (seg.Count == 0) return;
        if (seg.Count == 1)
        {
            var p = seg[0];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y)) return;
            g.DrawEllipse(pen, p.X - 1.5f, p.Y - 1.5f, 3f, 3f);
            return;
        }
        for (int i = 0; i < seg.Count; i++)
        {
            if (!float.IsFinite(seg[i].X) || !float.IsFinite(seg[i].Y)) return;
        }
        g.DrawLines(pen, seg.ToArray());
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

    private TrendPoint? FindFailPoint(int si, int bucket)
    {
        if (si < 0 || si >= _series.Count) return null;
        foreach (var p in _series[si].FailPoints)
            if (p.Bucket == bucket) return p;
        return null;
    }

    /// <summary>悬停命中：该桶的 PASS 点优先，没有则退 FAIL 点。</summary>
    private TrendPoint? FindAnyPoint(int si, int bucket) => FindPoint(si, bucket) ?? FindFailPoint(si, bucket);

    /// <summary>
    /// tooltip 摆放（纯函数，可直测）：默认贴在鼠标右上方；右侧/顶部空间不足时翻边，最后夹进控件内。
    /// </summary>
    public static Rectangle PlaceTooltip(Size tipSize, int mouseX, int mouseY, int controlWidth, int controlHeight)
    {
        int x = mouseX + 10;
        int y = mouseY - tipSize.Height - 10;
        if (x + tipSize.Width > controlWidth - 4) x = mouseX - tipSize.Width - 10;
        if (y < 4) y = mouseY + 14;
        x = Math.Clamp(x, 4, Math.Max(4, controlWidth - tipSize.Width - 4));
        y = Math.Clamp(y, 4, Math.Max(4, controlHeight - tipSize.Height - 4));
        return new Rectangle(x, y, tipSize.Width, tipSize.Height);
    }

    private void DrawTooltip(Graphics g, int mouseX, float anchorY)
    {
        var ser = _series[_hoverSeries];
        var passP = FindPoint(_hoverSeries, _hoverBucket);
        var failP = FindFailPoint(_hoverSeries, _hoverBucket);
        var p = passP ?? failP;
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
                  (passP == null ? "" : $"PASS 均值 {Fmt(passP.MeanValue)}{unit}  ·  {passP.N} 个样本\n") +
                  (failP == null ? "" : $"FAIL 均值 {Fmt(failP.MeanValue)}{unit}  ·  {failP.N} 个失败项\n") +
                  $"{band}\n相对位置 {pos}";

        var sz = TextRenderer.MeasureText(tip, Theme.Small);
        var padded = new Size(sz.Width + 12, sz.Height + 8);
        var rect = PlaceTooltip(padded, mouseX, (int)anchorY, Width, Height);
        using var bg = new SolidBrush(Theme.Surface);
        using var pen = new Pen(Theme.CardBorder);
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
        var (chartLeft, chartRight, chartTop, chartBottom) = PlotRect();
        if (mx < chartLeft || mx > chartRight || my < chartTop || my > chartBottom) return false;

        int bucketCount = _data.BucketCount;
        var (x0, x1) = PlotBucketRange(_series, bucketCount);
        int xSpan = Math.Max(1, x1 - x0);
        float xSlot = (float)(chartRight - chartLeft) / xSpan;
        int slot = xSlot <= 0 ? x0 : x0 + (int)Math.Round((mx - chartLeft) / xSlot);
        slot = Math.Clamp(slot, x0, x1);

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

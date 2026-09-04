using System.Drawing.Drawing2D;

namespace FctAggregator;

/// <summary>
/// 工业大屏看板：24小时逐小时产出与良率走势双轴图。
/// </summary>
public sealed class HourlyTrendChart : Control
{
    private List<HourlyStatItem> _stats = new();
    private int _hoverHour = -1;

    public HourlyTrendChart()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Surface;
    }

    public void SetData(List<HourlyStatItem> stats)
    {
        _stats = stats ?? new List<HourlyStatItem>();
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int chartLeft = 50;
        int chartRight = Width - 50;
        int chartBottom = Height - 40;
        int chartTop = 55;

        if (e.X >= chartLeft && e.X <= chartRight && e.Y >= chartTop && e.Y <= chartBottom && _stats.Count >= 24)
        {
            float barW = (float)(chartRight - chartLeft) / 24f;
            int h = (int)((e.X - chartLeft) / barW);
            h = Math.Clamp(h, 0, 23);
            if (_hoverHour != h)
            {
                _hoverHour = h;
                Invalidate();
            }
        }
        else if (_hoverHour != -1)
        {
            _hoverHour = -1;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverHour != -1)
        {
            _hoverHour = -1;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // 绘制工业质感卡片背景
        Theme.DrawCard(g, ClientRectangle, null);

        // 标题与图例
        TextRenderer.DrawText(g, "24小时逐小时产出与良率趋势", Theme.BodyBold,
            new Rectangle(16, 12, 280, 24), Theme.TextMain,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        // 图例 (PASS 绿, FAIL 红, 良率折线 蓝)
        int legX = Width - 240;
        DrawLegend(g, legX, 16, Theme.Success, "PASS 产出");
        DrawLegend(g, legX + 75, 16, Theme.Danger, "FAIL 异常");
        DrawLegend(g, legX + 150, 16, Color.FromArgb(0, 180, 255), "良率 %");

        int chartLeft = 50;
        int chartRight = Width - 50;
        int chartTop = 55;
        int chartBottom = Height - 35;
        int chartW = chartRight - chartLeft;
        int chartH = chartBottom - chartTop;

        if (chartW <= 20 || chartH <= 20) return;

        // 找出最大产量，用于做 Y 轴比例
        int maxTotal = 10;
        foreach (var it in _stats)
        {
            if (it.Total > maxTotal) maxTotal = it.Total;
        }
        // 向上圆整
        maxTotal = ((maxTotal / 10) + 1) * 10;

        // 背景水平参考线与左/右 Y 轴刻度
        using var gridPen = new Pen(Color.FromArgb(38, Theme.TextFaint), 1f) { DashStyle = DashStyle.Dot };
        for (int i = 0; i <= 4; i++)
        {
            float y = chartBottom - (chartH * (i / 4f));
            g.DrawLine(gridPen, chartLeft, y, chartRight, y);

            // 左 Y 轴刻度 (件数)
            int val = (int)(maxTotal * (i / 4f));
            TextRenderer.DrawText(g, val.ToString(), Theme.Tiny,
                new Rectangle(4, (int)y - 8, chartLeft - 8, 16), Theme.TextFaint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            // 右 Y 轴刻度 (良率 0%, 25%, 50%, 75%, 100%)
            int yld = i * 25;
            TextRenderer.DrawText(g, $"{yld}%", Theme.Tiny,
                new Rectangle(chartRight + 6, (int)y - 8, 40, 16), Theme.TextFaint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        // X 轴基线
        using var axisPen = new Pen(Color.FromArgb(80, Theme.TextFaint), 1f);
        g.DrawLine(axisPen, chartLeft, chartBottom, chartRight, chartBottom);

        if (_stats.Count < 24) return;

        float slotW = (float)chartW / 24f;
        float barW = Math.Max(4f, slotW * 0.65f);

        var points = new List<PointF>();

        // 绘制柱状图 (堆叠柱: 下面 PASS 绿色，上面 FAIL 红色)
        using var passBrush = new SolidBrush(Color.FromArgb(200, 39, 174, 96));
        using var failBrush = new SolidBrush(Color.FromArgb(220, 235, 77, 75));
        using var hoverBrush = new SolidBrush(Color.FromArgb(40, 255, 255, 255));

        for (int h = 0; h < 24; h++)
        {
            var item = _stats[h];
            float cx = chartLeft + (h * slotW) + (slotW / 2f);
            float bx = cx - (barW / 2f);

            // 悬停背景高亮
            if (h == _hoverHour)
            {
                g.FillRectangle(hoverBrush, chartLeft + (h * slotW), chartTop, slotW, chartH);
            }

            if (item.Total > 0)
            {
                float passH = ((float)item.Pass / maxTotal) * chartH;
                float failH = ((float)item.Fail / maxTotal) * chartH;

                if (passH > 0)
                {
                    g.FillRectangle(passBrush, bx, chartBottom - passH, barW, passH);
                }
                if (failH > 0)
                {
                    g.FillRectangle(failBrush, bx, chartBottom - passH - failH, barW, failH);
                }
            }

            // X 轴刻度标签 (偶数小时显示)
            if (h % 2 == 0)
            {
                TextRenderer.DrawText(g, $"{h:D2}h", Theme.Tiny,
                    new Rectangle((int)(cx - 15), chartBottom + 4, 30, 16), Theme.TextFaint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            // 收集良率折线点
            if (item.Total > 0)
            {
                float py = chartBottom - (float)(item.YieldRate / 100.0 * chartH);
                points.Add(new PointF(cx, py));
            }
        }

        // 绘制良率折线
        if (points.Count > 1)
        {
            using var linePen = new Pen(Color.FromArgb(0, 180, 255), 2.2f);
            g.DrawLines(linePen, points.ToArray());
        }

        // 绘制折点圆环
        using var ptFill = new SolidBrush(Color.FromArgb(240, 255, 255));
        using var ptBorder = new Pen(Color.FromArgb(0, 180, 255), 2f);
        using var lowYieldPen = new Pen(Theme.Danger, 2.5f);
        using var lowYieldFill = new SolidBrush(Theme.Danger);

        for (int h = 0; h < 24; h++)
        {
            var item = _stats[h];
            if (item.Total <= 0) continue;

            float cx = chartLeft + (h * slotW) + (slotW / 2f);
            float py = chartBottom - (float)(item.YieldRate / 100.0 * chartH);

            if (item.YieldRate < 95.0)
            {
                g.FillEllipse(lowYieldFill, cx - 4, py - 4, 8, 8);
                g.DrawEllipse(lowYieldPen, cx - 4, py - 4, 8, 8);
            }
            else
            {
                g.FillEllipse(ptFill, cx - 3.5f, py - 3.5f, 7, 7);
                g.DrawEllipse(ptBorder, cx - 3.5f, py - 3.5f, 7, 7);
            }
        }

        // 绘制悬停 Tooltip 框
        if (_hoverHour >= 0 && _hoverHour < 24)
        {
            DrawHoverTooltip(g, _stats[_hoverHour], chartLeft + (_hoverHour * slotW) + (slotW / 2f), chartTop, chartH);
        }
    }

    private void DrawHoverTooltip(Graphics g, HourlyStatItem item, float cx, int top, int height)
    {
        string title = $"{item.Hour:D2}:00 ~ {item.Hour:D2}:59";
        string row1 = $"总产出: {item.Total} pcs";
        string row2 = $"PASS: {item.Pass}  FAIL: {item.Fail}";
        string row3 = item.Total > 0 ? $"良率: {item.YieldRate:F1}%" : "良率: —";

        int tipW = 145;
        int tipH = 75;
        int tipX = (int)cx + 10;
        if (tipX + tipW > Width - 10) tipX = (int)cx - tipW - 10;
        int tipY = top + 10;

        var tipRect = new Rectangle(tipX, tipY, tipW, tipH);
        using var bgBrush = new SolidBrush(Color.FromArgb(235, 20, 24, 33));
        using var borderPen = new Pen(Color.FromArgb(0, 180, 255), 1f);
        g.FillRectangle(bgBrush, tipRect);
        g.DrawRectangle(borderPen, tipRect);

        TextRenderer.DrawText(g, title, Theme.BodyBold,
            new Rectangle(tipX + 8, tipY + 6, tipW - 16, 16), Color.White,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, row1, Theme.Tiny,
            new Rectangle(tipX + 8, tipY + 24, tipW - 16, 14), Color.FromArgb(200, 210, 225),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, row2, Theme.Tiny,
            new Rectangle(tipX + 8, tipY + 39, tipW - 16, 14), Color.FromArgb(200, 210, 225),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, row3, Theme.BodyBold,
            new Rectangle(tipX + 8, tipY + 54, tipW - 16, 16),
            item.YieldRate >= 95.0 ? Theme.Success : Theme.Danger,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    private static void DrawLegend(Graphics g, int x, int y, Color c, string label)
    {
        using var b = new SolidBrush(c);
        g.FillRectangle(b, x, y + 3, 12, 10);
        TextRenderer.DrawText(g, label, Theme.Tiny,
            new Rectangle(x + 16, y, 65, 16), Theme.TextSub,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>
/// 工业大屏看板：当月综合良率质量指标仪表盘。
/// </summary>
public sealed class TodayGaugePanel : Control
{
    private int _pass;
    private int _fail;
    private int _interrupted;

    public TodayGaugePanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Surface;
    }

    public void SetData(int pass, int fail, int interrupted = 0)
    {
        _pass = pass;
        _fail = fail;
        _interrupted = interrupted;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        Theme.DrawCard(g, ClientRectangle, null);

        // 标题
        TextRenderer.DrawText(g, "当月综合良率", Theme.BodyBold,
            new Rectangle(16, 12, 240, 24), Theme.TextMain,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        int total = _pass + _fail;
        double yieldRate = total > 0 ? (double)_pass / total * 100.0 : 0.0;

        int gaugeY = 44;
        int gaugeH = Height - gaugeY - 54;

        // 仪表环：当月综合良率
        Color yieldColor = total == 0 ? Theme.TextFaint : (yieldRate >= 98.0 ? Theme.Success : (yieldRate >= 95.0 ? Color.FromArgb(241, 196, 15) : Theme.Danger));
        DrawRingMeter(g, new Rectangle(0, gaugeY, Width, gaugeH),
            yieldRate, total > 0 ? $"{yieldRate:F1}%" : "—",
            yieldRate >= 98.0 ? "质量状态: 达标 (≥98%)" : "质量状态: 需关注",
            yieldColor);

        // 底部指标横条 (当月已测数、中断数)
        int botY = Height - 48;
        using var divPen = new Pen(Color.FromArgb(30, Theme.TextFaint), 1f);
        g.DrawLine(divPen, 16, botY, Width - 16, botY);

        int colW = (Width - 32) / 2;
        DrawMetricItem(g, 16, botY + 6, colW, "当月总测试", $"{total} pcs");
        DrawMetricItem(g, 16 + colW, botY + 6, colW, "当月测试中断", $"{_interrupted} 次");
    }

    private static void DrawRingMeter(Graphics g, Rectangle r, double percent, string centerText, string subText, Color arcColor)
    {
        const float penW = 8f;
        const int subH = 16;
        const int gap = 4;

        // 可用高度 = 总高 - 副文字 - 上下留白，圆环尺寸同时受宽度限制
        int availH = r.Height - subH - gap * 2;
        int size = Math.Min(r.Width - 24, availH);
        if (size < 40) return;

        int cx = r.X + (r.Width / 2);
        int cy = r.Y + gap + (availH / 2);
        var ringRect = new Rectangle(cx - size / 2, cy - size / 2, size, size);

        // 背景灰色圆环
        using var bgPen = new Pen(Color.FromArgb(35, Theme.TextFaint), penW) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(bgPen, ringRect, 135, 270);

        // 前景圆环 (扫过的角度)
        float sweep = (float)(Math.Clamp(percent, 0.0, 100.0) / 100.0 * 270.0);
        if (sweep > 1)
        {
            using var arcPen = new Pen(arcColor, penW) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arcPen, ringRect, 135, sweep);
        }

        // 中心大字：与圆环共用同一条中轴线（水平/垂直均对齐圆心）
        TextRenderer.DrawText(g, centerText, Theme.NumberMid,
            new Rectangle(cx - size / 2, cy - 20, size, 40), Theme.TextMain,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // 底部补充说明 (位于圆环下方，不与弧线重叠)
        TextRenderer.DrawText(g, subText, Theme.Tiny,
            new Rectangle(r.X + 4, r.Bottom - subH - gap, r.Width - 8, subH), Theme.TextFaint,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static void DrawMetricItem(Graphics g, int x, int y, int w, string label, string val)
    {
        TextRenderer.DrawText(g, label, Theme.Tiny,
            new Rectangle(x, y, w, 14), Theme.TextFaint,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, val, Theme.BodyBold,
            new Rectangle(x, y + 14, w, 16), Theme.TextMain,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>
/// 工业大屏看板：当日 Top 5 故障不良项排行榜。
/// </summary>
public sealed class TopFailRankPanel : Control
{
    private List<TopFailItem> _items = new();

    public TopFailRankPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Surface;
    }

    public void SetData(List<TopFailItem> items)
    {
        _items = items ?? new List<TopFailItem>();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        Theme.DrawCard(g, ClientRectangle, null);

        // 标题与副标
        TextRenderer.DrawText(g, "当日 Top 5 故障不良项排行", Theme.BodyBold,
            new Rectangle(16, 12, 220, 24), Theme.TextMain,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        TextRenderer.DrawText(g, "工位/测项聚合", Theme.Tiny,
            new Rectangle(Width - 110, 16, 95, 16), Theme.TextFaint,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        int startY = 48;
        if (_items.Count == 0)
        {
            TextRenderer.DrawText(g, "✨ 今日暂无 FAIL 故障测项记录，产线状态极佳", Theme.Body,
                new Rectangle(16, startY + 40, Width - 32, 40), Theme.Success,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        int rowH = Math.Max(32, (Height - startY - 12) / 5);
        int maxCount = _items.Count > 0 ? _items[0].Count : 1;

        for (int i = 0; i < Math.Min(5, _items.Count); i++)
        {
            var it = _items[i];
            int y = startY + (i * rowH);

            // 排名徽章
            DrawBadge(g, 16, y + 4, i + 1);

            // 不良测项名称
            int nameW = (int)(Width * 0.40f);
            TextRenderer.DrawText(g, it.FailItem, Theme.BodyBold,
                new Rectangle(46, y + 4, nameW, 18), Theme.TextMain,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            // 频次与占比说明
            string ratioText = $"{it.Count}次 ({it.Ratio:F1}%)";
            if (!string.IsNullOrEmpty(it.MainStation)) ratioText += $" · 机台:{it.MainStation}";

            TextRenderer.DrawText(g, ratioText, Theme.Tiny,
                new Rectangle(Width - 170, y + 4, 155, 18), Theme.TextFaint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            // 水平进度占比条
            int barX = 46;
            int barY = y + 24;
            int barMaxW = Width - barX - 16;
            int barH = 5;

            using var barBg = new SolidBrush(Color.FromArgb(30, Theme.TextFaint));
            g.FillRectangle(barBg, barX, barY, barMaxW, barH);

            float fillW = Math.Max(4f, (float)it.Count / maxCount * barMaxW);
            Color barCol = i == 0 ? Theme.Danger : (i == 1 ? Color.FromArgb(230, 126, 34) : Color.FromArgb(0, 180, 255));
            using var barFill = new SolidBrush(barCol);
            g.FillRectangle(barFill, barX, barY, fillW, barH);
        }
    }

    private static void DrawBadge(Graphics g, int x, int y, int rank)
    {
        Color bg = rank switch
        {
            1 => Color.FromArgb(231, 76, 60),  // 红色/金
            2 => Color.FromArgb(230, 126, 34), // 橙色
            3 => Color.FromArgb(241, 196, 15), // 黄色
            _ => Color.FromArgb(80, 90, 105)   // 深灰
        };
        using var b = new SolidBrush(bg);
        g.FillEllipse(b, x, y, 20, 20);
        TextRenderer.DrawText(g, rank.ToString(), Theme.Tiny,
            new Rectangle(x, y, 20, 20), Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>
/// 工业大屏看板：实时异常告警与动态播报流列表。
/// </summary>
public sealed class LiveAlertPanel : Control
{
    private List<LiveFailAlert> _alerts = new();
    private int _hoverIndex = -1;

    public event Action<LiveFailAlert>? AlertClicked;

    public LiveAlertPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Surface;
    }

    public void SetData(List<LiveFailAlert> alerts)
    {
        _alerts = alerts ?? new List<LiveFailAlert>();
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int startY = 48;
        int rowH = 34;
        if (e.Y >= startY)
        {
            int idx = (e.Y - startY) / rowH;
            if (idx >= 0 && idx < _alerts.Count)
            {
                if (_hoverIndex != idx) { _hoverIndex = idx; Cursor = Cursors.Hand; Invalidate(); }
                return;
            }
        }
        if (_hoverIndex != -1) { _hoverIndex = -1; Cursor = Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1) { _hoverIndex = -1; Cursor = Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_hoverIndex >= 0 && _hoverIndex < _alerts.Count)
        {
            AlertClicked?.Invoke(_alerts[_hoverIndex]);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        Theme.DrawCard(g, ClientRectangle, null);

        // 标题与呼吸指示灯
        using var dotB = new SolidBrush(Theme.Danger);
        g.FillEllipse(dotB, 16, 18, 10, 10);

        TextRenderer.DrawText(g, "实时异常告警播报流", Theme.BodyBold,
            new Rectangle(32, 12, 180, 24), Theme.TextMain,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        TextRenderer.DrawText(g, $"最新 {_alerts.Count} 条", Theme.Tiny,
            new Rectangle(Width - 110, 16, 95, 16), Theme.TextFaint,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        int startY = 46;
        if (_alerts.Count == 0)
        {
            TextRenderer.DrawText(g, "暂无未处理异常警报", Theme.Body,
                new Rectangle(16, startY + 40, Width - 32, 40), Theme.TextFaint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        int rowH = 34;
        int maxRows = (Height - startY - 8) / rowH;

        for (int i = 0; i < Math.Min(maxRows, _alerts.Count); i++)
        {
            var a = _alerts[i];
            int y = startY + (i * rowH);
            var rowRect = new Rectangle(12, y, Width - 24, rowH - 4);

            // 悬停背景
            if (i == _hoverIndex)
            {
                using var hovB = new SolidBrush(Color.FromArgb(40, 255, 255, 255));
                g.FillRectangle(hovB, rowRect);
            }
            else
            {
                using var cardB = new SolidBrush(Color.FromArgb(18, 235, 77, 75));
                g.FillRectangle(cardB, rowRect);
            }

            // 异常红边框
            using var borP = new Pen(Color.FromArgb(60, 235, 77, 75), 1f);
            g.DrawRectangle(borP, rowRect);

            // 时间标签
            string timeStr = a.TimeText.Length >= 19 ? a.TimeText.Substring(11, 8) : a.TimeText;
            TextRenderer.DrawText(g, timeStr, Theme.Tiny,
                new Rectangle(18, y, 65, rowH - 4), Color.FromArgb(235, 120, 120),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            // 机台标签
            if (!string.IsNullOrEmpty(a.StationId))
            {
                var stRect = new Rectangle(85, y + 4, 75, rowH - 12);
                using var stB = new SolidBrush(Color.FromArgb(50, Theme.TextFaint));
                g.FillRectangle(stB, stRect);
                TextRenderer.DrawText(g, a.StationId, Theme.Tiny, stRect, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }

            // SN 与失败原因
            int txtX = 168;
            int txtW = Width - txtX - 80;
            string desc = $"SN:{a.Sn}  |  {a.FailReason}";
            TextRenderer.DrawText(g, desc, Theme.BodyBold,
                new Rectangle(txtX, y, txtW, rowH - 4), Theme.TextMain,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            // 测试员 / 状态
            TextRenderer.DrawText(g, a.Tester, Theme.Tiny,
                new Rectangle(Width - 85, y, 70, rowH - 4), Theme.TextFaint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}

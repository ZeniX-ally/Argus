using System.Drawing.Drawing2D;

namespace FctAggregator;

public sealed class HourlyTrendChart : Control
{
    private List<HourlyStatItem> _stats = new();
    private int _hoverHour = -1;
    private string _paintSig = "";
    private bool _hasSig;

    public int PaintGeneration { get; private set; }
    public string LastPaintSig => _paintSig;

    public HourlyTrendChart()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Surface;
    }

    public void SetData(List<HourlyStatItem>? stats)
    {
        stats ??= new List<HourlyStatItem>();
        var sig = HourlySig(stats);
        if (_hasSig && sig == _paintSig) return;
        _hasSig = true;
        _paintSig = sig;
        PaintGeneration++;
        _stats = stats;
        Invalidate();
    }

    private static string HourlySig(List<HourlyStatItem> stats)
    {
        if (stats.Count == 0) return "";
        var sb = new System.Text.StringBuilder(stats.Count * 12);
        foreach (var s in stats)
            sb.Append(s.Hour).Append(',').Append(s.Pass).Append(',').Append(s.Fail).Append(';');
        return sb.ToString();
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
        try
        {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        Theme.DrawCard(g, ClientRectangle, null);

        TextRenderer.DrawText(g, "24小时逐小时产出与良率趋势", Theme.BodyBold,
            new Rectangle(16, 12, 280, 24), Theme.TextMain,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        int legX = Width - 240;
        DrawLegend(g, legX, 16, Theme.Success, "PASS 产出");
        DrawLegend(g, legX + 75, 16, Theme.Danger, "FAIL 异常");
        DrawLegend(g, legX + 150, 16, Theme.ChartAccent, "良率 %");

        int chartLeft = 50;
        int chartRight = Width - 50;
        int chartTop = 55;
        int chartBottom = Height - 35;
        int chartW = chartRight - chartLeft;
        int chartH = chartBottom - chartTop;

        if (chartW <= 20 || chartH <= 20) return;

        int maxTotal = 10;
        for (int h = 0; h < _stats.Count; h++)
        {
            int tot = _stats[h].Pass + _stats[h].Fail;
            if (tot > maxTotal) maxTotal = tot;
        }
        maxTotal = ((maxTotal / 10) + 1) * 10;

        using var gridPen = new Pen(Color.FromArgb(38, Theme.TextFaint), 1f) { DashStyle = DashStyle.Dot };
        for (int i = 0; i <= 4; i++)
        {
            float y = chartBottom - (chartH * (i / 4f));
            g.DrawLine(gridPen, chartLeft, y, chartRight, y);

            int val = (int)(maxTotal * (i / 4f));
            TextRenderer.DrawText(g, val.ToString(), Theme.Tiny,
                new Rectangle(4, (int)y - 8, chartLeft - 8, 16), Theme.TextFaint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            int yld = i * 25;
            TextRenderer.DrawText(g, $"{yld}%", Theme.Tiny,
                new Rectangle(chartRight + 6, (int)y - 8, 40, 16), Theme.TextFaint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        using var axisPen = new Pen(Color.FromArgb(80, Theme.TextFaint), 1f);
        g.DrawLine(axisPen, chartLeft, chartBottom, chartRight, chartBottom);

        if (_stats.Count < 24) return;

        float slotW = (float)chartW / 24f;
        float barW = Math.Max(4f, slotW * 0.65f);

        var points = new List<PointF>();

        using var passBrush = new SolidBrush(Theme.ChartPass);
        using var failBrush = new SolidBrush(Theme.ChartFail);
        using var hoverBrush = new SolidBrush(Theme.ChartHover);

        for (int h = 0; h < 24; h++)
        {
            var item = _stats[h];
            float passN = item.Pass;
            float failN = item.Fail;
            float totalN = passN + failN;
            float cx = chartLeft + (h * slotW) + (slotW / 2f);
            float bx = cx - (barW / 2f);

            if (h == _hoverHour)
            {
                g.FillRectangle(hoverBrush, chartLeft + (h * slotW), chartTop, slotW, chartH);
            }

            if (totalN > 0)
            {
                float passH = (passN / maxTotal) * chartH;
                float failH = (failN / maxTotal) * chartH;

                if (passH > 0)
                {
                    g.FillRectangle(passBrush, bx, chartBottom - passH, barW, passH);
                }
                if (failH > 0)
                {
                    g.FillRectangle(failBrush, bx, chartBottom - passH - failH, barW, failH);
                }
            }

            if (h % 2 == 0)
            {
                TextRenderer.DrawText(g, $"{h:D2}h", Theme.Tiny,
                    new Rectangle((int)(cx - 15), chartBottom + 4, 30, 16), Theme.TextFaint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            if (totalN > 0)
            {
                float py = chartBottom - (float)(item.YieldRate / 100.0 * chartH);
                points.Add(new PointF(cx, py));
            }
        }

        if (points.Count > 1)
        {
            using var linePen = new Pen(Theme.ChartAccent, 2.2f);
            g.DrawLines(linePen, points.ToArray());
        }

        using var ptFill = new SolidBrush(Theme.ChartPointFill);
        using var ptBorder = new Pen(Theme.ChartAccent, 2f);
        using var lowYieldPen = new Pen(Theme.Danger, 2.5f);
        using var lowYieldFill = new SolidBrush(Theme.Danger);

        for (int h = 0; h < 24; h++)
        {
            var item = _stats[h];
            float passN = item.Pass;
            float failN = item.Fail;
            if (passN + failN <= 0) continue;

            float cx = chartLeft + (h * slotW) + (slotW / 2f);
            double yld = item.YieldRate;
            float py = chartBottom - (float)(yld / 100.0 * chartH);

            if (yld < 95.0)
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

        if (_hoverHour >= 0 && _hoverHour < 24)
        {
            DrawHoverTooltip(g, _stats[_hoverHour], chartLeft + (_hoverHour * slotW) + (slotW / 2f), chartTop, chartH);
        }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[自绘] OnPaint 异常已兜底: {ex.Message}");
            try { TextRenderer.DrawText(e.Graphics, "（自绘异常，详见日志）", Theme.Small, ClientRectangle, Theme.TextFaint); } catch { }
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
        using var bgBrush = new SolidBrush(Theme.ChartTooltipBg);
        using var borderPen = new Pen(Theme.ChartAccent, 1f);
        g.FillRectangle(bgBrush, tipRect);
        g.DrawRectangle(borderPen, tipRect);

        TextRenderer.DrawText(g, title, Theme.BodyBold,
            new Rectangle(tipX + 8, tipY + 6, tipW - 16, 16), Color.White,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, row1, Theme.Tiny,
            new Rectangle(tipX + 8, tipY + 24, tipW - 16, 14), Theme.ChartTooltipFg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, row2, Theme.Tiny,
            new Rectangle(tipX + 8, tipY + 39, tipW - 16, 14), Theme.ChartTooltipFg,
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

public sealed class DeviceOnlinePanel : Panel
{
    private readonly Label _title = new();
    private readonly Label _head = new();
    private readonly Label _foot = new();
    private readonly Label _empty = new();
    private readonly TableLayoutPanel _grid = new();
    private int _layoutSig;
    private string _dataSig = "";

    public event Action? OpenDetails;

    public DeviceOnlinePanel()
    {
        DoubleBuffered = true;
        BackColor = Theme.Surface;
        Padding = new Padding(Theme.Gap);

        var bar = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Theme.Surface, Cursor = Cursors.Hand };
        _title.Text = "设备在线";
        _title.Font = Theme.BodyBold;
        _title.ForeColor = Theme.TextMain;
        _title.AutoSize = false;
        _title.Dock = DockStyle.Left;
        _title.Width = 88;
        _title.TextAlign = ContentAlignment.MiddleLeft;
        _head.Font = Theme.Tiny;
        _head.ForeColor = Theme.TextFaint;
        _head.Dock = DockStyle.Fill;
        _head.TextAlign = ContentAlignment.MiddleRight;
        bar.Controls.Add(_head);
        bar.Controls.Add(_title);
        bar.Click += (_, _) => OpenDetails?.Invoke();
        _title.Click += (_, _) => OpenDetails?.Invoke();
        _head.Click += (_, _) => OpenDetails?.Invoke();

        _foot.Dock = DockStyle.Bottom;
        _foot.Height = 22;
        _foot.Font = Theme.Tiny;
        _foot.ForeColor = Theme.TextFaint;
        _foot.TextAlign = ContentAlignment.MiddleLeft;
        _foot.Cursor = Cursors.Hand;
        _foot.Click += (_, _) => OpenDetails?.Invoke();

        _grid.Dock = DockStyle.Fill;
        _grid.BackColor = Theme.Surface;
        _grid.Resize += (_, _) => RelayoutGrid();

        _empty.Dock = DockStyle.Fill;
        _empty.TextAlign = ContentAlignment.MiddleCenter;
        _empty.Font = Theme.Body;
        _empty.ForeColor = Theme.TextFaint;
        _empty.Text = "FCT.ini 未登记 COM / USB";

        Controls.Add(_grid);
        Controls.Add(_foot);
        Controls.Add(bar);
    }

    public void SetData(FctIniData data)
    {
        data ??= new FctIniData();
        var sig = Sig(data);
        if (sig == _dataSig && _grid.Controls.Count == data.Devices.Count) return;
        _dataSig = sig;

        int comOn = data.Devices.Count(d => d.Type == "com" && d.Online);
        int comOff = data.Devices.Count(d => d.Type == "com" && !d.Online);
        int usb = data.Devices.Count(d => d.Type == "usb");
        if (!data.Found)
        {
            _head.Text = "未找到 FCT.ini";
            _head.ForeColor = Theme.Danger;
            _foot.Text = "点击打开设备状态";
            SyncCards(Array.Empty<DeviceInfo>(), data.Error ?? "未找到 FCT.ini，点击打开设备状态页");
            return;
        }

        _head.ForeColor = comOff > 0 ? Theme.Danger : Theme.TextFaint;
        _head.Text = comOff > 0 ? $"COM 掉线 {comOff}  ·  在线 {comOn}" : $"COM 在线 {comOn}  ·  USB {usb}";
        _foot.Text = data.ExtraSystemComCount > 0
            ? $"系统另有 {data.ExtraSystemComCount} 个 COM 未登记  ·  点击打开设备状态"
            : "全部为 FCT.ini 登记项  ·  点击打开设备状态";

        var list = data.Devices
            .OrderBy(d => d.Type == "com" ? 0 : 1)
            .ThenBy(d => d.Type == "com" && !d.Online ? 0 : 1)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SyncCards(list, list.Count == 0 ? "FCT.ini 未登记 COM / USB" : null);
    }

    private static string Sig(FctIniData d)
    {
        var sb = new System.Text.StringBuilder(d.Found ? "1" : "0");
        sb.Append('|').Append(d.Error ?? "").Append('|').Append(d.ExtraSystemComCount);
        foreach (var x in d.Devices)
            sb.Append('|').Append(x.Name).Append('/').Append(x.Port).Append('/').Append(x.Type).Append('/').Append(x.Online ? '1' : '0');
        return sb.ToString();
    }

    private void SyncCards(IReadOnlyList<DeviceInfo> devs, string? emptyText)
    {
        if (devs.Count == 0)
        {
            while (_grid.Controls.Count > 0)
            {
                var last = _grid.Controls[_grid.Controls.Count - 1];
                _grid.Controls.RemoveAt(_grid.Controls.Count - 1);
                if (last != _empty) last.Dispose();
            }
            _empty.Text = emptyText ?? "FCT.ini 未登记 COM / USB";
            if (_empty.Parent != _grid) _grid.Controls.Add(_empty);
            _empty.Visible = true;
            _layoutSig = 0;
            RelayoutGrid();
            return;
        }

        if (_empty.Parent == _grid)
        {
            _grid.Controls.Remove(_empty);
            _empty.Visible = false;
        }
        while (_grid.Controls.Count > devs.Count)
        {
            var last = _grid.Controls[_grid.Controls.Count - 1];
            _grid.Controls.RemoveAt(_grid.Controls.Count - 1);
            last.Dispose();
        }
        for (int i = 0; i < devs.Count; i++)
        {
            if (i >= _grid.Controls.Count)
            {
                var card = new DeviceCard { Dock = DockStyle.Fill, Margin = new Padding(4) };
                _grid.Controls.Add(card);
            }
            ((DeviceCard)_grid.Controls[i]).Bind(devs[i]);
        }
        _layoutSig = 0;
        RelayoutGrid();
    }

    private void RelayoutGrid()
    {
        int n = 0;
        foreach (Control c in _grid.Controls)
            if (c is DeviceCard) n++;
        if (n <= 0)
        {
            _grid.ColumnCount = 1;
            _grid.RowCount = 1;
            _grid.ColumnStyles.Clear();
            _grid.RowStyles.Clear();
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            return;
        }
        int w = Math.Max(1, _grid.ClientSize.Width);
        int h = Math.Max(1, _grid.ClientSize.Height);
        int cols = n <= 2 ? 1 : Math.Clamp(w / 170, 1, 4);
        if (cols > n) cols = n;
        int rows = (n + cols - 1) / cols;
        while (cols > 1 && rows > 0 && h / rows < 72)
        {
            cols--;
            rows = (n + cols - 1) / cols;
        }
        int sig = n * 1000 + cols * 10 + rows;
        if (sig == _layoutSig && _grid.ColumnCount == cols && _grid.RowCount == rows) return;
        _layoutSig = sig;

        _grid.SuspendLayout();
        _grid.ColumnCount = cols;
        _grid.RowCount = rows;
        _grid.ColumnStyles.Clear();
        _grid.RowStyles.Clear();
        for (int c = 0; c < cols; c++)
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
        for (int r = 0; r < rows; r++)
            _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
        int i = 0;
        foreach (Control ctl in _grid.Controls)
        {
            if (ctl is not DeviceCard) continue;
            _grid.SetColumn(ctl, i % cols);
            _grid.SetRow(ctl, i / cols);
            i++;
        }
        _grid.ResumeLayout(true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        try
        {
        Theme.DrawCard(e.Graphics, ClientRectangle, null);
        base.OnPaint(e);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[自绘] OnPaint 异常已兜底: {ex.Message}");
            try { TextRenderer.DrawText(e.Graphics, "（自绘异常，详见日志）", Theme.Small, ClientRectangle, Theme.TextFaint); } catch { }
        }
    }
}

public sealed class TopFailRankPanel : Control
{
    private List<TopFailItem> _items = new();
    // UI-10：行内长测项名会被 EndEllipsis 截断，悬停列出完整名称（含原因提示）
    private static readonly ToolTip ListTip = new() { AutoPopDelay = 20000, InitialDelay = 500, ReshowDelay = 200 };
    private string _paintSig = "";
    private bool _hasSig;

    public int PaintGeneration { get; private set; }
    public string LastPaintSig => _paintSig;

    public TopFailRankPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Surface;
    }

    public void SetData(List<TopFailItem>? items)
    {
        items ??= new List<TopFailItem>();
        var sig = TopFailSig(items);
        if (_hasSig && sig == _paintSig) return;
        _hasSig = true;
        _paintSig = sig;
        PaintGeneration++;
        _items = items;
        ListTip.SetToolTip(this, BuildListTooltip());
        Invalidate();
    }

    private static string TopFailSig(List<TopFailItem> items)
    {
        if (items.Count == 0) return "";
        var sb = new System.Text.StringBuilder(items.Count * 24);
        foreach (var it in items)
            sb.Append(it.FailItem).Append('\u0001').Append(it.Count).Append('\u0001')
                .Append(it.Ratio.ToString("R")).Append('\u0001').Append(it.MainStation).Append('\u0001')
                .Append(it.RootCauseHint).Append('\u0002');
        return sb.ToString();
    }

    private string BuildListTooltip()
    {
        if (_items.Count == 0) return "";
        var sb = new System.Text.StringBuilder("当日 Top 5 故障不良项（表格内过长会截断，这里看全）：");
        int rank = 0;
        foreach (var it in _items.Take(5))
        {
            rank++;
            sb.AppendLine().Append($"{rank}. {it.FailItem} — {it.Count} 次 ({it.Ratio:F1}%)");
            if (!string.IsNullOrEmpty(it.MainStation)) sb.Append($" · 机台:{it.MainStation}");
            if (!string.IsNullOrEmpty(it.RootCauseHint)) sb.AppendLine().Append($"      ↳ {it.RootCauseHint}");
        }
        return sb.ToString();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        try
        {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        Theme.DrawCard(g, ClientRectangle, null);

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

        int slotH = Math.Max(40, (Height - startY - 12) / 5);
        int y = startY;
        float maxNow = 1;
        for (int k = 0; k < Math.Min(5, _items.Count); k++)
            maxNow = Math.Max(maxNow, _items[k].Count);

        for (int i = 0; i < Math.Min(5, _items.Count); i++)
        {
            var it = _items[i];
            bool hasHint = !string.IsNullOrEmpty(it.RootCauseHint);
            int rowH = hasHint ? Math.Max(slotH, 48) : slotH;
            float countNow = it.Count;

            DrawBadge(g, 16, y + 4, i + 1);

            int nameW = (int)(Width * 0.40f);
            TextRenderer.DrawText(g, it.FailItem, Theme.BodyBold,
                new Rectangle(46, y + 4, nameW, 18), Theme.TextMain,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            string ratioText = $"{Math.Round(countNow)}次 ({it.Ratio:F1}%)";
            if (!string.IsNullOrEmpty(it.MainStation)) ratioText += $" · 机台:{it.MainStation}";

            TextRenderer.DrawText(g, ratioText, Theme.Tiny,
                new Rectangle(Width - 170, y + 4, 155, 18), Theme.TextFaint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            if (hasHint)
            {
                TextRenderer.DrawText(g, $"↳ {it.RootCauseHint}", Theme.Tiny,
                    new Rectangle(46, y + 22, Width - 62, 16), Theme.Warning,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }

            int barX = 46;
            int barY = y + (hasHint ? 38 : 24);
            int barMaxW = Width - barX - 16;
            int barH = 5;

            using var barBg = new SolidBrush(Color.FromArgb(30, Theme.TextFaint));
            g.FillRectangle(barBg, barX, barY, barMaxW, barH);

            float fillW = Math.Max(4f, countNow / maxNow * barMaxW);
            Color barCol = i == 0 ? Theme.Danger : (i == 1 ? Theme.ChartBarOrange : Theme.ChartAccent);
            using var barFill = new SolidBrush(barCol);
            g.FillRectangle(barFill, barX, barY, fillW, barH);
            y += rowH;
        }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[自绘] OnPaint 异常已兜底: {ex.Message}");
            try { TextRenderer.DrawText(e.Graphics, "（自绘异常，详见日志）", Theme.Small, ClientRectangle, Theme.TextFaint); } catch { }
        }
    }

    private static void DrawBadge(Graphics g, int x, int y, int rank)
    {
        Color bg = rank switch
        {
            1 => Theme.ChartBarRed,
            2 => Theme.ChartBarOrange,
            3 => Theme.ChartBarYellow,
            _ => Theme.ChartBarGray
        };
        using var b = new SolidBrush(bg);
        g.FillEllipse(b, x, y, 20, 20);
        TextRenderer.DrawText(g, rank.ToString(), Theme.Tiny,
            new Rectangle(x, y, 20, 20), Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>总览页：自学习基线预警条（无预警时高度 0）。</summary>
public sealed class LearnAlertStrip : Control
{
    private List<string> _lines = new();

    public LearnAlertStrip()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Bg;
        Height = 0;
        Visible = false;
    }

    public void SetAlerts(IReadOnlyList<string>? lines)
    {
        _lines = lines?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
        if (_lines.Count == 0)
        {
            Height = 0;
            Visible = false;
        }
        else
        {
            Height = Math.Min(80, 12 + _lines.Count * 22);
            Visible = true;
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        try
        {
        if (_lines.Count == 0) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(Theme.Gap, 4, Width - Theme.Gap * 2, Height - 8);
        using var bg = new SolidBrush(Color.FromArgb(28, Theme.Warning));
        using var border = new Pen(Color.FromArgb(80, Theme.Warning));
        g.FillRectangle(bg, rect);
        g.DrawRectangle(border, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        int y = rect.Y + 6;
        foreach (var line in _lines.Take(3))
        {
            TextRenderer.DrawText(g, line, Theme.Small,
                new Rectangle(rect.X + 10, y, rect.Width - 20, 20), Theme.TextMain,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            y += 22;
        }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[自绘] OnPaint 异常已兜底: {ex.Message}");
            try { TextRenderer.DrawText(e.Graphics, "（自绘异常，详见日志）", Theme.Small, ClientRectangle, Theme.TextFaint); } catch { }
        }
    }
}

public sealed class LiveAlertPanel : Control
{
    private List<LiveFailAlert> _alerts = new();
    private int _hoverIndex = -1;
    private string _paintSig = "";
    private bool _hasSig;

    public int PaintGeneration { get; private set; }
    public string LastPaintSig => _paintSig;

    /// <summary>告警行起始 Y。**绘制与命中测试必须共用这一个常量**——原先 OnPaint 写 46、
    /// OnMouseMove 写 48，悬停/点击整体偏 2px，100% 缩放下就错（与 DPI 无关）。</summary>
    private const int RowsTop = 46;

    public event Action<LiveFailAlert>? AlertClicked;

    public LiveAlertPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Surface;
    }

    public void SetData(List<LiveFailAlert>? alerts)
    {
        alerts ??= new List<LiveFailAlert>();
        var sig = AlertSig(alerts);
        if (_hasSig && sig == _paintSig) return;
        _hasSig = true;
        _paintSig = sig;
        PaintGeneration++;
        _alerts = alerts;
        Invalidate();
    }

    private static string AlertSig(List<LiveFailAlert> alerts)
    {
        if (alerts.Count == 0) return "";
        var sb = new System.Text.StringBuilder(alerts.Count * 24);
        foreach (var a in alerts)
            sb.Append(a.Id).Append('\u0001').Append(a.TimeText).Append('\u0001')
                .Append(a.Sn).Append('\u0001').Append(a.FailReason).Append('\u0002');
        return sb.ToString();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int startY = RowsTop;
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
        try
        {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        Theme.DrawCard(g, ClientRectangle, null);

        using var dotB = new SolidBrush(Theme.Danger);
        g.FillEllipse(dotB, 16, 18, 10, 10);

        TextRenderer.DrawText(g, "实时异常告警播报流", Theme.BodyBold,
            new Rectangle(32, 12, 180, 24), Theme.TextMain,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        TextRenderer.DrawText(g, $"最新 {_alerts.Count} 条", Theme.Tiny,
            new Rectangle(Width - 110, 16, 95, 16), Theme.TextFaint,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        int startY = RowsTop;
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

            var rowBg = i == _hoverIndex ? Theme.ChartHover : Theme.ChartFailSoft;
            using var rowB = new SolidBrush(rowBg);
            g.FillRectangle(rowB, rowRect);

            using var borP = new Pen(Theme.ChartFailBorder, 1f);
            g.DrawRectangle(borP, rowRect);

            string timeStr = a.TimeText.Length >= 19 ? a.TimeText.Substring(11, 8) : a.TimeText;
            TextRenderer.DrawText(g, timeStr, Theme.Tiny,
                new Rectangle(18, y, 65, rowH - 4), Theme.ChartFailText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            if (!string.IsNullOrEmpty(a.StationId))
            {
                var stRect = new Rectangle(85, y + 4, 75, rowH - 12);
                // D6：原为 Color.FromArgb(50, TextFaint) 半透明底 + 白字——近白底白字对比 ≈1.1:1，机台号实际不可见。
                // 改实色 TextFaint 底 + 白字（≈4.7:1）。
                using var stB = new SolidBrush(Theme.TextFaint);
                g.FillRectangle(stB, stRect);
                TextRenderer.DrawText(g, a.StationId, Theme.Tiny, stRect, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }

            int txtX = 168;
            int txtW = Width - txtX - 80;
            string desc = $"SN:{a.Sn}  |  {a.FailReason}";
            TextRenderer.DrawText(g, desc, Theme.BodyBold,
                new Rectangle(txtX, y, txtW, rowH - 4), Theme.TextMain,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            TextRenderer.DrawText(g, a.Tester, Theme.Tiny,
                new Rectangle(Width - 85, y, 70, rowH - 4), Theme.TextFaint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[自绘] OnPaint 异常已兜底: {ex.Message}");
            try { TextRenderer.DrawText(e.Graphics, "（自绘异常，详见日志）", Theme.Small, ClientRectangle, Theme.TextFaint); } catch { }
        }
    }
}

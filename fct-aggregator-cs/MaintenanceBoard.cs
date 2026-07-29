using System.Drawing.Drawing2D;

namespace FctAggregator;

/// <summary>
/// 维修记录看板视图: 5 列(未知问题 / 正在排查 / 未完成 / 进行中 / 已完成)，卡片可跨列拖动改状态。
/// 卡片全部自绘(不含子控件) —— 子控件会吞掉 MouseDown/MouseMove 导致拖拽拿不到事件，
/// 且 5 列 × 上百张卡若每张挂 4~5 个 Label 会产生上千个窗口句柄。
/// </summary>
public class MaintenanceBoard : Panel
{
    /// <summary>每列最多加载多少张卡(列头徽章显示的仍是真实总数)。</summary>
    public const int PerColumnLimit = 120;

    private const int MinColumnWidth = 208;

    private readonly Engine _engine;
    private readonly Dictionary<string, StatusColumn> _columns = new();
    private TableLayoutPanel _grid = null!;

    /// <summary>某条记录被拖动改状态 / 补填后触发，供外层同步列表视图。</summary>
    public event Action? Changed;
    /// <summary>请求编辑某条记录(双击卡片)。</summary>
    public event Action<MaintenanceRecord>? EditRequested;

    /// <summary>右键卡片（记录 / 锚点控件 / 控件内坐标）。菜单内容由 MaintenancePanel 统一提供，与列表视图一致。</summary>
    public event Action<MaintenanceRecord, Control, Point>? ContextRequested;

    public MaintenanceBoard(Engine engine)
    {
        _engine = engine;
        BuildUi();
    }

    private void BuildUi()
    {
        AutoScroll = true;
        BackColor = Color.FromArgb(244, 246, 249);
        Padding = new Padding(6, 4, 6, 4);

        var defs = MaintenanceMeta.Statuses;
        _grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = defs.Length,
            RowCount = 1,
            // 窗口变窄时靠 MinimumSize 触发外层横向滚动，而不是把列挤成一条
            MinimumSize = new Size(MinColumnWidth * defs.Length, 0),
            BackColor = Color.Transparent,
        };
        for (int i = 0; i < defs.Length; i++)
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / defs.Length));
        _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        for (int i = 0; i < defs.Length; i++)
        {
            var col = new StatusColumn(defs[i]) { Dock = DockStyle.Fill };
            col.DropRequested += OnDropRequested;
            col.CardEditRequested += m => EditRequested?.Invoke(m);
            col.CardContextRequested += (m, ctl, at) => ContextRequested?.Invoke(m, ctl, at);
            _columns[defs[i].Key] = col;
            _grid.Controls.Add(col, i, 0);
        }

        Controls.Add(_grid);
    }

    /// <summary>看板当前已加载的全部记录(供导出)。</summary>
    public List<MaintenanceRecord> LoadedRecords() =>
        _columns.Values.SelectMany(c => c.Records()).ToList();

    /// <summary>整体重载(切页 / 手动刷新 / 新增或删除后调用)。</summary>
    public void Reload()
    {
        try
        {
            var counts = _engine.Db.CountMaintenanceByStatus();
            // legacy 'closed' 与任何未知状态归并到对应有效列，保证徽章总数与实际可见列一致
            var normalized = new Dictionary<string, int>();
            foreach (var kv in counts)
            {
                var key = MaintenanceMeta.Normalize(kv.Key);
                normalized[key] = normalized.GetValueOrDefault(key) + kv.Value;
            }

            foreach (var def in MaintenanceMeta.Statuses)
            {
                var col = _columns[def.Key];
                var rows = _engine.Db.ListMaintenance(def.Key, PerColumnLimit);
                // 'closed' 记录若尚未迁移，会由 ListMaintenance(resolved) 漏掉，这里补进已完成列
                if (def.Key == MaintenanceMeta.DoneStatus)
                {
                    var legacy = _engine.Db.ListMaintenance(MaintenanceMeta.LegacyClosed, PerColumnLimit);
                    if (legacy.Count > 0)
                        rows = rows.Concat(legacy)
                                   .OrderByDescending(m => string.IsNullOrEmpty(m.UpdatedAt) ? m.CreatedAt : m.UpdatedAt)
                                   .Take(PerColumnLimit).ToList();
                }
                col.SetCards(rows, normalized.GetValueOrDefault(def.Key));
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"维修看板加载失败: {ex.Message}");
        }
    }

    /// <summary>只刷新各列头的计数徽章(拖动后用，避免重建所有卡片造成闪烁)。</summary>
    private void RefreshCounts()
    {
        try
        {
            var counts = _engine.Db.CountMaintenanceByStatus();
            var normalized = new Dictionary<string, int>();
            foreach (var kv in counts)
            {
                var key = MaintenanceMeta.Normalize(kv.Key);
                normalized[key] = normalized.GetValueOrDefault(key) + kv.Value;
            }
            foreach (var def in MaintenanceMeta.Statuses)
                _columns[def.Key].SetTotal(normalized.GetValueOrDefault(def.Key));
        }
        catch (Exception ex) { Logger.Warning($"维修看板计数刷新失败: {ex.Message}"); }
    }

    /// <summary>卡片被拖到某一列: 落库 + 就地搬控件(不整页重建)。</summary>
    private void OnDropRequested(MaintenanceCard card, string targetStatus)
    {
        var rec = card.Record;
        var from = MaintenanceMeta.Normalize(rec.Status);
        if (from == targetStatus) return;

        try
        {
            bool ok;
            if (targetStatus == MaintenanceMeta.DoneStatus)
            {
                // 拖到「已完成」: 弹轻量补填框(确定=写维修人与措施 / 跳过=只改状态 / 取消=撤销)
                using var dlg = new QuickResolveForm(rec, _engine.Db);
                var r = dlg.ShowDialog(this);
                if (r == DialogResult.Cancel) return;
                if (r == DialogResult.OK)
                {
                    var edited = rec.Clone();
                    edited.Resolver = dlg.Resolver;
                    edited.Resolution = dlg.Resolution;
                    edited.Status = targetStatus;
                    ok = _engine.Db.UpdateMaintenance(edited);
                    if (ok)
                    {
                        rec.Resolver = edited.Resolver;
                        rec.Resolution = edited.Resolution;
                    }
                }
                else
                {
                    ok = _engine.Db.UpdateMaintenanceStatus(rec.Id, targetStatus);
                }
            }
            else
            {
                ok = _engine.Db.UpdateMaintenanceStatus(rec.Id, targetStatus);
            }

            if (!ok)
            {
                MessageBox.Show($"记录 #{rec.Id} 更新失败（可能已被删除）。", "提示");
                Reload();
                return;
            }

            rec.Status = targetStatus;
            rec.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _columns[from].RemoveCard(card);
            _columns[targetStatus].InsertCardTop(card);
            RefreshCounts();
            Logger.Info($"维修记录 #{rec.Id} 状态: {MaintenanceMeta.ZhOf(from)} -> {MaintenanceMeta.ZhOf(targetStatus)}");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Error($"维修记录状态更新失败: {ex.Message}");
            MessageBox.Show("状态更新失败: " + ex.Message);
            Reload();
        }
    }

    // ==================================================================
    //  一列
    // ==================================================================
    private sealed class StatusColumn : Panel
    {
        public string StatusKey => _def.Key;

        public event Action<MaintenanceCard, string>? DropRequested;
        public event Action<MaintenanceRecord>? CardEditRequested;
        public event Action<MaintenanceRecord, Control, Point>? CardContextRequested;

        private readonly MaintenanceMeta.StatusDef _def;
        private readonly Panel _head;
        private readonly BufferedFlowPanel _cards;
        private readonly Label _more;
        private int _total;
        private bool _highlight;

        private static readonly Font HeadFont = new("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        private static readonly Font BadgeFont = new("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
        private static readonly Font HintFont = new("Microsoft YaHei UI", 8.5F);

        public StatusColumn(MaintenanceMeta.StatusDef def)
        {
            _def = def;
            Padding = new Padding(4, 0, 4, 0);
            BackColor = Color.Transparent;
            DoubleBuffered = true;

            _head = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.Transparent };
            _head.Paint += PaintHead;

            _more = new Label
            {
                Dock = DockStyle.Bottom, Height = 20, Visible = false,
                ForeColor = Color.FromArgb(120, 132, 150), Font = HintFont,
                TextAlign = ContentAlignment.MiddleCenter,
            };

            _cards = new BufferedFlowPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Color.FromArgb(236, 239, 244),
                Padding = new Padding(2, 4, 2, 4),
            };
            _cards.Paint += PaintCardsBackdrop;
            _cards.ClientSizeChanged += (_, _) => ResizeCards();

            // 列本体 / 列头 / 卡片区 三处都要能接放, 光标落在哪都不会失效
            foreach (Control c in new Control[] { this, _head, _cards })
            {
                c.AllowDrop = true;
                c.DragEnter += OnDragEnterOver;
                c.DragOver += OnDragEnterOver;
                c.DragLeave += (_, _) => SetHighlight(false);
                c.DragDrop += OnDragDrop;
            }

            Controls.Add(_cards);
            Controls.Add(_more);
            Controls.Add(_head);
            _cards.BringToFront();
        }

        public IEnumerable<MaintenanceRecord> Records() =>
            _cards.Controls.OfType<MaintenanceCard>().Select(c => c.Record);

        public void SetTotal(int total)
        {
            if (_total == total) return;
            _total = total;
            _head.Invalidate();
            UpdateMoreHint();
        }

        public void SetCards(List<MaintenanceRecord> rows, int total)
        {
            _cards.SuspendLayout();
            foreach (var old in _cards.Controls.OfType<MaintenanceCard>().ToList())
            {
                Detach(old);
                _cards.Controls.Remove(old);
                old.Dispose();
            }
            foreach (var m in rows)
                _cards.Controls.Add(Attach(new MaintenanceCard(m)));
            _cards.ResumeLayout();
            _total = total;
            _head.Invalidate();
            ResizeCards();
            UpdateMoreHint();
            _cards.Invalidate();
        }

        public void RemoveCard(MaintenanceCard card)
        {
            _cards.Controls.Remove(card);
            UpdateMoreHint();
            _cards.Invalidate();
        }

        public void InsertCardTop(MaintenanceCard card)
        {
            _cards.Controls.Add(card);
            _cards.Controls.SetChildIndex(card, 0);
            ResizeCards();
            UpdateMoreHint();
            _cards.Invalidate();
        }

        private MaintenanceCard Attach(MaintenanceCard c)
        {
            c.AllowDrop = true;
            c.DragEnter += OnDragEnterOver;
            c.DragOver += OnDragEnterOver;
            c.DragLeave += (_, _) => SetHighlight(false);
            c.DragDrop += OnDragDrop;
            c.EditRequested += OnCardEdit;
            c.ContextRequested += OnCardContext;
            return c;
        }

        private void Detach(MaintenanceCard c)
        {
            c.DragEnter -= OnDragEnterOver;
            c.DragOver -= OnDragEnterOver;
            c.DragDrop -= OnDragDrop;
            c.EditRequested -= OnCardEdit;
            c.ContextRequested -= OnCardContext;
        }

        private void OnCardEdit(MaintenanceCard c) => CardEditRequested?.Invoke(c.Record);

        private void OnCardContext(MaintenanceCard c, Point at) =>
            CardContextRequested?.Invoke(c.Record, c, at);

        private void UpdateMoreHint()
        {
            var shown = _cards.Controls.OfType<MaintenanceCard>().Count();
            var truncated = _total > shown;
            _more.Text = truncated ? $"仅显示最近 {shown} 条，共 {_total} 条" : "";
            _more.Visible = truncated;
        }

        private void ResizeCards()
        {
            var w = _cards.ClientSize.Width - _cards.Padding.Horizontal - 14;
            if (w < 80) w = 80;
            foreach (var c in _cards.Controls.OfType<MaintenanceCard>())
                if (c.Width != w) c.Width = w;
        }

        // ---------- 拖放 ----------
        private static MaintenanceCard? CardOf(DragEventArgs e) =>
            e.Data?.GetData(typeof(MaintenanceCard)) as MaintenanceCard;

        private void OnDragEnterOver(object? sender, DragEventArgs e)
        {
            var card = CardOf(e);
            if (card == null) { e.Effect = DragDropEffects.None; return; }
            var same = MaintenanceMeta.Normalize(card.Record.Status) == StatusKey;
            e.Effect = same ? DragDropEffects.None : DragDropEffects.Move;
            SetHighlight(!same);
            if (!same) AutoScrollDuringDrag(e);
        }

        /// <summary>拖到卡片区上下边缘时自动滚动(WinForms 不自带)。</summary>
        private void AutoScrollDuringDrag(DragEventArgs e)
        {
            if (!_cards.VerticalScroll.Visible) return;
            var p = _cards.PointToClient(new Point(e.X, e.Y));
            const int edge = 28, step = 26;
            int delta = p.Y < edge ? -step : p.Y > _cards.ClientSize.Height - edge ? step : 0;
            if (delta == 0) return;
            var v = _cards.VerticalScroll.Value + delta;
            v = Math.Clamp(v, _cards.VerticalScroll.Minimum, _cards.VerticalScroll.Maximum);
            _cards.VerticalScroll.Value = v;
        }

        private void OnDragDrop(object? sender, DragEventArgs e)
        {
            SetHighlight(false);
            var card = CardOf(e);
            if (card == null) return;
            if (MaintenanceMeta.Normalize(card.Record.Status) == StatusKey) return;
            DropRequested?.Invoke(card, StatusKey);
        }

        private void SetHighlight(bool on)
        {
            if (_highlight == on) return;
            _highlight = on;
            _cards.BackColor = on ? ControlPaint.Light(_def.Accent, 0.86f) : Color.FromArgb(236, 239, 244);
            _head.Invalidate();
        }

        // ---------- 绘制 ----------
        private void PaintHead(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = _head.ClientRectangle;

            using (var bg = new SolidBrush(_highlight ? ControlPaint.Light(_def.Accent, 0.75f) : Color.White))
                g.FillRectangle(bg, r);
            using (var bar = new SolidBrush(_def.Accent))
                g.FillRectangle(bar, new Rectangle(0, r.Bottom - 3, r.Width, 3));

            TextRenderer.DrawText(g, _def.Zh, HeadFont, new Rectangle(8, 0, r.Width - 54, r.Height - 3),
                Color.FromArgb(38, 46, 58), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            var badge = _total.ToString();
            var bw = Math.Max(24, TextRenderer.MeasureText(badge, BadgeFont).Width + 12);
            var br = new Rectangle(r.Width - bw - 8, (r.Height - 3 - 18) / 2, bw, 18);
            using (var path = MaintenanceDraw.Rounded(br, 9))
            using (var b = new SolidBrush(_def.Accent))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, badge, BadgeFont, br, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void PaintCardsBackdrop(object? sender, PaintEventArgs e)
        {
            if (_cards.Controls.OfType<MaintenanceCard>().Any()) return;
            var r = _cards.ClientRectangle;
            r.Inflate(-10, -10);
            if (r.Width <= 0 || r.Height <= 0) return;
            using var pen = new Pen(Color.FromArgb(205, 212, 222)) { DashStyle = DashStyle.Dash };
            e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width, Math.Min(r.Height, 64));
            TextRenderer.DrawText(e.Graphics, "拖动卡片到此处", HintFont,
                new Rectangle(r.X, r.Y, r.Width, Math.Min(r.Height, 64)),
                Color.FromArgb(150, 160, 175),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>只为把 DoubleBuffered 打开(FlowLayoutPanel 的该属性是 protected)。</summary>
    private sealed class BufferedFlowPanel : FlowLayoutPanel
    {
        public BufferedFlowPanel() => DoubleBuffered = true;
    }
}

// ======================================================================
//  卡片
// ======================================================================
/// <summary>看板上的一张维修记录卡片。全部内容自绘，无子控件。</summary>
public sealed class MaintenanceCard : Panel
{
    public MaintenanceRecord Record { get; }

    /// <summary>双击请求编辑。</summary>
    public event Action<MaintenanceCard>? EditRequested;

    /// <summary>右键请求弹菜单（参数为卡片内坐标）。</summary>
    public event Action<MaintenanceCard, Point>? ContextRequested;

    private static readonly Font TitleFont = new("Microsoft YaHei UI", 9F, FontStyle.Bold);
    private static readonly Font BodyFont = new("Microsoft YaHei UI", 8.25F);
    private static readonly Font MetaFont = new("Microsoft YaHei UI", 8F);

    /// <summary>全卡片共用一个 ToolTip —— 每张卡各建一个会产生上百个未释放的组件。</summary>
    private static readonly ToolTip Tip = new() { AutoPopDelay = 15000, InitialDelay = 600, ReshowDelay = 200 };

    private bool _hover;
    private bool _pressed;
    private Point _origin;

    public MaintenanceCard(MaintenanceRecord m)
    {
        Record = m;
        Height = 80;
        Margin = new Padding(4, 3, 4, 3);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Tip.SetToolTip(this, BuildTooltip());
    }

    private string BuildTooltip()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"#{Record.Id}  {Record.FailItem}");
        // v2.3.0: 不再展示设备型号 / SN
        sb.AppendLine($"严重度: {MaintenanceMeta.SeverityZhOf(Record.Severity)}");
        if (!string.IsNullOrWhiteSpace(Record.FailReason)) sb.AppendLine($"故障描述: {Record.FailReason}");
        if (!string.IsNullOrWhiteSpace(Record.Resolution)) sb.AppendLine($"维修措施: {Record.Resolution}");
        if (!string.IsNullOrWhiteSpace(Record.Notes)) sb.AppendLine($"备注: {Record.Notes}");
        sb.AppendLine($"记录日期: {Record.CreatedAt}");
        sb.AppendLine($"最后更新: {(string.IsNullOrEmpty(Record.UpdatedAt) ? "—" : Record.UpdatedAt)}");
        sb.Append("双击编辑 · 拖动到其它列改状态");
        return sb.ToString();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);

        using (var path = MaintenanceDraw.Rounded(r, 5))
        {
            using (var bg = new SolidBrush(_hover ? Color.FromArgb(252, 253, 255) : Color.White))
                g.FillPath(bg, path);
            using (var pen = new Pen(_hover ? MaintenanceMeta.AccentOf(Record.Status) : Color.FromArgb(214, 220, 229)))
                g.DrawPath(pen, path);
        }

        // 左侧严重度色条
        using (var sev = new SolidBrush(MaintenanceMeta.SeverityColorOf(Record.Severity)))
            g.FillRectangle(sev, new Rectangle(1, 4, 4, Height - 9));

        const int left = 12;
        int idW = 42;

        // 第 1 行: 故障项目 + 右上角 #ID
        TextRenderer.DrawText(g, Record.FailItem, TitleFont,
            new Rectangle(left, 7, Math.Max(20, Width - left - idW - 8), 18),
            Color.FromArgb(32, 40, 52), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, $"#{Record.Id}", MetaFont,
            new Rectangle(Width - idW - 6, 7, idW, 18),
            Color.FromArgb(160, 170, 184), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        // 第 2 行: 故障描述（v2.3.0 起不再显示设备 SN —— 维修记录不再关心 SN）
        var dev = !string.IsNullOrWhiteSpace(Record.FailReason) ? Record.FailReason
                : !string.IsNullOrWhiteSpace(Record.Resolution) ? Record.Resolution : "—";
        TextRenderer.DrawText(g, dev, BodyFont,
            new Rectangle(left, 29, Math.Max(20, Width - left - 8), 16),
            Color.FromArgb(88, 98, 114), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // 第 3 行: 严重度 · 维修人 · 最后更新
        var sevZh = MaintenanceMeta.SeverityZhOf(Record.Severity);
        var who = string.IsNullOrWhiteSpace(Record.Resolver) ? "未指派" : Record.Resolver;
        var when = ShortTime(string.IsNullOrEmpty(Record.UpdatedAt) ? Record.CreatedAt : Record.UpdatedAt);
        TextRenderer.DrawText(g, $"{sevZh} · {who}", MetaFont,
            new Rectangle(left, 50, Math.Max(20, Width - left - 96), 16),
            Color.FromArgb(128, 138, 154), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, when, MetaFont,
            new Rectangle(Width - 92, 50, 86, 16),
            Color.FromArgb(150, 160, 175), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    /// <summary>时间显示: 今天只显示 HH:mm，其余显示 MM-DD HH:mm。</summary>
    private static string ShortTime(string? ts)
    {
        if (string.IsNullOrWhiteSpace(ts)) return "—";
        if (!DateTime.TryParse(ts, out var dt)) return ts.Length > 16 ? ts[..16] : ts;
        return dt.Date == DateTime.Today ? dt.ToString("今天 HH:mm") : dt.ToString("MM-dd HH:mm");
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true; Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false; _pressed = false; Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // 卡片是自绘的 Panel, 不会自己跳右键菜单; 同时右键不能走拖拽分支
        if (e.Button == MouseButtons.Right)
        {
            _pressed = false;
            Focus();
            ContextRequested?.Invoke(this, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        _origin = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed || e.Button != MouseButtons.Left) return;
        // 必须过拖动阈值才起拖, 否则单击即触发拖拽、双击编辑会失灵
        var dz = SystemInformation.DragSize;
        if (Math.Abs(e.X - _origin.X) < dz.Width && Math.Abs(e.Y - _origin.Y) < dz.Height) return;
        _pressed = false;
        DoDragDrop(this, DragDropEffects.Move);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left) EditRequested?.Invoke(this);
    }
}

/// <summary>圆角路径小工具(卡片与列头共用)。</summary>
internal static class MaintenanceDraw
{
    internal static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        if (d <= 0 || r.Width <= d || r.Height <= d) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// ======================================================================
//  拖到「已完成」时的轻量补填框
// ======================================================================
/// <summary>拖到「已完成」列时弹出，补填维修人与维修措施。确定 / 跳过 / 取消。</summary>
public class QuickResolveForm : Form
{
    public string Resolver => ResolverUtil.Normalize(_resolver.Text);
    public string Resolution => _resolution.Text.Trim();

    private readonly TextBox _resolver;
    private readonly TextBox _resolution;

    public QuickResolveForm(MaintenanceRecord m, Database? db = null)
    {
        Text = $"标记为已完成 — #{m.Id}";
        Width = 460; Height = 300;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9F);

        var head = new Label
        {
            Dock = DockStyle.Top, Height = 44, Padding = new Padding(14, 8, 14, 0),
            Text = m.FailItem, Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            AutoEllipsis = true,
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(14, 0, 14, 6),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _resolver = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 5, 3, 5), Text = m.Resolver };
        // 维修人一行右侧挂一个【选择】，同样可多选
        var whoHost = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0),
        };
        whoHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        whoHost.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        var btnPick = new Button
        {
            Dock = DockStyle.Fill, Text = "选择", Margin = new Padding(2, 5, 3, 5),
            BackColor = Color.FromArgb(217, 231, 245), Enabled = db != null,
        };
        btnPick.Click += (_, _) =>
        {
            if (db == null) return;
            using var pick = new ResolverPickerForm(db, _resolver.Text);
            if (pick.ShowDialog(this) == DialogResult.OK) _resolver.Text = pick.Result;
        };
        whoHost.Controls.Add(_resolver, 0, 0);
        whoHost.Controls.Add(btnPick, 1, 0);
        _resolution = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical,
            Margin = new Padding(3, 5, 3, 5), Text = m.Resolution,
        };

        layout.Controls.Add(MakeLabel("维修人员:"), 0, 0);
        layout.Controls.Add(whoHost, 1, 0);
        layout.Controls.Add(MakeLabel("维修措施:"), 0, 1);
        layout.Controls.Add(_resolution, 1, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 8, 10, 8),
        };
        var ok = new Button { Text = "确定", Width = 84, Height = 28, DialogResult = DialogResult.OK };
        var skip = new Button { Text = "跳过不填", Width = 84, Height = 28, DialogResult = DialogResult.Ignore };
        var cancel = new Button { Text = "取消", Width = 84, Height = 28, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange(new Control[] { ok, skip, cancel });
        AcceptButton = ok;
        CancelButton = cancel;

        var hint = new Label
        {
            Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(14, 0, 14, 0),
            ForeColor = Color.FromArgb(120, 132, 150),
            Text = "「取消」将撤销本次状态变更，卡片留在原列。",
        };

        Controls.Add(layout);
        Controls.Add(hint);
        Controls.Add(buttons);
        Controls.Add(head);
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft,
    };
}

using System.Drawing.Drawing2D;

namespace FctAggregator;

public class MaintenanceBoard : Panel
{
    public const int PerColumnLimit = 120;

    private const int MinColumnWidth = 208;

    private readonly Engine _engine;
    private readonly Dictionary<string, StatusColumn> _columns = new();
    private TableLayoutPanel _grid = null!;

    public event Action? Changed;
    public event Action<MaintenanceRecord>? EditRequested;

    public event Action<MaintenanceRecord, Control, Point>? ContextRequested;

    public MaintenanceBoard(Engine engine)
    {
        _engine = engine;
        BuildUi();
    }

    private void BuildUi()
    {
        AutoScroll = true;
        BackColor = Theme.Bg;
        Padding = new Padding(6, 4, 6, 4);

        var defs = MaintenanceMeta.Statuses;
        _grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = defs.Length,
            RowCount = 1,
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
            col.CardActivateRequested += OnCardActivate;
            col.CardContextRequested += OnCardContext;
            col.CardPreviewRequested += OnCardPreview;
            _columns[defs[i].Key] = col;
            _grid.Controls.Add(col, i, 0);
        }

        Controls.Add(_grid);
    }

    public List<MaintenanceRecord> LoadedRecords() =>
        _columns.Values.SelectMany(c => c.Records()).ToList();

    public int PendingTodoCount { get; private set; }

    private bool _customRange;
    private DateTime? _todoFrom = DateTime.Today.AddDays(-Math.Max(1, AppConfig.Instance.TodoScanDays) + 1);
    private DateTime? _todoTo = DateTime.Today;

    public void SetTodoRange(DateTime? from, DateTime? to, bool custom = false)
    {
        _customRange = custom;
        _todoFrom = from;
        _todoTo = to;
        Reload();
    }

    public void Reload()
    {
        CardPreviewForm.CloseCurrent();
        if (!_customRange)
        {
            _todoFrom = DateTime.Today.AddDays(-Math.Max(1, AppConfig.Instance.TodoScanDays) + 1);
            _todoTo = DateTime.Today;
        }
        try
        {
            var todos = LoadTodos();
            PendingTodoCount = todos.Count;

            var counts = _engine.Db.CountMaintenanceByStatus();
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
                if (def.Key == MaintenanceMeta.DoneStatus)
                {
                    var legacy = _engine.Db.ListMaintenance(MaintenanceMeta.LegacyClosed, PerColumnLimit);
                    if (legacy.Count > 0)
                        rows = rows.Concat(legacy)
                                   .OrderByDescending(m => string.IsNullOrEmpty(m.UpdatedAt) ? m.CreatedAt : m.UpdatedAt)
                                   .Take(PerColumnLimit).ToList();
                }
                var mine = def.Key == MaintenanceMeta.DefaultStatus ? todos : new List<TodoItem>();
                col.SetCards(rows, mine, normalized.GetValueOrDefault(def.Key));
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"维修看板加载失败: {ex.Message}");
        }
    }

    private List<TodoItem> LoadTodos()
    {
        try
        {
            _engine.Db.SyncTodoItems(AppConfig.Instance.TodoScanDays);
            var from = _customRange ? _todoFrom : (DateTime?)null;
            var to = _customRange ? _todoTo : (DateTime?)null;
            var list = _engine.Db.ListTodoView(from, to);
            return list.Count > PerColumnLimit ? list.Take(PerColumnLimit).ToList() : list;
        }
        catch (Exception ex)
        {
            Logger.Error($"[待办] 待办列表加载失败(待办列将为空): {ex}");
            return new List<TodoItem>();
        }
    }

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

    private void OnCardActivate(BoardCardBase card)
    {
        switch (card)
        {
            case TodoCard t: ConfirmTodo(t.Item, MaintenanceMeta.DefaultStatus); break;
            case MaintenanceCard m: EditRequested?.Invoke(m.Record); break;
        }
    }

    private void OnCardContext(BoardCardBase card, Control anchor, Point at)
    {
        switch (card)
        {
            case TodoCard t: BuildTodoMenu(t.Item).Show(anchor, at); break;
            case MaintenanceCard m: ContextRequested?.Invoke(m.Record, anchor, at); break;
        }
    }

    private void OnCardPreview(BoardCardBase card, Control anchor)
    {
        try
        {
            switch (card)
            {
                case TodoCard t:
                {
                    var (title, sub, rows, footer) = t.BuildPreview();
                    CardPreviewForm.ShowFor(anchor, title, sub, Theme.Danger,
                        rows, MergedWithCounts(t.Item.Variants, t.Item.StationId), footer);
                    break;
                }
                case MaintenanceCard m:
                {
                    var (title, sub, rows, footer) = m.BuildPreview();
                    var items = m.SourceItems;
                    try
                    {
                        var linked = _engine.Db.GetTodoByMaintenance(m.Record.Id);
                        if (linked != null && linked.Variants.Count > 0) items = linked.Variants;
                    }
                    catch (Exception ex) { Logger.Warning($"预览反查待办失败: {ex.Message}"); }
                    CardPreviewForm.ShowFor(anchor, title, sub, MaintenanceMeta.AccentOf(m.Record.Status),
                        rows, items.Count > 1 ? MergedWithCounts(items, m.Record.StationId) : null, footer);
                    break;
                }
            }
        }
        catch (Exception ex) { Logger.Warning($"卡片预览失败: {ex.Message}"); }
    }

    private List<(string item, int count)> MergedWithCounts(List<string> items, string stationId)
    {
        var counts = new Dictionary<string, int>();
        try { counts = _engine.Db.CountFailByItems(items, stationId); }
        catch (Exception ex) { Logger.Warning($"预览统计各项次数失败: {ex.Message}"); }
        return items.Select(x => (x, counts.GetValueOrDefault(x))).ToList();
    }

    private ContextMenuStrip BuildTodoMenu(TodoItem todo)
    {
        var menu = new ContextMenuStrip { Font = Font };
        menu.Items.Add(new ToolStripMenuItem(
            $"未确认不良 · 优先级{todo.PriorityZh} · {todo.SortCount} 次" +
            (todo.VariantCount > 1 ? $" · 合并 {todo.VariantCount} 项" : "")) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("确认问题（建维修记录）", null, (_, _) => ConfirmTodo(todo, MaintenanceMeta.DefaultStatus));
        foreach (var def in MaintenanceMeta.Statuses)
        {
            if (def.Key == MaintenanceMeta.DefaultStatus) continue;
            var key = def.Key;
            menu.Items.Add($"确认并置为 {def.Zh}", null, (_, _) => ConfirmTodo(todo, key));
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("删除此待办", null, (_, _) => DeleteTodoItem(todo));
        return menu;
    }

    private void DeleteTodoItem(TodoItem todo)
    {
        var msg = $"确定删除待办「{todo.Title}」？\n\n" +
                  $"该故障项累计 {todo.TotalCount} 次不良，删除后不再出现在待办列。\n" +
                  "若它已关联维修记录，记录本身不受影响。";
        if (MessageBox.Show(msg, "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            if (_engine.Db.DeleteTodo(todo.Id))
            {
                Logger.Info($"[待办] 删除待办 #{todo.Id}: {todo.Title}");
                Reload();
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[待办] 删除失败: {ex.Message}");
            MessageBox.Show($"删除失败: {ex.Message}", "错误");
        }
    }

    private void ConfirmTodo(TodoItem todo, string targetStatus)
    {
        targetStatus = MaintenanceMeta.Normalize(targetStatus);
        var preset = new MaintenanceRecord
        {
            StationId = todo.StationId,
            FailItem = todo.Title,
            Severity = todo.SortCount >= TodoGrouping.HighThreshold ? "critical" : "major",
            Status = targetStatus,
        };
        using var dlg = new MaintenanceForm(_engine.ResolvedStationId, preset, null, ResolverCandidates(), _engine.Db);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var id = _engine.Db.AcknowledgeTodo(todo.Id, dlg.Result);
            foreach (var who in ResolverUtil.Split(dlg.Result.Resolver))
            {
                try { _engine.Db.AddResolver(who); } catch { }
            }
            Logger.Info($"[待办] 确认不良 -> 维修记录 #{id}（{MaintenanceMeta.ZhOf(dlg.Result.Status)}）: {todo.Title}" +
                        (todo.VariantCount > 1 ? $"（合并 {todo.VariantCount} 个同类项）" : ""));
            Reload();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Error($"[待办] 确认不良失败: {ex.Message}");
            MessageBox.Show($"创建维修记录失败: {ex.Message}", "错误");
        }
    }

    private List<string> ResolverCandidates()
    {
        try { return _engine.Db.ListResolvers(); }
        catch (Exception ex) { Logger.Warning($"读取维修人候选失败: {ex.Message}"); return new List<string>(); }
    }

    private void OnDropRequested(BoardCardBase dragged, string targetStatus)
    {
        if (dragged is TodoCard todoCard)
        {
            ConfirmTodo(todoCard.Item, targetStatus);
            return;
        }
        if (dragged is not MaintenanceCard card) return;

        var rec = card.Record;
        var from = MaintenanceMeta.Normalize(rec.Status);
        if (from == targetStatus) return;

        var edited = rec.Clone();
        edited.Status = targetStatus;
        using (var dlg = new MaintenanceForm(_engine.ResolvedStationId, edited, null, ResolverCandidates(), _engine.Db))
        {
            if (dlg.ShowDialog(this) == DialogResult.Cancel) return;
            edited = dlg.Result;
        }

        try
        {
            if (!_engine.Db.UpdateMaintenance(edited))
            {
                MessageBox.Show($"记录 #{rec.Id} 更新失败（可能已被删除）。", "提示");
                Reload();
                return;
            }

            var to = MaintenanceMeta.Normalize(edited.Status);
            var oldStatus = rec.Status;
            rec.Status = to;
            rec.Resolver = edited.Resolver;
            rec.Resolution = edited.Resolution;
            rec.FailReason = edited.FailReason;
            rec.Notes = edited.Notes;
            rec.Severity = edited.Severity;
            rec.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _columns[from].RemoveCard(card);
            _columns[to].InsertCardTop(card);
            RefreshCounts();
            Logger.Info($"维修记录 #{rec.Id} 状态: {MaintenanceMeta.ZhOf(oldStatus)} -> {MaintenanceMeta.ZhOf(to)}");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Error($"维修记录状态更新失败: {ex.Message}");
            MessageBox.Show("状态更新失败: " + ex.Message);
            Reload();
        }
    }

    private sealed class StatusColumn : Panel
    {
        public string StatusKey => _def.Key;

        public event Action<BoardCardBase, string>? DropRequested;
        public event Action<BoardCardBase>? CardActivateRequested;
        public event Action<BoardCardBase, Control, Point>? CardContextRequested;
        public event Action<BoardCardBase, Control>? CardPreviewRequested;

        private readonly MaintenanceMeta.StatusDef _def;
        private readonly Panel _head;
        private readonly BufferedFlowPanel _cards;
        private readonly Label _more;
        private int _total;
        private int _todoCount;
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
                ForeColor = Theme.TextFaint, Font = HintFont,
                TextAlign = ContentAlignment.MiddleCenter,
            };

            _cards = new BufferedFlowPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Theme.Surface,
                Padding = new Padding(2, 4, 2, 4),
            };
            _cards.Paint += PaintCardsBackdrop;
            _cards.ClientSizeChanged += (_, _) => ResizeCards();

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

        public void SetCards(List<MaintenanceRecord> rows, List<TodoItem> todos, int total)
        {
            _cards.SuspendLayout();
            foreach (var old in _cards.Controls.OfType<BoardCardBase>().ToList())
            {
                Detach(old);
                _cards.Controls.Remove(old);
                old.Dispose();
            }
            foreach (var t in todos)
                _cards.Controls.Add(Attach(new TodoCard(t)));
            foreach (var m in rows)
                _cards.Controls.Add(Attach(new MaintenanceCard(m)));
            _cards.ResumeLayout();
            _total = total;
            _todoCount = todos.Count;
            _head.Invalidate();
            ResizeCards();
            UpdateMoreHint();
            _cards.Invalidate();
        }

        public void RemoveCard(BoardCardBase card)
        {
            _cards.Controls.Remove(card);
            UpdateMoreHint();
            _cards.Invalidate();
        }

        public void InsertCardTop(BoardCardBase card)
        {
            _cards.Controls.Add(card);
            var todoCount = _cards.Controls.OfType<TodoCard>().Count();
            _cards.Controls.SetChildIndex(card, todoCount);
            ResizeCards();
            UpdateMoreHint();
            _cards.Invalidate();
        }

        private BoardCardBase Attach(BoardCardBase c)
        {
            c.AllowDrop = true;
            c.DragEnter += OnDragEnterOver;
            c.DragOver += OnDragEnterOver;
            c.DragLeave += (_, _) => SetHighlight(false);
            c.DragDrop += OnDragDrop;
            c.ActivateRequested += OnCardActivate;
            c.ContextRequested += OnCardContext;
            c.PreviewRequested += OnCardPreview;
            return c;
        }

        private void Detach(BoardCardBase c)
        {
            c.DragEnter -= OnDragEnterOver;
            c.DragOver -= OnDragEnterOver;
            c.DragDrop -= OnDragDrop;
            c.ActivateRequested -= OnCardActivate;
            c.ContextRequested -= OnCardContext;
            c.PreviewRequested -= OnCardPreview;
        }

        private void OnCardActivate(BoardCardBase c) => CardActivateRequested?.Invoke(c);

        private void OnCardContext(BoardCardBase c, Point at) =>
            CardContextRequested?.Invoke(c, c, at);

        private void OnCardPreview(BoardCardBase c) => CardPreviewRequested?.Invoke(c, c);

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
            foreach (var c in _cards.Controls.OfType<BoardCardBase>())
                if (c.Width != w) c.Width = w;
        }
        private static BoardCardBase? CardOf(DragEventArgs e) => BoardCardBase.FromDrag(e);

        private void OnDragEnterOver(object? sender, DragEventArgs e)
        {
            var card = CardOf(e);
            if (card == null) { e.Effect = DragDropEffects.None; return; }
            var same = card.ColumnKey == StatusKey;
            e.Effect = same ? DragDropEffects.None : DragDropEffects.Move;
            SetHighlight(!same);
            if (!same) AutoScrollDuringDrag(e);
        }

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
            if (card.ColumnKey == StatusKey) return;
            DropRequested?.Invoke(card, StatusKey);
        }

        private void SetHighlight(bool on)
        {
            if (_highlight == on) return;
            _highlight = on;
            _cards.BackColor = on ? Theme.SurfaceHi : Theme.Surface;
            _head.Invalidate();
        }

        private void PaintHead(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = _head.ClientRectangle;

            using (var bg = new SolidBrush(_highlight ? Theme.SurfaceHi : Theme.Surface))
                g.FillRectangle(bg, r);
            using (var bar = new SolidBrush(_def.Accent))
                g.FillRectangle(bar, new Rectangle(0, r.Bottom - 3, r.Width, 3));

            TextRenderer.DrawText(g, _def.Zh, HeadFont, new Rectangle(8, 0, r.Width - 54, r.Height - 3),
                Theme.TextSub, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            var badge = _total.ToString();
            var bw = Math.Max(24, TextRenderer.MeasureText(badge, BadgeFont).Width + 12);
            var br = new Rectangle(r.Width - bw - 8, (r.Height - 3 - 18) / 2, bw, 18);
            using (var path = MaintenanceDraw.Rounded(br, 9))
            using (var b = new SolidBrush(_def.Accent))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, badge, BadgeFont, br, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            if (_todoCount > 0)
            {
                var t = $"新 {_todoCount}";
                var tw = TextRenderer.MeasureText(t, BadgeFont).Width + 12;
                var tr = new Rectangle(br.X - tw - 4, br.Y, tw, 18);
                using (var path = MaintenanceDraw.Rounded(tr, 9))
                using (var b = new SolidBrush(Color.FromArgb(200, 16, 46)))
                    g.FillPath(b, path);
                TextRenderer.DrawText(g, t, BadgeFont, tr, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        private void PaintCardsBackdrop(object? sender, PaintEventArgs e)
        {
            if (_cards.Controls.OfType<BoardCardBase>().Any()) return;
            var r = _cards.ClientRectangle;
            r.Inflate(-10, -10);
            if (r.Width <= 0 || r.Height <= 0) return;
            using var pen = new Pen(Color.FromArgb(185, 185, 185)) { DashStyle = DashStyle.Dash };
            e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width, Math.Min(r.Height, 64));
            TextRenderer.DrawText(e.Graphics, "拖动卡片到此处", HintFont,
                new Rectangle(r.X, r.Y, r.Width, Math.Min(r.Height, 64)),
                Color.FromArgb(150, 150, 150),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    private sealed class BufferedFlowPanel : FlowLayoutPanel
    {
        public BufferedFlowPanel() => DoubleBuffered = true;
    }
}

public sealed class MaintenanceCard : BoardCardBase
{
    public MaintenanceRecord Record { get; }

    public override string ColumnKey => MaintenanceMeta.Normalize(Record.Status);

    public List<string> SourceItems { get; }

    private static readonly Font TitleFont = new("Microsoft YaHei UI", 9F, FontStyle.Bold);
    private static readonly Font BodyFont = new("Microsoft YaHei UI", 8.25F);
    private static readonly Font MetaFont = new("Microsoft YaHei UI", 8F);

    public MaintenanceCard(MaintenanceRecord m)
    {
        Record = m;
        SourceItems = TodoGrouping.ParseSourceItems(m.Notes);
        if (SourceItems.Count > 1) Height = 100;
        Tip.SetToolTip(this, BuildTooltip());
    }

    private string BuildTooltip()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"#{Record.Id}  {Record.FailItem}");
        sb.AppendLine($"严重度: {MaintenanceMeta.SeverityZhOf(Record.Severity)}");
        if (SourceItems.Count > 1)
        {
            sb.AppendLine($"合并的 fail 项（{SourceItems.Count} 项）:");
            foreach (var v in SourceItems.Take(10)) sb.AppendLine($"   · {v}");
            if (SourceItems.Count > 10) sb.AppendLine($"   …另有 {SourceItems.Count - 10} 项");
        }
        if (!string.IsNullOrWhiteSpace(Record.FailReason)) sb.AppendLine($"故障描述: {Record.FailReason}");
        if (!string.IsNullOrWhiteSpace(Record.Resolution)) sb.AppendLine($"维修措施: {Record.Resolution}");
        if (!string.IsNullOrWhiteSpace(Record.Notes)) sb.AppendLine($"备注: {Record.Notes}");
        sb.AppendLine($"记录日期: {Record.CreatedAt}");
        sb.AppendLine($"最后更新: {(string.IsNullOrEmpty(Record.UpdatedAt) ? "—" : Record.UpdatedAt)}");
        sb.Append("单击预览 · 双击编辑 · 拖动到其它列改状态");
        return sb.ToString();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);

        using (var path = MaintenanceDraw.Rounded(r, 5))
        {
            using (var bg = new SolidBrush(Hover ? Theme.RowHover : Theme.Surface))
                g.FillPath(bg, path);
            using (var pen = new Pen(Hover ? MaintenanceMeta.AccentOf(Record.Status) : Theme.CardBorder))
                g.DrawPath(pen, path);
        }

        using (var sev = new SolidBrush(MaintenanceMeta.SeverityColorOf(Record.Severity)))
            g.FillRectangle(sev, new Rectangle(1, 4, 4, Height - 9));

        const int left = 12;
        int idW = 42;

        TextRenderer.DrawText(g, Record.FailItem, TitleFont,
            new Rectangle(left, 7, Math.Max(20, Width - left - idW - 8), 18),
            Theme.TextMain, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, $"#{Record.Id}", MetaFont,
            new Rectangle(Width - idW - 6, 7, idW, 18),
            Theme.TextSub, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        var dev = !string.IsNullOrWhiteSpace(Record.FailReason) ? Record.FailReason
                : !string.IsNullOrWhiteSpace(Record.Resolution) ? Record.Resolution : "—";
        TextRenderer.DrawText(g, dev, BodyFont,
            new Rectangle(left, 29, Math.Max(20, Width - left - 8), 16),
            Theme.TextSub, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        var sevZh = MaintenanceMeta.SeverityZhOf(Record.Severity);
        var who = string.IsNullOrWhiteSpace(Record.Resolver) ? "未指派" : Record.Resolver;
        var when = ShortTime(string.IsNullOrEmpty(Record.UpdatedAt) ? Record.CreatedAt : Record.UpdatedAt);
        TextRenderer.DrawText(g, $"{sevZh} · {who}", MetaFont,
            new Rectangle(left, 50, Math.Max(20, Width - left - 96), 16),
            Theme.TextSub, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, when, MetaFont,
            new Rectangle(Width - 92, 50, 86, 16),
            Theme.TextSub, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        if (SourceItems.Count > 1)
        {
            var text = $"合并 {SourceItems.Count} 项：{SourceItems[0]}" +
                       (SourceItems.Count > 1 ? $"  +{SourceItems.Count - 1}" : "");
            TextRenderer.DrawText(g, text, MetaFont,
                new Rectangle(left, 70, Math.Max(20, Width - left - 8), 16),
                Theme.TextFaint, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    public (string title, string subtitle, List<(string, string)> rows, string footer) BuildPreview()
    {
        var rows = new List<(string, string)>
        {
            ("记录号:", $"#{Record.Id}"),
            ("状　　态:", MaintenanceMeta.ZhOf(MaintenanceMeta.Normalize(Record.Status))),
            ("严重程度:", MaintenanceMeta.SeverityZhOf(Record.Severity)),
            ("维修人员:", string.IsNullOrWhiteSpace(Record.Resolver) ? "未指派" : Record.Resolver),
            ("故障描述:", string.IsNullOrWhiteSpace(Record.FailReason) ? "—" : Record.FailReason),
            ("维修措施:", string.IsNullOrWhiteSpace(Record.Resolution) ? "—" : Record.Resolution),
            ("备　　注:", NotesWithoutSourceItems()),
            ("记录日期:", Record.CreatedAt),
            ("最后更新:", string.IsNullOrEmpty(Record.UpdatedAt) ? "—" : Record.UpdatedAt),
            ("机　　台:", string.IsNullOrWhiteSpace(Record.StationId) ? "—" : Record.StationId),
        };
        return (Record.FailItem,
                $"#{Record.Id} · {MaintenanceMeta.ZhOf(MaintenanceMeta.Normalize(Record.Status))}" +
                (SourceItems.Count > 1 ? $" · 合并 {SourceItems.Count} 项" : ""),
                rows,
                "双击卡片=编辑 · 拖动到其它列=改状态 · 右键=更多操作");
    }

    private string NotesWithoutSourceItems()
    {
        var n = Record.Notes ?? "";
        var i = n.IndexOf(TodoGrouping.SourceItemsTag, StringComparison.Ordinal);
        if (i >= 0) n = n[..i];
        n = n.Replace("\r", " ").Replace("\n", " ").Trim();
        return n.Length == 0 ? "—" : n;
    }
}

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

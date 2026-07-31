using System.Drawing.Drawing2D;

namespace FctAggregator;

/// <summary>
/// 维修记录看板视图: 5 列(未知问题 / 正在排查 / 待办 / 进行中 / 已完成)，卡片可跨列拖动改状态。
/// 卡片全部自绘(不含子控件) —— 子控件会吞掉 MouseDown/MouseMove 导致拖拽拿不到事件，
/// 且 5 列 × 上百张卡若每张挂 4~5 个 Label 会产生上千个窗口句柄。
///
/// v2.5.0：**待办不再是独立页面**。「待办」列同时装两种卡：
///   · 上半部 <see cref="TodoCard"/> —— 新出现的不良（尚未确认，来自 test_records 聚合）
///   · 下半部 <see cref="MaintenanceCard"/> —— 已确认待修的维修记录
/// 待办卡双击=确认建单、拖到别列=确认并置为该状态。
///
/// v2.5.1：待办卡**不可忽略 / 不可删除** —— 它是真实 FAIL 数据的投影，
/// 唯一出口是确认后处理掉（拖到「已完成」）。
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
            col.CardActivateRequested += OnCardActivate;
            col.CardContextRequested += OnCardContext;
            col.CardPreviewRequested += OnCardPreview;
            _columns[defs[i].Key] = col;
            _grid.Controls.Add(col, i, 0);
        }

        Controls.Add(_grid);
    }

    /// <summary>看板当前已加载的全部记录(供导出)。待办卡不是记录，不参与导出。</summary>
    public List<MaintenanceRecord> LoadedRecords() =>
        _columns.Values.SelectMany(c => c.Records()).ToList();

    /// <summary>当前「待办」列里未确认的不良数量(供外层显示角标)。</summary>
    public int PendingTodoCount { get; private set; }

    // 待办时间区间（null = 不限，即“全部·永久保留”）
    private DateTime? _todoFrom = DateTime.Today.AddDays(-29);
    private DateTime? _todoTo = DateTime.Today;

    /// <summary>设待办列的时间区间并重载（只影响显示，不删任何待办）。</summary>
    public void SetTodoRange(DateTime? from, DateTime? to)
    {
        _todoFrom = from;
        _todoTo = to;
        Reload();
    }

    /// <summary>整体重载(切页 / 手动刷新 / 新增或删除后调用)。</summary>
    public void Reload()
    {
        CardPreviewForm.CloseCurrent();      // 卡片要重建了，旧预览内容会过期
        try
        {
            var todos = LoadTodos();
            PendingTodoCount = todos.Count;

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
                // 未确认的不良只挂在「待办」列
                var mine = def.Key == MaintenanceMeta.DefaultStatus ? todos : new List<TodoItem>();
                col.SetCards(rows, mine, normalized.GetValueOrDefault(def.Key));
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"维修看板加载失败: {ex.Message}");
        }
    }

    /// <summary>读未确认的不良(失败不影响看板其余部分)。</summary>
    private List<TodoItem> LoadTodos()
    {
        try
        {
            // 先把「近 N 天」的新不良并入登记表（增量、按大项合并、永久保留），再按区间取视图。
            _engine.Db.SyncTodoItems(AppConfig.Instance.TodoScanDays);
            var list = _engine.Db.ListTodoView(_todoFrom, _todoTo);
            return list.Count > PerColumnLimit ? list.Take(PerColumnLimit).ToList() : list;
        }
        catch (Exception ex)
        {
            Logger.Warning($"待办(未确认不良)加载失败: {ex.Message}");
            return new List<TodoItem>();
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

    // ==================== 卡片交互(两种卡分流) ====================

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

    /// <summary>
    /// 单击卡片 → 弹详情预览。
    /// 卡上只能显前两个合并项，预览里把**完整清单 + 每项真实次数**列出来。
    /// </summary>
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
                    // 合并清单优先取关联的待办行（权威、会随新变体增长），没有则回退到备注里的清单
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

    /// <summary>给合并清单配上每项的真实不良次数（一次 IN 查询）。</summary>
    private List<(string item, int count)> MergedWithCounts(List<string> items, string stationId)
    {
        var counts = new Dictionary<string, int>();
        try { counts = _engine.Db.CountFailByItems(items, stationId); }
        catch (Exception ex) { Logger.Warning($"预览统计各项次数失败: {ex.Message}"); }
        return items.Select(x => (x, counts.GetValueOrDefault(x))).ToList();
    }

    /// <summary>未确认不良的右键菜单。
    /// **没有忽略/删除** —— 待办是真实不良的投影，只能确认后处理掉。</summary>
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
        menu.Items.Add(new ToolStripMenuItem("来自真实不良，不可忽略 / 删除") { Enabled = false });
        return menu;
    }

    /// <summary>确认一条未确认不良: 补填维修人/严重度 → 建维修记录(可直接落到目标列)。</summary>
    private void ConfirmTodo(TodoItem todo, string targetStatus)
    {
        targetStatus = MaintenanceMeta.Normalize(targetStatus);
        var zh = targetStatus == MaintenanceMeta.DefaultStatus ? null : MaintenanceMeta.ZhOf(targetStatus);
        using var dlg = new TodoConfirmForm(todo, _engine.Db, zh);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            // 确认 = 建记录 + 与待办行关联（记录被删 / 复发时待办会自动回到未确认）
            var id = _engine.Db.AcknowledgeTodo(todo.Id, dlg.Resolver, dlg.Severity, targetStatus);
            // 手敲的新名字自动进名单(与维修表单行为一致)
            foreach (var who in ResolverUtil.Split(dlg.Resolver))
            {
                try { _engine.Db.AddResolver(who); } catch { }
            }
            Logger.Info($"[待办] 确认不良 -> 维修记录 #{id}（{MaintenanceMeta.ZhOf(targetStatus)}）: {todo.Title}" +
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

    /// <summary>卡片被拖到某一列: 落库 + 就地搬控件(不整页重建)。</summary>
    private void OnDropRequested(BoardCardBase dragged, string targetStatus)
    {
        // 未确认不良拖出待办列 = 确认 + 直接置为目标状态
        if (dragged is TodoCard todoCard)
        {
            ConfirmTodo(todoCard.Item, targetStatus);
            return;
        }
        if (dragged is not MaintenanceCard card) return;

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

        public void SetCards(List<MaintenanceRecord> rows, List<TodoItem> todos, int total)
        {
            _cards.SuspendLayout();
            foreach (var old in _cards.Controls.OfType<BoardCardBase>().ToList())
            {
                Detach(old);
                _cards.Controls.Remove(old);
                old.Dispose();
            }
            // 未确认的不良排在最上面 —— 它们才是需要马上看一眼的
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
            // 未确认不良固定在最上面，新落入的记录卡插在它们下面
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
        // ---------- 拖放 ----------
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
            if (card.ColumnKey == StatusKey) return;
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

            // 未确认不良：主徐章左侧再挂一个红徐章（只有待办列会有）
            if (_todoCount > 0)
            {
                var t = $"新 {_todoCount}";
                var tw = TextRenderer.MeasureText(t, BadgeFont).Width + 12;
                var tr = new Rectangle(br.X - tw - 4, br.Y, tw, 18);
                using (var path = MaintenanceDraw.Rounded(tr, 9))
                using (var b = new SolidBrush(Color.FromArgb(197, 48, 48)))
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
public sealed class MaintenanceCard : BoardCardBase
{
    public MaintenanceRecord Record { get; }

    /// <summary>记录卡所在列 = 它的状态。</summary>
    public override string ColumnKey => MaintenanceMeta.Normalize(Record.Status);

    /// <summary>
    /// 该记录背后合并了哪些原始 fail 项。
    /// 从备注里回读（确认待办时写进去的），**不额外查库** —— 上百张卡每张发一次 SQL 会卡。
    /// </summary>
    public List<string> SourceItems { get; }

    private static readonly Font TitleFont = new("Microsoft YaHei UI", 9F, FontStyle.Bold);
    private static readonly Font BodyFont = new("Microsoft YaHei UI", 8.25F);
    private static readonly Font MetaFont = new("Microsoft YaHei UI", 8F);

    public MaintenanceCard(MaintenanceRecord m)
    {
        Record = m;
        SourceItems = TodoGrouping.ParseSourceItems(m.Notes);
        // 合并过的记录多给一行，把“合并了哪些项”也显在卡上
        if (SourceItems.Count > 1) Height = 100;
        Tip.SetToolTip(this, BuildTooltip());
    }

    private string BuildTooltip()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"#{Record.Id}  {Record.FailItem}");
        // v2.3.0: 不再展示设备型号 / SN
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
            using (var bg = new SolidBrush(Hover ? Color.FromArgb(252, 253, 255) : Color.White))
                g.FillPath(bg, path);
            using (var pen = new Pen(Hover ? MaintenanceMeta.AccentOf(Record.Status) : Color.FromArgb(214, 220, 229)))
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

        // 第 4 行(仅合并过的记录): 合并了哪些原始 fail 项
        if (SourceItems.Count > 1)
        {
            var text = $"合并 {SourceItems.Count} 项：{SourceItems[0]}" +
                       (SourceItems.Count > 1 ? $"  +{SourceItems.Count - 1}" : "");
            TextRenderer.DrawText(g, text, MetaFont,
                new Rectangle(left, 70, Math.Max(20, Width - left - 8), 16),
                Color.FromArgb(150, 140, 120), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>预览弹窗用的内容。</summary>
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

    /// <summary>备注里去掉“来源测试项”那一大段（预览里它已经单独成表了）。</summary>
    private string NotesWithoutSourceItems()
    {
        var n = Record.Notes ?? "";
        var i = n.IndexOf(TodoGrouping.SourceItemsTag, StringComparison.Ordinal);
        if (i >= 0) n = n[..i];
        n = n.Replace("\r", " ").Replace("\n", " ").Trim();
        return n.Length == 0 ? "—" : n;
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

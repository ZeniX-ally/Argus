namespace FctAggregator;

/// <summary>分析页：PASS 偏离折线 + 正常态自学习 + 时段归因 + TDMS。</summary>
public class AnalysisPanel : Panel
{
    private readonly Engine _engine;
    private System.Windows.Forms.Timer _timer = null!;
    private Label _summary = null!;
    private DataGridView _attrGrid = null!;
    private Label _attrEmpty = null!;
    private DataGridView _tdmsGrid = null!;
    private Label _tdmsEmpty = null!;
    private MeasurementTrendChart _devChart = null!;
    private DateTimePicker _dtFrom = null!;
    private DateTimePicker _dtTo = null!;
    private CheckedListBox _itemList = null!;
    private TextBox _itemSearch = null!;
    private Label _itemCaption = null!;
    private LearningPanel _learnPanel = null!;
    private int _chartGen;
    private bool _fetchAllBuckets;
    private bool _bucketLoadedAll;
    private readonly HashSet<string> _bucketLoaded = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _dataBusy;
    private volatile bool _rangeUserSet;
    private bool _bindingRange;
    private int _refreshGen;
    private long _lastDataRefreshTicks;
    private TrendResult? _trend;
    private List<TrendSeries> _allSeries = new();
    private readonly HashSet<string> _selectedItems = new(StringComparer.OrdinalIgnoreCase);
    private bool _userPickedItems;
    private bool _bindingItems;
    private string? _chartStatusHint;

    public AnalysisPanel(Engine engine)
    {
        _engine = engine;
        DoubleBuffered = true;
        BuildUi();
        _timer = new System.Windows.Forms.Timer { Interval = 120_000 };
        _timer.Tick += (_, _) => { if (Visible) Refresh2(); };
        _timer.Start();
    }

    private void BuildUi()
    {
        Padding = new Padding(Theme.Gap);
        BackColor = Theme.Bg;
        AutoScroll = true;

        _summary = new Label
        {
            Dock = DockStyle.Top, Height = 36, ForeColor = Theme.TextSub, Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(2, 2, 0, 2),
        };

        _attrGrid = NewGrid();
        _attrGrid.Columns.Add("slot", "时段");
        _attrGrid.Columns.Add("n", "样本");
        _attrGrid.Columns.Add("fails", "失败");
        _attrGrid.Columns.Add("rate", "失败率");
        _attrGrid.Columns.Add("global", "全局率");
        _attrGrid.Columns.Add("ratio", "偏差比");
        _attrGrid.Columns.Add("msg", "说明");
        _attrGrid.Columns[^1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

        _tdmsGrid = NewGrid();
        _tdmsGrid.Columns.Add("sn", "SN");
        _tdmsGrid.Columns.Add("model", "型号");
        _tdmsGrid.Columns.Add("group", "Group(章节)");
        _tdmsGrid.Columns.Add("channel", "通道");
        _tdmsGrid.Columns.Add("n", "样本");
        _tdmsGrid.Columns.Add("mean", "mean±std");
        _tdmsGrid.Columns.Add("range", "min~max");
        _tdmsGrid.Columns.Add("ts", "时间");
        _tdmsGrid.Columns[^1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

        _attrEmpty = EmptyLabel("暂无时段归因快照（每日维护窗口自动计算）");
        _tdmsEmpty = EmptyLabel("TDMS 无数据（配置 tdms_root 后自动采集）");

        _devChart = new MeasurementTrendChart { Dock = DockStyle.Fill };
        var chartToolbar = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Theme.Surface };
        chartToolbar.Controls.Add(new Label
        {
            Text = "时间范围", Left = 12, Top = 10, Width = 60, Height = 20,
            ForeColor = Theme.TextFaint, Font = Theme.Tiny, TextAlign = ContentAlignment.MiddleLeft,
        });
        _dtFrom = MakeDatePicker(74);
        _dtTo = MakeDatePicker(204);
        _dtFrom.Value = DateTime.Today.AddDays(-29);
        _dtTo.Value = DateTime.Today;
        _dtFrom.ValueChanged += (_, _) => MarkRangeTouched();
        _dtTo.ValueChanged += (_, _) => MarkRangeTouched();
        chartToolbar.Controls.Add(_dtFrom);
        chartToolbar.Controls.Add(new Label
        {
            Text = "~", Left = 190, Top = 10, Width = 12, Height = 20,
            ForeColor = Theme.TextFaint, Font = Theme.Small, TextAlign = ContentAlignment.MiddleCenter,
        });
        chartToolbar.Controls.Add(_dtTo);
        var btnApply = Theme.MakeButton("应用", 52);
        btnApply.Height = 26;
        btnApply.Left = 324;
        btnApply.Top = 5;
        btnApply.Click += (_, _) => { MarkRangeTouched(); RefreshTrendChart(); };
        chartToolbar.Controls.Add(btnApply);

        var chartLeft = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
        chartLeft.Controls.Add(_devChart);
        chartLeft.Controls.Add(chartToolbar);
        _devChart.MinimumSize = new Size(200, 240);

        var picker = BuildItemPicker();
        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface,
        };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 268));
        split.Controls.Add(chartLeft, 0, 0);
        split.Controls.Add(picker, 1, 0);

        var chartHost = new Panel
        {
            Dock = DockStyle.Top, Height = 400, MinimumSize = new Size(0, 360),
            BackColor = Theme.Surface, Padding = new Padding(0, 0, 0, 8),
        };
        chartHost.Controls.Add(split);

        _learnPanel = new LearningPanel(_engine, embedded: true) { Dock = DockStyle.Top, Height = 460 };

        var attrHost = SectionHost("失败时段归因（按小时桶）", _attrGrid, _attrEmpty, 220);
        var tdmsHost = SectionHost("TDMS 特征化（最近通道统计）", _tdmsGrid, _tdmsEmpty, 220);

        Controls.Add(tdmsHost);
        Controls.Add(attrHost);
        Controls.Add(_learnPanel);
        Controls.Add(chartHost);
        Controls.Add(_summary);
    }

    private static Panel SectionHost(string title, DataGridView grid, Label empty, int height = 280)
    {
        var host = new Panel { Dock = DockStyle.Top, BackColor = Theme.Surface, Padding = new Padding(12, 8, 12, 10), Height = height };
        host.Controls.Add(grid);
        host.Controls.Add(empty);
        host.Controls.Add(new Label
        {
            Text = title, Dock = DockStyle.Top, Height = 30,
            Font = Theme.CachedFont("Microsoft YaHei UI", 11F, FontStyle.Bold),
            ForeColor = Theme.Primary,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(2, 0, 0, 0),
        });
        return host;
    }

    private static DataGridView NewGrid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        BackgroundColor = Theme.Surface, BorderStyle = BorderStyle.None,
    };

    private static Label EmptyLabel(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, ForeColor = Theme.TextFaint, Font = Theme.Small,
        TextAlign = ContentAlignment.MiddleCenter, BackColor = Theme.Surface,
    };

    private static DateTimePicker MakeDatePicker(int left) => new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd",
        Width = 112,
        Left = left,
        Top = 6,
    };

    /// <summary>用户一旦自己动过日期，就不再自动跟随库内最新 PASS 日期。</summary>
    private void MarkRangeTouched()
    {
        if (!_bindingRange) _rangeUserSet = true;
    }

    private void SyncRangePickers(DateTime from, DateTime to)
    {
        _bindingRange = true;
        try
        {
            if (to < from) from = to;
            if (_dtFrom.Value.Date != from.Date) _dtFrom.Value = from.Date;
            if (_dtTo.Value.Date != to.Date) _dtTo.Value = to.Date;
        }
        finally { _bindingRange = false; }
    }

    private Panel BuildItemPicker()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(8, 6, 8, 6),
        };
        _itemCaption = new Label
        {
            Dock = DockStyle.Top, Height = 22, ForeColor = Theme.TextMain, Font = Theme.Small,
            Text = "测项 0/0", TextAlign = ContentAlignment.MiddleLeft,
        };
        var btns = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 34, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, BackColor = Theme.Surface, Padding = new Padding(0, 2, 0, 0),
        };
        var btnAll = Theme.MakeButton("全选", 56);
        var btnTop = Theme.MakeButton("波动最大", 80);
        var btnNone = Theme.MakeButton("清空", 56);
        btnAll.Height = btnTop.Height = btnNone.Height = 26;
        btnAll.Click += (_, _) => SelectAllVisible();
        btnTop.Click += (_, _) => SelectTopVolatile();
        btnNone.Click += (_, _) => SelectNone();
        btns.Controls.AddRange(new Control[] { btnAll, btnTop, btnNone });

        _itemSearch = new TextBox
        {
            Dock = DockStyle.Top, Height = 24, Font = Theme.Small, PlaceholderText = "筛选测项…",
        };
        var searchDebounce = UiAsync.Debounce(_itemSearch, 180, RebuildItemList);
        _itemSearch.TextChanged += (_, _) => { searchDebounce.Stop(); searchDebounce.Start(); };

        _itemList = new CheckedListBox
        {
            Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false,
            Font = Theme.Small, BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Surface, ForeColor = Theme.TextMain,
        };
        _itemList.ItemCheck += OnItemCheck;

        host.Controls.Add(_itemList);
        host.Controls.Add(_itemSearch);
        host.Controls.Add(btns);
        host.Controls.Add(_itemCaption);
        return host;
    }

    private void OnItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_bindingItems || e.Index < 0 || e.Index >= _itemList.Items.Count) return;
        _userPickedItems = true;
        var name = _itemList.Items[e.Index]?.ToString() ?? "";
        if (e.NewValue == CheckState.Checked) _selectedItems.Add(name);
        else _selectedItems.Remove(name);
        // 平时只对勾选项取桶数据：新勾的测项若还没桶数据，补一次查询
        bool missing = e.NewValue == CheckState.Checked && !_bucketLoadedAll && !_bucketLoaded.Contains(name);
        if (missing) RefreshTrendChart();
        else BeginInvoke(PushChart);
    }

    private void SelectAllVisible()
    {
        _userPickedItems = true;
        _bindingItems = true;
        try
        {
            for (int i = 0; i < _itemList.Items.Count; i++)
            {
                var name = _itemList.Items[i]?.ToString() ?? "";
                if (name.Length == 0) continue;
                _selectedItems.Add(name);
                _itemList.SetItemChecked(i, true);
            }
        }
        finally { _bindingItems = false; }
        RefreshTrendChart();   // 勾选集合变大 → 需要补取这些测项的桶数据
    }

    private void SelectNone()
    {
        _userPickedItems = true;
        _selectedItems.Clear();
        RebuildItemList();
        PushChart();
    }

    private void SelectTopVolatile()
    {
        _userPickedItems = true;
        _selectedItems.Clear();
        // 波动最大要基于全量桶数据算：清空勾选 + 本次全取，ApplyTrendData 会自动重选 Top8
        _fetchAllBuckets = true;
        RefreshTrendChart();
    }

    /// <summary>
    /// 勾选剪枝：只保留「窗口内仍存在的测项」。
    /// 传入的 liveNames 必须是窗口内**全部**测项（<see cref="TrendResult.AllItemNames"/>）——
    /// 不能用已取桶数据的系列来判定：只勾少数测项时系列天然缺项，会把用户勾选误剔掉。
    /// </summary>
    public static void PruneSelection(HashSet<string> selected, IEnumerable<string>? liveNames)
    {
        if (selected == null) return;
        var live = new HashSet<string>(liveNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        selected.RemoveWhere(n => !live.Contains(n));
    }

    private void ApplyTrendData(TrendResult? trend, List<TrendSeries> series, string? statusHint)
    {
        _trend = trend;
        _allSeries = series ?? new List<TrendSeries>();
        _chartStatusHint = statusHint;
        PruneSelection(_selectedItems, _trend?.AllItemNames);
        if (!_userPickedItems || _selectedItems.Count == 0)
        {
            _selectedItems.Clear();
            foreach (var n in MeasurementTrendBuilder.TopVolatileNames(_allSeries, 8))
                _selectedItems.Add(n);
        }
        RebuildItemList();
        PushChart();
    }

    private void RebuildItemList()
    {
        if (_itemList == null) return;
        _bindingItems = true;
        try
        {
            var q = (_itemSearch?.Text ?? "").Trim();
            _itemList.BeginUpdate();
            _itemList.Items.Clear();
            // 列表用「窗口内出现过的全部测项」（不受只取部分桶数据影响）
            foreach (var name in _trend?.AllItemNames ?? new List<string>())
            {
                if (q.Length > 0 && name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                _itemList.Items.Add(name, _selectedItems.Contains(name));
            }
            _itemList.EndUpdate();
        }
        finally { _bindingItems = false; }
        UpdateItemCaption();
    }

    private void UpdateItemCaption()
    {
        if (_itemCaption == null) return;
        var total = _trend?.AllItemNames.Count ?? 0;
        var shown = _itemList?.Items.Count ?? 0;
        _itemCaption.Text = shown == total
            ? $"测项 已选 {_selectedItems.Count}/{total}"
            : $"测项 已选 {_selectedItems.Count}/{total}（列出 {shown}）";
    }

    private void PushChart()
    {
        UpdateItemCaption();
        var shown = MeasurementTrendBuilder.Filter(_allSeries, _selectedItems);
        if (shown.Count == 0)
        {
            var hint = _trend?.AllItemNames.Count is not > 0 ? null : "请在右侧勾选要查看的测项";
            _devChart.SetData(_trend, shown, hint, null);
            return;
        }
        var st = $"已选 {_selectedItems.Count}/{_trend?.AllItemNames.Count ?? 0} 项";
        if (!string.IsNullOrWhiteSpace(_chartStatusHint)) st += "  ·  " + _chartStatusHint;
        _devChart.SetData(_trend, shown, null, st);
    }

    /// <param name="force">用户点刷新时 true，跳过节流。</param>
    public void Refresh2(bool force = false)
    {
        if (!force && Visible)
        {
            long now = Environment.TickCount64;
            if (now - _lastDataRefreshTicks < 2500) return;
        }
        _lastDataRefreshTicks = Environment.TickCount64;
        RefreshTrendChart();
        RefreshDataAsync(force);
    }

    private void RefreshDataAsync(bool force)
    {
        // force（用户点刷新）只跳过节流，不再绕过在途锁：连点刷新会并发重叠，多批后台任务互相覆盖
        _ = force;
        if (_dataBusy) return;
        _dataBusy = true;
        int gen = ++_refreshGen;
        var db = _engine.Db;
        var cfg = _engine.Config;
        Task.Run(() =>
        {
            try
            {
                var attr = AnalysisEngine.GetAttributionState(db);
                var snap = LearningSnapshot.Capture(db, cfg);
                var tdms = AnalysisEngine.GetTdmsState(db);
                var tdmsRows = db.GetRecentTdmsFeatures(50);
                var measRows = db.CountPassMeasurementRows();
                UiAsync.Post(this, () =>
                {
                    try
                    {
                        if (IsDisposed || gen != _refreshGen) return;
                        ApplySummary(attr, snap, tdms);
                        FillAttr(attr);
                        FillTdmsRows(tdmsRows);
                        _learnPanel.BindSnapshot(snap, measRows);
                    }
                    finally { if (gen == _refreshGen) _dataBusy = false; }
                });
            }
            catch (Exception ex)
            {
                Logger.Warning($"[分析] 后台刷新失败: {ex.Message}");
                UiAsync.Post(this, () => _dataBusy = false);
            }
        });
    }

    private void ApplySummary(AttributionState? attr, LearningSnapshot snap, TdmsState? tdms)
    {
        var parts = new List<string>();
        if (snap.Enabled)
            parts.Add($"正常态 {snap.Ready}/{snap.Total} ready，偏离事件 {snap.Events.Count}");
        if (attr != null)
            parts.Add($"时段归因 {attr.Date}：异常桶 {attr.Buckets.Count(r => r.Dim == "hour")}");
        if (tdms != null)
            parts.Add($"TDMS {tdms.RowsTotal} 行");
        _summary.Text = parts.Count > 0
            ? string.Join("；", parts) + "。"
            : "暂无分析数据——先采集 PASS 或点「回填历史」。";
    }

    private void RefreshTrendChart()
    {
        // 代际模型：每次请求都自增代际并真的发起查询，回调只应用「最新一代」的结果。
        // 不用「忙就丢弃」的悲观锁——过去那种写法会把用户点「应用」的请求静默扔掉，
        // 并且解锁只发生在异步回调里，一旦回调没执行（Post 被静默丢弃）就永久卡死。
        int gen = ++_chartGen;
        var db = _engine.Db;
        bool autoRange = !_rangeUserSet;
        var from = _dtFrom.Value.Date;
        var to = _dtTo.Value.Date;
        // 桶数上限随图宽自适应：分桶即降采样，点数不会超过图宽能分辨的密度
        int maxBuckets = Math.Clamp(_devChart.Width / 3, 60, 300);
        // 只取「当前勾选测项」的桶数据（差一两个数量级）；未勾选或需要算「波动最大」时取全部
        bool allBuckets = _fetchAllBuckets || _selectedItems.Count == 0;
        _fetchAllBuckets = false;
        var wanted = allBuckets ? null : _selectedItems.ToList();
        int selectedCount = _selectedItems.Count;
        // 记住本次取桶覆盖了哪些测项，供勾选变化时判断「要不要补查」。
        // 不能用 _allSeries 判断：它按「有基准带」过滤，无限值被跳过的测项会被误判成"没取过"，每次勾选都白查一次。
        _bucketLoadedAll = wanted == null;
        _bucketLoaded.Clear();
        if (wanted != null) foreach (var n in wanted) _bucketLoaded.Add(n);
        Task.Run(() =>
        {
            TrendResult trend;
            try
            {
                if (autoRange)
                {
                    var rg = db.GetPassMeasurementDateRange();
                    if (rg is { } r && DateTime.TryParseExact(r.Max, "yyyy-MM-dd",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var maxD))
                    {
                        to = maxD;
                        from = maxD.AddDays(-29);
                    }
                }
                trend = MeasurementTrendBuilder.Build(db, from, to, maxBuckets, null, wanted);
            }
            catch (Exception ex)
            {
                Logger.Warning($"[分析] 趋势图构建失败: {ex.Message}");
                trend = new TrendResult { From = from, ToExclusive = to.AddDays(1) };
                trend.Hint = "趋势构建失败：" + ex.Message;
            }
            Logger.Info($"[分析] 趋势 gen={gen} 范围={trend.From:yyyy-MM-dd}~{trend.ToExclusive.AddDays(-1):yyyy-MM-dd}" +
                        $" 桶宽={trend.BucketSeconds}s 桶数={trend.BucketCount}" +
                        $" 测项={trend.AllItemNames.Count} 系列={trend.Series.Count} 跳过={trend.SkippedNoBand}" +
                        $" 勾选={selectedCount} 取桶={(wanted == null ? "全部" : wanted.Count.ToString())}" +
                        (string.IsNullOrWhiteSpace(trend.Hint) ? "" : $" 提示={trend.Hint}"));
            UiAsync.Post(this, () =>
            {
                try
                {
                    if (IsDisposed) return;
                    if (gen != _chartGen) return;   // 过期结果：丢弃，绝不覆盖最新一代
                    if (!_rangeUserSet) SyncRangePickers(trend.From, trend.ToExclusive.AddDays(-1));
                    ApplyTrendData(trend, trend.Series, null);
                }
                catch (Exception ex) { Logger.Warning($"[分析] 趋势回写失败: {ex.Message}"); }
            });
        });
    }

    private void FillTdmsRows(
        List<(long RecordId, string Model, string Sn, string GroupName, string ChannelName, string Section, int N, double? Min, double? Max, double? Mean, double? Std, string Ts)> rows)
    {
        UiAsync.RunGridUpdate(_tdmsGrid, () =>
        {
            _tdmsGrid.Rows.Clear();
            _tdmsEmpty.Visible = rows.Count == 0;
            _tdmsGrid.Visible = rows.Count > 0;
            foreach (var r in rows)
            {
                _tdmsGrid.Rows.Add(
                    r.Sn, r.Model,
                    string.IsNullOrEmpty(r.Section) ? r.GroupName : $"{r.GroupName}(§{r.Section})",
                    r.ChannelName, r.N.ToString(),
                    r.Mean == null ? "-" : $"{r.Mean:F4}±{r.Std:F4}",
                    r.Min == null ? "-" : $"{r.Min:F4}~{r.Max:F4}",
                    r.Ts);
            }
        });
    }

    private void FillAttr(AttributionState? state)
    {
        var rows = (state?.Buckets ?? new List<AttributionBucketItem>())
            .Where(b => string.Equals(b.Dim, "hour", StringComparison.OrdinalIgnoreCase))
            .ToList();
        UiAsync.RunGridUpdate(_attrGrid, () =>
        {
            _attrGrid.Rows.Clear();
            _attrEmpty.Visible = rows.Count == 0;
            _attrGrid.Visible = rows.Count > 0;
            if (state != null && rows.Count == 0)
                _attrEmpty.Text = state.GlobalFailRate <= 0 ? "窗口期内全局零失败" : "无超阈值时段异常桶";
            foreach (var b in rows)
            {
                var slot = int.TryParse(b.Key, out var h) ? $"{h:D2}:00–{h:D2}:59" : b.Key;
                _attrGrid.Rows.Add(
                    slot, b.N, b.Fails,
                    b.FailRate.ToString("P1"), b.GlobalRate.ToString("P1"),
                    b.Ratio.ToString("F2") + "×", b.Hint);
            }
        });
    }
}

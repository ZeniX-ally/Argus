namespace FctAggregator;

/// <summary>正常态自学习区：心跳 / 覆盖率 / 模型明细 / 偏离事件（嵌入「分析」页）。数据全部本机直查。</summary>
public class LearningPanel : Panel
{
    private readonly Engine _engine;
    private System.Windows.Forms.Timer _timer = null!;
    private Label _summary = null!;
    private Label _cover = null!;
    private Button _btnBackfill = null!;
    private Button _btnReplay = null!;
    private Button _btnReset = null!;
    private ComboBox _winDays = null!;
    private ComboBox _srcFilter = null!;
    private TextBox _search = null!;
    private DataGridView _modelGrid = null!;
    private DataGridView _eventGrid = null!;
    private Label _modelEmpty = null!;
    private Label _eventEmpty = null!;
    private bool _busy;
    private readonly bool _embedded;
    private System.Windows.Forms.Timer? _searchDebounce;
    private volatile bool _refreshBusy;
    private int _refreshGen;

    public LearningPanel(Engine engine, bool embedded = false)
    {
        _engine = engine;
        _embedded = embedded;
        DoubleBuffered = true;
        BuildUi();
        if (!_embedded)
        {
            _timer = new System.Windows.Forms.Timer { Interval = 60_000 };
            _timer.Tick += (_, _) => { if (Visible) Refresh2(); };
            _timer.Start();
        }
    }

    private void BuildUi()
    {
        Padding = new Padding(Theme.Gap);
        BackColor = Theme.Bg;

        _summary = new Label
        {
            Dock = DockStyle.Top, Height = 28, ForeColor = Theme.TextSub, Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _cover = new Label
        {
            Dock = DockStyle.Top, Height = 24, ForeColor = Theme.TextMain, Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 72, BackColor = Theme.Bg, WrapContents = true,
            Padding = new Padding(0, 2, 0, 4),
        };
        _btnBackfill = Theme.MakeButton("回填历史", 90);
        _btnBackfill.Click += (_, _) => StartBackfill();
        _btnReplay = Theme.MakeButton("现场回放", 90);
        _btnReplay.Click += (_, _) => StartReplay();
        _btnReset = Theme.MakeButton("重置所选", 90);
        _btnReset.Click += (_, _) => ResetSelected();
        toolbar.Controls.Add(new Label
        {
            Text = "窗口", Width = 36, Height = 28, ForeColor = Theme.TextFaint, Font = Theme.Tiny,
            TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 4, 0, 0),
        });
        _winDays = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 4, 8, 0),
        };
        _winDays.Items.AddRange(new object[] { "自动", "近 30 天", "近 90 天", "近 180 天", "近 365 天" });
        _winDays.SelectedIndex = 0;
        _srcFilter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 4, 8, 0) };
        _srcFilter.Items.AddRange(new object[] { "全部来源", "measurement", "waveform", "device" });
        _srcFilter.SelectedIndex = 0;
        _srcFilter.SelectedIndexChanged += (_, _) => ApplyFilterOnly();
        _search = new TextBox { Width = 180, PlaceholderText = "搜索 signal / 型号", Margin = new Padding(0, 4, 0, 0) };
        _searchDebounce = UiAsync.Debounce(_search, 300, () => { if (Visible) ApplyFilterOnly(); });
        _search.TextChanged += (_, _) =>
        {
            _searchDebounce?.Stop();
            _searchDebounce?.Start();
        };
        toolbar.Controls.Add(_btnBackfill);
        toolbar.Controls.Add(_btnReplay);
        toolbar.Controls.Add(_btnReset);
        toolbar.Controls.Add(_winDays);
        toolbar.Controls.Add(_srcFilter);
        toolbar.Controls.Add(_search);

        _modelGrid = NewGrid();
        _modelGrid.Columns.Add("src", "来源");
        _modelGrid.Columns.Add("model", "型号");
        _modelGrid.Columns.Add("key", "信号");
        _modelGrid.Columns.Add("n", "n");
        _modelGrid.Columns.Add("mean", "均值±σ");
        _modelGrid.Columns.Add("q", "P01/P50/P99");
        _modelGrid.Columns.Add("st", "状态");
        _modelGrid.Columns.Add("ts", "更新");
        _modelGrid.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

        _eventGrid = NewGrid();
        _eventGrid.Columns.Add("ts", "时间");
        _eventGrid.Columns.Add("src", "来源");
        _eventGrid.Columns.Add("model", "型号");
        _eventGrid.Columns.Add("n", "信号数");
        _eventGrid.Columns.Add("top", "Top 信号");
        _eventGrid.Columns.Add("score", "分数");
        _eventGrid.Columns[4].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

        _modelEmpty = EmptyLabel("尚无正常态模型——点「回填历史」（自动补采 XML 测量值并训练），或等待新 PASS 入库。");
        _eventEmpty = EmptyLabel("尚无偏离事件（需 ≥3 个 ready 信号同时明显偏离）。");

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 280, BackColor = Theme.Bg,
        };
        var topHost = WrapGrid("模型明细", _modelGrid, _modelEmpty);
        var botHost = WrapGrid("偏离事件", _eventGrid, _eventEmpty);
        split.Panel1.Controls.Add(topHost);
        split.Panel2.Controls.Add(botHost);

        Controls.Add(split);
        Controls.Add(toolbar);
        Controls.Add(_cover);
        Controls.Add(_summary);
    }

    private static Panel WrapGrid(string title, DataGridView grid, Label empty)
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 4, 0, 0) };
        var lab = new Label
        {
            Text = title, Dock = DockStyle.Top, Height = 22, ForeColor = Theme.TextMain, Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        empty.Dock = DockStyle.Fill;
        grid.Dock = DockStyle.Fill;
        host.Controls.Add(grid);
        host.Controls.Add(empty);
        host.Controls.Add(lab);
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

    private int WindowIndex => _winDays?.SelectedIndex ?? 0;

    /// <summary>嵌入分析页时由父面板传入已加载快照，避免重复查库。</summary>
    public void BindSnapshot(LearningSnapshot snap, long measRows) => ApplySnapshot(snap, measRows);

    public void Refresh2()
    {
        if (_refreshBusy) return;
        _refreshBusy = true;
        int gen = ++_refreshGen;
        var db = _engine.Db;
        var cfg = AppConfig.Instance;
        Task.Run(() =>
        {
            try
            {
                var snap = LearningSnapshot.Capture(db, cfg);
                var meas = db.CountPassMeasurementRows();
                UiAsync.Post(this, () =>
                {
                    try
                    {
                        if (IsDisposed || gen != _refreshGen) return;
                        ApplySnapshot(snap, meas);
                    }
                    catch (Exception ex) { Logger.Warning($"[正常态] 刷新回写失败: {ex.Message}"); }
                    finally { _refreshBusy = false; }
                });
            }
            catch (Exception ex)
            {
                Logger.Warning($"[正常态] 刷新失败: {ex.Message}");
                UiAsync.Post(this, () => _refreshBusy = false);
            }
        });
    }

    private void ApplyFilterOnly()
    {
        if (_lastSnap == null) return;
        ApplyGrids(_lastSnap, AppConfig.Instance);
    }

    private LearningSnapshot? _lastSnap;

    private void ApplySnapshot(LearningSnapshot snap, long measRows)
    {
        _lastSnap = snap;
        var db = _engine.Db;
        _summary.Text = (snap.Enabled ? "学习开" : "学习关（learn_normal_enabled=false）") + "  ·  " + snap.HeartbeatSummary();
        var pct = snap.Total == 0 ? 0 : 100.0 * snap.Ready / snap.Total;
        var winHint = WindowIndex == 0
            ? $"自动≈{LearnPipeline.ResolveBackfillDays(db, 0)}天"
            : _winDays?.SelectedItem?.ToString() ?? "";
        _cover.Text = $"测量缓存 {measRows} 行  ·  覆盖 {snap.Ready}/{snap.Total} ready（{pct:F0}%）  learning {snap.Learning}  stale {snap.Stale}  ·  {winHint}";
        ApplyGrids(snap, AppConfig.Instance);
    }

    private void ApplyGrids(LearningSnapshot snap, AppConfig cfg)
    {
        var src = _srcFilter.SelectedIndex <= 0 ? "" : _srcFilter.SelectedItem?.ToString() ?? "";
        var q = (_search.Text ?? "").Trim();
        var models = snap.Models.Where(m =>
            (src.Length == 0 || m.Source == src) &&
            (q.Length == 0 || (m.SignalKey?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) ||
             (m.Model?.Contains(q, StringComparison.OrdinalIgnoreCase) == true))).ToList();
        var now = DateTime.Now;
        UiAsync.RunGridUpdate(_modelGrid, () =>
        {
            _modelGrid.Rows.Clear();
            _modelEmpty.Visible = models.Count == 0;
            _modelGrid.Visible = models.Count > 0;
            foreach (var m in models)
            {
                var st = NormalModelStore.EffectiveStatus(m, now, cfg.LearnNormalStaleDays);
                _modelGrid.Rows.Add(m.Source, m.Model, m.SignalKey, m.N.ToString(),
                    $"{m.Mean:F4}±{m.Sigma:F4}", $"{m.P01:F3}/{m.P50:F3}/{m.P99:F3}", st, m.LastTs);
                _modelGrid.Rows[^1].Tag = m;
            }
        });
        UiAsync.RunGridUpdate(_eventGrid, () =>
        {
            _eventGrid.Rows.Clear();
            _eventEmpty.Visible = snap.Events.Count == 0;
            _eventGrid.Visible = snap.Events.Count > 0;
            foreach (var e in snap.Events)
                _eventGrid.Rows.Add(e.Ts, e.Source, e.Model, e.SignalCount.ToString(), e.TopSignal, e.TopScore.ToString("F0"));
        });
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        _btnBackfill.Enabled = !busy;
        _btnReplay.Enabled = !busy;
        _btnReset.Enabled = !busy;
        _winDays.Enabled = !busy;
        if (status != null) _summary.Text = status;
    }

    /// <summary>回填窗口大于配置保留期时提示写入 config，避免补完又被每日清理删掉。</summary>
    private bool EnsureRetentionCoversBackfill()
    {
        var cfg = AppConfig.Instance;
        int days = LearnPipeline.EffectiveMeasureRetentionDays(cfg, _engine.Db, WindowIndex);
        int configured = Math.Clamp(cfg.AnalyzeMeasureRetentionDays,
            LearnPipeline.MeasureRetentionMin, LearnPipeline.MeasureRetentionMax);
        if (configured >= days) return true;
        var ans = MessageBox.Show(
            $"当前 analyze_measure_retention_days = {configured}，小于本次回填/维护窗口 {days} 天。\n" +
            "若不提高，库维护会继续删除更早的 test_measurements，分析页自定义日期仍会缺旧段。\n\n" +
            $"是否改为 {days} 并保存 config.json？",
            "测量保留期", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (ans == DialogResult.Cancel) return false;
        if (ans == DialogResult.Yes)
        {
            cfg.AnalyzeMeasureRetentionDays = days;
            cfg.Save();
        }
        return true;
    }

    private void StartBackfill()
    {
        if (_busy) return;
        if (!AppConfig.Instance.LearnNormalEnabled)
        {
            MessageBox.Show("请先在 config.json 打开 learn_normal_enabled。", "自学习", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!AppConfig.Instance.AnalyzeCollectPass &&
            !(AppConfig.Instance.AnalyzeTdmsEnabled && !string.IsNullOrWhiteSpace(AppConfig.Instance.TdmsRoot)))
        {
            if (MessageBox.Show(
                    "analyze_collect_pass 为 false 且未配置 TDMS，只能做 SN 补采，无法从 XML 补测量值。\n仍继续？",
                    "自学习", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;
        }
        if (!EnsureRetentionCoversBackfill()) return;

        SetBusy(true, "正在回填（补采 XML → 训练模型）…");
        var progress = new Progress<string>(msg => BeginInvoke(() => _summary.Text = msg));
        Task.Run(() =>
        {
            LearnPipeline.FullBackfillResult? result = null;
            string? err = null;
            try { result = LearnPipeline.RunFullBackfill(_engine, WindowIndex, progress); }
            catch (Exception ex) { err = ex.Message; Logger.Warning($"[正常态] UI 回填失败: {ex.Message}"); }
            BeginInvoke(() =>
            {
                SetBusy(false);
                Refresh2();
                if (err != null)
                    MessageBox.Show($"回填失败：{err}", "自学习", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else if (result != null)
                    _summary.Text = "回填完成：" + result.FormatSummary() + "  ·  " +
                                    LearningSnapshot.Capture(_engine.Db, AppConfig.Instance).HeartbeatSummary();
            });
        });
    }

    private void StartReplay()
    {
        if (_busy) return;
        int days = LearnPipeline.ResolveBackfillDays(_engine.Db, WindowIndex);
        if (MessageBox.Show(
                $"将复制本机库到临时库做现场回放（不动原库），窗口 {days} 天。\n可能耗时数分钟，是否继续？",
                "现场回放", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        SetBusy(true, "现场回放准备中…");
        var cfg = AppConfig.Instance;
        var dbPath = _engine.Db.DbPath;
        Task.Run(() =>
        {
            ReplayReport? rp = null;
            string? err = null;
            try
            {
                try { if (!IsDisposed) BeginInvoke(() => _summary.Text = $"现场回放运行中（{days} 天）…"); } catch { }
                rp = ReplayRunner.RunField(dbPath, cfg, days, null);
            }
            catch (Exception ex) { err = ex.Message; Logger.Warning($"[正常态] 现场回放失败: {ex.Message}"); }
            try
            {
                if (IsDisposed || !IsHandleCreated) return; // 审计：窗体已关闭时不再回写 UI
                BeginInvoke(() =>
                {
                    SetBusy(false);
                    Refresh2();
                    if (err != null)
                    {
                        MessageBox.Show($"回放失败：{err}", "现场回放", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (rp == null) return;
                    var body = $"检出率 {rp.DetectionRate:P0}\n误报 {rp.FalsePosPerDay:F2} 条/天\nready 覆盖 {rp.ReadyCoverage:P0}\n偏离事件 {rp.EventsTotal}\n\n{(rp.Pass ? "三指标达标" : "三指标未达标")}";
                    MessageBox.Show(body, "现场回放结果", MessageBoxButtons.OK,
                        rp.Pass ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                });
            }
            catch (Exception ex) { Logger.Warning($"[正常态] 回放 UI 回写失败: {ex.Message}"); }
        });
    }

    private void ResetSelected()
    {
        if (_modelGrid.CurrentRow?.Tag is not NormalModelRow row) return;
        if (MessageBox.Show($"重置 {row.Source}/{row.Model}/{row.SignalKey}？将清零重新学习。", "自学习",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try { _engine.Db.DeleteNormalModel(row.Source, row.Model, row.SignalKey); }
        catch (Exception ex) { Logger.Warning($"[正常态] 重置失败: {ex.Message}"); }
        Refresh2();
    }
}

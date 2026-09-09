namespace FctAggregator;

/// <summary>正常态自学习区：心跳 / 覆盖率 / 模型明细 / 偏离事件（嵌入「分析」页）。数据全部本机直查。</summary>
public class LearningPanel : Panel
{
    private readonly Engine _engine;
    private System.Windows.Forms.Timer _timer = null!;
    private Label _summary = null!;
    private Label _cover = null!;
    private Button _btnBackfill = null!;
    private Button _btnReset = null!;
    private ComboBox _srcFilter = null!;
    private TextBox _search = null!;
    private DataGridView _modelGrid = null!;
    private DataGridView _eventGrid = null!;
    private Label _modelEmpty = null!;
    private Label _eventEmpty = null!;
    private bool _busy;

    public LearningPanel(Engine engine)
    {
        _engine = engine;
        DoubleBuffered = true;
        BuildUi();
        _timer = new System.Windows.Forms.Timer { Interval = 30000 };
        _timer.Tick += (_, _) => { if (Visible) Refresh2(); };
        _timer.Start();
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
            Dock = DockStyle.Top, Height = 36, BackColor = Theme.Bg, WrapContents = false,
        };
        _btnBackfill = Theme.MakeButton("回填历史", 90);
        _btnBackfill.Click += (_, _) => StartBackfill();
        _btnReset = Theme.MakeButton("重置所选", 90);
        _btnReset.Click += (_, _) => ResetSelected();
        _srcFilter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120, FlatStyle = FlatStyle.Flat };
        _srcFilter.Items.AddRange(new object[] { "全部来源", "measurement", "waveform", "device" });
        _srcFilter.SelectedIndex = 0;
        _srcFilter.SelectedIndexChanged += (_, _) => Refresh2();
        _search = new TextBox { Width = 180, PlaceholderText = "搜索 signal / 型号" };
        _search.TextChanged += (_, _) => Refresh2();
        toolbar.Controls.Add(_btnBackfill);
        toolbar.Controls.Add(_btnReset);
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

        _modelEmpty = EmptyLabel("尚无正常态模型——打开 learn_normal_enabled 后采集 PASS，或点「回填历史」。");
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
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
        BackgroundColor = Theme.Surface, BorderStyle = BorderStyle.None,
    };

    private static Label EmptyLabel(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, ForeColor = Theme.TextFaint, Font = Theme.Small,
        TextAlign = ContentAlignment.MiddleCenter, BackColor = Theme.Surface,
    };

    public void Refresh2()
    {
        var db = _engine.Db;
        var cfg = AppConfig.Instance;
        var snap = LearningSnapshot.Capture(db, cfg);
        _summary.Text = (snap.Enabled ? "学习开" : "学习关（learn_normal_enabled=false）") + "  ·  " + snap.HeartbeatSummary();
        var pct = snap.Total == 0 ? 0 : 100.0 * snap.Ready / snap.Total;
        _cover.Text = $"覆盖 {snap.Ready}/{snap.Total} ready（{pct:F0}%）  learning {snap.Learning}  stale {snap.Stale}";

        var src = _srcFilter.SelectedIndex <= 0 ? "" : _srcFilter.SelectedItem?.ToString() ?? "";
        var q = (_search.Text ?? "").Trim();
        var models = snap.Models.Where(m =>
            (src.Length == 0 || m.Source == src) &&
            (q.Length == 0 || (m.SignalKey?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) ||
             (m.Model?.Contains(q, StringComparison.OrdinalIgnoreCase) == true))).ToList();
        _modelGrid.Rows.Clear();
        _modelEmpty.Visible = models.Count == 0;
        _modelGrid.Visible = models.Count > 0;
        var now = DateTime.Now;
        foreach (var m in models)
        {
            var st = NormalModelStore.EffectiveStatus(m, now, cfg.LearnNormalStaleDays);
            _modelGrid.Rows.Add(m.Source, m.Model, m.SignalKey, m.N.ToString(),
                $"{m.Mean:F4}±{m.Sigma:F4}", $"{m.P01:F3}/{m.P50:F3}/{m.P99:F3}", st, m.LastTs);
            _modelGrid.Rows[^1].Tag = m;
        }

        _eventGrid.Rows.Clear();
        _eventEmpty.Visible = snap.Events.Count == 0;
        _eventGrid.Visible = snap.Events.Count > 0;
        foreach (var e in snap.Events)
            _eventGrid.Rows.Add(e.Ts, e.Source, e.Model, e.SignalCount.ToString(), e.TopSignal, e.TopScore.ToString("F0"));
    }

    private void StartBackfill()
    {
        if (_busy) return;
        if (!AppConfig.Instance.LearnNormalEnabled)
        {
            MessageBox.Show("请先在 config.json 打开 learn_normal_enabled。", "自学习", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _busy = true;
        _btnBackfill.Enabled = false;
        _summary.Text = "正在回填…";
        var db = _engine.Db;
        var cfg = AppConfig.Instance;
        Task.Run(() =>
        {
            int n = 0;
            try { n = LearnBackfill.Run(db, cfg, 30); }
            catch (Exception ex) { Logger.Warning($"[正常态] UI 回填失败: {ex.Message}"); }
            BeginInvoke(() =>
            {
                _busy = false;
                _btnBackfill.Enabled = true;
                Refresh2();
                _summary.Text = $"回填完成，处理窗口 {n}。  " + LearningSnapshot.Capture(db, cfg).HeartbeatSummary();
            });
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

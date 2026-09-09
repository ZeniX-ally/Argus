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
    private SignalDeviationChart _devChart = null!;
    private ComboBox _winDays = null!;
    private LearningPanel _learnPanel = null!;
    private volatile bool _chartBusy;

    public AnalysisPanel(Engine engine)
    {
        _engine = engine;
        DoubleBuffered = true;
        BuildUi();
        _timer = new System.Windows.Forms.Timer { Interval = 60000 };
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

        _devChart = new SignalDeviationChart { Dock = DockStyle.Fill };
        _winDays = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = 96, Left = 78, Top = 7,
        };
        _winDays.Items.AddRange(new object[] { "近 3 天", "近 7 天", "近 14 天" });
        _winDays.SelectedIndex = 1;
        _winDays.SelectedIndexChanged += (_, _) => RefreshDeviationChart();
        var chartToolbar = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Theme.Surface };
        chartToolbar.Controls.Add(new Label
        {
            Text = "时间窗口", Left = 12, Top = 10, Width = 60, Height = 20,
            ForeColor = Theme.TextFaint, Font = Theme.Tiny, TextAlign = ContentAlignment.MiddleLeft,
        });
        chartToolbar.Controls.Add(_winDays);
        var chartHost = new Panel { Dock = DockStyle.Top, Height = 360, BackColor = Theme.Surface, Padding = new Padding(0, 0, 0, 8) };
        chartHost.Controls.Add(_devChart);
        chartHost.Controls.Add(chartToolbar);

        _learnPanel = new LearningPanel(_engine) { Dock = DockStyle.Top, Height = 420 };

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
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
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
        try { AnalysisEngine.RunOnce(db, _engine.Config); }
        catch (Exception ex) { Logger.Warning($"[分析] 刷新重算失败: {ex.Message}"); }

        var attr = AnalysisEngine.GetAttributionState(db);
        var snap = LearningSnapshot.Capture(db, _engine.Config);
        var tdms = AnalysisEngine.GetTdmsState(db);

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

        FillAttr(attr);
        FillTdms(db);
        RefreshDeviationChart();
        _learnPanel.Refresh2();
    }

    private void RefreshDeviationChart()
    {
        if (_chartBusy) return;
        _chartBusy = true;
        int days = _winDays?.SelectedIndex switch { 0 => 3, 1 => 7, _ => 14 };
        var db = _engine.Db;
        var cfg = _engine.Config;
        Task.Run(() =>
        {
            List<string> labels = new();
            List<DeviationSeries> series = new();
            try { (labels, series) = DeviationSeriesBuilder.Build(db, cfg, days); }
            catch (Exception ex) { Logger.Warning($"[分析] 偏离折线构建失败: {ex.Message}"); }
            try
            {
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(() => { _devChart?.SetData(labels, series); });
            }
            catch { }
            finally { _chartBusy = false; }
        });
    }

    private void FillTdms(Database db)
    {
        _tdmsGrid.Rows.Clear();
        var rows = db.GetRecentTdmsFeatures(50);
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
    }

    private void FillAttr(AttributionState? state)
    {
        _attrGrid.Rows.Clear();
        var rows = (state?.Buckets ?? new List<AttributionBucketItem>())
            .Where(b => string.Equals(b.Dim, "hour", StringComparison.OrdinalIgnoreCase))
            .ToList();
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
    }
}

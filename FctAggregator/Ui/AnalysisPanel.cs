namespace FctAggregator;

/// <summary>规格06：深度分析页——漂移/逼近限预警 + CPK 低能力清单（数据源 app_meta 分析快照）。</summary>
public class AnalysisPanel : Panel
{
    private readonly Engine _engine;
    private System.Windows.Forms.Timer _timer = null!;
    private Label _summary = null!;
    private DataGridView _driftGrid = null!;
    private DataGridView _cpkGrid = null!;
    private Label _driftEmpty = null!;
    private Label _cpkEmpty = null!;

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
            Dock = DockStyle.Top, Height = 24, ForeColor = Theme.TextSub, Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(2, 2, 0, 2),
        };

        _driftGrid = NewGrid();
        _driftGrid.Columns.Add("kind", "类型");
        _driftGrid.Columns.Add("model", "型号");
        _driftGrid.Columns.Add("test", "测项");
        _driftGrid.Columns.Add("today", "今日均值");
        _driftGrid.Columns.Add("base", "基线均值");
        _driftGrid.Columns.Add("expect", "期望区间");
        _driftGrid.Columns.Add("score", "得分");
        _driftGrid.Columns.Add("n", "样本(基/今)");
        _driftGrid.Columns.Add("msg", "说明");
        _driftGrid.Columns[^1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

        _cpkGrid = NewGrid();
        _cpkGrid.Columns.Add("model", "型号");
        _cpkGrid.Columns.Add("test", "测项");
        _cpkGrid.Columns.Add("cpk", "CPK");
        _cpkGrid.Columns.Add("n", "样本");
        _cpkGrid.Columns.Add("unit", "单位");
        _cpkGrid.Columns.Add("lo", "下限");
        _cpkGrid.Columns.Add("hi", "上限");

        _driftEmpty = EmptyLabel();
        _cpkEmpty = EmptyLabel();

        var driftHost = new Panel { Dock = DockStyle.Top, BackColor = Theme.Surface, Padding = new Padding(12, 8, 12, 10) };
        driftHost.Height = 320;
        driftHost.Controls.Add(_driftGrid);
        driftHost.Controls.Add(_driftEmpty);
        driftHost.Controls.Add(SectionTitle("规格带漂移 / 逼近规格限预警"));

        var cpkHost = new Panel { Dock = DockStyle.Top, BackColor = Theme.Surface, Padding = new Padding(12, 8, 12, 10) };
        cpkHost.Height = 320;
        cpkHost.Controls.Add(_cpkGrid);
        cpkHost.Controls.Add(_cpkEmpty);
        cpkHost.Controls.Add(SectionTitle("CPK 低能力清单（< 1.33）"));

        Controls.Add(cpkHost);
        Controls.Add(driftHost);
        Controls.Add(_summary);
    }

    private static DataGridView NewGrid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
        BackgroundColor = Theme.Surface, BorderStyle = BorderStyle.None,
    };

    private static Label SectionTitle(string text) => new()
    {
        Text = text, Dock = DockStyle.Top, Height = 30,
        Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
        ForeColor = Theme.Primary,
        TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(2, 0, 0, 0),
    };

    private static Label EmptyLabel() => new()
    {
        Text = "", Dock = DockStyle.Fill, ForeColor = Theme.TextFaint, Font = Theme.Small,
        TextAlign = ContentAlignment.MiddleCenter, BackColor = Theme.Surface,
    };

    public void Refresh2()
    {
        var db = _engine.Db;
        var drift = AnalysisEngine.GetDriftState(db);
        var cpk = AnalysisEngine.GetCpkState(db);

        if (drift == null && cpk == null)
        {
            _summary.Text = "暂无分析结果——分析引擎在每日维护窗口自动运行（db_maintenance_hour，默认 3 点），" +
                            "也可用 Argus.exe backfill 先回填存量数据加速冷启动。";
        }
        else
        {
            var parts = new List<string>();
            if (drift != null)
                parts.Add($"漂移快照 {drift.Date}（窗口 {drift.WindowDays} 天，分析 {drift.AnalyzedTests} 测项）：预警 {drift.Alerts.Count} 项");
            if (cpk != null)
                parts.Add($"CPK 快照 {cpk.Date}（窗口 {cpk.WindowDays} 天）：低能力 {cpk.LowCount}/{cpk.AnalyzedTests} 项");
            _summary.Text = string.Join("；", parts) + "。样本不足 30 的测项不参与评级（冷启动保护）。";
        }

        FillDrift(drift);
        FillCpk(cpk);
    }

    private void FillDrift(DriftState? state)
    {
        _driftGrid.Rows.Clear();
        var rows = state?.Alerts ?? new List<DriftAlertItem>();
        _driftEmpty.Visible = rows.Count == 0;
        _driftGrid.Visible = rows.Count > 0;
        if (state == null) _driftEmpty.Text = "暂无漂移快照（每日维护窗口自动计算）";
        else if (rows.Count == 0) _driftEmpty.Text = "今日无漂移/逼近限预警";
        foreach (var a in rows)
        {
            _driftGrid.Rows.Add(
                a.Kind == "drift" ? "均值漂移" : "逼近规格限",
                a.Model, a.TestName,
                a.TodayMean.ToString("F4"), a.BaseMean.ToString("F4"),
                a.Kind == "drift" ? $"[{a.ExpectedLow:F4}, {a.ExpectedHigh:F4}]" : "-",
                a.Score.ToString("F2"), $"{a.NBase}/{a.NToday}", a.Message);
        }
    }

    private void FillCpk(CpkState? state)
    {
        _cpkGrid.Rows.Clear();
        var rows = state?.Items ?? new List<CpkItem>();
        _cpkEmpty.Visible = rows.Count == 0;
        _cpkGrid.Visible = rows.Count > 0;
        if (state == null) _cpkEmpty.Text = "暂无 CPK 快照（每日维护窗口自动计算）";
        else if (rows.Count == 0) _cpkEmpty.Text = "窗口期内无 CPK < 1.33 的测项";
        foreach (var c in rows)
        {
            _cpkGrid.Rows.Add(
                c.Model, c.TestName,
                c.Cpk?.ToString("F3") ?? "-",
                c.N.ToString(),
                c.Unit ?? "", c.Lolim?.ToString("F4") ?? "-", c.Hilim?.ToString("F4") ?? "-");
        }
    }
}

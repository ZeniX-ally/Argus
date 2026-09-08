namespace FctAggregator;

/// <summary>规格06：深度分析页——漂移/逼近限预警 + CPK 低能力清单 + 失败归因 + tester 效应量 + 章节群挂。</summary>
public class AnalysisPanel : Panel
{
    private readonly Engine _engine;
    private System.Windows.Forms.Timer _timer = null!;
    private Label _summary = null!;
    private DataGridView _driftGrid = null!;
    private DataGridView _cpkGrid = null!;
    private DataGridView _attrGrid = null!;
    private DataGridView _effectGrid = null!;
    private DataGridView _groupGrid = null!;
    private Label _driftEmpty = null!;
    private Label _cpkEmpty = null!;
    private Label _attrEmpty = null!;
    private Label _effectEmpty = null!;
    private Label _groupEmpty = null!;
    private DataGridView _tdmsGrid = null!;
    private Label _tdmsEmpty = null!;

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
            Dock = DockStyle.Top, Height = 40, ForeColor = Theme.TextSub, Font = Theme.Small,
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

        _attrGrid = NewGrid();
        _attrGrid.Columns.Add("dim", "维度");
        _attrGrid.Columns.Add("key", "桶值");
        _attrGrid.Columns.Add("n", "样本");
        _attrGrid.Columns.Add("fails", "失败");
        _attrGrid.Columns.Add("rate", "失败率");
        _attrGrid.Columns.Add("global", "全局率");
        _attrGrid.Columns.Add("ratio", "偏差比");
        _attrGrid.Columns.Add("panelng", "panel NG 率");
        _attrGrid.Columns.Add("msg", "说明");
        _attrGrid.Columns[^1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

        _effectGrid = NewGrid();
        _effectGrid.Columns.Add("model", "型号");
        _effectGrid.Columns.Add("test", "测项");
        _effectGrid.Columns.Add("a", "TesterA(均值/样本)");
        _effectGrid.Columns.Add("b", "TesterB(均值/样本)");
        _effectGrid.Columns.Add("d", "效应量 d");
        _effectGrid.Columns.Add("msg", "说明");
        _effectGrid.Columns[^1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

        _groupGrid = NewGrid();
        _groupGrid.Columns.Add("section", "章节");
        _groupGrid.Columns.Add("count", "不同信号数");
        _groupGrid.Columns.Add("signals", "信号");
        _groupGrid.Columns.Add("msg", "提示");
        _groupGrid.Columns[^1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

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

        _driftEmpty = EmptyLabel("暂无漂移快照（每日维护窗口自动计算）");
        _cpkEmpty = EmptyLabel("暂无 CPK 快照（每日维护窗口自动计算）");
        _attrEmpty = EmptyLabel("暂无归因快照（每日维护窗口自动计算）");
        _effectEmpty = EmptyLabel("暂无 tester 对比快照（每日维护窗口自动计算）");
        _groupEmpty = EmptyLabel("暂无章节群挂预警");
        _tdmsEmpty = EmptyLabel("TDMS 特征化未启用或无数据（需配置 tdms_root 并打开 analyze_tdms_enabled）");

        var driftHost = SectionHost("规格带漂移 / 逼近规格限预警", _driftGrid, _driftEmpty);
        var cpkHost = SectionHost("CPK 低能力清单（< 1.33）", _cpkGrid, _cpkEmpty);
        var attrHost = SectionHost("失败多维归因（治具 / 测试员 / 时段 异常桶）", _attrGrid, _attrEmpty);
        var effectHost = SectionHost("Tester 效应量对比（同测项均值差 / 合并σ）", _effectGrid, _effectEmpty);
        var groupHost = SectionHost("章节群挂预警（规格 05 自学习）", _groupGrid, _groupEmpty);
        var tdmsHost = SectionHost("TDMS 特征化（最近通道统计，FAIL+PASS）", _tdmsGrid, _tdmsEmpty);

        Controls.Add(tdmsHost);
        Controls.Add(groupHost);
        Controls.Add(effectHost);
        Controls.Add(attrHost);
        Controls.Add(cpkHost);
        Controls.Add(driftHost);
        Controls.Add(_summary);
    }

    private static Panel SectionHost(string title, DataGridView grid, Label empty)
    {
        var host = new Panel { Dock = DockStyle.Top, BackColor = Theme.Surface, Padding = new Padding(12, 8, 12, 10) };
        host.Height = 280;
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
        var drift = AnalysisEngine.GetDriftState(db);
        var cpk = AnalysisEngine.GetCpkState(db);
        var attr = AnalysisEngine.GetAttributionState(db);
        var fixture = AnalysisEngine.GetFixtureState(db);
        var group = LearningEngine.GetGroupAlerts(db);
        var tdms = AnalysisEngine.GetTdmsState(db);

        if (drift == null && cpk == null && attr == null && fixture == null && tdms == null)
        {
            _summary.Text = "暂无分析结果——分析引擎在每日维护窗口自动运行（db_maintenance_hour，默认 3 点），" +
                            "也可用 Argus.exe backfill 先回填存量数据加速冷启动。";
        }
        else
        {
            var parts = new List<string>();
            if (drift != null)
                parts.Add($"漂移 {drift.Date}（{drift.WindowDays} 天窗口，{drift.AnalyzedTests} 测项）：预警 {drift.Alerts.Count}");
            if (cpk != null)
                parts.Add($"CPK：低能力 {cpk.LowCount}/{cpk.AnalyzedTests}");
            if (attr != null)
                parts.Add($"归因 {attr.Date}（全局失败率 {attr.GlobalFailRate:P1}）：异常桶 {attr.Buckets.Count}，资源相关 {attr.ResourceCorrelations.Count}" +
                          (attr.DeviationEvidence.Count > 0 ? $"，偏离证据 {attr.DeviationEvidence.Count}" : ""));
            if (fixture != null)
                parts.Add($"tester 对比：效应量异常 {fixture.Effects.Count}/{fixture.AnalyzedTests} 测项");
            if (tdms != null)
                parts.Add($"TDMS 特征化：{tdms.RowsTotal} 行 / {tdms.RecordsTotal} 记录（跳过 {tdms.FilesSkipped}）");
            _summary.Text = string.Join("；", parts) + "。样本不足的测项/桶不参与评级（冷启动保护）。";
        }

        FillDrift(drift);
        FillCpk(cpk);
        FillAttr(attr);
        FillEffect(fixture);
        FillGroup(group);
        FillTdms(db);
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

    private void FillDrift(DriftState? state)
    {
        _driftGrid.Rows.Clear();
        var rows = state?.Alerts ?? new List<DriftAlertItem>();
        _driftEmpty.Visible = rows.Count == 0;
        _driftGrid.Visible = rows.Count > 0;
        if (state != null && rows.Count == 0) _driftEmpty.Text = "今日无漂移/逼近限预警";
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
        if (state != null && rows.Count == 0) _cpkEmpty.Text = "窗口期内无 CPK < 1.33 的测项";
        foreach (var c in rows)
        {
            _cpkGrid.Rows.Add(
                c.Model, c.TestName,
                c.Cpk?.ToString("F3") ?? "-",
                c.N.ToString(),
                c.Unit ?? "", c.Lolim?.ToString("F4") ?? "-", c.Hilim?.ToString("F4") ?? "-");
        }
    }

    private void FillAttr(AttributionState? state)
    {
        _attrGrid.Rows.Clear();
        var rows = state?.Buckets ?? new List<AttributionBucketItem>();
        _attrEmpty.Visible = rows.Count == 0;
        _attrGrid.Visible = rows.Count > 0;
        if (state != null && rows.Count == 0)
            _attrEmpty.Text = state.GlobalFailRate <= 0 ? "窗口期内全局零失败，无可比基线" : "无超阈值异常桶";
        foreach (var b in rows)
        {
            _attrGrid.Rows.Add(
                b.Dim switch { "fixture" => "治具", "tester" => "测试员", _ => "时段" },
                b.Key, b.N, b.Fails,
                b.FailRate.ToString("P1"), b.GlobalRate.ToString("P1"),
                b.Ratio.ToString("F2") + "×", b.PanelNgRate.ToString("P1"), b.Hint);
        }
    }

    private void FillEffect(FixtureState? state)
    {
        _effectGrid.Rows.Clear();
        var rows = state?.Effects ?? new List<EffectItem>();
        _effectEmpty.Visible = rows.Count == 0;
        _effectGrid.Visible = rows.Count > 0;
        if (state != null && rows.Count == 0)
            _effectEmpty.Text = "无 |d| ≥ 阈值的测项（或 tester 样本不足 / 存量 PASS 无 tester 冷启动中）";
        foreach (var e in rows)
        {
            _effectGrid.Rows.Add(
                e.Model, e.TestName,
                $"{e.TesterA}({e.MeanA:F4}/{e.N1})",
                $"{e.TesterB}({e.MeanB:F4}/{e.N2})",
                e.D.ToString("F2"), e.Hint);
        }
    }

    private void FillGroup(GroupAlertState? state)
    {
        _groupGrid.Rows.Clear();
        var rows = state?.Alerts ?? new List<GroupAlertItem>();
        _groupEmpty.Visible = rows.Count == 0;
        _groupGrid.Visible = rows.Count > 0;
        if (state != null && rows.Count == 0)
            _groupEmpty.Text = $"今日无章节群挂预警（阈值 {state.Threshold} 个不同信号族）";
        foreach (var g in rows)
        {
            _groupGrid.Rows.Add(
                "§" + g.Section, g.DistinctSignalCount.ToString(),
                string.Join("、", g.Signals), string.IsNullOrEmpty(g.Message) ? g.Hint : g.Message);
        }
    }
}

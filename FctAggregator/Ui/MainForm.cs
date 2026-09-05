using System.Text;

namespace FctAggregator;

public class MainForm : Form
{
    private readonly Engine _engine;
    private readonly AppConfig _cfg;

    private TabControl _tabs = null!;
    private Label _pageTitle = null!;
    private ChipBar _chips = null!;
    private Panel _progressPanel = null!;
    private Label _progressLabel = null!;
    private ProgressBar _progressBar = null!;
    private Label _statusLeft = null!;
    private Label _statusRight = null!;
    private Label _lblAggLink = null!;
    private ToolTip _aggLinkTip = null!;
    private System.Windows.Forms.Timer _aggLinkTimer = null!;

    private readonly Dictionary<string, KpiCard> _kpi = new();
    private readonly Dictionary<string, Label> _statValues = new();
    private SectionPanel _detailPanel = null!;
    private HourlyTrendChart _hourlyChart = null!;
    private TodayGaugePanel _todayGauge = null!;
    private TopFailRankPanel _topFailRank = null!;
    private LiveAlertPanel _liveAlert = null!;

    private Panel _dashboardPage = null!;
    private DeviceStatusPanel _devicePage = null!;
    private MaintenancePanel _maintPage = null!;
    private FailListPanel _failPage = null!;
    private DebugPanel _debugPage = null!;
    private System.Windows.Forms.Timer _timer = null!;
    private System.Windows.Forms.Timer? _autoUpdateTimer;

    private static readonly string[] PageTitles =
    {
        "总览", "待办 / 维修记录", "设备状态", "FAIL 记录", "调试工具",
        "TDMS 波形", "聚合看板",
    };

    private const int OwnPageCount = 5;

    private static readonly (string key, string label, Func<Form> factory)[] Tools =
    {
        ("tdms",       "TDMS 波形", () => new FctTdmsViewer.MainForm()),
        ("agg",        "聚合看板", () => new AggCenterForm(
            AppConfig.Instance.AggShareRoot,
            Path.Combine(AppConfig.BaseDir, "data", "agg_center.db"))),
    };

    private readonly ToolHost[] _toolPages = new ToolHost[Tools.Length];

    private int _page = 0;

    private const int DebugPageIndex = 4;

    public MainForm(Engine engine, bool debugMode = false)
    {
        _engine = engine;
        _cfg = AppConfig.Instance;
        Text = "Argus";
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Width = Math.Min(1408, wa.Width);
        Height = Math.Min(768, wa.Height);
        MinimumSize = new Size(1200, 720);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        BackColor = Theme.Bg;
        AppIcon.Apply(this);
        Font = Theme.Body;
        KeyPreview = true;

        BuildUi();

        DesktopNotifier.Enabled = _cfg.DesktopNotify;
        DesktopNotifier.MinIntervalSeconds = _cfg.NotifyMinIntervalSec;
        DesktopNotifier.Activated += OnNotificationClicked;
        DesktopNotifier.Init();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    private void BuildUi()
    {

        var contentHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        Controls.Add(contentHost);

        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = Theme.StatusBarHeight, BackColor = Theme.Surface };
        statusBar.Paint += (_, e) =>
        {
            using var p = new Pen(Theme.Border);
            e.Graphics.DrawLine(p, 0, 0, statusBar.Width, 0);
        };
        _statusLeft = new Label
        {
            Dock = DockStyle.Left, Width = 760, ForeColor = Theme.TextSub, Font = Theme.Small,
            BackColor = Theme.Surface, Padding = new Padding(14, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft,
        };
        _statusRight = new Label
        {
            Dock = DockStyle.Right, Width = 280, ForeColor = Theme.TextFaint, Font = Theme.Small,
            BackColor = Theme.Surface, Padding = new Padding(0, 0, 14, 0), TextAlign = ContentAlignment.MiddleRight,
        };
        var btnAggSettings = new Button
        {
            Text = "聚合设置",
            Dock = DockStyle.Right, Width = 96, Height = Theme.StatusBarHeight,
            FlatStyle = FlatStyle.Flat, BackColor = Theme.Surface, ForeColor = Theme.TextSub,
            Font = Theme.Small, Cursor = Cursors.Hand,
        };
        btnAggSettings.FlatAppearance.BorderColor = Theme.Border;
        btnAggSettings.Click += (_, _) => ShowAggSettingsDialog();
        _lblAggLink = new Label
        {
            Text = "聚合：…",
            Dock = DockStyle.Right, Width = 150, TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Theme.TextFaint, BackColor = Theme.Surface, Font = Theme.Small,
            Cursor = Cursors.Hand,
        };
        _lblAggLink.Click += (_, _) => ShowAggSettingsDialog();
        _aggLinkTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 400, ReshowDelay = 200 };
        _aggLinkTip.SetToolTip(_lblAggLink, "聚合链路状态");
        _aggLinkTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _aggLinkTimer.Tick += (_, _) => { try { UpdateAggLinkUi(); } catch { } };
        _aggLinkTimer.Start();
        statusBar.Controls.Add(_statusRight);
        statusBar.Controls.Add(btnAggSettings);
        statusBar.Controls.Add(_lblAggLink);
        statusBar.Controls.Add(_statusLeft);
        Controls.Add(statusBar);

        _progressPanel = new Panel
        {
            Dock = DockStyle.Top, Height = 28, BackColor = Theme.Surface,
            Padding = new Padding(18, 5, 18, 5), Visible = false,
        };
        _progressLabel = new Label
        {
            Dock = DockStyle.Left, Width = 230, Text = "扫描准备中…", ForeColor = Theme.TextSub,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _progressBar = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee };
        _progressPanel.Controls.Add(_progressBar);
        _progressPanel.Controls.Add(_progressLabel);
        Controls.Add(_progressPanel);

        var topBar = new Panel { Dock = DockStyle.Top, Height = Theme.TopBarHeight, BackColor = Theme.Surface };
        topBar.Paint += (_, e) =>
        {
            using var p = new Pen(Theme.Border);
            e.Graphics.DrawLine(p, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
        };
        _pageTitle = new Label
        {
            Dock = DockStyle.Left, Width = 260, Text = PageTitles[0],
            Font = Theme.PageTitle, ForeColor = Theme.TextMain, BackColor = Theme.Surface,
            Padding = new Padding(18, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft,
        };
        _chips = new ChipBar { Dock = DockStyle.Fill };
        var btnRefresh = Theme.MakeButton("刷新", 74);
        btnRefresh.Dock = DockStyle.Fill;
        btnRefresh.Margin = new Padding(0);
        btnRefresh.Click += (_, _) => RefreshCurrentPage();
        var refreshHost = new Panel
        {
            Dock = DockStyle.Right, Width = 94, BackColor = Theme.Surface, Padding = new Padding(9, 2, 9, 2),
        };
        refreshHost.Controls.Add(btnRefresh);
        topBar.Controls.Add(_chips);
        topBar.Controls.Add(refreshHost);
        topBar.Controls.Add(_pageTitle);

        Controls.Add(topBar);

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.Dock = DockStyle.Fill;
        _tabs.Font = Theme.Body;
        contentHost.Controls.Add(_tabs);

        _dashboardPage = BuildDashboardPage();
        _devicePage = new DeviceStatusPanel(_cfg) { Dock = DockStyle.Fill };
        _maintPage = new MaintenancePanel(_engine) { Dock = DockStyle.Fill };
        _failPage = new FailListPanel(_engine) { Dock = DockStyle.Fill };
        _debugPage = new DebugPanel(_engine) { Dock = DockStyle.Fill };

        Control[] pages = { _dashboardPage, _maintPage, _devicePage, _failPage, _debugPage };
        for (int i = 0; i < pages.Length; i++)
        {
            var tp = new TabPage(PageTitles[i]) { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            pages[i].Dock = DockStyle.Fill;
            tp.Controls.Add(pages[i]);
            Theme.Apply(pages[i]);
            _tabs.TabPages.Add(tp);
        }
        for (int i = 0; i < Tools.Length; i++)
        {
            var t = Tools[i];
            var host = new ToolHost(t.label, t.factory) { Dock = DockStyle.Fill };
            _toolPages[i] = host;
            var tp = new TabPage(t.label) { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            tp.Controls.Add(host);
            _tabs.TabPages.Add(tp);
        }
        _tabs.SelectedIndex = 0;
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_page >= OwnPageCount)
            {
                var oldHost = _toolPages[_page - OwnPageCount];
                oldHost.DeactivateTool();
            }
            var idx = _tabs.SelectedIndex;
            _page = idx;
            _pageTitle.Text = PageTitles[Math.Min(idx, PageTitles.Length - 1)];
            if (idx >= OwnPageCount)
            {
                var host = _toolPages[idx - OwnPageCount];
                host.Ensure();
                host.ActivateTool();
            }
            RefreshCurrentPage();
        };

        KeyDown += OnShortcut;
    }

    private Panel BuildDashboardPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.Gap, Theme.Gap, Theme.Gap, 0) };

        var kpiRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 92, ColumnCount = 5, RowCount = 1, BackColor = Theme.Bg,
        };
        for (int i = 0; i < 5; i++) kpiRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        void AddKpi(string key, string title, Color accent, int col)
        {
            var c = new KpiCard(title, accent) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.Gap, 0) };
            _kpi[key] = c;
            kpiRow.Controls.Add(c, col, 0);
        }
        AddKpi("today_product", "今日产品数（SN 去重）", Theme.Primary, 0);
        AddKpi("today_pass", "今日 PASS", Theme.Success, 1);
        AddKpi("today_fail", "今日 FAIL", Theme.Danger, 2);
        AddKpi("today_yield", "今日良率", Theme.Info, 3);
        AddKpi("today_interrupted", "今日中断", Theme.Warning, 4);
        _kpi["today_interrupted"].Margin = new Padding(0);

        var lower = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, Theme.Gap, 0, Theme.Gap) };

        var mainGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Theme.Bg,
        };
        mainGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55f));
        mainGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45f));
        mainGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 52f));
        mainGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 48f));

        _hourlyChart = new HourlyTrendChart { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.Gap, Theme.Gap) };
        _todayGauge = new TodayGaugePanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, Theme.Gap) };
        _topFailRank = new TopFailRankPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.Gap, 0) };
        _liveAlert = new LiveAlertPanel { Dock = DockStyle.Fill, Margin = new Padding(0) };

        _liveAlert.AlertClicked += alert =>
        {
            ShowPage(3);
        };

        mainGrid.Controls.Add(_hourlyChart, 0, 0);
        mainGrid.Controls.Add(_todayGauge, 1, 0);
        mainGrid.Controls.Add(_topFailRank, 0, 1);
        mainGrid.Controls.Add(_liveAlert, 1, 1);

        _detailPanel = new SectionPanel("生产大屏监控") { Dock = DockStyle.Fill };
        _detailPanel.Content.Controls.Add(mainGrid);
        lower.Controls.Add(_detailPanel);

        page.Controls.Add(lower);
        page.Controls.Add(kpiRow);
        lower.BringToFront();
        return page;
    }

    private void ShowPage(int index)
    {
        if (index < 0 || index >= PageTitles.Length) return;
        CardPreviewForm.CloseCurrent();
        if (_page >= OwnPageCount)
        {
            _toolPages[_page - OwnPageCount].DeactivateTool();
        }
        if (_tabs.SelectedIndex != index) _tabs.SelectedIndex = index;
        else
        {
            _page = index;
            _pageTitle.Text = PageTitles[Math.Min(index, PageTitles.Length - 1)];
            if (index >= OwnPageCount) _toolPages[index - OwnPageCount].Ensure();
            RefreshCurrentPage();
        }
    }

    private void RefreshCurrentPage()
    {
        try
        {
            switch (_page)
            {
                case 0:
                    Tick();
                    UpdateDashboardWidgets();
                    break;
                case 1: _maintPage.Refresh2(); break;
                case 2: _devicePage.Refresh2(); break;
                case 3: _failPage.Refresh2(); break;
                default: break;
            }
        }
        catch (Exception ex) { Logger.Warning($"页面刷新失败: {ex.Message}"); }
    }

    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (!e.Control) return;
        int idx = e.KeyCode switch
        {
            Keys.D1 => 0, Keys.D2 => 1, Keys.D3 => 2,
            Keys.D4 => 3, Keys.D5 => DebugPageIndex,
            _ => -1,
        };
        if (idx >= 0) { ShowPage(idx); e.Handled = true; return; }
        int tool = e.KeyCode switch
        {
            Keys.D6 => 0, Keys.D7 => 1,
            _ => -1,
        };
        if (tool >= 0)
        {
            ShowPage(OwnPageCount + tool);
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.R) { RefreshCurrentPage(); e.Handled = true; }
    }

    private void OnNotificationClicked()
    {
        try
        {
            if (InvokeRequired) { BeginInvoke(OnNotificationClicked); return; }
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
            BringToFront();
            ShowPage(1);
        }
        catch (Exception ex) { Logger.Warning($"提示点击处理失败: {ex.Message}"); }
    }

    private int _tickCount = 0;
    private int _pendingTodo = 0;
    private int _openMaint = 0;
    private string _todoTop = "—";

    private void Tick()
    {
        _tickCount++;
        UpdateChips();
        UpdateProgress();
        UpdateKpi();
        if (_page == 0 && (_tickCount % 3 == 1)) UpdateDashboardWidgets();
        if (_tickCount % 10 == 1) UpdateTodoBadge();
        UpdateStatusBar();
        if (_tickCount % 5 == 1) UpdateAggLinkUi();
        AutoRefreshMaintenance();
    }

    private void UpdateAggLinkUi()
    {
        try
        {
            var cfg = AppConfig.Instance;
            var mesh = _engine.Mesh;
            var links = mesh?.PeerLinks ?? Array.Empty<PeerLink>();
            if (mesh == null || links.Length == 0)
            {
                _lblAggLink.Text = "Mesh：单节点";
                _lblAggLink.ForeColor = Theme.TextFaint;
                _aggLinkTip.SetToolTip(_lblAggLink, "未配置 peers，本机以单节点模式运行（可在 config.json 的 peers 添加邻居）");
                return;
            }
            var lines = links.Select(l =>
                $"{l.Url}: {(l.State switch {
                    AggLinkState.Disconnected => "断连",
                    AggLinkState.Degraded => "积压",
                    AggLinkState.Connected => "正常",
                    _ => "等待" })}{(l.ConsecutiveFailures > 0 ? $"(失败{l.ConsecutiveFailures})" : "")}");
            var tip = "P2P 邻居链路:\n" + string.Join("\n", lines) + "\n上次成功: "
                    + links.Where(l => l.LastSuccessAt != default).Select(l => l.LastSuccessAt.ToString("HH:mm:ss")).DefaultIfEmpty("从未").First();
            var disconnected = links.Any(l => l.State == AggLinkState.Disconnected);
            var degraded = links.Any(l => l.State == AggLinkState.Degraded);
            _lblAggLink.Text = disconnected ? "● 邻居断连" : degraded ? "● 邻居积压" : "● 互联正常";
            _lblAggLink.ForeColor = disconnected ? Theme.Danger : degraded ? Theme.Warning : Theme.Success;
            _aggLinkTip.SetToolTip(_lblAggLink, tip);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[聚合推送] 状态灯刷新失败: {ex.Message}");
        }
    }

    private void UpdateChips()
    {
        var s = AppState.Snapshot();
        var chips = new List<(string, Color)>
        {
            ($"机台 {(string.IsNullOrEmpty(s.StationId) ? "自动识别" : s.StationId)}", Theme.Primary),
            (s.Status switch
                {
                    "running" => "采集运行中",
                    "error" => "结果目录异常",
                    _ => "空闲",
                },
             s.Status == "running" ? Theme.Success : s.Status == "error" ? Theme.Danger : Theme.Warning),
            ($"型号 {s.ModelsCount}", Theme.Neutral),
            (s.HistoricalScanComplete ? "历史扫描完成" : "历史扫描中", s.HistoricalScanComplete ? Theme.Success : Theme.Warning),
            ($"飞书 {(s.WebhookConfigured ? "已配置" : "未配置")}", s.WebhookConfigured ? Theme.Success : Theme.Neutral),
            ($"桌面提示 {(DesktopNotifier.Enabled ? "开" : "关")}", DesktopNotifier.Enabled ? Theme.Success : Theme.Neutral),
        };
        _chips.SetChips(chips);
    }

    private void UpdateProgress()
    {
        var (phase, total, parsed) = AppState.GetScanProgress();
        switch (phase)
        {
            case "scanning":
                _progressPanel.Visible = true;
                _progressBar.Style = ProgressBarStyle.Marquee;
                _progressLabel.Text = $"扫描中… 已发现 {total} 个文件";
                break;
            case "parsing":
                _progressPanel.Visible = true;
                _progressBar.Style = ProgressBarStyle.Continuous;
                _progressBar.Maximum = Math.Max(total, 1);
                _progressBar.Value = Math.Min(parsed, _progressBar.Maximum);
                var pct = total > 0 ? parsed * 100.0 / total : 0;
                _progressLabel.Text = $"解析中… {parsed}/{total}（{pct:F1}%）";
                break;
            default:
                _progressPanel.Visible = false;
                break;
        }
    }

    private void UpdateKpi()
    {
        var s = AppState.Snapshot();
        void SetKpi(string k, string v, string sub = "")
        {
            if (_kpi.TryGetValue(k, out var c)) c.Set(v, sub);
        }

        SetKpi("today_product", s.TodayProductCount.ToString("N0"), $"累计 {s.ProductCount:N0}");
        SetKpi("today_pass", s.TodayPass.ToString("N0"));
        SetKpi("today_fail", s.TodayFail.ToString("N0"));
        SetKpi("today_yield", $"{s.TodayYield:F1}%");
        SetKpi("today_interrupted", s.TodayInterrupted.ToString("N0"));

        _todayGauge?.SetData(s.MonthPass, s.MonthFail, s.MonthInterrupted);
    }

    private void UpdateDashboardWidgets()
    {
        try
        {
            if (_engine?.Db == null) return;
            string todayYmd = DateTime.Now.ToString("yyyyMMdd");
            var hourly = _engine.Db.FetchDailyHourlyStats(string.Empty, todayYmd);
            _hourlyChart?.SetData(hourly);

            var topFails = _engine.Db.FetchDailyTopFails(string.Empty, todayYmd, 5);
            _topFailRank?.SetData(topFails);

            var alerts = _engine.Db.FetchRecentFailAlerts(string.Empty, 10);
            _liveAlert?.SetData(alerts);
        }
        catch (Exception ex)
        {
            Logger.Warning($"主页大屏组件更新失败: {ex.Message}");
        }
    }

    private void UpdateTodoBadge()
    {
        try
        {
            _pendingTodo = _engine.Db.CountPendingTodos();
            var counts = _engine.Db.CountMaintenanceByStatus();
            _openMaint = counts.Where(kv => MaintenanceMeta.Normalize(kv.Key) != MaintenanceMeta.DoneStatus)
                               .Sum(kv => kv.Value);
            var top = _engine.Db.ListTodoView(null, null, 1).FirstOrDefault();
            _todoTop = top == null ? "—" : $"{top.SortCount} 次";
            if (_tabs.TabPages.Count > 1)
                _tabs.TabPages[1].Text = _pendingTodo > 0 ? $"待办 / 维修 ({_pendingTodo})" : PageTitles[1];
        }
        catch (Exception ex) { Logger.Warning($"待办角标刷新失败: {ex.Message}"); }
    }

    private void UpdateStatusBar()
    {
        var s = AppState.Snapshot();
        var db = Path.Combine("data", $"{(string.IsNullOrEmpty(s.StationId) ? "fct" : s.StationId)}.db");
        var left = $"结果目录: {_cfg.ResultsRoot}     库: {db}     待办 {_pendingTodo} 条     未完成维修 {_openMaint} 条";
        if (s.Status == "error") left = "⚠ 结果目录不存在，数据采集未运行！  " + left;
        if (_statusLeft.Text != left) _statusLeft.Text = left;
        var right = $"更新于 {DateTime.Now:HH:mm}     Ctrl+1~5 切页 / 6~9 工具 / R 刷新";
        if (_statusRight.Text != right) _statusRight.Text = right;
    }

    private int _lastFailSeen = -1;
    private DateTime _lastMaintAutoRefresh = DateTime.MinValue;

    private void AutoRefreshMaintenance()
    {
        var fail = AppState.Snapshot().Fail;
        if (_lastFailSeen < 0) { _lastFailSeen = fail; return; }
        var stale = (DateTime.Now - _lastMaintAutoRefresh).TotalSeconds > 15;
        if (fail == _lastFailSeen && !stale) return;
        _lastFailSeen = fail;
        if (!_maintPage.Visible) return;
        if ((DateTime.Now - _lastMaintAutoRefresh).TotalSeconds < 3) return;
        _lastMaintAutoRefresh = DateTime.Now;
        try { _maintPage.Refresh2(); }
        catch (Exception ex) { Logger.Warning($"待办看板自动刷新失败: {ex.Message}"); }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        BeginInvoke(() =>
        {
            try
            {
                if (_cfg.AutoUpdate) AutoUpdateCheck();
                else UpdatePromptForm.ShowIfAvailable(_engine.Db);
            }
            catch (Exception ex)
            {
                Logger.Warning($"[更新器] 检测提示失败: {ex.Message}");
            }
            if (_cfg.AutoUpdate)
            {
                _autoUpdateTimer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
                _autoUpdateTimer.Tick += (_, _) => { try { AutoUpdateCheck(); } catch (Exception ex) { Logger.Warning($"[更新器] 周期检测失败: {ex.Message}"); } };
                _autoUpdateTimer.Start();
            }
        });
    }

    private void AutoUpdateCheck()
    {
        var info = UpdateChecker.Scan(db: _engine.Db);
        if (info == null) return;
        Logger.Info($"[更新器] 无感热升级：发现新包 v{info.Version}（{Path.GetFileName(info.ZipPath)}），自动暂存中…");
        UpdateChecker.StageUpdate(info, _engine.Db);
        UpdateChecker.MarkPrompted(info.Version, _engine.Db);
        DesktopNotifier.NotifyRaw($"发现新版本 v{info.Version}：Argus 将在几秒后自动重启完成升级，数据与配置不受影响。");
        UpdateChecker.ScheduleRestart(delaySeconds: 3);
        var t = new System.Windows.Forms.Timer { Interval = 1500 };
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); Close(); };
        t.Start();
    }

    private void ShowAggSettingsDialog()
    {
        var cfg = AppConfig.Instance;
        using var dlg = new Form
        {
            Text = "聚合设置",
            Size = new Size(560, 420),
            MinimumSize = new Size(560, 420),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Theme.Bg,
            Font = Theme.Body,
        };

        var stateLabel = new Label
        {
            Text = cfg.AggEnabled
                ? $"当前: 已开启聚合推送（{cfg.AggTransport} → {UrlOrShare()}）"
                : "当前: 未开启聚合推送",
            Location = new Point(24, 16), AutoSize = true, ForeColor = Theme.TextSub, Font = Theme.Small,
        };
        dlg.Controls.Add(stateLabel);

        var linkStateLabel = new Label
        {
            Text = BuildLinkStateText(),
            Location = new Point(24, 34), AutoSize = true, ForeColor = Theme.TextFaint, Font = Theme.Small,
        };
        dlg.Controls.Add(linkStateLabel);

        var secLabel = new Label
        {
            Text = "① 连接聚合端（本机作为机台推送数据）", Location = new Point(24, 52),
            AutoSize = true, ForeColor = Theme.TextSub, Font = Theme.Body,
        };
        dlg.Controls.Add(secLabel);

        var lblIp = new Label { Text = "聚合端 IP", Location = new Point(24, 86), AutoSize = true, ForeColor = Theme.TextSub };
        var txtIp = new TextBox { Location = new Point(150, 82), Width = 200, Text = CurrentIpOf(cfg) };
        var lblPort = new Label { Text = "端口", Location = new Point(362, 86), AutoSize = true, ForeColor = Theme.TextSub };
        var txtPort = new TextBox { Location = new Point(410, 82), Width = 80, Text = cfg.MeshPort.ToString() };
        var btnConnect = new Button { Text = "连接", Location = new Point(150, 116), Size = new Size(110, 30) };
        var lblConnMsg = new Label { Text = "", Location = new Point(270, 121), AutoSize = true, ForeColor = Theme.Success, Font = Theme.Small };
        dlg.Controls.Add(lblIp); dlg.Controls.Add(txtIp);
        dlg.Controls.Add(lblPort); dlg.Controls.Add(txtPort);
        dlg.Controls.Add(btnConnect); dlg.Controls.Add(lblConnMsg);

        btnConnect.Click += (_, _) =>
        {
            var ip = txtIp.Text.Trim();
            if (ip.Length == 0 || !int.TryParse(txtPort.Text.Trim(), out var port) || port < 1 || port > 65535)
            {
                lblConnMsg.ForeColor = Theme.Danger;
                lblConnMsg.Text = "IP 或端口无效";
                return;
            }
            cfg.AggEnabled = true;
            cfg.AggTransport = "http";
            cfg.AggHttpUrl = $"http://{ip}:{port}/";
            if (!cfg.Save())
            {
                lblConnMsg.ForeColor = Theme.Danger;
                lblConnMsg.Text = "配置保存失败（看日志）";
                return;
            }
            _engine.RestartPusher();
            lblConnMsg.ForeColor = Theme.Success;
            lblConnMsg.Text = $"已连接 {cfg.AggHttpUrl}，推送器已重启";
            stateLabel.Text = $"当前: 已开启聚合推送（http → {cfg.AggHttpUrl}）";
        };

        var sec2 = new Label
        {
            Text = "② 本机一键成为聚合端（自动配置环境 + 启动聚合服务）", Location = new Point(24, 168),
            AutoSize = true, ForeColor = Theme.TextSub, Font = Theme.Body,
        };
        dlg.Controls.Add(sec2);

        var btnBecome = new Button { Text = "一键部署聚合服务", Location = new Point(24, 200), Size = new Size(180, 34) };
        var lblBecomeMsg = new Label
        {
            Text = "会弹 UAC 确认（防火墙放行需要管理员权限）。完成后浏览器访问 http://本机IP:8081/ 即可查看看板。",
            Location = new Point(24, 244), Size = new Size(500, 40),
            ForeColor = Theme.TextFaint, Font = Theme.Small,
        };
        dlg.Controls.Add(btnBecome); dlg.Controls.Add(lblBecomeMsg);

        btnBecome.Click += (_, _) =>
        {
            if (!AggDeployer.IsAdmin())
            {
                var ask = MessageBox.Show(
                    "一键成为聚合端需要管理员权限（防火墙放行）。\n是否以管理员身份重新启动部署？",
                    "聚合服务一键部署", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (ask != DialogResult.Yes) return;
                if (AggDeployer.RelaunchAsAdmin())
                {
                    lblBecomeMsg.ForeColor = Theme.Success;
                    lblBecomeMsg.Text = "已以管理员身份启动部署窗口（UAC 点「是」），完成后本机即聚合端。";
                }
                else
                {
                    lblBecomeMsg.ForeColor = Theme.Danger;
                    lblBecomeMsg.Text = "提权启动失败（UAC 被取消？），请右键 Argus.exe → 以管理员身份运行后重试。";
                }
                return;
            }

            lblBecomeMsg.ForeColor = Theme.TextSub;
            lblBecomeMsg.Text = "部署中，请稍候...";
            Cursor = Cursors.WaitCursor;
            try
            {
                var r = AggDeployer.Deploy();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"步骤 1: config.json 就绪（端口 {r.Port}）");
                sb.AppendLine(r.FirewallOk
                    ? "步骤 2: 防火墙放行 ✓"
                    : $"步骤 2: 防火墙放行失败（{r.FirewallMsg}）");
                sb.AppendLine(r.AutoStartOk
                    ? "步骤 3: 开机自启 ✓"
                    : $"步骤 3: 开机自启失败（{r.AutoStartMsg}）");
                sb.AppendLine(r.ServiceOk
                    ? "步骤 4: 聚合服务已启动 ✓"
                    : $"步骤 4: 服务启动失败（{r.ServiceMsg}）");
                if (r.Addresses.Count > 0)
                {
                    sb.AppendLine("步骤 5: 浏览器访问地址");
                    foreach (var ip in r.Addresses)
                        sb.AppendLine($"  http://{ip}:{r.Port}/{(r.NewToken.Length > 0 ? $"?token={r.NewToken}" : "")}");
                }
                else
                    sb.AppendLine($"步骤 5: 浏览器访问 http://本机IP:{r.Port}/" +
                        (r.NewToken.Length > 0 ? $"?token={r.NewToken}" : ""));
                if (r.NewToken.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"访问令牌(agg_token)：{r.NewToken}");
                    sb.AppendLine("（首次用上面带 ?token= 的地址打开即可；机台端推送需配同一串 token）");
                }
                sb.AppendLine();
                sb.AppendLine("机台端在「聚合设置」里填本机 IP 即可连接。");
                MessageBox.Show(sb.ToString(), "聚合服务一键部署",
                    MessageBoxButtons.OK, r.FullSuccess ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                lblBecomeMsg.ForeColor = Theme.Success;
                lblBecomeMsg.Text = r.FullSuccess
                    ? "部署完成，本机已是聚合端（浏览器访问上面地址查看看板）。"
                    : "部署部分完成（见弹窗），未成功的步骤请看提示。";
            }
            catch (Exception ex)
            {
                lblBecomeMsg.ForeColor = Theme.Danger;
                lblBecomeMsg.Text = $"部署失败: {ex.Message}";
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        };

        var btnDisable = new Button
        {
            Text = "断开聚合推送", Location = new Point(24, 310), Size = new Size(140, 30),
            ForeColor = Theme.Danger,
        };
        var lblDisableMsg = new Label { Text = "", Location = new Point(180, 315), AutoSize = true, ForeColor = Theme.TextSub, Font = Theme.Small };
        dlg.Controls.Add(btnDisable); dlg.Controls.Add(lblDisableMsg);
        btnDisable.Click += (_, _) =>
        {
            cfg.AggEnabled = false;
            cfg.Save();
            _engine.RestartPusher();
            lblDisableMsg.Text = "已断开，推送器已停止";
            stateLabel.Text = "当前: 未开启聚合推送";
        };

        Theme.Apply(dlg);
        dlg.ShowDialog(this);
    }

    private string UrlOrShare()
    {
        var cfg = AppConfig.Instance;
        return cfg.AggTransport.Contains("http", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(cfg.AggHttpUrl)
            ? cfg.AggHttpUrl
            : cfg.AggShareRoot;
    }

    private string BuildLinkStateText()
    {
        var cfg = AppConfig.Instance;
        var mesh = _engine.Mesh;
        var links = mesh?.PeerLinks ?? Array.Empty<PeerLink>();
        if (links.Length == 0) return "链路状态: 未配置 peers（单节点模式）";
        try
        {
            var summaries = links.Select(l => $"{l.Url}:{(l.State switch
            {
                AggLinkState.Disconnected => "断连",
                AggLinkState.Degraded => "积压",
                AggLinkState.Connected => "正常",
                _ => "等待",
            })}").ToList();
            return "链路状态: " + string.Join(" ", summaries);
        }
        catch (Exception ex)
        {
            return $"链路状态: 读取失败（{ex.Message}）";
        }
    }

    private static string CurrentIpOf(AppConfig cfg)
    {
        if (cfg.AggHttpUrl.Length == 0) return "";
        try
        {
            var u = new Uri(cfg.AggHttpUrl);
            return u.Host;
        }
        catch { return ""; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            var confirm = MessageBox.Show("确定要退出吗？退出后将停止数据采集。", "确认退出",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) { e.Cancel = true; return; }
        }
        DesktopNotifier.Shutdown();
        base.OnFormClosing(e);
    }
}

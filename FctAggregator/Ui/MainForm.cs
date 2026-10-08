using System.Text;

namespace FctAggregator;

public class MainForm : AppForm
{
    private static readonly string AppVersion = typeof(MainForm).Assembly.GetName().Version is { } v
        ? $"{v.Major}.{v.Minor}.{v.Build}"
        : "0.0.0";

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
    private ToolTip? _statusTip;

    private readonly Dictionary<string, KpiCard> _kpi = new();
    private HourlyTrendChart _hourlyChart = null!;
    private DeviceOnlinePanel _deviceOnline = null!;
    private TopFailRankPanel _topFailRank = null!;
    private LiveAlertPanel _liveAlert = null!;
    private LearnAlertStrip _learnAlertStrip = null!;
    private Label _taktStrip = null!;

    private Panel _dashboardPage = null!;
    private DeviceStatusPanel _devicePage = null!;
    private MaintenancePanel _maintPage = null!;
    private FailListPanel _failPage = null!;
    private BackupCodePanel _backupPage = null!;
    private DebugPanel _debugPage = null!;
    private System.Windows.Forms.Timer _timer = null!;
    private System.Windows.Forms.Timer? _autoUpdateTimer;

    private static readonly string[] PageTitles =
    {
        "总览", "待办 / 维修记录", "设备状态", "FAIL 记录", "备份", "调试工具",
        "TDMS 波形",
    };

    private const int OwnPageCount = 6;

    private static readonly (string key, string label, Func<Form> factory)[] Tools =
    {
        ("tdms",       "TDMS 波形", () => new FctTdmsViewer.MainForm()),
    };

    private readonly ToolHost[] _toolPages = new ToolHost[Tools.Length];

    private int _page = 0;

    /// <summary>当前页索引。UI 异常现场诊断用（见 UiScreenFit.DescribeActiveForm）。</summary>
    public int CurrentPageIndex => _page;

    private const int BackupPageIndex = 4;
    private const int DebugPageIndex = 5;

    public MainForm(Engine engine, bool debugMode = false)
    {
        _engine = engine;
        _cfg = AppConfig.Instance;
        Text = $"Argus v{AppVersion}";
        // UI-W10：AutoScaleMode.Dpi（见 Theme.ApplyDpi）会在 OnLayout 的 PerformAutoScale 里把下面这几个
        // 尺寸再乘一次缩放系数，所以工作区（**设备像素**）必须先折算回逻辑像素，否则非 100% 缩放下
        // 窗口与最小尺寸都会超出屏幕：1024×768 机台在 150% 下 MinimumSize 被撑到 1536×1080，工作区只有
        // 1024×728 —— 正好复发「右侧控件看不到」（更新日志 v3.35.x 修过一次，但当时只覆盖 100% 缩放）。
        var uiScale = UiScreenFit.AutoScaleFactor(this);
        var waPx = Screen.PrimaryScreen!.WorkingArea.Size;
        var wa = UiScreenFit.LogicalWorkArea(waPx, uiScale);
        Width = Math.Max(640, Math.Min(1408, wa.Width));
        Height = Math.Max(480, Math.Min(768, wa.Height));
        // 低分屏（1024x768 机台）下 MinimumSize 若大于工作区，会把窗口强制撑出屏幕，右侧控件看不到
        MinimumSize = new Size(Math.Min(1200, wa.Width), Math.Min(720, wa.Height));
        // 现场排查：把这几个数写进日志，Win10/Win11 的缩放差异一眼可见（见 UiScreenFit.Describe）
        Logger.Info(UiScreenFit.Describe(waPx, uiScale, new Size(Width, Height), MinimumSize, DeviceDpi));
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
        DesktopNotifier.RestoreRequested += RestoreFromTray;
        DesktopNotifier.ExitRequested += RequestExit;
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
            AutoEllipsis = true,
        };
        _statusRight = new Label
        {
            Dock = DockStyle.Right, Width = 280, ForeColor = Theme.TextFaint, Font = Theme.Small,
            BackColor = Theme.Surface, Padding = new Padding(0, 0, 14, 0), TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true,
        };
        // 状态栏原来左右宽度写死 760+280：1024 宽屏上两段加起来超过屏宽，右侧（时间/版本）被推出可视区。
        // 改为按实际宽度分配，并把完整文本挂到 ToolTip（截断后仍可读全）。
        _statusTip ??= new ToolTip { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 200 };
        foreach (var lbl in new[] { _statusLeft, _statusRight })
        {
            var l = lbl;
            l.TextChanged += (_, _) => _statusTip.SetToolTip(l, l.Text);
            _statusTip.SetToolTip(l, "");
        }
        statusBar.Resize += (_, _) =>
        {
            int total = statusBar.ClientSize.Width;
            int right = Math.Clamp(total / 4, 140, 280);
            _statusRight.Width = right;
            _statusLeft.Width = Math.Max(160, total - right);
        };
        statusBar.Controls.Add(_statusRight);
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
            Dock = DockStyle.Left, Width = 280, Text = PageTitles[0],
            Font = Theme.PageTitle, ForeColor = Theme.TextMain, BackColor = Theme.Surface,
            Padding = new Padding(18, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft,
        };
        _chips = new ChipBar { Dock = DockStyle.Fill };
        var btnRefresh = Theme.MakeButton("刷新", 74);
        btnRefresh.Dock = DockStyle.Fill;
        btnRefresh.Margin = new Padding(0);
        btnRefresh.Click += (_, _) => RefreshCurrentPage(force: true);
        var refreshHost = new Panel
        {
            Dock = DockStyle.Right, Width = 94, BackColor = Theme.Surface, Padding = new Padding(9, 2, 9, 2),
        };
        refreshHost.Controls.Add(btnRefresh);
        topBar.Controls.Add(_chips);
        topBar.Controls.Add(refreshHost);
        topBar.Controls.Add(_pageTitle);

        Controls.Add(topBar);

        _tabs = new BufferedTabControl { Dock = DockStyle.Fill };
        _tabs.Dock = DockStyle.Fill;
        _tabs.Font = Theme.Body;
        contentHost.Controls.Add(_tabs);

        _dashboardPage = BuildDashboardPage();
        _devicePage = new DeviceStatusPanel(_cfg) { Dock = DockStyle.Fill };
        _maintPage = new MaintenancePanel(_engine) { Dock = DockStyle.Fill };
        _failPage = new FailListPanel(_engine) { Dock = DockStyle.Fill };
        _backupPage = new BackupCodePanel(_engine) { Dock = DockStyle.Fill };
        _debugPage = new DebugPanel(_engine) { Dock = DockStyle.Fill };

        Control[] pages = { _dashboardPage, _maintPage, _devicePage, _failPage, _backupPage, _debugPage };
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
            // 切页立刻返回；刷新下一拍再跑，避免 SelectedIndexChanged 里同步打库/扫盘卡住 UI
            BeginInvoke(() => RefreshCurrentPage());
        };

        KeyDown += OnShortcut;
    }

    private Panel BuildDashboardPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.Gap) };

        var kpiRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 92, ColumnCount = 5, RowCount = 1, BackColor = Theme.Bg,
            Margin = new Padding(0, 0, 0, Theme.Gap),
        };
        for (int i = 0; i < 5; i++) kpiRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        void AddKpi(string key, string title, Color accent, int col)
        {
            var c = new KpiCard(title, accent) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.Gap, 0) };
            _kpi[key] = c;
            kpiRow.Controls.Add(c, col, 0);
        }
        AddKpi("today_product", "今日总测试", Theme.Primary, 0);
        AddKpi("today_pass", "今日 PASS", Theme.Success, 1);
        AddKpi("today_fail", "今日 FAIL", Theme.Danger, 2);
        AddKpi("today_yield", "今日良率", Theme.Info, 3);
        AddKpi("today_interrupted", "今日中断", Theme.Warning, 4);
        _kpi["today_interrupted"].Margin = new Padding(0);

        var lower = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, Theme.Gap, 0, 0) };

        var mainGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Theme.Bg,
        };
        mainGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54f));
        mainGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46f));
        mainGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 52f));
        mainGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 48f));

        _hourlyChart = new HourlyTrendChart { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.Gap, Theme.Gap) };
        _deviceOnline = new DeviceOnlinePanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 0) };
        _topFailRank = new TopFailRankPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.Gap, 0) };
        _liveAlert = new LiveAlertPanel { Dock = DockStyle.Fill, Margin = new Padding(0) };

        _liveAlert.AlertClicked += alert =>
        {
            ShowPage(3);
        };
        _deviceOnline.OpenDetails += () => ShowPage(2);

        var bottomLeft = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        bottomLeft.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        bottomLeft.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        bottomLeft.Controls.Add(_topFailRank, 0, 0);
        bottomLeft.Controls.Add(_liveAlert, 1, 0);

        mainGrid.Controls.Add(_hourlyChart, 0, 0);
        mainGrid.Controls.Add(bottomLeft, 0, 1);
        mainGrid.Controls.Add(_deviceOnline, 1, 0);
        mainGrid.SetRowSpan(_deviceOnline, 2);

        var dashPanel = new SectionPanel("生产大屏监控") { Dock = DockStyle.Fill };
        dashPanel.Content.Controls.Add(mainGrid);
        lower.Controls.Add(dashPanel);

        _learnAlertStrip = new LearnAlertStrip { Dock = DockStyle.Top };
        _taktStrip = new Label
        {
            Dock = DockStyle.Top, Height = 28, Text = AppState.TodayTaktLine,
            Font = Theme.BodyBold, ForeColor = Theme.TextMain, BackColor = Theme.Surface,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 8, 0),
            AutoEllipsis = true,
        };

        page.Controls.Add(lower);
        page.Controls.Add(_learnAlertStrip);
        page.Controls.Add(_taktStrip);
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
            BeginInvoke(() => RefreshCurrentPage());
        }
    }

    private void RefreshCurrentPage(bool force = false)
    {
        try
        {
            switch (_page)
            {
                case 0:
                    UpdateKpi();
                    UpdateChips();
                    UpdateDashboardWidgets();
                    if (force) UpdateTodoBadgeAsync();
                    break;
                case 1: _maintPage.Refresh2(); break;
                case 2: _devicePage.Refresh2(); break;
                case 3: _failPage.Refresh2(); break;
                case BackupPageIndex: _backupPage.Refresh2(); break;
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
            Keys.D4 => 3, Keys.D5 => BackupPageIndex, Keys.D6 => DebugPageIndex,
            _ => -1,
        };
        if (idx >= 0) { ShowPage(idx); e.Handled = true; return; }
        int tool = e.KeyCode switch
        {
            Keys.D7 => 0,
            _ => -1,
        };
        if (tool >= 0)
        {
            ShowPage(OwnPageCount + tool);
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.R) { RefreshCurrentPage(force: true); e.Handled = true; }
    }

    private bool _forceExit;

    private void RequestExit()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(RequestExit); return; }
        _forceExit = true;
        Close();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void RestoreFromTray()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(RestoreFromTray); return; }
        ShowInTaskbar = true;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
        BringToFront();
    }

    private void OnNotificationClicked()
    {
        try
        {
            if (InvokeRequired) { BeginInvoke(OnNotificationClicked); return; }
            RestoreFromTray();
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
        if (_page == 0)
        {
            RefreshDeviceOnline();
            UpdateDashboardWidgets();
        }
        if (_tickCount % 10 == 1) UpdateTodoBadgeAsync();
        UpdateStatusBar();
        if (_tickCount % 5 == 1) RefreshLiveStats();
        AutoRefreshMaintenance();
    }

    /// <summary>看板实时性兜底：后台轻量重算数据库统计（不含磁盘扫描），跨日归零与无入库场景下 KPI 也保持与库内一致。</summary>
    private void RefreshLiveStats()
    {
        if (_engine?.Db == null) return;
        var db = _engine.Db;
        var sid = _engine.ResolvedStationId;
        Task.Run(() =>
        {
            try { CycleBackfill.Run(db, sid, DateTime.Now.ToString("yyyyMMdd"), 25); }
            catch (Exception ex) { Logger.Warning($"[节拍] 补读失败: {ex.Message}"); }
            try { AppState.RefreshDbStats(db, sid); }
            catch (Exception ex) { Logger.Warning($"[实时刷新] 统计刷新失败: {ex.Message}"); }
        });
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

        SetKpi("today_product", s.TodayProductCount.ToString("N0"),
            string.IsNullOrEmpty(s.TodayModelLine) ? $"PASS+FAIL · 累计台次 {s.ProductCount:N0}" : s.TodayModelLine);
        SetKpi("today_pass", s.TodayPass.ToString("N0"));
        SetKpi("today_fail", s.TodayFail.ToString("N0"));
        SetKpi("today_yield", s.TodayPass + s.TodayFail == 0 ? "—" : $"{s.TodayYield:F1}%");
        SetKpi("today_interrupted", s.TodayInterrupted.ToString("N0"));
        if (_taktStrip != null && _taktStrip.Text != s.TodayTaktLine)
            _taktStrip.Text = s.TodayTaktLine;
    }

    private volatile bool _deviceOnlineBusy = false;

    private void RefreshDeviceOnline()
    {
        // D1：FctIni.Snapshot 内含注册表+串口枚举，FCT.ini 缺失时每 15s 走 LocateIni——
        // 原实现直接在 1s Tick 的 UI 线程跑，窗口周期性卡死。改后台线程 + UiAsync.Post 回填
        //（同 DeviceStatusPanel.Kick 范式，MainForm 是当年唯一漏改处）。
        if (_deviceOnline == null || _deviceOnlineBusy) return;
        _deviceOnlineBusy = true;
        var cfgPath = _cfg.FctIniPath;
        Task.Run(() =>
        {
            FctIniData? data = null;
            try { data = FctIni.Snapshot(cfgPath); }
            catch (Exception ex) { Logger.Warning($"[总览] 设备在线刷新失败: {ex.Message}"); }
            UiAsync.Post(this, () =>
            {
                _deviceOnlineBusy = false;
                if (data != null && !IsDisposed) _deviceOnline.SetData(data);
            });
        });
    }

    private volatile bool _dashUpdating = false;
    private long _lastDashRun = long.MinValue; // v3.36.0 性能：看板聚合从每秒降为 5s 节流

    private void UpdateDashboardWidgets()
    {
        try
        {
            var s = AppState.Snapshot();
            _hourlyChart?.SetData(s.Hourly);
            _topFailRank?.SetData(s.TopFails);
            _liveAlert?.SetData(s.Alerts);
            MaybeRefreshLearnStrip();
        }
        catch (Exception ex)
        {
            Logger.Warning($"主页大屏组件更新失败: {ex.Message}");
        }
    }

    private void MaybeRefreshLearnStrip()
    {
        if (!_cfg.LearnBaselineEnabled || _engine?.Db == null) return;
        if (_dashUpdating) return;
        if (Environment.TickCount64 - _lastDashRun < 5000) return;
        _dashUpdating = true;
        _lastDashRun = Environment.TickCount64;
        var db = _engine.Db;
        Task.Run(() =>
        {
            var learnLines = new List<string>();
            try
            {
                var bs = LearningEngine.GetBaselineState(db);
                if (bs?.Alerts != null)
                    foreach (var a in bs.Alerts.Take(2))
                        learnLines.Add($"[基线] {a.Message}");
            }
            catch (Exception ex) { Logger.Warning($"[总览] 基线预警查询失败: {ex.Message}"); }
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(() =>
                {
                    try { _learnAlertStrip?.SetAlerts(learnLines); }
                    catch (Exception ex) { Logger.Warning($"主页基线条刷新失败: {ex.Message}"); }
                });
            }
            catch (Exception ex) { Logger.Warning($"主页基线条调度失败: {ex.Message}"); }
            finally { _dashUpdating = false; }
        });
    }

    private volatile bool _todoBadgeBusy;

    private void UpdateTodoBadgeAsync()
    {
        if (_todoBadgeBusy || _engine?.Db == null) return;
        _todoBadgeBusy = true;
        var db = _engine.Db;
        Task.Run(() =>
        {
            try
            {
                int pending = db.CountPendingTodos();
                var counts = db.CountMaintenanceByStatus();
                int open = counts.Where(kv => MaintenanceMeta.Normalize(kv.Key) != MaintenanceMeta.DoneStatus)
                    .Sum(kv => kv.Value);
                var top = db.ListTodoView(null, null, 1).FirstOrDefault();
                string todoTop = top == null ? "—" : $"{top.SortCount} 次";
                // 审计：早退必须先复位 _todoBadgeBusy——构造函数里的首次 Tick 在句柄创建前执行，
                // 原来直接 return 会让标志永久为 true，之后每 10 秒的调用都在入口被拦掉，
                // 标签页「待办 / 维修 (N)」与状态栏计数整场会话停在 0。
                if (!IsHandleCreated || IsDisposed) { _todoBadgeBusy = false; return; }
                BeginInvoke(() =>
                {
                    try
                    {
                        _pendingTodo = pending;
                        _openMaint = open;
                        _todoTop = todoTop;
                        if (_tabs.TabPages.Count > 1)
                            _tabs.TabPages[1].Text = _pendingTodo > 0 ? $"待办 / 维修 ({_pendingTodo})" : PageTitles[1];
                        UpdateStatusBar();
                    }
                    catch (Exception ex) { Logger.Warning($"待办角标 UI 更新失败: {ex.Message}"); }
                    finally { _todoBadgeBusy = false; }
                });
            }
            catch (Exception ex)
            {
                Logger.Warning($"待办角标刷新失败: {ex.Message}");
                _todoBadgeBusy = false;
            }
        });
    }

    private void UpdateStatusBar()
    {
        var s = AppState.Snapshot();
        var db = Path.Combine("data", $"{(string.IsNullOrEmpty(s.StationId) ? "fct" : s.StationId)}.db");
        var left = $"结果目录: {_cfg.ResultsRoot}     库: {db}     待办 {_pendingTodo} 条     未完成维修 {_openMaint} 条";
        if (s.Status == "error") left = "⚠ 结果目录不存在，数据采集未运行！  " + left;
        if (_statusLeft.Text != left) _statusLeft.Text = left;
        var right = $"v{AppVersion}     更新于 {DateTime.Now:HH:mm}     Ctrl+1~6 切页 / 7 TDMS / R 刷新";
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
        // 审计 D3：扫描/解压/整目录复制曾在 UI 线程同步执行——data 大时窗口白屏无响应。全部移入后台线程，
        // UI 线程只做 BeginInvoke 收尾（通知/重启计时器）。
        var db = _engine.Db;
        Task.Run(() =>
        {
            UpdateInfo? info = null;
            try { info = UpdateChecker.Scan(db: db); }
            catch (Exception ex) { Logger.Warning($"[更新器] 扫描失败: {ex.Message}"); return; }
            if (info == null) return;
            Logger.Info($"[更新器] 无感热升级：发现新包 v{info.Version}（{Path.GetFileName(info.ZipPath)}），自动暂存中…");
            try { UpdateChecker.StageUpdate(info, db); }
            catch (Exception ex)
            {
                Logger.Warning($"[更新器] 暂存失败: {ex.Message}");
                return;
            }
            // 审计 C4：由旧进程在退出前自行换装（被本进程加载锁定的文件先改名 *.old 让位再覆盖）。
            // 旧实现委托新进程提交——新进程无法覆盖自己已加载的 Argus.dll，提交必然 IOException 失败。
            bool committed;
            try { committed = UpdateChecker.TryCommitPending(db); }
            catch (Exception ex) { Logger.Warning($"[更新器] 提交异常: {ex.Message}"); committed = false; }
            var ver = info.Version; // 闭包内可空收窄丢失——提前取值
            // 走 UiAsync.Post：它已处理"窗体已销毁/句柄未创建"，原来的裸 BeginInvoke 在关窗瞬间会抛
            UiAsync.Post(this, () =>
            {
                if (!committed)
                {
                    DesktopNotifier.NotifyRaw($"v{ver} 已暂存但安装未完成（详见日志），5 分钟后自动重试。");
                    return; // pending 保留、未标记 prompted → 下一轮检测自动重试
                }
                UpdateChecker.MarkPrompted(ver, db); // 提交成功后才标记，杜绝版本被永久静默
                DesktopNotifier.NotifyRaw($"新版本 v{ver} 已安装，Argus 将在几秒后自动重启。");
                UpdateChecker.ScheduleRestart(delaySeconds: 3);
                var t = new System.Windows.Forms.Timer { Interval = 1500 };
                t.Tick += (_, _) => { t.Stop(); t.Dispose(); RequestExit(); };
                t.Start();
            });
        });
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_DEVICECHANGE = 0x0219;
        if (m.Msg == WM_DEVICECHANGE)
        {
            _devicePage?.NotifyHardwareChange();
            if (IsHandleCreated && !IsDisposed) BeginInvoke(RefreshDeviceOnline);
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_forceExit && e.CloseReason == CloseReason.UserClosing && DesktopNotifier.TrayReady)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        DesktopNotifier.Shutdown();
        base.OnFormClosing(e);
    }
}

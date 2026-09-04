using System.Text;

namespace FctAggregator;

/// <summary>
/// 主窗口（v2.8.0 GUI 重构）。
///
/// 布局从「底部一排按钮 + 一行长文字 header」改成常见的后台面板结构：
/// <code>
/// ┌──────────┬───────────────────────────────────────────┐
/// │ 侧边导航 │ 顶栏：页面标题 + 状态胶囊 + 刷新           │
/// │ (168px)  ├───────────────────────────────────────────┤
/// │  主页    │ 扫描进度条（仅扫描时出现）                 │
/// │  待办/维修├──────────────────────────────────────────┤
/// │  FAIL记录│                                           │
/// │  设备状态│            内容区（各页面）                │
/// │  调试    │                                           │
/// │          ├───────────────────────────────────────────┤
/// │  退出    │ 状态栏：机台 · 型号 · 库 · 待办 · 更新时间 │
/// └──────────┴───────────────────────────────────────────┘
/// </code>
/// 要点：
///   · 导航项自绘（<see cref="NavButton"/>），带**待办角标**，红点直接提示有几条未确认不良；
///   · 顶栏状态改成胶囊（<see cref="ChipBar"/>），比一行长文字好扫；
///   · 主页顶部一排 KPI 卡（<see cref="KpiCard"/>），良率/不良/待办一眼可见，明细收进小卡；
///   · 视觉规范集中在 <see cref="Theme"/>，不再各写各的颜色字号；
///   · Ctrl+1..5 切页。
/// </summary>
public class MainForm : Form
{
    private readonly Engine _engine;
    private readonly AppConfig _cfg;

    // ===== 外壳 =====
    // v3.6.0：去掉侧栏导航与自绘标题栏，改顶部 TabControl 经典布局。
    private TabControl _tabs = null!;
    private Label _pageTitle = null!;
    private ChipBar _chips = null!;
    private Panel _progressPanel = null!;
    private Label _progressLabel = null!;
    private ProgressBar _progressBar = null!;
    private Label _statusLeft = null!;
    private Label _statusRight = null!;
    private Label _lblAggLink = null!;      // 状态栏聚合链路状态灯（v3.4.11 连通性加固）
    private ToolTip _aggLinkTip = null!;
    private System.Windows.Forms.Timer _aggLinkTimer = null!;   // Mesh 节点链路状态定时刷新（v3.5.0）

    // ===== 主页 =====
    private readonly Dictionary<string, KpiCard> _kpi = new();
    private readonly Dictionary<string, Label> _statValues = new();
    private SectionPanel _detailPanel = null!;
    private HourlyTrendChart _hourlyChart = null!;
    private TodayGaugePanel _todayGauge = null!;
    private TopFailRankPanel _topFailRank = null!;
    private LiveAlertPanel _liveAlert = null!;

    // ===== 页面 =====
    private Panel _dashboardPage = null!;
    private DeviceStatusPanel _devicePage = null!;
    private MaintenancePanel _maintPage = null!;
    // v3.22.1：FAIL 记录页回归 —— v3.18.5 结构合并时从主窗口 Tab 里被摘掉（FailListPanel 类一直在，
    // 自检也一直在测），产线用户升级后找不到"扫描到的所有 FAIL"入口。恢复为主程序第 4 页。
    private FailListPanel _failPage = null!;
    private DebugPanel _debugPage = null!;
    private System.Windows.Forms.Timer _timer = null!;
    private System.Windows.Forms.Timer? _autoUpdateTimer;   // v3.26.0 无感热升级 5 分钟周期检测

    /// <summary>页索引 → 标题（顶栏显示）。0~4 主程序页，5~6 内嵌的工具页。</summary>
    private static readonly string[] PageTitles =
    {
        "总览", "待办 / 维修记录", "设备状态", "FAIL 记录", "调试工具",
        "TDMS 波形", "聚合看板",
    };

    /// <summary>主程序自己的页面数（后面两个是内嵌工具页）。</summary>
    private const int OwnPageCount = 5;

    /// <summary>
    /// 工具（v2.9.4 起**内嵌**在主窗口里，不再弹独立窗口）。
    /// key 同时是 CLI 子命令名（<c>Argus.exe rank</c> 仍能单独开窗）。
    ///
    /// ⚠ v3.0.0：**数据分发已移出本程序**。它能改文件时间戳、按模板造数、批量投放 XML，
    /// 放在产线机台的主程序里误点一次后果很重。现在是独立程序 <c>FCT-Distributor.exe</c>
    /// （仓库里的 <c>FctDistributor/</c>），只发给工程/测试人员，不进产线包。
    /// </summary>
    private static readonly (string key, string label, Func<Form> factory)[] Tools =
    {
        ("tdms",       "TDMS 波形", () => new FctTdmsViewer.MainForm()),
        // v3.4.2：聚合看板内嵌进主程序 —— 任何机台都能点开成为聚合端。
        // 惰加载 + 常驻：首次切过来才起监听（聚合库/共享目录/HTTP 8080），切走不停止，
        // 主程序退出才收尾；与 `argus agg`（独立窗口模式）同源同配置。
        ("agg",        "聚合看板", () => new AggCenterForm(
            AppConfig.Instance.AggShareRoot,
            Path.Combine(AppConfig.BaseDir, "data", "agg_center.db"))),
    };

    /// <summary>两个工具页（惰加载：第一次切过去才构造）。</summary>
    private readonly ToolHost[] _toolPages = new ToolHost[Tools.Length];

    private int _page = 0;

    /// <summary>
    /// --debug 启动参数：显示侧边栏「调试工具」入口。
    /// 默认 false（生产形态）——调试页有清空数据库/推送测试/自启开关，产线操作员不应能进。
    /// </summary>

    /// <summary>「调试工具」在 <see cref="PageTitles"/> 里的页索引（v3.22.1 FAIL 页回归后从 3 移到 4）。</summary>
    private const int DebugPageIndex = 4;

    public MainForm(Engine engine, bool debugMode = false)
    {
        _engine = engine;
        _cfg = AppConfig.Instance;
        Text = "Argus";
        // 内嵌工具自带最小尺寸，窗口得能装下；装不下时由 ToolHost 的 AutoScroll 兜底。
        // v3.0.0：数据分发（1040×790，原来最大的那个）移出后，剩下最大是 TDMS 波形 1000×640，
        // 所以最小窗口从 1240×860 降到 1200×720 —— 1366×768 的机台不再出横向滚动条。
        // v3.1.0：默认尺寸与开启动画素材同分辨率（1408×768），屏幕更小时自动收窄（如 1366×768 机台）
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Width = Math.Min(1408, wa.Width);
        Height = Math.Min(768, wa.Height);
        MinimumSize = new Size(1200, 720);
        StartPosition = FormStartPosition.CenterScreen;
        // v3.6.0：回归 Windows 原版风格 —— 用系统标准标题栏（默认 Sizable），
        // 不再用自绘炭黑标题栏；最大化由系统处理。
        FormBorderStyle = FormBorderStyle.Sizable;
        BackColor = Theme.Bg;
        AppIcon.Apply(this);                     // v2.9.4: 统一走 AppIcon（嵌入资源优先）
        Font = Theme.Body;
        KeyPreview = true;

        BuildUi();

        // ===== Windows 原生桌面提示 =====
        DesktopNotifier.Enabled = _cfg.DesktopNotify;
        DesktopNotifier.MinIntervalSeconds = _cfg.NotifyMinIntervalSec;
        DesktopNotifier.Activated += OnNotificationClicked;
        DesktopNotifier.Init();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    // ==================================================================
    //  外壳
    // ==================================================================
    private void BuildUi()
    {
        // ⚠ WinForms 停靠的真实规则：**按 z-order 从后往前处理，也就是最后 Add 的最先停靠**。
        //   所以想让侧栏占满整列高（最先停靠），它必须**最后** Add；
        //   而 Dock=Fill 的内容区要拿“剩下的”（最后停靠），它必须**最先** Add。
        //   写反了不报错：内容区会占整个客户区、其余栏盖在它上面（自检已有几何断言盯这个）。

        // ===== 内容区（最先 Add = 最后停靠，吃剩下的空间）=====
        var contentHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        Controls.Add(contentHost);

        // ===== 状态栏 =====
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
        // 聚合设置（机台视角）：连接聚合端 / 本机一键成为聚合端（v3.4.4 管理后台）
        var btnAggSettings = new Button
        {
            Text = "聚合设置",
            Dock = DockStyle.Right, Width = 96, Height = Theme.StatusBarHeight,
            FlatStyle = FlatStyle.Flat, BackColor = Theme.Surface, ForeColor = Theme.TextSub,
            Font = Theme.Small, Cursor = Cursors.Hand,
        };
        btnAggSettings.FlatAppearance.BorderColor = Theme.Border;
        btnAggSettings.Click += (_, _) => ShowAggSettingsDialog();
        // 聚合链路状态灯（v3.4.11）：绿=正常 / 黄=积压 / 红=断连 / 灰=未启用；悬浮显示详情
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
        // Mesh 节点状态定时刷新（v3.5.0 去中心化：PeerLink 状态由后台线程维护，UI 每 3s 轮询）
        _aggLinkTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _aggLinkTimer.Tick += (_, _) => { try { UpdateAggLinkUi(); } catch { } };
        _aggLinkTimer.Start();
        // 停靠顺序：后 Add 的先停靠 —— 按钮/灯先停靠（靠左），状态文本最后停靠（最右）
        statusBar.Controls.Add(_statusRight);
        statusBar.Controls.Add(btnAggSettings);
        statusBar.Controls.Add(_lblAggLink);
        statusBar.Controls.Add(_statusLeft);
        Controls.Add(statusBar);

        // ===== 扫描进度（细条，只在扫描时出现）=====
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

        // ===== 顶栏 =====
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
        // 同理：Fill 先 Add，两侧后 Add
        topBar.Controls.Add(_chips);
        topBar.Controls.Add(refreshHost);
        topBar.Controls.Add(_pageTitle);

        Controls.Add(topBar);

        // ===== v3.6.0：顶部 TabControl 承载所有页面（经典 Windows 布局，替代侧栏导航）=====
        // 每个页面占一个 TabPage，工具页的 ToolHost 也放进去（惰加载逻辑不变）。
        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.Dock = DockStyle.Fill;
        _tabs.Font = Theme.Body;
        contentHost.Controls.Add(_tabs);

        _dashboardPage = BuildDashboardPage();
        _devicePage = new DeviceStatusPanel(_cfg) { Dock = DockStyle.Fill };
        _maintPage = new MaintenancePanel(_engine) { Dock = DockStyle.Fill };
        _failPage = new FailListPanel(_engine) { Dock = DockStyle.Fill };
        _debugPage = new DebugPanel(_engine) { Dock = DockStyle.Fill };

        // 5 个主程序页 + 2 个工具页 = 7 个 TabPage
        Control[] pages = { _dashboardPage, _maintPage, _devicePage, _failPage, _debugPage };
        for (int i = 0; i < pages.Length; i++)
        {
            var tp = new TabPage(PageTitles[i]) { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            pages[i].Dock = DockStyle.Fill;
            tp.Controls.Add(pages[i]);
            Theme.Apply(pages[i]);
            _tabs.TabPages.Add(tp);
        }
        // 内嵌工具页（惰加载：这里只建宿主，真正构造在第一次切过去时）
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
            // 离开旧页：停用工具页
            if (_page >= OwnPageCount)
            {
                var oldHost = _toolPages[_page - OwnPageCount];
                oldHost.DeactivateTool();
            }
            // 切到新页
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

    // ==================================================================
    //  主页
    // ==================================================================
    private Panel BuildDashboardPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.Gap, Theme.Gap, Theme.Gap, 0) };

        // ---- 今日（主）：一排 5 张大卡，当日数据放最显眼位置 ----
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
        // 最后一张不要右边距
        _kpi["today_interrupted"].Margin = new Padding(0);

        // ---- 工业大屏核心区域（2×2 紧凑充满式栅格） ----
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
            // 点击实时异常流水，自动切到 FAIL 记录页进行定位
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

    // ==================================================================
    //  切页 / 快捷键
    // ==================================================================
    private void ShowPage(int index)
    {
        if (index < 0 || index >= PageTitles.Length) return;
        CardPreviewForm.CloseCurrent();
        // 离开当前工具页时停用
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

    /// <summary>顶栏【刷新】/ 切页时刷新当前页数据。工具页自己有刷新按钮，不插手。</summary>
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
                case 3: _failPage.Refresh2(); break;      // v3.22.1 FAIL 记录页回归
                default: break;                           // 4 = 调试(自刷新) / 5~6 = 内嵌工具页
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
            Keys.D4 => 3, Keys.D5 => DebugPageIndex,   // v3.22.1：Ctrl+D4 = FAIL 记录，Ctrl+D5 = 调试工具
            _ => -1,
        };
        if (idx >= 0) { ShowPage(idx); e.Handled = true; return; }
        // Ctrl+6..7 = 两个内嵌工具页（TDMS 波形 / 聚合看板）
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

    /// <summary>点桌面提示 / 双击托盘图标: 窗口拉到前面并直接跳到待办看板。</summary>
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

    // ==================================================================
    //  定时刷新
    // ==================================================================
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
        if (_page == 0 && (_tickCount % 3 == 1)) UpdateDashboardWidgets(); // 主页大屏图表 3 秒查库轻量刷新
        if (_tickCount % 10 == 1) UpdateTodoBadge();    // 查库的活儿 10 秒一次就够（拖动时减少撞库卡顿）
        UpdateStatusBar();
        if (_tickCount % 5 == 1) UpdateAggLinkUi();     // 积压数等低频刷新
        AutoRefreshMaintenance();
    }

    /// <summary>状态栏 Mesh 节点链路状态灯刷新（v3.5.0 去中心化）。
    /// 绿=全 peer 正常 / 黄=有积压 / 红=有断连 / 灰=无 peer 未启用；悬浮提示各 peer 明细。</summary>
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

        // 今日（主）—— 今日产品卡副标题带累计，串起两个层次
        SetKpi("today_product", s.TodayProductCount.ToString("N0"), $"累计 {s.ProductCount:N0}");
        SetKpi("today_pass", s.TodayPass.ToString("N0"));
        SetKpi("today_fail", s.TodayFail.ToString("N0"));
        SetKpi("today_yield", $"{s.TodayYield:F1}%");
        SetKpi("today_interrupted", s.TodayInterrupted.ToString("N0"));

        // 工业大屏组件：当月综合良率仪表盘
        _todayGauge?.SetData(s.MonthPass, s.MonthFail, s.MonthInterrupted);
    }

    /// <summary>主页工业大屏组件数据更新（小时趋势、Top5 不良、实时异常流水）。</summary>
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

    /// <summary>待办角标 / 未完成维修数 / 次数最高的待办（查库，低频）。</summary>
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
            // v3.6.0：待办角标改到「待办 / 维修」Tab 的标题上（原先是侧栏导航红点）
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
        // ⚠ 右侧只到「分钟」：HH:mm:ss 每秒变化会让状态栏每秒重绘一次，
        //   拖动窗口时每秒撞上一次小卡顿（点击后拖动轻微延迟的来源之一）
        var right = $"更新于 {DateTime.Now:HH:mm}     Ctrl+1~5 切页 / 6~9 工具 / R 刷新";
        if (_statusRight.Text != right) _statusRight.Text = right;
    }

    private int _lastFailSeen = -1;
    private DateTime _lastMaintAutoRefresh = DateTime.MinValue;

    /// <summary>
    /// FAIL 总数一变就刷待办看板 —— 新不良无需手动点刷新就会出现在「待办」列。
    /// ⚠ 节流：看板全量 Reload 很贵（SyncTodoItems + 区间聚合 + 600 张自绘卡重建 + 每卡注册 ToolTip），
    ///   FAIL 风暴 / 历史扫描期间 FAIL 每批增长，若不节流每秒全刷一次 → 点击维修页后拖动窗口严重卡顿。
    ///   现在：FAIL 变化后至少间隔 3 秒才刷一次，且只在看板页可见时刷。
    /// v3.3.0 兜底：FAIL 计数未变但距上次刷新超过 15s 也刷一次 ——
    ///   刷新触发依赖 RefreshStats 链路，兜底保证“新 fail 出现在待办列”不依赖任何计数变化。
    /// </summary>
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
        // ⚠ v3.1.0.2：**禁用 AnimateWindow(AW_CENTER)** —— 实测系统窗口动画会干扰后续拖动
        //   合成（点击后拖动窗口严重延迟）。过渡改为纯逻辑：splash 圆点 → 主窗口直接显示
        //   （splash 在主窗口显示 450ms 后被隐藏，视觉上仍连贯）。

        // v3.6.0 更新器：延迟到主窗口稳定显示后再检测，避免阻塞首屏。
        // v3.26.0 无感热升级（auto_update=true，默认）：检测到新包自动「暂存 → 托盘气泡告知 →
        //   排定分离进程重启 → CommitPendingUpdate 完成升级」，全程零弹窗零人工。
        // auto_update=false 时保持旧行为：弹窗让用户选「立即更新/稍后提醒」。
        // 另挂 5 分钟周期检测（运行中放入新包也能被无感升级）。
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

    /// <summary>
    /// 无感热升级检测（v3.26.0，auto_update=true 时走这条路径）：
    /// 扫到更高版本的更新包 → 自动暂存+备份 → 托盘气泡告知 → 排定分离进程 3s 后拉起新版
    /// （--post-update）→ 关闭本窗体优雅退出（引擎停机 → 进程退出 → 新实例完成文件替换）。
    /// 暂存失败（包损坏/被占用）只记日志，下个周期重试；不标记已提示，避免漏掉有效更新。
    /// </summary>
    private void AutoUpdateCheck()
    {
        var info = UpdateChecker.Scan(db: _engine.Db);
        if (info == null) return;
        Logger.Info($"[更新器] 无感热升级：发现新包 v{info.Version}（{Path.GetFileName(info.ZipPath)}），自动暂存中…");
        UpdateChecker.StageUpdate(info, _engine.Db);
        UpdateChecker.MarkPrompted(info.Version, _engine.Db);
        DesktopNotifier.NotifyRaw($"发现新版本 v{info.Version}：Argus 将在几秒后自动重启完成升级，数据与配置不受影响。");
        UpdateChecker.ScheduleRestart(delaySeconds: 3);
        // 给托盘气泡/日志一点落盘时间，再优雅退出（Application.Run 返回 → engine.Stop → 进程退出）
        var t = new System.Windows.Forms.Timer { Interval = 1500 };
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); Close(); };
        t.Start();
    }

    /// <summary>
    /// 机台视角「聚合设置」对话框（v3.4.4 管理后台）：
    ///   ① 连接聚合端：填 IP[:端口] → 写 config(agg_enabled/http/url) → 重启推送器；
    ///   ② 本机一键成为聚合端：拉起 <c>Argus.exe agg --install</c>（现成的一键部署：
    ///      生成配置/防火墙/开机自启/启动聚合服务）。
    /// </summary>
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

        // ===== 当前状态 =====
        var stateLabel = new Label
        {
            Text = cfg.AggEnabled
                ? $"当前: 已开启聚合推送（{cfg.AggTransport} → {UrlOrShare()}）"
                : "当前: 未开启聚合推送",
            Location = new Point(24, 16), AutoSize = true, ForeColor = Theme.TextSub, Font = Theme.Small,
        };
        dlg.Controls.Add(stateLabel);

        // ===== 链路状态（v3.4.11 连通性加固：现场一眼看到连接健康） =====
        var linkStateLabel = new Label
        {
            Text = BuildLinkStateText(),
            Location = new Point(24, 34), AutoSize = true, ForeColor = Theme.TextFaint, Font = Theme.Small,
        };
        dlg.Controls.Add(linkStateLabel);

        // ===== ① 连接聚合端 =====
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

        // ===== ② 本机一键成为聚合端 =====
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
            // 防火墙放行需要管理员：当前是管理员直接执行，否则 runas 提权重启自身执行
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

            // 已是管理员：当前进程直接执行部署（不依赖任何外部 bat）
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

        // ===== ③ 断开聚合推送 =====
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

    /// <summary>聚合设置弹窗里的链路状态行文本（v3.5.0 去中心化：显示各 peer 链路摘要）。</summary>
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

    /// <summary>从已配置的 http url 里提取 IP（填连接框默认值用）；没有则空串。</summary>
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
        // 托盘图标必须显式隐藏，否则退出后图标会残留到鼠标滑过才消失
        DesktopNotifier.Shutdown();
        base.OnFormClosing(e);
    }
}

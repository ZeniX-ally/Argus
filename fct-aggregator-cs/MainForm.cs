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
    private Panel _nav = null!;
    private readonly List<NavButton> _navButtons = new();
    private Label _pageTitle = null!;
    private ChipBar _chips = null!;
    private Panel _progressPanel = null!;
    private Label _progressLabel = null!;
    private ProgressBar _progressBar = null!;
    private Label _statusLeft = null!;
    private Label _statusRight = null!;

    // ===== 主页 =====
    private readonly Dictionary<string, KpiCard> _kpi = new();
    private readonly Dictionary<string, Label> _statValues = new();
    private RichTextBox _logBox = null!;
    private int _logSynced = 0;

    // ===== 页面 =====
    private Panel _dashboardPage = null!;
    private DeviceStatusPanel _devicePage = null!;
    private MaintenancePanel _maintPage = null!;
    private DebugPanel _debugPage = null!;
    private FailListPanel _failPage = null!;
    private System.Windows.Forms.Timer _timer = null!;

    /// <summary>页索引 → 标题（顶栏显示）。0~4 主程序页，5~8 内嵌的工具页。</summary>
    private static readonly string[] PageTitles =
    {
        "总览", "待办 / 维修记录", "FAIL 记录", "设备状态", "调试工具",
        "数据分发", "FAIL 排行", "取数打包", "TDMS 波形",
    };

    /// <summary>主程序自己的页面数（后面那四个是内嵌工具页）。</summary>
    private const int OwnPageCount = 5;

    /// <summary>
    /// 工具（v2.9.2 起**内嵌**在主窗口里，不再弹独立窗口）。
    /// key 同时是 CLI 子命令名（<c>FCT-Aggregator.exe distribute</c> 仍能单独开窗）。
    /// </summary>
    private static readonly (string key, string label, Func<Form> factory)[] Tools =
    {
        ("distribute", "数据分发", () => new FctDistributor.MainForm()),
        ("rank",       "FAIL 排行", () => new FctFailRanker.MainForm()),
        ("fetch",      "取数打包", () => new FctFetcher.MainForm()),
        ("tdms",       "TDMS 波形", () => new FctTdmsViewer.MainForm()),
    };

    /// <summary>四个工具页（惰加载：第一次切过去才构造）。</summary>
    private readonly ToolHost[] _toolPages = new ToolHost[Tools.Length];

    private int _page = 0;

    public MainForm(Engine engine)
    {
        _engine = engine;
        _cfg = AppConfig.Instance;
        Text = "Zenix-Ally | FCT 工具套件 v2.9.2";
        // 内嵌的工具自带最小尺寸（最大的数据分发是 1040×790），窗口得能装下；
        // 装不下时由 ToolHost 的 AutoScroll 兼底。
        Width = 1360; Height = 920;
        MinimumSize = new Size(1240, 860);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        try { Icon = new Icon(Path.Combine(AppConfig.BaseDir, "app_icon.ico")); } catch { }
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
        statusBar.Controls.Add(_statusLeft);
        statusBar.Controls.Add(_statusRight);
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
            Dock = DockStyle.Right, Width = 92, BackColor = Theme.Surface, Padding = new Padding(9, 12, 9, 12),
        };
        refreshHost.Controls.Add(btnRefresh);
        // 同理：Fill 先 Add，两侧后 Add
        topBar.Controls.Add(_chips);
        topBar.Controls.Add(refreshHost);
        topBar.Controls.Add(_pageTitle);
        Controls.Add(topBar);

        // ===== 侧边导航（最后 Add = 最先停靠 → 占满整列高）=====
        Controls.Add(BuildNav());

        // ===== 页面 =====
        _dashboardPage = BuildDashboardPage();
        _devicePage = new DeviceStatusPanel(_cfg) { Dock = DockStyle.Fill, Visible = false };
        _maintPage = new MaintenancePanel(_engine) { Dock = DockStyle.Fill, Visible = false };
        _debugPage = new DebugPanel(_engine) { Dock = DockStyle.Fill, Visible = false };
        _failPage = new FailListPanel(_engine) { Dock = DockStyle.Fill, Visible = false };
        // v2.9.2：主程序的面板也过一道主题器，把漏刷的默认控件（输入框边框、
        // ListView 底色、普通按钮）拉齐；自绘控件与看板已在 Theme.Apply 里跳过
        foreach (Control page in new Control[] { _dashboardPage, _devicePage, _maintPage, _debugPage, _failPage })
            Theme.Apply(page);
        contentHost.Controls.Add(_dashboardPage);
        contentHost.Controls.Add(_devicePage);
        contentHost.Controls.Add(_maintPage);
        contentHost.Controls.Add(_debugPage);
        contentHost.Controls.Add(_failPage);
        // 四个内嵌工具页（惰加载：这里只建宿主，真正构造在第一次切过去时）
        for (int i = 0; i < Tools.Length; i++)
        {
            var t = Tools[i];
            _toolPages[i] = new ToolHost(t.label, t.factory);
            contentHost.Controls.Add(_toolPages[i]);
        }
        _dashboardPage.BringToFront();

        KeyDown += OnShortcut;
    }

    /// <summary>侧边导航：品牌块 + 5 个导航项 + 底部退出。只构造不挂，由调用方按停靠顺序 Add。</summary>
    private Panel BuildNav()
    {
        _nav = new Panel { Dock = DockStyle.Left, Width = Theme.NavWidth, BackColor = Theme.NavBg };

        var brand = new Panel { Dock = DockStyle.Top, Height = Theme.TopBarHeight, BackColor = Theme.NavBg };
        brand.Paint += (_, e) =>
        {
            TextRenderer.DrawText(e.Graphics, "Zenix-Ally", new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
                new Rectangle(18, 6, brand.Width - 24, 22), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics, "FCT 数据聚合", Theme.Small,
                new Rectangle(18, 26, brand.Width - 24, 18), Theme.NavText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        };

        var exit = new NavButton("退出程序", -1) { Dock = DockStyle.Bottom };
        exit.Click += (_, _) => Close();

        var version = new Label
        {
            Dock = DockStyle.Bottom, Height = 26, Text = "v2.9.2", ForeColor = Color.FromArgb(120, 134, 154),
            Font = Theme.Small, BackColor = Theme.NavBg, TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(18, 0, 0, 0),
        };

        // 同一个规则：后 Add 的先停靠。想让“总览”在最上面，就把它最后 Add。
        for (int i = PageTitles.Length - 1; i >= 0; i--)
        {
            var idx = i;
            var b = new NavButton(NavLabel(i), i);
            b.Click += (_, _) => ShowPage(idx);
            _navButtons.Add(b);
        }
        // ===== 工具箱（v2.9.2：也是切页，不再弹独立窗口）=====
        var toolButtons = new List<NavButton>();
        for (int i = Tools.Length - 1; i >= 0; i--)
        {
            var page = OwnPageCount + i;
            var b = new NavButton(Tools[i].label, page);
            b.Click += (_, _) => ShowPage(page);
            toolButtons.Add(b);
            _navButtons.Add(b);                             // 与主页面一样参与高亮
        }
        var toolTitle = new Label
        {
            Dock = DockStyle.Top, Height = 26, Text = "工具箱", ForeColor = Color.FromArgb(118, 132, 152),
            Font = Theme.Tiny, BackColor = Theme.NavBg, TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(18, 0, 0, 4),
        };

        _nav.Controls.Add(version);
        _nav.Controls.Add(exit);
        foreach (var b in toolButtons) _nav.Controls.Add(b);       // 工具箱在页面导航下面
        _nav.Controls.Add(toolTitle);
        foreach (var b in _navButtons.Where(x => x.PageIndex < OwnPageCount).ToList())
            _nav.Controls.Add(b);                                 // PageIndex 4 → 0 逐个 Add
        _nav.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.NavBg });
        _nav.Controls.Add(brand);                                 // 最后 Add → 最先停靠 → 真的在最上面
        _navButtons.Sort((a, b) => a.PageIndex.CompareTo(b.PageIndex));
        SetActiveNav(0);
        return _nav;
    }

    private static string NavLabel(int i) => i switch
    {
        0 => "总览",
        1 => "待办 / 维修",
        2 => "FAIL 记录",
        3 => "设备状态",
        4 => "调试工具",
        _ => "",
    };

    private void SetActiveNav(int index)
    {
        foreach (var b in _navButtons) b.Active = b.PageIndex == index;
    }

    // ==================================================================
    //  主页
    // ==================================================================
    private Panel BuildDashboardPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.Gap, Theme.Gap, Theme.Gap, 0) };

        // ---- 顶部 KPI 一排 ----
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
        AddKpi("product", "产品总数（SN 去重）", Theme.Primary, 0);
        AddKpi("pass", "PASS 良品", Theme.Success, 1);
        AddKpi("fail", "FAIL 不良", Theme.Danger, 2);
        AddKpi("yield", "良率", Theme.Info, 3);
        AddKpi("todo", "待办（未确认不良）", Theme.Warning, 4);
        // 最后一张不要右边距
        _kpi["todo"].Margin = new Padding(0);

        // ---- 今日一排 ----
        var todayRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 76, ColumnCount = 4, RowCount = 1, BackColor = Theme.Bg,
            Padding = new Padding(0, Theme.Gap, 0, 0),
        };
        for (int i = 0; i < 4; i++) todayRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        void AddToday(string key, string title, Color accent, int col)
        {
            var c = new KpiCard(title, accent, big: false) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.Gap, 0) };
            _kpi[key] = c;
            todayRow.Controls.Add(c, col, 0);
        }
        AddToday("today_pass", "今日 PASS", Theme.Success, 0);
        AddToday("today_fail", "今日 FAIL", Theme.Danger, 1);
        AddToday("today_interrupted", "今日中断", Theme.Neutral, 2);
        AddToday("today_yield", "今日良率", Theme.Info, 3);
        _kpi["today_yield"].Margin = new Padding(0);

        // ---- 下半：左明细 + 右日志 ----
        var lower = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, Theme.Gap, 0, Theme.Gap) };

        var detail = new SectionPanel("统计明细") { Dock = DockStyle.Left, Width = 300, Margin = new Padding(0) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Theme.Surface };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        var rows = new (string label, string key)[]
        {
            ("中断 (INTERRUPTED)", "interrupted"),
            ("Invalid", "invalid"),
            ("解析错误", "parse_error"),
            ("XML 文件总数", "xml"),
            ("维修记录（未完）", "maint_open"),
            ("待办累计次数 Top", "todo_top"),
        };
        int r = 0;
        foreach (var (label, key) in rows)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            grid.Controls.Add(new Label
            {
                Text = label, Dock = DockStyle.Fill, ForeColor = Theme.TextSub, Font = Theme.Body,
                TextAlign = ContentAlignment.MiddleLeft,
            }, 0, r);
            var val = new Label
            {
                Text = "—", Dock = DockStyle.Fill, ForeColor = Theme.TextMain, Font = Theme.BodyBold,
                TextAlign = ContentAlignment.MiddleRight,
            };
            grid.Controls.Add(val, 1, r);
            _statValues[key] = val;
            r++;
        }
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(new Label { Text = "" }, 0, r);
        detail.Content.Controls.Add(grid);

        var logs = new SectionPanel("实时日志") { Dock = DockStyle.Fill, Margin = new Padding(0) };
        logs.Hint = "（最新在下方）";
        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BackColor = Theme.Surface, BorderStyle = BorderStyle.None,
            Font = Theme.Mono, WordWrap = false, TabStop = false,
        };
        var logTools = new Panel { Dock = DockStyle.Bottom, Height = 36, BackColor = Theme.Surface };
        var btnClear = Theme.MakeButton("清空日志", 88);
        btnClear.Dock = DockStyle.Right;
        btnClear.Click += (_, _) => { Logger.ClearGuiLogs(); _logBox.Clear(); _logSynced = 0; };
        logTools.Controls.Add(btnClear);
        logs.Content.Controls.Add(_logBox);
        logs.Content.Controls.Add(logTools);

        var spacer = new Panel { Dock = DockStyle.Left, Width = Theme.Gap, BackColor = Theme.Bg };
        lower.Controls.Add(logs);
        lower.Controls.Add(spacer);
        lower.Controls.Add(detail);

        page.Controls.Add(lower);
        page.Controls.Add(todayRow);
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
        CardPreviewForm.CloseCurrent();      // 切页先关掉卡片预览弹窗
        _page = index;
        _dashboardPage.Visible = index == 0;
        _maintPage.Visible = index == 1;
        _failPage.Visible = index == 2;
        _devicePage.Visible = index == 3;
        _debugPage.Visible = index == 4;
        for (int i = 0; i < _toolPages.Length; i++)
            _toolPages[i].Visible = index == OwnPageCount + i;

        Control target;
        if (index >= OwnPageCount)
        {
            var host = _toolPages[index - OwnPageCount];
            host.Ensure();                    // 首次切过去才真正构造并内嵌
            target = host;
        }
        else
        {
            target = index switch
            {
                1 => _maintPage, 2 => _failPage, 3 => _devicePage, 4 => _debugPage, _ => _dashboardPage
            };
        }
        target.BringToFront();
        _pageTitle.Text = PageTitles[index];
        SetActiveNav(index);
        RefreshCurrentPage();
    }

    /// <summary>顶栏【刷新】/ 切页时刷新当前页数据。工具页自己有刷新按钮，不插手。</summary>
    private void RefreshCurrentPage()
    {
        try
        {
            switch (_page)
            {
                case 1: _maintPage.Refresh2(); break;
                case 2: _failPage.Refresh2(); break;
                case 3: _devicePage.Refresh2(); break;
                case 0: Tick(); break;
                default: break;               // 5~8 = 内嵌工具页
            }
        }
        catch (Exception ex) { Logger.Warning($"页面刷新失败: {ex.Message}"); }
    }

    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (!e.Control) return;
        int idx = e.KeyCode switch
        {
            Keys.D1 => 0, Keys.D2 => 1, Keys.D3 => 2, Keys.D4 => 3, Keys.D5 => 4,
            _ => -1,
        };
        if (idx >= 0) { ShowPage(idx); e.Handled = true; return; }
        // Ctrl+6..9 = 四个内嵌工具页
        int tool = e.KeyCode switch
        {
            Keys.D6 => 0, Keys.D7 => 1, Keys.D8 => 2, Keys.D9 => 3,
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
    private int _lastFailSeen = -1;
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
        UpdateLogs();
        if (_tickCount % 5 == 1) UpdateTodoBadge();     // 查库的活儿 5 秒一次就够
        UpdateStatusBar();
        AutoRefreshMaintenance();
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
        void SetVal(string k, string v)
        {
            if (_statValues.TryGetValue(k, out var l) && l.Text != v) l.Text = v;
        }

        SetKpi("product", s.ProductCount.ToString("N0"), $"XML {s.XmlCount:N0} 个");
        SetKpi("pass", s.Pass.ToString("N0"));
        SetKpi("fail", s.Fail.ToString("N0"), s.Interrupted > 0 ? $"中断 {s.Interrupted}" : "");
        SetKpi("yield", $"{s.YieldRate:F1}%");
        SetKpi("todo", _pendingTodo.ToString("N0"), _pendingTodo > 0 ? "点左侧「待办 / 维修」处理" : "暂无待处理");

        SetKpi("today_pass", s.TodayPass.ToString("N0"));
        SetKpi("today_fail", s.TodayFail.ToString("N0"));
        SetKpi("today_interrupted", s.TodayInterrupted.ToString("N0"));
        SetKpi("today_yield", $"{s.TodayYield:F1}%");

        SetVal("interrupted", s.Interrupted.ToString("N0"));
        SetVal("invalid", s.Invalid.ToString("N0"));
        SetVal("parse_error", s.ParseError.ToString("N0"));
        SetVal("xml", s.XmlCount.ToString("N0"));
        SetVal("maint_open", _openMaint.ToString("N0"));
        SetVal("todo_top", _todoTop);
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
            var nav = _navButtons.FirstOrDefault(b => b.PageIndex == 1);
            if (nav != null) nav.Badge = _pendingTodo;
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
        var right = $"更新于 {DateTime.Now:HH:mm:ss}     Ctrl+1~5 切页 / 6~9 工具 / R 刷新";
        _statusRight.Text = right;
    }

    private void UpdateLogs()
    {
        var logs = Logger.SnapshotGuiLogs();
        if (_logSynced >= logs.Count)
        {
            if (_logSynced > logs.Count) { _logBox.Clear(); _logSynced = 0; } // 被清空过
            if (_logSynced >= logs.Count) return;
        }
        var batch = logs.Skip(_logSynced).Take(200).ToList();
        _logSynced += batch.Count;
        foreach (var line in batch)
        {
            Color color = Theme.TextMain;
            if (line.Contains("[ERROR]")) color = Theme.Danger;
            else if (line.Contains("[WARNING]")) color = Theme.Warning;
            else if (line.Contains("[INFO]")) color = Theme.Primary;
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.SelectionColor = color;
            _logBox.AppendText(line + Environment.NewLine);
        }
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    /// <summary>
    /// FAIL 总数一变就刷待办看板 —— 新不良无需手动点刷新就会出现在「待办」列。
    /// 只在该页可见时重载，避免后台白白查库。
    /// </summary>
    private void AutoRefreshMaintenance()
    {
        var fail = AppState.Snapshot().Fail;
        if (_lastFailSeen < 0) { _lastFailSeen = fail; return; }
        if (fail == _lastFailSeen) return;
        _lastFailSeen = fail;
        if (_maintPage.Visible)
        {
            try { _maintPage.Refresh2(); }
            catch (Exception ex) { Logger.Warning($"待办看板自动刷新失败: {ex.Message}"); }
        }
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

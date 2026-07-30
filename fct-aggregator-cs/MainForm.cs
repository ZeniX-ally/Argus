using System.Text;

namespace FctAggregator;

public class MainForm : Form
{
    private readonly Engine _engine;
    private readonly AppConfig _cfg;

    // Header
    private Label _headerLabel = null!;
    // 进度条
    private Panel _progressPanel = null!;
    private Label _progressLabel = null!;
    private ProgressBar _progressBar = null!;
    // 统计
    private readonly Dictionary<string, Label> _statValues = new();
    // 日志
    private RichTextBox _logBox = null!;
    private int _logSynced = 0;
    // 页面切换
    private Panel _dashboardPage = null!;
    private DeviceStatusPanel _devicePage = null!;
    private MaintenancePanel _maintPage = null!;
    private DebugPanel _debugPage = null!;
    private FailListPanel _failPage = null!;
    private System.Windows.Forms.Timer _timer = null!;

    public MainForm(Engine engine)
    {
        _engine = engine;
        _cfg = AppConfig.Instance;
        Text = "Zenix-Ally | FCT-Aggregator v2.6.0";
        Width = 1200; Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = new Icon(Path.Combine(AppConfig.BaseDir, "app_icon.ico")); } catch { }
        Font = new Font("Microsoft YaHei UI", 9F);

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

    private void BuildUi()
    {
        // ===== Header 状态栏 =====
        _headerLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 46,
            BackColor = Color.FromArgb(26, 82, 118),
            ForeColor = Color.White,
            Padding = new Padding(14, 0, 14, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 10F),
        };
        Controls.Add(_headerLabel);

        // ===== 进度条 =====
        _progressPanel = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(14, 4, 14, 4) };
        _progressLabel = new Label { Dock = DockStyle.Left, Width = 200, TextAlign = ContentAlignment.MiddleLeft, Text = "扫描准备中..." };
        _progressBar = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee };
        _progressPanel.Controls.Add(_progressBar);
        _progressPanel.Controls.Add(_progressLabel);
        Controls.Add(_progressPanel);

        // ===== 底部按钮栏 =====
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8, 6, 8, 6) };
        var btnDashboard = MakeButton("主页", (_, _) => ShowPage(0));
        var btnDevice = MakeButton("设备状态", (_, _) => ShowPage(1));
        var btnMaint = MakeButton("待办/维修", (_, _) => ShowPage(2));
        var btnFail = MakeButton("FAIL记录", (_, _) => ShowPage(4));
        var btnDebug = MakeButton("调试", (_, _) => ShowPage(3));
        var btnClearLog = MakeButton("清空日志", (_, _) => { Logger.ClearGuiLogs(); _logBox.Clear(); _logSynced = 0; });
        var btnExit = MakeButton("退出", (_, _) => Close());
        btnExit.BackColor = Color.FromArgb(146, 43, 33); btnExit.ForeColor = Color.White;
        footer.Controls.AddRange(new Control[] { btnDashboard, btnDevice, btnMaint, btnFail, btnDebug, btnClearLog, btnExit });
        Controls.Add(footer);

        // ===== 内容区(多页切换) =====
        var contentHost = new Panel { Dock = DockStyle.Fill };
        Controls.Add(contentHost);
        contentHost.BringToFront();

        _dashboardPage = BuildDashboardPage();
        _devicePage = new DeviceStatusPanel(_cfg) { Dock = DockStyle.Fill, Visible = false };
        _maintPage = new MaintenancePanel(_engine) { Dock = DockStyle.Fill, Visible = false };
        _debugPage = new DebugPanel(_engine) { Dock = DockStyle.Fill, Visible = false };
        _failPage = new FailListPanel(_engine) { Dock = DockStyle.Fill, Visible = false };
        contentHost.Controls.Add(_dashboardPage);
        contentHost.Controls.Add(_devicePage);
        contentHost.Controls.Add(_maintPage);
        contentHost.Controls.Add(_debugPage);
        contentHost.Controls.Add(_failPage);
        _dashboardPage.BringToFront();
    }

    private Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = false, Width = 100, Height = 30 };
        b.Click += onClick;
        return b;
    }

    private SplitContainer _dashSplit = null!;

    private Panel BuildDashboardPage()
    {
        var page = new Panel { Dock = DockStyle.Fill };
        var split = new SplitContainer { Dock = DockStyle.Fill };
        _dashSplit = split;
        page.Controls.Add(split);

        // 左: 统计
        var statBox = new GroupBox { Text = "Statistics", Dock = DockStyle.Fill, Padding = new Padding(10) };
        var statLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true };
        statLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        statLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

        var rows = new (string label, string key, bool bold)[]
        {
            ("PASS (良品)", "pass", false),
            ("FAIL (不良)", "fail", false),
            ("中断", "interrupted", false),
            ("Invalid", "invalid", false),
            ("Parse Error", "parse_error", false),
            ("产品总数(SN去重)", "product", true),
            ("XML文件总数", "xml", true),
            ("良率", "yield", true),
            ("—— 今日 ——", "sep", true),
            ("今日 PASS", "today_pass", false),
            ("今日 FAIL", "today_fail", false),
            ("今日中断", "today_interrupted", false),
            ("今日良率", "today_yield", true),
        };
        int r = 0;
        foreach (var (label, key, bold) in rows)
        {
            var name = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 3, 4) };
            var val = new Label { Text = key == "sep" ? "" : "0", AutoSize = true, Anchor = AnchorStyles.Right, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(3, 4, 3, 4) };
            if (bold)
            {
                name.Font = new Font(Font, FontStyle.Bold);
                val.Font = new Font(Font, FontStyle.Bold);
            }
            statLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));   // 每行固定行高, 避免最后一行被拉开
            statLayout.Controls.Add(name, 0, r);
            statLayout.Controls.Add(val, 1, r);
            _statValues[key] = val;
            r++;
        }
        // 末尾加一个弹性空行, 吸收剩余空间(不让“今日良率”被推开)
        statLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        statLayout.Controls.Add(new Label { Text = "" }, 0, r);
        statBox.Controls.Add(statLayout);
        split.Panel1.Controls.Add(statBox);

        // 右: 日志
        var logBox = new GroupBox { Text = "Latest Logs", Dock = DockStyle.Fill, Padding = new Padding(10) };
        _logBox = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, Font = new Font("Consolas", 9F) };
        logBox.Controls.Add(_logBox);
        split.Panel2.Controls.Add(logBox);

        return page;
    }

    private void ShowPage(int index)
    {
        _dashboardPage.Visible = index == 0;
        _devicePage.Visible = index == 1;
        _maintPage.Visible = index == 2;
        _debugPage.Visible = index == 3;
        _failPage.Visible = index == 4;
        if (index == 1) _devicePage.Refresh2();
        if (index == 2) _maintPage.Refresh2();
        if (index == 4) _failPage.Refresh2();
        Control target = index switch { 1 => _devicePage, 2 => _maintPage, 3 => _debugPage, 4 => _failPage, _ => _dashboardPage };
        target.BringToFront();
    }

    // (清空数据库功能已移至 Debug 页)

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
            ShowPage(2);
        }
        catch (Exception ex) { Logger.Warning($"提示点击处理失败: {ex.Message}"); }
    }

    // ==================== 定时刷新 ====================
    private int _lastFailSeen = -1;

    private void Tick()
    {
        UpdateHeader();
        UpdateProgress();
        UpdateStats();
        UpdateLogs();
        AutoRefreshMaintenance();
    }

    /// <summary>
    /// FAIL 总数一变就刷待办看板 —— 新不良无需手动点刷新就会出现在「待办」列。
    /// 只在该页可见时重载，避开后台白白查库。
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

    private void UpdateHeader()
    {
        var s = AppState.Snapshot();
        var statusColor = s.Status == "running" ? "🟢" : s.Status == "error" ? "🔴" : "🟡";
        var webhookMark = s.WebhookConfigured ? "🟢 ON" : "🔴 OFF";
        var notifyMark = DesktopNotifier.Enabled ? "🟢 ON" : "⚪ OFF";
        var scanMark = s.HistoricalScanComplete ? "🟢 Ready" : "🟡 Scanning...";
        var station = string.IsNullOrEmpty(s.StationId) ? "Auto-detect" : s.StationId;
        var text = $"Zenix-Ally     Station: {station}     Status: {statusColor} {s.Status}     " +
                   $"Models: {s.ModelsCount}     Webhook: {webhookMark}     桌面提示: {notifyMark}     History: {scanMark}";
        if (s.Status == "error")
            text += "     ⚠ 结果目录不存在，数据采集未运行！";
        if (_headerLabel.Text != text) _headerLabel.Text = text;
    }

    private void UpdateProgress()
    {
        var (phase, total, parsed) = AppState.GetScanProgress();
        switch (phase)
        {
            case "scanning":
                _progressPanel.Visible = true;
                _progressBar.Style = ProgressBarStyle.Marquee;
                _progressLabel.Text = $"扫描中... 已发现 {total} 个文件";
                break;
            case "parsing":
                _progressPanel.Visible = true;
                _progressBar.Style = ProgressBarStyle.Continuous;
                _progressBar.Maximum = Math.Max(total, 1);
                _progressBar.Value = Math.Min(parsed, _progressBar.Maximum);
                var pct = total > 0 ? parsed * 100.0 / total : 0;
                _progressLabel.Text = $"解析中... {parsed}/{total} ({pct:F1}%)";
                break;
            case "done":
            default:
                _progressPanel.Visible = false;
                break;
        }
    }

    private void UpdateStats()
    {
        var s = AppState.Snapshot();
        void Set(string k, string v) { if (_statValues.TryGetValue(k, out var l) && l.Text != v) l.Text = v; }
        Set("pass", s.Pass.ToString());
        Set("fail", s.Fail.ToString());
        Set("interrupted", s.Interrupted.ToString());
        Set("invalid", s.Invalid.ToString());
        Set("parse_error", s.ParseError.ToString());
        Set("product", s.ProductCount.ToString());
        Set("xml", s.XmlCount.ToString());
        Set("yield", $"{s.YieldRate:F1}%");
        Set("today_pass", s.TodayPass.ToString());
        Set("today_fail", s.TodayFail.ToString());
        Set("today_interrupted", s.TodayInterrupted.ToString());
        Set("today_yield", $"{s.TodayYield:F1}%");
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
            Color color = Color.Black;
            if (line.Contains("[ERROR]")) color = Color.FromArgb(200, 40, 40);
            else if (line.Contains("[WARNING]")) color = Color.FromArgb(200, 120, 0);
            else if (line.Contains("[INFO]")) color = Color.FromArgb(40, 80, 160);
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.SelectionColor = color;
            _logBox.AppendText(line + Environment.NewLine);
        }
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SetSplitRatio();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        SetSplitRatio();
    }

    /// <summary>主页左右比例: 统计 20% / 日志 80%。</summary>
    private void SetSplitRatio()
    {
        if (_dashSplit == null || _dashSplit.Width <= 0) return;
        try
        {
            int target = (int)(_dashSplit.Width * 0.20);
            // 限制在合法范围内
            int min = _dashSplit.Panel1MinSize;
            int max = _dashSplit.Width - _dashSplit.Panel2MinSize - _dashSplit.SplitterWidth;
            if (max > min) _dashSplit.SplitterDistance = Math.Clamp(target, min, max);
        }
        catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            var confirm = MessageBox.Show("确定要退出吗？退出后将停止数据采集。", "确认退出",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) { e.Cancel = true; return; }
        }
        // 托盘图标必须显式隐藏，否则退出后图标会残留到鰠标滑过才消失
        DesktopNotifier.Shutdown();
        base.OnFormClosing(e);
    }
}

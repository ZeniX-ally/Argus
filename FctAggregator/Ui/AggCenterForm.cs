using System.Globalization;
using System.Text;

namespace FctAggregator;

#pragma warning disable CS0618

public class AggCenterForm : Form
{
    private readonly string _shareRoot;
    private readonly string _dbPath;
    private readonly int _heartbeatTimeoutSec;
    private readonly int _httpPort;
    private readonly AggDatabase _db;
    private readonly AggWatcher _watcher;
    private HttpIngest? _ingest;
    private AggAlertService _alert;
    private readonly System.Windows.Forms.Timer _timer = new();
    private TableLayoutPanel _split = null!;

    private Label _lblShareRoot = null!;
    private Label _lblDbPath = null!;
    private readonly Dictionary<string, KpiCard> _kpis = new();

    private FlowLayoutPanel _cardFlow = null!;
    private Label _emptyLabel = null!;
    private readonly Dictionary<string, MachineCard> _cards = new();

    private DataGridView _grid = null!;
    private Label _emptyGridLabel = null!;
    private List<AggFailRow> _gridFails = new();

    private ToolStripStatusLabel _statusState = null!;
    private ToolStripStatusLabel _statusRefresh = null!;
    private ToolStripStatusLabel _statusHttp = null!;
    private ToolStripStatusLabel _statusTimeout = null!;

    private long _lastGridSignature = -1;
    private bool _refreshing;
    private List<AggMachineStatus> _lastMachines = new();
    private string _filterMachine = "";
    private string _filterSearch = "";
    private ComboBox _filterCombo = null!;
    private TextBox _searchBox = null!;
    private bool _cardsCollapsed;
    private RegionCard _cardsRegion = null!;

    public AggCenterForm(string shareRoot, string dbPath, int heartbeatTimeoutSec = 90)
    {
        _shareRoot = shareRoot;
        _dbPath = dbPath;
        _heartbeatTimeoutSec = heartbeatTimeoutSec;

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _db = new AggDatabase(dbPath);
        _watcher = new AggWatcher(shareRoot, _db, heartbeatTimeoutSec);
        _watcher.Changed += OnWatcherChanged;

        var cfg = AppConfig.Instance;
        _alert = new AggAlertService(_watcher, _db, cfg.AggWebhookUrl, cfg.AggSummaryMinutes);
        _alert.Start();

        _httpPort = cfg.AggHttpPort;
        var transport = string.IsNullOrEmpty(cfg.AggTransport) ? "smb" : cfg.AggTransport;
        if (transport.Contains("http", StringComparison.OrdinalIgnoreCase))
        {
            _ingest = new HttpIngest(_httpPort, _watcher.IngestFail, _watcher.IngestHeartbeat, cfg.AggToken);
            Logger.Info($"[聚合看板] HTTP 接收已接线: 端口 {_httpPort}（transport={transport}）");
        }
        else
        {
            Logger.Info($"[聚合看板] HTTP 接收未启用（agg_transport='{transport}'，纯 smb 通道）");
        }

        Text = "Argus 聚合看板";
        MinimumSize = new Size(1000, 650);
        Size = new Size(1220, 780);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        Font = Theme.Body;
        DoubleBuffered = true;

        BuildUi();
        Theme.Apply(this);

        _timer.Interval = 3000;
        _timer.Tick += (_, _) => RefreshData();
        _timer.Start();

        Shown += OnShown;
        FormClosed += OnFormClosed;
    }

    private void BuildUi()
    {
        var topBar = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Theme.Surface };
        topBar.Paint += (_, e) =>
        {
            using var p = new Pen(Theme.Border);
            e.Graphics.DrawLine(p, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
        };
        var infoLeft = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(16, 5, 16, 5) };
        _lblShareRoot = InfoLabel();
        _lblDbPath = InfoLabel();
        infoLeft.Controls.Add(_lblDbPath);
        infoLeft.Controls.Add(_lblShareRoot);
        topBar.Controls.Add(infoLeft);

        var tip = new ToolTip();
        tip.SetToolTip(_lblShareRoot, _shareRoot);
        tip.SetToolTip(_lblDbPath, _dbPath);

        var kpiBar = new Panel { Dock = DockStyle.Top, Height = 114, BackColor = Theme.Bg, Padding = new Padding(Theme.Gap, Theme.Gap, Theme.Gap, Theme.Gap) };
        var kpiTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, BackColor = Theme.Bg };
        kpiTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        kpiTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        kpiTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 14));
        kpiTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        kpiTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        kpiTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        void AddKpi(string key, string title, Color accent, int col)
        {
            var c = new KpiCard(title, accent) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.Gap, 0) };
            _kpis[key] = c;
            kpiTable.Controls.Add(c, col, 0);
        }
        AddKpi("online", "在线机台", Theme.Success, 0);
        AddKpi("offline", "离线机台", Theme.Danger, 1);
        kpiTable.Controls.Add(new Panel
        {
            Dock = DockStyle.Fill, BackColor = Theme.Border, Margin = new Padding(0, 8, 0, 8),
        }, 2, 0);
        AddKpi("fail", "累计 FAIL", Theme.Primary, 3);
        AddKpi("processed", "已处理文件", Theme.Info, 4);
        _kpis["processed"].Margin = new Padding(0);
        kpiBar.Controls.Add(kpiTable);

        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg,
        };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 480));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _cardsRegion = new RegionCard("机台状态", collapsed =>
        {
            _cardsCollapsed = collapsed;
            split.ColumnStyles[0].Width = collapsed ? 0 : 480;
        });
        _cardFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Theme.Surface,
            FlowDirection = FlowDirection.TopDown, AutoScroll = true,
        };
        _cardFlow.ClientSizeChanged += (_, _) =>
        {
            if (_cards.Count == 0) return;
            var w = Math.Max(180, Math.Max(0, _cardFlow.ClientSize.Width) / 2 - Theme.Gap);
            foreach (var c in _cards.Values) c.SetCardWidth(w);
        };
        _emptyLabel = new Label
        {
            Text = "等待机台上线…（共享目录暂无机台目录）",
            AutoSize = true, ForeColor = Theme.TextFaint, Font = Theme.Body,
            Location = new Point(Theme.Gap + 4, Theme.Gap + 8),
        };
        _cardsRegion.Content.Controls.Add(_cardFlow);
        _cardsRegion.Content.Controls.Add(_emptyLabel);
        _cardsRegion.Dock = DockStyle.Fill;
        split.Controls.Add(_cardsRegion, 0, 0);

        var gridRegion = new RegionCard("FAIL 明细");
        var toolbar = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Theme.Surface };
        toolbar.Paint += (_, e) =>
        {
            using var p = new Pen(Theme.Border);
            e.Graphics.DrawLine(p, 0, toolbar.Height - 1, toolbar.Width, toolbar.Height - 1);
        };
        _filterCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Font = Theme.Small,
            Location = new Point(Theme.Gap, 4),
        };
        _filterCombo.Items.Add("全部机台");
        _filterCombo.SelectedIndex = 0;
        _filterCombo.SelectedIndexChanged += (_, _) =>
        {
            _filterMachine = _filterCombo.SelectedIndex <= 0 ? "" : (string)_filterCombo.SelectedItem!;
            ResetGridFilter();
        };
        _searchBox = new TextBox
        {
            Width = 180, Font = Theme.Small, Location = new Point(Theme.Gap + 160, 4),
            PlaceholderText = "搜索 失败原因 / SN…",
        };
        _searchBox.TextChanged += (_, _) => { _filterSearch = _searchBox.Text.Trim(); ResetGridFilter(); };
        var btnExport = new Button
        {
            Text = "导出 CSV", Width = 86, Height = 28, Font = Theme.Small,
            BackColor = Theme.Surface, ForeColor = Theme.TextMain, FlatStyle = FlatStyle.System,
            Location = new Point(Theme.Gap + 350, 4),
        };
        btnExport.Click += (_, _) => ExportCurrentCsv();
        var btnRefresh = new Button
        {
            Text = "刷新", Width = 60, Height = 28, Font = Theme.Small,
            BackColor = Theme.Surface, ForeColor = Theme.TextMain, FlatStyle = FlatStyle.System,
            Location = new Point(Theme.Gap + 444, 4),
        };
        btnRefresh.Click += (_, _) => RefreshData();
        toolbar.Controls.Add(_filterCombo);
        toolbar.Controls.Add(_searchBox);
        toolbar.Controls.Add(btnExport);
        toolbar.Controls.Add(btnRefresh);
        toolbar.Resize += (_, _) =>
        {
            _filterCombo.Left = Theme.Gap;
            _searchBox.Left = Theme.Gap + 160;
            btnExport.Left = Theme.Gap + 350;
            btnRefresh.Left = Theme.Gap + 444;
        };
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowTemplate = { Height = 24 }, ColumnHeadersHeight = 28,
        };
        BuildColumns();
        _grid.CellDoubleClick += OnGridDoubleClick;
        _emptyGridLabel = new Label
        {
            Dock = DockStyle.Fill, Text = "暂无 FAIL 记录，等待机台推送…",
            TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.TextFaint,
            Font = Theme.Body, BackColor = Theme.Surface, Visible = false,
        };
        gridRegion.Content.Controls.Add(_grid);
        gridRegion.Content.Controls.Add(_emptyGridLabel);
        gridRegion.Content.Controls.Add(toolbar);
        gridRegion.Dock = DockStyle.Fill;
        split.Controls.Add(gridRegion, 1, 0);

        var status = new StatusStrip
        {
            Dock = DockStyle.Bottom, BackColor = Theme.Surface, SizingGrip = false,
            GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(8, 0, 8, 0),
        };
        _statusState = new ToolStripStatusLabel { Text = "监听中", ForeColor = Theme.Success, Font = Theme.Small };
        _statusRefresh = new ToolStripStatusLabel { Text = "上次刷新 --:--:--", ForeColor = Theme.TextSub, Font = Theme.Small };
        _statusHttp = new ToolStripStatusLabel { Text = "HTTP 接收 关", ForeColor = Theme.TextFaint, Font = Theme.Small };
        _statusTimeout = new ToolStripStatusLabel { Text = $"心跳超时 {_heartbeatTimeoutSec} 秒", ForeColor = Theme.TextFaint, Font = Theme.Small };
        status.Items.Add(_statusState);
        status.Items.Add(_statusRefresh);
        status.Items.Add(_statusHttp);
        status.Items.Add(new ToolStripStatusLabel { Spring = true });
        status.Items.Add(_statusTimeout);
        var btnSettings = new ToolStripButton("设置") { DisplayStyle = ToolStripItemDisplayStyle.Text, Font = Theme.Small };
        btnSettings.Click += (_, _) => ShowSettingsDialog();
        status.Items.Add(btnSettings);

        Controls.Add(status);
        Controls.Add(split);
        Controls.Add(kpiBar);
        Controls.Add(topBar);

        _split = split;
    }

    private static Label InfoLabel() => new()
    {
        Dock = DockStyle.Top, Height = 22, Font = Theme.Small, ForeColor = Theme.TextSub,
        TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false,
    };

    private void BuildColumns()
    {
        _grid.Columns.Clear();
        _grid.Columns.Add(Col("时间", 150));
        _grid.Columns.Add(Col("机台", 80));
        _grid.Columns.Add(Col("型号", 90));
        _grid.Columns.Add(Col("SN", 120));
        _grid.Columns.Add(Col("测试日期", 90));
        _grid.Columns.Add(Col("失败原因", 0, fill: true));
        _grid.Columns.Add(Col("测试员", 70));
        _grid.Columns.Add(Col("结果", 60));
    }

    private static DataGridViewTextBoxColumn Col(string header, int width, bool fill = false) => new()
    {
        Name = header, HeaderText = header, Width = width, MinimumWidth = 40,
        AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
        SortMode = DataGridViewColumnSortMode.NotSortable,
    };

    private void OnShown(object? sender, EventArgs e)
    {
        try { _db.Open(); }
        catch (Exception ex)
        {
            Logger.Warning($"聚合库打开失败: {ex.Message}");
            SetState("数据库异常", Theme.Danger);
        }
        try { _watcher.Start(); }
        catch (Exception ex)
        {
            Logger.Warning($"监听启动失败: {ex.Message}");
            SetState("监听异常", Theme.Danger);
        }
        try { _ingest?.Start(); }
        catch (Exception ex)
        {
            Logger.Warning($"HTTP 接收启动异常: {ex.Message}");
            SetState("HTTP 异常", Theme.Danger);
        }
        RefreshData();
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        _watcher.Changed -= OnWatcherChanged;
        _alert.Stop();
        try { _watcher.Stop(); } catch (Exception ex) { Logger.Warning($"看板停止异常: {ex.Message}"); }
        try { _ingest?.Stop(); } catch (Exception ex) { Logger.Warning($"HTTP 接收停止异常: {ex.Message}"); }
        try { _db.Close(); } catch (Exception ex) { Logger.Warning($"聚合库关闭异常: {ex.Message}"); }
        _watcher.Dispose();
        try { _ingest?.Dispose(); } catch { }
        _db.Dispose();
    }

    private void ShowSettingsDialog()
    {
        var cfg = AppConfig.Instance;
        using         var dlg = new Form
        {
            Text = "聚合服务设置",
            Size = new Size(520, 590),
            MinimumSize = new Size(520, 590),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Theme.Bg,
            Font = Theme.Body,
        };

        var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = cfg.AggHttpPort, Width = 300 };
        var token = new TextBox { Text = "", Width = 300, UseSystemPasswordChar = true, PlaceholderText = "留空=不修改；填入新值立即生效" };
        var webhook = new TextBox { Text = cfg.AggWebhookUrl, Width = 300 };
        var summary = new NumericUpDown { Minimum = 1, Maximum = 1440, Value = cfg.AggSummaryMinutes, Width = 300 };
        var shareRoot = new TextBox { Text = cfg.AggShareRoot, Width = 300 };
        var transport = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
        transport.Items.Add("http（机台直推）");
        transport.Items.Add("smb（共享目录）");
        transport.SelectedIndex = cfg.AggTransport.Contains("http", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

        var tipToken = new Label
        {
            Text = cfg.AggToken.Length > 0 ? "当前已配置令牌" : "当前未配置令牌（不限访问）",
            AutoSize = true, ForeColor = Theme.TextFaint, Font = Theme.Small,
        };

        var y = 18;
        void Row(string label, Control c, string hint = "")
        {
            dlg.Controls.Add(new Label
            {
                Text = label, Location = new Point(24, y + 3), AutoSize = true,
                ForeColor = Theme.TextSub, Font = Theme.Body,
            });
            c.Location = new Point(190, y);
            dlg.Controls.Add(c);
            if (hint.Length > 0)
                dlg.Controls.Add(new Label
                {
                    Text = hint, Location = new Point(190, y + 28),
                    AutoSize = true, ForeColor = Theme.TextFaint, Font = Theme.Small,
                });
            y += hint.Length > 0 ? 58 : 42;
        }
        Row("监听端口", port, "改后需重启聚合服务生效");
        Row("访问令牌 (agg_token)", token, "");
        tipToken.Location = new Point(190, y);
        dlg.Controls.Add(tipToken);
        y += 24;
        Row("飞书告警 webhook", webhook, "机台离线告警 + 定时汇总推送目标；留空关闭");
        Row("汇总间隔（分钟）", summary, "");
        Row("共享目录根", shareRoot, "smb 通道机台目录根；http 通道可留空");
        Row("传输通道", transport, "改后需重启聚合服务生效");

        var cardHeart = new CheckBox { Text = "心跳", Checked = cfg.CardShowHeartbeat, AutoSize = true, ForeColor = Theme.TextMain };
        var cardLast = new CheckBox { Text = "最近 FAIL", Checked = cfg.CardShowLastFail, AutoSize = true, ForeColor = Theme.TextMain };
        var cardQueue = new CheckBox { Text = "队列", Checked = cfg.CardShowQueue, AutoSize = true, ForeColor = Theme.TextMain };
        var cardSort = new ComboBox { Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
        cardSort.Items.Add("名称"); cardSort.Items.Add("FAIL 数降序"); cardSort.Items.Add("在线优先");
        cardSort.SelectedIndex = cfg.CardSort == "fail" ? 1 : cfg.CardSort == "online" ? 2 : 0;
        var cardCompact = new CheckBox { Text = "紧凑模式（隐藏副行）", Checked = cfg.CardCompact, AutoSize = true, ForeColor = Theme.TextMain };
        var cardBox = new GroupBox
        {
            Text = "机台卡片显示（自定义）", Location = new Point(24, y), Size = new Size(472, 112),
            BackColor = Theme.Surface, ForeColor = Theme.TextSub, Font = Theme.Small,
        };
        cardHeart.Location = new Point(16, 28); cardLast.Location = new Point(90, 28); cardQueue.Location = new Point(200, 28);
        cardBox.Controls.Add(new Label { Text = "排序", Location = new Point(16, 62), AutoSize = true, ForeColor = Theme.TextSub, Font = Theme.Small });
        cardSort.Location = new Point(60, 58);
        cardCompact.Location = new Point(210, 62);
        cardBox.Controls.Add(cardHeart); cardBox.Controls.Add(cardLast); cardBox.Controls.Add(cardQueue);
        cardBox.Controls.Add(cardSort); cardBox.Controls.Add(cardCompact);
        dlg.Controls.Add(cardBox);
        y += 126;

        var msg = new Label
        {
            Text = "", Location = new Point(24, y + 2), AutoSize = true,
            ForeColor = Theme.Success, Font = Theme.Small,
        };
        dlg.Controls.Add(msg);

        var btnSave = new Button { Text = "保存", Location = new Point(190, y + 24), Size = new Size(100, 30) };
        var btnCancel = new Button { Text = "取消", Location = new Point(300, y + 24), Size = new Size(100, 30) };
        dlg.Controls.Add(btnSave);
        dlg.Controls.Add(btnCancel);

        btnCancel.Click += (_, _) => dlg.Close();
        btnSave.Click += (_, _) =>
        {
            var oldPort = cfg.AggHttpPort;
            var oldTransport = cfg.AggTransport;
            var newToken = token.Text.Trim();
            var newWebhook = webhook.Text.Trim();
            var newSummary = (int)summary.Value;
            var newShare = shareRoot.Text.Trim();
            var newTransport = transport.SelectedIndex == 0 ? "http" : "smb";
            var newPort = (int)port.Value;

            var changed = new List<string>();
            if (newPort != oldPort) { cfg.AggHttpPort = newPort; changed.Add("port"); }
            if (newToken.Length > 0 && newToken != cfg.AggToken) { cfg.AggToken = newToken; changed.Add("token"); }
            if (newWebhook != cfg.AggWebhookUrl) { cfg.AggWebhookUrl = newWebhook; changed.Add("webhook"); }
            if (newSummary != cfg.AggSummaryMinutes) { cfg.AggSummaryMinutes = newSummary; changed.Add("summary"); }
            if (newShare != cfg.AggShareRoot) { cfg.AggShareRoot = newShare; changed.Add("share"); }
            if (newTransport != oldTransport) { cfg.AggTransport = newTransport; changed.Add("transport"); }
            if (cardHeart.Checked != cfg.CardShowHeartbeat) { cfg.CardShowHeartbeat = cardHeart.Checked; changed.Add("card"); }
            if (cardLast.Checked != cfg.CardShowLastFail) { cfg.CardShowLastFail = cardLast.Checked; changed.Add("card"); }
            if (cardQueue.Checked != cfg.CardShowQueue) { cfg.CardShowQueue = cardQueue.Checked; changed.Add("card"); }
            var newSort = cardSort.SelectedIndex == 1 ? "fail" : cardSort.SelectedIndex == 2 ? "online" : "name";
            if (newSort != cfg.CardSort) { cfg.CardSort = newSort; changed.Add("card"); }
            if (cardCompact.Checked != cfg.CardCompact) { cfg.CardCompact = cardCompact.Checked; changed.Add("card"); }

            if (changed.Count == 0) { dlg.Close(); return; }
            if (!cfg.Save())
            {
                msg.ForeColor = Theme.Danger;
                msg.Text = "保存失败（看日志）";
                return;
            }
            ReconfigureServices();
            if (changed.Contains("card")) ApplyCardConfig();
            msg.ForeColor = Theme.Success;
            msg.Text = changed.Contains("port") || changed.Contains("transport")
                ? "已保存。端口/通道变更需重启聚合服务完全生效。"
                : "已保存并生效。";
        };

        Theme.Apply(dlg);
        dlg.ShowDialog(this);
    }

    private void ReconfigureServices()
    {
        var cfg = AppConfig.Instance;
        _alert.Stop();
        _alert = new AggAlertService(_watcher, _db, cfg.AggWebhookUrl, cfg.AggSummaryMinutes);
        _alert.Start();
        if (_ingest != null) { try { _ingest.Stop(); _ingest.Dispose(); } catch { } _ingest = null; }
        var transport = string.IsNullOrEmpty(cfg.AggTransport) ? "smb" : cfg.AggTransport;
        if (transport.Contains("http", StringComparison.OrdinalIgnoreCase))
        {
            _ingest = new HttpIngest(cfg.AggHttpPort, _watcher.IngestFail, _watcher.IngestHeartbeat, cfg.AggToken);
            _ingest.Start();
            _statusHttp.Text = $"HTTP 接收 :{cfg.AggHttpPort}";
            _statusHttp.ForeColor = Theme.Success;
        }
        else
        {
            _statusHttp.Text = "HTTP 接收 关";
            _statusHttp.ForeColor = Theme.TextFaint;
        }
        Logger.Info($"[聚合看板] 设置已应用: transport={transport}, port={cfg.AggHttpPort}, webhook={(cfg.AggWebhookUrl.Length > 0 ? "on" : "off")}");
    }

    private void RefreshData()
    {
        if (_refreshing || IsDisposed || !IsHandleCreated) return;
        _refreshing = true;
        try
        {
            SetState("监听中", Theme.Success);
            var machines = _watcher.GetMachines();
            var fails = _watcher.GetRecentFails(200);
            UpdateInfoBar();
            UpdateCards(machines);
            UpdateGrid(fails);
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            Logger.Warning($"看板刷新失败: {ex.Message}");
            SetState("刷新异常", Theme.Danger);
        }
        finally { _refreshing = false; }
    }

    private void OnWatcherChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(RefreshData)); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void UpdateInfoBar()
    {
        SetText(_lblShareRoot, $"监听目录  {_shareRoot}");
        SetText(_lblDbPath, $"聚合库  {_dbPath}");
        var machines = _watcher.GetMachines();
        var online = machines.Count(m => m.Online);
        var total = _watcher.TotalFails;
        if (_kpis.TryGetValue("online", out var ko)) ko.Set(online.ToString("N0"), $"共 {machines.Count:N0} 台");
        if (_kpis.TryGetValue("offline", out var koff)) koff.Set((machines.Count - online).ToString("N0"), "心跳超时 90s");
        if (_kpis.TryGetValue("fail", out var kf)) kf.Set(total.ToString("N0"), "全部机台");
        if (_kpis.TryGetValue("processed", out var kp)) kp.Set(_watcher.ProcessedFiles.ToString("N0"), "本次运行");
    }

    private void UpdateCards(List<AggMachineStatus> machines)
    {
        _lastMachines = machines;
        var cfg = AppConfig.Instance;
        IEnumerable<AggMachineStatus> sorted = machines;
        if (cfg.CardSort == "fail") sorted = machines.OrderByDescending(m => m.FailCount);
        else if (cfg.CardSort == "online") sorted = machines.OrderByDescending(m => m.Online).ThenBy(m => m.Machine, StringComparer.OrdinalIgnoreCase);
        else sorted = machines.OrderBy(m => m.Machine, StringComparer.OrdinalIgnoreCase);
        var list = sorted.ToList();
        var want = list.Select(m => m.Machine).ToHashSet();

        var flowW = Math.Max(0, _cardFlow.ClientSize.Width);
        var cardW = Math.Max(180, flowW / 2 - Theme.Gap);

        _cardFlow.SuspendLayout();
        foreach (var kv in _cards.ToList())
        {
            if (!want.Contains(kv.Key))
            {
                _cardFlow.Controls.Remove(kv.Value);
                _cards.Remove(kv.Key);
                kv.Value.Dispose();
            }
        }
        foreach (var m in list)
        {
            if (!_cards.TryGetValue(m.Machine, out var card))
            {
                card = new MachineCard(this, m.Machine);
                _cards[m.Machine] = card;
                _cardFlow.Controls.Add(card);
            }
            card.SetCardWidth(cardW);
            card.ApplyConfig(cfg.CardShowHeartbeat, cfg.CardShowLastFail, cfg.CardShowQueue, cfg.CardCompact);
            card.Update(m);
        }
        _cardFlow.ResumeLayout();
        _cardFlow.AutoScrollMinSize = new Size(0,
            _cardFlow.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Bottom) + 4);

        _emptyLabel.Visible = list.Count == 0;
        _cardsRegion?.SetTitle($"机台状态 {list.Count} 台");
        UpdateFilterCombo();
    }

    private void ApplyCardConfig()
    {
        if (_lastMachines.Count == 0) return;
        UpdateCards(_lastMachines);
    }

    private void UpdateGrid(List<AggFailRow> fails)
    {
        IEnumerable<AggFailRow> src = fails;
        if (_filterMachine.Length > 0) src = src.Where(f => f.Machine == _filterMachine);
        if (_filterSearch.Length > 0)
            src = src.Where(f => (f.FailReason ?? "").Contains(_filterSearch, StringComparison.OrdinalIgnoreCase)
                              || (f.Sn ?? "").Contains(_filterSearch, StringComparison.OrdinalIgnoreCase));
        var list = src.ToList();
        long sig = list.Count * 100003L + (list.Count > 0 ? list[0].Id : 0)
                 + _filterMachine.Length * 1009L + _filterSearch.Length;
        if (sig == _lastGridSignature) return;
        _lastGridSignature = sig;
        _gridFails = list;

        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (var f in list)
        {
            var row = _grid.Rows[_grid.Rows.Add()];
            row.Cells[0].Value = f.IngestTs ?? "";
            row.Cells[1].Value = f.Machine ?? "";
            row.Cells[2].Value = f.Model ?? "";
            row.Cells[3].Value = f.Sn ?? "";
            row.Cells[4].Value = f.TestDate ?? "";
            row.Cells[5].Value = f.FailReason ?? "";
            row.Cells[5].ToolTipText = f.FailReason ?? "";
            row.Cells[6].Value = f.Tester ?? "";
            row.Cells[7].Value = f.Result ?? "";
        }
        _grid.ResumeLayout();
        _emptyGridLabel.Visible = list.Count == 0;
    }

    private void ResetGridFilter()
    {
        _lastGridSignature = -1;
        RefreshData();
    }

    private void UpdateFilterCombo()
    {
        if (_filterCombo == null || _filterCombo.IsDisposed) return;
        var keep = _filterCombo.SelectedIndex > 0 ? (string)_filterCombo.SelectedItem! : "";
        _filterCombo.Items.Clear();
        _filterCombo.Items.Add("全部机台");
        foreach (var m in _lastMachines.Select(x => x.Machine).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            _filterCombo.Items.Add(m);
        _filterCombo.SelectedIndex = keep.Length > 0 ? Math.Max(0, _filterCombo.Items.IndexOf(keep)) : 0;
    }

    private void ExportCurrentCsv()
    {
        if (_gridFails.Count == 0)
        {
            MessageBox.Show(this, "当前筛选无 FAIL 记录", "导出", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            using var dlg = new SaveFileDialog
            {
                FileName = $"fail-filtered-{DateTime.Now:yyyyMMdd-HHmm}.csv",
                Filter = "CSV 文件 (*.csv)|*.csv",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var sb = new StringBuilder();
            sb.AppendLine("时间,机台,型号,SN,测试日期,失败原因,测试员,结果");
            foreach (var f in _gridFails)
                sb.AppendLine(string.Join(",", new[] { Csv(f.IngestTs), Csv(f.Machine), Csv(f.Model), Csv(f.Sn), Csv(f.TestDate), Csv(f.FailReason), Csv(f.Tester), Csv(f.Result) }));
            File.WriteAllText(dlg.FileName, "\uFEFF" + sb.ToString(), Encoding.UTF8);
            MessageBox.Show(this, $"已导出 {_gridFails.Count} 条：{dlg.FileName}", "导出", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { Logger.Warning($"[聚合看板] 导出筛选结果失败: {ex.Message}"); }
    }

    private void UpdateStatusBar()
    {
        _statusRefresh.Text = $"上次刷新 {DateTime.Now:HH:mm:ss}";
        UpdateHttpStatus();
    }

    private void UpdateHttpStatus()
    {
        if (_ingest == null)
        {
            _statusHttp.Text = "HTTP 接收 关";
            _statusHttp.ForeColor = Theme.TextFaint;
            return;
        }
        if (_ingest.Listening)
        {
            _statusHttp.Text = $"HTTP 监听 :{_httpPort} ✓ 已收 {_ingest.ReceivedCount:N0} 条";
            _statusHttp.ForeColor = Theme.Success;
        }
        else
        {
            _statusHttp.Text = $"HTTP 监听失败 :{_httpPort}";
            _statusHttp.ForeColor = Theme.Danger;
        }
    }

    private void SetState(string text, Color color)
    {
        _statusState.Text = text;
        _statusState.ForeColor = color;
    }

    private static void SetText(Label l, string text)
    {
        if (l.Text != text) l.Text = text;
    }

    private void OnGridDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _gridFails.Count) return;
        ShowFailDetail(_gridFails[e.RowIndex]);
    }

    private void ShowFailDetail(AggFailRow f)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"时间        {f.IngestTs}");
        sb.AppendLine($"机台        {f.Machine}");
        sb.AppendLine($"序号        {f.Seq}");
        sb.AppendLine($"类型        {f.Type}");
        sb.AppendLine($"结果        {f.Result}");
        sb.AppendLine($"工位        {f.StationId}");
        sb.AppendLine($"型号        {f.Model}");
        sb.AppendLine($"类别        {f.Category}");
        sb.AppendLine($"测试日期    {f.TestDate}");
        sb.AppendLine($"SN          {f.Sn}");
        sb.AppendLine($"测试员      {f.Tester}");
        sb.AppendLine($"面板状态    {f.PanelStatus}");
        sb.AppendLine($"批次时间戳  {f.BatchTimestamp}");
        sb.AppendLine($"文件大小    {f.FileSize} 字节");
        sb.AppendLine($"含 FAIL 项  {f.HasFailItems}");
        sb.AppendLine($"XML 路径    {f.XmlPath}");
        sb.AppendLine();
        sb.AppendLine("失败原因：");
        sb.AppendLine(f.FailReason ?? "(无)");

        using var dlg = new Form
        {
            Text = $"FAIL 明细 — {f.Machine} #{f.Seq}",
            Size = new Size(560, 480), MinimumSize = new Size(420, 300),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.Bg, Font = Theme.Body,
        };
        dlg.Controls.Add(new TextBox
        {
            Multiline = true, ReadOnly = true, WordWrap = true,
            ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(8),
            BackColor = Theme.Surface, ForeColor = Theme.TextMain, Font = Theme.Mono,
            Text = sb.ToString(),
        });
        dlg.ShowDialog(this);
    }

    private static void CopyMachineName(string machine)
    {
        try { Clipboard.SetText(machine); } catch { }
    }

    private void ExportMachineCsv(string machine)
    {
        try
        {
            var rows = _db.QueryFails(2000, machine);
            if (rows.Count == 0)
            {
                MessageBox.Show(this, "该机台暂无 FAIL 记录", "导出", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var dlg = new SaveFileDialog
            {
                FileName = $"fail-{machine}-{DateTime.Now:yyyyMMdd-HHmm}.csv",
                Filter = "CSV 文件 (*.csv)|*.csv",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var sb = new StringBuilder();
            sb.AppendLine("时间,机台,型号,SN,测试日期,失败原因,测试员,结果");
            foreach (var f in rows)
                sb.AppendLine(string.Join(",", new[] { Csv(f.IngestTs), Csv(f.Machine), Csv(f.Model), Csv(f.Sn), Csv(f.TestDate), Csv(f.FailReason), Csv(f.Tester), Csv(f.Result) }));
            File.WriteAllText(dlg.FileName, "\uFEFF" + sb.ToString(), Encoding.UTF8);
            MessageBox.Show(this, $"已导出 {rows.Count} 条：{dlg.FileName}", "导出", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { Logger.Warning($"[聚合看板] 导出 {machine} FAIL 失败: {ex.Message}"); }
    }

    private static string Csv(string? s)
    {
        s ??= "";
        if (s.StartsWith("=") || s.StartsWith("+") || s.StartsWith("-") || s.StartsWith("@") || s.StartsWith("\t") || s.StartsWith("\r"))
            s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private void RefreshOneCard(string machine)
    {
        var s = _lastMachines.FirstOrDefault(x => x.Machine == machine);
        if (s != null && _cards.TryGetValue(machine, out var card)) card.Update(s);
    }

    private sealed class MachineCard : Panel
    {
        private readonly Label _name;
        private readonly Label _lamp;
        private readonly Label _status;
        private readonly Label _lblHeartbeat;
        private readonly Label _lblFailCount;
        private readonly Label _lblLastFail;
        private readonly Label _lblQueue;
        private Color _accent = Theme.Success;
        private readonly AggCenterForm _owner;

        public MachineCard(AggCenterForm owner, string machine)
        {
            _owner = owner;
            Height = 150;
            Margin = new Padding(0, 0, Theme.Gap, Theme.Gap);
            BackColor = Theme.Surface;
            Padding = new Padding(12, 8, 12, 6);

            var header = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Theme.Surface };
            _name = new Label
            {
                Text = machine, Dock = DockStyle.Fill, Font = Theme.BodyBold,
                ForeColor = Theme.TextMain, TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true, UseMnemonic = false,
            };
            _status = new Label
            {
                Dock = DockStyle.Right, Width = 88, Font = Theme.Small,
                ForeColor = Theme.TextSub, TextAlign = ContentAlignment.MiddleRight,
                Text = "在线", UseMnemonic = false,
            };
            _lamp = new Label
            {
                Dock = DockStyle.Left, Width = 20, Text = "●", Font = new Font(Theme.Body.FontFamily, 11F, FontStyle.Bold),
                ForeColor = Theme.Success, TextAlign = ContentAlignment.MiddleCenter,
            };
            header.Controls.Add(_name);
            header.Controls.Add(_status);
            header.Controls.Add(_lamp);

            _lblHeartbeat = RowLabel();
            _lblFailCount = new Label
            {
                Dock = DockStyle.Top, Height = 26, Font = Theme.NumberSmall,
                ForeColor = Theme.Primary, TextAlign = ContentAlignment.MiddleLeft,
                Text = "累计 FAIL  0", UseMnemonic = false,
            };
            _lblLastFail = RowLabel();
            _lblQueue = RowLabel();

            Controls.Add(_lblQueue);
            Controls.Add(_lblLastFail);
            Controls.Add(_lblFailCount);
            Controls.Add(_lblHeartbeat);
            Controls.Add(header);

            var menu = new ContextMenuStrip();
            menu.Items.Add("复制机台名", null, (_, _) => AggCenterForm.CopyMachineName(machine));
            menu.Items.Add("导出该机台 FAIL (CSV)", null, (_, _) => _owner.ExportMachineCsv(machine));
            menu.Items.Add("刷新本卡", null, (_, _) => _owner.RefreshOneCard(machine));
            ContextMenuStrip = menu;
        }

        public void ApplyConfig(bool showHeartbeat, bool showLastFail, bool showQueue, bool compact)
        {
            _lblHeartbeat.Visible = showHeartbeat;
            _lblLastFail.Visible = showLastFail;
            _lblQueue.Visible = showQueue;
            var h = compact ? 112 : 150;
            if (Height != h) Height = h;
        }

        private static Label RowLabel() => new()
        {
            Dock = DockStyle.Top, Height = 20, Font = Theme.Small,
            ForeColor = Theme.TextSub, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false,
        };

        public void SetCardWidth(int width)
        {
            if (Width != width)
            {
                Width = width;
                Invalidate();
            }
        }

        public void Update(AggMachineStatus s)
        {
            var online = s.Online;
            SetText(_lblHeartbeat, online ? $"心跳 {TimePart(s.LastHeartbeat)}" : OfflineText(s));
            SetText(_lblFailCount, $"累计 FAIL  {s.FailCount:N0}");
            _lblFailCount.ForeColor = s.FailCount > 0 ? Theme.Danger : Theme.TextSub;
            SetText(_lblLastFail, $"最近 FAIL  {(string.IsNullOrEmpty(s.LastFailAt) ? "—" : s.LastFailAt)}");
            SetText(_lblQueue, $"待推队列  {s.Queued:N0}");

            var color = online ? Theme.Success : Theme.Danger;
            if (_lamp.ForeColor != color) _lamp.ForeColor = color;
            SetText(_status, online ? "在线" : "离线");
            if (_status.ForeColor != color) _status.ForeColor = color;
            if (_accent != color) { _accent = color; Invalidate(); }
        }

        private static string TimePart(string ts)
        {
            return !string.IsNullOrEmpty(ts) && ts.Length >= 19 ? ts[11..] : ts;
        }

        private static string OfflineText(AggMachineStatus s)
        {
            if (string.IsNullOrEmpty(s.LastHeartbeat)) return "离线 —";
            if (!DateTime.TryParseExact(s.LastHeartbeat, "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                return "离线";
            var sec = (long)Math.Max(0, (DateTime.Now - t).TotalSeconds);
            if (sec < 60) return $"离线 {sec} 秒";
            if (sec < 3600) return $"离线 {sec / 60} 分钟";
            if (sec < 86400) return $"离线 {sec / 3600} 小时 {sec % 3600 / 60} 分钟";
            return $"离线 {sec / 86400} 天";
        }

        private static void SetText(Label l, string text)
        {
            if (l.Text != text) l.Text = text;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Theme.DrawCard(e.Graphics, ClientRectangle, _accent);
        }
    }

    private sealed class RegionCard : Panel
    {
        public Panel Content { get; }
        private readonly Label _title;
        private readonly Label _fold;
        private readonly Action<bool>? _onFold;
        private bool _collapsed;

        public RegionCard(string title, Action<bool>? onFold = null)
        {
            _onFold = onFold;
            BackColor = Theme.Bg;

            var bar = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Theme.SurfaceHi };
            bar.Paint += (_, e) =>
            {
                using var p = new Pen(Theme.Border);
                e.Graphics.DrawLine(p, 0, bar.Height - 1, bar.Width, bar.Height - 1);
            };
            _title = new Label
            {
                Text = title, Dock = DockStyle.Fill, Font = Theme.BodyBold,
                ForeColor = Theme.TextMain, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(Theme.Gap + 4, 0, 0, 0), UseMnemonic = false,
            };
            _fold = new Label
            {
                Text = "▼", Dock = DockStyle.Right, Width = 36, Font = Theme.BodyBold,
                ForeColor = Theme.TextSub, TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
            };
            _fold.Click += (_, _) => Toggle();
            bar.Controls.Add(_title);
            bar.Controls.Add(_fold);

            Content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(Theme.Gap, Theme.Gap, Theme.Gap, 0) };
            Controls.Add(Content);
            Controls.Add(bar);
            Paint += DrawRegionBorder;
        }

        public void SetTitle(string t) { if (_title.Text != t) _title.Text = t; }

        private void Toggle()
        {
            _collapsed = !_collapsed;
            Content.Visible = !_collapsed;
            _fold.Text = _collapsed ? "▶" : "▼";
            _onFold?.Invoke(_collapsed);
        }

        private static void DrawRegionBorder(object? sender, PaintEventArgs e)
        {
            var c = sender as RegionCard;
            if (c == null || c.Width <= 1 || c.Height <= 1) return;
            using var p = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(p, 0, 0, c.Width - 1, c.Height - 1);
        }
    }
}

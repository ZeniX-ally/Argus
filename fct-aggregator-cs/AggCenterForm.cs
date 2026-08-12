using System.Globalization;
using System.Text;

namespace FctAggregator;

/// <summary>
/// 聚合端看板窗体（Phase 2）。
///
/// 部署在产线共享目录宿主机，通过 <c>Argus.exe agg</c> 子命令启动：
/// 监听每台机推送的 fail-*.json / heartbeat.json，合并入库，
/// 并在一张窗体内实时展示多机台状态卡片 + FAIL 明细。
///
/// 数据流：<see cref="AggWatcher"/>（后台线程扫描 + 心跳监控）→ Changed 事件
/// （跨线程 BeginInvoke 进 UI 线程）→ 3 秒定时 + 事件驱动的双重刷新。
/// 卡片与表格都做增量更新（不重建已存在的卡片、行数没变不重绑），防闪烁。
/// </summary>
public class AggCenterForm : Form
{
    private readonly string _shareRoot;
    private readonly string _dbPath;
    private readonly int _heartbeatTimeoutSec;
    private readonly int _httpPort;
    private readonly AggDatabase _db;
    private readonly AggWatcher _watcher;
    private readonly HttpIngest? _ingest;   // agg_transport 含 http 才创建，纯 smb 部署为 null
    private readonly System.Windows.Forms.Timer _timer = new();
    private SplitContainer _split = null!;

    // ===== 顶部信息条 =====
    private Label _lblShareRoot = null!;
    private Label _lblDbPath = null!;
    private Label _lblTotalFails = null!;
    private Label _lblProcessed = null!;

    // ===== 机台卡片区 =====
    private FlowLayoutPanel _cardFlow = null!;
    private Label _emptyLabel = null!;
    private readonly Dictionary<string, MachineCard> _cards = new();

    // ===== FAIL 明细 =====
    private DataGridView _grid = null!;
    private Label _emptyGridLabel = null!;
    private List<AggFailRow> _gridFails = new();

    // ===== 底部状态栏 =====
    private ToolStripStatusLabel _statusState = null!;
    private ToolStripStatusLabel _statusRefresh = null!;
    private ToolStripStatusLabel _statusHttp = null!;
    private ToolStripStatusLabel _statusTimeout = null!;

    /// <summary>最近一次 FAIL 明细的绑定签名（行数 + 最新行 Id），没变就不重绑表格。</summary>
    private long _lastGridSignature = -1;
    private bool _refreshing;

    public AggCenterForm(string shareRoot, string dbPath, int heartbeatTimeoutSec = 90)
    {
        _shareRoot = shareRoot;
        _dbPath = dbPath;
        _heartbeatTimeoutSec = heartbeatTimeoutSec;

        // 聚合库目录不存在则先建（共享目录晚于库挂载也能正常开窗）
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _db = new AggDatabase(dbPath);
        _watcher = new AggWatcher(shareRoot, _db, heartbeatTimeoutSec);
        _watcher.Changed += OnWatcherChanged;   // 后台线程事件，跨线程进 UI

        // HTTP 接收（B2 双通道）：agg_transport 含 http（http/both）才启用，否则保持纯 smb 行为
        var cfg = AppConfig.Instance;
        _httpPort = cfg.AggHttpPort;
        var transport = string.IsNullOrEmpty(cfg.AggTransport) ? "smb" : cfg.AggTransport;
        if (transport.Contains("http", StringComparison.OrdinalIgnoreCase))
        {
            _ingest = new HttpIngest(_httpPort, _watcher.IngestFail, _watcher.IngestHeartbeat);
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
        Theme.Apply(this);   // 与主程序同一套视觉规范；卡片是动态建的，自带上主题色

        _timer.Interval = 3000;
        _timer.Tick += (_, _) => RefreshData();
        _timer.Start();

        Shown += OnShown;
        FormClosed += OnFormClosed;
    }

    // ==================================================================
    //  布局
    // ==================================================================

    /// <summary>
    /// 三层结构：顶部信息条（监听目录/聚合库/累计 FAIL/已处理文件）→
    /// 中部上下分栏（上：机台卡片横向流式区，下：FAIL 明细表）→ 底部状态栏。
    /// </summary>
    private void BuildUi()
    {
        // ===== 顶部信息条 =====
        var topBar = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Theme.Surface };
        topBar.Paint += (_, e) =>
        {
            using var p = new Pen(Theme.Border);
            e.Graphics.DrawLine(p, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
        };
        var infoLeft = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(16, 7, 16, 7) };
        _lblShareRoot = InfoLabel();
        _lblDbPath = InfoLabel();
        infoLeft.Controls.Add(_lblDbPath);       // 先加的先停靠 → 在下面
        infoLeft.Controls.Add(_lblShareRoot);

        var infoRight = new Panel { Dock = DockStyle.Right, Width = 300, BackColor = Theme.Surface, Padding = new Padding(8, 7, 16, 7) };
        _lblTotalFails = new Label
        {
            Dock = DockStyle.Top, Height = 26, Font = Theme.NumberSmall,
            ForeColor = Theme.TextSub, TextAlign = ContentAlignment.MiddleRight, Text = "累计 FAIL  0",
        };
        _lblProcessed = new Label
        {
            Dock = DockStyle.Top, Height = 20, Font = Theme.Small,
            ForeColor = Theme.TextFaint, TextAlign = ContentAlignment.MiddleRight, Text = "已处理 0 个文件",
        };
        infoRight.Controls.Add(_lblProcessed);
        infoRight.Controls.Add(_lblTotalFails);

        topBar.Controls.Add(infoLeft);           // Fill 先加 = 最后停靠 = 吃剩余
        topBar.Controls.Add(infoRight);

        // 长路径鼠标悬停看全名
        var tip = new ToolTip();
        tip.SetToolTip(_lblShareRoot, _shareRoot);
        tip.SetToolTip(_lblDbPath, _dbPath);

        // ===== 中部：上卡片区 / 下 FAIL 明细 =====
        _split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
            Panel1MinSize = 150, Panel2MinSize = 130,
        };
        _split.Panel1.BackColor = Theme.Bg;
        _split.Panel2.BackColor = Theme.Bg;

        // -- 机台卡片区（横向流式 + 自动横向滚动，机台多时滚动条兜底）--
        var cardsHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.Gap) };
        _cardFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Theme.Bg,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true,
        };
        _emptyLabel = new Label
        {
            Text = "等待机台上线…（共享目录暂无机台目录）",
            AutoSize = true, ForeColor = Theme.TextFaint, Font = Theme.Body,
            Location = new Point(Theme.Gap + 4, Theme.Gap + 8),
        };
        cardsHost.Controls.Add(_cardFlow);
        cardsHost.Controls.Add(_emptyLabel);
        _split.Panel1.Controls.Add(cardsHost);

        // -- FAIL 明细表 --
        var gridHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.Gap) };
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
            Font = Theme.Body, BackColor = Theme.Bg, Visible = false,
        };
        gridHost.Controls.Add(_grid);
        gridHost.Controls.Add(_emptyGridLabel);   // 后加的在上层：无数据时盖住表格
        _split.Panel2.Controls.Add(gridHost);

        // ===== 底部状态栏 =====
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
        status.Items.Add(new ToolStripStatusLabel { Spring = true });   // 弹簧把右侧信息推到最右
        status.Items.Add(_statusTimeout);

        // 停靠顺序：后 Add 的先停靠 —— 状态栏最先停靠(底部)，split 吃剩余，topBar 顶
        Controls.Add(topBar);
        Controls.Add(_split);
        Controls.Add(status);
    }

    private static Label InfoLabel() => new()
    {
        Dock = DockStyle.Top, Height = 24, Font = Theme.Small, ForeColor = Theme.TextSub,
        TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false,
    };

    /// <summary>FAIL 明细列：时间/机台/型号/SN/测试日期/失败原因/测试员/结果。</summary>
    private void BuildColumns()
    {
        _grid.Columns.Clear();
        _grid.Columns.Add(Col("时间", 150));
        _grid.Columns.Add(Col("机台", 80));
        _grid.Columns.Add(Col("型号", 90));
        _grid.Columns.Add(Col("SN", 120));
        _grid.Columns.Add(Col("测试日期", 90));
        _grid.Columns.Add(Col("失败原因", 0, fill: true));   // 唯一自适应列，其余固定宽
        _grid.Columns.Add(Col("测试员", 70));
        _grid.Columns.Add(Col("结果", 60));
    }

    private static DataGridViewTextBoxColumn Col(string header, int width, bool fill = false) => new()
    {
        Name = header, HeaderText = header, Width = width, MinimumWidth = 40,
        AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
        SortMode = DataGridViewColumnSortMode.NotSortable,
    };

    // ==================================================================
    //  生命周期：Shown 启动 / FormClosed 收尾（不泄漏后台线程）
    // ==================================================================

    private void OnShown(object? sender, EventArgs e)
    {
        // SplitterDistance 必须等布局完成后再设（构造期设置会因控件尺寸为 0 抛异常）
        try { _split.SplitterDistance = Math.Min(205, Math.Max(_split.Panel1MinSize, _split.Height - _split.Panel2MinSize - 20)); }
        catch (Exception ex) { Logger.Warning($"分隔条初始化失败: {ex.Message}"); }
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
        // 队列里还有文件在写也直接停：简单可靠优先，不弹确认
        try { _watcher.Stop(); } catch (Exception ex) { Logger.Warning($"看板停止异常: {ex.Message}"); }
        try { _ingest?.Stop(); } catch (Exception ex) { Logger.Warning($"HTTP 接收停止异常: {ex.Message}"); }
        try { _db.Close(); } catch (Exception ex) { Logger.Warning($"聚合库关闭异常: {ex.Message}"); }
        _watcher.Dispose();
        try { _ingest?.Dispose(); } catch { }
        _db.Dispose();
    }

    // ==================================================================
    //  刷新
    // ==================================================================

    /// <summary>
    /// 一次完整刷新：信息条 → 卡片（增量）→ 表格（签名没变不重绑）→ 状态栏。
    /// 只在 UI 线程执行（定时器 Tick / Changed 事件 BeginInvoke 都落到这里）。
    /// </summary>
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

    /// <summary>AggWatcher 后台线程事件：BeginInvoke 转到 UI 线程立即刷一次。
    /// 风暴场景多个事件排进队列也无妨——每次刷新都是增量比对，开销小。</summary>
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
        var total = _watcher.TotalFails;
        SetText(_lblTotalFails, $"累计 FAIL  {total:N0}");
        _lblTotalFails.ForeColor = total > 0 ? Theme.Danger : Theme.TextSub;
        SetText(_lblProcessed, $"已处理 {_watcher.ProcessedFiles:N0} 个文件");
    }

    /// <summary>
    /// 机台卡片增量更新：按机台名缓存卡片，新增的才建、消失的移除、
    /// 剩下的只改文字颜色（SuspendLayout/ResumeLayout 包住增删，防闪烁）。
    /// </summary>
    private void UpdateCards(List<AggMachineStatus> machines)
    {
        var sorted = machines.OrderBy(m => m.Machine, StringComparer.OrdinalIgnoreCase).ToList();
        var want = sorted.Select(m => m.Machine).ToHashSet();

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
        foreach (var m in sorted)
        {
            if (!_cards.TryGetValue(m.Machine, out var card))
            {
                card = new MachineCard(m.Machine);
                _cards[m.Machine] = card;
                _cardFlow.Controls.Add(card);
            }
            card.Update(m);
        }
        _cardFlow.ResumeLayout();

        _emptyLabel.Visible = sorted.Count == 0;
    }

    /// <summary>FAIL 明细：签名（行数 + 最新行 Id）没变不重绑，避免每 3 秒整表重建。</summary>
    private void UpdateGrid(List<AggFailRow> fails)
    {
        long sig = fails.Count * 100003L + (fails.Count > 0 ? fails[0].Id : 0);
        if (sig == _lastGridSignature) return;
        _lastGridSignature = sig;
        _gridFails = fails;

        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (var f in fails)
        {
            var row = _grid.Rows[_grid.Rows.Add()];
            row.Cells[0].Value = f.IngestTs ?? "";
            row.Cells[1].Value = f.Machine ?? "";
            row.Cells[2].Value = f.Model ?? "";
            row.Cells[3].Value = f.Sn ?? "";
            row.Cells[4].Value = f.TestDate ?? "";
            row.Cells[5].Value = f.FailReason ?? "";
            row.Cells[5].ToolTipText = f.FailReason ?? "";   // 原因太长时悬停看全文
            row.Cells[6].Value = f.Tester ?? "";
            row.Cells[7].Value = f.Result ?? "";
        }
        _grid.ResumeLayout();
        _emptyGridLabel.Visible = fails.Count == 0;
    }

    private void UpdateStatusBar()
    {
        _statusRefresh.Text = $"上次刷新 {DateTime.Now:HH:mm:ss}";
        UpdateHttpStatus();
    }

    /// <summary>HTTP 接收状态：未启用（灰）/ 监听中带已收条数（绿）/ 启动失败（红）。</summary>
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

    // ==================================================================
    //  交互：双击明细弹详情
    // ==================================================================

    private void OnGridDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _gridFails.Count) return;
        ShowFailDetail(_gridFails[e.RowIndex]);
    }

    /// <summary>双击一行：弹简单只读窗体，展示完整失败原因 + 全部字段。</summary>
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

    // ==================================================================
    //  机台卡片（自绘圆角白卡，只在状态变化时改文字，防闪烁）
    // ==================================================================

    /// <summary>一台机一张小卡（200x124）：机台名 + 状态灯 + 心跳 + FAIL 数 + 队列。</summary>
    private sealed class MachineCard : Panel
    {
        private readonly Label _name;
        private readonly Label _lamp;
        private readonly Label _status;
        private readonly Label _lblHeartbeat;
        private readonly Label _lblFailCount;
        private readonly Label _lblLastFail;
        private readonly Label _lblQueue;
        private Color _accent = Theme.Success;   // 卡片左侧强调条颜色 = 在线/离线

        public MachineCard(string machine)
        {
            Size = new Size(200, 124);
            Margin = new Padding(0, 0, Theme.Gap, Theme.Gap);
            BackColor = Theme.Surface;
            Padding = new Padding(10, 6, 10, 4);

            // 头行：机台名（左）+ 状态文字 + 状态灯（右）
            var header = new Panel { Dock = DockStyle.Top, Height = 24, BackColor = Theme.Surface };
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
                Dock = DockStyle.Right, Width = 18, Text = "●", Font = Theme.BodyBold,
                ForeColor = Theme.Success, TextAlign = ContentAlignment.MiddleCenter,
            };
            header.Controls.Add(_name);     // Fill 先加
            header.Controls.Add(_status);
            header.Controls.Add(_lamp);     // 后加 = 最右

            _lblHeartbeat = RowLabel();
            _lblFailCount = new Label
            {
                Dock = DockStyle.Top, Height = 26, Font = Theme.NumberSmall,
                ForeColor = Theme.TextSub, TextAlign = ContentAlignment.MiddleLeft,
                Text = "累计 FAIL  0", UseMnemonic = false,
            };
            _lblLastFail = RowLabel();
            _lblQueue = RowLabel();

            // 后 Add 的先停靠：头行必须最后加，才在最上面
            Controls.Add(_lblQueue);
            Controls.Add(_lblLastFail);
            Controls.Add(_lblFailCount);
            Controls.Add(_lblHeartbeat);
            Controls.Add(header);
        }

        private static Label RowLabel() => new()
        {
            Dock = DockStyle.Top, Height = 20, Font = Theme.Small,
            ForeColor = Theme.TextSub, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false,
        };

        /// <summary>状态变化才改文字/颜色；强调条变了才重绘卡片。</summary>
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

        /// <summary>离线时长文案：秒 → 分钟 → 小时 → 天。</summary>
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
            Theme.DrawCard(e.Graphics, ClientRectangle, _accent);   // 圆角白卡 + 状态色强调条
        }
    }
}

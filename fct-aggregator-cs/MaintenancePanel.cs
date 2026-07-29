namespace FctAggregator;

/// <summary>
/// 维修记录页。两种视图:
/// 看板(默认, 5 列可拖动改状态) / 列表(表格 + 状态筛选)。
/// 功能: 新增 / 编辑 / 改状态 / 删除 / 导出 xlsx·csv。
/// </summary>
public class MaintenancePanel : Panel
{
    private const int ListLimit = 500;

    private readonly Engine _engine;
    private MaintenanceBoard _board = null!;
    private TodoPanel _todo = null!;
    private ListView _list = null!;
    private Panel _listHost = null!;
    private ComboBox _filter = null!;
    private Label _filterLabel = null!;
    private Button _btnBoard = null!;
    private Button _btnList = null!;
    private Button _btnTodo = null!;
    private bool _boardMode = true;
    private List<MaintenanceRecord> _current = new();   // 列表视图当前内容(供导出)

    public MaintenancePanel(Engine engine)
    {
        _engine = engine;
        BuildUi();
        ApplyViewMode();
    }

    private void BuildUi()
    {
        Padding = new Padding(12);

        var title = new Label
        {
            Text = "维修记录", Dock = DockStyle.Top, Height = 30,
            Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold),
        };

        // ===== 顶部工具栏 =====
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(0, 4, 0, 4) };

        _btnBoard = new Button { Text = "看板", Width = 62, Height = 28, FlatStyle = FlatStyle.Flat };
        _btnBoard.Click += (_, _) => SetViewMode("board");
        _btnList = new Button { Text = "列表", Width = 62, Height = 28, FlatStyle = FlatStyle.Flat };
        _btnList.Click += (_, _) => SetViewMode("list");
        _btnTodo = new Button { Text = "待办", Width = 62, Height = 28, FlatStyle = FlatStyle.Flat };
        _btnTodo.Click += (_, _) => SetViewMode("todo");
        bar.Controls.Add(_btnBoard);
        bar.Controls.Add(_btnList);
        bar.Controls.Add(_btnTodo);

        _filterLabel = new Label
        {
            Text = "筛选:", AutoSize = true, Anchor = AnchorStyles.Left,
            Margin = new Padding(12, 8, 4, 0),
        };
        bar.Controls.Add(_filterLabel);
        _filter = new ComboBox { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
        _filter.Items.AddRange(MaintenanceMeta.FilterItems());
        _filter.SelectedIndex = 0;
        _filter.SelectedIndexChanged += (_, _) => { if (!_boardMode) RefreshList(); };
        bar.Controls.Add(_filter);

        var btnFromFail = new Button
        {
            Text = "从FAIL选故障项", Width = 122, Height = 28,
            Margin = new Padding(12, 3, 3, 3),
            BackColor = Color.FromArgb(226, 240, 217),
        };
        btnFromFail.Click += (_, _) => NewFromFailItems();
        bar.Controls.Add(btnFromFail);

        var btnNew = new Button { Text = "新增记录", Width = 90, Height = 28, Margin = new Padding(3, 3, 3, 3) };
        btnNew.Click += (_, _) => NewRecord();
        bar.Controls.Add(btnNew);
        var btnRefresh = new Button { Text = "刷新", Width = 70, Height = 28 };
        btnRefresh.Click += (_, _) => Refresh2();
        bar.Controls.Add(btnRefresh);
        var btnExport = new Button { Text = "导出", Width = 80, Height = 28 };
        btnExport.Click += (s, _) => ShowExportMenu(btnExport);
        bar.Controls.Add(btnExport);

        // ===== 看板 =====
        _board = new MaintenanceBoard(_engine) { Dock = DockStyle.Fill, Visible = true };
        _board.EditRequested += EditRecord;
        _board.ContextRequested += (m, anchor, at) => BuildRecordMenu(m).Show(anchor, at);
        _board.Changed += () => { if (_viewMode == "list") RefreshList(); };

        // ===== 列表 =====
        _listHost = new Panel { Dock = DockStyle.Fill, Visible = false };
        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true,
        };
        _list.Columns.Add("ID", 50);
        _list.Columns.Add("故障项目", 240);
        _list.Columns.Add("故障描述", 180);
        _list.Columns.Add("严重度", 66);
        _list.Columns.Add("状态", 80);
        _list.Columns.Add("维修人", 76);
        _list.Columns.Add("记录日期", 132);
        _list.Columns.Add("最后更新", 132);
        _list.MouseClick += List_MouseClick;
        _list.MouseDoubleClick += List_MouseDoubleClick;
        _listHost.Controls.Add(_list);

        // ===== 待办 =====
        _todo = new TodoPanel(_engine) { Dock = DockStyle.Fill, Visible = false };

        Controls.Add(_board);
        Controls.Add(_listHost);
        Controls.Add(_todo);
        Controls.Add(bar);
        Controls.Add(title);
    }

    private string _viewMode = "board";

    // ==================== 视图切换 ====================
    private void SetViewMode(string mode)
    {
        if (_viewMode == mode) return;
        _viewMode = mode;
        ApplyViewMode();
        Refresh2();
    }

    private void ApplyViewMode()
    {
        _board.Visible = _viewMode == "board";
        _listHost.Visible = _viewMode == "list";
        _todo.Visible = _viewMode == "todo";
        if (_viewMode == "board") _board.BringToFront();
        else if (_viewMode == "list") _listHost.BringToFront();
        else _todo.BringToFront();

        // 筛选下拉只对列表视图有意义
        _filter.Enabled = _viewMode == "list";
        _filterLabel.Enabled = _viewMode == "list";

        // 待办视图下隐藏导出、从FAIL选故障项等按钮（待办有自己的操作界面）
        // 我们不改按钮Visible，让用户自由切换

        var on = Color.FromArgb(26, 82, 118);
        var off = SystemColors.Control;
        _btnBoard.BackColor = _viewMode == "board" ? on : off;
        _btnBoard.ForeColor = _viewMode == "board" ? Color.White : SystemColors.ControlText;
        _btnList.BackColor = _viewMode == "list" ? on : off;
        _btnList.ForeColor = _viewMode == "list" ? Color.White : SystemColors.ControlText;
        _btnTodo.BackColor = _viewMode == "todo" ? on : off;
        _btnTodo.ForeColor = _viewMode == "todo" ? Color.White : SystemColors.ControlText;
    }

    /// <summary>外部(MainForm 切页)调用的刷新入口。</summary>
    public void Refresh2()
    {
             if (_viewMode == "board") _board.Reload();
        else if (_viewMode == "list")  RefreshList();
        else                            _todo.Reload();
    }

    private void RefreshList()
    {
        var filterZh = _filter.SelectedItem?.ToString() ?? "全部";
        var statusKey = filterZh == "全部" ? "" : MaintenanceMeta.KeyOf(filterZh);
        _list.BeginUpdate();
        _list.Items.Clear();
        _current.Clear();
        try
        {
            var rows = _engine.Db.ListMaintenance(statusKey, ListLimit);
            // 「已完成」筛选时把尚未迁移的 legacy 'closed' 一并纳入
            if (statusKey == MaintenanceMeta.DoneStatus)
            {
                var legacy = _engine.Db.ListMaintenance(MaintenanceMeta.LegacyClosed, ListLimit);
                if (legacy.Count > 0)
                    rows = rows.Concat(legacy)
                               .OrderByDescending(m => string.IsNullOrEmpty(m.UpdatedAt) ? m.CreatedAt : m.UpdatedAt)
                               .Take(ListLimit).ToList();
            }
            foreach (var m in rows)
            {
                _current.Add(m);
                var item = new ListViewItem(m.Id.ToString());
                item.SubItems.Add(m.FailItem);
                item.SubItems.Add(m.FailReason);
                item.SubItems.Add(MaintenanceMeta.SeverityZhOf(m.Severity));
                item.SubItems.Add(MaintenanceMeta.ZhOf(m.Status));
                item.SubItems.Add(m.Resolver);
                item.SubItems.Add(m.CreatedAt);
                item.SubItems.Add(m.UpdatedAt);
                item.Tag = m;
                _list.Items.Add(item);
            }
        }
        catch (Exception ex) { Logger.Error($"维修记录加载失败: {ex.Message}"); }
        finally { _list.EndUpdate(); }
    }

    // ==================== 增 / 改 / 删 ====================
    private void NewRecord()
    {
        using var dlg = new MaintenanceForm(_engine.ResolvedStationId, null, null, ResolverCandidates(), _engine.Db);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var id = _engine.Db.CreateMaintenance(dlg.Result);
            Logger.Info($"新增维修记录 #{id}: {dlg.Result.FailItem}");
            Refresh2();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"创建失败: {ex.Message}");
            Logger.Error($"新增维修记录失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 【从FAIL选故障项】：从 FAIL 记录里挑去重后的测试项作为维修依据，
    /// 勾多个就一次建多条（也可在表单里选择合并为一条）。
    /// </summary>
    private void NewFromFailItems()
    {
        List<string> items;
        using (var picker = new FailItemPickerForm(_engine))
        {
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            items = picker.SelectedItems;
        }
        if (items.Count == 0) return;

        using var dlg = new MaintenanceForm(_engine.ResolvedStationId, null, items, ResolverCandidates(), _engine.Db);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        int ok = 0;
        var fails = new List<string>();
        try
        {
            foreach (var rec in dlg.BatchResults())
            {
                try
                {
                    var id = _engine.Db.CreateMaintenance(rec);
                    ok++;
                    Logger.Info($"新增维修记录 #{id}（来源 FAIL 故障项）: {rec.FailItem}");
                }
                catch (Exception ex)
                {
                    fails.Add($"{rec.FailItem}: {ex.Message}");
                    Logger.Error($"新增维修记录失败: {ex.Message}");
                }
            }
        }
        finally { Refresh2(); }

        if (fails.Count > 0)
            MessageBox.Show($"成功 {ok} 条，失败 {fails.Count} 条：\n" + string.Join("\n", fails.Take(5)), "提示");
        else
            MessageBox.Show($"已根据 FAIL 记录创建 {ok} 条维修记录。", "完成",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>历史维修人候选（给表单下拉用，失败不影响主流程）。</summary>
    private List<string> ResolverCandidates()
    {
        try { return _engine.Db.ListResolvers(); }        // 名单 ∪ 历史
        catch (Exception ex) { Logger.Warning($"读取维修人候选失败: {ex.Message}"); return new List<string>(); }
    }

    private void EditRecord(MaintenanceRecord m)
    {
        using var dlg = new MaintenanceForm(_engine.ResolvedStationId, m, null, ResolverCandidates(), _engine.Db);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (!_engine.Db.UpdateMaintenance(dlg.Result))
            {
                MessageBox.Show($"记录 #{m.Id} 更新失败（可能已被删除）。", "提示");
            }
            else
            {
                Logger.Info($"编辑维修记录 #{m.Id}: {dlg.Result.FailItem}");
            }
            Refresh2();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败: {ex.Message}");
            Logger.Error($"编辑维修记录失败: {ex.Message}");
        }
    }

    private void List_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (_list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is MaintenanceRecord m) EditRecord(m);
    }

    private void List_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || _list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is not MaintenanceRecord m) return;
        BuildRecordMenu(m).Show(_list, e.Location);
    }

    /// <summary>
    /// 单条记录的右键菜单：编辑 / 标记为五个状态 / 删除。
    /// **看板卡片与列表行共用这一份**（菜单内容只存在一处，不会两边走歧）。
    /// </summary>
    private ContextMenuStrip BuildRecordMenu(MaintenanceRecord m)
    {
        var menu = new ContextMenuStrip { Font = Font };
        menu.Items.Add(new ToolStripMenuItem($"#{m.Id}  {Ellipsis(m.FailItem, 24)}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("编辑...", null, (_, _) => EditRecord(m));
        menu.Items.Add(new ToolStripSeparator());
        var currentKey = MaintenanceMeta.Normalize(m.Status);
        foreach (var def in MaintenanceMeta.Statuses)
        {
            var item = new ToolStripMenuItem($"标记为 {def.Zh}")
            {
                Enabled = def.Key != currentKey,
                Checked = def.Key == currentKey,
            };
            var key = def.Key;
            item.Click += (_, _) => SetStatus(m, key);
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("删除", null, (_, _) => DeleteRecord(m));
        return menu;
    }

    private static string Ellipsis(string? s, int max)
    {
        s ??= "";
        return s.Length <= max ? s : s[..max] + "…";
    }

    private void SetStatus(MaintenanceRecord m, string status)
    {
        try
        {
            // 与看板一致: 标记为「已完成」时也弹补填框
            if (status == MaintenanceMeta.DoneStatus)
            {
                using var dlg = new QuickResolveForm(m, _engine.Db);
                var r = dlg.ShowDialog(this);
                if (r == DialogResult.Cancel) return;
                if (r == DialogResult.OK)
                {
                    var edited = m.Clone();
                    edited.Resolver = dlg.Resolver;
                    edited.Resolution = dlg.Resolution;
                    edited.Status = status;
                    _engine.Db.UpdateMaintenance(edited);
                    Logger.Info($"维修记录 #{m.Id} 状态 -> {MaintenanceMeta.ZhOf(status)}（已补填维修人/措施）");
                    Refresh2();
                    return;
                }
            }
            _engine.Db.UpdateMaintenanceStatus(m.Id, status);
            Logger.Info($"维修记录 #{m.Id} 状态: {MaintenanceMeta.ZhOf(m.Status)} -> {MaintenanceMeta.ZhOf(status)}");
            Refresh2();
        }
        catch (Exception ex)
        {
            MessageBox.Show("状态更新失败: " + ex.Message);
            Logger.Error($"维修记录状态更新失败: {ex.Message}");
        }
    }

    private void DeleteRecord(MaintenanceRecord m)
    {
        if (MessageBox.Show($"确定删除维修记录 #{m.Id}（{m.FailItem}）？", "确认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            _engine.Db.DeleteMaintenance(m.Id);
            Logger.Info($"删除维修记录 #{m.Id}");
            Refresh2();
        }
        catch (Exception ex)
        {
            MessageBox.Show("删除失败: " + ex.Message);
            Logger.Error($"删除维修记录失败: {ex.Message}");
        }
    }

    // ==================== 导出 ====================
    /// <summary>弹出导出菜单: Excel / CSV。</summary>
    private void ShowExportMenu(Control anchor)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("导出为 Excel (.xlsx)", null, (_, _) => DoExport(xlsx: true));
        menu.Items.Add("导出为 CSV (.csv)", null, (_, _) => DoExport(xlsx: false));
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    private void DoExport(bool xlsx)
    {
        // 看板视图导出当前加载的全部记录; 列表视图导出当前筛选结果(语义与 v2.1.0 一致)
        var records = _boardMode ? _board.LoadedRecords() : _current;
        if (records.Count == 0)
        {
            MessageBox.Show("当前没有可导出的维修记录。", "提示");
            return;
        }
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmm");
        using var dlg = new SaveFileDialog
        {
            Filter = xlsx ? "Excel 工作簿 (*.xlsx)|*.xlsx" : "CSV 文件 (*.csv)|*.csv",
            FileName = xlsx ? $"维修记录_{ts}.xlsx" : $"维修记录_{ts}.csv",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            if (xlsx) MaintenanceExporter.ExportXlsx(dlg.FileName, records);
            else MaintenanceExporter.ExportCsv(dlg.FileName, records);
            Logger.Info($"维修记录已导出: {dlg.FileName} ({records.Count} 条)");
            if (MessageBox.Show($"已导出 {records.Count} 条维修记录！\n是否打开文件?", "完成",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = dlg.FileName, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败: " + ex.Message);
            Logger.Error($"维修记录导出失败: {ex.Message}");
        }
    }
}

/// <summary>
/// 新增 / 编辑维修记录的对话框。
///
/// 三种用法：
///   · `new MaintenanceForm(stationId, null, null, who)`        —— 手工新增一条
///   · `new MaintenanceForm(stationId, rec,  null, who)`        —— 编辑（保留 Id）
///   · `new MaintenanceForm(stationId, null, items, who)`       —— 从 FAIL 选来的 N 个故障项批量建单
///
/// v2.3.0 起：**设备型号 / 设备SN 不再录入**（DB 列保留，历史值不动）；
/// 维修人员改为可下拉选历史值，不用每次手敲。
/// </summary>
public class MaintenanceForm : Form
{
    public MaintenanceRecord Result { get; }

    private readonly bool _isEdit;
    private readonly List<string> _batchItems;      // 批量模式下的故障项（>1 才算批量）
    private readonly List<string> _resolverPool;

    private TextBox _failItem = null!, _reason = null!;
    private TextBox _resolution = null!, _notes = null!;
    private TextBox _batchView = null!;
    private ComboBox _severity = null!, _status = null!, _resolver = null!;
    private readonly Database? _db;                 // 有它才能开【+ 人员】管理名单
    private CheckBox _mergeOne = null!;
    private DateTimePicker _date = null!;
    private Control _resolverHost = null!;

    private bool IsBatch => _batchItems.Count > 0;

    public MaintenanceForm(string stationId,
                           MaintenanceRecord? edit = null,
                           IEnumerable<string>? failItems = null,
                           IEnumerable<string>? resolvers = null,
                           Database? db = null)
    {
        _db = db;
        _isEdit = edit != null;
        _batchItems = failItems?.Where(x => !string.IsNullOrWhiteSpace(x))
                                .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList() ?? new List<string>();
        _resolverPool = resolvers?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();

        // 编辑模式下操作副本, 用户点「取消」时不污染看板/列表里的原对象
        Result = edit?.Clone() ?? new MaintenanceRecord { StationId = stationId, Status = MaintenanceMeta.DefaultStatus };
        if (!_isEdit) Result.StationId = stationId;
        if (IsBatch) Result.FailItem = _batchItems[0];

        Text = _isEdit ? $"编辑维修记录 #{Result.Id}"
             : IsBatch ? $"新增维修记录（{_batchItems.Count} 个故障项）"
                       : "新增维修记录";
        Width = 470;
        Height = IsBatch ? 620 : 560;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9F);
        BuildUi();
        LoadValues();
    }

    private void BuildUi()
    {
        // 行：0 日期 / 1 故障项目 / 2 故障描述 / 3 严重度 / 4 维修人 / 5 维修措施 / 6 状态 / 7 备注 [/ 8 合并]
        int rows = IsBatch ? 9 : 8;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14), RowCount = rows,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < rows; i++)
        {
            // 故障项目(批量时是只读列表) / 维修措施 / 备注 都是多行
            bool tall = (IsBatch && i == 1) || i == 5 || i == 7;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, tall ? 68 : 34));
        }

        var m = new Padding(3, 5, 3, 5);
        _date = new DateTimePicker
        {
            Dock = DockStyle.Fill, Format = DateTimePickerFormat.Custom,
            CustomFormat = "yyyy-MM-dd HH:mm", ShowUpDown = true, Value = DateTime.Now, Margin = m,
        };
        _failItem = new TextBox { Dock = DockStyle.Fill, Margin = m };
        _batchView = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, Margin = m,
            BackColor = Color.FromArgb(245, 247, 250),
        };
        _reason = new TextBox { Dock = DockStyle.Fill, Margin = m };
        _severity = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = m };
        _severity.Items.AddRange(MaintenanceMeta.SeverityOrderZh.Cast<object>().ToArray());
        // 维修人：可选历史值、也可直接打新名字
        _resolver = new ComboBox
        {
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, Margin = m,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems,
        };
        if (_resolverPool.Count > 0) _resolver.Items.AddRange(_resolverPool.Cast<object>().ToArray());
        // 维修人一行 = 下拉 + 【+ 人员】按钮（按钮点开名单管理，回来自动刷新下拉）
        // 维修人一行 = 可编辑下拉（单人快选/手敲）+ 【选择】多选 + 【+ 人员】名单管理
        var resolverHost = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0),
        };
        resolverHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        resolverHost.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        resolverHost.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        var btnPickWho = new Button
        {
            Dock = DockStyle.Fill, Text = "选择", Margin = new Padding(2, 5, 2, 5),
            BackColor = Color.FromArgb(217, 231, 245),
        };
        btnPickWho.Click += (_, _) => PickResolvers();
        btnPickWho.Enabled = _db != null;
        var btnAddWho = new Button
        {
            Dock = DockStyle.Fill, Text = "+ 人员", Margin = new Padding(0, 5, 3, 5),
            BackColor = Color.FromArgb(226, 240, 217),
        };
        btnAddWho.Click += (_, _) => ManageResolvers();
        btnAddWho.Enabled = _db != null;
        resolverHost.Controls.Add(_resolver, 0, 0);
        resolverHost.Controls.Add(btnPickWho, 1, 0);
        resolverHost.Controls.Add(btnAddWho, 2, 0);
        _resolverHost = resolverHost;

        _resolution = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Margin = m };
        _status = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = m };
        _status.Items.AddRange(MaintenanceMeta.StatusItems());
        _notes = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Margin = m };
        _mergeOne = new CheckBox
        {
            Dock = DockStyle.Fill, Margin = m,
            Text = $"合并为一条记录（否则建 {_batchItems.Count} 条，每个故障项一条）",
        };

        void AddRow(int r, string label, Control c)
        {
            var lbl = new Label
            {
                Text = label, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, AutoSize = false,
            };
            layout.Controls.Add(lbl, 0, r);
            layout.Controls.Add(c, 1, r);
        }
        AddRow(0, "记录日期 *:", _date);
        AddRow(1, IsBatch ? $"故障项目({_batchItems.Count}):" : "故障项目 *:", IsBatch ? _batchView : _failItem);
        AddRow(2, "故障描述:", _reason);
        AddRow(3, "严重程度:", _severity);
        AddRow(4, "维修人员:", _resolverHost);
        AddRow(5, "维修措施:", _resolution);
        AddRow(6, "当前状态:", _status);
        AddRow(7, "备注:", _notes);
        if (IsBatch) AddRow(8, "", _mergeOne);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10),
        };
        var ok = new Button { Text = "保存", Width = 80, DialogResult = DialogResult.None };
        ok.Click += OnSave;
        var cancel = new Button { Text = "取消", Width = 80, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        CancelButton = cancel;

        Controls.Add(layout);
        Controls.Add(buttons);
    }

    private void LoadValues()
    {
        _failItem.Text = Result.FailItem;
        _reason.Text = Result.FailReason;
        _resolver.Text = Result.Resolver;
        _resolution.Text = Result.Resolution;
        _notes.Text = Result.Notes;
        if (IsBatch)
            _batchView.Text = string.Join(Environment.NewLine, _batchItems.Select((x, i) => $"{i + 1}. {x}"));

        _severity.SelectedItem = MaintenanceMeta.SeverityZhOf(
            string.IsNullOrEmpty(Result.Severity) ? MaintenanceMeta.DefaultSeverity : Result.Severity);
        if (_severity.SelectedIndex < 0) _severity.SelectedIndex = 0;

        _status.SelectedItem = MaintenanceMeta.ZhOf(MaintenanceMeta.Normalize(Result.Status));
        if (_status.SelectedIndex < 0)
            _status.SelectedItem = MaintenanceMeta.ZhOf(MaintenanceMeta.DefaultStatus);

        if (!string.IsNullOrWhiteSpace(Result.CreatedAt) &&
            DateTime.TryParse(Result.CreatedAt, out var dt))
            _date.Value = dt;
    }

    /// <summary>【选择】：多选维修人，结果回填成 `张三、李四`。</summary>
    private void PickResolvers()
    {
        if (_db == null) return;
        using var dlg = new ResolverPickerForm(_db, _resolver.Text);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        ReloadResolverItems(dlg.Result);
        _resolver.Text = dlg.Result;          // 多人时下拉里没有完全匹配项, 直接写文本
    }

    /// <summary>打开维修人员名单管理；回来后刷新下拉并尽量保住当前输入。</summary>
    private void ManageResolvers()
    {
        if (_db == null) return;
        var keep = _resolver.Text.Trim();
        using var dlg = new ResolverManagerForm(_db, keep);
        dlg.ShowDialog(this);
        if (!dlg.Changed) return;
        ReloadResolverItems(keep);
    }

    private void ReloadResolverItems(string keep)
    {
        if (_db == null) return;
        try
        {
            var items = _db.ListResolvers();
            _resolver.BeginUpdate();
            _resolver.Items.Clear();
            _resolver.Items.AddRange(items.Cast<object>().ToArray());
            _resolver.EndUpdate();
            // 名单里有刚才那个名字就选中它，否则保留原文本
            var hit = items.FirstOrDefault(x => string.Equals(x, keep, StringComparison.OrdinalIgnoreCase));
            if (hit != null) _resolver.SelectedItem = hit; else _resolver.Text = keep;
        }
        catch (Exception ex) { Logger.Warning($"刷新维修人候选失败: {ex.Message}"); }
    }

    private void OnSave(object? sender, EventArgs e)
    {
        if (!IsBatch && string.IsNullOrWhiteSpace(_failItem.Text))
        {
            MessageBox.Show("故障项目为必填项。", "提示");
            return;
        }
        if (!IsBatch) Result.FailItem = _failItem.Text.Trim();
        Result.FailReason = _reason.Text.Trim();
        Result.Severity = MaintenanceMeta.SeverityKeyOf(_severity.SelectedItem?.ToString());
        // 多人统一规范化为 `张三、李四`（兼容手敲的 , ， / ; 等分隔符）
        Result.Resolver = ResolverUtil.Normalize(_resolver.Text);
        // 手打的新名字自动进名单（**逐人**登记，不会把「张三、李四」整串当一个人）
        if (_db != null)
        {
            foreach (var who in ResolverUtil.Split(Result.Resolver))
            {
                try { if (_db.AddResolver(who)) Logger.Info($"维修人员名单自动新增: {who}"); }
                catch (Exception ex) { Logger.Warning($"自动登记维修人失败: {ex.Message}"); }
            }
        }
        Result.Resolution = _resolution.Text.Trim();
        Result.Notes = _notes.Text.Trim();
        Result.Status = MaintenanceMeta.KeyOf(_status.SelectedItem?.ToString());
        if (string.IsNullOrEmpty(Result.Status)) Result.Status = MaintenanceMeta.DefaultStatus;
        Result.CreatedAt = _date.Value.ToString("yyyy-MM-dd HH:mm:ss");
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>
    /// 批量模式下要落库的记录集合：
    /// 勾了「合并为一条」→ 1 条（故障项用 " / " 连接）；否则每个故障项一条。
    /// 非批量模式返回单条 <see cref="Result"/>。
    /// </summary>
    public List<MaintenanceRecord> BatchResults()
    {
        if (!IsBatch) return new List<MaintenanceRecord> { Result };

        if (_mergeOne.Checked)
        {
            var one = Result.Clone();
            one.FailItem = string.Join(" / ", _batchItems);
            return new List<MaintenanceRecord> { one };
        }
        return _batchItems.Select(item =>
        {
            var r = Result.Clone();
            r.Id = 0;
            r.FailItem = item;
            return r;
        }).ToList();
    }
}

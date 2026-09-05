namespace FctAggregator;

public class MaintenancePanel : Panel
{
    private const int ListLimit = 500;

    private readonly Engine _engine;
    private MaintenanceBoard _board = null!;
    private ListView _list = null!;
    private Panel _listHost = null!;
    private ComboBox _filter = null!;
    private Label _filterLabel = null!;
    private Button _btnBoard = null!;
    private Button _btnList = null!;
    private ComboBox _range = null!;
    private Label _rangeLabel = null!;
    private DateTime? _customFrom, _customTo;
    private List<MaintenanceRecord> _current = new();

    public MaintenancePanel(Engine engine)
    {
        _engine = engine;
        BuildUi();
        ApplyViewMode();
    }

    private void BuildUi()
    {
        Padding = new Padding(Theme.Gap);
        BackColor = Theme.Bg;

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 42, Padding = new Padding(0, 4, 0, 4), BackColor = Theme.Bg,
        };

        _btnBoard = new Button { Text = "看板", Width = 64, Height = 30, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 2, 4, 2) };
        _btnBoard.Click += (_, _) => SetViewMode("board");
        _btnList = new Button { Text = "列表", Width = 64, Height = 30, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 2, 4, 2) };
        _btnList.Click += (_, _) => SetViewMode("list");
        bar.Controls.Add(_btnBoard);
        bar.Controls.Add(_btnList);

        _filterLabel = new Label
        {
            Text = "筛选:", AutoSize = true, Anchor = AnchorStyles.Left,
            Margin = new Padding(12, 9, 4, 0),
        };
        bar.Controls.Add(_filterLabel);
        _filter = new ComboBox { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 6, 4, 0) };
        _filter.Items.AddRange(MaintenanceMeta.FilterItems());
        _filter.SelectedIndex = 0;
        _filter.SelectedIndexChanged += (_, _) => { if (_viewMode == "list") RefreshList(); };
        bar.Controls.Add(_filter);

        _rangeLabel = new Label
        {
            Text = "待办区间:", AutoSize = true, Anchor = AnchorStyles.Left,
            Margin = new Padding(12, 9, 4, 0),
        };
        bar.Controls.Add(_rangeLabel);
        _range = new ComboBox { Width = 146, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 6, 4, 0) };
        _range.Items.AddRange(TodoRange.Presets(AppConfig.Instance.TodoScanDays).Cast<object>().ToArray());
        _range.SelectedIndex = 1;
        _range.SelectedIndexChanged += (_, _) => ApplyTodoRange();
        bar.Controls.Add(_range);

        var btnFromFail = new Button
        {
            Text = "从FAIL选择故障", Width = 118, Height = 30,
            Margin = new Padding(12, 2, 4, 2),
            BackColor = Color.FromArgb(20, 20, 20), ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        btnFromFail.Click += (_, _) => NewFromFailItems();
        bar.Controls.Add(btnFromFail);

        var btnNew = new Button { Text = "新增记录", Width = 82, Height = 30, Margin = new Padding(0, 2, 4, 2) };
        btnNew.Click += (_, _) => NewRecord();
        bar.Controls.Add(btnNew);
        var btnRefresh = new Button { Text = "刷新", Width = 76, Height = 30, Margin = new Padding(0, 2, 4, 2) };
        btnRefresh.Click += (_, _) => Refresh2();
        bar.Controls.Add(btnRefresh);
        var btnExport = new Button { Text = "导出", Width = 76, Height = 30, Margin = new Padding(0, 2, 4, 2) };
        btnExport.Click += (s, _) => ShowExportMenu(btnExport);
        bar.Controls.Add(btnExport);

        _board = new MaintenanceBoard(_engine) { Dock = DockStyle.Fill, Visible = true };
        _board.EditRequested += EditRecord;
        _board.ContextRequested += (m, anchor, at) => BuildRecordMenu(m).Show(anchor, at);
        _board.Changed += () => { if (_viewMode == "list") RefreshList(); };

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

        Controls.Add(_board);
        Controls.Add(_listHost);
        Controls.Add(bar);
    }

    private string _viewMode = "board";

    private void SetViewMode(string mode)
    {
        if (_viewMode == mode) return;
        _viewMode = mode;
        ApplyViewMode();
        Refresh2();
    }

    private void ApplyViewMode()
    {
        CardPreviewForm.CloseCurrent();
        _board.Visible = _viewMode == "board";
        _listHost.Visible = _viewMode == "list";
        if (_viewMode == "board") _board.BringToFront();
        else _listHost.BringToFront();

        _filter.Enabled = _viewMode == "list";
        _filterLabel.Enabled = _viewMode == "list";
        _range.Enabled = _viewMode == "board";
        _rangeLabel.Enabled = _viewMode == "board";

        var on = Theme.Primary;
        var off = Theme.Surface;
        _btnBoard.BackColor = _viewMode == "board" ? on : off;
        _btnBoard.ForeColor = _viewMode == "board" ? Color.White : Theme.TextMain;
        _btnBoard.FlatAppearance.BorderColor = Theme.Border;
        _btnList.BackColor = _viewMode == "list" ? on : off;
        _btnList.ForeColor = _viewMode == "list" ? Color.White : Theme.TextMain;
        _btnList.FlatAppearance.BorderColor = Theme.Border;
    }

    public void Refresh2()
    {
        if (_viewMode == "board") _board.Reload();
        else RefreshList();
    }

    public int PendingTodoCount => _board.PendingTodoCount;

    private void ApplyTodoRange()
    {
        if (_range.SelectedItem is not TodoRange r) return;
        if (r.Label.StartsWith("自定义"))
        {
            using var dlg = new TodoRangeForm(_customFrom, _customTo);
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                _range.SelectedIndex = 1;
                return;
            }
            _customFrom = dlg.From;
            _customTo = dlg.To;
            _board.SetTodoRange(_customFrom, _customTo, custom: true);
            Logger.Info($"[待办] 区间已设为 {_customFrom:yyyy-MM-dd} ~ {_customTo:yyyy-MM-dd}");
            return;
        }
        _board.SetTodoRange(r.From, r.To);
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

    private List<string> ResolverCandidates()
    {
        try { return _engine.Db.ListResolvers(); }
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
        var edited = m.Clone();
        edited.Status = MaintenanceMeta.Normalize(status);
        using (var dlg = new MaintenanceForm(_engine.ResolvedStationId, edited, null, ResolverCandidates(), _engine.Db))
        {
            if (dlg.ShowDialog(this) == DialogResult.Cancel) return;
            edited = dlg.Result;
        }
        try
        {
            if (!_engine.Db.UpdateMaintenance(edited))
            {
                MessageBox.Show($"记录 #{m.Id} 更新失败（可能已被删除）。", "提示");
            }
            else
            {
                Logger.Info($"维修记录 #{m.Id} 状态: {MaintenanceMeta.ZhOf(m.Status)} -> {MaintenanceMeta.ZhOf(edited.Status)}");
            }
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
        int failCount = 0;
        try { failCount = _engine.Db.CountFailRecords(m.FailItem, m.StationId); }
        catch (Exception ex) { Logger.Warning($"查故障项 FAIL 数失败: {ex.Message}"); }

        var msg = $"确定删除维修记录 #{m.Id}（{m.FailItem}）？";
        if (failCount > 0)
            msg += $"\n\n⚠ 该故障项在 FAIL 记录里还有 {failCount} 条真实不良，\n" +
                   "删掉本记录后它会以「未确认」卡片**自动回到「待办」列**（不良不可删除）。\n" +
                   "如果问题已解决，请改为把卡片拖到「已完成」。";

        if (MessageBox.Show(msg, "确认",
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

    private void ShowExportMenu(Control anchor)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("导出为 Excel (.xlsx)", null, (_, _) => DoExport(xlsx: true));
        menu.Items.Add("导出为 CSV (.csv)", null, (_, _) => DoExport(xlsx: false));
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    private void DoExport(bool xlsx)
    {
        var records = _viewMode == "board" ? _board.LoadedRecords() : _current;
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

    public class MaintenanceForm : Form
    {
        public MaintenanceRecord Result { get; }

        private readonly bool _isEdit;
        private readonly List<string> _batchItems;
        private readonly List<string> _resolverPool;

        private TextBox _failItem = null!, _reason = null!;
        private TextBox _resolution = null!, _notes = null!;
        private TextBox _batchView = null!;
        private ComboBox _severity = null!, _status = null!, _resolver = null!;
        private readonly Database? _db;
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
            _isEdit = edit is { Id: > 0 };
            _batchItems = failItems?.Where(x => !string.IsNullOrWhiteSpace(x))
                                    .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList() ?? new List<string>();
            _resolverPool = resolvers?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();

            Result = edit?.Clone() ?? new MaintenanceRecord { StationId = stationId, Status = MaintenanceMeta.DefaultStatus };
            if (!_isEdit && string.IsNullOrEmpty(Result.StationId)) Result.StationId = stationId;
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
        int rows = IsBatch ? 9 : 8;
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14), RowCount = rows,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < rows; i++)
        {
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
            BackColor = Theme.ToolAltRow,
        };
        _reason = new TextBox { Dock = DockStyle.Fill, Margin = m };
        _severity = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = m };
        _severity.Items.AddRange(MaintenanceMeta.SeverityOrderZh.Cast<object>().ToArray());
        _resolver = new ComboBox
        {
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, Margin = m,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems,
        };
        if (_resolverPool.Count > 0) _resolver.Items.AddRange(_resolverPool.Cast<object>().ToArray());
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
            BackColor = Color.FromArgb(200, 16, 46), ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        btnPickWho.Click += (_, _) => PickResolvers();
        btnPickWho.Enabled = _db != null;
        var btnAddWho = new Button
        {
            Dock = DockStyle.Fill, Text = "+ 人员", Margin = new Padding(0, 5, 3, 5),
            BackColor = Color.FromArgb(20, 20, 20), ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
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

    private void PickResolvers()
    {
        if (_db == null) return;
        using var dlg = new ResolverPickerForm(_db, _resolver.Text);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        ReloadResolverItems(dlg.Result);
        _resolver.Text = dlg.Result;
    }

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
        Result.Resolver = ResolverUtil.Normalize(_resolver.Text);
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

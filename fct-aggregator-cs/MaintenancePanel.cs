using System.Text;

namespace FctAggregator;

/// <summary>维修记录页: 列表 + 新增 + 状态变更 + 删除。</summary>
public class MaintenancePanel : Panel
{
    private readonly Engine _engine;
    private ListView _list = null!;
    private ComboBox _filter = null!;
    private List<MaintenanceRecord> _current = new();   // 当前列表(供导出)

    public MaintenancePanel(Engine engine)
    {
        _engine = engine;
        BuildUi();
    }

    private void BuildUi()
    {
        Padding = new Padding(12);

        var title = new Label { Text = "维修记录", Dock = DockStyle.Top, Height = 30, Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold) };

        // 顶部工具栏
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(0, 4, 0, 4) };
        bar.Controls.Add(new Label { Text = "筛选:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 4, 0) });
        _filter = new ComboBox { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
        _filter.Items.AddRange(new object[] { "全部", "待处理", "进行中", "已解决", "已关闭" });
        _filter.SelectedIndex = 0;
        _filter.SelectedIndexChanged += (_, _) => Refresh2();
        bar.Controls.Add(_filter);
        var btnNew = new Button { Text = "新增记录", Width = 90, Height = 28 };
        btnNew.Click += (_, _) => NewRecord();
        bar.Controls.Add(btnNew);
        var btnRefresh = new Button { Text = "刷新", Width = 70, Height = 28 };
        btnRefresh.Click += (_, _) => Refresh2();
        bar.Controls.Add(btnRefresh);
        var btnExport = new Button { Text = "导出", Width = 80, Height = 28 };
        btnExport.Click += (s, _) => ShowExportMenu(btnExport);
        bar.Controls.Add(btnExport);

        // 列表
        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true,
        };
        _list.Columns.Add("ID", 50);
        _list.Columns.Add("故障项目", 200);
        _list.Columns.Add("设备SN", 180);
        _list.Columns.Add("严重度", 70);
        _list.Columns.Add("状态", 80);
        _list.Columns.Add("维修人", 80);
        _list.Columns.Add("时间", 140);
        _list.MouseClick += List_MouseClick;

        Controls.Add(_list);
        Controls.Add(bar);
        Controls.Add(title);
    }

    private static readonly Dictionary<string, string> StatusZh = new()
    {
        ["open"] = "待处理", ["in_progress"] = "进行中", ["resolved"] = "已解决", ["closed"] = "已关闭",
    };
    private static readonly Dictionary<string, string> SeverityZh = new()
    {
        ["critical"] = "严重", ["major"] = "一般", ["minor"] = "轻微",
    };

    public void Refresh2()
    {
        var filterZh = _filter.SelectedItem?.ToString() ?? "全部";
        var statusKey = filterZh == "全部" ? "" : StatusZh.FirstOrDefault(kv => kv.Value == filterZh).Key ?? "";
        _list.Items.Clear();
        _current.Clear();
        try
        {
            foreach (var m in _engine.Db.ListMaintenance(statusKey))
            {
                _current.Add(m);
                var item = new ListViewItem(m.Id.ToString());
                item.SubItems.Add(m.FailItem);
                item.SubItems.Add(m.EquipmentSn);
                item.SubItems.Add(SeverityZh.GetValueOrDefault(m.Severity, m.Severity));
                item.SubItems.Add(StatusZh.GetValueOrDefault(m.Status, m.Status));
                item.SubItems.Add(m.Resolver);
                item.SubItems.Add(m.CreatedAt);
                item.Tag = m;
                _list.Items.Add(item);
            }
        }
        catch (Exception ex) { Logger.Error($"维修记录加载失败: {ex.Message}"); }
    }

    private void NewRecord()
    {
        using var dlg = new MaintenanceForm(_engine.ResolvedStationId);
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            try { _engine.Db.CreateMaintenance(dlg.Result); Refresh2(); }
            catch (Exception ex) { MessageBox.Show($"创建失败: {ex.Message}"); }
        }
    }

    private void List_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || _list.SelectedItems.Count == 0) return;
        var m = (MaintenanceRecord)_list.SelectedItems[0].Tag!;
        var menu = new ContextMenuStrip();
        menu.Items.Add("标记为 进行中", null, (_, _) => SetStatus(m.Id, "in_progress"));
        menu.Items.Add("标记为 已解决", null, (_, _) => SetStatus(m.Id, "resolved"));
        menu.Items.Add("标记为 已关闭", null, (_, _) => SetStatus(m.Id, "closed"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("删除", null, (_, _) =>
        {
            if (MessageBox.Show("确定删除这条维修记录？", "确认", MessageBoxButtons.YesNo) == DialogResult.Yes)
            { _engine.Db.DeleteMaintenance(m.Id); Refresh2(); }
        });
        menu.Show(_list, e.Location);
    }

    private void SetStatus(int id, string status)
    {
        _engine.Db.UpdateMaintenanceStatus(id, status);
        Refresh2();
    }

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
        if (_current.Count == 0)
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
            if (xlsx) MaintenanceExporter.ExportXlsx(dlg.FileName, _current);
            else MaintenanceExporter.ExportCsv(dlg.FileName, _current);
            Logger.Info($"维修记录已导出: {dlg.FileName} ({_current.Count} 条)");
            if (MessageBox.Show($"已导出 {_current.Count} 条维修记录！\n是否打开文件?", "完成",
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

/// <summary>新增/编辑维修记录的对话框。</summary>
public class MaintenanceForm : Form
{
    public MaintenanceRecord Result { get; } = new();
    private TextBox _failItem = null!, _model = null!, _sn = null!, _reason = null!, _resolver = null!;
    private TextBox _resolution = null!;
    private ComboBox _severity = null!;
    private DateTimePicker _date = null!;

    public MaintenanceForm(string stationId)
    {
        Result.StationId = stationId;
        Text = "维修记录";
        Width = 460; Height = 480;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9F);
        BuildUi();
    }

    private void BuildUi()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14), RowCount = 8 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // 各行统一行高; 维修措施行(index 7)更高
        for (int i = 0; i < 8; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 7 ? 64 : 34));

        _date = new DateTimePicker
        {
            Dock = DockStyle.Fill, Format = DateTimePickerFormat.Custom,
            CustomFormat = "yyyy-MM-dd HH:mm", ShowUpDown = true, Value = DateTime.Now,
            Margin = new Padding(3, 5, 3, 5),
        };
        _failItem = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 5, 3, 5) };
        _model = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 5, 3, 5) };
        _sn = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 5, 3, 5) };
        _reason = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 5, 3, 5) };
        _severity = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 5, 3, 5) };
        _severity.Items.AddRange(new object[] { "一般", "严重", "轻微" });
        _severity.SelectedIndex = 0;
        _resolver = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 5, 3, 5) };
        _resolution = new TextBox { Dock = DockStyle.Fill, Multiline = true, Margin = new Padding(3, 5, 3, 5) };

        void AddRow(int r, string label, Control c)
        {
            // 标签 Dock.Fill + 垂直居中, 与输入框对齐
            var lbl = new Label
            {
                Text = label, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, AutoSize = false,
            };
            layout.Controls.Add(lbl, 0, r);
            layout.Controls.Add(c, 1, r);
        }
        AddRow(0, "记录日期 *:", _date);
        AddRow(1, "故障项目 *:", _failItem);
        AddRow(2, "设备型号:", _model);
        AddRow(3, "设备SN:", _sn);
        AddRow(4, "故障描述:", _reason);
        AddRow(5, "严重程度:", _severity);
        AddRow(6, "维修人员:", _resolver);
        AddRow(7, "维修措施:", _resolution);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var ok = new Button { Text = "保存", Width = 80, DialogResult = DialogResult.None };
        ok.Click += OnSave;
        var cancel = new Button { Text = "取消", Width = 80, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        Controls.Add(layout);
        Controls.Add(buttons);
    }

    private static readonly Dictionary<string, string> SevZhToKey = new()
    {
        ["一般"] = "major", ["严重"] = "critical", ["轻微"] = "minor",
    };

    private void OnSave(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_failItem.Text))
        {
            MessageBox.Show("故障项目为必填项。", "提示");
            return;
        }
        Result.FailItem = _failItem.Text.Trim();
        Result.EquipmentModel = _model.Text.Trim();
        Result.EquipmentSn = _sn.Text.Trim();
        Result.FailReason = _reason.Text.Trim();
        Result.Severity = SevZhToKey.GetValueOrDefault(_severity.SelectedItem?.ToString() ?? "一般", "major");
        Result.Resolver = _resolver.Text.Trim();
        Result.Resolution = _resolution.Text.Trim();
        Result.CreatedAt = _date.Value.ToString("yyyy-MM-dd HH:mm:ss");
        DialogResult = DialogResult.OK;
        Close();
    }
}

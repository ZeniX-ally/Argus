using System.Text;

namespace FctAggregator;

/// <summary>
/// 待办面板：显示 FAIL 记录中尚未确认的故障项（无活跃维修记录 + 未忽略）。
///
/// 工程师在此「确认问题」→ 自动创建一条维修记录（状态 = 未完成），
/// 然后可到看板继续处理。既有的「从FAIL选故障项」按钮仍保留（用于批量/手工选）。
///
/// 设计要点：
///   · 数据源 = test_records 实时聚合（非独立表），确认后由维修记录的存在性自动拦截
///   · 忽略的项记入 `dismissed_todos` 表，不再显示
///   · 列表按「最近出现时间」倒序，同批次的按出现次数排序
///   · 双击行 = 快速确认（弹轻量补填框），右键菜单 = 忽略/确认
/// </summary>
public class TodoPanel : Panel
{
    private readonly Engine _engine;

    private ListView _list = null!;
    private Label _statusLabel = null!;
    private Button _btnRefresh = null!;
    private Button _btnConfirm = null!;
    private Button _btnDismiss = null!;
    private CheckBox _onlyThisStation = null!;

    private List<TodoItem> _current = new();

    public TodoPanel(Engine engine)
    {
        _engine = engine;
        BuildUi();
    }

    private void BuildUi()
    {
        Padding = new Padding(12);
        BackColor = Color.FromArgb(250, 251, 253);
        DoubleBuffered = true;

        var title = new Label
        {
            Text = "待办 — 需确认的 FAIL 故障项",
            Dock = DockStyle.Top, Height = 30,
            Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold),
            ForeColor = Color.FromArgb(197, 48, 48),
        };

        // ===== 工具栏 =====
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 40, Padding = new Padding(0, 4, 0, 4),
        };

        _btnRefresh = new Button { Text = "↻ 刷新", Width = 76, Height = 28, FlatStyle = FlatStyle.Flat };
        _btnRefresh.Click += (_, _) => Reload();
        bar.Controls.Add(_btnRefresh);

        _onlyThisStation = new CheckBox
        {
            Text = "只看本机台", Margin = new Padding(8, 5, 4, 3), AutoSize = true,
            Checked = true,
        };
        _onlyThisStation.CheckedChanged += (_, _) => Reload();
        bar.Controls.Add(_onlyThisStation);

        _btnConfirm = new Button
        {
            Text = "确认问题（建维修记录）", Width = 168, Height = 28, Margin = new Padding(8, 3, 3, 3),
            BackColor = Color.FromArgb(226, 240, 217),
            FlatStyle = FlatStyle.Flat,
        };
        _btnConfirm.Click += (_, _) => ConfirmSelected();
        bar.Controls.Add(_btnConfirm);

        _btnDismiss = new Button
        {
            Text = "忽略", Width = 70, Height = 28, Margin = new Padding(3, 3, 3, 3),
            FlatStyle = FlatStyle.Flat,
        };
        _btnDismiss.Click += (_, _) => DismissSelected();
        bar.Controls.Add(_btnDismiss);

        // ===== 提示横幅 =====
        var tip = new Label
        {
            Dock = DockStyle.Top, Height = 22, Padding = new Padding(0, 0, 0, 2),
            ForeColor = Color.FromArgb(130, 140, 158),
            Font = new Font("Microsoft YaHei UI", 8.5F),
            Text = "以下故障项来自 FAIL 记录（按故障项去重），尚无人在处理。双击某行快速「确认问题」进入维修流程。",
        };

        // ===== 列表 =====
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.HideSelection = false;
        _list.Columns.Add("故障项目", 340);
        _list.Columns.Add("出现次数", 80, HorizontalAlignment.Center);
        _list.Columns.Add("首次出现", 140, HorizontalAlignment.Center);
        _list.Columns.Add("最近出现", 140, HorizontalAlignment.Center);
        _list.Columns.Add("型号", 120);
        _list.Columns.Add("机台", 80);
        _list.MouseDoubleClick += List_MouseDoubleClick;
        _list.MouseClick += List_MouseClick;
        _list.SelectedIndexChanged += (_, _) => UpdateButtonState();

        // ===== 状态栏 =====
        _statusLabel.Dock = DockStyle.Bottom;
        _statusLabel.Height = 24;
        _statusLabel.ForeColor = Color.DimGray;

        Controls.Add(_list);
        Controls.Add(tip);
        Controls.Add(bar);
        Controls.Add(title);
        Controls.Add(_statusLabel);
    }

    /// <summary>加载/刷新待办列表。</summary>
    public void Reload()
    {
        try
        {
            _current = _engine.Db.ListPendingTodos(
                _onlyThisStation.Checked ? _engine.ResolvedStationId : "");
            Rebind();
        }
        catch (Exception ex)
        {
            Logger.Error($"加载待办列表失败: {ex.Message}");
            _statusLabel.Text = $"加载失败: {ex.Message}";
        }
    }

    private void Rebind()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        try
        {
            foreach (var t in _current)
            {
                var it = new ListViewItem(t.FailItem) { Tag = t };
                it.SubItems.Add(t.OccurrenceCount.ToString());
                it.SubItems.Add(ShortTime(t.FirstSeen));
                it.SubItems.Add(ShortTime(t.LastSeen));
                it.SubItems.Add(string.IsNullOrEmpty(t.Model) ? "—" : t.Model);
                it.SubItems.Add(string.IsNullOrEmpty(t.StationId) ? "—" : t.StationId);
                _list.Items.Add(it);
            }
        }
        finally
        {
            _list.EndUpdate();
            UpdateButtonState();
            _statusLabel.Text = $"共 {_current.Count} 项待确认";
        }
    }

    private void UpdateButtonState()
    {
        bool hasSel = _list.SelectedItems.Count > 0;
        _btnConfirm.Enabled = hasSel;
        _btnDismiss.Enabled = hasSel;
    }

    // ==================== 确认问题 ====================

    /// <summary>确认选中的待办项：弹轻量补填框 → 建维修记录 → 刷新。</summary>
    private void ConfirmSelected()
    {
        if (_list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is not TodoItem todo) return;

        using var dlg = new TodoConfirmForm(todo, _engine.Db);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var id = _engine.Db.AcknowledgeTodo(
                todo.FailItem, todo.StationId, todo.Model,
                dlg.Resolver, dlg.Severity);
            Logger.Info($"[待办] 确认问题 -> 维修记录 #{id}: {todo.FailItem}");
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"创建维修记录失败: {ex.Message}", "错误");
            Logger.Error($"[待办] 确认问题失败: {ex.Message}");
        }
    }

    /// <summary>忽略选中的待办项（不再显示，不入维修记录）。</summary>
    private void DismissSelected()
    {
        if (_list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is not TodoItem todo) return;

        if (MessageBox.Show(
                $"确定忽略故障项「{todo.FailItem}」？\n它将不再出现在待办列表，但不会创建维修记录。",
                "忽略待办", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        try
        {
            _engine.Db.DismissTodo(todo.FailItem, todo.StationId, todo.Model);
            Logger.Info($"[待办] 已忽略: {todo.FailItem}");
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"忽略失败: {ex.Message}", "错误");
        }
    }

    private void List_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (_list.SelectedItems.Count == 0) return;
        ConfirmSelected();
    }

    private void List_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || _list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is not TodoItem todo) return;

        var menu = new ContextMenuStrip { Font = Font };
        menu.Items.Add($"#{todo.FailItem}") .Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("确认问题（建维修记录）", null, (_, _) => ConfirmSelected());
        menu.Items.Add("忽略（不再提醒）", null, (_, _) => DismissSelected());
        menu.Show(_list, e.Location);
    }

    private static string ShortTime(string? ts)
    {
        if (string.IsNullOrWhiteSpace(ts)) return "—";
        if (!DateTime.TryParse(ts, out var dt)) return ts.Length > 16 ? ts[..16] : ts;
        return dt.ToString("MM-dd HH:mm");
    }
}

// ==================================================================
//  确认问题补填框（轻量版，只填维修人 + 严重度）
// ==================================================================
/// <summary>
/// 从待办确认问题时弹出，补填维修人与严重度。
/// 确定 → 创建一条「未完成」维修记录，进入看板工作流。
/// </summary>
public class TodoConfirmForm : Form
{
    public string Resolver => _resolver.Text.Trim();
    public string Severity => MaintenanceMeta.SeverityKeyOf(_severity.SelectedItem?.ToString());

    private readonly ComboBox _resolver;
    private readonly ComboBox _severity;

    public TodoConfirmForm(TodoItem todo, Database? db = null)
    {
        Text = $"确认问题 — {todo.FailItem}";
        Width = 460; Height = 260;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9F);

        var head = new Label
        {
            Dock = DockStyle.Top, Height = 52, Padding = new Padding(14, 8, 14, 0),
            Text = $"故障项：{todo.FailItem}\n已出现 {todo.OccurrenceCount} 次，最近 {ShortTime(todo.LastSeen)}",
            Font = new Font("Microsoft YaHei UI", 9.5F),
            ForeColor = Color.FromArgb(60, 70, 88),
            AutoEllipsis = true,
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(14, 6, 14, 6),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        _resolver = new ComboBox
        {
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown,
            Margin = new Padding(3, 5, 3, 5),
        };
        if (db != null)
        {
            try
            {
                var pool = db.ListResolvers();
                if (pool.Count > 0) _resolver.Items.AddRange(pool.Cast<object>().ToArray());
            }
            catch { }
        }
        _severity = new ComboBox
        {
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(3, 5, 3, 5),
        };
        _severity.Items.AddRange(MaintenanceMeta.SeverityOrderZh.Cast<object>().ToArray());
        _severity.SelectedIndex = 0;

        layout.Controls.Add(MakeLabel("维修人员:"), 0, 0);
        layout.Controls.Add(_resolver, 1, 0);
        layout.Controls.Add(MakeLabel("严重程度:"), 0, 1);
        layout.Controls.Add(_severity, 1, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 8, 10, 8),
        };
        var ok = new Button { Text = "确认，建维修记录", Width = 148, Height = 28, DialogResult = DialogResult.None };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_resolver.Text))
            {
                MessageBox.Show("请填写或选择维修人员。", "提示");
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        var cancel = new Button { Text = "取消", Width = 80, Height = 28, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange(new Control[] { ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;

        var note = new Label
        {
            Dock = DockStyle.Bottom, Height = 20,
            Padding = new Padding(14, 0, 14, 0),
            ForeColor = Color.FromArgb(140, 150, 168),
            Text = "确定后自动创建「未完成」维修记录，可在看板中继续处理。",
        };

        Controls.Add(layout);
        Controls.Add(note);
        Controls.Add(buttons);
        Controls.Add(head);
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private static string ShortTime(string? ts)
    {
        if (string.IsNullOrWhiteSpace(ts)) return "—";
        if (!DateTime.TryParse(ts, out var dt)) return ts.Length > 16 ? ts[..16] : ts;
        return dt.ToString("MM-dd HH:mm");
    }
}

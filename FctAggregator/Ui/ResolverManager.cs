namespace FctAggregator;

public static class ResolverUtil
{
    public const string Sep = "、";

    private static readonly char[] Splitters = { '\u3001', ',', '\uff0c', '/', '\uff0f', ';', '\uff1b', '|', '\u3002' };

    public static List<string> Split(string? field)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(field)) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in field.Split(Splitters, StringSplitOptions.RemoveEmptyEntries))
        {
            var n = part.Trim();
            if (n.Length == 0) continue;
            if (seen.Add(n)) result.Add(n);
        }
        return result;
    }

    public static string Join(IEnumerable<string>? names)
    {
        if (names == null) return "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var n in names)
        {
            var t = (n ?? "").Trim();
            if (t.Length == 0) continue;
            if (seen.Add(t)) list.Add(t);
        }
        return string.Join(Sep, list);
    }

    public static string Normalize(string? field) => Join(Split(field));

    public static bool Contains(string? field, string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return false;
        return Split(field).Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
    }

    public static string Replace(string? field, string oldName, string newName)
    {
        oldName = (oldName ?? "").Trim();
        newName = (newName ?? "").Trim();
        if (oldName.Length == 0 || newName.Length == 0) return field ?? "";
        var list = Split(field);
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], oldName, StringComparison.OrdinalIgnoreCase))
                list[i] = newName;
        return Join(list);
    }
}

public class ResolverManagerForm : Form
{
    private readonly Database _db;
    private readonly ListBox _list = new();
    private readonly TextBox _name = new();
    private readonly Label _hint = new();

    public bool Changed { get; private set; }

    public ResolverManagerForm(Database db, string? preset = null)
    {
        _db = db;

        Text = "维修人员";
        Width = 420;
        Height = 420;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildUi();
        _name.Text = (preset ?? "").Trim();
        Reload();
    }

    private void BuildUi()
    {
        var lbl = new Label
        {
            Left = 14, Top = 12, Width = 380, Height = 20,
            Text = "输入姓名 → 【添加】即可，之后在维修记录里直接下拉选。",
            ForeColor = Color.FromArgb(140, 140, 140),
        };

        _name.Left = 14; _name.Top = 38; _name.Width = 250;
        _name.PlaceholderText = "维修人员姓名";
        _name.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Add(); }
        };

        var btnAdd = new Button
        {
            Left = 272, Top = 37, Width = 120, Height = 25, Text = "添加",
            BackColor = Color.FromArgb(200, 16, 46), ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        btnAdd.Click += (_, _) => Add();

        _list.Left = 14; _list.Top = 74; _list.Width = 378; _list.Height = 240;
        _list.IntegralHeight = false;
        _list.DoubleClick += (_, _) => Rename();

        var btnRename = new Button { Left = 14, Top = 322, Width = 90, Height = 28, Text = "改名..." };
        btnRename.Click += (_, _) => Rename();
        var btnDel = new Button { Left = 110, Top = 322, Width = 90, Height = 28, Text = "删除" };
        btnDel.Click += (_, _) => Del();

        _hint.Left = 14; _hint.Top = 354; _hint.Width = 280; _hint.Height = 18;
        _hint.ForeColor = Color.DimGray;

        var btnClose = new Button
        {
            Left = 302, Top = 322, Width = 90, Height = 28, Text = "关闭",
            DialogResult = DialogResult.OK,
        };

        AcceptButton = btnAdd;
        CancelButton = btnClose;
        Controls.AddRange(new Control[] { lbl, _name, btnAdd, _list, btnRename, btnDel, _hint, btnClose });
    }

    private void Reload()
    {
        var roster = _db.RosterResolvers();
        var history = _db.DistinctResolvers();
        var inRoster = new HashSet<string>(roster, StringComparer.OrdinalIgnoreCase);

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var n in roster) _list.Items.Add(n);
        foreach (var n in history)
            if (!inRoster.Contains(n)) _list.Items.Add(n + "   (仅历史记录)");
        _list.EndUpdate();

        _hint.Text = $"名单 {roster.Count} 人" +
                     (history.Count(h => !inRoster.Contains(h)) is var extra && extra > 0
                        ? $"，另有 {extra} 个只出现在历史记录里" : "");
    }

    private string? Selected()
    {
        if (_list.SelectedItem is not string s) return null;
        int i = s.IndexOf("   (", StringComparison.Ordinal);
        return (i > 0 ? s[..i] : s).Trim();
    }

    private void Add()
    {
        var n = _name.Text.Trim();
        if (n.Length == 0) { MessageBox.Show("请输入姓名。", "提示"); _name.Focus(); return; }
        if (n.Length > 32) { MessageBox.Show("姓名过长（最多 32 个字）。", "提示"); return; }
        try
        {
            if (_db.AddResolver(n))
            {
                Changed = true;
                Logger.Info($"维修人员名单新增: {n}");
                _name.Clear();
                Reload();
                for (int i = 0; i < _list.Items.Count; i++)
                    if (string.Equals(_list.Items[i]?.ToString(), n, StringComparison.OrdinalIgnoreCase))
                    { _list.SelectedIndex = i; break; }
            }
            else
            {
                MessageBox.Show($"「{n}」已经在名单里了。", "提示");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"新增维修人员失败: {ex.Message}");
            MessageBox.Show("添加失败: " + ex.Message, "提示");
        }
        _name.Focus();
    }

    private void Rename()
    {
        var old = Selected();
        if (old == null) { MessageBox.Show("请先在列表里选一个人。", "提示"); return; }

        using var dlg = new RenameResolverForm(old, _db.CountRecordsByResolver(old));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var neu = dlg.NewName;
        if (neu.Length == 0 || string.Equals(neu, old, StringComparison.Ordinal)) return;

        try
        {
            int synced = _db.RenameResolver(old, neu, dlg.SyncRecords);
            Changed = true;
            Logger.Info($"维修人员改名: {old} -> {neu}" + (dlg.SyncRecords ? $"，同步历史记录 {synced} 条" : ""));
            Reload();
            MessageBox.Show(dlg.SyncRecords
                ? $"已改名，并同步了 {synced} 条历史记录。"
                : "已改名（历史记录里的旧名字未改动）。", "完成");
        }
        catch (Exception ex)
        {
            Logger.Error($"维修人员改名失败: {ex.Message}");
            MessageBox.Show("改名失败: " + ex.Message, "提示");
        }
    }

    private void Del()
    {
        var n = Selected();
        if (n == null) { MessageBox.Show("请先在列表里选一个人。", "提示"); return; }

        int used = _db.CountRecordsByResolver(n);
        var msg = $"从名单里删除「{n}」？";
        if (used > 0)
            msg += $"\n\n注意：他在 {used} 条历史维修记录里出现过，" +
                   "**这些记录不会被改动**（只是以后下拉里不再默认出现）。";
        if (MessageBox.Show(msg, "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        try
        {
            if (_db.DeleteResolver(n))
            {
                Changed = true;
                Logger.Info($"维修人员名单删除: {n}（历史记录 {used} 条未改动）");
                Reload();
            }
            else
            {
                MessageBox.Show($"「{n}」不在名单里（可能只出现在历史记录中，无法删除）。", "提示");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"删除维修人员失败: {ex.Message}");
            MessageBox.Show("删除失败: " + ex.Message, "提示");
        }
    }
}

public class RenameResolverForm : Form
{
    public string NewName => _name.Text.Trim();
    public bool SyncRecords => _sync.Checked;

    private readonly TextBox _name = new();
    private readonly CheckBox _sync = new();

    public RenameResolverForm(string oldName, int usedCount)
    {
        Text = $"改名 — {oldName}";
        Width = 400; Height = 210;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9F);

        var lbl = new Label { Left = 14, Top = 16, Width = 80, Text = "新名字:", TextAlign = ContentAlignment.MiddleLeft };
        _name.Left = 96; _name.Top = 13; _name.Width = 270; _name.Text = oldName;
        _name.SelectAll();

        _sync.Left = 16; _sync.Top = 52; _sync.Width = 350; _sync.Height = 40;
        _sync.Checked = false;
        _sync.Text = usedCount > 0
            ? $"同时把 {usedCount} 条历史维修记录里的「{oldName}」也改掉（治错别字）"
            : "同时改历史记录（当前没有用到这个名字的记录）";
        _sync.Enabled = usedCount > 0;

        var ok = new Button { Left = 186, Top = 118, Width = 88, Height = 30, Text = "确定", DialogResult = DialogResult.OK };
        var cancel = new Button { Left = 280, Top = 118, Width = 88, Height = 30, Text = "取消", DialogResult = DialogResult.Cancel };
        AcceptButton = ok; CancelButton = cancel;

        Controls.AddRange(new Control[] { lbl, _name, _sync, ok, cancel });
    }
}

public class ResolverPickerForm : Form
{
    public string Result { get; private set; } = "";

    public List<string> SelectedNames { get; private set; } = new();

    private readonly Database _db;
    private readonly CheckedListBox _list = new();
    private readonly TextBox _newName = new();
    private readonly TextBox _filter = new();
    private readonly Label _hint = new();
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _all = new();
    private bool _binding;

    public ResolverPickerForm(Database db, string? current)
    {
        _db = db;
        foreach (var n in ResolverUtil.Split(current)) _checked.Add(n);

        Text = "选择维修人员（可多选）";
        Width = 430;
        Height = 480;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildUi();
        Reload();
    }

    private void BuildUi()
    {
        var tip = new Label
        {
            Left = 14, Top = 10, Width = 396, Height = 18,
            Text = "勾选一个或多个 —— 多人会存成「张三、李四」。",
            ForeColor = Color.FromArgb(140, 140, 140),
        };

        var lblF = new Label { Left = 14, Top = 34, Width = 40, Text = "过滤:", TextAlign = ContentAlignment.MiddleRight };
        _filter.Left = 58; _filter.Top = 32; _filter.Width = 160;
        _filter.PlaceholderText = "姓名关键字";
        _filter.TextChanged += (_, _) => Rebind();

        var btnAll = new Button { Left = 226, Top = 31, Width = 58, Height = 24, Text = "全选" };
        btnAll.Click += (_, _) => SetFilteredChecked(true);
        var btnNone = new Button { Left = 288, Top = 31, Width = 58, Height = 24, Text = "清空" };
        btnNone.Click += (_, _) => SetFilteredChecked(false);
        var btnManage = new Button { Left = 350, Top = 31, Width = 60, Height = 24, Text = "管理" };
        btnManage.Click += (_, _) =>
        {
            using var dlg = new ResolverManagerForm(_db);
            dlg.ShowDialog(this);
            if (dlg.Changed) Reload();
        };

        _list.Left = 14; _list.Top = 62; _list.Width = 396; _list.Height = 296;
        _list.CheckOnClick = true;
        _list.IntegralHeight = false;
        _list.ItemCheck += (_, e) =>
        {
            if (_binding) return;
            if (e.Index < 0 || e.Index >= _list.Items.Count) return;
            var name = _list.Items[e.Index]?.ToString() ?? "";
            if (e.NewValue == CheckState.Checked) _checked.Add(name); else _checked.Remove(name);
            if (IsHandleCreated) BeginInvoke(UpdateHint); else UpdateHint();
        };

        var lblNew = new Label { Left = 14, Top = 366, Width = 58, Text = "新增:", TextAlign = ContentAlignment.MiddleRight };
        _newName.Left = 76; _newName.Top = 364; _newName.Width = 190;
        _newName.PlaceholderText = "输入新人员姓名";
        _newName.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddAndCheck(); } };
        var btnAdd = new Button
        {
            Left = 272, Top = 363, Width = 138, Height = 25, Text = "添加并勾选",
            BackColor = Color.FromArgb(200, 16, 46), ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        btnAdd.Click += (_, _) => AddAndCheck();

        _hint.Left = 14; _hint.Top = 394; _hint.Width = 230; _hint.Height = 34;
        _hint.ForeColor = Color.DimGray;

        var ok = new Button
        {
            Left = 244, Top = 400, Width = 80, Height = 30, Text = "确定",
            DialogResult = DialogResult.OK, Font = new Font(Font, FontStyle.Bold),
        };
        ok.Click += (_, _) =>
        {
            SelectedNames = _all.Where(n => _checked.Contains(n)).ToList();
            foreach (var n in _checked)
                if (!SelectedNames.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase)))
                    SelectedNames.Add(n);
            Result = ResolverUtil.Join(SelectedNames);
        };
        var cancel = new Button
        {
            Left = 330, Top = 400, Width = 80, Height = 30, Text = "取消",
            DialogResult = DialogResult.Cancel,
        };
        AcceptButton = ok; CancelButton = cancel;

        Controls.AddRange(new Control[]
        {
            tip, lblF, _filter, btnAll, btnNone, btnManage, _list,
            lblNew, _newName, btnAdd, _hint, ok, cancel,
        });
    }

    private void AddAndCheck()
    {
        var n = _newName.Text.Trim();
        if (n.Length == 0) { MessageBox.Show("请输入姓名。", "提示"); _newName.Focus(); return; }
        if (n.Length > 32) { MessageBox.Show("姓名过长（最多 32 个字）。", "提示"); return; }
        try
        {
            _db.AddResolver(n);
            _checked.Add(n);
            _newName.Clear();
            Reload();
            Logger.Info($"维修人员名单新增(选择框内): {n}");
        }
        catch (Exception ex)
        {
            Logger.Error($"新增维修人员失败: {ex.Message}");
            MessageBox.Show("添加失败: " + ex.Message, "提示");
        }
        _newName.Focus();
    }

    private void Reload()
    {
        try { _all = _db.ListResolvers(); }
        catch (Exception ex) { Logger.Warning($"读取维修人候选失败: {ex.Message}"); _all = new List<string>(); }
        foreach (var n in _checked)
            if (!_all.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))) _all.Add(n);
        Rebind();
    }

    private IEnumerable<string> Filtered()
    {
        var kw = _filter.Text.Trim();
        return kw.Length == 0 ? _all
             : _all.Where(n => n.Contains(kw, StringComparison.OrdinalIgnoreCase));
    }

    private void Rebind()
    {
        _binding = true;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var n in Filtered())
            {
                int i = _list.Items.Add(n);
                _list.SetItemChecked(i, _checked.Contains(n));
            }
        }
        finally { _list.EndUpdate(); _binding = false; }
        UpdateHint();
    }

    private void UpdateHint()
    {
        var picked = _all.Where(n => _checked.Contains(n)).ToList();
        _hint.Text = $"候选 {_all.Count} 人，已勾选 {_checked.Count} 人\r\n" +
                     (picked.Count == 0 ? "（未选人 = 未指派）"
                                        : "→ " + ResolverUtil.Join(picked));
    }

    public IReadOnlyList<string> AllCandidates => _all;
    public int CheckedCount => _checked.Count;
    public void SetFilterForTest(string kw) => _filter.Text = kw;
    public void CheckForTest(string name) { _checked.Add(name); Rebind(); }
    public string BuildResultForTest()
    {
        SelectedNames = _all.Where(n => _checked.Contains(n)).ToList();
        Result = ResolverUtil.Join(SelectedNames);
        return Result;
    }

    private void SetFilteredChecked(bool on)
    {
        foreach (var n in Filtered().ToList())
        {
            if (on) _checked.Add(n); else _checked.Remove(n);
        }
        Rebind();
    }
}

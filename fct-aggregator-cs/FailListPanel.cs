namespace FctAggregator;

/// <summary>
/// FAIL 记录页（v3.1.0.3：树形 + 表格双视图）。
/// <code>
/// ┌ [刷新] [树形|表格] 共 N 条 FAIL · M 个失败项  [搜索框.......] 双击 SN 查看 XML ┐
/// ├──────────────────────────────────────────────────────────────────────────┤
/// │ 表格视图：失败项（按次数倒序、高频标红） | SN | 型号 | 时间                 │
/// │ 树形视图：▾ 失败项 [N 次] → SN · 型号 · 时间                                │
/// └──────────────────────────────────────────────────────────────────────────┘
/// </code>
/// 两种视图共用：搜索即时过滤（失败项/SN/型号）、双击打开 XML 报告。
/// </summary>
public sealed class FailListPanel : Panel
{
    private readonly Engine _engine;
    private TreeView _tree = null!;
    private DataGridView _grid = null!;
    private TextBox _search = null!;
    private Label _countLabel = null!;
    private Button _btnTree = null!, _btnTable = null!;
    private ComboBox _sort = null!;

    private const string SortGroup = "按失败项分组";
    private const string SortTime = "按时间倒序";
    private bool TimeMode => _sort.SelectedIndex == 1;

    /// <summary>最近一次加载的 FAIL 记录（过滤时重建视图用）。</summary>
    private List<FailRecord> _all = new();

    /// <summary>按失败项聚合后的分组（按次数倒序）。</summary>
    private List<GroupData> _groups = new();

    private bool _treeMode = false;   // 默认表格视图（横向规范列对齐）

    private sealed class GroupData
    {
        public string FailItem = "";
        public int Count;
        public List<FailRecord> Records = new();
    }

    public FailListPanel(Engine engine)
    {
        _engine = engine;
        BuildUi();
    }

    // =================================================================
    //  界面
    // =================================================================
    private void BuildUi()
    {
        Padding = new Padding(Theme.Gap);
        BackColor = Theme.Bg;

        // ---- 工具栏：TableLayoutPanel 横向规范各元素位置 ----
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 44, ColumnCount = 7, RowCount = 1,
            BackColor = Theme.Bg, Padding = new Padding(0, 7, 0, 5),
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 0 刷新
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 1 树形
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 2 表格
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 3 计数
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // 4 搜索框
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 5 提示
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 6 排序方式

        var btnRefresh = Theme.MakeButton("刷新", 76);
        btnRefresh.Margin = new Padding(0, 0, 6, 0);
        btnRefresh.Click += (_, _) => Refresh2();
        bar.Controls.Add(btnRefresh, 0, 0);

        _btnTree = Theme.MakeButton("树形", 52);
        _btnTable = Theme.MakeButton("表格", 52, primary: true);
        _btnTree.Margin = new Padding(0, 0, 2, 0);
        _btnTable.Margin = new Padding(0, 0, 8, 0);
        _btnTree.Click += (_, _) => SetView(tree: true);
        _btnTable.Click += (_, _) => SetView(tree: false);
        bar.Controls.Add(_btnTree, 1, 0);
        bar.Controls.Add(_btnTable, 2, 0);

        _countLabel = new Label
        {
            Text = "", AutoSize = true, ForeColor = Theme.TextSub,
            Font = Theme.BodyBold, Margin = new Padding(6, 6, 0, 0),
        };
        bar.Controls.Add(_countLabel, 3, 0);

        _search = new TextBox
        {
            Width = 260, PlaceholderText = "搜索失败项 / SN / 型号",
            Font = Theme.Body, BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(16, 2, 0, 0), Anchor = AnchorStyles.Left,
        };
        _search.TextChanged += (_, _) => RebuildActiveView();
        bar.Controls.Add(_search, 4, 0);

        var hint = new Label
        {
            Text = "双击行查看 XML", AutoSize = true,
            ForeColor = Theme.TextFaint, Font = Theme.Small,
            Margin = new Padding(16, 8, 0, 0), Anchor = AnchorStyles.Left,
        };
        bar.Controls.Add(hint, 5, 0);

        // ---- 排序方式: 按失败项分组(默认) / 按时间倒序 ----
        _sort = new ComboBox
        {
            Width = 130, DropDownStyle = ComboBoxStyle.DropDownList,
            Font = Theme.Body, Margin = new Padding(12, 2, 0, 0), Anchor = AnchorStyles.Left,
        };
        _sort.Items.AddRange(new object[] { SortGroup, SortTime });
        _sort.SelectedIndex = 0;
        _sort.SelectedIndexChanged += (_, _) => RebuildActiveView();
        bar.Controls.Add(_sort, 6, 0);

        // ---- 表格视图（默认）----
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            EnableHeadersVisualStyles = false, BorderStyle = BorderStyle.FixedSingle,
            BackgroundColor = Theme.Surface, Font = Theme.Body,
            ColumnHeadersHeight = 30,
        };
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.SurfaceHi;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.Font = Theme.BodyBold;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(52, 66, 88);
        _grid.DefaultCellStyle.SelectionForeColor = Theme.TextMain;
        _grid.Columns.Add("item", "失败项");
        _grid.Columns["item"]!.Width = 430;
        _grid.Columns["item"]!.MinimumWidth = 260;
        _grid.Columns.Add("sn", "SN");
        _grid.Columns["sn"]!.Width = 300;
        _grid.Columns.Add("model", "型号");
        _grid.Columns["model"]!.Width = 110;
        _grid.Columns.Add("time", "时间");
        _grid.Columns["time"]!.Width = 210;
        _grid.Columns["time"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _grid.CellDoubleClick += Grid_CellDoubleClick;

        // ---- 树形视图 ----
        _tree = new TreeView
        {
            Dock = DockStyle.Fill, BackColor = Theme.Surface, Font = Theme.Body,
            FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.FixedSingle,
            ShowLines = true, ShowPlusMinus = true, ShowRootLines = true,
            Indent = 18, LineColor = Theme.Border,
        };
        _tree.DoubleClick += Tree_DoubleClick;

        Controls.Add(_grid);
        Controls.Add(_tree);
        Controls.Add(bar);
        _tree.Visible = false;
    }

    /// <summary>切换 树形/表格 视图，并同步按钮高亮。</summary>
    private void SetView(bool tree)
    {
        if (_treeMode == tree) return;
        _treeMode = tree;
        _btnTree.BackColor = tree ? Theme.Primary : Theme.Surface;
        _btnTree.ForeColor = tree ? Color.White : Theme.TextMain;
        _btnTable.BackColor = tree ? Theme.Surface : Theme.Primary;
        _btnTable.ForeColor = tree ? Theme.TextMain : Color.White;
        _tree.Visible = tree;
        _grid.Visible = !tree;
        RebuildActiveView();
    }

    // =================================================================
    //  数据加载
    // =================================================================
    public void Refresh2()
    {
        try
        {
            _all = _engine.Db.AllFails(_engine.ResolvedStationId);
            ComputeGroups();
            RebuildActiveView();
        }
        catch (Exception ex)
        {
            Logger.Error($"FAIL 记录加载失败: {ex.Message}");
        }
    }

    /// <summary>按失败项聚合：组内时间倒序，组按次数倒序。
    /// ⚠ 排序必须用统一格式后的时间：TestDate 是 8 位数字、Timestamp 是 ISO，原始字符串比较会错乱。</summary>
    private void ComputeGroups()
    {
        _groups = _all
            .GroupBy(f => f.FailItem)
            .Select(g => new GroupData
            {
                FailItem = g.Key,
                Count = g.Count(),
                Records = g.OrderByDescending(r => TimeUtil.Normalize(r.TestDate))
                           .ThenByDescending(r => TimeUtil.Normalize(r.Timestamp))
                           .ToList(),
            })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.FailItem, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>时间列显示: 优先 batch 时间, 退目录日期; 统一格式(yyyy-MM-dd HH:mm:ss)。</summary>
    private static string DisplayTime(FailRecord f) =>
        TimeUtil.Normalize(f.Timestamp) is { Length: > 0 } t ? t : TimeUtil.Normalize(f.TestDate);

    private List<string> FilterKeys => _search.Text.Trim()
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();    private bool Hit(string s) => FilterKeys.All(k => s.Contains(k, StringComparison.OrdinalIgnoreCase));

    private void RebuildActiveView()
    {
        _countLabel.Text = TimeMode
            ? $"共 {_all.Count} 条 FAIL · 按时间倒序"
            : $"共 {_all.Count} 条 FAIL · {_groups.Count} 个失败项";
        if (_treeMode) BuildTree();
        else BuildGrid();
    }

    // =================================================================
    //  表格视图
    // =================================================================
    private void BuildGrid()
    {
        var keys = FilterKeys;
        bool hasFilter = keys.Count > 0;

        _grid.SuspendLayout();
        _grid.Rows.Clear();
        try
        {
            int hitCount = 0;
            if (TimeMode)
            {
                // 平铺: 全部记录按时间倒序(日期 → 时间)。失败项次数从分组取, 高频仍标色
                var counts = _groups.ToDictionary(g => g.FailItem, g => g.Count, StringComparer.OrdinalIgnoreCase);
                var rows = _all.OrderByDescending(r => TimeUtil.Normalize(r.TestDate))
                               .ThenByDescending(r => TimeUtil.Normalize(r.Timestamp))
                               .ToList();
                int i = 0;
                foreach (var f in rows)
                {
                    if (hasFilter && !(Hit(f.FailItem) || Hit(f.Sn) || Hit(f.Model))) continue;
                    hitCount++;
                    AddGridRow(f, counts.GetValueOrDefault(f.FailItem), i++);
                }
            }
            else
            {
                int gi = 0;   // 分组序号（隔组浅色底纹，视觉上区分区块）
                foreach (var g in _groups)
                {
                    bool grpHit = !hasFilter || Hit(g.FailItem);
                    var shown = grpHit
                        ? g.Records
                        : g.Records.Where(r => Hit(r.Sn) || Hit(r.Model)).ToList();
                    if (!grpHit && shown.Count == 0) continue;
                    hitCount += shown.Count;
                    foreach (var f in shown) AddGridRow(f, g.Count, gi);
                    gi++;
                }
            }
            if (hasFilter)
                _countLabel.Text = $"共 {_all.Count} 条 FAIL · 过滤命中 {hitCount} 条";
        }
        finally
        {
            _grid.ResumeLayout();
        }
    }

    /// <summary>表格加一行(两模式共用)。count = 该失败项总次数, gi = 分区序号(隔区浅色底纹)。</summary>
    private void AddGridRow(FailRecord f, int count, int gi)
    {
        var alt = gi % 2 == 0 ? Theme.Surface : Color.FromArgb(247, 248, 250);
        var fg = count >= 5 ? Theme.Danger
               : count >= 2 ? Color.FromArgb(251, 146, 60)
               : Theme.TextMain;
        int i = _grid.Rows.Add();
        var r = _grid.Rows[i];
        r.Tag = f.XmlPath;
        r.DefaultCellStyle.BackColor = alt;
        r.Cells[0].Value = f.FailItem;
        r.Cells[0].Style.Font = Theme.BodyBold;
        r.Cells[0].Style.ForeColor = fg;
        r.Cells[1].Value = f.Sn;
        r.Cells[2].Value = f.Model;
        r.Cells[3].Value = DisplayTime(f);
    }

    // =================================================================
    //  树形视图
    // =================================================================
    private void BuildTree()
    {
        var keys = FilterKeys;
        bool hasFilter = keys.Count > 0;

        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        try
        {
            int hitCount = 0;
            if (TimeMode)
            {
                // 按日期分组: 每天一个父节点, 组内按时间倒序
                var rows = _all.OrderByDescending(r => TimeUtil.Normalize(r.TestDate))
                               .ThenByDescending(r => TimeUtil.Normalize(r.Timestamp))
                               .ToList();
                foreach (var day in rows.GroupBy(r => TimeUtil.Normalize(r.TestDate)[..10]))
                {
                    var dn = new TreeNode($"{day.Key}   [{day.Count()} 次]")
                    {
                        NodeFont = Theme.BodyBold,
                        ForeColor = Theme.TextMain,
                    };
                    foreach (var f in day)
                    {
                        if (hasFilter && !(Hit(f.FailItem) || Hit(f.Sn) || Hit(f.Model))) continue;
                        hitCount++;
                        var time = DisplayTime(f);
                        var label = $"{f.Sn}  ·  {f.Model}  ·  {time}";
                        var cn = new TreeNode(label)
                        {
                            Tag = f.XmlPath,
                            ForeColor = Theme.TextMain,
                            BackColor = Theme.Surface,
                        };
                        dn.Nodes.Add(cn);
                    }
                    if (dn.Nodes.Count == 0) continue;
                    _tree.Nodes.Add(dn);
                    dn.Expand();
                }
            }
            else
            {
                foreach (var g in _groups)
                {
                    bool grpHit = !hasFilter || Hit(g.FailItem);
                    var shown = grpHit
                        ? g.Records
                        : g.Records.Where(r => Hit(r.Sn) || Hit(r.Model)).ToList();
                    if (!grpHit && shown.Count == 0) continue;
                    hitCount += shown.Count;

                    var gn = new TreeNode($"{g.FailItem}   [{g.Count} 次]")
                    {
                        Tag = g.FailItem,
                        NodeFont = Theme.BodyBold,
                    };
                    if (g.Count >= 5) gn.ForeColor = Theme.Danger;
                    else if (g.Count >= 2) gn.ForeColor = Color.FromArgb(251, 146, 60);

                    foreach (var f in shown)
                    {
                        var time = DisplayTime(f);
                        var label = string.IsNullOrEmpty(f.Model)
                            ? $"{f.Sn}   {time}"
                            : $"{f.Sn}  ·  {f.Model}  ·  {time}";
                        var cn = new TreeNode(label)
                        {
                            Tag = f.XmlPath,
                            ForeColor = Theme.TextMain,
                            NodeFont = grpHit ? null : Theme.BodyBold,
                            BackColor = grpHit ? Theme.Surface : Color.FromArgb(56, 52, 38),
                        };
                        gn.Nodes.Add(cn);
                    }
                    _tree.Nodes.Add(gn);
                    gn.Expand();
                }
            }
            if (hasFilter)
                _countLabel.Text = $"共 {_all.Count} 条 FAIL · 过滤命中 {hitCount} 条";
        }
        finally
        {
            _tree.EndUpdate();
        }
    }

    // =================================================================
    //  双击打开 XML
    // =================================================================
    private void Grid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].Tag is string p) OpenXml(p);
    }

    private void Tree_DoubleClick(object? sender, EventArgs e)
    {
        if (_tree.SelectedNode?.Tag is not string tag) return;
        if (!tag.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return;  // 组节点 Tag=失败项名
        OpenXml(tag);
    }

    private void OpenXml(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            MessageBox.Show($"文件不存在:\n{path}", "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        using var dlg = new FctFailRanker.XmlViewerForm(path);
        dlg.ShowDialog();
    }
}

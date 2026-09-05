namespace FctAggregator;

public sealed class FailListPanel : Panel
{
    private readonly Engine _engine;
    private TreeView _tree = null!;
    private DataGridView _grid = null!;
    private TextBox _search = null!;
    private Label _countLabel = null!;
    private Button _btnTree = null!, _btnTable = null!;
    private ComboBox _sort = null!;

    public const string SortGroup = "按失败项分组";
    public const string SortSn = "按SN合并问题";
    public const string SortTime = "按时间倒序";
    private bool SnMode => (_sort.SelectedItem as string) == SortSn;
    private bool TimeMode => (_sort.SelectedItem as string) == SortTime;

    private List<FailRecord> _all = new();

    private List<GroupData> _groups = new();

    private List<SnGroupData> _snGroups = new();

    private bool _treeMode = false;

    private sealed class GroupData
    {
        public string FailItem = "";
        public int Count;
        public List<FailRecord> Records = new();
    }

    private sealed class SnGroupData
    {
        public string Sn = "";
        public string Model = "";
        public string LatestTime = "";
        public string PrimaryXmlPath = "";
        public List<string> FailItems = new();
        public List<FailRecord> Records = new();
    }

    public FailListPanel(Engine engine)
    {
        _engine = engine;
        BuildUi();
    }

    private void BuildUi()
    {
        Padding = new Padding(Theme.Gap);
        BackColor = Theme.Bg;

        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 44, ColumnCount = 7, RowCount = 1,
            BackColor = Theme.Bg, Padding = new Padding(0, 7, 0, 5),
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

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

        _sort = new ComboBox
        {
            Width = 140, DropDownStyle = ComboBoxStyle.DropDownList,
            Font = Theme.Body, Margin = new Padding(12, 2, 0, 0), Anchor = AnchorStyles.Left,
        };
        _sort.Items.AddRange(new object[] { SortGroup, SortSn, SortTime });
        _sort.SelectedIndex = 0;
        _sort.SelectedIndexChanged += (_, _) => RebuildActiveView();
        bar.Controls.Add(_sort, 6, 0);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            EnableHeadersVisualStyles = false, BorderStyle = BorderStyle.FixedSingle,
            BackgroundColor = Theme.Surface, Font = Theme.Body,
            ColumnHeadersHeight = 30,
        };
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.SurfaceHi;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.TextSub;
        _grid.ColumnHeadersDefaultCellStyle.Font = Theme.BodyBold;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(20, 20, 20);
        _grid.DefaultCellStyle.SelectionForeColor = Color.White;
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

    public void Refresh2()
    {
        try
        {
            _all = _engine.Db.AllFails(_engine.ResolvedStationId);
            ComputeGroups();
            ComputeSnGroups();
            RebuildActiveView();
        }
        catch (Exception ex)
        {
            Logger.Error($"FAIL 记录加载失败: {ex.Message}");
        }
    }

    private void ComputeGroups()
    {
        var cfg = AppConfig.Instance;
        bool merge = cfg.LearnFailMergeEnabled;
        var level = cfg.LearnFailMergeLevel;
        _groups = _all
            .GroupBy(f => merge ? FailReasonMerger.GetMergedKey(f.FailItem, true, level) : f.FailItem)
            .Select(g => new GroupData
            {
                FailItem = g.Key,
                Count = g.Count(),
                Records = g.OrderByDescending(DisplayTime)
                           .ToList(),
            })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.FailItem, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ComputeSnGroups()
    {
        _snGroups = _all
            .GroupBy(f => string.IsNullOrWhiteSpace(f.Sn) ? "—" : f.Sn.Trim())
            .Select(g =>
            {
                var recs = g.OrderByDescending(DisplayTime).ToList();
                var items = recs.Select(r => r.FailItem).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
                var latestRec = recs.FirstOrDefault();
                return new SnGroupData
                {
                    Sn = g.Key,
                    Model = latestRec?.Model ?? "",
                    LatestTime = latestRec != null ? DisplayTime(latestRec) : "",
                    PrimaryXmlPath = latestRec?.XmlPath ?? "",
                    FailItems = items,
                    Records = recs,
                };
            })
            .OrderByDescending(g => g.LatestTime)
            .ThenByDescending(g => g.Records.Count)
            .ToList();
    }

    private static string DisplayTime(FailRecord f)
    {
        var fnTime = TimeUtil.ExtractFileNameTime(f.XmlPath);
        if (fnTime.Length > 0) return fnTime;
        return TimeUtil.Normalize(f.Timestamp) is { Length: > 0 } t ? t : TimeUtil.Normalize(f.TestDate);
    }

    private List<string> FilterKeys => _search.Text.Trim()
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();    private bool Hit(string s) => FilterKeys.All(k => s.Contains(k, StringComparison.OrdinalIgnoreCase));

    private void RebuildActiveView()
    {
        _countLabel.Text = SnMode
            ? $"共 {_all.Count} 条 FAIL · {_snGroups.Count} 个独立 SN（合并问题）"
            : (TimeMode
                ? $"共 {_all.Count} 条 FAIL · 按时间倒序"
                : $"共 {_all.Count} 条 FAIL · {_groups.Count} 个失败项");
        if (_treeMode) BuildTree();
        else BuildGrid();
    }

    private void BuildGrid()
    {
        var keys = FilterKeys;
        bool hasFilter = keys.Count > 0;

        _grid.SuspendLayout();
        _grid.Rows.Clear();
        try
        {
            int hitCount = 0;
            if (SnMode)
            {
                int gi = 0;
                foreach (var g in _snGroups)
                {
                    bool match = !hasFilter || Hit(g.Sn) || Hit(g.Model) || g.FailItems.Any(Hit);
                    if (!match) continue;
                    hitCount += g.Records.Count;

                    var alt = gi % 2 == 0 ? Theme.Surface : Theme.AltRowA;
                    var fg = g.FailItems.Count >= 5 ? Theme.Danger
                           : g.FailItems.Count >= 2 ? Theme.Warning
                           : Theme.TextMain;
                    int i = _grid.Rows.Add();
                    var r = _grid.Rows[i];
                    r.Tag = g.PrimaryXmlPath;
                    r.DefaultCellStyle.BackColor = alt;

                    var itemSummary = string.Join(", ", g.FailItems);
                    if (g.FailItems.Count > 1) itemSummary += $" (共 {g.FailItems.Count} 项)";
                    r.Cells[0].Value = itemSummary;
                    r.Cells[0].Style.Font = Theme.BodyBold;
                    r.Cells[0].Style.ForeColor = fg;
                    r.Cells[1].Value = g.Sn;
                    r.Cells[2].Value = g.Model;
                    r.Cells[3].Value = g.LatestTime;
                    gi++;
                }
            }
            else if (TimeMode)
            {
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
                int gi = 0;
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

    private void AddGridRow(FailRecord f, int count, int gi)
    {
        var alt = gi % 2 == 0 ? Theme.Surface : Theme.AltRowA;
        var fg = count >= 5 ? Theme.Danger
               : count >= 2 ? Theme.Warning
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

    private void BuildTree()
    {
        var keys = FilterKeys;
        bool hasFilter = keys.Count > 0;

        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        try
        {
            int hitCount = 0;
            if (SnMode)
            {
                foreach (var g in _snGroups)
                {
                    bool snHit = !hasFilter || Hit(g.Sn) || Hit(g.Model);
                    var shown = snHit
                        ? g.Records
                        : g.Records.Where(r => Hit(r.FailItem)).ToList();
                    if (!snHit && shown.Count == 0) continue;
                    hitCount += shown.Count;

                    var itemsText = string.Join(", ", g.FailItems);
                    var nodeTitle = $"SN: {g.Sn}   [{g.FailItems.Count} 项失败]  ·  {g.Model}  ·  {g.LatestTime}  ({itemsText})";
                    var snNode = new TreeNode(nodeTitle)
                    {
                        Tag = g.PrimaryXmlPath,
                        NodeFont = Theme.BodyBold,
                    };
                    if (g.FailItems.Count >= 5) snNode.ForeColor = Theme.Danger;
                    else if (g.FailItems.Count >= 2) snNode.ForeColor = Theme.Warning;

                    foreach (var f in shown)
                    {
                        var time = DisplayTime(f);
                        var fn = string.IsNullOrEmpty(f.XmlPath) ? "" : System.IO.Path.GetFileName(f.XmlPath);
                        var label = string.IsNullOrEmpty(fn)
                            ? $"{f.FailItem}   ·   {time}"
                            : $"{f.FailItem}   ·   {time}   ·   {fn}";
                        var cn = new TreeNode(label)
                        {
                            Tag = f.XmlPath,
                            ForeColor = Theme.TextMain,
                            BackColor = Theme.Surface,
                        };
                        snNode.Nodes.Add(cn);
                    }
                    _tree.Nodes.Add(snNode);
                    snNode.Expand();
                }
            }
            else if (TimeMode)
            {
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
                    else if (g.Count >= 2) gn.ForeColor = Theme.Warning;

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
                            BackColor = grpHit ? Theme.Surface : Theme.AltRowB,
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

    private void Grid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].Tag is string p) OpenXml(p);
    }

    private void Tree_DoubleClick(object? sender, EventArgs e)
    {
        if (_tree.SelectedNode?.Tag is not string tag) return;
        if (!tag.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return;
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

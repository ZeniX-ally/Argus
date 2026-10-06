namespace FctAggregator;

public sealed class FailListPanel : Panel
{
    private readonly Engine _engine;
    private DataGridView _grid = null!;
    private TextBox _search = null!;
    private Label _countLabel = null!;

    /// <summary>屏幕表格只取最近 N 条 FAIL 记录再展开其失败项（保持"最近"语义，防大库拖慢界面）。</summary>
    private const int RecentFailRecords = 2000;

    private List<FailItemDetail> _all = new();
    private Dictionary<string, int> _monthCounts = new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _loadBusy;
    private int _loadGen;
    // DisplayTime 内部要跑正则，而它被放进排序比较器会被反复求值：每次加载按明细缓存一次
    private readonly Dictionary<FailItemDetail, string> _dispTimeCache = new();
    private System.Windows.Forms.Timer? _searchDebounce;

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
            Dock = DockStyle.Top, Height = 44, ColumnCount = 4, RowCount = 1,
            BackColor = Theme.Bg, Padding = new Padding(0, 7, 0, 5),
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var btnRefresh = Theme.MakeButton("刷新", 76);
        btnRefresh.Margin = new Padding(0, 0, 6, 0);
        btnRefresh.Click += (_, _) => Refresh2();
        bar.Controls.Add(btnRefresh, 0, 0);

        _countLabel = new Label
        {
            Text = "", AutoSize = true, ForeColor = Theme.TextSub,
            Font = Theme.BodyBold, Margin = new Padding(6, 6, 0, 0),
        };
        bar.Controls.Add(_countLabel, 1, 0);

        _search = new TextBox
        {
            Width = 260, PlaceholderText = "搜索失败项 / SN / 型号",
            Font = Theme.Body, BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(16, 2, 0, 0), Anchor = AnchorStyles.Left,
        };
        _searchDebounce = UiAsync.Debounce(_search, 250, BuildGrid);
        _search.TextChanged += (_, _) =>
        {
            _searchDebounce?.Stop();
            _searchDebounce?.Start();
        };
        bar.Controls.Add(_search, 2, 0);

        var hint = new Label
        {
            Text = "双击行查看 XML", AutoSize = true,
            ForeColor = Theme.TextFaint, Font = Theme.Small,
            Margin = new Padding(16, 8, 0, 0), Anchor = AnchorStyles.Left,
        };
        bar.Controls.Add(hint, 3, 0);

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
        _grid.DefaultCellStyle.SelectionBackColor = Theme.Primary;
        _grid.DefaultCellStyle.SelectionForeColor = Color.White;
        _grid.Columns.Add("item", "项目");
        _grid.Columns["item"]!.Width = 280;
        _grid.Columns["item"]!.MinimumWidth = 160;
        _grid.Columns.Add("month", "本月");
        _grid.Columns["month"]!.Width = 72;
        _grid.Columns.Add("value", "值");
        _grid.Columns["value"]!.Width = 110;
        _grid.Columns.Add("limit", "限值");
        _grid.Columns["limit"]!.Width = 160;
        _grid.Columns.Add("model", "型号");
        _grid.Columns["model"]!.Width = 100;
        _grid.Columns.Add("sn", "SN");
        _grid.Columns["sn"]!.Width = 220;
        _grid.Columns.Add("time", "测试Fail时间");
        _grid.Columns["time"]!.Width = 180;
        _grid.Columns["time"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _grid.CellDoubleClick += Grid_CellDoubleClick;

        Controls.Add(_grid);
        Controls.Add(bar);
    }

    public void Refresh2()
    {
        if (_loadBusy) return;
        _loadBusy = true;
        int gen = ++_loadGen;
        var sid = _engine.ResolvedStationId;
        var db = _engine.Db;
        Task.Run(() =>
        {
            List<FailItemDetail> rows;
            Dictionary<string, int> monthCounts;
            try
            {
                rows = db.QueryFailItemDetails(sid, recentRecords: RecentFailRecords);
                var (from, to) = RepeatFailMonth.Range(DateTime.Today);
                monthCounts = db.CountFailDetailsByName(from, to, sid);
            }
            catch (Exception ex)
            {
                Logger.Error($"FAIL 记录加载失败: {ex.Message}");
                UiAsync.Post(this, () =>
                {
                    try
                    {
                        if (IsDisposed) return;
                        _countLabel.Text = $"加载失败：{ex.Message}（下方显示的仍是上一次的数据，不是最新）";
                    }
                    catch { }
                    finally { _loadBusy = false; }
                });
                return;
            }
            UiAsync.Post(this, () =>
            {
                try
                {
                    if (IsDisposed || gen != _loadGen) return;
                    _all = rows;
                    _monthCounts = monthCounts;
                    _dispTimeCache.Clear();
                    BuildGrid();
                }
                catch (Exception ex) { Logger.Error($"FAIL 记录刷新回写失败: {ex.Message}"); }
                finally { _loadBusy = false; }
            });
        });
    }

    private string DisplayTime(FailItemDetail f)
    {
        if (_dispTimeCache.TryGetValue(f, out var cached)) return cached;
        var fnTime = TimeUtil.ExtractFileNameTime(f.XmlPath);
        var t = fnTime.Length > 0
            ? fnTime
            : (TimeUtil.Normalize(f.Ts) is { Length: > 0 } n ? n : f.Ts);
        _dispTimeCache[f] = t;
        return t;
    }

    private string _filterKeysSrc = "\0";
    private List<string> _filterKeysCache = new();
    private List<string> FilterKeys
    {
        get
        {
            var src = _search.Text;
            if (!string.Equals(_filterKeysSrc, src, StringComparison.Ordinal))
            {
                _filterKeysSrc = src;
                _filterKeysCache = src.Trim()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            return _filterKeysCache;
        }
    }

    private bool Hit(string s) => FilterKeys.All(k => s.Contains(k, StringComparison.OrdinalIgnoreCase));

    private void BuildGrid()
    {
        var keys = FilterKeys;
        bool hasFilter = keys.Count > 0;
        _countLabel.Text = $"{_all.Count} 个失败项 · 按时间倒序";

        var rows = _all.OrderByDescending(DisplayTime).ToList();

        _grid.SuspendLayout();
        _grid.Rows.Clear();
        try
        {
            int hitCount = 0;
            int i = 0;
            foreach (var f in rows)
            {
                if (hasFilter && !(Hit(f.TestName) || Hit(f.Sn) || Hit(f.Model))) continue;
                hitCount++;
                AddGridRow(f, RepeatFailMonth.Lookup(_monthCounts, f.TestName), i++);
            }
            if (hasFilter)
                _countLabel.Text = $"{_all.Count} 个失败项 · 过滤命中 {hitCount} 项";
        }
        finally
        {
            _grid.ResumeLayout();
        }
    }

    private void AddGridRow(FailItemDetail f, int count, int gi)
    {
        var alt = gi % 2 == 0 ? Theme.Surface : Theme.AltRowA;
        var fg = count >= 5 ? Theme.Danger
               : count >= 2 ? Theme.Warning
               : Theme.TextMain;
        int i = _grid.Rows.Add();
        var r = _grid.Rows[i];
        r.Tag = f.XmlPath;
        r.DefaultCellStyle.BackColor = alt;
        if (!f.HasDetail) r.DefaultCellStyle.ForeColor = Theme.TextSub;
        r.Cells[0].Value = f.TestName;
        r.Cells[0].Style.Font = Theme.BodyBold;
        r.Cells[0].Style.ForeColor = fg;
        r.Cells[1].Value = count > 0 ? $"{count} 次" : "";
        r.Cells[1].Style.ForeColor = fg;
        r.Cells[1].Style.Font = count >= RepeatFailMonth.Threshold ? Theme.BodyBold : Theme.Body;
        r.Cells[2].Value = f.ValueDisplay;
        r.Cells[3].Value = f.LimitDisplay;
        r.Cells[4].Value = f.Model;
        r.Cells[5].Value = f.Sn;
        r.Cells[6].Value = DisplayTime(f);
    }

    private void Grid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].Tag is string p) OpenXml(p);
    }

    private void OpenXml(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            MessageBox.Show($"文件不存在:\n{path}", "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        using var dlg = new FctFailRanker.XmlViewerForm(path, _engine.Db);
        dlg.ShowDialog();
    }
}

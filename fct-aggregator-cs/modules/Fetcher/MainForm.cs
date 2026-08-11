using System.Diagnostics;

namespace FctFetcher;

public sealed class MainForm : Form
{
    private readonly TextBox _txtResults = new();
    private readonly TextBox _txtTdms = new();
    private readonly TextBox _txtOut = new();
    private readonly DateTimePicker _dtStart = new();
    private readonly DateTimePicker _dtEnd = new();
    private readonly CheckBox _chkPack = new();
    private readonly CheckBox _chkOnline = new();
    private readonly Button _btnRun = new();
    private readonly Button _btnOpen = new();
    private readonly ProgressBar _bar = new();
    private readonly Label _summary = new();
    private readonly TabControl _tabs = new();
    private readonly DataGridView _grid = new();
    private readonly TextBox _log = new();

    private Config _cfg;
    private string _lastOut = "";
    private List<Record> _recs = new();

    public MainForm()
    {
        _cfg = Config.Load(Program.ConfigPath);

        Text = "FCT-Fetcher  v1.1.0  —  fail 文件捞取器";
        Width = 1080;
        Height = 740;
        MinimumSize = new Size(900, 620);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("微软雅黑", 9F);
        LoadAppIcon();

        int y = 14;
        void Lab(string t, int top) =>
            Controls.Add(new Label { Text = t, Left = 14, Top = top + 3, Width = 96 });

        Lab("Results 根目录", y);
        _txtResults.SetBounds(112, y, 800, 24);
        _txtResults.Text = _cfg.ResultsRoot;
        Controls.Add(_txtResults);
        AddBrowse(920, y, _txtResults);

        y += 32;
        Lab("TDMS 根目录", y);
        _txtTdms.SetBounds(112, y, 800, 24);
        _txtTdms.Text = _cfg.TdmsRoot;
        Controls.Add(_txtTdms);
        AddBrowse(920, y, _txtTdms);

        y += 32;
        Lab("输出目录", y);
        _txtOut.SetBounds(112, y, 800, 24);
        _txtOut.Text = _cfg.ResolveOutputDir(Program.ExeDir);
        Controls.Add(_txtOut);
        AddBrowse(920, y, _txtOut);

        y += 38;
        Lab("日期区间", y);
        _dtStart.SetBounds(112, y, 124, 24);
        _dtStart.Format = DateTimePickerFormat.Custom;
        _dtStart.CustomFormat = "yyyy-MM-dd";
        _dtStart.Value = DateTime.Today.AddDays(-2);
        Controls.Add(_dtStart);

        Controls.Add(new Label { Text = "~", Left = 244, Top = y + 3, Width = 14 });

        _dtEnd.SetBounds(262, y, 124, 24);
        _dtEnd.Format = DateTimePickerFormat.Custom;
        _dtEnd.CustomFormat = "yyyy-MM-dd";
        _dtEnd.Value = DateTime.Today;
        Controls.Add(_dtEnd);

        Controls.Add(new Label
        {
            Text = "(含首尾两天)", Left = 394, Top = y + 3, Width = 84, ForeColor = Color.Gray
        });

        _chkPack.SetBounds(490, y, 250, 24);
        _chkPack.Text = "分类归集并打包为 {日期}.zip";
        _chkPack.Checked = _cfg.PackFiles;
        Controls.Add(_chkPack);

        _chkOnline.SetBounds(750, y, 210, 24);
        _chkOnline.Text = "同时扫 Online (通常不需要)";
        _chkOnline.Checked = _cfg.Categories.Any(
            c => c.Equals("Online", StringComparison.OrdinalIgnoreCase));
        Controls.Add(_chkOnline);

        y += 34;
        _btnRun.SetBounds(112, y, 128, 32);
        _btnRun.Text = "开始捞取";
        _btnRun.Click += OnRun;
        Controls.Add(_btnRun);

        _btnOpen.SetBounds(250, y, 128, 32);
        _btnOpen.Text = "打开输出目录";
        _btnOpen.Enabled = false;
        _btnOpen.Click += (_, _) =>
        {
            if (Directory.Exists(_lastOut)) Process.Start("explorer.exe", _lastOut);
        };
        Controls.Add(_btnOpen);

        _bar.SetBounds(390, y + 6, 570, 20);
        Controls.Add(_bar);

        y += 40;
        _summary.SetBounds(14, y, 1040, 22);
        _summary.Text = "就绪。";
        _summary.Font = new Font("微软雅黑", 9.5F, FontStyle.Bold);
        _summary.ForeColor = Color.FromArgb(31, 78, 121);
        _summary.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_summary);

        y += 26;
        _tabs.SetBounds(14, y, 1040, 620 - y + 60);
        _tabs.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(_tabs);

        var tabRes = new TabPage("捞取结果");
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.BackgroundColor = Color.White;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(68, 114, 196);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("微软雅黑", 9F, FontStyle.Bold);
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = 30;
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 249, 252);
        _grid.CellDoubleClick += OnGridDoubleClick;
        tabRes.Controls.Add(_grid);
        _tabs.TabPages.Add(tabRes);

        var tabLog = new TabPage("日志");
        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.ReadOnly = true;
        _log.BackColor = Color.FromArgb(30, 30, 30);
        _log.ForeColor = Color.FromArgb(220, 220, 220);
        _log.Font = new Font("Consolas", 9F);
        tabLog.Controls.Add(_log);
        _tabs.TabPages.Add(tabLog);

        SetupGridColumns();
    }

    private void SetupGridColumns()
    {
        void Col(string name, string header, int width,
                 DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
        {
            var c = new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, Width = width,
                SortMode = DataGridViewColumnSortMode.Automatic,
            };
            c.DefaultCellStyle.Alignment = align;
            _grid.Columns.Add(c);
        }
        var C = DataGridViewContentAlignment.MiddleCenter;
        Col("Sn", "SN", 250, C);
        Col("Station", "站点", 60, C);
        Col("Date", "日期", 80, C);
        Col("Model", "型号", 80, C);
        Col("FailCount", "失败项数", 70, C);
        Col("Csv", "CSV", 55, C);
        Col("TdmsCount", "TDMS", 60, C);
        Col("FailItems", "失败项", 360);
    }

    /// <summary>从嵌入资源加载图标。失败不影响使用，静默忽略。</summary>
    private void LoadAppIcon()
    {
        try
        {
            var asm = typeof(MainForm).Assembly;
            var name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith("app_icon.ico",
                              StringComparison.OrdinalIgnoreCase));
            if (name == null) return;
            using var s = asm.GetManifestResourceStream(name);
            if (s != null) Icon = new Icon(s);
        }
        catch { /* 图标缺失不影响功能 */ }
    }

    private void AddBrowse(int left, int top, TextBox target)
    {
        var b = new Button { Text = "...", Left = left, Top = top, Width = 40, Height = 24 };
        b.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog();
            if (Directory.Exists(target.Text)) d.SelectedPath = target.Text;
            if (d.ShowDialog() == DialogResult.OK) target.Text = d.SelectedPath;
        };
        Controls.Add(b);
    }

    /// <summary>双击某行 -> 在资源管理器里定位该 XML。</summary>
    private void OnGridDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _recs.Count) return;
        var p = _recs[e.RowIndex].XmlPath;
        if (File.Exists(p)) Process.Start("explorer.exe", $"/select,\"{p}\"");
    }

    private void Log(string s)
    {
        if (_log.InvokeRequired) { _log.BeginInvoke(() => Log(s)); return; }
        _log.AppendText(s + Environment.NewLine);
    }

    private async void OnRun(object? sender, EventArgs e)
    {
        if (_dtStart.Value.Date > _dtEnd.Value.Date)
        {
            MessageBox.Show("起始日期晚于结束日期。", "参数错误",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _cfg.ResultsRoot = _txtResults.Text.Trim();
        _cfg.TdmsRoot = _txtTdms.Text.Trim();
        _cfg.OutputDir = _txtOut.Text.Trim();
        _cfg.PackFiles = _chkPack.Checked;
        _cfg.Categories = _chkOnline.Checked
            ? new[] { "Offline", "Online" }
            : new[] { "Offline" };
        try { _cfg.Save(Program.ConfigPath); }
        catch (Exception ex)
        {
            Log($"[警告] 配置保存失败(未写入磁盘), 本次运行使用内存中的配置: {ex.Message}");
            MessageBox.Show(
                $"配置保存失败, 未写入磁盘, 请检查 {Program.ConfigPath} 内容。\n\n{ex.Message}",
                "配置保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        _btnRun.Enabled = false;
        _log.Clear();
        _grid.Rows.Clear();
        _recs.Clear();
        _bar.Value = 0;
        _summary.Text = "正在扫描...";
        _summary.ForeColor = Color.FromArgb(31, 78, 121);

        var start = _dtStart.Value.Date;
        var end = _dtEnd.Value.Date;
        Log($"日期区间: {start:yyyy-MM-dd} ~ {end:yyyy-MM-dd} (含首尾)");

        try
        {
            var (recs, st, xlsx, pr) = await Task.Run(() =>
            {
                var rs = Scanner.Scan(_cfg, start, end, out var stats, Log,
                    (d, t) =>
                    {
                        if (t <= 0) return;
                        int p = (int)(100L * d / t);
                        if (_bar.InvokeRequired) _bar.BeginInvoke(() => _bar.Value = Math.Min(p, 100));
                    });
                if (rs.Count == 0) return (rs, stats, "", (Packager.Result?)null);

                FileLocator.Attach(rs, _cfg, Log);
                var od = _cfg.ResolveOutputDir(Program.ExeDir);
                var x = Exporter.Export(rs, _cfg, start, end, od);
                Packager.Result? p2 = _cfg.PackFiles
                    ? Packager.Pack(rs, od, start, end, x, _cfg.KeepStageDir, Log)
                    : null;
                return (rs, stats, x, p2);
            });

            _bar.Value = 100;
            _recs = recs;

            Log("");
            Log(new string('=', 58));
            Log($"扫描 XML 总数      : {st.XmlTotal}");
            Log($"  路径不合规跳过   : {st.SkipBadPath}");
            Log($"  日期区间外       : {st.SkipRange}");
            Log($"  区间内           : {st.InRange}");
            Log($"    无 fail 项跳过  : {st.SkipNoFail}");
            Log($"    debug 跳过     : {st.SkipDebug}");
            if (st.SkipParseError > 0) Log($"    XML 解析失败   : {st.SkipParseError}");
            Log($"  >> 命中(含fail项) : {st.Fail}");
            Log(new string('=', 58));

            if (recs.Count == 0)
            {
                _summary.Text = $"未捞到含 fail 项的记录。（区间内 {st.InRange} 个 XML，" +
                                $"无 fail {st.SkipNoFail}，debug {st.SkipDebug}）";
                _summary.ForeColor = Color.DarkOrange;
                return;
            }

            foreach (var r in recs)
                _grid.Rows.Add(r.Sn, r.Station, r.Date, r.Model,
                               r.FailItems.Count,
                               r.CsvPath.Length > 0 ? "✔" : "—",
                               r.TdmsPaths.Count,
                               string.Join(" | ", r.FailItems.Select(x => x.Name)));

            // CSV / TDMS 未命中的行标黄，便于一眼看出缺件
            for (int i = 0; i < recs.Count; i++)
            {
                if (recs[i].CsvPath.Length == 0 || recs[i].TdmsPaths.Count == 0)
                    _grid.Rows[i].DefaultCellStyle.BackColor = Color.FromArgb(255, 249, 219);
            }

            int csvHit = recs.Count(r => r.CsvPath.Length > 0);
            int tdmsHit = recs.Count(r => r.TdmsPaths.Count > 0);
            int snCount = recs.Select(r => r.Sn).Distinct().Count();

            Log($"CSV  命中: {csvHit}/{recs.Count}");
            Log($"TDMS 命中: {tdmsHit}/{recs.Count}" +
                (Directory.Exists(_cfg.TdmsRoot) ? "" : $"   [目录不存在: {_cfg.TdmsRoot}]"));
            Log("");
            Log($"清单已输出: {xlsx}");

            var sb = new System.Text.StringBuilder();
            sb.Append($"命中 {recs.Count} 条 / SN {snCount} 个    ");
            sb.Append($"CSV {csvHit}/{recs.Count}    TDMS {tdmsHit}/{recs.Count}");
            if (pr != null)
            {
                Log($"已打包: {pr.ZipPath}");
                Log($"  共 {pr.Total} 个文件 (xml {pr.Xml} / csv {pr.Csv} / tdms {pr.Tdms}), " +
                    $"压缩后 {Packager.HumanSize(pr.ZipBytes)}");
                sb.Append($"    已打包 {Path.GetFileName(pr.ZipPath)} " +
                          $"({pr.Total} 个文件, {Packager.HumanSize(pr.ZipBytes)})");
            }
            _summary.Text = sb.ToString();
            _summary.ForeColor = (csvHit == recs.Count && tdmsHit == recs.Count)
                ? Color.FromArgb(0, 112, 60)
                : Color.FromArgb(180, 95, 0);

            _lastOut = _cfg.ResolveOutputDir(Program.ExeDir);
            _btnOpen.Enabled = true;
            _tabs.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            Log($"[异常] {ex.GetType().Name}: {ex.Message}");
            _summary.Text = $"失败: {ex.Message}";
            _summary.ForeColor = Color.Firebrick;
            MessageBox.Show(ex.Message, "捞取失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnRun.Enabled = true;
        }
    }
}

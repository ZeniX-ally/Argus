using System.Text.Json;
using System.Windows.Forms;

namespace FctFailRanker;

public class MainForm : Form
{
    private readonly TextBox _txtRoot = new();
    private readonly DateTimePicker _dtStart = new();
    private readonly DateTimePicker _dtEnd = new();
    private readonly Button _btnScan = new();
    private readonly Button _btnExport = new();
    private readonly Button _btnExportRank = new();
    private readonly Button _btnExportXlsx = new();
    private readonly Button _btnBrowse = new();
    private readonly ProgressBar _progress = new();
    private readonly ListView _lvRank = new();
    private readonly TextBox _txtLog = new();
    private readonly Label _lblSummary = new();
    private readonly Label _lblSurvey = new();
    private readonly TreeView _tvSurvey = new();
    private readonly ComboBox _cbModel = new();
    private readonly Label _lblModelShare = new();

    private List<XmlRecord> _records = new();
    private CsvExporter.Summary? _summary;
    private List<CsvExporter.FailRank> _ranks = new();
    private List<CsvExporter.GroupResult> _byModel = new();   // 各型号分组(含排名)

    public MainForm()
    {
        Text = "FCT 不良项排名导出工具 v1.5.0";
        Width = 980;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        TryLoadIcon();

        // ---- 顶部: 目录 ----
        var lblRoot = new Label { Text = "结果目录:", Left = 12, Top = 18, Width = 70, TextAlign = ContentAlignment.MiddleRight };
        _txtRoot.Left = 88; _txtRoot.Top = 15; _txtRoot.Width = 640;
        _txtRoot.Text = LoadDefaultRoot();
        _btnBrowse.Text = "浏览..."; _btnBrowse.Left = 736; _btnBrowse.Top = 14; _btnBrowse.Width = 120;
        _btnBrowse.Click += (_, _) => BrowseFolder();

        // ---- 时间段 ----
        var lblStart = new Label { Text = "起始日期:", Left = 12, Top = 52, Width = 70, TextAlign = ContentAlignment.MiddleRight };
        _dtStart.Left = 88; _dtStart.Top = 49; _dtStart.Width = 160; _dtStart.Format = DateTimePickerFormat.Custom; _dtStart.CustomFormat = "yyyy-MM-dd";
        _dtStart.Value = DateTime.Today.AddDays(-7);
        var lblEnd = new Label { Text = "结束日期:", Left = 270, Top = 52, Width = 70, TextAlign = ContentAlignment.MiddleRight };
        _dtEnd.Left = 346; _dtEnd.Top = 49; _dtEnd.Width = 160; _dtEnd.Format = DateTimePickerFormat.Custom; _dtEnd.CustomFormat = "yyyy-MM-dd";
        _dtEnd.Value = DateTime.Today;

        _btnScan.Text = "扫描统计"; _btnScan.Left = 620; _btnScan.Top = 48; _btnScan.Width = 100; _btnScan.Height = 62;
        _btnScan.Click += async (_, _) => await ScanAsync();
        _btnExportXlsx.Text = "导出 Excel\n(含分组排名)"; _btnExportXlsx.Left = 728; _btnExportXlsx.Top = 48; _btnExportXlsx.Width = 110; _btnExportXlsx.Height = 62;
        _btnExportXlsx.Enabled = false;
        _btnExportXlsx.Click += (_, _) => ExportXlsx();
        _btnExport.Text = "导出完整 CSV"; _btnExport.Left = 846; _btnExport.Top = 48; _btnExport.Width = 110; _btnExport.Height = 28;
        _btnExport.Enabled = false;
        _btnExport.Click += (_, _) => ExportCsv(rankOnly: false);
        _btnExportRank.Text = "仅排名表 CSV"; _btnExportRank.Left = 846; _btnExportRank.Top = 82; _btnExportRank.Width = 110; _btnExportRank.Height = 28;
        _btnExportRank.Enabled = false;
        _btnExportRank.Click += (_, _) => ExportCsv(rankOnly: true);

        // ---- 进度条 ----
        _progress.Left = 12; _progress.Top = 116; _progress.Width = 596; _progress.Height = 16;

        // ---- 概览 ----
        _lblSummary.Left = 12; _lblSummary.Top = 138; _lblSummary.Width = 944; _lblSummary.Height = 22;
        _lblSummary.Text = "请设置时间段后点击【扫描统计】";
        _lblSummary.ForeColor = Color.DarkBlue;

        // ---- 左侧: 磁盘扫描概览(型号/月份/XML数) ----
        _lblSurvey.Left = 12; _lblSurvey.Top = 164; _lblSurvey.Width = 366; _lblSurvey.Height = 20;
        _lblSurvey.Font = new Font("Microsoft YaHei UI", 8F);
        _lblSurvey.Text = "扫描概览:";
        _lblSurvey.ForeColor = Color.DarkGreen;
        _tvSurvey.Left = 12; _tvSurvey.Top = 186; _tvSurvey.Width = 360; _tvSurvey.Height = 328;
        _tvSurvey.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _tvSurvey.HideSelection = false;
        _tvSurvey.MouseDown += OnSurveyMouseDown;
        _tvSurvey.BeforeExpand += OnSurveyBeforeExpand;

        // ---- 右侧: 排名列表 (上方加型号切换 + 占比) ----
        var lblRank = new Label { Text = "不良项排名:", Left = 384, Top = 166, Width = 80 };
        var lblModelSel = new Label { Text = "型号:", Left = 470, Top = 166, Width = 34, TextAlign = ContentAlignment.MiddleRight };
        _cbModel.Left = 506; _cbModel.Top = 163; _cbModel.Width = 160;
        _cbModel.DropDownStyle = ComboBoxStyle.DropDownList;
        // 事件在 PopulateModelCombo 中挂载(OnModelChanged), 此处不重复挂载
        _lblModelShare.Left = 676; _lblModelShare.Top = 166; _lblModelShare.Width = 280; _lblModelShare.Height = 20;
        _lblModelShare.ForeColor = Color.Firebrick;
        _lblModelShare.TextAlign = ContentAlignment.MiddleLeft;

        _lvRank.Left = 384; _lvRank.Top = 190; _lvRank.Width = 572; _lvRank.Height = 324;
        _lvRank.View = View.Details; _lvRank.FullRowSelect = true; _lvRank.GridLines = true;
        _lvRank.Columns.Add("排名", 36);
        _lvRank.Columns.Add("不良项名称", 180);
        _lvRank.Columns.Add("出现次数", 54);
        _lvRank.Columns.Add("受影响产品", 64);
        _lvRank.Columns.Add("占比%", 48);
        _lvRank.Columns.Add("测量值", 70);
        _lvRank.Columns.Add("规格", 60);
        _lvRank.Columns.Add("单位", 40);
        _lvRank.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        // ---- 日志 ----
        var lblLog = new Label { Text = "日志:", Left = 12, Top = 520, Width = 60 };
        _txtLog.Left = 12; _txtLog.Top = 542; _txtLog.Width = 944; _txtLog.Height = 128;
        _txtLog.Multiline = true; _txtLog.ReadOnly = true; _txtLog.ScrollBars = ScrollBars.Vertical;
        _txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        Controls.AddRange(new Control[]
        {
            lblRoot, _txtRoot, _btnBrowse,
            lblStart, _dtStart, lblEnd, _dtEnd, _btnScan, _btnExportXlsx, _btnExport, _btnExportRank,
            _progress, _lblSummary, _lblSurvey, _tvSurvey,
            lblRank, lblModelSel, _cbModel, _lblModelShare, _lvRank, lblLog, _txtLog
        });
    }

    private void TryLoadIcon()
    {
        try
        {
            FctAggregator.AppIcon.Apply(this);   // v2.9.4: 统一图标入口
            var ico = Path.Combine(AppContext.BaseDirectory, "app_icon.ico");
            if (File.Exists(ico)) Icon = new Icon(ico);
        }
        catch { /* 图标加载失败不影响使用 */ }
    }

    private string LoadDefaultRoot()
    {
        // 尝试读取同目录 config.json 的 results_root, 否则默认 D:\Results
        try
        {
            var cfg = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(cfg))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
                if (doc.RootElement.TryGetProperty("results_root", out var p))
                    return p.GetString() ?? "D:\\Results";
            }
        }
        catch { }
        return "D:\\Results";
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "选择测试结果根目录 (含 Online/Offline)" };
        if (Directory.Exists(_txtRoot.Text)) dlg.SelectedPath = _txtRoot.Text;
        if (dlg.ShowDialog() == DialogResult.OK) _txtRoot.Text = dlg.SelectedPath;
    }

    private void Log(string msg)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(msg)); return; }
        _txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
    }

    private async Task ScanAsync()
    {
        var root = _txtRoot.Text.Trim();
        var start = _dtStart.Value.Date;
        var end = _dtEnd.Value.Date;
        if (start > end) { MessageBox.Show("起始日期不能晚于结束日期"); return; }
        if (!Directory.Exists(root)) { MessageBox.Show("结果目录不存在: " + root); return; }

        _btnScan.Enabled = false; _btnExport.Enabled = false; _btnExportRank.Enabled = false;
        _btnExportXlsx.Enabled = false;
        _txtLog.Clear(); _lvRank.Items.Clear();
        _progress.Value = 0;
        Log($"开始扫描: {root}");
        Log($"时间段: {start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}");

        try
        {
            // 先按磁盘实际文件夹结构构建树(如实映射, 不自定义分级)
            RenderFolderTree(root);

            await Task.Run(() =>
            {
                _records = XmlScanner.Scan(root, start, end,
                    log: Log,
                    progress: (done, total) =>
                    {
                        if (InvokeRequired)
                            BeginInvoke(() =>
                            {
                                _progress.Maximum = Math.Max(total, 1);
                                _progress.Value = Math.Min(done, _progress.Maximum);
                            });
                    });
            });

            var (summary, ranks) = CsvExporter.Aggregate(_records);
            _summary = summary; _ranks = ranks;
            _byModel = CsvExporter.AggregateByModel(_records);
            PopulateModelCombo();
            RenderResults();
            _btnExport.Enabled = _records.Count > 0;
            _btnExportRank.Enabled = _ranks.Count > 0;
            _btnExportXlsx.Enabled = _records.Count > 0;
            Log($"统计完成: 记录 {summary.Total}, 不良项种类 {ranks.Count}");
        }
        catch (Exception ex)
        {
            Log("[异常] " + ex.Message);
            MessageBox.Show("扫描出错: " + ex.Message);
        }
        finally
        {
            _btnScan.Enabled = true;
        }
    }

    private void RenderResults()
    {
        if (_summary == null) return;
        _lblSummary.Text =
            $"总记录 {_summary.Total}  |  产品 {_summary.DistinctSn}  |  PASS {_summary.Pass}  " +
            $"FAIL {_summary.Fail}  中断 {_summary.Interrupted}  |  良率 {_summary.Yield:F2}%  " +
            $"|  不良项累计 {_summary.TotalFailOccurrences}";
        RenderRankForSelectedModel();
    }

    /// <summary>填充型号下拉: “全部” + 各型号(带该型号不良占比)。</summary>
    private void PopulateModelCombo()
    {
        _cbModel.SelectedIndexChanged -= OnModelChanged;   // 防重入(先移除写入时的事件)
        _cbModel.Items.Clear();
        _cbModel.Items.Add("全部型号");
        int totalOcc = _summary?.TotalFailOccurrences ?? 0;
        foreach (var g in _byModel)
        {
            double share = totalOcc > 0 ? (double)g.Summary.TotalFailOccurrences / totalOcc * 100.0 : 0;
            // 下拉项直接带上该型号占总不良的百分比
            _cbModel.Items.Add($"{g.Key}  ({share:F1}%)");
        }
        _cbModel.SelectedIndex = 0;
        _cbModel.SelectedIndexChanged += OnModelChanged;
    }

    private void OnModelChanged(object? sender, EventArgs e) => RenderRankForSelectedModel();

    /// <summary>根据下拉选中的型号, 渲染对应的不良项排名 + 占比提示。</summary>
    private void RenderRankForSelectedModel()
    {
        if (_summary == null) return;
        int idx = _cbModel.SelectedIndex;
        List<CsvExporter.FailRank> ranks;
        int totalOcc = _summary.TotalFailOccurrences;

        if (idx <= 0)
        {
            // 全部型号
            ranks = _ranks;
            _lblModelShare.Text = $"全部型号  不良累计 {totalOcc} 次 (100%)";
        }
        else
        {
            var g = _byModel[idx - 1];   // 减 1 因为首项是“全部”
            ranks = g.Ranks;
            int occ = g.Summary.TotalFailOccurrences;
            double share = totalOcc > 0 ? (double)occ / totalOcc * 100.0 : 0;
            _lblModelShare.Text = $"{g.Key}  不良 {occ} 次  占总不良 {share:F1}%  |  FAIL {g.Summary.Fail} 台  良率 {g.Summary.Yield:F2}%";
        }

        _lvRank.BeginUpdate();
        _lvRank.Items.Clear();
        int rank = 1;
        foreach (var r in ranks)
        {
            var item = new ListViewItem(rank.ToString());
            item.SubItems.Add(r.Item);
            item.SubItems.Add(r.Count.ToString());
            item.SubItems.Add(r.AffectedUnits.ToString());
            item.SubItems.Add($"{r.Percent:F2}");
            item.SubItems.Add(r.Values);
            item.SubItems.Add(r.Limits);
            item.SubItems.Add(r.Units);
            if (rank <= 3) item.BackColor = Color.MistyRose;
            _lvRank.Items.Add(item);
            rank++;
        }
        _lvRank.EndUpdate();
    }

    private void RenderFolderTree(string root)
    {
        _tvSurvey.BeginUpdate();
        _tvSurvey.Nodes.Clear();
        try
        {
            if (!Directory.Exists(root))
            {
                _lblSurvey.Text = "目录不存在: " + root;
                _tvSurvey.EndUpdate();
                return;
            }
            var rootNode = new TreeNode(root)
            {
                Tag = new FsTag { IsDir = true, Path = root },
                NodeFont = new Font(_tvSurvey.Font, FontStyle.Bold),
            };
            _tvSurvey.Nodes.Add(rootNode);
            PopulateChildren(rootNode);   // 先填一层
            rootNode.Expand();
            _lblSurvey.Text = "文件夹结构 (双击展开/收起，双击 XML 看内容，双击其它文件默认打开)";
        }
        catch (Exception ex)
        {
            _lblSurvey.Text = "读取目录失败: " + ex.Message;
        }
        finally
        {
            _tvSurvey.EndUpdate();
        }
    }

    /// <summary>为目录节点填充子项(子目录 + 文件)。惰性: 子目录先放一个占位节点, 展开时再加载。</summary>
    private void PopulateChildren(TreeNode dirNode)
    {
        if (dirNode.Tag is not FsTag tag || !tag.IsDir) return;
        if (tag.Loaded) return;
        tag.Loaded = true;
        dirNode.Nodes.Clear();   // 清除占位节点

        try
        {
            // 子目录(排序)
            foreach (var dir in Directory.EnumerateDirectories(tag.Path)
                         .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                var child = new TreeNode(name)
                {
                    Tag = new FsTag { IsDir = true, Path = dir },
                    ForeColor = Color.FromArgb(60, 90, 150),
                };
                // 占位节点, 使其可展开
                if (DirHasChildren(dir)) child.Nodes.Add(new TreeNode("加载中...") { Tag = null });
                dirNode.Nodes.Add(child);
            }
            // 文件(排序)
            foreach (var file in Directory.EnumerateFiles(tag.Path)
                         .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                bool isXml = name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
                var child = new TreeNode(name)
                {
                    Tag = new FsTag { IsDir = false, Path = file },
                    ForeColor = isXml ? Color.FromArgb(60, 60, 60) : Color.DimGray,
                };
                dirNode.Nodes.Add(child);
            }
        }
        catch (Exception ex)
        {
            dirNode.Nodes.Add(new TreeNode($"[无法读取: {ex.Message}]") { Tag = null });
        }
    }

    private static bool DirHasChildren(string dir)
    {
        try
        {
            using var e1 = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
            return e1.MoveNext();
        }
        catch { return false; }
    }

    // ---- 树节点 Tag: 映射磁盘文件系统项 ----
    private class FsTag
    {
        public bool IsDir;
        public string Path = "";
        public bool Loaded;    // 目录子项是否已惰性加载
    }

    // 记录上一次“第一下”点击的节点(用于双击判定, 不受展开后错位影响)
    private TreeNode? _tvFirstClickNode;

    private void OnSurveyMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var node = _tvSurvey.GetNodeAt(e.X, e.Y);
        // 只有当鼠标确实落在节点行上(而不是右侧空白)才算
        if (node != null)
        {
            var bounds = node.Bounds;
            bool onRow = e.Y >= bounds.Top && e.Y < bounds.Bottom;
            if (!onRow) node = null;
        }

        if (e.Clicks == 1)
        {
            _tvFirstClickNode = node;
        }
        else if (e.Clicks == 2)
        {
            // 双击: 以“第一下”的节点为准(展开前), 且两下在同一节点
            var target = _tvFirstClickNode;
            if (target != null && target == node && target.Tag is FsTag tag && !tag.IsDir)
            {
                OpenFile(tag.Path);
            }
            // 目录: 交给 TreeView 默认行为(展开/收起) + BeforeExpand 惰性加载
        }
    }

    /// <summary>展开目录前惰性加载子项。</summary>
    private void OnSurveyBeforeExpand(object? sender, TreeViewCancelEventArgs e)
    {
        if (e.Node?.Tag is FsTag tag && tag.IsDir && !tag.Loaded)
            PopulateChildren(e.Node);
    }

    /// <summary>双击文件: xml 用报告查看器, 其它用默认程序打开。</summary>
    private void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show("文件不存在:\n" + path, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            ShowXmlContent(path);
        }
        else
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开失败: " + ex.Message);
                Log("[打开文件失败] " + ex.Message);
            }
        }
    }
    /// <summary>弹窗查看 XML 内容(报告式: 头部信息 + 所有测试项, 失败项标红)。</summary>
    private void ShowXmlContent(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show("文件不存在:\n" + path, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            using var viewer = new XmlViewerForm(path);
            viewer.ShowDialog((IWin32Window)(TopLevelControl ?? this));   // 内嵌后 this 不是顶层窗（FindForm 会返回自己，须用 TopLevelControl）
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开 XML 失败: " + ex.Message);
            Log("[查看XML失败] " + ex.Message);
        }
    }

    private void ExportCsv(bool rankOnly)
    {
        if (_summary == null || _records.Count == 0) return;
        var start = _dtStart.Value.Date;
        var end = _dtEnd.Value.Date;
        var defName = rankOnly
            ? $"FCT不良排名表_{start:yyyyMMdd}-{end:yyyyMMdd}.csv"
            : $"FCT不良完整报表_{start:yyyyMMdd}-{end:yyyyMMdd}.csv";
        using var dlg = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = defName,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            if (rankOnly)
                CsvExporter.ExportRankOnly(dlg.FileName, start, end, _summary, _ranks);
            else
                CsvExporter.Export(dlg.FileName, start, end, _records, _summary, _ranks);
            Log($"已导出: {dlg.FileName}");
            if (MessageBox.Show("导出成功！是否打开文件?", "完成",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dlg.FileName,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败: " + ex.Message);
            Log("[导出失败] " + ex.Message);
        }
    }

    private void ExportXlsx()
    {
        if (_summary == null || _records.Count == 0) return;
        var start = _dtStart.Value.Date;
        var end = _dtEnd.Value.Date;
        using var dlg = new SaveFileDialog
        {
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            FileName = $"FCT不良排名_{start:yyyyMMdd}-{end:yyyyMMdd}.xlsx",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            XlsxExporter.Export(dlg.FileName, start, end, _records, _summary, _ranks);
            Log($"已导出 Excel: {dlg.FileName}");
            if (MessageBox.Show("导出成功！(含 总排名/各型号/各机台/明细 多个 Sheet)\n是否打开文件?", "完成",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dlg.FileName,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败: " + ex.Message);
            Log("[Excel导出失败] " + ex.Message);
        }
    }
}

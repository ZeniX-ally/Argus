using System.Diagnostics;

namespace FctTdmsViewer;

public sealed class MainForm : Form
{
    private TdmsDoc? _doc;

    private readonly ToolStrip _tool = new();
    private readonly ToolStripButton _btnOpen = new("打开 TDMS");
    private readonly ToolStripButton _btnExportSel = new("导出选中通道");
    private readonly ToolStripButton _btnExportSum = new("导出结构清单");
    private readonly ToolStripButton _btnClearSel = new("清空勾选");
    private readonly ToolStripComboBox _cboOrder = new();
    private readonly ToolStripLabel _lblFile = new("未打开文件");

    private readonly TextBox _search = new();
    private readonly TreeView _tree = new();
    private readonly WaveformPanel _wave = new();
    private readonly DataGridView _grid = new();
    private readonly ListView _props = new();
    private readonly Label _stat = new();
    private readonly TabControl _tabs = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusText = new("就绪");

    // 勾选的通道（跨 Group 累积，用于多通道叠加对比）
    private readonly List<ChannelInfo> _checked = new();
    // 当前选中的通道（未勾选任何通道时，直接显示它 —— 做到"选中即出图"）
    private ChannelInfo? _current;

    public MainForm()
    {
        Text = "FCT-TdmsViewer  v1.1.0";
        Width = 1280;
        Height = 820;
        MinimumSize = new Size(1000, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("微软雅黑", 9F);
        AllowDrop = true;
        LoadAppIcon();

        // ---- 工具栏 ----
        _tool.GripStyle = ToolStripGripStyle.Hidden;
        _cboOrder.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboOrder.Items.AddRange(new object[] { "按测试项编号排序", "按文件内原始顺序" });
        _cboOrder.SelectedIndex = 0;
        _cboOrder.Width = 150;
        _cboOrder.SelectedIndexChanged += (_, _) =>
        {
            if (_doc == null) return;
            _doc.SortGroups(_cboOrder.SelectedIndex == 0
                ? TdmsDoc.GroupOrder.ByNumber
                : TdmsDoc.GroupOrder.FileOrder);
            RebuildTree(_search.Text.Trim());
        };
        _tool.Items.AddRange(new ToolStripItem[]
        {
            _btnOpen, new ToolStripSeparator(),
            _btnExportSel, _btnExportSum, new ToolStripSeparator(),
            _btnClearSel, new ToolStripSeparator(),
            new ToolStripLabel("排序:"), _cboOrder, new ToolStripSeparator(),
            _lblFile,
        });
        _btnOpen.Click += (_, _) => PickFile();
        _btnExportSel.Click += (_, _) => ExportSelected();
        _btnExportSum.Click += (_, _) => ExportSummary();
        _btnClearSel.Click += (_, _) => ClearChecks();
        Controls.Add(_tool);

        // ---- 主分栏 ----
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 380,
            FixedPanel = FixedPanel.Panel1,
        };
        Controls.Add(split);
        split.BringToFront();

        // 左：搜索 + 树
        var left = new Panel { Dock = DockStyle.Fill };
        _search.Dock = DockStyle.Top;
        _search.PlaceholderText = "搜索组名或通道名（即时过滤，空格分隔多关键词）";
        _search.TextChanged += (_, _) => RebuildTree(_search.Text.Trim());
        left.Controls.Add(_search);

        _tree.Dock = DockStyle.Fill;
        _tree.CheckBoxes = true;
        _tree.HideSelection = false;
        _tree.AfterCheck += OnAfterCheck;
        _tree.AfterSelect += OnAfterSelect;
        left.Controls.Add(_tree);
        _tree.BringToFront();
        split.Panel1.Controls.Add(left);

        // 右：上波形 + 下数据/属性
        var rsplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 400,
        };
        split.Panel2.Controls.Add(rsplit);

        _wave.Dock = DockStyle.Fill;
        rsplit.Panel1.Controls.Add(_wave);

        _stat.Dock = DockStyle.Top;
        _stat.Height = 24;
        _stat.TextAlign = ContentAlignment.MiddleLeft;
        _stat.Font = new Font("Consolas", 9F);
        _stat.ForeColor = Color.FromArgb(31, 78, 121);
        rsplit.Panel2.Controls.Add(_stat);

        _tabs.Dock = DockStyle.Fill;
        rsplit.Panel2.Controls.Add(_tabs);
        _tabs.BringToFront();

        var tabData = new TabPage("数据");
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(68, 114, 196);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("微软雅黑", 9F, FontStyle.Bold);
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 249, 252);
        _grid.DefaultCellStyle.Font = new Font("Consolas", 9F);
        tabData.Controls.Add(_grid);
        _tabs.TabPages.Add(tabData);

        var tabProp = new TabPage("属性");
        _props.Dock = DockStyle.Fill;
        _props.View = View.Details;
        _props.FullRowSelect = true;
        _props.GridLines = true;
        _props.Columns.Add("属性", 240);
        _props.Columns.Add("值", 600);
        tabProp.Controls.Add(_props);
        _tabs.TabPages.Add(tabProp);

        // ---- 状态栏 ----
        _status.Items.Add(_statusText);
        Controls.Add(_status);

        // 拖放打开
        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] fs && fs.Length > 0)
                LoadFile(fs[0]);
        };

        UpdateButtons();
    }

    public void LoadFile(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            MessageBox.Show($"文件不存在:\n{path}", "打开失败",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            Cursor = Cursors.WaitCursor;
            _doc?.Dispose();
            _checked.Clear();
            _wave.Clear();
            _grid.Columns.Clear();
            _grid.Rows.Clear();
            _props.Items.Clear();
            _stat.Text = "";

            var sw = Stopwatch.StartNew();
            _doc = TdmsDoc.Load(path);
            sw.Stop();
            _current = null;

            _search.Text = "";
            RebuildTree("");

            _lblFile.Text = Path.GetFileName(path);
            _statusText.Text = $"{_doc.Groups.Count} 组 / {_doc.TotalChannels} 通道 / " +
                               $"{Human(_doc.FileBytes)} / 读元信息 {sw.ElapsedMilliseconds} ms";
            Text = $"FCT-TdmsViewer  v1.1.0  —  {Path.GetFileName(path)}";

            // 打开即展示：自动选中第一个数值通道，不用手动勾选
            SelectFirstNumericChannel();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{ex.GetType().Name}: {ex.Message}", "解析失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _statusText.Text = "解析失败";
        }
        finally
        {
            Cursor = Cursors.Default;
            UpdateButtons();
        }
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

    private void PickFile()
    {
        using var d = new OpenFileDialog
        {
            Filter = "TDMS 文件 (*.tdms)|*.tdms|所有文件 (*.*)|*.*",
            Title = "打开 TDMS 文件",
        };
        if (d.ShowDialog() == DialogResult.OK) LoadFile(d.FileName);
    }

    private void RebuildTree(string filter)
    {
        if (_doc == null) return;
        // 空格分隔多关键词，需全部命中（AND）
        var keys = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries |
                                     StringSplitOptions.TrimEntries);
        bool hasFilter = keys.Length > 0;

        bool Hit(string s) =>
            keys.All(k => s.Contains(k, StringComparison.OrdinalIgnoreCase));

        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        int hitCount = 0;

        foreach (var g in _doc.Groups)
        {
            List<ChannelInfo> matched;
            if (!hasFilter)
            {
                matched = g.Channels;
            }
            else if (Hit(g.Name))
            {
                matched = g.Channels;      // 组名命中 -> 保留整组
            }
            else
            {
                matched = g.Channels.Where(c => Hit(c.Name)).ToList();
            }
            if (matched.Count == 0) continue;
            hitCount += matched.Count;

            var gn = new TreeNode($"{g.Seq:00}  {g.Name}   [{g.Channels.Count} 通道 · {g.SampleCount} 点]")
            {
                Tag = g,
            };
            foreach (var c in matched)
            {
                int ci = g.Channels.IndexOf(c) + 1;
                var label = c.Numeric
                    ? $"{ci:000}  {c.Name}   ({c.TypeName}, {c.Count})"
                    : $"{ci:000}  {c.Name}   ({c.TypeName}, {c.Count}) [非数值]";
                var cn = new TreeNode(label) { Tag = c };
                if (_checked.Contains(c)) cn.Checked = true;
                if (!c.Numeric) cn.ForeColor = Color.Gray;
                gn.Nodes.Add(cn);
            }
            _tree.Nodes.Add(gn);
            if (hasFilter) gn.Expand();
        }
        _tree.EndUpdate();

        if (hasFilter)
            _statusText.Text = $"过滤 \"{filter}\": {_tree.Nodes.Count} 组 / {hitCount} 通道命中";
    }

    /// <summary>打开文件后自动选中第一个数值通道，使图表与表格立即有内容。</summary>
    private void SelectFirstNumericChannel()
    {
        foreach (TreeNode gn in _tree.Nodes)
        {
            foreach (TreeNode cn in gn.Nodes)
            {
                if (cn.Tag is ChannelInfo ci && ci.Numeric && ci.Count > 0)
                {
                    gn.Expand();
                    _tree.SelectedNode = cn;
                    cn.EnsureVisible();
                    return;
                }
            }
        }
        // 没有数值通道时退而选第一个组，至少表格有统计概览
        if (_tree.Nodes.Count > 0) _tree.SelectedNode = _tree.Nodes[0];
    }

    private bool _suppress;

    private void OnAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_suppress || e.Node == null) return;

        // 勾组 = 勾该组下全部（受当前过滤限制）
        if (e.Node.Tag is GroupInfo)
        {
            _suppress = true;
            foreach (TreeNode cn in e.Node.Nodes)
            {
                cn.Checked = e.Node.Checked;
                SyncChecked(cn);
            }
            _suppress = false;
        }
        else
        {
            SyncChecked(e.Node);
        }
        RefreshWave();
    }

    private void SyncChecked(TreeNode node)
    {
        if (node.Tag is not ChannelInfo ci) return;
        if (node.Checked)
        {
            if (!ci.Numeric)
            {
                node.Checked = false;   // 非数值通道不进波形
                return;
            }
            if (!_checked.Contains(ci)) _checked.Add(ci);
        }
        else
        {
            _checked.Remove(ci);
        }
    }

    private void ClearChecks()
    {
        _suppress = true;
        foreach (TreeNode g in _tree.Nodes)
        {
            g.Checked = false;
            foreach (TreeNode c in g.Nodes) c.Checked = false;
        }
        _suppress = false;
        _checked.Clear();
        RefreshWave();
        UpdateButtons();
    }

    private const int MaxSeries = 8;

    private void RefreshWave()
    {
        if (_doc == null) { UpdateButtons(); return; }

        // 显示集合 = 勾选的通道；没有勾选时用当前选中的通道
        var show = new List<ChannelInfo>(_checked);
        if (_current != null && _current.Numeric && !show.Contains(_current))
            show.Insert(0, _current);

        if (show.Count == 0)
        {
            _wave.Clear();
            _stat.Text = "";
            UpdateButtons();
            return;
        }
        if (show.Count > MaxSeries)
            _statusText.Text = $"共 {show.Count} 条，波形仅显示前 {MaxSeries} 条（导出不受限制）";

        var items = show.Take(MaxSeries)
            .Select(c => (name: c.Name, data: _doc!.GetData(c), inc: TdmsDoc.GetIncrement(c)))
            .ToList();
        _wave.SetSeries(items);

        // 单条时给出详细统计
        if (show.Count == 1)
        {
            var st = TdmsDoc.Describe(_doc.GetData(show[0]));
            _stat.Text = st == null
                ? $"{show[0].Name}: 无数据"
                : $"{show[0].Name}   n={st.N}   min={st.Min:G6}   max={st.Max:G6}   " +
                  $"mean={st.Mean:G6}   std={st.Std:G6}   首={st.First:G6}   末={st.Last:G6}";
        }
        else
        {
            _stat.Text = $"叠加显示 {Math.Min(show.Count, MaxSeries)} 条" +
                         (_checked.Count > 0 ? $"（已勾选 {_checked.Count}）" : "");
        }
        UpdateButtons();
    }

    private void OnAfterSelect(object? sender, TreeViewEventArgs e)
    {
        if (_doc == null || e.Node == null) return;

        _props.BeginUpdate();
        _props.Items.Clear();
        if (e.Node.Tag is GroupInfo g)
        {
            _current = null;
            foreach (var p in g.Properties)
                _props.Items.Add(new ListViewItem(new[] { p.Key, p.Value?.ToString() ?? "" }));
            ShowGroupTable(g);
        }
        else if (e.Node.Tag is ChannelInfo c)
        {
            _current = c;
            _props.Items.Add(new ListViewItem(new[] { "(Group)", c.GroupName }));
            _props.Items.Add(new ListViewItem(new[] { "(Channel)", c.Name }));
            _props.Items.Add(new ListViewItem(new[] { "(DataType)", c.TypeName }));
            _props.Items.Add(new ListViewItem(new[] { "(Count)", c.Count.ToString() }));
            foreach (var p in c.Properties)
                _props.Items.Add(new ListViewItem(new[] { p.Key, p.Value?.ToString() ?? "" }));
            ShowChannelTable(c);
        }
        _props.EndUpdate();

        RefreshWave();     // 选中即出图
    }

    private const int MaxGridRows = 5000;

    private void ShowChannelTable(ChannelInfo c)
    {
        if (_doc == null) return;
        _grid.SuspendLayout();
        _grid.Columns.Clear();
        _grid.Rows.Clear();
        _grid.Columns.Add("idx", "#");
        _grid.Columns["idx"]!.Width = 70;
        double inc = TdmsDoc.GetIncrement(c);
        if (inc > 0)
        {
            _grid.Columns.Add("t", "Time(s)");
            _grid.Columns["t"]!.Width = 90;
        }
        _grid.Columns.Add("v", c.Name);
        _grid.Columns["v"]!.Width = 200;

        if (c.Numeric)
        {
            var d = _doc.GetData(c);
            int n = Math.Min(d.Length, MaxGridRows);
            var rows = new List<DataGridViewRow>(n);
            for (int i = 0; i < n; i++)
            {
                var r = new DataGridViewRow();
                r.CreateCells(_grid);
                int k = 0;
                r.Cells[k++].Value = i;
                if (inc > 0) r.Cells[k++].Value = (i * inc).ToString("0.####");
                r.Cells[k].Value = d[i].ToString("G8");
                rows.Add(r);
            }
            _grid.Rows.AddRange(rows.ToArray());
            _statusText.Text = d.Length > MaxGridRows
                ? $"{c.Name}: 共 {d.Length} 点，表格仅显示前 {MaxGridRows} 行（波形与导出为全量）"
                : $"{c.Name}: {d.Length} 点";
        }
        else
        {
            var txt = _doc.GetText(c);
            int n = Math.Min(txt.Length, MaxGridRows);
            for (int i = 0; i < n; i++)
            {
                int k = _grid.Rows.Add();
                var r = _grid.Rows[k];
                int j = 0;
                r.Cells[j++].Value = i;
                if (inc > 0) r.Cells[j++].Value = (i * inc).ToString("0.####");
                r.Cells[j].Value = txt[i];
            }
            _statusText.Text = $"{c.Name}: 非数值类型 ({c.TypeName})，{txt.Length} 条";
        }
        _grid.ResumeLayout();
    }

    /// <summary>选中 Group 时，横向列出该组所有通道的统计概览。</summary>
    private void ShowGroupTable(GroupInfo g)
    {
        if (_doc == null) return;
        _grid.SuspendLayout();
        _grid.Columns.Clear();
        _grid.Rows.Clear();
        foreach (var (name, w) in new[]
                 {
                     ("通道", 260), ("类型", 80), ("点数", 70),
                     ("最小", 110), ("最大", 110), ("均值", 110), ("末值", 110),
                 })
        {
            int i = _grid.Columns.Add(name, name);
            _grid.Columns[i].Width = w;
        }

        var rows = new List<DataGridViewRow>(g.Channels.Count);
        foreach (var c in g.Channels)
        {
            var r = new DataGridViewRow();
            r.CreateCells(_grid);
            r.Cells[0].Value = c.Name;
            r.Cells[1].Value = c.TypeName;
            r.Cells[2].Value = c.Count;
            var st = c.Numeric ? TdmsDoc.Describe(_doc.GetData(c)) : null;
            if (st != null)
            {
                r.Cells[3].Value = st.Min.ToString("G6");
                r.Cells[4].Value = st.Max.ToString("G6");
                r.Cells[5].Value = st.Mean.ToString("G6");
                r.Cells[6].Value = st.Last.ToString("G6");
            }
            rows.Add(r);
        }
        _grid.Rows.AddRange(rows.ToArray());
        _grid.ResumeLayout();
        _statusText.Text = $"{g.Name}: {g.Channels.Count} 通道 · {g.SampleCount} 点";
    }

    private void ExportSelected()
    {
        if (_doc == null) return;
        // 有勾选就导勾选的；否则导当前选中这一条
        var target = _checked.Count > 0
            ? new List<ChannelInfo>(_checked)
            : (_current != null && _current.Numeric
                ? new List<ChannelInfo> { _current }
                : new List<ChannelInfo>());
        if (target.Count == 0) return;

        using var d = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"{Path.GetFileNameWithoutExtension(_doc.Path)}_{target.Count}ch.csv",
        };
        if (d.ShowDialog() != DialogResult.OK) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            Exporter.ExportChannels(_doc, target, d.FileName);
            _statusText.Text = $"已导出 {target.Count} 个通道 -> {d.FileName}";
            if (MessageBox.Show("导出完成，是否打开所在文件夹？", "完成",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start("explorer.exe", $"/select,\"{d.FileName}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { Cursor = Cursors.Default; }
    }

    private void ExportSummary()
    {
        if (_doc == null) return;
        using var d = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"{Path.GetFileNameWithoutExtension(_doc.Path)}_summary.csv",
        };
        if (d.ShowDialog() != DialogResult.OK) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            Exporter.ExportSummary(_doc, d.FileName);
            _statusText.Text = $"已导出结构清单 -> {d.FileName}";
            if (MessageBox.Show("导出完成，是否打开所在文件夹？", "完成",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start("explorer.exe", $"/select,\"{d.FileName}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { Cursor = Cursors.Default; }
    }

    private void UpdateButtons()
    {
        bool open = _doc != null;
        _btnExportSum.Enabled = open;
        int exportable = _checked.Count > 0 ? _checked.Count
                       : (_current != null && _current.Numeric ? 1 : 0);
        _btnExportSel.Enabled = open && exportable > 0;
        _btnClearSel.Enabled = open && _checked.Count > 0;
        _btnExportSel.Text = exportable > 0
            ? $"导出选中通道 ({exportable})"
            : "导出选中通道";
    }

    private static string Human(long b)
        => b >= 1 << 20 ? $"{b / (double)(1 << 20):F1} MB"
         : b >= 1 << 10 ? $"{b / (double)(1 << 10):F0} KB" : $"{b} B";

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _doc?.Dispose();
        base.OnFormClosed(e);
    }
}

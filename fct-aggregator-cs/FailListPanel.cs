namespace FctAggregator;

/// <summary>FAIL 记录页: 列表展示所有 FAIL(SN/首个fail项/时间), 双击打开排版后的 XML。</summary>
public class FailListPanel : Panel
{
    private readonly Engine _engine;
    private ListView _list = null!;

    public FailListPanel(Engine engine)
    {
        _engine = engine;
        BuildUi();
    }

    private void BuildUi()
    {
        Padding = new Padding(Theme.Gap);
        BackColor = Theme.Bg;

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Bg };
        var btnRefresh = Theme.MakeButton("刷新", 76);
        btnRefresh.Click += (_, _) => Refresh2();
        bar.Controls.Add(btnRefresh);
        var hint = new Label
        {
            Text = "（双击某行可查看该 XML 详细内容）", AutoSize = true,
            ForeColor = Theme.TextFaint, Font = Theme.Small, Margin = new Padding(8, 10, 0, 0),
        };
        bar.Controls.Add(hint);

        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true,
            BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.Surface, Font = Theme.Body,
        };
        _list.Columns.Add("SN", 300);
        _list.Columns.Add("失败项", 320);
        _list.Columns.Add("型号", 100);
        _list.Columns.Add("时间", 180);
        _list.DoubleClick += List_DoubleClick;

        Controls.Add(_list);
        Controls.Add(bar);
    }

    public void Refresh2()
    {
        _list.Items.Clear();
        try
        {
            foreach (var f in _engine.Db.AllFails(_engine.ResolvedStationId))
            {
                var item = new ListViewItem(f.Sn);
                item.SubItems.Add(f.FailItem);
                item.SubItems.Add(f.Model);
                item.SubItems.Add(string.IsNullOrEmpty(f.Timestamp) ? f.TestDate : f.Timestamp);
                item.Tag = f.XmlPath;
                _list.Items.Add(item);
            }
        }
        catch (Exception ex) { Logger.Error($"FAIL 记录加载失败: {ex.Message}"); }
    }

    private void List_DoubleClick(object? sender, EventArgs e)
    {
        if (_list.SelectedItems.Count == 0) return;
        var path = _list.SelectedItems[0].Tag as string;
        if (string.IsNullOrEmpty(path)) return;
        using var dlg = new XmlViewerForm(path);
        dlg.ShowDialog();
    }
}

/// <summary>XML 报告查看窗口: 产品信息 + 所有测试项完整流程(失败项标红)。</summary>
public class XmlViewerForm : Form
{
    public XmlViewerForm(string xmlPath)
    {
        Text = "测试报告 - " + Path.GetFileName(xmlPath);
        Width = 960; Height = 760;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.White;

        BuildReport(xmlPath);
    }

    private void BuildReport(string xmlPath)
    {
        XmlParser.ReportData data;
        try { data = XmlParser.ParseReport(xmlPath); }
        catch (Exception ex)
        {
            Controls.Add(new Label { Text = $"解析失败: {ex.Message}", ForeColor = Color.Red, Dock = DockStyle.Top, Height = 40 });
            return;
        }

        // 从文件名提取 SN/型号
        var fname = Path.GetFileName(xmlPath);
        var stem = fname.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? fname[..^4] : fname;
        stem = stem.Replace("(debug)", "").Replace("(Debug)", "");
        var parts = stem.Split('_');
        string sn = !string.IsNullOrEmpty(data.Sn) ? data.Sn : (parts.Length >= 6 ? parts[5] : "");
        string model = sn.Length >= 8 ? sn[..8] : "";
        bool isFail = fname.Length >= 2 && fname[..2].ToUpperInvariant() == "F_";
        int failCount = data.Tests.Count(t => t.Status == "Failed");
        string result = isFail || failCount > 0 ? "FAIL" : (data.PanelStatus == "Passed" ? "PASS" : data.PanelStatus);

        // ===== 顶部: 产品信息(Dock Top) =====
        var header = new Panel { Dock = DockStyle.Top, Height = 150, Padding = new Padding(16, 12, 16, 8) };

        var titleLbl = new Label { Text = "FCT 测试报告", AutoSize = true, Location = new Point(16, 8), Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold) };
        header.Controls.Add(titleLbl);

        var info = new TableLayoutPanel { Location = new Point(16, 44), AutoSize = true, ColumnCount = 4 };
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        AddInfo(info, 0, 0, "SN", sn);
        AddInfo(info, 0, 2, "型号", model);
        AddInfo(info, 1, 0, "机台", data.Tester);
        AddInfo(info, 1, 2, "测试员", data.FactoryUser);
        AddInfo(info, 2, 0, "测试时间", data.BatchTimestamp);
        info.Controls.Add(new Label { Text = "总体结果", ForeColor = Color.Gray, AutoSize = true, Margin = new Padding(0, 4, 0, 4) }, 2, 2);
        info.Controls.Add(new Label
        {
            Text = $"{result}  (测试项 {data.Tests.Count}, 失败 {failCount})", AutoSize = true, Margin = new Padding(0, 4, 0, 4),
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = result == "FAIL" ? Color.FromArgb(231, 76, 60) : Color.FromArgb(39, 174, 96),
        }, 3, 2);
        header.Controls.Add(info);

        // ===== 中部: 所有测试项表格(ListView, Fill) =====
        var list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true,
            Font = new Font("Microsoft YaHei UI", 9F),
        };
        list.Columns.Add("#", 45);
        list.Columns.Add("测试项", 360);
        list.Columns.Add("实测值", 130);
        list.Columns.Add("下限", 100);
        list.Columns.Add("上限", 100);
        list.Columns.Add("单位", 55);
        list.Columns.Add("判定", 70);

        int idx = 1;
        var failColor = Color.FromArgb(231, 76, 60);
        var passColor = Color.FromArgb(39, 174, 96);
        list.BeginUpdate();
        foreach (var t in data.Tests)
        {
            var item = new ListViewItem(idx.ToString());
            item.SubItems.Add(t.Name);
            item.SubItems.Add(t.Value);
            item.SubItems.Add(t.Lolim);
            item.SubItems.Add(t.Hilim);
            item.SubItems.Add(t.Unit);
            item.SubItems.Add(t.Status == "Passed" ? "PASS" : t.Status == "Failed" ? "FAIL" : t.Status);
            if (t.Status == "Failed")
            {
                item.ForeColor = failColor;
                item.Font = new Font(list.Font, FontStyle.Bold);
                item.BackColor = Color.FromArgb(253, 237, 236);   // 淡红背景
            }
            else if (t.Status == "Passed")
            {
                item.UseItemStyleForSubItems = true;
            }
            list.Items.Add(item);
            idx++;
        }
        list.EndUpdate();

        Controls.Add(list);     // Fill
        Controls.Add(header);   // Top
    }

    private static void AddInfo(TableLayoutPanel t, int row, int col, string label, string value)
    {
        t.Controls.Add(new Label { Text = label, ForeColor = Color.Gray, AutoSize = true, Margin = new Padding(0, 4, 0, 4) }, col, row);
        t.Controls.Add(new Label { Text = value, AutoSize = true, Margin = new Padding(0, 4, 0, 4), Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) }, col + 1, row);
    }
}

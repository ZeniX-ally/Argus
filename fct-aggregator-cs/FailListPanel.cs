namespace FctAggregator;

/// <summary>FAIL 记录页: 列表展示所有 FAIL(SN/首个fail项/时间), 双击打开排版后的 XML。</summary>
public class FailListPanel : Panel
{
    private readonly Engine _engine;
    private ListView _list = null!;
    private Label _countLabel = null!;

    public FailListPanel(Engine engine)
    {
        _engine = engine;
        BuildUi();
    }

    private void BuildUi()
    {
        Padding = new Padding(Theme.Gap);
        BackColor = Theme.Bg;

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(0, 6, 0, 4), BackColor = Theme.Bg };
        var btnRefresh = Theme.MakeButton("刷新", 76);
        btnRefresh.Margin = new Padding(0, 2, 4, 2);
        btnRefresh.Click += (_, _) => Refresh2();
        bar.Controls.Add(btnRefresh);
        _countLabel = new Label
        {
            Text = "共 0 条 FAIL", AutoSize = true,
            ForeColor = Theme.TextSub, Font = Theme.BodyBold, Margin = new Padding(6, 8, 0, 0),
        };
        bar.Controls.Add(_countLabel);
        var hint = new Label
        {
            Text = "（双击某行可查看该 XML 详细内容）", AutoSize = true,
            ForeColor = Theme.TextFaint, Font = Theme.Small, Margin = new Padding(14, 10, 0, 0),
        };
        bar.Controls.Add(hint);

        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true,
            BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.Surface, Font = Theme.Body,
        };
        _list.Columns.Add("SN", 280);
        _list.Columns.Add("失败项", 330);
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
            var all = _engine.Db.AllFails(_engine.ResolvedStationId);
            _countLabel.Text = $"共 {all.Count} 条 FAIL";
            foreach (var f in all)
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
        using var dlg = new FctFailRanker.XmlViewerForm(path);   // v2.9.3: 两份查看器合一，保留功能更全的那份
        dlg.ShowDialog();
    }
}

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
        using var dlg = new FctFailRanker.XmlViewerForm(path);   // v2.9.3: 两份查看器合一，保留功能更全的那份
        dlg.ShowDialog();
    }
}

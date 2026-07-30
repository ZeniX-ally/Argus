namespace FctAggregator;

/// <summary>设备状态面板: 型号/软件版本/COM设备(带小灯)/A2L。
/// 一屏铺满不滚动; 刷新只更新数据不重建控件(防闪烁)。</summary>
public class DeviceStatusPanel : Panel
{
    private readonly AppConfig _cfg;
    private System.Windows.Forms.Timer _timer = null!;

    private Label _iniLabel = null!;
    private Label _modelsValue = null!;
    private Label _fwValue = null!;
    private Label _a2lValue = null!;
    private Label _errorLabel = null!;
    private TableLayoutPanel _devGrid = null!;

    // 设备行控件缓存(灯/名/端口/状态), 按索引复用, 不重建
    private readonly List<(Label lamp, Label name, Label port, Label status)> _devRows = new();
    private const int MaxDevRows = 20;   // 预建行数(设备一般十几个)

    public DeviceStatusPanel(AppConfig cfg)
    {
        _cfg = cfg;
        DoubleBuffered = true;   // 防闪烁
        BuildUi();
        _timer = new System.Windows.Forms.Timer { Interval = 3000 };
        _timer.Tick += (_, _) => { if (Visible) Refresh2(); };
        _timer.Start();
    }

    private void BuildUi()
    {
        Padding = new Padding(Theme.Gap);
        BackColor = Theme.Bg;

        // 页标题由主窗口顶标统一显示（v2.8.0），这里只留 ini 路径与错误提示
        var title = new Label { Dock = DockStyle.Top, Height = 0, Visible = false };
        _iniLabel = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Theme.TextFaint, Font = Theme.Small };
        _errorLabel = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 0, ForeColor = Theme.Danger, Font = Theme.BodyBold, Visible = false };

        // 型号
        var modelTitle = SectionTitle("支持的型号");
        _modelsValue = ValueLabel();
        // 版本
        var fwTitle = SectionTitle("当前软件版本");
        _fwValue = ValueLabel();
        // A2L
        var a2lTitle = SectionTitle("A2L 文件");
        _a2lValue = ValueLabel();
        // 设备(标题 + 双列网格, 填充剩余空间)
        var devTitle = SectionTitle("设备状态");
        _devGrid = BuildDeviceGrid();

        // 顺序 Add(Dock=Top 是逆序显示, 所以后 Add 的在上面; 用一个容器按自然顺序)
        var host = new Panel { Dock = DockStyle.Fill };
        // 设备网格填充
        _devGrid.Dock = DockStyle.Fill;
        host.Controls.Add(_devGrid);      // fill
        host.Controls.Add(devTitle);      // top
        // 用 FlowLayout 顶部区域承载 型号/版本/A2L(紧凑)
        var topInfo = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Height = 175 };
        topInfo.Controls.Add(modelTitle);
        topInfo.Controls.Add(_modelsValue);
        topInfo.Controls.Add(fwTitle);
        topInfo.Controls.Add(_fwValue);
        topInfo.Controls.Add(a2lTitle);
        topInfo.Controls.Add(_a2lValue);
        host.Controls.Add(topInfo);       // top(在devTitle之上)

        // devGrid(fill) + devTitle(top) + topInfo(top) 的 Dock 顺序:
        // WinForms Dock: 后添加的 Top 更靠上。已按 fill, devTitle, topInfo 顺序添加 => topInfo最上, devTitle次之, devGrid填充

        Controls.Add(host);         // fill
        Controls.Add(_errorLabel);  // top
        Controls.Add(_iniLabel);    // top
        Controls.Add(title);        // top
    }

    private TableLayoutPanel BuildDeviceGrid()
    {
        // 双列: 每列 4 个子列(灯/名/端口/状态), 共 8 列
        var grid = new TableLayoutPanel
        {
            ColumnCount = 8, RowCount = (MaxDevRows + 1) / 2 + 1,
            AutoScroll = false, GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
        };
        // 列宽: 灯(20) 名(140) 端口(auto) 状态(60) x2
        int[] widths = { 22, 140, 120, 60, 22, 140, 120, 60 };
        foreach (var w in widths)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, w));

        int rowsPerCol = (MaxDevRows + 1) / 2;
        for (int i = 0; i < MaxDevRows; i++)
        {
            var lamp = new Label { Text = "", AutoSize = false, Width = 20, Height = 22, Font = new Font("Microsoft YaHei UI", 12F), TextAlign = ContentAlignment.MiddleCenter };
            var name = new Label { Text = "", AutoSize = false, Width = 138, Height = 22, Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
            var port = new Label { Text = "", AutoSize = false, Width = 118, Height = 22, Font = new Font("Microsoft YaHei UI", 9F), TextAlign = ContentAlignment.MiddleLeft };
            var status = new Label { Text = "", AutoSize = false, Width = 58, Height = 22, Font = new Font("Microsoft YaHei UI", 9F), TextAlign = ContentAlignment.MiddleLeft };
            _devRows.Add((lamp, name, port, status));

            int col = i < rowsPerCol ? 0 : 4;   // 左列 0-3, 右列 4-7
            int row = i < rowsPerCol ? i : i - rowsPerCol;
            grid.Controls.Add(lamp, col + 0, row);
            grid.Controls.Add(name, col + 1, row);
            grid.Controls.Add(port, col + 2, row);
            grid.Controls.Add(status, col + 3, row);
        }
        return grid;
    }

    private Label SectionTitle(string text) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(0, 6, 0, 2),
        ForeColor = Color.FromArgb(26, 82, 118),
        Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
    };

    private Label ValueLabel() => new()
    {
        Text = "", AutoSize = true, Margin = new Padding(4, 0, 0, 4),
        Font = new Font("Microsoft YaHei UI", 9F),
    };

    private string IniPath()
    {
        var p = _cfg.FctIniPath;
        return string.IsNullOrEmpty(p) ? @"C:\FTS\Apps\XPT\Cfg\FCT.ini" : p;
    }

    /// <summary>刷新: 只更新文字/颜色, 不重建控件(防闪烁)。</summary>
    public void Refresh2()
    {
        FctIniData data;
        try { data = FctIni.Parse(IniPath()); }
        catch (Exception ex)
        {
            ShowError($"解析异常: {ex.Message}");
            return;
        }

        _iniLabel.Text = $"FCT.ini: {data.IniPath}";

        if (!data.Found)
        {
            ShowError(data.Error ?? "FCT.ini 未找到");
            return;
        }
        _errorLabel.Visible = false;
        _errorLabel.Height = 0;

        // 型号
        SetIfChanged(_modelsValue, data.Models.Count > 0 ? string.Join("    ", data.Models) : "(无)");
        // 版本
        SetIfChanged(_fwValue, data.FwVersions.Count > 0
            ? string.Join("    ", data.FwVersions.Select(v => $"{v.Label}={v.Version}"))
            : "(无)");
        // A2L
        SetIfChanged(_a2lValue, data.A2lFiles.Count > 0
            ? string.Join("\n", data.A2lFiles.Select(a => $"{a.Label}: {a.File}"))
            : "(无)");

        // 设备(复用行控件, 只改文字颜色)
        var devs = data.Devices;
        for (int i = 0; i < _devRows.Count; i++)
        {
            var (lamp, name, port, status) = _devRows[i];
            if (i < devs.Count)
            {
                var dev = devs[i];
                string lampText = "●";
                Color lampColor, statusColor; string statusText;
                if (dev.Type == "com")
                {
                    if (dev.Online) { lampColor = Color.FromArgb(39, 174, 96); statusText = "在线"; statusColor = lampColor; }
                    else { lampColor = Color.FromArgb(231, 76, 60); statusText = "离线"; statusColor = lampColor; }
                }
                else { lampColor = Color.Silver; statusText = "USB"; statusColor = Color.Gray; }

                SetIfChanged(lamp, lampText); if (lamp.ForeColor != lampColor) lamp.ForeColor = lampColor;
                SetIfChanged(name, dev.Name);
                SetIfChanged(port, dev.Port);
                SetIfChanged(status, statusText); if (status.ForeColor != statusColor) status.ForeColor = statusColor;
            }
            else
            {
                // 多余的行清空
                SetIfChanged(lamp, ""); SetIfChanged(name, ""); SetIfChanged(port, ""); SetIfChanged(status, "");
            }
        }
    }

    private void ShowError(string msg)
    {
        _errorLabel.Text = msg;
        _errorLabel.Visible = true;
        _errorLabel.Height = 120;
    }

    private static void SetIfChanged(Label lbl, string text)
    {
        if (lbl.Text != text) lbl.Text = text;
    }
}

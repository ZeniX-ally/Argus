namespace FctAggregator;

/// <summary>
/// 设备状态面板: 型号 / 软件版本 / A2L / 设备（COM·USB）。
/// 刷新只更新数据不重建控件（防闪烁）。
///
/// v3.0.1 重新设计（对称 + 简洁）：
///   · 顶部信息两列对称白卡：左「支持的型号」右「当前软件版本」，A2L 跨整行；
///     行高自适应，不再固定 175px（旧版 A2L 文件多时下半截被裁掉）。
///   · 设备网格双列对称 + 列头，列宽按百分比铺满（旧版绝对列宽只占 684px，
///     内容区右侧留一大片空白）。
///   · 整个面板可滚动，设备再多也不截断。
/// </summary>
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
    private const int MaxDevRows = 20;   // 双列各 10 行（车间设备一般十几个）

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
        AutoScroll = true;   // 窗口矮 / 内容高时滚动，不截断

        // FCT.ini 路径（小字，超长省略号）
        _iniLabel = new Label
        {
            Dock = DockStyle.Top, Height = 22, ForeColor = Theme.TextFaint,
            Font = Theme.Small, AutoEllipsis = true, Padding = new Padding(0, 2, 0, 0),
        };
        // 错误提示（平时隐藏，出错时展开）
        _errorLabel = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 0, Visible = false,
            ForeColor = Theme.Danger, Font = Theme.BodyBold, Padding = new Padding(0, 6, 0, 6),
        };

        // ===================== 顶部信息：两列对称白卡 =====================
        var infoGrid = new TableLayoutPanel
        {
            ColumnCount = 2, RowCount = 4, Dock = DockStyle.Top, AutoSize = true,
            BackColor = Theme.Surface, Padding = new Padding(16, 12, 16, 10),
            Margin = new Padding(0, 0, 0, Theme.Gap),
        };
        infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int i = 0; i < 4; i++) infoGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _modelsValue = ValueLabel();
        _fwValue = ValueLabel();
        _a2lValue = ValueLabel(maxWidth: 620);   // A2L 多行（多个文件 \n 拼接），限宽自动换行

        infoGrid.Controls.Add(SectionTitle("支持的型号"), 0, 0);
        infoGrid.Controls.Add(SectionTitle("当前软件版本"), 1, 0);
        infoGrid.Controls.Add(_modelsValue, 0, 1);
        infoGrid.Controls.Add(_fwValue, 1, 1);
        infoGrid.Controls.Add(SectionTitle("A2L 文件"), 0, 2);
        infoGrid.SetColumnSpan(infoGrid.GetControlFromPosition(0, 2)!, 2);   // 标题跨整行
        infoGrid.Controls.Add(_a2lValue, 0, 3);
        infoGrid.SetColumnSpan(_a2lValue, 2);

        // ===================== 设备状态：双列对称 + 列头 =====================
        var devTitle = new Label
        {
            Text = "设备状态（● 在线 / ○ 离线）", Dock = DockStyle.Top, Height = 30,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
            ForeColor = Theme.Primary,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0),
        };
        _devGrid = BuildDeviceGrid();
        _devGrid.Dock = DockStyle.Fill;

        var devHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
        devHost.Controls.Add(_devGrid);   // fill（最先 Add = 最后停靠 = 吃剩余）
        devHost.Controls.Add(devTitle);   // top（后 Add = 先停靠 = 在上）

        // ===== 组装：Fill 最先 Add，Top 后 Add（后停靠的在上）=====
        Controls.Add(devHost);
        Controls.Add(infoGrid);
        Controls.Add(_errorLabel);
        Controls.Add(_iniLabel);          // 最后 Add -> 最先停靠 -> 最上面
    }

    /// <summary>
    /// 双列设备网格：每半 4 列（灯/名称/端口/状态），带列头行。
    /// 列宽用**百分比**铺满内容区（旧版绝对列宽 684px 在宽窗口右侧留大片空白）。
    /// </summary>
    private TableLayoutPanel BuildDeviceGrid()
    {
        const int rowsPerHalf = MaxDevRows / 2;   // 10
        var grid = new TableLayoutPanel
        {
            ColumnCount = 8, RowCount = rowsPerHalf + 1,   // +1 列头
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
            BackColor = Theme.Surface, Padding = new Padding(16, 0, 16, 12),
        };
        // 每半 4 列百分比：灯 8% / 名称 42% / 端口 28% / 状态 22%
        float[] half = { 8, 42, 28, 22 };
        for (int i = 0; i < 8; i++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, half[i % 4]));
        for (int r = 0; r <= rowsPerHalf; r++)
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));

        // 列头（两半对称：灯列留空）
        string[] heads = { "", "名称", "端口", "状态" };
        for (int halfIdx = 0; halfIdx < 2; halfIdx++)
            for (int c = 0; c < 4; c++)
            {
                grid.Controls.Add(new Label
                {
                    Text = heads[c], Dock = DockStyle.Fill,
                    Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                    ForeColor = Theme.TextSub, TextAlign = ContentAlignment.MiddleLeft,
                    Margin = new Padding(c == 3 ? 0 : 0, 0, 8, 0),
                }, halfIdx * 4 + c, 0);
            }

        // 数据行（每半 10 行）
        for (int i = 0; i < MaxDevRows; i++)
        {
            var lamp = new Label
            {
                Text = "", Dock = DockStyle.Fill, AutoSize = false,
                Font = new Font("Microsoft YaHei UI", 11F),
                TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0),
            };
            var name = new Label
            {
                Text = "", Dock = DockStyle.Fill, AutoSize = false,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0),
                AutoEllipsis = true,
            };
            var port = new Label
            {
                Text = "", Dock = DockStyle.Fill, AutoSize = false,
                Font = new Font("Microsoft YaHei UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0),
            };
            var status = new Label
            {
                Text = "", Dock = DockStyle.Fill, AutoSize = false,
                Font = new Font("Microsoft YaHei UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0),
            };
            _devRows.Add((lamp, name, port, status));

            int col = i < rowsPerHalf ? 0 : 4;   // 左半 0-3，右半 4-7
            int row = i < rowsPerHalf ? i : i - rowsPerHalf;
            grid.Controls.Add(lamp, col + 0, row + 1);   // +1 让出列头行
            grid.Controls.Add(name, col + 1, row + 1);
            grid.Controls.Add(port, col + 2, row + 1);
            grid.Controls.Add(status, col + 3, row + 1);
        }
        return grid;
    }

    /// <summary>小节标题（白卡内左对齐，AutoSize 让行高自适应）。</summary>
    private static Label SectionTitle(string text) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(0, 0, 12, 2),
        ForeColor = Theme.Primary,
        Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
    };

    /// <summary>字段值（AutoSize + 限宽：多行文本自动换行、行高自适应）。</summary>
    private static Label ValueLabel(int maxWidth = 300) => new()
    {
        Text = "", AutoSize = true, MaximumSize = new Size(maxWidth, 0),
        Margin = new Padding(0, 0, 12, 6),
        Font = new Font("Microsoft YaHei UI", 9F),
        ForeColor = Theme.TextMain,
    };

    private string IniPath()
    {
        var p = _cfg.FctIniPath;
        return string.IsNullOrEmpty(p) ? @"C:\FTS\Apps\PEU\Cfg\FCT.ini" : p;
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
        // A2L（多行）
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

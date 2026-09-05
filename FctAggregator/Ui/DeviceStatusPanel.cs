namespace FctAggregator;

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

    private readonly List<(Label lamp, Label name, Label port, Label status)> _devRows = new();
    private const int MaxDevRows = 20;

    public DeviceStatusPanel(AppConfig cfg)
    {
        _cfg = cfg;
        DoubleBuffered = true;
        BuildUi();
        _timer = new System.Windows.Forms.Timer { Interval = 3000 };
        _timer.Tick += (_, _) => { if (Visible) Refresh2(); };
        _timer.Start();
    }

    private void BuildUi()
    {
        Padding = new Padding(Theme.Gap);
        BackColor = Theme.Bg;
        AutoScroll = true;

        _iniLabel = new Label
        {
            Dock = DockStyle.Top, Height = 22, ForeColor = Theme.TextFaint,
            Font = Theme.Small, AutoEllipsis = true, Padding = new Padding(0, 2, 0, 0),
        };
        _errorLabel = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 0, Visible = false,
            ForeColor = Theme.Danger, Font = Theme.BodyBold, Padding = new Padding(0, 6, 0, 6),
        };

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
        _a2lValue = ValueLabel(maxWidth: 620);

        infoGrid.Controls.Add(SectionTitle("支持的型号"), 0, 0);
        infoGrid.Controls.Add(SectionTitle("当前软件版本"), 1, 0);
        infoGrid.Controls.Add(_modelsValue, 0, 1);
        infoGrid.Controls.Add(_fwValue, 1, 1);
        infoGrid.Controls.Add(SectionTitle("A2L 文件"), 0, 2);
        infoGrid.SetColumnSpan(infoGrid.GetControlFromPosition(0, 2)!, 2);
        infoGrid.Controls.Add(_a2lValue, 0, 3);
        infoGrid.SetColumnSpan(_a2lValue, 2);

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
        devHost.Controls.Add(_devGrid);
        devHost.Controls.Add(devTitle);

        Controls.Add(devHost);
        Controls.Add(infoGrid);
        Controls.Add(_errorLabel);
        Controls.Add(_iniLabel);
    }

    private TableLayoutPanel BuildDeviceGrid()
    {
        const int rowsPerHalf = MaxDevRows / 2;
        var grid = new TableLayoutPanel
        {
            ColumnCount = 8, RowCount = rowsPerHalf + 1,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
            BackColor = Theme.Surface, Padding = new Padding(16, 0, 16, 12),
        };
        float[] half = { 8, 42, 28, 22 };
        for (int i = 0; i < 8; i++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, half[i % 4]));
        for (int r = 0; r <= rowsPerHalf; r++)
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));

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

            int col = i < rowsPerHalf ? 0 : 4;
            int row = i < rowsPerHalf ? i : i - rowsPerHalf;
            grid.Controls.Add(lamp, col + 0, row + 1);
            grid.Controls.Add(name, col + 1, row + 1);
            grid.Controls.Add(port, col + 2, row + 1);
            grid.Controls.Add(status, col + 3, row + 1);
        }
        return grid;
    }

    private static Label SectionTitle(string text) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(0, 0, 12, 2),
        ForeColor = Theme.Primary,
        Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
    };

    private static Label ValueLabel(int maxWidth = 300) => new()
    {
        Text = "", AutoSize = true, MaximumSize = new Size(maxWidth, 0),
        Margin = new Padding(0, 0, 12, 6),
        Font = new Font("Microsoft YaHei UI", 9F),
        ForeColor = Theme.TextMain,
    };

    public void Refresh2()
    {
        FctIniData data;
        try { data = FctIni.Parse(_cfg.FctIniPath); }
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

        SetIfChanged(_modelsValue, data.Models.Count > 0 ? string.Join("    ", data.Models) : "(无)");
        SetIfChanged(_fwValue, data.FwVersions.Count > 0
            ? string.Join("    ", data.FwVersions.Select(v => $"{v.Label}={v.Version}"))
            : "(无)");
        SetIfChanged(_a2lValue, data.A2lFiles.Count > 0
            ? string.Join("\n", data.A2lFiles.Select(a => $"{a.Label}: {a.File}"))
            : "(无)");

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
                    if (dev.Online) { lampColor = Theme.Success; statusText = "在线"; statusColor = lampColor; }
                    else { lampColor = Theme.Danger; statusText = "离线"; statusColor = lampColor; }
                }
                else { lampColor = Color.Silver; statusText = "USB"; statusColor = Color.Gray; }

                SetIfChanged(lamp, lampText); if (lamp.ForeColor != lampColor) lamp.ForeColor = lampColor;
                SetIfChanged(name, dev.Name);
                SetIfChanged(port, dev.Port);
                SetIfChanged(status, statusText); if (status.ForeColor != statusColor) status.ForeColor = statusColor;
            }
            else
            {
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

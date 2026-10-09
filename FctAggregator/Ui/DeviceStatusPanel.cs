using System.Drawing.Drawing2D;

namespace FctAggregator;

public class DeviceStatusPanel : Panel
{
    private readonly AppConfig _cfg;
    private readonly System.Windows.Forms.Timer _timer;
    private FileSystemWatcher? _iniWatch;
    private string _watchPath = "";
    private volatile bool _busy;
    private volatile bool _pending;
    private FctIniData _data = new();

    private readonly Label _clock = new();
    private readonly Label _iniPath = new();
    private readonly KpiCard _kpiOn;
    private readonly KpiCard _kpiOff;
    private readonly KpiCard _kpiUsb;
    private readonly TableLayoutPanel _grid;
    private readonly Label _empty;
    private readonly Label _meta = new();
    private int _layoutSig;

    public DeviceStatusPanel(AppConfig cfg)
    {
        _cfg = cfg;
        DoubleBuffered = true;
        BackColor = Theme.Bg;
        Padding = new Padding(Theme.Gap);

        var header = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Bg };
        var title = new Label
        {
            Text = "设备状态", Font = Theme.PageTitle, ForeColor = Theme.TextMain,
            AutoSize = false, Width = 160, Height = 36, TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(0, 2),
        };
        _clock = new Label
        {
            Font = Theme.Small, ForeColor = Theme.TextFaint, AutoSize = false,
            Width = 280, Height = 36, TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(168, 2), Text = "尚未探测",
        };
        var btn = Theme.MakeButton("立即刷新", 96, primary: true);
        btn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btn.Location = new Point(Width - 108, 4);
        header.Resize += (_, _) => btn.Left = Math.Max(460, header.Width - 108);
        btn.Click += (_, _) => Kick(forceIni: true);
        header.Controls.Add(title);
        header.Controls.Add(_clock);
        header.Controls.Add(btn);

        var kpiHost = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 92, ColumnCount = 3, RowCount = 1,
            BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 6),
        };
        kpiHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        kpiHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        kpiHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.4f));
        _kpiOn = new KpiCard("COM 在线", Theme.Success, big: false) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.Gap, 0) };
        _kpiOff = new KpiCard("COM 离线", Theme.Danger, big: false) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.Gap, 0) };
        _kpiUsb = new KpiCard("USB 已登记", Theme.Info, big: false) { Dock = DockStyle.Fill, Margin = new Padding(0) };
        kpiHost.Controls.Add(_kpiOn, 0, 0);
        kpiHost.Controls.Add(_kpiOff, 1, 0);
        kpiHost.Controls.Add(_kpiUsb, 2, 0);

        _grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Padding = new Padding(0, 4, 0, 4),
        };
        _grid.Resize += (_, _) => RelayoutGrid();
        _empty = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Font = Theme.Body, ForeColor = Theme.TextFaint,
            Text = "FCT.ini 未登记 COM / USB",
        };

        var foot = new Panel { Dock = DockStyle.Bottom, Height = 40, BackColor = Theme.Surface, Padding = new Padding(12, 4, 12, 4) };
        _iniPath = new Label
        {
            Dock = DockStyle.Left, Width = 420, Font = Theme.Small, ForeColor = Theme.TextFaint,
            AutoEllipsis = true, Text = "FCT.ini: —", TextAlign = ContentAlignment.MiddleLeft,
        };
        _meta = new Label
        {
            Dock = DockStyle.Fill, Font = Theme.Small, ForeColor = Theme.TextMain,
            AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0),
        };
        foot.Controls.Add(_meta);
        foot.Controls.Add(_iniPath);
        foot.Resize += (_, _) => _iniPath.Width = Math.Clamp(foot.ClientSize.Width / 2, 200, 560);
        const string hint = "在线 = Windows 枚举到该 COM。插拔走设备变更消息，最多再等 0.2 秒轮询。";
        var tip = new ToolTip { ShowAlways = true };
        tip.SetToolTip(foot, hint);
        tip.SetToolTip(_iniPath, hint);

        Controls.Add(_grid);
        Controls.Add(foot);
        Controls.Add(kpiHost);
        Controls.Add(header);

        VisibleChanged += (_, _) => { if (Visible) Kick(false); };
        _timer = new System.Windows.Forms.Timer { Interval = 200 };
        _timer.Tick += (_, _) => { if (Visible) Kick(false); };
        _timer.Start();
        HandleCreated += (_, _) => Kick(false);
    }

    public void Refresh2() => Kick(forceIni: true);

    public void NotifyHardwareChange() => Kick(false);

    private void Kick(bool forceIni)
    {
        if (forceIni) FctIni.Invalidate();
        if (_busy) { _pending = true; return; }
        _busy = true;
        _pending = false;
        var cfgPath = _cfg.FctIniPath;
        Task.Run(() =>
        {
            FctIniData data;
            try { data = FctIni.Snapshot(cfgPath); }
            catch (Exception ex) { data = new FctIniData { Error = ex.Message, IniPath = cfgPath, ProbedAt = DateTime.Now }; }
            if (!IsHandleCreated || IsDisposed) { _busy = false; return; }
            try
            {
                BeginInvoke(() =>
                {
                    try { Apply(data); }
                    catch (Exception ex) { Logger.Warning($"[设备状态] 刷新 UI: {ex.Message}"); }
                    finally
                    {
                        _busy = false;
                        if (_pending) Kick(false);
                    }
                });
            }
            catch { _busy = false; }
        });
    }

    private void Apply(FctIniData data)
    {
        _data = data;
        ArmWatcher(data.Found ? data.IniPath : "");
        var ageMs = (DateTime.Now - data.ProbedAt).TotalMilliseconds;
        _clock.Text = data.ProbedAt == default
            ? "尚未探测"
            : $"探测 {data.ProbedAt:HH:mm:ss}  ·  {Math.Max(0, (int)ageMs / 1000)} 秒前";

        if (!data.Found)
        {
            _kpiOn.Set("—");
            _kpiOff.Set("—");
            _kpiUsb.Set("—");
            _iniPath.Text = "FCT.ini: 未找到";
            _iniPath.ForeColor = Theme.Danger;
            _meta.Text = data.Error ?? "未找到 FCT.ini";
            _meta.ForeColor = Theme.Danger;
            SyncCards(Array.Empty<DeviceInfo>());
            return;
        }

        _iniPath.ForeColor = Theme.TextFaint;
        _iniPath.Text = "FCT.ini: " + data.IniPath;
        int on = data.Devices.Count(d => d.Type == "com" && d.Online);
        int off = data.Devices.Count(d => d.Type == "com" && !d.Online);
        int usb = data.Devices.Count(d => d.Type == "usb");
        _kpiOn.Set(on.ToString(), $"共 {on + off} 路 COM");
        _kpiOff.Set(off.ToString(), off == 0 ? "全部在线" : "插拔即时刷新");
        _kpiUsb.Set(usb.ToString(), "不探在线，只显示登记");

        var models = data.Models.Count > 0 ? string.Join("  ", data.Models) : "（ini 未写型号）";
        var fw = data.FwVersions.Count > 0
            ? string.Join("  ", data.FwVersions.Select(v => $"{v.Label}={v.Version}"))
            : "（无版本）";
        var extra = data.ExtraSystemComCount > 0
            ? $"  ·  系统另有 {data.ExtraSystemComCount} 个 COM 未在 ini 登记"
            : "";
        _meta.ForeColor = Theme.TextMain;
        _meta.Text = $"型号  {models}    软件  {fw}{extra}";
        if (data.A2lFiles.Count > 0)
            _meta.Text += "    A2L  " + string.Join("；", data.A2lFiles.Select(a => a.Label));

        SyncCards(data.Devices);
    }

    private void SyncCards(IReadOnlyList<DeviceInfo> devs)
    {
        if (devs.Count == 0)
        {
            while (_grid.Controls.Count > 0)
            {
                var last = _grid.Controls[_grid.Controls.Count - 1];
                _grid.Controls.RemoveAt(_grid.Controls.Count - 1);
                if (last != _empty) last.Dispose();
            }
            if (_empty.Parent != _grid) _grid.Controls.Add(_empty);
            _empty.Visible = true;
            _layoutSig = 0;
            RelayoutGrid();
            return;
        }

        if (_empty.Parent == _grid)
        {
            _grid.Controls.Remove(_empty);
            _empty.Visible = false;
        }
        while (_grid.Controls.Count > devs.Count)
        {
            var last = _grid.Controls[_grid.Controls.Count - 1];
            _grid.Controls.RemoveAt(_grid.Controls.Count - 1);
            last.Dispose();
        }
        for (int i = 0; i < devs.Count; i++)
        {
            if (i >= _grid.Controls.Count)
            {
                var card = new DeviceCard { Dock = DockStyle.Fill, Margin = new Padding(6) };
                _grid.Controls.Add(card);
            }
            ((DeviceCard)_grid.Controls[i]).Bind(devs[i]);
        }
        _layoutSig = 0;
        RelayoutGrid();
    }

    private void RelayoutGrid()
    {
        int n = 0;
        foreach (Control c in _grid.Controls)
            if (c is DeviceCard) n++;
        if (n <= 0)
        {
            _grid.ColumnCount = 1;
            _grid.RowCount = 1;
            _grid.ColumnStyles.Clear();
            _grid.RowStyles.Clear();
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            return;
        }
        int w = Math.Max(1, _grid.ClientSize.Width);
        int cols = n <= 3 ? n : Math.Clamp(w / 220, 2, 6);
        if (cols > n) cols = n;
        int rows = (n + cols - 1) / cols;
        int sig = n * 100 + cols * 10 + rows;
        if (sig == _layoutSig && _grid.ColumnCount == cols && _grid.RowCount == rows) return;
        _layoutSig = sig;

        _grid.SuspendLayout();
        _grid.ColumnCount = cols;
        _grid.RowCount = rows;
        _grid.ColumnStyles.Clear();
        _grid.RowStyles.Clear();
        for (int c = 0; c < cols; c++)
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
        for (int r = 0; r < rows; r++)
            _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
        int i = 0;
        foreach (Control ctl in _grid.Controls)
        {
            if (ctl is not DeviceCard) continue;
            _grid.SetColumn(ctl, i % cols);
            _grid.SetRow(ctl, i / cols);
            _grid.SetColumnSpan(ctl, 1);
            _grid.SetRowSpan(ctl, 1);
            i++;
        }
        _grid.ResumeLayout(true);
    }

    private void ArmWatcher(string iniPath)
    {
        if (string.Equals(_watchPath, iniPath, StringComparison.OrdinalIgnoreCase) && _iniWatch != null)
            return;
        try { _iniWatch?.Dispose(); } catch { }
        _iniWatch = null;
        _watchPath = iniPath ?? "";
        if (string.IsNullOrWhiteSpace(iniPath) || !File.Exists(iniPath)) return;
        try
        {
            var dir = Path.GetDirectoryName(iniPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            _iniWatch = new FileSystemWatcher(dir, Path.GetFileName(iniPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            void OnIni()
            {
                FctIni.Invalidate();
                // UI-W10：本回调在 FileSystemWatcher 的线程池线程上跑，裸 BeginInvoke 与窗口销毁竞态时
                // 会抛 InvalidOperationException——线程池线程上的未处理异常会直接结束进程（AppDomain 兜底只记日志）。
                // UiAsync.Post 已处理「已销毁 / 句柄未创建」两种情况。
                UiAsync.Post(this, () => Kick(false));
            }
            _iniWatch.Changed += (_, _) => OnIni();
            _iniWatch.Renamed += (_, _) => OnIni();
        }
        catch (Exception ex) { Logger.Warning($"[设备状态] 监视 FCT.ini 失败: {ex.Message}"); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            try { _iniWatch?.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}

internal sealed class DeviceCard : Control
{
    private DeviceInfo _dev = new();

    public DeviceCard()
    {
        DoubleBuffered = true;
        Dock = DockStyle.Fill;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Surface;
    }

    public void Bind(DeviceInfo d)
    {
        if (_dev.Name == d.Name && _dev.Port == d.Port && _dev.Type == d.Type && _dev.Online == d.Online)
            return;
        _dev = new DeviceInfo { Name = d.Name, Port = d.Port, Type = d.Type, Online = d.Online };
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        try
        {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.DrawCard(g, ClientRectangle, null);

        bool usb = _dev.Type == "usb";
        Color lamp = usb ? Theme.LampUsb : (_dev.Online ? Theme.Success : Theme.Danger);
        string st = usb ? "USB 已登记" : (_dev.Online ? "在线" : "离线");

        int pad = Math.Max(16, Math.Min(Width, Height) / 16);
        int lampR = Math.Clamp(Math.Min(Width, Height) / 10, 12, 28);
        var nameFont = Height >= 160 ? Theme.SectionTitle : Theme.BodyBold;
        var portFont = Height >= 140 ? Theme.Body : Theme.Mono;
        var stFont = Height >= 180 ? Theme.NumberSmall : Theme.BodyBold;
        int nameH = Math.Max(28, lampR + 8);
        int portH = Height >= 140 ? 32 : 24;
        int stH = Height >= 180 ? 40 : 28;
        int gap = Height >= 160 ? 12 : 8;
        int blockH = nameH + gap + portH + gap + stH;
        int top = Math.Max(pad, (Height - blockH) / 2);

        using (var b = new SolidBrush(lamp))
            g.FillEllipse(b, pad, top + (nameH - lampR) / 2, lampR, lampR);

        int nameLeft = pad + lampR + 12;
        TextRenderer.DrawText(g, string.IsNullOrEmpty(_dev.Name) ? "(未命名)" : _dev.Name, nameFont,
            new Rectangle(nameLeft, top, Width - nameLeft - pad, nameH), Theme.TextMain,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        int y = top + nameH + gap;
        TextRenderer.DrawText(g, _dev.Port, portFont,
            new Rectangle(pad, y, Width - pad * 2, portH), Theme.TextSub,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        y += portH + gap;
        TextRenderer.DrawText(g, st, stFont,
            new Rectangle(pad, y, Width - pad * 2, stH), lamp,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[自绘] OnPaint 异常已兜底: {ex.Message}");
            try { TextRenderer.DrawText(e.Graphics, "（自绘异常，详见日志）", Theme.Small, ClientRectangle, Theme.TextFaint); } catch { }
        }
    }
}

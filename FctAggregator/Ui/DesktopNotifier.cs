using System.Text;

namespace FctAggregator;

public static class DesktopNotifier
{
    public static bool Enabled { get; set; } = true;

    public static int MinIntervalSeconds { get; set; } = 15;

    public static event Action? Activated;

    private static NotifyIcon? _icon;
    private static System.Windows.Forms.Timer? _timer;
    private static SynchronizationContext? _ui;
    private static readonly object Gate = new();
    private static readonly List<string> _queue = new();
    private static DateTime _lastShown = DateTime.MinValue;
    private static bool _inited;

    public static void Init()
    {
        if (_inited) return;
        _inited = true;
        _ui = SynchronizationContext.Current;
        try
        {
            var ico = AppIcon.Load(16);

            _icon = new NotifyIcon
            {
                Icon = ico,
                Visible = true,
                Text = "Argus",
                BalloonTipIcon = ToolTipIcon.Warning,
            };
            _icon.BalloonTipClicked += (_, _) => Activated?.Invoke();
            _icon.MouseDoubleClick += (_, _) => Activated?.Invoke();

            var menu = new ContextMenuStrip();
            menu.Items.Add("打开主窗口", null, (_, _) => Activated?.Invoke());
            var toggle = new ToolStripMenuItem("桌面提示") { CheckOnClick = true, Checked = Enabled };
            toggle.CheckedChanged += (_, _) => Enabled = toggle.Checked;
            menu.Items.Add(toggle);
            _icon.ContextMenuStrip = menu;

            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += (_, _) => Flush();
            _timer.Start();

            Logger.Info($"桌面提示已就绪（{(Enabled ? "开启" : "关闭")}，最小间隔 {MinIntervalSeconds}s）");
        }
        catch (Exception ex)
        {
            _icon = null;
            Logger.Warning($"桌面提示初始化失败(功能自动禁用): {ex.Message}");
        }
    }

    public static void NotifyFail(TestRecord rec)
    {
        var item = FirstFailItem(rec);
        var sb = new StringBuilder();
        sb.Append(string.IsNullOrEmpty(rec.Model) ? "未知型号" : rec.Model);
        if (!string.IsNullOrWhiteSpace(rec.Sn)) sb.Append($" · SN {rec.Sn}");
        if (!string.IsNullOrWhiteSpace(item)) sb.Append($"\n{item}");
        Enqueue(sb.ToString());
    }

    public static void NotifyRaw(string text) => Enqueue(text);

    private static void Enqueue(string text)
    {
        if (!Enabled) return;
        lock (Gate) _queue.Add(text);
        if (_ui == null) Flush();
    }

    private static void Flush()
    {
        if (_icon == null) return;
        List<string> batch;
        lock (Gate)
        {
            if (_queue.Count == 0) return;
            if ((DateTime.Now - _lastShown).TotalSeconds < MinIntervalSeconds) return;
            batch = new List<string>(_queue);
            _queue.Clear();
            _lastShown = DateTime.Now;
        }

        string title, body;
        if (batch.Count == 1)
        {
            title = "⚠ FCT 不良";
            body = batch[0];
        }
        else
        {
            title = $"⚠ FCT 不良 ×{batch.Count}";
            body = string.Join("\n", batch.Take(2).Select(x => x.Replace("\n", " | ")));
            if (batch.Count > 2) body += $"\n…另有 {batch.Count - 2} 条";
        }
        body += "\n点击查看待办";

        try
        {
            if (body.Length > 220) body = body[..220] + "…";
            _icon.ShowBalloonTip(8000, title, body, ToolTipIcon.Warning);
        }
        catch (Exception ex) { Logger.Warning($"桌面提示弹出失败: {ex.Message}"); }
    }

    private static string FirstFailItem(TestRecord rec)
    {
        if (rec.FailedTests.Count > 0)
        {
            var f = rec.FailedTests[0];
            var extra = string.IsNullOrWhiteSpace(f.Value) ? "" : $" = {f.Value}{f.Unit}";
            return rec.FailedTests.Count > 1
                 ? $"{f.Name}{extra}（共 {rec.FailedTests.Count} 项不良）"
                 : $"{f.Name}{extra}";
        }
        return rec.FailReason ?? "";
    }

    public static void Shutdown()
    {
        try { _timer?.Stop(); _timer?.Dispose(); } catch { }
        try
        {
            if (_icon != null) { _icon.Visible = false; _icon.Dispose(); }
        }
        catch { }
        _timer = null;
        _icon = null;
    }
}

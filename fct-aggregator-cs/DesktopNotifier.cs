using System.Text;

namespace FctAggregator;

/// <summary>
/// Windows 原生桌面提示（不良报警）。
///
/// 实现方式：托盘 <see cref="NotifyIcon"/> + BalloonTip。
/// Win10/11 下这就是系统通知中心里的原生 Toast（会进「通知」列表、跟随勿扰模式），
/// 而且**不需要**引 UWP 包、也不用注册 AUMID —— 车间里的机台不装额外依赖最稳。
///
/// 使用约定：
///   · <see cref="Init"/> 必须在 UI 线程（MainForm 构造里）调用一次，内部记住 UI 同步上下文
///   · <see cref="NotifyFail"/> 可从任意后台线程调用（Engine 的文件监控线程）
///   · 高频不良会被合并：<see cref="MinIntervalSeconds"/> 秒内只弹一次，期间的条数汇总成一条
/// </summary>
public static class DesktopNotifier
{
    /// <summary>总开关（config.json: desktop_notify）。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>两次弹窗的最小间隔秒数，期间的不良合并成一条汇总提示。</summary>
    public static int MinIntervalSeconds { get; set; } = 15;

    /// <summary>用户点了通知（或双击托盘图标）—— MainForm 用它把窗口拉到前台并切到待办看板。</summary>
    public static event Action? Activated;

    private static NotifyIcon? _icon;
    private static System.Windows.Forms.Timer? _timer;
    private static SynchronizationContext? _ui;
    private static readonly object Gate = new();
    private static readonly List<string> _queue = new();
    private static DateTime _lastShown = DateTime.MinValue;
    private static bool _inited;

    /// <summary>在 UI 线程调用一次。失败(极老系统/无桌面会话)只记日志，不影响主流程。</summary>
    public static void Init()
    {
        if (_inited) return;
        _inited = true;
        _ui = SynchronizationContext.Current;
        try
        {
            // v2.9.4: 托盘要 16×16；原来读 BaseDir 下的文件，而发布包里根本没有那个文件，
            // 于是一直退成系统灰图标。现在统一走 AppIcon（嵌入资源优先）。
            var ico = AppIcon.Load(16);

            _icon = new NotifyIcon
            {
                Icon = ico,
                Visible = true,
                Text = "FCT-Aggregator",
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

            // 1 秒心跳: 到点了才把积压的不良汇总弹出去(节流)
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

    /// <summary>报一条不良。线程安全，可从后台线程调用。</summary>
    public static void NotifyFail(TestRecord rec)
    {
        var item = FirstFailItem(rec);
        var sb = new StringBuilder();
        sb.Append(string.IsNullOrEmpty(rec.Model) ? "未知型号" : rec.Model);
        if (!string.IsNullOrWhiteSpace(rec.Sn)) sb.Append($" · SN {rec.Sn}");
        if (!string.IsNullOrWhiteSpace(item)) sb.Append($"\n{item}");
        Enqueue(sb.ToString());
    }

    /// <summary>自定义一条提示（调试页测试用）。</summary>
    public static void NotifyRaw(string text) => Enqueue(text);

    private static void Enqueue(string text)
    {
        if (!Enabled) return;
        lock (Gate) _queue.Add(text);
        // UI 线程上的 timer 会在下一拍统一弹出; 没有 UI 上下文时兜底直接试一次
        if (_ui == null) Flush();
    }

    /// <summary>UI 线程：到间隔就把积压的合并弹出。</summary>
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
            // BalloonTip 文本上限 ~255 字符，超了 Windows 会直接不显示
            if (body.Length > 220) body = body[..220] + "…";
            _icon.ShowBalloonTip(8000, title, body, ToolTipIcon.Warning);
        }
        catch (Exception ex) { Logger.Warning($"桌面提示弹出失败: {ex.Message}"); }
    }

    /// <summary>取一条有代表性的故障项文本。</summary>
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

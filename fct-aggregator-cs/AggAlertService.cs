using System.Text;
using System.Text.Json;

namespace FctAggregator;

/// <summary>
/// 聚合端飞书告警/汇总服务（Phase 3）。
///
/// 两类推送（webhook 未配置则全部跳过，纯增强不阻塞聚合）：
///   1. 离线告警：机台在线→离线翻转立即推卡片（红 header「机台离线」+ 机台名 + 最后确认时间）；
///   2. 定时汇总：每 agg_summary_minutes（默认 60）分钟推一次各机台 FAIL 总数/在线状态汇总卡片。
///
/// 线程模型：<see cref="Start"/> 起一个后台线程做定时汇总；MachineOffline 事件由
/// AggWatcher 心跳线程回调（事件内做异步推送，不阻塞心跳轮询）。
/// 幂等/防抖：同一机台 10 分钟内只报一次离线（重复翻转不刷屏）；汇总按周期整点近似。
/// </summary>
public class AggAlertService : IDisposable
{
    private const int OfflineMinIntervalSec = 600;   // 同一机台两次离线告警最小间隔（防抖）
    private const int OnlineMinIntervalSec = 600;    // 同一机台两次上线通知最小间隔（防抖，与离线对称）

    private readonly AggWatcher _watcher;
    private readonly AggDatabase _db;
    private readonly string _webhook;
    private readonly int _summaryMinutes;
    private readonly Dictionary<string, long> _lastOfflineAt = new();   // 机台 -> 上次离线告警 TickCount
    private readonly Dictionary<string, long> _lastOnlineAt = new();    // 机台 -> 上次上线通知 TickCount
    private Thread? _thread;
    private volatile bool _stopping;
    private volatile bool _started;

    public AggAlertService(AggWatcher watcher, AggDatabase db, string webhook, int summaryMinutes)
    {
        _watcher = watcher;
        _db = db;
        _webhook = webhook ?? "";
        _summaryMinutes = Math.Max(1, summaryMinutes);
    }

    /// <summary>是否配置了飞书 webhook（未配置则 Start 只挂事件不推，汇总线程不启动）。</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(_webhook);

    /// <summary>启动：订阅离线/上线事件 + 起汇总线程（webhook 未配置时只记录日志不启动）。
    /// 防抖预热：启动瞬间已在离线/在线的机台直接记入防抖表，避免服务重启后
    /// 对「本来就离线/在线」的机台重复告警（v3.4.11 连通性加固）。</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        if (!Enabled)
        {
            Logger.Info("[聚合告警] agg_webhook_url 未配置，离线告警与定时汇总均不启用");
            return;
        }
        _watcher.MachineOffline += OnMachineOffline;
        _watcher.MachineOnline += OnMachineOnline;
        // 防抖预热：现有状态不告警（离线机台不重复报、在线机台不报上线）
        try
        {
            var now = Environment.TickCount64;
            foreach (var m in _watcher.GetMachines())
            {
                if (m.Online) _lastOnlineAt[m.Machine] = now;
                else _lastOfflineAt[m.Machine] = now;
            }
        }
        catch (Exception ex) { Logger.Warning($"[聚合告警] 防抖预热失败: {ex.Message}"); }
        _thread = new Thread(SummaryLoop) { IsBackground = true, Name = "agg-alert" };
        _thread.Start();
        Logger.Info($"[聚合告警] 已启用: 离线告警+上线通知(同机台{OfflineMinIntervalSec / 60}分钟防抖) + 定时汇总(每{_summaryMinutes}分钟)");
    }

    /// <summary>停止：退订事件 + 停后台线程。</summary>
    public void Stop()
    {
        if (!_started) return;
        _stopping = true;
        _watcher.MachineOffline -= OnMachineOffline;
        _watcher.MachineOnline -= OnMachineOnline;
        try { _thread?.Join(3000); } catch { }
        _started = false;
    }

    public void Dispose() => Stop();

    // ==================== 离线告警 ====================

    private void OnMachineOffline(string machine, DateTime lastSeen)
    {
        var now = Environment.TickCount64;
        lock (_lastOfflineAt)
        {
            if (_lastOfflineAt.TryGetValue(machine, out var last) && now - last < OfflineMinIntervalSec * 1000L)
                return;   // 防抖：短时间内同机台重复翻转不刷屏
            _lastOfflineAt[machine] = now;
        }
        Logger.Warning($"[聚合告警] 机台离线: {machine}（最后确认 {lastSeen:yyyy-MM-dd HH:mm:ss}）");
        _ = SendOfflineAlertAsync(machine, lastSeen);
    }

    private async Task SendOfflineAlertAsync(string machine, DateTime lastSeen)
    {
        var header = new
        {
            title = new { tag = "plain_text", content = $"机台离线 · {machine}" },
            template = "red",
        };
        var elements = new List<object>
        {
            new
            {
                tag = "div",
                fields = new object[]
                {
                    Field("机台", machine),
                    Field("最后确认", lastSeen == DateTime.MinValue ? "—" : lastSeen.ToString("yyyy-MM-dd HH:mm:ss")),
                },
            },
            new
            {
                tag = "markdown",
                content = $"**机台 {Escape(machine)} 已离线**，请到产线检查（网络断连 / 程序退出 / 机器断电）。",
            },
            new
            {
                tag = "note",
                elements = new object[] { new { tag = "plain_text", content = $"Argus 聚合告警 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}" } },
            },
        };
        await PostCard(new { header, elements });
    }

    // ==================== 恢复上线通知（v3.4.11 连通性加固） ====================

    private void OnMachineOnline(string machine, DateTime lastSeen)
    {
        var now = Environment.TickCount64;
        lock (_lastOnlineAt)
        {
            if (_lastOnlineAt.TryGetValue(machine, out var last) && now - last < OnlineMinIntervalSec * 1000L)
                return;   // 防抖：短时间重复翻转不刷屏
            _lastOnlineAt[machine] = now;
        }
        Logger.Info($"[聚合告警] 机台恢复上线: {machine}");
        _ = SendOnlineAlertAsync(machine, lastSeen);
    }

    private async Task SendOnlineAlertAsync(string machine, DateTime lastSeen)
    {
        var header = new
        {
            title = new { tag = "plain_text", content = $"机台恢复上线 · {machine}" },
            template = "green",
        };
        var elements = new List<object>
        {
            new
            {
                tag = "div",
                fields = new object[]
                {
                    Field("机台", machine),
                    Field("确认时间", lastSeen == DateTime.MinValue ? "—" : lastSeen.ToString("yyyy-MM-dd HH:mm:ss")),
                },
            },
            new
            {
                tag = "markdown",
                content = $"**机台 {Escape(machine)} 已恢复上线**，聚合链路已重新打通，数据推送恢复正常。",
            },
            new
            {
                tag = "note",
                elements = new object[] { new { tag = "plain_text", content = $"Argus 聚合告警 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}" } },
            },
        };
        await PostCard(new { header, elements });
    }

    // ==================== 定时汇总 ====================

    private void SummaryLoop()
    {
        // 启动后等一个周期再首次推（避免服务刚起就刷屏）
        var waitMs = _summaryMinutes * 60_000;
        while (!_stopping)
        {
            for (int i = 0; i < waitMs / 1000 && !_stopping; i++)
            {
                try { Thread.Sleep(1000); } catch { }
            }
            if (_stopping) break;
            try { SendSummaryAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { Logger.Warning($"[聚合告警] 定时汇总失败: {ex.Message}"); }
        }
    }

    private async Task SendSummaryAsync()
    {
        List<AggMachineStatus> machines;
        try { machines = _watcher.GetMachines(); }
        catch (Exception ex)
        {
            Logger.Warning($"[聚合告警] 汇总取机台状态失败: {ex.Message}");
            return;
        }

        long totalFails;
        try { totalFails = _db.FailCount(""); }
        catch (Exception ex)
        {
            Logger.Warning($"[聚合告警] 汇总取 FAIL 总数失败: {ex.Message}");
            totalFails = -1;
        }

        int online = machines.Count(m => m.Online);
        var header = new
        {
            title = new { tag = "plain_text", content = $"Argus 聚合汇总 · {DateTime.Now:MM-dd HH:mm}" },
            template = "blue",
        };

        var elements = new List<object>
        {
            new
            {
                tag = "div",
                fields = new object[]
                {
                    Field("机台总数", machines.Count.ToString()),
                    Field("在线", $"{online} / {machines.Count}"),
                    Field("累计 FAIL", totalFails < 0 ? "—" : totalFails.ToString()),
                    Field("离线", (machines.Count - online).ToString()),
                },
            },
        };

        if (machines.Count > 0)
        {
            var sb = new StringBuilder("**各机台明细**\n");
            foreach (var m in machines)
            {
                var dot = m.Online ? "●" : "○";
                var hb = string.IsNullOrEmpty(m.LastHeartbeat) ? "无心跳" : m.LastHeartbeat;
                sb.AppendLine($"{dot} {Escape(m.Machine)} · FAIL {m.FailCount} · 心跳 {hb}");
            }
            elements.Add(new { tag = "markdown", content = sb.ToString().TrimEnd() });
        }

        elements.Add(new
        {
            tag = "note",
            elements = new object[] { new { tag = "plain_text", content = $"Argus 聚合服务 · 每 {_summaryMinutes} 分钟自动推送" } },
        });

        await PostCard(new { header, elements });
    }

    // ==================== 飞书卡片发送（复用机台端卡片样式约定） ====================

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static object Field(string label, string value) => new
    {
        is_short = true,
        text = new { tag = "lark_md", content = $"**{label}**\n{Escape(value)}" },
    };

    /// <summary>卡片 POST（无重试——告警是尽力而为，失败下轮汇总还会带状态；返回是否成功）。</summary>
    private async Task<bool> PostCard(object card)
    {
        if (!Enabled) return false;
        if (!_webhook.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Error("[聚合告警] 拒绝发送: agg_webhook_url 必须 https://");
            return false;
        }
        try
        {
            var payload = JsonSerializer.Serialize(new { msg_type = "interactive", card });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_webhook, content);
            if (!resp.IsSuccessStatusCode)
                Logger.Warning($"[聚合告警] 飞书推送失败: HTTP {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[聚合告警] 飞书推送异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>飞书 lark_md 转义（与机台端 FeishuNotifier.Escape 同约定：粗体/竖线/链接/反引号）。</summary>
    private static string Escape(string? s)
    {
        s ??= "";
        return s.Replace("*", "\\*").Replace("|", "\\|")
                .Replace("[", "\\[").Replace("]", "\\]").Replace("`", "\\`");
    }
}

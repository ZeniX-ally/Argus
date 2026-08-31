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

    private readonly MeshReceiver? _receiver;   // v3.5.0 去中心化告警源（非 null 时优先）
#pragma warning disable CS0618   // 遗留中心模式源（仅旧 GUI/兼容路径构造）
    private readonly AggWatcher? _watcher;
#pragma warning restore CS0618
    private readonly AggDatabase _db;
    private readonly string _webhook;
    private readonly int _summaryMinutes;
    private readonly Dictionary<string, long> _lastOfflineAt = new();   // 机台 -> 上次离线告警 TickCount
    private readonly Dictionary<string, long> _lastOnlineAt = new();    // 机台 -> 上次上线通知 TickCount
    private readonly Dictionary<string, long> _lastDiskAlert = new();   // P6 设备磁盘告警防抖
    private readonly Dictionary<string, long> _lastCpuAlert = new();    // P6 CPU 告警防抖
    private readonly Dictionary<string, long> _lastDeviceOffline = new(); // P6 设备离线告警防抖
    private readonly Dictionary<string, long> _lastYieldAlert = new(); // P8 良率跌破告警防抖
    private Thread? _thread;
    private Thread? _deviceThread;
    private volatile bool _stopping;
    private volatile bool _started;

    /// <summary>v3.5.0 去中心化：告警源为本机 MeshReceiver（peer 离线/上线事件同源）。</summary>
    public AggAlertService(MeshReceiver receiver, AggDatabase db, string webhook, int summaryMinutes)
    {
        _receiver = receiver;
        _db = db;
        _webhook = webhook ?? "";
        _summaryMinutes = Math.Max(1, summaryMinutes);
    }

    /// <summary>遗留中心模式源（v3.5.0 前，仅旧 AggCenterForm GUI 兼容路径使用）。</summary>
#pragma warning disable CS0618
    public AggAlertService(AggWatcher watcher, AggDatabase db, string webhook, int summaryMinutes)
    {
        _watcher = watcher;
        _db = db;
        _webhook = webhook ?? "";
        _summaryMinutes = Math.Max(1, summaryMinutes);
    }
#pragma warning restore CS0618

    /// <summary>告警源机台在线状态（去中心化：MeshReceiver peer 视图；旧模式：AggWatcher 机台视图）。</summary>
    private List<PeerStatusDto> GetMachines()
    {
        if (_receiver != null) return _receiver.GetPeerStatuses();
#pragma warning disable CS0618
        return _watcher!.GetMachines().Select(m => new PeerStatusDto
        {
            Machine = m.Machine, Online = m.Online,
            LastHeartbeat = m.LastHeartbeat,
            FailCount = m.FailCount,
        }).ToList();
#pragma warning restore CS0618
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
        SubscribeSourceEvents();
        // 防抖预热：现有状态不告警（离线机台不重复报、在线机台不报上线）
        try
        {
            var now = Environment.TickCount64;
            foreach (var m in GetMachines())
            {
                if (m.Online) _lastOnlineAt[m.Machine] = now;
                else _lastOfflineAt[m.Machine] = now;
            }
        }
        catch (Exception ex) { Logger.Warning($"[聚合告警] 防抖预热失败: {ex.Message}"); }
        _thread = new Thread(SummaryLoop) { IsBackground = true, Name = "agg-alert" };
        _thread.Start();
        // P6 设备告警轮询（磁盘/CPU/离线，每 60s 一次，复用同 webhook）
        _deviceThread = new Thread(DeviceAlertLoop) { IsBackground = true, Name = "agg-device-alert" };
        _deviceThread.Start();
        Logger.Info($"[聚合告警] 已启用: 离线告警+上线通知(同机台{OfflineMinIntervalSec / 60}分钟防抖) + 定时汇总(每{_summaryMinutes}分钟) + 设备告警(磁盘/CPU/离线) + 良率跌破(阈值 {AppConfig.Instance.YieldAlertYieldPct}% {(AppConfig.Instance.YieldAlertEnabled ? "启用" : "关闭")})");
    }

    /// <summary>停止：退订事件 + 停后台线程。</summary>
    public void Stop()
    {
        if (!_started) return;
        _stopping = true;
        UnsubscribeSourceEvents();
        try { _thread?.Join(3000); } catch { }
        try { _deviceThread?.Join(3000); } catch { }
        _started = false;
    }

    public void Dispose() => Stop();

    /// <summary>订阅告警源事件（按构造源分发）。</summary>
    private void SubscribeSourceEvents()
    {
        if (_receiver != null)
        {
            _receiver.PeerOffline += OnMachineOffline;
            _receiver.PeerOnline += OnMachineOnline;
        }
#pragma warning disable CS0618
        else
        {
            _watcher!.MachineOffline += OnMachineOffline;
            _watcher.MachineOnline += OnMachineOnline;
        }
#pragma warning restore CS0618
    }

    /// <summary>退订告警源事件。</summary>
    private void UnsubscribeSourceEvents()
    {
        if (_receiver != null)
        {
            _receiver.PeerOffline -= OnMachineOffline;
            _receiver.PeerOnline -= OnMachineOnline;
        }
#pragma warning disable CS0618
        else
        {
            _watcher!.MachineOffline -= OnMachineOffline;
            _watcher.MachineOnline -= OnMachineOnline;
        }
#pragma warning restore CS0618
    }

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
        List<PeerStatusDto> machines;
        try { machines = GetMachines(); }
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

    // ==================== P6 设备告警（磁盘/CPU/离线，进飞书，复用同 webhook） ====================

    private void DeviceAlertLoop()
    {
        // 启动后等 30s 再首次扫描（等首批 device_info 上报完成）
        for (int i = 0; i < 30 && !_stopping; i++) try { Thread.Sleep(1000); } catch { }
        while (!_stopping)
        {
            try { CheckDeviceAlerts(); }
            catch (Exception ex) { Logger.Warning($"[聚合告警] 设备告警扫描失败: {ex.Message}"); }
            try { CheckYieldAlerts(); }
            catch (Exception ex) { Logger.Warning($"[聚合告警] 良率告警扫描失败: {ex.Message}"); }
            for (int i = 0; i < 60 && !_stopping; i++) try { Thread.Sleep(1000); } catch { }
        }
    }

    /// <summary>P8 告警规则中心：良率跌破检测（基于 yld_daily 今日汇总，阈值来自 Config.yield_alert_*）。</summary>
    private void CheckYieldAlerts()
    {
        if (!Enabled) return;
        var cfg = AppConfig.Instance;
        if (!cfg.YieldAlertEnabled || cfg.YieldAlertYieldPct <= 0) return;
        var thr = cfg.YieldAlertYieldPct;
        // 取今日各机台良率（yld_daily 按 (machine, test_date) 幂等，今日统计已在心跳 30s 上报）
        var today = DateTime.Now.ToString("yyyyMMdd");
        List<AggDatabase.YldDailyRow> rows;
        try { rows = _db.QueryDailyStats(dateFromYmd: today, dateToYmd: today); } catch { return; }
        if (rows.Count == 0) return;
        // 按机台聚合（同一机台今日可能多行？实际上 machine+date 唯一，这里直接取）
        var byMachine = new Dictionary<string, (int total, int pass)>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var cur = byMachine.GetValueOrDefault(r.Machine);
            cur.total += r.Total; cur.pass += r.Pass;
            byMachine[r.Machine] = cur;
        }
        foreach (var kv in byMachine)
        {
            var machine = kv.Key;
            var total = kv.Value.total;
            var pass = kv.Value.pass;
            if (total == 0) continue;
            var y = pass * 100.0 / total;
            if (y >= thr) continue;
            var key = machine;
            var nowTick = Environment.TickCount64;
            bool should = false;
            lock (_lastYieldAlert)
            {
                if (!_lastYieldAlert.TryGetValue(key, out var last) || nowTick - last >= 600_000)
                { _lastYieldAlert[key] = nowTick; should = true; }
            }
            if (!should) continue;
            Logger.Warning($"[告警规则] 良率跌破: {machine} {y:F2}% < {thr}%（{pass}/{total}）");
            try { _db.InsertAlertHistory(machine, "yield", $"yield {y:F2}% < {thr}% ({pass}/{total})", $"今日良率跌破阈值 {thr}%"); } catch { }
            _ = SendYieldAlertAsync(machine, y, thr, pass, total);
        }
    }

    /// <summary>统一规则快照（供 /api/alerts/rules 可观测）：磁盘/CPU/离线/良率四规则阈值与开关。</summary>
    public static object GetAlertRulesSnapshot()
    {
        var cfg = AppConfig.Instance;
        return new
        {
            disk = new { enabled = cfg.DeviceAlertDiskFreeGb > 0, threshold_gb = cfg.DeviceAlertDiskFreeGb },
            cpu = new { enabled = cfg.DeviceAlertCpuPct > 0, threshold_pct = cfg.DeviceAlertCpuPct },
            offline = new { enabled = cfg.DeviceAlertOfflineMinutes > 0, threshold_minutes = cfg.DeviceAlertOfflineMinutes },
            yield = new { enabled = cfg.YieldAlertEnabled && cfg.YieldAlertYieldPct > 0, threshold_pct = cfg.YieldAlertYieldPct },
            webhook_set = !string.IsNullOrWhiteSpace(cfg.AggWebhookUrl),
            summary_minutes = cfg.AggSummaryMinutes,
        };
    }

    private void CheckDeviceAlerts()
    {
        if (!Enabled) return;
        List<DeviceInfoRow> devices;
        try { devices = _db.ListDeviceInfos(); } catch { return; }
        if (devices.Count == 0) return;
        var cfg = AppConfig.Instance;
        var diskThr = cfg.DeviceAlertDiskFreeGb;
        var cpuThr = cfg.DeviceAlertCpuPct;
        var offlineMin = cfg.DeviceAlertOfflineMinutes;
        var now = DateTime.Now;
        foreach (var dev in devices)
        {
            // 离线判定：last_seen 超阈值（与 MeshReceiver 90s 心跳超时独立，设备阈值可配 5 分钟）
            if (offlineMin > 0 && !string.IsNullOrEmpty(dev.LastSeen))
            {
                if (DateTime.TryParse(dev.LastSeen, out var last))
                {
                    var offMin = (now - last).TotalMinutes;
                    if (offMin >= offlineMin)
                    {
                        var key = dev.Machine;
                        var nowTick = Environment.TickCount64;
                        lock (_lastDeviceOffline)
                        {
                            if (_lastDeviceOffline.TryGetValue(key, out var lastTick) && nowTick - lastTick < 600_000) continue;
                            _lastDeviceOffline[key] = nowTick;
                        }
                        Logger.Warning($"[设备告警] 离线: {dev.Machine} 已 {offMin:F0} 分钟未上报");
                        try { _db.InsertAlertHistory(dev.Machine, "offline", $"offline {offMin:F0}min >= {offlineMin}min", $"离线 {offMin:F0} 分钟"); } catch { }
                        _ = SendDeviceOfflineAlertAsync(dev, offMin);
                    }
                }
            }
            // 磁盘告警：剩余 < 阈值
            if (diskThr > 0 && dev.DiskFreeGb > 0 && dev.DiskFreeGb < diskThr)
            {
                var key = dev.Machine;
                var nowTick = Environment.TickCount64;
                bool should = false;
                lock (_lastDiskAlert)
                {
                    if (!_lastDiskAlert.TryGetValue(key, out var last) || nowTick - last >= 600_000)
                    { _lastDiskAlert[key] = nowTick; should = true; }
                }
                if (should)
                {
                    Logger.Warning($"[设备告警] 磁盘低: {dev.Machine} 剩余 {dev.DiskFreeGb:F1}GB < {diskThr}GB");
                    try { _db.InsertAlertHistory(dev.Machine, "disk", $"disk {dev.DiskFreeGb:F1}GB < {diskThr}GB", $"磁盘剩余 {dev.DiskFreeGb:F1}GB"); } catch { }
                     _ = SendDeviceDiskAlertAsync(dev, diskThr);
                }
            }
            // CPU 告警：当前采样 > 阈值（0=关闭）
            if (cpuThr > 0 && dev.CpuUsage >= cpuThr)
            {
                var key = dev.Machine;
                var nowTick = Environment.TickCount64;
                bool should = false;
                lock (_lastCpuAlert)
                {
                    if (!_lastCpuAlert.TryGetValue(key, out var last) || nowTick - last >= 600_000)
                    { _lastCpuAlert[key] = nowTick; should = true; }
                }
                if (should)
                {
                    Logger.Warning($"[设备告警] CPU 高: {dev.Machine} {dev.CpuUsage:F1}% >= {cpuThr}%");
                    try { _db.InsertAlertHistory(dev.Machine, "cpu", $"cpu {dev.CpuUsage:F1}% >= {cpuThr}%", $"CPU {dev.CpuUsage:F1}%"); } catch { }
                     _ = SendDeviceCpuAlertAsync(dev, cpuThr);
                }
            }
        }
    }

    private async Task SendDeviceDiskAlertAsync(DeviceInfoRow dev, double thr)
    {
        var header = new { title = new { tag = "plain_text", content = $"设备磁盘告警 · {dev.Machine}" }, template = "red" };
        var elements = new List<object>
        {
            new { tag = "div", fields = new object[] { Field("机台", dev.Machine), Field("剩余磁盘", $"{dev.DiskFreeGb:F1} GB"), Field("阈值", $"< {thr} GB"), Field("主机", dev.Hostname) } },
            new { tag = "markdown", content = $"**机台 {Escape(dev.Machine)} 磁盘空间不足**（剩余 {dev.DiskFreeGb:F1}GB < {thr}GB），请及时清理。" },
            new { tag = "note", elements = new object[] { new { tag = "plain_text", content = $"Argus 设备告警 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}" } } },
        };
        await PostCard(new { header, elements });
    }

    private async Task SendDeviceCpuAlertAsync(DeviceInfoRow dev, int thr)
    {
        var header = new { title = new { tag = "plain_text", content = $"设备 CPU 告警 · {dev.Machine}" }, template = "red" };
        var elements = new List<object>
        {
            new { tag = "div", fields = new object[] { Field("机台", dev.Machine), Field("CPU", $"{dev.CpuUsage:F1}%"), Field("阈值", $">= {thr}%"), Field("主机", dev.Hostname) } },
            new { tag = "markdown", content = $"**机台 {Escape(dev.Machine)} CPU 持续高位**（{dev.CpuUsage:F1}% >= {thr}%）。" },
            new { tag = "note", elements = new object[] { new { tag = "plain_text", content = $"Argus 设备告警 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}" } } },
        };
        await PostCard(new { header, elements });
    }

    private async Task SendDeviceOfflineAlertAsync(DeviceInfoRow dev, double offMin)
    {
        var header = new { title = new { tag = "plain_text", content = $"设备离线 · {dev.Machine}" }, template = "red" };
        var elements = new List<object>
        {
            new { tag = "div", fields = new object[] { Field("机台", dev.Machine), Field("离线时长", $"{offMin:F0} 分钟"), Field("最后上报", dev.LastSeen ?? "—"), Field("IP", dev.Ip ?? "—") } },
            new { tag = "markdown", content = $"**设备 {Escape(dev.Machine)} 已离线 {offMin:F0} 分钟**，请检查网络/程序/供电。" },
            new { tag = "note", elements = new object[] { new { tag = "plain_text", content = $"Argus 设备告警 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}" } } },
        };
        await PostCard(new { header, elements });
    }

    private async Task SendYieldAlertAsync(string machine, double y, double thr, int pass, int total)
    {
        var header = new { title = new { tag = "plain_text", content = $"良率跌破 · {machine}" }, template = "red" };
        var elements = new List<object>
        {
            new { tag = "div", fields = new object[] { Field("机台", machine), Field("良率", $"{y:F2}%"), Field("阈值", $"< {thr}%"), Field("样本", $"{pass}/{total}") } },
            new { tag = "markdown", content = $"**机台 {Escape(machine)} 良率跌破**（{y:F2}% < {thr}%），请关注制程异常。" },
            new { tag = "note", elements = new object[] { new { tag = "plain_text", content = $"Argus 良率告警 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}" } } },
        };
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

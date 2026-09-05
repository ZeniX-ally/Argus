using System.Text;
using System.Text.Json;

namespace FctAggregator;

public class AggAlertService : IDisposable
{
    private const int OfflineMinIntervalSec = 600;
    private const int OnlineMinIntervalSec = 600;

    private readonly MeshReceiver? _receiver;
#pragma warning disable CS0618
    private readonly AggWatcher? _watcher;
#pragma warning restore CS0618
    private readonly AggDatabase _db;
    private readonly string _webhook;
    private readonly int _summaryMinutes;
    private readonly Dictionary<string, long> _lastOfflineAt = new();
    private readonly Dictionary<string, long> _lastOnlineAt = new();
    private readonly Dictionary<string, long> _lastDiskAlert = new();
    private readonly Dictionary<string, long> _lastCpuAlert = new();
    private readonly Dictionary<string, long> _lastDeviceOffline = new();
    private readonly Dictionary<string, long> _lastYieldAlert = new();
    private Thread? _thread;
    private Thread? _deviceThread;
    private volatile bool _stopping;
    private volatile bool _started;

    public AggAlertService(MeshReceiver receiver, AggDatabase db, string webhook, int summaryMinutes)
    {
        _receiver = receiver;
        _db = db;
        _webhook = webhook ?? "";
        _summaryMinutes = Math.Max(1, summaryMinutes);
    }

#pragma warning disable CS0618
    public AggAlertService(AggWatcher watcher, AggDatabase db, string webhook, int summaryMinutes)
    {
        _watcher = watcher;
        _db = db;
        _webhook = webhook ?? "";
        _summaryMinutes = Math.Max(1, summaryMinutes);
    }
#pragma warning restore CS0618

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

    public bool Enabled => !string.IsNullOrWhiteSpace(_webhook);

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
        _deviceThread = new Thread(DeviceAlertLoop) { IsBackground = true, Name = "agg-device-alert" };
        _deviceThread.Start();
        Logger.Info($"[聚合告警] 已启用: 离线告警+上线通知(同机台{OfflineMinIntervalSec / 60}分钟防抖) + 定时汇总(每{_summaryMinutes}分钟) + 设备告警(磁盘/CPU/离线) + 良率跌破(阈值 {AppConfig.Instance.YieldAlertYieldPct}% {(AppConfig.Instance.YieldAlertEnabled ? "启用" : "关闭")})");
    }

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

    private void OnMachineOffline(string machine, DateTime lastSeen)
    {
        var now = Environment.TickCount64;
        lock (_lastOfflineAt)
        {
            if (_lastOfflineAt.TryGetValue(machine, out var last) && now - last < OfflineMinIntervalSec * 1000L)
                return;
            _lastOfflineAt[machine] = now;
        }
        Logger.Warning($"[聚合告警] 机台离线: {machine}（最后确认 {lastSeen:yyyy-MM-dd HH:mm:ss}）");
        _ = SendOfflineAlertAsync(machine, lastSeen);
    }

    private async Task SendOfflineAlertAsync(string machine, DateTime lastSeen)
    {
        var elements = new List<object>
        {
            FeishuCardV2.FieldRow(("机台", machine), ("最后确认", lastSeen == DateTime.MinValue ? "—" : lastSeen.ToString("yyyy-MM-dd HH:mm:ss"))),
            FeishuCardV2.Md($"**机台 {FeishuCardV2.Escape(machine)} 已离线**，请到产线检查（网络断连 / 程序退出 / 机器断电）。"),
            FeishuCardV2.Hr(),
            FeishuCardV2.Note($"Argus 聚合告警 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}"),
        };
        await PostCard(FeishuCardV2.Root($"机台离线 · {machine}", "red", elements, bannerImgKey: AppConfig.Instance.FeishuBannerImgKey));
    }

    private void OnMachineOnline(string machine, DateTime lastSeen)
    {
        var now = Environment.TickCount64;
        lock (_lastOnlineAt)
        {
            if (_lastOnlineAt.TryGetValue(machine, out var last) && now - last < OnlineMinIntervalSec * 1000L)
                return;
            _lastOnlineAt[machine] = now;
        }
        Logger.Info($"[聚合告警] 机台恢复上线: {machine}");
        _ = SendOnlineAlertAsync(machine, lastSeen);
    }

    private async Task SendOnlineAlertAsync(string machine, DateTime lastSeen)
    {
        var elements = new List<object>
        {
            FeishuCardV2.FieldRow(("机台", machine), ("确认时间", lastSeen == DateTime.MinValue ? "—" : lastSeen.ToString("yyyy-MM-dd HH:mm:ss"))),
            FeishuCardV2.Md($"**机台 {FeishuCardV2.Escape(machine)} 已恢复上线**，聚合链路已重新打通，数据推送恢复正常。"),
            FeishuCardV2.Hr(),
            FeishuCardV2.Note($"Argus 聚合告警 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}"),
        };
        await PostCard(FeishuCardV2.Root($"机台恢复上线 · {machine}", "green", elements, bannerImgKey: AppConfig.Instance.FeishuBannerImgKey));
    }

    private void SummaryLoop()
    {
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

        var elements = new List<object>
        {
            FeishuCardV2.FieldRow(("机台总数", machines.Count.ToString()), ("在线", $"{online} / {machines.Count}")),
            FeishuCardV2.FieldRow(("累计 FAIL", totalFails < 0 ? "—" : totalFails.ToString()), ("离线", (machines.Count - online).ToString())),
        };

        if (machines.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var m in machines)
            {
                var dot = m.Online ? "●" : "○";
                var hb = string.IsNullOrEmpty(m.LastHeartbeat) ? "无心跳" : m.LastHeartbeat;
                sb.AppendLine($"{dot} {FeishuCardV2.Escape(m.Machine)} · FAIL {m.FailCount} · 心跳 {hb}");
            }
            elements.Add(FeishuCardV2.Hr());
            elements.Add(FeishuCardV2.Md("**各机台明细**", heading: true));
            elements.Add(FeishuCardV2.Md(sb.ToString().TrimEnd()));
        }

        elements.Add(FeishuCardV2.Hr());
        elements.Add(FeishuCardV2.Note($"Argus 聚合服务 · 每 {_summaryMinutes} 分钟自动推送"));

        await PostCard(FeishuCardV2.Root($"Argus 聚合汇总 · {DateTime.Now:MM-dd HH:mm}", "blue", elements, bannerImgKey: AppConfig.Instance.FeishuBannerImgKey));
    }

    private void DeviceAlertLoop()
    {
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

    private void CheckYieldAlerts()
    {
        if (!Enabled) return;
        var cfg = AppConfig.Instance;
        if (!cfg.YieldAlertEnabled || cfg.YieldAlertYieldPct <= 0) return;
        var thr = cfg.YieldAlertYieldPct;
        var today = DateTime.Now.ToString("yyyyMMdd");
        List<AggDatabase.YldDailyRow> rows;
        try { rows = _db.QueryDailyStats(dateFromYmd: today, dateToYmd: today); } catch { return; }
        if (rows.Count == 0) return;
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
        var elements = new List<object>
        {
            FeishuCardV2.FieldRow(("机台", dev.Machine), ("剩余磁盘", $"{dev.DiskFreeGb:F1} GB")),
            FeishuCardV2.FieldRow(("阈值", $"< {thr} GB"), ("主机", dev.Hostname)),
            FeishuCardV2.Md($"**机台 {FeishuCardV2.Escape(dev.Machine)} 磁盘空间不足**（剩余 {dev.DiskFreeGb:F1}GB < {thr}GB），请及时清理。"),
            FeishuCardV2.Hr(),
            FeishuCardV2.Note($"Argus 设备告警 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}"),
        };
        await PostCard(FeishuCardV2.Root($"设备磁盘告警 · {dev.Machine}", "red", elements, bannerImgKey: AppConfig.Instance.FeishuBannerImgKey));
    }

    private async Task SendDeviceCpuAlertAsync(DeviceInfoRow dev, int thr)
    {
        var elements = new List<object>
        {
            FeishuCardV2.FieldRow(("机台", dev.Machine), ("CPU", $"{dev.CpuUsage:F1}%")),
            FeishuCardV2.FieldRow(("阈值", $">= {thr}%"), ("主机", dev.Hostname)),
            FeishuCardV2.Md($"**机台 {FeishuCardV2.Escape(dev.Machine)} CPU 持续高位**（{dev.CpuUsage:F1}% >= {thr}%）。"),
            FeishuCardV2.Hr(),
            FeishuCardV2.Note($"Argus 设备告警 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}"),
        };
        await PostCard(FeishuCardV2.Root($"设备 CPU 告警 · {dev.Machine}", "red", elements, bannerImgKey: AppConfig.Instance.FeishuBannerImgKey));
    }

    private async Task SendDeviceOfflineAlertAsync(DeviceInfoRow dev, double offMin)
    {
        var elements = new List<object>
        {
            FeishuCardV2.FieldRow(("机台", dev.Machine), ("离线时长", $"{offMin:F0} 分钟")),
            FeishuCardV2.FieldRow(("最后上报", dev.LastSeen ?? "—"), ("IP", dev.Ip ?? "—")),
            FeishuCardV2.Md($"**设备 {FeishuCardV2.Escape(dev.Machine)} 已离线 {offMin:F0} 分钟**，请检查网络/程序/供电。"),
            FeishuCardV2.Hr(),
            FeishuCardV2.Note($"Argus 设备告警 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}"),
        };
        await PostCard(FeishuCardV2.Root($"设备离线 · {dev.Machine}", "red", elements, bannerImgKey: AppConfig.Instance.FeishuBannerImgKey));
    }

    private async Task SendYieldAlertAsync(string machine, double y, double thr, int pass, int total)
    {
        var elements = new List<object>
        {
            FeishuCardV2.FieldRow(("机台", machine), ("良率", $"{y:F2}%")),
            FeishuCardV2.FieldRow(("阈值", $"< {thr}%"), ("样本", $"{pass}/{total}")),
            FeishuCardV2.Md($"**机台 {FeishuCardV2.Escape(machine)} 良率跌破**（{y:F2}% < {thr}%），请关注制程异常。"),
            FeishuCardV2.Hr(),
            FeishuCardV2.Note($"Argus 良率告警 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}"),
        };
        await PostCard(FeishuCardV2.Root($"良率跌破 · {machine}", "red", elements, bannerImgKey: AppConfig.Instance.FeishuBannerImgKey));
    }

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

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
}

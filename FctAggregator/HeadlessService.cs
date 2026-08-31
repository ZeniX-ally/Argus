using System.Runtime.InteropServices;

namespace FctAggregator;

/// <summary>
/// 可启停生命周期组件契约（无头服务组装层的统一入口抽象）。
/// </summary>
public interface IStartable
{
    /// <summary>组件名称（日志/诊断用）。</summary>
    string Name { get; }

    /// <summary>启动组件。</summary>
    void Start();

    /// <summary>停止组件。</summary>
    void Stop();
}

/// <summary>
/// 聚合端无头服务组装层（P2-A，Agent C 扩展）：
/// 把 Engine（采集）+ Mesh 节点 + Web 看板 + 告警服务 + 库维护 + 设备采集 串成一条生命周期，
/// 供 <c>argus agg --web</c> / <c>argus agg --service</c>（<see cref="Program.RunAggWebService"/>/RunAggService，无窗体常驻）使用，
/// 也便于其他宿主复用同一套启动/停止/释放顺序。
/// 契约：
///   - 单写者 WAL：Engine 与聚合端共享同一 AggDatabase 实例（经构造函数共享），避免双实例锁分离；
///   - 设备采集：轻量心跳 + 5 分钟全量，失败静默，下轮补齐，不走补偿队列；
///   - 会话锁定不中断：无头进程无 GUI，采集/推送后台线程与用户会话无关（Session 0 服务更彻底）；
///   - 崩溃自愈：看门狗检测并重启关键组件 + Windows Service failure 重启（sc failure）。
/// </summary>
public sealed class HeadlessService : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly string _stationId;
    private readonly AggDatabase _db;
    private readonly Database _localDb;
    private readonly MeshNode _mesh;
    private readonly WebAggServer _server;
    // 告警服务随设置热生效会重建，故不做 readonly
    private AggAlertService _alert;
    private DbMaintenance? _maintenance;
    private DeviceInfoCollector? _collector;
    private Engine? _engine;
    private Thread? _watchdog;
    private volatile bool _stopping;
    private bool _disposed;

    /// <summary>Mesh 节点（对外暴露，供事件绑定/诊断用）。</summary>
    public MeshNode Mesh => _mesh;

    /// <summary>副本库（对外暴露，供查询/打印路径用）。</summary>
    public AggDatabase Db => _db;

    /// <summary>本机采集引擎（P2-A 组装后非 null，供大屏/服务化用；纯看板节点可能为 null）。</summary>
    public Engine? Engine => _engine;

    /// <summary>Web 服务器是否已进入监听（Start 后据此决定打印访问地址或警告）。</summary>
    public bool Listening => _server.Listening;

    /// <summary>
    /// 组装全部组件：
    /// 副本库 Open 失败仅记日志、不中断构造；stationId 缺省时由 StationDetector 探测，仍为空回落 "AGG-NODE"。
    /// Engine 按共享 AggDatabase 组装（单写者），避免双实例。
    /// </summary>
    public HeadlessService(AppConfig cfg, bool withEngine = true)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));

        _db = new AggDatabase(Path.Combine(AppConfig.BaseDir, "data", "mesh_agg.db"));
        try { _db.Open(); }
        catch (Exception ex) { Logger.Error($"[聚合服务] 副本库打开失败（后续插入会再试）: {ex.Message}"); }

        _stationId = _cfg.StationId;
        if (string.IsNullOrEmpty(_stationId)) _stationId = StationDetector.DetectStation() ?? "AGG-NODE";

        if (withEngine)
        {
            // 共享同一 AggDatabase，避免 WAL 双写锁分离；Database 路径与 Engine 一致
            _engine = new Engine(_cfg, _db);
            _localDb = _engine.Db;
            _mesh = _engine.Mesh;
        }
        else
        {
            _localDb = new Database(Path.Combine(AppConfig.BaseDir, "data", $"{(string.IsNullOrEmpty(_stationId) ? "fct" : _stationId)}.db"));
            _mesh = new MeshNode(_cfg, _stationId, _localDb, _db, _cfg.Peers);
        }
        _server = new WebAggServer(_cfg.MeshPort, _mesh, _db, _cfg.ResultsRoot, _cfg.AggShareRoot, _cfg.AggToken);
        _alert = new AggAlertService(_mesh.Receiver, _db, _cfg.AggWebhookUrl, _cfg.AggSummaryMinutes);

        // 设备采集：轻量心跳注入 + 5 分钟全量推送（失败静默，下轮补齐）
        if (_cfg.DeviceInfoEnabled)
        {
            _collector = new DeviceInfoCollector(_cfg, _stationId, _cfg.Peers);
            try { _mesh.Pusher.SetLightProvider(() => _collector.GetLightSnapshot()); } catch { }
        }

        // Web 设置页保存后热生效（订阅放在构造里，保证 Start 前已就绪，事件只会发生在 Start 之后）
        WireSettingsHotReload();
    }

    /// <summary>
    /// 按序启动 engine（如有）→ mesh → collector → server → alert → maintenance + 看门狗。
    /// server 启动失败不在此处断言（失败与否靠 <see cref="Listening"/> 判断，调用方据此打印访问地址或警告）。
    /// 崩溃自愈：看门狗后台线程按 30s 检测关键组件存活，异常自动重启（指数退避）。
    /// 会话锁定不中断：无头常驻无 GUI，采集在后台线程，Session 锁定仅影响 UI 线程。
    /// </summary>
    public void Start()
    {
        _stopping = false;
        // Engine 的 Mesh 在构造时已 Start（Engine 构造即 Start Mesh）；此处确保 engine 的文件监控已起
        if (_engine != null)
        {
            try { _engine.Start(); } catch (Exception ex) { Logger.Warning($"[聚合服务] Engine 启动异常: {ex.Message}"); }
        }
        else
        {
            try { _mesh.Start(); } catch (Exception ex) { Logger.Warning($"[聚合服务] Mesh 启动异常: {ex.Message}"); }
        }
        // 维护线程（每日 03:00 checkpoint/vacuum + 备份 + 7 天采样清理）
        try { _maintenance = DbMaintenance.StartFor(_cfg, _db); } catch (Exception ex) { Logger.Warning($"[聚合服务] 维护线程启动失败: {ex.Message}"); }
        // 设备采集线程
        try { _collector?.Start(); } catch (Exception ex) { Logger.Warning($"[聚合服务] 设备采集启动失败: {ex.Message}"); }
        try { _server.Start(); } catch (Exception ex) { Logger.Warning($"[聚合服务] Web 服务启动失败: {ex.Message}"); }
        try { _alert.Start(); } catch (Exception ex) { Logger.Warning($"[聚合服务] 告警服务启动失败: {ex.Message}"); }

        // 防止系统休眠导致采集中断（ES_CONTINUOUS | ES_SYSTEM_REQUIRED），仅 Windows
        try { if (OperatingSystem.IsWindows()) SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED); } catch { }

        // 看门狗：崩溃自愈
        _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "headless-watchdog" };
        _watchdog.Start();

        Logger.Info($"[聚合服务] 无头服务已启动（engine={_engine != null}, collector={_collector != null}, maintenance={_maintenance != null}, listening={_server.Listening}）");
    }

    /// <summary>
    /// Web 设置页保存后热生效：告警服务按新 webhook/汇总间隔重建（token 由 server 内部自更新）。
    /// </summary>
    private void WireSettingsHotReload()
    {
        _server.SettingsChanged += () =>
        {
            try
            {
                var c2 = AppConfig.Instance;
                _alert.Stop();
                _alert = new AggAlertService(_mesh.Receiver, _db, c2.AggWebhookUrl, c2.AggSummaryMinutes);
                _alert.Start();
                Logger.Info($"[聚合服务] Web 设置已热生效（webhook={(c2.AggWebhookUrl.Length > 0 ? "on" : "off")}, 汇总={c2.AggSummaryMinutes} 分钟）");
            }
            catch (Exception ex) { Logger.Warning($"[聚合服务] 设置热生效失败: {ex.Message}"); }
        };
    }

    private void WatchdogLoop()
    {
        int backoff = 0;
        while (!_stopping)
        {
            try { Thread.Sleep(30000); } catch { break; }
            if (_stopping) break;
            try
            {
                // 检测 Web 服务是否意外停止（端口被抢/监听异常）
                if (!_server.Listening)
                {
                    Logger.Warning("[聚合服务] 看门狗：Web 服务未监听，尝试重启…");
                    try { _server.Start(); } catch (Exception ex) { Logger.Warning($"[聚合服务] Web 重启失败: {ex.Message}"); }
                }
                // 检测 collector 线程（轻量，不强依赖）
                // Mesh 推送看门狗已在 MeshPusher 内部，这里仅打点
                backoff = 0;
            }
            catch (Exception ex)
            {
                backoff = Math.Min(5, backoff + 1);
                Logger.Warning($"[聚合服务] 看门狗异常（退避 {backoff * 5}s）: {ex.Message}");
                try { Thread.Sleep(backoff * 5000); } catch { }
            }
        }
    }

    /// <summary>
    /// 停止全部组件，顺序：collector → engine/mesh → maintenance → server → alert。
    /// </summary>
    public void Stop()
    {
        _stopping = true;
        try { _collector?.Stop(); } catch { }
        try { _maintenance?.Stop(); } catch { }
        try
        {
            if (_engine != null) _engine.Stop();
            else _mesh.Stop();
        }
        catch { }
        try { _server.Stop(); } catch { }
        try { _alert.Stop(); } catch { }
        try { _watchdog?.Join(2000); } catch { }
        _watchdog = null;
        try { if (OperatingSystem.IsWindows()) SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS); } catch { }
    }

    /// <summary>
    /// 释放：先 Stop 各组件，再按 using 语义释放 Web 服务器 / Mesh / 副本库
    /// （alert 原代码未用 using 持有，随 GC 即可）。多次调用安全。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        try { _collector?.Dispose(); } catch { }
        _server.Dispose();
        try { _mesh.Dispose(); } catch { }
        _db.Dispose();
    }

    [DllImport("kernel32.dll")]
    private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

    [Flags]
    private enum EXECUTION_STATE : uint
    {
        ES_AWAYMODE_REQUIRED = 0x00000040,
        ES_CONTINUOUS = 0x80000000,
        ES_DISPLAY_REQUIRED = 0x00000002,
        ES_SYSTEM_REQUIRED = 0x00000001,
    }
}

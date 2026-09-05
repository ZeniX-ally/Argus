using System.Runtime.InteropServices;

namespace FctAggregator;

public interface IStartable
{
    string Name { get; }

    void Start();

    void Stop();
}

public sealed class HeadlessService : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly string _stationId;
    private readonly AggDatabase _db;
    private readonly Database _localDb;
    private readonly MeshNode _mesh;
    private readonly WebAggServer _server;
    private AggAlertService _alert;
    private DbMaintenance? _maintenance;
    private DeviceInfoCollector? _collector;
    private Engine? _engine;
    private Thread? _watchdog;
    private volatile bool _stopping;
    private bool _disposed;

    public MeshNode Mesh => _mesh;

    public AggDatabase Db => _db;

    public Engine? Engine => _engine;

    public bool Listening => _server.Listening;

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

        if (_cfg.DeviceInfoEnabled)
        {
            _collector = new DeviceInfoCollector(_cfg, _stationId, _cfg.Peers);
            try { _mesh.Pusher.SetLightProvider(() => _collector.GetLightSnapshot()); } catch { }
        }

        WireSettingsHotReload();
    }

    public void Start()
    {
        _stopping = false;
        if (_engine != null)
        {
            try { _engine.Start(); } catch (Exception ex) { Logger.Warning($"[聚合服务] Engine 启动异常: {ex.Message}"); }
        }
        else
        {
            try { _mesh.Start(); } catch (Exception ex) { Logger.Warning($"[聚合服务] Mesh 启动异常: {ex.Message}"); }
        }
        try { _maintenance = DbMaintenance.StartFor(_cfg, _db); } catch (Exception ex) { Logger.Warning($"[聚合服务] 维护线程启动失败: {ex.Message}"); }
        try { _collector?.Start(); } catch (Exception ex) { Logger.Warning($"[聚合服务] 设备采集启动失败: {ex.Message}"); }
        try { _server.Start(); } catch (Exception ex) { Logger.Warning($"[聚合服务] Web 服务启动失败: {ex.Message}"); }
        try { _alert.Start(); } catch (Exception ex) { Logger.Warning($"[聚合服务] 告警服务启动失败: {ex.Message}"); }

        try { if (OperatingSystem.IsWindows()) SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED); } catch { }

        _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "headless-watchdog" };
        _watchdog.Start();

        Logger.Info($"[聚合服务] 无头服务已启动（engine={_engine != null}, collector={_collector != null}, maintenance={_maintenance != null}, listening={_server.Listening}）");
    }

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
                if (!_server.Listening)
                {
                    Logger.Warning("[聚合服务] 看门狗：Web 服务未监听，尝试重启…");
                    try { _server.Start(); } catch (Exception ex) { Logger.Warning($"[聚合服务] Web 重启失败: {ex.Message}"); }
                }
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

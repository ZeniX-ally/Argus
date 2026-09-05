namespace FctAggregator;

public sealed class MeshNode : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly Database _localDb;
    private readonly AggDatabase _aggDb;
    private readonly string _machine;
    private readonly string[] _peers;
    private DbMaintenance? _maintenance;

    public MeshPusher Pusher { get; }
    public MeshReceiver Receiver { get; }
    public MeshGossiper Gossiper { get; }
    public TodoSync TodoSync { get; }

    public MeshNode(AppConfig cfg, string stationId, Database localDb, AggDatabase aggDb, IEnumerable<string> peers)
    {
        _cfg = cfg;
        _localDb = localDb;
        _aggDb = aggDb;
        _machine = string.IsNullOrEmpty(stationId) ? "UNKNOWN" : stationId;
        _peers = peers
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Where(p => !IsSelfUrl(p, cfg.MeshPort))
            .ToArray();

        Pusher = new MeshPusher(cfg, _machine, _localDb, _peers);
        Receiver = new MeshReceiver(_aggDb, heartbeatTimeoutSec: 90, localMachine: _machine);
        Receiver.SetPeerUrls(_peers);
        Gossiper = new MeshGossiper(cfg, _aggDb, _peers);
        TodoSync = new TodoSync(_localDb, Pusher, _machine);
    }

    private static bool IsSelfUrl(string url, int port)
    {
        try
        {
            var u = new Uri(url);
            var host = u.Host.ToLowerInvariant();
            if (u.Port != port) return false;
            if (host == "localhost" || host == "127.0.0.1" || host == "::1") return true;
            foreach (var addr in LocalIPv4Addresses())
                if (addr == host) return true;
            return false;
        }
        catch { return false; }
    }

    private static List<string> LocalIPv4Addresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var addr in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
                if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && !System.Net.IPAddress.IsLoopback(addr))
                    list.Add(addr.ToString());
        }
        catch { }
        return list;
    }

    public void Start()
    {
        _aggDb.Open();
        Pusher.Init();
        TodoSync.Start();
        Gossiper.Start();
        _maintenance = DbMaintenance.StartFor(_cfg, _aggDb);
        Logger.Info($"[Mesh节点] 已启动: machine={_machine}, peers={_peers.Length}" +
                    (_peers.Length == 0 ? "（单节点模式）" : $"（{string.Join(", ", _peers)}）"));
    }

    public void Stop()
    {
        _maintenance?.Stop();
        Gossiper.Stop();
        TodoSync.Stop();
        Pusher.Stop();
        Logger.Info("[Mesh节点] 已停止");
    }

    public AggDatabase AggDb => _aggDb;
    public string LocalMachine => _machine;
    public Database LocalDb => _localDb;
    public string[] PeerUrls => _peers.ToArray();

    public PeerLink[] PeerLinks => Pusher.GetLinks();

    public List<PeerStatusDto> PeerStatuses => Receiver.GetPeerStatuses();

    public List<TodoSyncRow> GetTodoSyncStates() => _localDb.GetTodoSyncStates();

    public void Dispose() => Stop();
}

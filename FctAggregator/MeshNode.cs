namespace FctAggregator;

/// <summary>
/// P2P 统一节点（去中心化架构的核心编排对象）。
///
/// 每台机台 = 一个 MeshNode，内部组合三个职责（改造自原 AgentPusher 单向推送 + AggWatcher 中心接收）：
/// - <see cref="MeshPusher"/>：向所有邻居广播 FAIL/心跳（断线补偿队列 + 看门狗）；
/// - <see cref="MeshReceiver"/>：HttpListener 经 WebAggServer 分发的入站处理（幂等入库到本地副本库）；
/// - <see cref="MeshGossiper"/>：定期与邻居对账，缺口增量补推（全量副本 + 断线补推）。
///
/// 节点对"peer 列表"是全互联视角：每台机配置相同的静态邻居集合（含或不包含自己均可，
/// Pusher 会自动跳过自身 URL）。单节点（无 peer）时退化为主机本地全功能模式（仍可看板）。
/// </summary>
public sealed class MeshNode : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly Database _localDb;       // 本机本地库（FAIL 数据源，seq 来源）
    private readonly AggDatabase _aggDb;       // 本机副本库（全产线 FAIL 摘要）
    private readonly string _machine;
    private readonly string[] _peers;
    private DbMaintenance? _maintenance;

    public MeshPusher Pusher { get; }
    public MeshReceiver Receiver { get; }
    public MeshGossiper Gossiper { get; }
    /// <summary>全局待办池（跨机同步）。供 WebAggServer 的 /api/mesh/event 路由转发。</summary>
    public TodoSync TodoSync { get; }

    public MeshNode(AppConfig cfg, string stationId, Database localDb, AggDatabase aggDb, IEnumerable<string> peers)
    {
        _cfg = cfg;
        _localDb = localDb;
        _aggDb = aggDb;
        _machine = string.IsNullOrEmpty(stationId) ? "UNKNOWN" : stationId;
        // 去掉自身 URL（若配置里写了自己），避免自环推送
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
        // 粗略判断：URL 含 localhost/127.0.0.1 且端口匹配，视为自身（去中心化下避免自环）
        try
        {
            var u = new Uri(url);
            var host = u.Host.ToLowerInvariant();
            if (u.Port != port) return false;
            if (host == "localhost" || host == "127.0.0.1" || host == "::1") return true;
            // L-6：本机局域网 IP 也算自身（peers 里误写自己的 LAN IP 时不再自环推送）
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

    private static string LocalHostHint()
    {
        try { return System.Net.Dns.GetHostName(); } catch { return "localhost"; }
    }

    /// <summary>启动：先后台推送（订阅本地库入库事件）+ 全局待办池同步 + 启动对账 + 副本库每日维护；
    /// Receiver 由 WebAggServer 在统一端口注册路由。</summary>
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

    /// <summary>副本库（聚合库，对外暴露供 HeadlessService 复用单写者 WAL，避免双实例锁分离）。</summary>
    public AggDatabase AggDb => _aggDb;
    /// <summary>本机机台名（P4 查询结果的 Machine 字段来源）。</summary>
    public string LocalMachine => _machine;
    /// <summary>本机本地库（P4 源机检索直接读 test_records 全量含 PASS）。</summary>
    public Database LocalDb => _localDb;
    /// <summary>静态 peer URL 列表（P4 扇出目标）。</summary>
    public string[] PeerUrls => _peers.ToArray();

    /// <summary>邻居链路状态快照（供状态灯/调试面板）。</summary>
    public PeerLink[] PeerLinks => Pusher.GetLinks();

    /// <summary>邻居机台视图（含自身），供看板 /api/mesh/peers。</summary>
    public List<PeerStatusDto> PeerStatuses => Receiver.GetPeerStatuses();

    /// <summary>跨机待办同步状态（todo_sync_state），供看板 /api/todos 展示全局待办池。</summary>
    public List<TodoSyncRow> GetTodoSyncStates() => _localDb.GetTodoSyncStates();

    public void Dispose() => Stop();
}

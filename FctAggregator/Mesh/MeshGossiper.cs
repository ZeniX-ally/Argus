using System.Text;
using System.Text.Json;

namespace FctAggregator;

/// <summary>
/// 对账器（P2P 去中心化核心组件之一）。
///
/// 职责：与原 AggWatcher 的"兜底全量重扫"同思路，但在去中心化下改为**节点间按需对账**——
/// 定期（默认 30s）向每个 peer 拉取"各机台已同步到的最大 seq 摘要"（/api/mesh/summary），
/// 与本地副本库比对，发现缺口（本机缺某机台的某段 seq）则拉取增量补推（/api/mesh/fetch）。
///
/// 这样即使实时广播因网络抖动丢事件、或新机台上线/老机台离线恢复，也能在下一个对账周期内
/// 自动补齐全量副本，无需中心端、无需共享目录扫描。
///
/// 性能：摘要仅含 (machine, maxSeq) 小数据；增量拉取按缺口批量，O(peers×machines) 适配 ≤10 节点。
/// </summary>
public sealed class MeshGossiper
{
    private const int DefaultIntervalSec = 30;
    private const int StableIntervalSec = 60;
    private const int TightIntervalSec = 10;
    private readonly string[] _peers;
    private readonly AggDatabase _db;
    private readonly AppConfig _cfg;
    private readonly int _intervalSec;
    private volatile int _currentIntervalSec;
    private volatile string _adaptiveReason = "init";
    private readonly Dictionary<string, bool> _peerReachable = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _reachLock = new();
    private long _gossipCount;
    private long _lastGapCount;
    private string _lastGossipAt = "";
    private Thread? _thread;
    private volatile bool _stopping;

    public MeshGossiper(AppConfig cfg, AggDatabase db, IEnumerable<string> peers, int intervalSec = DefaultIntervalSec)
    {
        _cfg = cfg;
        _db = db;
        _peers = peers.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToArray();
        _intervalSec = Math.Max(5, intervalSec);
        _currentIntervalSec = _intervalSec;
    }

    /// <summary>v3.17.0 Lite-Infra 自适应间隔可观测：当前间隔秒（在线稳定60 / 缺口或刚恢复10）。</summary>
    public int CurrentIntervalSec => _currentIntervalSec;
    /// <summary>自适应原因：init / stable / gap / recovered。</summary>
    public string AdaptiveReason => _adaptiveReason;
    /// <summary>已执行对账轮次。</summary>
    public long GossipCount => Interlocked.Read(ref _gossipCount);
    /// <summary>上次对账的缺口数。</summary>
    public long LastGapCount => Interlocked.Read(ref _lastGapCount);
    /// <summary>上次对账时间。</summary>
    public string LastGossipAt => _lastGossipAt;

    public void Start()
    {
        if (_peers.Length == 0) return;
        if (_thread != null) return;
        _stopping = false;
        _currentIntervalSec = _intervalSec;
        _thread = new Thread(Loop) { IsBackground = true, Name = "mesh-gossiper" };
        _thread.Start();
        Logger.Info($"[Mesh对账] 已启动: peers={_peers.Length}, 周期={_intervalSec}s (自适应 stable={StableIntervalSec}s / tight={TightIntervalSec}s 可观测)");
    }

    public void Stop()
    {
        _stopping = true;
        try { _thread?.Join(3000); } catch { }
        _thread = null;
    }

    private void Loop()
    {
        while (!_stopping)
        {
            try { GossipOnce(); }
            catch (Exception ex) { Logger.Warning($"[Mesh对账] 周期异常: {ex.Message}"); }
            // v3.17.0 Lite-Infra：自适应间隔（stable 60 / gap或刚恢复 10）
            int cur = _currentIntervalSec;
            for (int i = 0; i < cur && !_stopping; i++)
                try { Thread.Sleep(1000); } catch { }
        }
    }

    /// <summary>向每个 peer 拉摘要并对账补推缺口。
    /// v3.17.0 Lite-Infra 自适应：在线稳定→60s、检测缺口/刚恢复→10s。</summary>
    private void GossipOnce()
    {
        // 本机已知各机台在本副本库的 maxSeq（对账基准）
        var localMax = _db.MaxSeqPerMachine();
        bool anyGap = false;
        bool anyRecovered = false;
        int gapCount = 0;
        foreach (var peer in _peers)
        {
            if (_stopping) return;
            bool wasReachable;
            lock (_reachLock) wasReachable = _peerReachable.TryGetValue(peer, out var pr) && pr;
            var summary = PullSummary(peer);
            bool nowReachable = summary != null;
            lock (_reachLock) _peerReachable[peer] = nowReachable;
            if (!wasReachable && nowReachable) anyRecovered = true;
            if (summary == null) continue;
            foreach (var (machine, peerMax) in summary)
            {
                long local = localMax.TryGetValue(machine, out var v) ? v : 0;
                if (peerMax <= local) continue;     // peer 没比我多，跳过
                anyGap = true;
                gapCount++;
                // 向 peer 拉取 (local, peerMax] 区间的该机台 FAIL 增量
                PullAndStore(peer, machine, local, peerMax);
            }
        }
        Interlocked.Increment(ref _gossipCount);
        Interlocked.Exchange(ref _lastGapCount, gapCount);
        _lastGossipAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        // 自适应间隔决策：缺口或刚恢复→10s 紧绷；否则检查是否全部在线稳定→60s；否则保持当前或回落 30
        bool allReachable;
        lock (_reachLock) allReachable = _peers.Length > 0 && _peers.All(p => _peerReachable.TryGetValue(p, out var r) && r);
        if (anyGap || anyRecovered)
        {
            _currentIntervalSec = TightIntervalSec;
            _adaptiveReason = anyRecovered ? "recovered" : "gap";
            Logger.Info($"[Mesh对账] 自适应收紧至 {TightIntervalSec}s（{(anyRecovered ? "刚恢复" : "检测缺口")} gap={gapCount}）");
        }
        else if (allReachable && !anyGap)
        {
            _currentIntervalSec = StableIntervalSec;
            _adaptiveReason = "stable";
        }
        else
        {
            // 部分离线但无缺口：保持紧绷 10s（防离线期间缺口漏检）
            if (!allReachable)
            {
                _currentIntervalSec = TightIntervalSec;
                _adaptiveReason = "unstable";
            }
            else
            {
                _currentIntervalSec = StableIntervalSec;
                _adaptiveReason = "stable";
            }
        }
    }

    /// <summary>测试/观测用：强制设置自适应状态（供自检）。</summary>
    internal void SetAdaptiveForTest(int intervalSec, string reason)
    {
        _currentIntervalSec = intervalSec;
        _adaptiveReason = reason;
    }

    private Dictionary<string, long>? PullSummary(string peer)
    {
        try
        {
            var url = peer + (peer.EndsWith("/") ? "" : "/") + "api/mesh/summary";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(_cfg.AggToken)) req.Headers.Add(MeshPusher.TokenHeader, _cfg.AggToken);
            using var resp = MeshPusher.SendStatic(req);
            if (!resp.IsSuccessStatusCode) return null;
            var txt = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(txt);
            var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("machines", out var arr))
                foreach (var e in arr.EnumerateArray())
                {
                    var m = e.GetProperty("machine").GetString() ?? "";
                    var s = e.GetProperty("max_seq").GetInt64();
                    if (m.Length > 0) dict[m] = s;
                }
            return dict;
        }
        catch (Exception ex) { Logger.Warning($"[Mesh对账] 拉摘要失败 {peer}: {ex.Message}"); return null; }
    }

    private void PullAndStore(string peer, string machine, long fromSeq, long toSeq)
    {
        try
        {
            var url = peer + (peer.EndsWith("/") ? "" : "/") +
                      $"api/mesh/fetch?machine={Uri.EscapeDataString(machine)}&from={fromSeq}&to={toSeq}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(_cfg.AggToken)) req.Headers.Add(MeshPusher.TokenHeader, _cfg.AggToken);
            using var resp = MeshPusher.SendStatic(req);
            if (!resp.IsSuccessStatusCode) return;
            var txt = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(txt);
            if (!doc.RootElement.TryGetProperty("events", out var arr)) return;
            // v3.5.3：先解析后整批单事务入库（InsertBatch）。对账补推常一次拉几百上千条缺口，
            // 逐条提交会把 WAL fsync 放大成吞吐瓶颈；幂等键保证重拉不重复。
            int parsed = 0;
            var rows = new List<AggFailRow>();
            foreach (var e in arr.EnumerateArray())
            {
                var json = e.GetString();
                if (string.IsNullOrEmpty(json)) continue;
                try
                {
                    var row = ParseRemoteFail(json);
                    if (string.IsNullOrEmpty(row.Machine)) continue;
                    rows.Add(row);
                    parsed++;
                }
                catch (Exception ex) { Logger.Warning($"[Mesh对账] 增量解析失败，跳过该条: {ex.Message}"); }
            }
            int stored = parsed > 0 ? _db.InsertBatch(rows) : 0;
            if (stored > 0) Logger.Info($"[Mesh对账] 从 {peer} 补齐 {machine} 缺口 {fromSeq}→{toSeq}：入库 {stored}/{parsed} 条");
        }
        catch (Exception ex) { Logger.Warning($"[Mesh对账] 拉增量失败 {peer}/{machine}: {ex.Message}"); }
    }

    private AggFailRow ParseRemoteFail(string json)
    {
        // 与 MeshReceiver.ParseFailJson 同格式
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        long seq = root.TryGetProperty("seq", out var ps) && ps.TryGetInt64(out var s) ? s : 0;
        var row = new AggFailRow { Seq = seq, Machine = root.TryGetProperty("machine", out var pm) ? (pm.GetString() ?? "") : "" };
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            row.StationId = Str(data, "station_id"); row.Model = Str(data, "model");
            row.Category = Str(data, "category"); row.TestDate = Str(data, "test_date");
            row.Sn = Str(data, "sn"); row.Result = Str(data, "result");
            row.XmlPath = Str(data, "xml_path"); row.FailReason = Str(data, "fail_reason");
            row.Tester = Str(data, "tester"); row.PanelStatus = Str(data, "panel_status");
            row.FixtureId = Str(data, "fixture_id");   // v3.22.0 规格01：老版本对端不带该字段 → 空串（前缀回退）
            row.BatchTimestamp = Str(data, "batch_timestamp");
            row.HasFailItems = Num(data, "has_fail_items") != 0;
            row.FileSize = Num(data, "file_size");
        }
        row.IngestTs = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return row;
    }

    private static string Str(JsonElement d, string n) =>
        d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static long Num(JsonElement d, string n) =>
        d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}

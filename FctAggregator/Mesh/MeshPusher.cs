using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FctAggregator;

/// <summary>
/// 单 peer 的链路状态（改造自原 AgentPusher 的 AggLinkSnapshot；去中心化后每台机对多个 peer 各有独立链路）。
/// </summary>
public sealed class PeerLink
{
    public string Url = "";
    public AggLinkState State = AggLinkState.Unknown;
    public DateTime LastSuccessAt;
    public int ConsecutiveFailures;
    public int Backlog;            // 该 peer 待推积压
    public DateTime DisconnectedSince;
    public bool DisconnectAlertSent;
    // v3.17.0 Lite-Infra：per-peer 成功/失败计数（/api/metrics 失败率可观测）
    public long Sent;
    public long Failed;
    /// <summary>per-peer 失败率（0-1，sent+failed==0 时 0）。</summary>
    public double FailureRate => (Sent + Failed) == 0 ? 0 : (double)Failed / (Sent + Failed);
}

/// <summary>
/// 出站推送器（P2P 去中心化核心组件之一）。
///
/// 复用原 AgentPusher 已被产线验证的稳健性范式：
/// - 事件只入内存队列、后台线程发送（绝不阻塞采集主链路）；
/// - 发送失败进断线补偿队列、看门狗退避重启（杜绝静默断连）；
/// - (machine, seq) 幂等：每个 peer 各自维护 maxSeq + 重启续推扫描。
///
/// 与原 AgentPusher 的区别：
/// - 不再有 smb 文件通道，纯 http POST 到各 peer 的 /api/mesh/fail 与 /api/mesh/heartbeat；
/// - 一对多：向配置的所有 peer 广播同一条 FAIL/心跳（单 peer 失败不影响其他 peer）；
/// - seq 仍用本地 test_records 自增 id（本机即数据源头，machine=本机名）。
/// </summary>
public sealed class MeshPusher
{
    public const int MaxQueue = 5000;
    private const int CatchUpBatch = 5000;
    private const int DisconnectAfterFails = 3;
    private const int OverflowNotifyMinMs = 30 * 60 * 1000;
    public const string TokenHeader = "X-Agg-Token";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>静态 HTTP 发送（供 MeshReceiver 按需拉取 XML 等跨机请求复用同一连接池）。</summary>
    internal static HttpResponseMessage SendStatic(HttpRequestMessage req) => _http.Send(req);
    internal static Task<HttpResponseMessage> SendStaticAsync(HttpRequestMessage req, CancellationToken ct = default) => _http.SendAsync(req, ct);

    private readonly AppConfig _cfg;
    private readonly Database _db;
    private readonly string _machine;
    private readonly string[] _peers;
    private readonly string _dataDir;
    private readonly int _retryMs;
    private readonly int _heartbeatMs;

    private readonly object _lock = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly List<AggQueueItem> _pending = new();
    private readonly HashSet<long> _pendingSeqs = new();

    private Thread? _worker;
    private volatile bool _stopping;
    private long _maxSeq;
    private long _maxSeenSeq;
    private bool _queueDirty;
    private bool _stateDirty;
    private long _lastQueuePersist;
    private long _lastHeartbeat;
    private long _droppedCount;
    // v3.10.0 P1-1 观测：发送成功 / 失败 累计（TryPostOne 每 peer 每次尝试计 1）
    private long _sentCount;
    private long _failCount;

    // 每 peer 独立链路状态（索引与 _peers 对齐）
    private readonly PeerLink[] _links;

    public string StatePath => Path.Combine(_dataDir, "mesh_state.json");
    public string QueuePath => Path.Combine(_dataDir, "mesh_queue.json");
    public bool Active { get; private set; }
    public string Machine => _machine;

    /// <summary>队列积压数（/api/metrics 观测）。</summary>
    public int QueuedCount { get { lock (_lock) return _pending.Count; } }
    /// <summary>累计丢弃事件数（队列超上限时，FAIL 仍在本地库可续推）。</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);
    /// <summary>发送成功尝试次数（每 peer 计 1）。</summary>
    public long SentCount => Interlocked.Read(ref _sentCount);
    /// <summary>发送失败尝试次数（每 peer 计 1）。</summary>
    public long FailCount => Interlocked.Read(ref _failCount);

    public MeshPusher(AppConfig cfg, string stationId, Database db,
                      IEnumerable<string> peers, string? dataDir = null,
                      int retrySec = 5, int heartbeatSec = 30)
    {
        _cfg = cfg;
        _db = db;
        _machine = string.IsNullOrEmpty(stationId) ? "UNKNOWN" : stationId;
        _peers = peers.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToArray();
        _dataDir = dataDir ?? Path.Combine(AppConfig.BaseDir, "data");
        _retryMs = Math.Max(1, retrySec) * 1000;
        _heartbeatMs = Math.Max(1, heartbeatSec) * 1000;
        _links = _peers.Select(p => new PeerLink { Url = p }).ToArray();
    }

    public void Init()
    {
        if (Active) return;
        if (_peers.Length == 0)
        {
            Logger.Info("[Mesh推送] 未配置 peers，本机以单节点模式运行（不向任何邻居推送）");
            return;
        }
        Active = true;
        try { Directory.CreateDirectory(_dataDir); } catch { }
        LoadState();
        LoadQueue();
        _db.RecordsInserted += OnRecordsInserted;
        _worker = new Thread(WatchdogLoop) { IsBackground = true, Name = "mesh-pusher" };
        _worker.Start();
        Logger.Info($"[Mesh推送] 已启用: machine={_machine}, peers={string.Join(", ", _peers)}，续推起点 max_seq={_maxSeq}");
    }

    public void Stop()
    {
        if (!Active) return;
        _stopping = true;
        _signal.Set();
        try { _worker?.Join(3000); } catch { }
        _db.RecordsInserted -= OnRecordsInserted;
        lock (_lock)
        {
            _queueDirty = true; _stateDirty = true;
            PersistQueueLocked();
            PersistStateLocked();
        }
        Active = false;
    }

    private void OnRecordsInserted(List<(TestRecord Rec, long Id)> rows)
    {
        try
        {
            foreach (var (rec, id) in rows)
                if (rec.Result == "FAIL") Enqueue("fail", id, BuildFailJson(rec, id));
        }
        catch (Exception ex) { Logger.Warning($"[Mesh推送] 插入事件处理异常: {ex.Message}"); }
    }

    internal void EnqueueFail(TestRecord rec, long id) => Enqueue("fail", id, BuildFailJson(rec, id));

    /// <summary>通用事件广播（非 FAIL，如全局待办变更）：立即向所有 peer 的 /api/mesh/event 广播。
    /// 与 FAIL 不同，todo 等事件不走断线补偿队列（最终由 Gossiper/重放保证一致），仅做 best-effort 实时同步。</summary>
    public void BroadcastEvent(string type, string json)
    {
        if (!Active || _stopping) return;
        for (int i = 0; i < _peers.Length; i++)
        {
            var url = _peers[i] + (_peers[i].EndsWith("/") ? "" : "/") + "api/mesh/event";
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                if (!string.IsNullOrEmpty(_cfg.AggToken)) req.Headers.Add(TokenHeader, _cfg.AggToken);
                _ = _http.SendAsync(req);   // best-effort，不阻塞
            }
            catch (Exception ex) { Logger.Warning($"[Mesh推送] 广播事件 {type} 失败 {_peers[i]}: {ex.Message}"); }
        }
    }

    private void Enqueue(string type, long seq, string json, bool persist = true)
    {
        if (!Active || _stopping || seq <= 0 || json.Length == 0) return;
        lock (_lock)
        {
            if (seq <= _maxSeq || !_pendingSeqs.Add(seq)) return;
            _pending.Add(new AggQueueItem { Seq = seq, Type = type, Json = json });
            if (seq > _maxSeenSeq) _maxSeenSeq = seq;
            if (_pending.Count > MaxQueue)
            {
                var dropped = _pending[0];
                _pending.RemoveAt(0);
                _pendingSeqs.Remove(dropped.Seq);
                Interlocked.Increment(ref _droppedCount);
                Logger.Warning($"[Mesh推送] 队列超上限({MaxQueue})，丢弃最老事件 seq={dropped.Seq}（累计丢弃 {_droppedCount} 条，FAIL 仍在本地库）");
            }
            _queueDirty = true;
            if (persist) MaybePersistQueueLocked();
        }
        _signal.Set();
    }

    private void WorkerLoop()
    {
        try
        {
            RunCatchUpScan();
            while (!_stopping)
            {
                List<AggQueueItem>? work;
                lock (_lock) { work = _pending.Count > 0 ? _pending.ToList() : null; }
                if (work == null) { CheckHeartbeat(); _signal.WaitOne(1000); continue; }

                work.Sort((a, b) => a.Seq.CompareTo(b.Seq));
                var sent = new List<AggQueueItem>(work.Count);
                foreach (var item in work)
                {
                    if (_stopping) break;
                    // 向所有 peer 广播：任一成功即认为该事件已传播（去中心化最终一致）
                    if (!TryBroadcast(item)) continue;
                    sent.Add(item);
                }

                lock (_lock)
                {
                    foreach (var s in sent)
                    {
                        _pending.RemoveAll(x => x.Seq == s.Seq);
                        _pendingSeqs.Remove(s.Seq);
                        if (s.Seq > _maxSeq) { _maxSeq = s.Seq; _stateDirty = true; }
                    }
                    if (sent.Count > 0) _queueDirty = true;
                    if (_queueDirty || _stateDirty) { PersistQueueLocked(); PersistStateLocked(); }
                }
                CheckHeartbeat();
                _signal.WaitOne(sent.Count == work.Count ? 1000 : _retryMs);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Mesh推送] worker 异常（看门狗将自动重启）: {ex.Message}");
            throw;
        }
    }

    private void WatchdogLoop()
    {
        var backoff = 0;
        while (!_stopping)
        {
            try { WorkerLoop(); return; }
            catch (Exception ex)
            {
                backoff = Math.Min(10, backoff + 1);
                Logger.Error($"[Mesh推送] worker 异常退出（连续 {backoff} 次），{backoff * 5}s 后自动重启: {ex.Message}");
                try { Thread.Sleep(backoff * 5000); } catch { }
            }
        }
    }

    private void RunCatchUpScan()
    {
        try
        {
            int total = 0;
            while (!_stopping)
            {
                List<(TestRecord Rec, long Id)> rows;
                lock (_lock) { rows = _db.FetchFailRecordsAfter(_maxSeq, CatchUpBatch); }
                if (rows.Count == 0) break;
                foreach (var (rec, id) in rows) Enqueue("fail", id, BuildFailJson(rec, id));
                total += rows.Count;
                if (rows.Count < CatchUpBatch) break;
            }
            if (total > 0) Logger.Info($"[Mesh推送] 续推扫描入队 {total} 条未推送 FAIL");
        }
        catch (Exception ex) { Logger.Warning($"[Mesh推送] 续推扫描失败: {ex.Message}"); }
    }

    /// <summary>向所有 peer 广播一条事件：全部失败才返回 false（保留在队列重试）。
    /// v3.17.0 Lite-Infra：单 worker 串行 → 按 peer 并发（Parallel.ForEach），共享队列，单 peer 慢不拖累全链路；
    /// 仍共享队列与 (machine,seq) 幂等：任一 peer 成功即认为已传播（去中心化最终一致，失败保留重试）。</summary>
    private bool TryBroadcast(AggQueueItem item)
    {
        if (_peers.Length == 0) return false;
        if (_peers.Length == 1)
        {
            var url = _peers[0] + (_peers[0].EndsWith("/") ? "" : "/") + (item.Type == "fail" ? "api/mesh/fail" : "api/mesh/heartbeat");
            return TryPostOne(url, item.Json, 0);
        }
        int anyOk = 0;
        // 按 peer 并发：每个 peer 独立任务，共享 HttpClient（线程安全），单 peer 慢不阻塞其他
        Parallel.ForEach(System.Linq.Enumerable.Range(0, _peers.Length), i =>
        {
            var url = _peers[i] + (_peers[i].EndsWith("/") ? "" : "/") + (item.Type == "fail" ? "api/mesh/fail" : "api/mesh/heartbeat");
            if (TryPostOne(url, item.Json, i)) Interlocked.Exchange(ref anyOk, 1);
        });
        return anyOk != 0;
    }

    private bool TryPostOne(string url, string json, int linkIdx)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            if (!string.IsNullOrEmpty(_cfg.AggToken)) req.Headers.Add(TokenHeader, _cfg.AggToken);
            var resp = _http.Send(req);
            if (resp.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref _sentCount);   // v3.10.0 P1-1 观测
                Interlocked.Increment(ref _links[linkIdx].Sent); // v3.17.0 per-peer
                RecordSuccess(linkIdx);
                return true;
            }
            Interlocked.Increment(ref _failCount);       // v3.10.0 P1-1 观测
            Interlocked.Increment(ref _links[linkIdx].Failed); // v3.17.0 per-peer
            RecordFailure(linkIdx, $"HTTP {(int)resp.StatusCode}");
            return false;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failCount);       // v3.10.0 P1-1 观测
            Interlocked.Increment(ref _links[linkIdx].Failed); // v3.17.0 per-peer
            RecordFailure(linkIdx, ex.Message);
            return false;
        }
    }

    private void CheckHeartbeat()
    {
        if (Environment.TickCount64 - _lastHeartbeat < _heartbeatMs) return;
        _lastHeartbeat = Environment.TickCount64;
        var json = BuildHeartbeatJson();
        if (_peers.Length == 1)
        {
            var url = _peers[0] + (_peers[0].EndsWith("/") ? "" : "/") + "api/mesh/heartbeat";
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                if (!string.IsNullOrEmpty(_cfg.AggToken)) req.Headers.Add(TokenHeader, _cfg.AggToken);
                var ok = _http.Send(req).IsSuccessStatusCode;
                if (ok) { Interlocked.Increment(ref _links[0].Sent); Interlocked.Increment(ref _sentCount); RecordSuccess(0); }
                else { Interlocked.Increment(ref _links[0].Failed); Interlocked.Increment(ref _failCount); RecordFailure(0, "心跳非 2xx"); }
            }
            catch (Exception ex) { Interlocked.Increment(ref _links[0].Failed); Interlocked.Increment(ref _failCount); RecordFailure(0, ex.Message); }
            return;
        }
        // v3.17.0：心跳也按 peer 并发，单 peer 慢不拖累其他
        Parallel.ForEach(System.Linq.Enumerable.Range(0, _peers.Length), i =>
        {
            var url = _peers[i] + (_peers[i].EndsWith("/") ? "" : "/") + "api/mesh/heartbeat";
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                if (!string.IsNullOrEmpty(_cfg.AggToken)) req.Headers.Add(TokenHeader, _cfg.AggToken);
                var ok = _http.Send(req).IsSuccessStatusCode;
                if (ok) { Interlocked.Increment(ref _links[i].Sent); Interlocked.Increment(ref _sentCount); RecordSuccess(i); }
                else { Interlocked.Increment(ref _links[i].Failed); Interlocked.Increment(ref _failCount); RecordFailure(i, "心跳非 2xx"); }
            }
            catch (Exception ex) { Interlocked.Increment(ref _links[i].Failed); Interlocked.Increment(ref _failCount); RecordFailure(i, ex.Message); }
        });
    }

    private void RecordSuccess(int i)
    {
        var l = _links[i];
        lock (_lock)
        {
            l.ConsecutiveFailures = 0;
            l.LastSuccessAt = DateTime.Now;
            if (l.State == AggLinkState.Disconnected)
            {
                Logger.Info($"[Mesh推送] 与 {l.Url} 链路已恢复");
                l.DisconnectedSince = default;
                l.DisconnectAlertSent = false;
                l.State = AggLinkState.Connected;
            }
            else if (l.State != AggLinkState.Connected) l.State = AggLinkState.Connected;
        }
    }

    private void RecordFailure(int i, string why)
    {
        var l = _links[i];
        lock (_lock)
        {
            l.ConsecutiveFailures++;
            if (l.State != AggLinkState.Disconnected && l.ConsecutiveFailures >= DisconnectAfterFails)
            {
                l.DisconnectedSince = DateTime.Now;
                Logger.Warning($"[Mesh推送] 与 {l.Url} 链路断连（连续失败 {l.ConsecutiveFailures} 次）：{why}");
                l.State = AggLinkState.Disconnected;
            }
        }
    }

    public PeerLink[] GetLinks() => _links.ToArray();

    /// <summary>v3.17.0 Lite-Infra：per-peer 失败率快照（/api/metrics 可观测）。</summary>
    public object[] GetPerPeerMetrics()
    {
        var arr = new object[_links.Length];
        for (int i = 0; i < _links.Length; i++)
        {
            var l = _links[i];
            var sent = Interlocked.Read(ref l.Sent);
            var failed = Interlocked.Read(ref l.Failed);
            var total = sent + failed;
            var rate = total == 0 ? 0.0 : Math.Round((double)failed / total, 4);
            arr[i] = new { url = l.Url, state = l.State.ToString(), consecutive_failures = l.ConsecutiveFailures, sent, failed, failure_rate = rate, last_success_at = l.LastSuccessAt == default ? "" : l.LastSuccessAt.ToString("yyyy-MM-dd HH:mm:ss") };
        }
        return arr;
    }

    // P6 设备监控：轻量心跳 cpu/mem 注入（由 DeviceInfoCollector 注册回调提供，向后兼容缺字段视为未知）
    private Func<(double cpu, int memUsed, int memTotal)>? _lightProvider;
    public void SetLightProvider(Func<(double cpu, int memUsed, int memTotal)> provider) => _lightProvider = provider;

    private string BuildHeartbeatJson()
    {
        long lastSeq; lock (_lock) { lastSeq = _maxSeenSeq; }
        // v3.8.0 总纲 P1 D1：心跳携带今日统计（机台本地 test_records 聚合，不推全量 PASS）。
        // 聚合端按 (machine, today) 幂等 upsert 到 yld_daily —— 良率数据底座，聚合零膨胀。
        // test_date 有索引，30s 一次的 COUNT 开销可忽略；FetchDailyStats 用目录日期口径。
        var today = DateTime.Now.ToString("yyyyMMdd");
        var st = _db.FetchDailyStats(_machine, today);
        var dict = new Dictionary<string, object>
        {
            ["machine"] = _machine,
            ["type"] = "heartbeat",
            ["ts"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["last_seq"] = lastSeq,
            ["queued"] = QueuedCount,
            ["today"] = today,
            ["today_total"] = st.Pass + st.Fail + st.Interrupted,
            ["today_pass"] = st.Pass,
            ["today_fail"] = st.Fail,
            ["today_interrupted"] = st.Interrupted,
            ["today_products"] = st.TodayProductCount,
        };
        // P6：心跳捎带轻量系统状态（cpu_usage/mem），对端缺字段视为未知，向后兼容
        try
        {
            var prov = _lightProvider;
            if (prov != null)
            {
                var (cpu, used, total) = prov();
                dict["system"] = new Dictionary<string, object> { ["cpu_usage"] = cpu, ["mem_used_mb"] = used, ["mem_total_mb"] = total };
            }
        }
        catch { }
        return JsonSerializer.Serialize(dict);
    }

    private string BuildFailJson(TestRecord rec, long id)
    {
        var data = new Dictionary<string, object?>
        {
            ["id"] = id, ["station_id"] = rec.StationId, ["model"] = rec.Model,
            ["category"] = rec.Category, ["test_date"] = rec.TestDate, ["sn"] = rec.Sn,
            ["result"] = rec.Result, ["xml_path"] = rec.XmlPath, ["fail_reason"] = rec.FailReason,
            ["tester"] = rec.Tester, ["panel_status"] = rec.PanelStatus,
            ["batch_timestamp"] = rec.BatchTimestamp,
            ["has_fail_items"] = rec.HasFailItems ? 1 : 0, ["file_size"] = rec.FileSize,
            // v3.22.0 规格01 fixture 归因：治具 ID 随推（对端老版本忽略未知字段，协议向后兼容；空则不带省流量）
            ["xml_available"] = 1,
        };
        if (!string.IsNullOrEmpty(rec.FixtureId)) data["fixture_id"] = rec.FixtureId;
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["machine"] = _machine, ["type"] = "fail", ["seq"] = id,
            ["ts"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), ["data"] = data,
        });
    }

    public long LastSeq { get { lock (_lock) return _maxSeq; } }

    // ===== 持久化（复用 AgentPusher 的原子写范式）=====
    private void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
            if (doc.RootElement.TryGetProperty("max_seq", out var v) && v.TryGetInt64(out var s))
            { _maxSeq = s; _maxSeenSeq = s; }
        }
        catch (Exception ex) { Logger.Warning($"[Mesh推送] 读 mesh_state.json 失败: {ex.Message}"); }
    }

    private void LoadQueue()
    {
        try
        {
            if (!File.Exists(QueuePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(QueuePath));
            if (!doc.RootElement.TryGetProperty("events", out var evs)) return;
            foreach (var e in evs.EnumerateArray())
            {
                long seq = 0; string type = "", json = "";
                if (e.TryGetProperty("seq", out var sv) && sv.TryGetInt64(out seq) &&
                    e.TryGetProperty("type", out var tv)) type = tv.GetString() ?? "";
                if (e.TryGetProperty("json", out var jv)) json = jv.GetString() ?? "";
                Enqueue(type, seq, json, persist: false);
            }
        }
        catch (Exception ex) { Logger.Warning($"[Mesh推送] 读 mesh_queue.json 失败: {ex.Message}"); }
    }

    private void MaybePersistQueueLocked()
    {
        if (Environment.TickCount64 - _lastQueuePersist < 5000) return;
        PersistQueueLocked();
    }

    private void PersistQueueLocked()
    {
        _lastQueuePersist = Environment.TickCount64;
        if (!_queueDirty) return;
        _queueDirty = false;
        try
        {
            var tmp = QueuePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new AggQueueFile { Events = _pending }), Encoding.UTF8);
            File.Move(tmp, QueuePath, overwrite: true);
        }
        catch (Exception ex) { _queueDirty = true; Logger.Warning($"[Mesh推送] 写 mesh_queue.json 失败: {ex.Message}"); }
    }

    private void PersistStateLocked()
    {
        if (!_stateDirty) return;
        _stateDirty = false;
        try
        {
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Dictionary<string, long> { ["max_seq"] = _maxSeq }), Encoding.UTF8);
            File.Move(tmp, StatePath, overwrite: true);
        }
        catch (Exception ex) { _stateDirty = true; Logger.Warning($"[Mesh推送] 写 mesh_state.json 失败: {ex.Message}"); }
    }

    private sealed class AggQueueFile
    {
        [System.Text.Json.Serialization.JsonPropertyName("events")]
        public List<AggQueueItem> Events { get; set; } = new();
    }
}

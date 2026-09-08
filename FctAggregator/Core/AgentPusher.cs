using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FctAggregator;

public class AggQueueItem
{
    [JsonPropertyName("seq")]
    public long Seq { get; set; }
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
    [JsonPropertyName("json")]
    public string Json { get; set; } = "";
}

public enum AggLinkState { Unknown, Connected, Degraded, Disconnected }

public sealed class AggLinkSnapshot
{
    public AggLinkState State;
    public DateTime LastSuccessAt;
    public int ConsecutiveFailures;
    public int Backlog;
    public long DroppedCount;
    public string Target = "";
    public DateTime DisconnectedSince;
    public bool DisconnectAlertSent;
}

[Obsolete("v3.5.0 起由 MeshNode/MeshPusher(P2P) 取代；保留仅供兼容与自检")]
public class AgentPusher
{
    public const int MaxQueue = 5000;
    private const int CatchUpBatch = 5000;
    private const int DisconnectAfterFails = 3;
    private const int OverflowNotifyMinMs = 30 * 60 * 1000;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    public const string TokenHeader = "X-Agg-Token";

    private readonly AppConfig _cfg;
    private readonly Database _db;
    private readonly string _machine;
    private readonly string _dataDir;
    private readonly int _retryMs;
    private readonly int _heartbeatMs;
    private readonly string[] _httpUrls;
    private int _urlIndex;

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

    private readonly object _linkLock = new();
    private AggLinkState _linkState = AggLinkState.Unknown;
    private DateTime _lastSuccessAt;
    private int _consecutiveFailures;
    private long _droppedCount;
    private DateTime _disconnectedSince;
    private bool _disconnectAlertSent;
    private long _lastOverflowNotify;

    public event Action<AggLinkState, AggLinkState>? LinkStateChanged;

    public string ShareRoot { get; set; } = "";

    public bool Active { get; private set; }
    public string Machine => _machine;
    public string StatePath => Path.Combine(_dataDir, "agg_state.json");
    public string QueuePath => Path.Combine(_dataDir, "agg_queue.json");

    public bool HttpMode => _cfg.AggTransport == "http";

    private string Chan => HttpMode ? "[聚合推送-http]" : "[聚合推送-smb]";

    public long LastSeq { get { lock (_lock) return _maxSeq; } }

    public int QueuedCount { get { lock (_lock) return _pending.Count; } }

    public AgentPusher(AppConfig cfg, string stationId, Database db,
                       string? dataDir = null, int retrySec = 5, int heartbeatSec = 30)
    {
        _cfg = cfg;
        _db = db;
        _machine = string.IsNullOrEmpty(stationId) ? "UNKNOWN" : stationId;
        _dataDir = dataDir ?? Path.Combine(AppConfig.BaseDir, "data");
        _retryMs = Math.Max(1, retrySec) * 1000;
        _heartbeatMs = Math.Max(1, heartbeatSec) * 1000;
        _httpUrls = SplitUrls(cfg.AggHttpUrl);
    }

    private static string[] SplitUrls(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
        return s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public void Init()
    {
        if (Active) return;
        if (!_cfg.AggEnabled ||
            (HttpMode ? string.IsNullOrEmpty(_cfg.AggHttpUrl) : string.IsNullOrEmpty(_cfg.AggShareRoot)))
        {
            Logger.Info($"[聚合推送] 未启用(agg_enabled={_cfg.AggEnabled}, transport={_cfg.AggTransport}, " +
                        $"share_root='{_cfg.AggShareRoot}', http_url='{_cfg.AggHttpUrl}')，本机不推送");
            return;
        }
        if (string.IsNullOrEmpty(_cfg.StationId)) Logger.Warning($"{Chan} station_id 未配置，machine 使用 UNKNOWN");
        ShareRoot = _cfg.AggShareRoot;
        Active = true;
        try { Directory.CreateDirectory(_dataDir); } catch { }
        LoadState();
        LoadQueue();
        _db.RecordsInserted += OnRecordsInserted;
        _worker = new Thread(WatchdogLoop) { IsBackground = true, Name = "agg-pusher" };
        _worker.Start();
        var target = HttpMode
            ? (_httpUrls.Length > 1 ? $"urls=[{string.Join(", ", _httpUrls)}]（主备 {_httpUrls.Length} 个）" : $"url={_cfg.AggHttpUrl}")
            : $"share_root={ShareRoot}";
        Logger.Info($"{Chan} 已启用: machine={_machine}, {target}, 续推起点 max_seq={_maxSeq}");
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
            _queueDirty = true;
            _stateDirty = true;
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
        catch (Exception ex) { Logger.Warning($"[聚合推送] 插入事件处理异常: {ex.Message}"); }
    }

    internal void EnqueueFail(TestRecord rec, long id) => Enqueue("fail", id, BuildFailJson(rec, id));

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
                Logger.Warning($"[聚合推送] 队列超上限({MaxQueue})，丢弃最老事件 seq={dropped.Seq}（累计丢弃 {_droppedCount} 条，FAIL 仍在本地库）");
                if (Environment.TickCount64 - _lastOverflowNotify >= OverflowNotifyMinMs)
                {
                    _lastOverflowNotify = Environment.TickCount64;
                    _ = NotifyLinkAsync("overflow", $"推送队列超过上限({MaxQueue})，已丢弃最老事件（累计丢弃 {_droppedCount} 条）——聚合端可能长时间不可达，请检查链路");
                }
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

                if (work == null)
                {
                    CheckHeartbeat();
                    _signal.WaitOne(1000);
                    continue;
                }

                work.Sort((a, b) => a.Seq.CompareTo(b.Seq));
                bool allOk = true;
                int pushed = 0;
                var sent = new List<AggQueueItem>(work.Count);
                foreach (var item in work)
                {
                    if (_stopping) { allOk = false; break; }
                    if (!TrySend(item)) { allOk = false; RecordFailure(); continue; }
                    sent.Add(item);
                    RecordSuccess();
                    if (++pushed % 200 == 0) CheckHeartbeat();
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
                    if (_queueDirty || _stateDirty)
                    {
                        PersistQueueLocked();
                        PersistStateLocked();
                    }
                }
                CheckHeartbeat();
                CheckDisconnectAlert();
                _signal.WaitOne(allOk ? 1000 : _retryMs);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[聚合推送] worker 异常（看门狗将自动重启）: {ex.Message}");
            throw;
        }
    }

    private void WatchdogLoop()
    {
        var backoff = 0;
        while (!_stopping)
        {
            try
            {
                WorkerLoop();
                backoff = 0;
                return;
            }
            catch (Exception ex)
            {
                backoff = Math.Min(10, backoff + 1);
                Logger.Error($"{Chan} worker 异常退出（连续 {backoff} 次），{backoff * 5}s 后自动重启: {ex.Message}");
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
            if (total > 0) Logger.Info($"[聚合推送] 续推扫描入队 {total} 条未推送 FAIL");
        }
        catch (Exception ex) { Logger.Warning($"[聚合推送] 续推扫描失败: {ex.Message}"); }
    }

    private bool TrySend(AggQueueItem item) => HttpMode ? TryPost(item) : TryPushFile(item);

    private bool TryPost(AggQueueItem item)
    {
        if (_httpUrls.Length == 0) return false;
        var errs = new List<string>();
        for (int i = 0; i < _httpUrls.Length; i++)
        {
            var idx = (_urlIndex + i) % _httpUrls.Length;
            if (TryPostOne(item, _httpUrls[idx], out var err))
            {
                if (_urlIndex != idx) Logger.Info($"{Chan} 聚合端切换: {_httpUrls[idx]}（主备 {_httpUrls.Length} 个）");
                _urlIndex = idx;
                return true;
            }
            errs.Add($"{_httpUrls[idx]} → {err}");
        }
        Logger.Warning($"{Chan} POST 失败 seq={item.Seq}: {string.Join(" | ", errs)}");
        return false;
    }

    private bool TryPostOne(AggQueueItem item, string url, out string err)
    {
        err = "";
        try
        {
            using var content = new StringContent(item.Json, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            if (!string.IsNullOrEmpty(_cfg.AggToken)) req.Headers.Add(TokenHeader, _cfg.AggToken);
            var resp = _http.Send(req);
            if (resp.IsSuccessStatusCode) return true;
            err = $"HTTP {(int)resp.StatusCode}";
            return false;
        }
        catch (Exception ex)
        {
            err = ex.Message;
            return false;
        }
    }

    private bool TryPushFile(AggQueueItem item)
    {
        try
        {
            var dir = Path.Combine(ShareRoot, _machine);
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, $"{item.Type}-{item.Seq}.json");
            var tmp = target + ".tmp";
            File.WriteAllText(tmp, item.Json + "\n", Encoding.UTF8);
            File.Move(tmp, target, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning($"{Chan} 写共享失败 seq={item.Seq}: {ex.Message}");
            return false;
        }
    }

    private void CheckHeartbeat()
    {
        if (Environment.TickCount64 - _lastHeartbeat < _heartbeatMs) return;
        _lastHeartbeat = Environment.TickCount64;
        var json = BuildHeartbeatJson();
        try
        {
            if (HttpMode)
            {
                if (_httpUrls.Length == 0) { RecordFailure(); return; }
                for (int i = 0; i < _httpUrls.Length; i++)
                {
                    var idx = (_urlIndex + i) % _httpUrls.Length;
                    if (TryPostHeartbeat(json, _httpUrls[idx]))
                    {
                        if (_urlIndex != idx) Logger.Info($"{Chan} 心跳切换聚合端: {_httpUrls[idx]}");
                        _urlIndex = idx;
                        RecordSuccess();
                        return;
                    }
                }
                Logger.Warning($"{Chan} 心跳 POST 失败: 全部 {_httpUrls.Length} 个聚合端均不可达");
                RecordFailure();
            }
            else
            {
                var dir = Path.Combine(ShareRoot, _machine);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "heartbeat.json"), json + "\n", Encoding.UTF8);
                RecordSuccess();
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"{Chan} 心跳发送失败: {ex.Message}");
            RecordFailure();
        }
    }

    private bool TryPostHeartbeat(string json, string url)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            if (!string.IsNullOrEmpty(_cfg.AggToken)) req.Headers.Add(TokenHeader, _cfg.AggToken);
            var resp = _http.Send(req);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public AggLinkSnapshot GetLinkSnapshot()
    {
        lock (_linkLock)
        {
            return new AggLinkSnapshot
            {
                State = _linkState,
                LastSuccessAt = _lastSuccessAt,
                ConsecutiveFailures = _consecutiveFailures,
                Backlog = QueuedCount,
                DroppedCount = Interlocked.Read(ref _droppedCount),
                Target = HttpMode
                    ? (_httpUrls.Length > 0 ? _httpUrls[Math.Min(_urlIndex, _httpUrls.Length - 1)] : _cfg.AggHttpUrl)
                    : ShareRoot,
                DisconnectedSince = _disconnectedSince,
                DisconnectAlertSent = _disconnectAlertSent,
            };
        }
    }

    private void RecordSuccess()
    {
        lock (_linkLock)
        {
            var backlog = QueuedCount;
            _consecutiveFailures = 0;
            _lastSuccessAt = DateTime.Now;
            if (_linkState == AggLinkState.Disconnected)
            {
                var wasAlerted = _disconnectAlertSent;
                var since = _disconnectedSince;
                _disconnectedSince = default;
                _disconnectAlertSent = false;
                var downMin = since == default ? 0 : (DateTime.Now - since).TotalMinutes;
                Logger.Info($"{Chan} 链路已恢复（曾断开 {(downMin >= 1 ? $"{downMin:F1} 分钟" : "不足 1 分钟")}，剩余积压 {backlog} 条继续推送）");
                if (wasAlerted && since != default)
                    _ = NotifyLinkAsync("recovered", $"聚合端已恢复连接（断开 {downMin:F0} 分钟，积压 {backlog} 条继续补推，已全部进入重试队列）");
                SetLinkStateLocked(backlog > 0 ? AggLinkState.Degraded : AggLinkState.Connected);
            }
            else if (_linkState == AggLinkState.Unknown || _linkState != (backlog > 0 ? AggLinkState.Degraded : AggLinkState.Connected))
            {
                SetLinkStateLocked(backlog > 0 ? AggLinkState.Degraded : AggLinkState.Connected);
            }
        }
    }

    private void RecordFailure()
    {
        lock (_linkLock)
        {
            _consecutiveFailures++;
            if (_linkState != AggLinkState.Disconnected && _consecutiveFailures >= DisconnectAfterFails)
            {
                _disconnectedSince = DateTime.Now;
                _disconnectAlertSent = false;
                Logger.Warning($"{Chan} 链路断连（连续失败 {_consecutiveFailures} 次，队列积压 {QueuedCount} 条，断线补偿继续重试）");
                SetLinkStateLocked(AggLinkState.Disconnected);
            }
        }
    }

    private void SetLinkStateLocked(AggLinkState next)
    {
        if (_linkState == next) return;
        var old = _linkState;
        _linkState = next;
        try { LinkStateChanged?.Invoke(old, next); }
        catch (Exception ex) { Logger.Warning($"{Chan} LinkStateChanged 回调异常: {ex.Message}"); }
    }

    private void CheckDisconnectAlert()
    {
        int fails;
        lock (_linkLock)
        {
            if (_linkState != AggLinkState.Disconnected || _disconnectAlertSent) return;
            if ((DateTime.Now - _disconnectedSince).TotalMinutes < _cfg.AggFailAlertMinutes) return;
            _disconnectAlertSent = true;
            fails = _consecutiveFailures;
        }
        _ = NotifyLinkAsync("disconnected",
            $"聚合端 {(_httpUrls.Length > 0 ? _httpUrls[Math.Min(_urlIndex, _httpUrls.Length - 1)] : ShareRoot)} 不可达已超过 {_cfg.AggFailAlertMinutes} 分钟（连续失败 {fails} 次，队列积压 {QueuedCount} 条）");
    }

    private async Task NotifyLinkAsync(string kind, string detail)
    {
        try
        {
            var wh = _cfg.WebhookUrl;
            if (string.IsNullOrWhiteSpace(wh)) return;
            await FeishuNotifier.SendAggLinkAlert(wh, _machine, kind, detail);
        }
        catch (Exception ex) { Logger.Warning($"{Chan} 链路告警推送失败: {ex.Message}"); }
    }

    private string BuildHeartbeatJson()
    {
        long lastSeq;
        lock (_lock) { lastSeq = _maxSeenSeq; }
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["machine"] = _machine,
            ["type"] = "heartbeat",
            ["ts"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["last_seq"] = lastSeq,
            ["queued"] = QueuedCount,
        });
    }

    private string BuildFailJson(TestRecord rec, long id)
    {
        var data = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["station_id"] = rec.StationId,
            ["model"] = rec.Model,
            ["category"] = rec.Category,
            ["test_date"] = rec.TestDate,
            ["sn"] = rec.Sn,
            ["result"] = rec.Result,
            ["xml_path"] = rec.XmlPath,
            ["fail_reason"] = rec.FailReason,
            ["tester"] = rec.Tester,
            ["panel_status"] = rec.PanelStatus,
            ["batch_timestamp"] = rec.BatchTimestamp,
            ["has_fail_items"] = rec.HasFailItems ? 1 : 0,
            ["file_size"] = rec.FileSize,
            ["xml_content"] = ReadXmlContent(rec.XmlPath),
        };
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["machine"] = _machine,
            ["type"] = "fail",
            ["seq"] = id,
            ["ts"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["data"] = data,
        });
    }

    private static string? ReadXmlContent(string? xmlPath)
    {
        if (string.IsNullOrEmpty(xmlPath)) return null;
        try
        {
            var fi = new FileInfo(xmlPath);
            if (!fi.Exists || fi.Length <= 0 || fi.Length > 512 * 1024) return null;
            return File.ReadAllText(xmlPath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[聚合推送] 读取 XML 内容失败（跳过跨机台拉取）: {ex.Message}");
            return null;
        }
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
            if (doc.RootElement.TryGetProperty("max_seq", out var v) && v.TryGetInt64(out var s))
            {
                _maxSeq = s;
                _maxSeenSeq = s;
            }
        }
        catch (Exception ex) { Logger.Warning($"[聚合推送] 读 agg_state.json 失败: {ex.Message}"); }
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
                long seq = 0;
                string type = "", json = "";
                if (e.TryGetProperty("seq", out var sv) && sv.TryGetInt64(out seq) &&
                    e.TryGetProperty("type", out var tv)) type = tv.GetString() ?? "";
                if (e.TryGetProperty("json", out var jv)) json = jv.GetString() ?? "";
                Enqueue(type, seq, json, persist: false);
            }
        }
        catch (Exception ex) { Logger.Warning($"[聚合推送] 读 agg_queue.json 失败: {ex.Message}"); }
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
        catch (Exception ex)
        {
            _queueDirty = true;
            Logger.Warning($"[聚合推送] 写 agg_queue.json 失败: {ex.Message}");
        }
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
        catch (Exception ex)
        {
            _stateDirty = true;
            Logger.Warning($"[聚合推送] 写 agg_state.json 失败: {ex.Message}");
        }
    }

    private sealed class AggQueueFile
    {
        [JsonPropertyName("events")]
        public List<AggQueueItem> Events { get; set; } = new();
    }
}

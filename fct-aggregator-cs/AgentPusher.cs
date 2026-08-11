using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FctAggregator;

/// <summary>聚合推送队列项：seq = test_records 自增 id（天然单调幂等），json = 事件原始 JSON 行。</summary>
public class AggQueueItem
{
    [JsonPropertyName("seq")]
    public long Seq { get; set; }
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
    [JsonPropertyName("json")]
    public string Json { get; set; } = "";
}

/// <summary>
/// 机台聚合推送器（Phase 1：FAIL 事件文件 + 心跳；status 事件待 Phase 2）。
///
/// 事件驱动：Database 成功写入 test_records 后经 <see cref="Database.RecordsInserted"/> 通知，
/// 把 FAIL 记录写成 {share}/{machine}/fail-{seq}.json 单行 JSON 文件；30 秒覆盖写心跳文件。
/// seq 用 test_records 自增 id，聚合端按 (machine, seq) 去重。
///
/// 断线补偿：写共享失败的事件进内存队列，后台线程每 5 秒重试；队列同时落盘本地
/// data/agg_queue.json（防重启丢队，最多 5000 条，超限丢最老并记日志）；已推送最大 seq
/// 持久化到 data/agg_state.json，重启后读状态续推 + 读队列补推 + 数据库兜底扫描。
/// 推送失败只记日志，绝不影响主程序（FAIL 已本地入库，聚合是增强不是依赖）。
/// </summary>
public class AgentPusher
{
    private const int MaxQueue = 5000;
    private const int CatchUpBatch = 5000;

    private readonly AppConfig _cfg;
    private readonly Database _db;
    private readonly string _machine;
    private readonly string _dataDir;
    private readonly int _retryMs;
    private readonly int _heartbeatMs;

    private readonly object _lock = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly List<AggQueueItem> _pending = new();
    private readonly HashSet<long> _pendingSeqs = new();

    private Thread? _worker;
    private volatile bool _stopping;
    private long _maxSeq;        // 已成功推送文件的最大 seq（持久化）
    private long _maxSeenSeq;    // 见过的最新 seq（含队列中，心跳 last_seq 用）
    private bool _queueDirty;
    private bool _stateDirty;
    private long _lastQueuePersist;
    private long _lastHeartbeat;

    /// <summary>共享目录根（可运行时改，worker 每轮都重读；测试/热改配置用）。</summary>
    public string ShareRoot { get; set; } = "";

    public bool Active { get; private set; }
    public string Machine => _machine;
    public string StatePath => Path.Combine(_dataDir, "agg_state.json");
    public string QueuePath => Path.Combine(_dataDir, "agg_queue.json");

    /// <summary>已成功推送的最大 seq。</summary>
    public long LastSeq { get { lock (_lock) return _maxSeq; } }

    /// <summary>内存队列中待推送条数（断线补偿）。</summary>
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
    }

    /// <summary>启动时调用：读配置，enabled 且 share_root 非空才激活；激活后订阅入库事件并起后台线程。</summary>
    public void Init()
    {
        if (Active) return;
        if (!_cfg.AggEnabled || string.IsNullOrEmpty(_cfg.AggShareRoot))
        {
            Logger.Info($"[聚合推送] 未启用(agg_enabled={_cfg.AggEnabled}, share_root='{_cfg.AggShareRoot}')，本机不推送");
            return;
        }
        if (string.IsNullOrEmpty(_cfg.StationId)) Logger.Warning("[聚合推送] station_id 未配置，machine 使用 UNKNOWN");
        ShareRoot = _cfg.AggShareRoot;
        Active = true;
        try { Directory.CreateDirectory(_dataDir); } catch { }
        LoadState();
        LoadQueue();
        _db.RecordsInserted += OnRecordsInserted;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "agg-pusher" };
        _worker.Start();
        Logger.Info($"[聚合推送] 已启用: machine={_machine}, share_root={ShareRoot}, 续推起点 max_seq={_maxSeq}");
    }

    /// <summary>停止：退订事件、停后台线程、落盘队列与状态。</summary>
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

    // ==================== 事件入口 ====================

    private void OnRecordsInserted(List<(TestRecord Rec, long Id)> rows)
    {
        try
        {
            foreach (var (rec, id) in rows)
                if (rec.Result == "FAIL") Enqueue("fail", id, BuildFailJson(rec, id));
        }
        catch (Exception ex) { Logger.Warning($"[聚合推送] 插入事件处理异常: {ex.Message}"); }
    }

    /// <summary>测试/调试用：直接入队一条 fail 事件（与入库事件同路径）。</summary>
    internal void EnqueueFail(TestRecord rec, long id) => Enqueue("fail", id, BuildFailJson(rec, id));

    private void Enqueue(string type, long seq, string json, bool persist = true)
    {
        if (!Active || _stopping || seq <= 0 || json.Length == 0) return;
        lock (_lock)
        {
            // 已推过或已在队中 -> 幂等去重
            if (seq <= _maxSeq || !_pendingSeqs.Add(seq)) return;
            _pending.Add(new AggQueueItem { Seq = seq, Type = type, Json = json });
            if (seq > _maxSeenSeq) _maxSeenSeq = seq;
            if (_pending.Count > MaxQueue)
            {
                var dropped = _pending[0];
                _pending.RemoveAt(0);
                _pendingSeqs.Remove(dropped.Seq);
                Logger.Warning($"[聚合推送] 队列超上限({MaxQueue})，丢弃最老事件 seq={dropped.Seq}（该 seq 不会补推，FAIL 仍在本地库）");
            }
            _queueDirty = true;
            if (persist) MaybePersistQueueLocked();
        }
        _signal.Set();
    }

    // ==================== 后台线程 ====================

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
                bool ok = true;
                int pushed = 0;
                foreach (var item in work)
                {
                    if (_stopping) { ok = false; break; }
                    if (!TryPushFile(item)) { ok = false; break; }
                    if (++pushed % 200 == 0) CheckHeartbeat();
                }

                lock (_lock)
                {
                    if (ok)
                    {
                        _pending.Clear();
                        _pendingSeqs.Clear();
                        long m = _maxSeq;
                        foreach (var w in work) if (w.Seq > m) m = w.Seq;
                        if (m > _maxSeq) { _maxSeq = m; _stateDirty = true; }
                        _queueDirty = true;
                    }
                    // 失败：整队保留，下次整队重推（文件按 (machine,seq) 幂等，重复写无副作用）
                    if (_queueDirty || _stateDirty)
                    {
                        PersistQueueLocked();
                        PersistStateLocked();
                    }
                }
                CheckHeartbeat();
                _signal.WaitOne(ok ? 1000 : _retryMs);
            }
        }
        catch (Exception ex) { Logger.Error($"[聚合推送] worker 异常: {ex.Message}"); }
    }

    /// <summary>重启续推兜底：数据库里 id &gt; 已推最大 seq 的 FAIL 全部入队（覆盖崩溃窗口/丢队场景）。</summary>
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

    // ==================== 推送 / 心跳 ====================

    private bool TryPushFile(AggQueueItem item)
    {
        try
        {
            var dir = Path.Combine(ShareRoot, _machine);
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, $"{item.Type}-{item.Seq}.json");
            // 先写 .tmp 再改名：聚合端只可能看到完整文件，不会读到写一半的
            var tmp = target + ".tmp";
            File.WriteAllText(tmp, item.Json + "\n", Encoding.UTF8);
            File.Move(tmp, target, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[聚合推送] 写共享失败 seq={item.Seq}: {ex.Message}");
            return false;
        }
    }

    private void CheckHeartbeat()
    {
        if (Environment.TickCount64 - _lastHeartbeat < _heartbeatMs) return;
        _lastHeartbeat = Environment.TickCount64;
        try
        {
            var dir = Path.Combine(ShareRoot, _machine);
            Directory.CreateDirectory(dir);
            long lastSeq;
            lock (_lock) { lastSeq = _maxSeenSeq; }
            var json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["machine"] = _machine,
                ["type"] = "heartbeat",
                ["ts"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["last_seq"] = lastSeq,
                ["queued"] = QueuedCount,
            });
            File.WriteAllText(Path.Combine(dir, "heartbeat.json"), json + "\n", Encoding.UTF8);
        }
        catch (Exception ex) { Logger.Warning($"[聚合推送] 心跳写共享失败: {ex.Message}"); }
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

    // ==================== 持久化 ====================
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

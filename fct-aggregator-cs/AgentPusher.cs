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

/// <summary>机台端聚合链路状态：Unknown=未启用/未知，Connected=正常，
/// Degraded=发送成功但有积压（链路通、聚合端消费慢），Disconnected=连续失败断连。</summary>
public enum AggLinkState { Unknown, Connected, Degraded, Disconnected }

/// <summary>链路状态快照（UI 状态灯/告警读取用，线程安全）。</summary>
public sealed class AggLinkSnapshot
{
    public AggLinkState State;
    /// <summary>最近一次成功发送（fail 或心跳）时间。</summary>
    public DateTime LastSuccessAt;
    /// <summary>连续失败次数（fail/心跳任一通道）。</summary>
    public int ConsecutiveFailures;
    /// <summary>待推队列积压条数。</summary>
    public int Backlog;
    /// <summary>队列溢出累计丢弃条数。</summary>
    public long DroppedCount;
    /// <summary>当前生效目标（http=当前聚合端 URL，smb=共享目录根）。</summary>
    public string Target = "";
    /// <summary>本次断连起点（State==Disconnected 时有效）。</summary>
    public DateTime DisconnectedSince;
    /// <summary>本次断连是否已发过飞书告警（决定恢复时是否发恢复通知）。</summary>
    public bool DisconnectAlertSent;
}

/// <summary>
/// 机台聚合推送器（Phase 2 增强：B2 双通道方案，smb 文件 / http 直推）。
///
/// 事件驱动：Database 成功写入 test_records 后经 <see cref="Database.RecordsInserted"/> 通知，
/// 把 FAIL 事件经 smb 通道写成 {share}/{machine}/fail-{seq}.json 单行 JSON 文件，
/// 或经 http 通道 POST 到聚合端 HTTP 服务（agg_transport 选择，默认 smb，POST 正文与文件内容完全相同）。
/// 30 秒一次心跳（文件版 / http 版 JSON 同构）。seq 用 test_records 自增 id，聚合端按 (machine, seq) 去重。
///
/// 断线补偿对两种通道统一生效：发送失败的事件进内存队列，后台线程每 5 秒重试；队列同时落盘本地
/// data/agg_queue.json（防重启丢队，最多 5000 条，超限丢最老并记日志）；已推送最大 seq
/// 持久化到 data/agg_state.json，重启后读状态续推 + 读队列补推 + 数据库兜底扫描。
/// 只有最终发送动作不同（写文件 vs POST），队列/重试/续推扫描均不区分通道。
/// 推送失败只记日志，绝不影响主程序（FAIL 已本地入库，聚合是增强不是依赖）。
/// </summary>
public class AgentPusher
{
    /// <summary>内存队列上限（超限丢最老；公开供自检断言）。</summary>
    public const int MaxQueue = 5000;
    private const int CatchUpBatch = 5000;
    /// <summary>连续失败多少次（约 3×5s=15s）判为断连。</summary>
    private const int DisconnectAfterFails = 3;
    /// <summary>队列溢出告警节流（毫秒）：同种告警 30 分钟最多推一次，防刷屏。</summary>
    private const int OverflowNotifyMinMs = 30 * 60 * 1000;

    /// <summary>http 直推复用单例；Timeout 5 秒，聚合端必须本地网内，失败走断线补偿队列重试。</summary>
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    /// <summary>共享 token 的请求头名（聚合端 HttpIngest/WebAggServer 按同名校验）。</summary>
    public const string TokenHeader = "X-Agg-Token";

    private readonly AppConfig _cfg;
    private readonly Database _db;
    private readonly string _machine;
    private readonly string _dataDir;
    private readonly int _retryMs;
    private readonly int _heartbeatMs;
    /// <summary>主备聚合端地址（agg_http_url 逗号分隔多个；发送时从上次成功索引开始轮询，第一个成功即成功）。</summary>
    private readonly string[] _httpUrls;
    private int _urlIndex;

    private readonly object _lock = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly List<AggQueueItem> _pending = new();
    private readonly HashSet<long> _pendingSeqs = new();

    private Thread? _worker;
    private volatile bool _stopping;
    private long _maxSeq;        // 已成功推送的最大 seq（持久化，smb/http 通用）
    private long _maxSeenSeq;    // 见过的最新 seq（含队列中，心跳 last_seq 用）
    private bool _queueDirty;
    private bool _stateDirty;
    private long _lastQueuePersist;
    private long _lastHeartbeat;

    // ===== 链路状态（worker 线程写，UI 读走 GetLinkSnapshot 锁）=====
    private readonly object _linkLock = new();
    private AggLinkState _linkState = AggLinkState.Unknown;
    private DateTime _lastSuccessAt;
    private int _consecutiveFailures;
    private long _droppedCount;
    private DateTime _disconnectedSince;
    private bool _disconnectAlertSent;
    private long _lastOverflowNotify;

    /// <summary>链路状态翻转事件（worker 线程触发；订阅方自行 BeginInvoke 进 UI 线程）。</summary>
    public event Action<AggLinkState, AggLinkState>? LinkStateChanged;

    /// <summary>共享目录根（可运行时改，worker 每轮都重读；测试/热改配置用）。</summary>
    public string ShareRoot { get; set; } = "";

    public bool Active { get; private set; }
    public string Machine => _machine;
    public string StatePath => Path.Combine(_dataDir, "agg_state.json");
    public string QueuePath => Path.Combine(_dataDir, "agg_queue.json");

    /// <summary>当前通道：http=POST 直推，否则 smb 写共享文件。</summary>
    public bool HttpMode => _cfg.AggTransport == "http";

    /// <summary>日志通道前缀：区分 [聚合推送-http] / [聚合推送-smb]。</summary>
    private string Chan => HttpMode ? "[聚合推送-http]" : "[聚合推送-smb]";

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
        // 主备聚合端：agg_http_url 逗号分隔（如 "http://10.0.0.1:8080/,http://10.0.0.2:8080/"）
        _httpUrls = SplitUrls(cfg.AggHttpUrl);
    }

    /// <summary>逗号分隔拆聚合端地址列表；空串/全空白返回空数组。</summary>
    private static string[] SplitUrls(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
        return s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>启动时调用：读配置，enabled 且对应通道就绪（smb 要 share_root、http 要 http_url）才激活；激活后订阅入库事件并起后台线程。</summary>
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
        // worker 由看门狗托管：异常退避重启，杜绝静默断连（v3.4.11 连通性加固）
        _worker = new Thread(WatchdogLoop) { IsBackground = true, Name = "agg-pusher" };
        _worker.Start();
        var target = HttpMode
            ? (_httpUrls.Length > 1 ? $"urls=[{string.Join(", ", _httpUrls)}]（主备 {_httpUrls.Length} 个）" : $"url={_cfg.AggHttpUrl}")
            : $"share_root={ShareRoot}";
        Logger.Info($"{Chan} 已启用: machine={_machine}, {target}, 续推起点 max_seq={_maxSeq}");
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
                Interlocked.Increment(ref _droppedCount);
                Logger.Warning($"[聚合推送] 队列超上限({MaxQueue})，丢弃最老事件 seq={dropped.Seq}（累计丢弃 {_droppedCount} 条，FAIL 仍在本地库）");
                // 溢出告警（飞书，30 分钟节流）：聚合链路异常时数据开始丢失，现场必须知道
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
                bool allOk = true;
                int pushed = 0;
                var sent = new List<AggQueueItem>(work.Count);
                foreach (var item in work)
                {
                    if (_stopping) { allOk = false; break; }
                    if (!TrySend(item)) { allOk = false; RecordFailure(); continue; }   // 失败保留在队列，下次只重推未成功的
                    sent.Add(item);
                    RecordSuccess();
                    if (++pushed % 200 == 0) CheckHeartbeat();
                }

                lock (_lock)
                {
                    // 成功项按 seq 精确出队（不再整体 Clear）：部分失败时已成功的立即推进
                    foreach (var s in sent)
                    {
                        _pending.RemoveAll(x => x.Seq == s.Seq);
                        _pendingSeqs.Remove(s.Seq);
                        if (s.Seq > _maxSeq) { _maxSeq = s.Seq; _stateDirty = true; }
                    }
                    // 本轮有成功 → 队列有变化，落盘
                    if (sent.Count > 0) _queueDirty = true;
                    if (_queueDirty || _stateDirty)
                    {
                        PersistQueueLocked();
                        PersistStateLocked();
                    }
                }
                CheckHeartbeat();
                CheckDisconnectAlert();   // 断连持续超阈值才推飞书（防抖动误报）
                _signal.WaitOne(allOk ? 1000 : _retryMs);
            }
        }
        catch (Exception ex)
        {
            // 上抛给看门狗（WatchdogLoop）退避重启 —— 线程异常退出 = 静默断连，绝不能发生
            Logger.Error($"[聚合推送] worker 异常（看门狗将自动重启）: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 看门狗：worker 异常退出后按 5s×次数 退避自动重启（上限 50s），
    /// 杜绝「worker 线程死掉、队列不推、心跳也停」的永久静默断连。
    /// 正常退出（_stopping）直接结束。
    /// </summary>
    private void WatchdogLoop()
    {
        var backoff = 0;
        while (!_stopping)
        {
            try
            {
                WorkerLoop();
                backoff = 0;
                return;   // 正常退出（Stop 置位后 WorkerLoop 循环自然结束）
            }
            catch (Exception ex)
            {
                backoff = Math.Min(10, backoff + 1);
                Logger.Error($"{Chan} worker 异常退出（连续 {backoff} 次），{backoff * 5}s 后自动重启: {ex.Message}");
                try { Thread.Sleep(backoff * 5000); } catch { }
            }
        }
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

    /// <summary>按通道分发发送动作：smb=写文件，http=POST 结构化 JSON（队列/重试/续推不区分通道）。</summary>
    private bool TrySend(AggQueueItem item) => HttpMode ? TryPost(item) : TryPushFile(item);

    /// <summary>http 直推：把与文件内容完全相同的 JSON 行（无尾换行）POST 到聚合端，返回是否成功。
    /// 主备支持：从上次成功索引开始按顺序尝试全部地址，第一个成功即成功（备援切换零配置）。
    /// 聚合端配置了 agg_token 时带共享 token 头（403 视为失败进重试队列，与断线同路径）。</summary>
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
            // 先写 .tmp 再改名：聚合端只可能看到完整文件，不会读到写一半的
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

    // ==================== 链路状态机（v3.4.11 连通性加固） ====================

    /// <summary>链路状态快照（UI 状态灯 / 自检读取；线程安全）。</summary>
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

    /// <summary>一次发送成功（fail 或心跳）：清零连续失败；断连恢复时触发恢复通知。</summary>
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

    /// <summary>一次发送失败：累计连续失败，达到阈值翻转断连（触发状态事件，UI 状态灯变红）。</summary>
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

    /// <summary>
    /// 断连飞书告警（延时确认）：断连持续 ≥ agg_fail_alert_minutes 分钟才推，
    /// 避免网络抖动瞬时断连就告警刷屏；同一断连周期只推一次（恢复通知由 RecordSuccess 补发）。
    /// </summary>
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

    /// <summary>链路告警走机台自己的飞书 webhook（webhook 未配置自动跳过）；告警失败不影响推送。</summary>
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

    /// <summary>心跳 JSON（文件版 / http 版共用）：machine/type/ts/last_seq/queued。</summary>
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
            ["xml_content"] = ReadXmlContent(rec.XmlPath),   // 跨机台 XML 拉取（≤512KB 才带上，聚合端落盘后 Web 可下载）
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

    /// <summary>读取 XML 报告内容用于上送（跨机台拉取）。不存在/超 512KB/读取失败 → null（不带该字段）。</summary>
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

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FctAggregator;

/// <summary>待处理队列项：完整文件路径 + 已重试次数（IO 失败放回队尾重试用）。</summary>
internal sealed class QueuedFile
{
    public string Path = "";
    public int Retries;
}

/// <summary>一台机台的实时状态缓存（心跳轮询线程 + HTTP 入口共同维护，GetMachines 只读）。</summary>
internal sealed class MachineState
{
    public string Machine = "";
    public string LastHeartbeat = "";
    public DateTime HeartbeatTime = DateTime.MinValue;   // 从未读到有效心跳 = MinValue
    public DateTime LastSeen = DateTime.MinValue;         // 最后一次确认存活（smb 心跳/http 推送），统一在线判定基准
    public bool ViaHttp;                                  // 由 http 通道出现过的机台：不参与目录消失清理
    public long LastSeq;
    public int Queued;
    public bool Online;
}

/// <summary>
/// 聚合入库失败（数据库层异常）。由 AggWatcher.IngestFail 抛出、HttpIngest/WebAggServer
/// 捕获后回 HTTP 5xx —— 机台端收到非 2xx 会把该事件放回断线补偿队列自动重推。
/// 注意：JSON 解析失败 / 缺字段这类「内容错误」不抛本异常（重推也一样坏，回 200 让机台端正常推进）。
/// </summary>
public sealed class AggIngestException : Exception
{
    public string Machine { get; }
    public long Seq { get; }

    public AggIngestException(string machine, long seq, string message) : base(message)
    {
        Machine = machine;
        Seq = seq;
    }
}

/// <summary>机台聚合看板用的机台状态快照：心跳信息来自内存缓存，FAIL 统计现查数据库。</summary>
public class AggMachineStatus
{
    public string Machine = "";
    public bool Online;
    public string LastHeartbeat = "";   // "yyyy-MM-dd HH:mm:ss" 或空串
    public long LastSeq;
    public int Queued;
    public long FailCount;
    public string LastFailAt = "";
    public string FirstSeenAt = "";
}

/// <summary>
/// 聚合端监听器（部署在共享目录宿主机，Phase 2）。
///
/// 三路数据入口：
///   1. 初始全量扫描：Start 时遍历 shareRoot 下各机台目录的 fail-*.json 逐个入库；
///   2. FileSystemWatcher 增量：Created/Renamed 事件只把完整路径塞进并发队列，
///      处理线程（100ms 轮询）负责解析入库 —— 事件回调里绝不做 IO；
///   3. 心跳轮询线程：每 pollSec 扫一遍机台目录读 heartbeat.json，超过
///      heartbeatTimeoutSec 未更新判离线，状态翻转时触发 <see cref="Changed"/>；
///   4. HTTP 入口（B2 双通道）：<see cref="IngestFail"/> / <see cref="IngestHeartbeat"/>
///      接收机台 http 直推，与文件路径共用同一解析/入库/幂等逻辑。
///
/// 在线判定 smb 与 http 两通道统一：MachineState.LastSeen 最后一次确认存活的时间，
/// now - LastSeen 超过心跳超时判离线；http 机台没有共享目录，不参与目录消失清理。
///
/// 机台端写文件是「写 .tmp 再 File.Move 改名」，SMB 共享上对聚合端表现为
/// Renamed 事件（e.Name/e.FullPath 已是新名字），所以 Created + Renamed 都要监听；
/// (machine, seq) 幂等入库保证同一文件重复触发也不产生重复行。
/// </summary>
public class AggWatcher : IDisposable
{
    private static readonly Regex FailFileRegex = new(@"^fail-(\d+)\.json$", RegexOptions.Compiled);
    private const int MaxRetries = 3;        // 单文件最大重试次数（IO 未写完/被占用）
    private const int QueueWaitMs = 100;     // 队列空时的轮询间隔

    private readonly string _shareRoot;
    private readonly AggDatabase _db;
    private readonly int _heartbeatTimeoutSec;
    private readonly int _pollSec;

    private readonly object _lock = new();
    private readonly ConcurrentQueue<QueuedFile> _pending = new();
    private readonly Dictionary<string, MachineState> _machines = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _fsw;
    private Thread? _worker;
    private Thread? _hbThread;
    private volatile bool _stopping;
    private volatile bool _started;
    private long _processedFiles;

    /// <summary>有新 FAIL 入库或心跳状态变化时触发（GUI 订阅后跨线程刷新）。</summary>
    public event Action? Changed;

    /// <summary>机台在线→离线翻转时触发（Phase 3 离线告警用）。参数 = 机台名 + 最后确认存活时间。
    /// 触发点：smb 心跳超时（UpdateHeartbeat）/ HTTP LastSeen 超时（RefreshMachines）。</summary>
    public event Action<string, DateTime>? MachineOffline;

    /// <summary>本次运行新入库文件数（INSERT 真正插入才算，重复忽略不计）。</summary>
    public long ProcessedFiles => Interlocked.Read(ref _processedFiles);

    /// <summary>全库 FAIL 总数。</summary>
    public long TotalFails
    {
        get
        {
            try { return _db.FailCount(""); }
            catch (Exception ex) { Logger.Warning($"[聚合监听] 查询 FAIL 总数失败: {ex.Message}"); return 0; }
        }
    }

    public AggWatcher(string shareRoot, AggDatabase db, int heartbeatTimeoutSec = 90, int pollSec = 10)
    {
        _shareRoot = shareRoot;
        _db = db;
        _heartbeatTimeoutSec = Math.Max(1, heartbeatTimeoutSec);
        _pollSec = Math.Max(1, pollSec);
    }

    /// <summary>启动：机器发现 + 初始全量扫描 + 增量监听 + 心跳监控。
    /// shareRoot 不存在时只记 ERROR 日志不抛异常（GUI 仍能开窗显示错误）。</summary>
    public void Start()
    {
        if (_started) return;
        _stopping = false;
        if (string.IsNullOrEmpty(_shareRoot) || !Directory.Exists(_shareRoot))
        {
            Logger.Error($"[聚合监听] 共享目录不存在，无法启动监听: '{_shareRoot}'");
            return;
        }

        try { _db.Open(); }
        catch (Exception ex) { Logger.Warning($"[聚合监听] 聚合库打开失败（后续插入会再试）: {ex.Message}"); }

        // 初始全量扫描（同步做：机台少数据量小，简单可靠）
        try { InitialScan(); }
        catch (Exception ex) { Logger.Error($"[聚合监听] 初始扫描失败: {ex.Message}"); }

        try
        {
            _fsw = new FileSystemWatcher(_shareRoot)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
            _fsw.Created += OnFsEvent;
            _fsw.Renamed += OnRenamed;
        }
        catch (Exception ex) { Logger.Error($"[聚合监听] 文件监听创建失败（仅保留轮询）: {ex.Message}"); }

        _worker = new Thread(ProcessLoop) { IsBackground = true, Name = "agg-watch-worker" };
        _hbThread = new Thread(HeartbeatLoop) { IsBackground = true, Name = "agg-heartbeat" };
        _worker.Start();
        _hbThread.Start();
        _started = true;
        Logger.Info($"[聚合监听] 已启动: share={_shareRoot}, 心跳超时={_heartbeatTimeoutSec}s, 轮询={_pollSec}s");
    }

    /// <summary>停止：停监听器、置退出标志，Join 两个后台线程（各 3 秒上限）。</summary>
    public void Stop()
    {
        if (!_started) return;
        _stopping = true;
        if (_fsw != null)
        {
            try { _fsw.EnableRaisingEvents = false; _fsw.Dispose(); } catch { }
            _fsw = null;
        }
        try { _worker?.Join(3000); } catch { }
        try { _hbThread?.Join(3000); } catch { }
        _started = false;
    }

    /// <summary>机台状态列表，按机台名排序。心跳信息读缓存，FAIL 统计现查库。</summary>
    public List<AggMachineStatus> GetMachines()
    {
        List<MachineState> snap;
        lock (_lock) snap = _machines.Values.ToList();
        var result = new List<AggMachineStatus>(snap.Count);
        foreach (var st in snap)
        {
            var s = new AggMachineStatus
            {
                Machine = st.Machine,
                Online = st.Online,
                LastHeartbeat = st.LastHeartbeat,
                LastSeq = st.LastSeq,
                Queued = st.Queued,
            };
            try
            {
                s.FailCount = _db.FailCount(st.Machine);
                s.LastFailAt = _db.LastFailAt(st.Machine) ?? "";
                // FirstSeenAt = MIN(ingest_ts)；无 FAIL 记录则回落缓存的心跳 ts，再没有就空串
                s.FirstSeenAt = _db.MinIngestTs(st.Machine) ?? st.LastHeartbeat;
            }
            catch (Exception ex) { Logger.Warning($"[聚合监听] 查询机台 {st.Machine} 状态失败: {ex.Message}"); }
            result.Add(s);
        }
        return result.OrderBy(x => x.Machine, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>最近 FAIL 明细（GUI 表格用），查询失败返回空表不抛异常。</summary>
    public List<AggFailRow> GetRecentFails(int limit = 200)
    {
        try { return _db.QueryFails(limit <= 0 ? 200 : limit); }
        catch (Exception ex) { Logger.Warning($"[聚合监听] 查询 FAIL 明细失败: {ex.Message}"); return new List<AggFailRow>(); }
    }

    // ==================== HTTP 入口（机台端 http 直推，B2 双通道） ====================

    /// <summary>机台 POST 的 fail JSON（与 fail-{seq}.json 文件同格式）→ 解析入库。
    /// 成功入库则 ProcessedFiles++ 并触发 Changed；缺 seq / machine 或 JSON 非法则忽略并记警告。
    /// 与文件监听共用 (machine, seq) 幂等：同一事件两条通道各来一次也只入库一次。
    /// 数据库写入失败抛 <see cref="AggIngestException"/>（HTTP 层据此回 5xx 让机台重推）。</summary>
    public void IngestFail(string json)
    {
        long seq;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("seq", out var ps) ||
                ps.ValueKind != JsonValueKind.Number || !ps.TryGetInt64(out seq) || seq <= 0)
            {
                Logger.Warning("[聚合监听] HTTP fail 缺少有效 seq，忽略");
                return;
            }
        }
        catch (JsonException ex)
        {
            Logger.Warning($"[聚合监听] HTTP fail JSON 解析失败，忽略: {ex.Message}");
            return;
        }

        var row = ParseFailJson(json, "", seq);   // http 通道没有目录名可回落，machine 缺失在下面拦截
        if (string.IsNullOrEmpty(row.Machine))
        {
            Logger.Warning("[聚合监听] HTTP fail 缺少 machine，忽略");
            return;
        }
        try
        {
            bool changed = false;
            if (_db.InsertFail(row) > 0)
            {
                Interlocked.Increment(ref _processedFiles);
                changed = true;
            }
            if (TouchHttpMachine(row.Machine)) changed = true;   // 推送也算存活证据；新机台自动出现在看板
            if (changed) TryFireChanged();
        }
        catch (AggIngestException) { throw; }
        catch (Exception ex)
        {
            // 数据库层失败（连接/锁/磁盘）：上抛带 (machine, seq)，HTTP 层回 5xx 触发机台端重推
            Logger.Error($"[聚合监听] HTTP fail 入库失败 machine={row.Machine} seq={row.Seq}: {ex.Message}");
            throw new AggIngestException(row.Machine, row.Seq, ex.Message);
        }
    }

    /// <summary>机台 POST 的心跳（machine/type/ts/last_seq/queued）→ 更新/创建该机台 MachineState
    /// （LastHeartbeat/LastSeq/Queued/LastSeen=now），上线状态翻转触发 Changed。
    /// 新机台首次 HTTP 推送即自动出现在看板。
    /// 判活基准 = 请求到达时刻（服务器时钟），与机台 ts 无关；ts 仅展示，且做未来时间校验
    /// （机台时钟过快时记 warning 并回落展示为当前时刻，避免看板时间线错乱）。</summary>
    public void IngestHeartbeat(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("machine", out var pm) || string.IsNullOrEmpty(pm.GetString()))
            {
                Logger.Warning("[聚合监听] HTTP 心跳缺少 machine，忽略");
                return;
            }
            var machine = pm.GetString()!;
            var ts = root.TryGetProperty("ts", out var pts) ? pts.GetString() ?? "" : "";
            long lastSeq = 0;
            int queued = 0;
            if (root.TryGetProperty("last_seq", out var pl)) { if (pl.TryGetInt64(out var l)) lastSeq = l; }
            if (root.TryGetProperty("queued", out var pq)) { if (pq.TryGetInt32(out var q)) queued = q; }

            // ts 未来时间校验：机台时钟快过聚合端时（>5 分钟超前）记 warning、展示回落 now
            if (ts.Length > 0 && TryParseTs(ts, out var hbTs) && hbTs > DateTime.Now.AddMinutes(5))
            {
                Logger.Warning($"[聚合监听] 机台 {machine} 心跳 ts 超前未来时间（{ts}），展示回落当前时刻");
                ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }

            bool changed = false;
            lock (_lock)
            {
                if (!_machines.TryGetValue(machine, out var st))
                {
                    st = new MachineState { Machine = machine };
                    _machines[machine] = st;
                    Logger.Info($"[聚合监听] 发现新机台(HTTP): {machine}");
                    changed = true;
                }
                st.ViaHttp = true;
                st.LastSeen = DateTime.Now;
                if (st.LastHeartbeat != ts) changed = true;
                if (!string.IsNullOrEmpty(ts)) st.LastHeartbeat = ts;
                st.LastSeq = lastSeq;
                st.Queued = queued;
                var online = (DateTime.Now - st.LastSeen).TotalSeconds <= _heartbeatTimeoutSec;
                if (online != st.Online)
                {
                    st.Online = online;
                    changed = true;
                }
            }
            if (changed) TryFireChanged();
        }
        catch (JsonException ex)
        {
            Logger.Warning($"[聚合监听] HTTP 心跳 JSON 解析失败，忽略: {ex.Message}");
        }
    }

    /// <summary>http 通道确认机台存活：LastSeen=now；不存在的机台自动创建（ViaHttp 标记，
    /// 无共享目录也不会被目录消失清理）。返回状态是否有变化（新机台/上线翻转）。</summary>
    private bool TouchHttpMachine(string machine)
    {
        bool changed = false;
        lock (_lock)
        {
            if (!_machines.TryGetValue(machine, out var st))
            {
                st = new MachineState { Machine = machine };
                _machines[machine] = st;
                Logger.Info($"[聚合监听] 发现新机台(HTTP): {machine}");
                changed = true;
            }
            st.ViaHttp = true;
            st.LastSeen = DateTime.Now;
            var online = (DateTime.Now - st.LastSeen).TotalSeconds <= _heartbeatTimeoutSec;
            if (online != st.Online)
            {
                st.Online = online;
                changed = true;
            }
        }
        return changed;
    }

    // ==================== 初始全量扫描 ====================

    /// <summary>遍历每个机台目录的 fail-*.json，逐个解析入库（INSERT OR IGNORE 天然幂等，不做 seq 优化）。</summary>
    private void InitialScan()
    {
        int scanned = 0;
        foreach (var dir in Directory.EnumerateDirectories(_shareRoot))
        {
            var machine = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(machine)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "fail-*.json"))
            {
                var m = FailFileRegex.Match(Path.GetFileName(file));
                if (!m.Success) continue;
                try
                {
                    var row = ParseFailFile(file, long.Parse(m.Groups[1].Value));
                    if (_db.InsertFail(row) > 0) Interlocked.Increment(ref _processedFiles);
                    scanned++;
                }
                catch (IOException ex) { Logger.Warning($"[聚合监听] 初始扫描读取失败，跳过 {file}: {ex.Message}"); }
                catch (Exception ex) { Logger.Warning($"[聚合监听] 初始扫描解析失败，跳过 {file}: {ex.Message}"); }
            }
        }
        if (scanned > 0) Logger.Info($"[聚合监听] 初始扫描完成，处理 {scanned} 个 fail 文件");
    }

    // ==================== 增量监听（事件回调只入队，不做 IO） ====================

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        if (_stopping) return;
        Enqueue(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (_stopping) return;
        Enqueue(e.FullPath);   // 机台端 .tmp -> File.Move 改名；e.FullPath/e.Name 已是新名字
    }

    private void Enqueue(string path)
    {
        try { _pending.Enqueue(new QueuedFile { Path = path }); }
        catch (Exception ex) { Logger.Warning($"[聚合监听] 入队失败: {ex.Message}"); }
    }

    // ==================== 处理线程 ====================

    private void ProcessLoop()
    {
        while (!_stopping)
        {
            try
            {
                if (_pending.TryDequeue(out var item)) ProcessQueuedFile(item);
                else Thread.Sleep(QueueWaitMs);
            }
            catch (Exception ex)
            {
                // 单轮异常绝不带崩线程
                Logger.Error($"[聚合监听] 处理线程异常: {ex.Message}");
                try { Thread.Sleep(QueueWaitMs); } catch { }
            }
        }
    }

    private void ProcessQueuedFile(QueuedFile item)
    {
        // 只处理 fail-{seq}.json；心跳由轮询线程管，.tmp 等其余文件一律忽略
        var name = Path.GetFileName(item.Path);
        var m = FailFileRegex.Match(name);
        if (!m.Success) return;
        long seq;
        try { seq = long.Parse(m.Groups[1].Value); }
        catch { return; }

        try
        {
            var row = ParseFailFile(item.Path, seq);
            if (_db.InsertFail(row) > 0)
            {
                Interlocked.Increment(ref _processedFiles);
                TryFireChanged();
            }
        }
        catch (IOException ex)
        {
            // 文件未写完/被占用：放回队尾重试，最多 3 次
            if (item.Retries < MaxRetries)
            {
                _pending.Enqueue(new QueuedFile { Path = item.Path, Retries = item.Retries + 1 });
                Logger.Warning($"[聚合监听] 读取失败({item.Retries + 1}/{MaxRetries})稍后重试 {item.Path}: {ex.Message}");
            }
            else Logger.Error($"[聚合监听] 重试 {MaxRetries} 次仍失败，丢弃 {item.Path}: {ex.Message}");
        }
        catch (JsonException ex)
        {
            // JSON 无效：丢弃并记 Warning（不重试，重试也会是同样的坏文件）
            Logger.Warning($"[聚合监听] JSON 解析失败，丢弃 {item.Path}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[聚合监听] 处理失败，丢弃 {item.Path}: {ex.Message}");
        }
    }

    /// <summary>解析 fail-{seq}.json 文件：读文本后统一走 <see cref="ParseFailJson"/>，
    /// machine 为空时用目录名，seq 以文件名为准。</summary>
    private AggFailRow ParseFailFile(string path, long seq)
    {
        var dirMachine = MachineFromPath(path);
        return ParseFailJson(File.ReadAllText(path, Encoding.UTF8), dirMachine, seq);
    }

    /// <summary>解析 fail JSON（文件内容或 HTTP body 同格式）：顶层 machine/type/seq/ts/data，
    /// data 里各字段 TryGetProperty 给默认值（缺省空串）；machine 为空时用 fallbackMachine；
    /// seq 以传入参数为准（文件版=文件名，HTTP 版=JSON 顶层）。
    /// 跨机台 XML 拉取：data.xml_content 非空时落盘到聚合端 {BaseDir}/data/agg_xml/{machine}/{seq}.xml，
    /// XmlPath 改写为聚合端本地路径（Web 下载走本地白名单，不再依赖机台路径）。</summary>
    private AggFailRow ParseFailJson(string json, string fallbackMachine, long seq)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var row = new AggFailRow { Seq = seq, Machine = fallbackMachine };

        if (root.TryGetProperty("machine", out var pm)) row.Machine = pm.GetString() ?? "";
        if (root.TryGetProperty("type", out var pt)) row.Type = pt.GetString() ?? "fail";
        if (root.TryGetProperty("ts", out var pts)) row.Ts = pts.GetString() ?? "";
        if (string.IsNullOrEmpty(row.Machine)) row.Machine = fallbackMachine;   // machine 为空用 fallback

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            row.StationId = Str(data, "station_id");
            row.Model = Str(data, "model");
            row.Category = Str(data, "category");
            row.TestDate = Str(data, "test_date");
            row.Sn = Str(data, "sn");
            row.Result = Str(data, "result");
            row.XmlPath = Str(data, "xml_path");
            row.FailReason = Str(data, "fail_reason");
            row.Tester = Str(data, "tester");
            row.PanelStatus = Str(data, "panel_status");
            row.BatchTimestamp = Str(data, "batch_timestamp");
            row.HasFailItems = Num(data, "has_fail_items") != 0;   // 0/1
            row.FileSize = Num(data, "file_size");
            // 跨机台 XML：内容随事件上送时落盘本地，XmlPath 改写为本地文件
            var xmlContent = Str(data, "xml_content");
            if (xmlContent.Length > 0)
            {
                var localPath = SaveXmlContent(row.Machine, seq, xmlContent);
                if (localPath != null) row.XmlPath = localPath;
            }
        }
        row.IngestTs = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return row;
    }

    /// <summary>把随事件上送的 XML 内容写到聚合端本地 data/agg_xml/{machine}/{seq}.xml，返回本地路径。
    /// 写失败记警告并返回 null（XmlPath 保留机台端原值，下载仍走白名单逻辑）。</summary>
    private string? SaveXmlContent(string machine, long seq, string content)
    {
        try
        {
            var dir = Path.Combine(AppConfig.BaseDir, "data", "agg_xml", machine);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{seq}.xml");
            File.WriteAllText(path, content, Encoding.UTF8);
            return path;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[聚合监听] XML 内容落盘失败 machine={machine} seq={seq}: {ex.Message}");
            return null;
        }
    }

    private static string Str(JsonElement data, string name) =>
        data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Num(JsonElement data, string name) =>
        data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    /// <summary>shareRoot 相对路径的第一段目录名即机台名；路径异常返回空串。</summary>
    private string MachineFromPath(string fullPath)
    {
        try
        {
            var rel = Path.GetRelativePath(_shareRoot, fullPath);
            var sep = rel.IndexOf(Path.DirectorySeparatorChar);
            return sep < 0 ? "" : rel[..sep];
        }
        catch { return ""; }
    }

    // ==================== 心跳轮询线程 ====================

    /// <summary>fail 文件兜底重扫周期（秒）。FileSystemWatcher 在 SMB 共享上可能漏事件
    /// （网络抖动/改名竞态），心跳轮询线程定期全量扫一遍，靠 (machine, seq) 幂等零成本。</summary>
    private const int RescanIntervalSec = 60;
    private int _rescanCountdown;   // 距下次重扫的剩余秒数（<=0 触发后重置）

    private void HeartbeatLoop()
    {
        while (!_stopping)
        {
            try
            {
                RefreshMachines();
                if (_rescanCountdown <= 0)
                {
                    RescanFailFiles();
                    _rescanCountdown = RescanIntervalSec;
                }
            }
            catch (Exception ex) { Logger.Warning($"[聚合监听] 心跳轮询异常: {ex.Message}"); }
            // 按秒递减，Stop 时最多 1 秒内退出
            for (int i = 0; i < _pollSec && !_stopping; i++)
            {
                try { Thread.Sleep(1000); } catch { }
            }
            _rescanCountdown -= _pollSec;
        }
    }

    /// <summary>
    /// 兜底全量重扫：遍历各机台目录 fail-*.json 全部入队（INSERT OR IGNORE 幂等，
    /// 已入库的直接忽略，零成本）。覆盖 FileSystemWatcher 漏事件/改名竞态等丢事件场景。
    /// 复用处理线程的队列与重试逻辑，不在这里做 IO 解析。
    /// </summary>
    private void RescanFailFiles()
    {
        if (string.IsNullOrEmpty(_shareRoot) || !Directory.Exists(_shareRoot)) return;
        int found = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_shareRoot))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "fail-*.json"))
                {
                    Enqueue(file);
                    found++;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[聚合监听] 兜底重扫失败: {ex.Message}");
            return;
        }
        if (found > 0)
            Logger.Info($"[聚合监听] 兜底重扫: 入队 {found} 个 fail 文件（幂等，重复处理零成本）");
    }

    /// <summary>扫描机台目录：新增/消失的目录更新缓存；读心跳判在线离线（翻转时触发 Changed）。</summary>
    private void RefreshMachines()
    {
        if (!Directory.Exists(_shareRoot)) return;   // 共享目录被摘走：保持上次状态，等恢复
        bool changed = false;
        foreach (var dir in Directory.EnumerateDirectories(_shareRoot))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name)) continue;

            // 有 fail-*.json 或 heartbeat.json 的才算是机台目录
            bool isMachine;
            try
            {
                isMachine = File.Exists(Path.Combine(dir, "heartbeat.json")) ||
                            Directory.EnumerateFiles(dir, "fail-*.json").Any();
            }
            catch (Exception ex) { Logger.Warning($"[聚合监听] 读机台目录失败 {dir}: {ex.Message}"); continue; }
            if (!isMachine) continue;

            MachineState st;
            lock (_lock)
            {
                if (!_machines.TryGetValue(name, out st!))
                {
                    st = new MachineState { Machine = name };
                    _machines[name] = st;
                    Logger.Info($"[聚合监听] 发现新机台: {name}");
                    changed = true;
                }
            }
            if (UpdateHeartbeat(dir, st)) changed = true;
        }

        // 目录已消失的机台从缓存移除（GUI 相应卡片会被移除）。
        // http 通道机台没有共享目录，永不因目录消失被移除（离线靠 LastSeen 超时判定）
        lock (_lock)
        {
            var now = DateTime.Now;
            // http 通道机台（ViaHttp）：没有目录可扫，轮询统一按 LastSeen 超时判在线/离线，
            // 与 smb 心跳同一判定基准（LastSeen 最后一次确认存活；超时翻转触发 Changed）
            foreach (var st in _machines.Values)
            {
                if (!st.ViaHttp || st.LastSeen == DateTime.MinValue) continue;
                var online = (now - st.LastSeen).TotalSeconds <= _heartbeatTimeoutSec;
                if (online != st.Online)
                {
                    Logger.Info($"[聚合监听] 机台 {st.Machine} {(online ? "上线" : "离线")}（HTTP LastSeen 超时判定）");
                    st.Online = online;
                    changed = true;
                    if (!online) FireMachineOffline(st.Machine, st.LastSeen);
                }
            }
            foreach (var gone in _machines.Keys
                         .Where(k => !_machines[k].ViaHttp &&
                                     !Directory.Exists(Path.Combine(_shareRoot, k))).ToList())
                _machines.Remove(gone);
        }
        if (changed) TryFireChanged();
    }

    /// <summary>读并解析一台机的心跳文件，更新在线状态与 LastSeq/Queued。
    /// 读取或解析失败一律保持原状态（临时 IO 故障不能误报离线）。返回状态是否变化。
    /// 在线判定（v3.4.2 改）：以 heartbeat.json 的**文件写时间**（LastWriteTime）为准 ——
    /// 机台端每次写心跳都刷新文件时间戳，聚合端拿它跟本机时钟比，不依赖机台时钟（时钟漂移不再误判）。
    /// JSON 内 ts 仅用于展示；文件写时间陈旧 → 不刷新 LastSeen，超时自然判离线。</summary>
    private bool UpdateHeartbeat(string dir, MachineState st)
    {
        var hbPath = Path.Combine(dir, "heartbeat.json");
        try
        {
            if (!File.Exists(hbPath)) return false;
            string ts = "";
            long lastSeq = st.LastSeq;
            int queued = st.Queued;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(hbPath, Encoding.UTF8));
                var root = doc.RootElement;
                if (root.TryGetProperty("ts", out var pv)) ts = pv.GetString() ?? "";
                if (root.TryGetProperty("last_seq", out var pl)) { if (pl.TryGetInt64(out var l)) lastSeq = l; }
                if (root.TryGetProperty("queued", out var pq)) { if (pq.TryGetInt32(out var q)) queued = q; }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[聚合监听] 心跳解析失败，保持原状态 {hbPath}: {ex.Message}");
                return false;
            }

            // 文件写时间即心跳新鲜度基准；机台时钟（ts）只展示不判活
            DateTime hbWrite;
            try { hbWrite = File.GetLastWriteTime(hbPath); }
            catch (Exception ex)
            {
                Logger.Warning($"[聚合监听] 心跳文件写时间读取失败，保持原状态 {hbPath}: {ex.Message}");
                return false;
            }

            lock (_lock)
            {
                bool changed = false;
                // 心跳文件新鲜（写时间距现在不超超时）才算机台确认存活：刷新 LastSeen；
                // 陈旧心跳（机台已停推）不刷新，LastSeen 停在最后确认点，超时后判离线
                if ((DateTime.Now - hbWrite).TotalSeconds <= _heartbeatTimeoutSec)
                    st.LastSeen = DateTime.Now;
                var online = (DateTime.Now - st.LastSeen).TotalSeconds <= _heartbeatTimeoutSec;
                if (online != st.Online)
                {
                    Logger.Info($"[聚合监听] 机台 {st.Machine} {(online ? "上线" : "离线")}（心跳 {ts}）");
                    st.Online = online;
                    changed = true;
                    if (!online) FireMachineOffline(st.Machine, st.LastSeen);
                }
                if (st.LastHeartbeat != ts) changed = true;
                st.LastHeartbeat = ts;
                st.LastSeq = lastSeq;
                st.Queued = queued;
                return changed;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[聚合监听] 心跳读取异常，保持原状态 {hbPath}: {ex.Message}");
            return false;
        }
    }

    // ==================== 事件 ====================

    /// <summary>解析 "yyyy-MM-dd HH:mm:ss"；先走 TimeUtil.Normalize（兼容 ISO/数字等怪格式），
    /// 再用 TryParseExact 兜底。失败返回 false。（HTTP 心跳 ts 未来时间校验用）</summary>
    private static bool TryParseTs(string ts, out DateTime dt)
    {
        var norm = TimeUtil.Normalize(ts);
        if (norm.Length == 0) { dt = default; return false; }
        return DateTime.TryParseExact(norm, "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out dt);
    }

    /// <summary>回调异常不能影响后台线程，统一包一层。</summary>
    private void TryFireChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Logger.Warning($"[聚合监听] Changed 回调异常: {ex.Message}"); }
    }

    /// <summary>触发机台离线事件（告警用）。回调异常不影响后台线程。</summary>
    private void FireMachineOffline(string machine, DateTime lastSeen)
    {
        try { MachineOffline?.Invoke(machine, lastSeen); }
        catch (Exception ex) { Logger.Warning($"[聚合监听] MachineOffline 回调异常: {ex.Message}"); }
    }

    public void Dispose() => Stop();
}

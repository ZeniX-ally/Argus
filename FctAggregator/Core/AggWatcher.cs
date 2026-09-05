using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FctAggregator;

internal sealed class QueuedFile
{
    public string Path = "";
    public int Retries;
}

internal sealed class MachineState
{
    public string Machine = "";
    public string LastHeartbeat = "";
    public DateTime HeartbeatTime = DateTime.MinValue;
    public DateTime LastSeen = DateTime.MinValue;
    public bool ViaHttp;
    public long LastSeq;
    public int Queued;
    public bool Online;
}

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

public class AggMachineStatus
{
    public string Machine = "";
    public bool Online;
    public string LastHeartbeat = "";
    public long LastSeq;
    public int Queued;
    public long FailCount;
    public string LastFailAt = "";
    public string FirstSeenAt = "";
}

[Obsolete("v3.5.0 起由 MeshNode/MeshReceiver(P2P) 取代；保留仅供兼容与自检")]
public class AggWatcher : IDisposable
{
    private static readonly Regex FailFileRegex = new(@"^fail-(\d+)\.json$", RegexOptions.Compiled);
    private const int MaxRetries = 3;
    private const int QueueWaitMs = 100;

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

    public event Action? Changed;

    public event Action<string, DateTime>? MachineOffline;

    public event Action<string, DateTime>? MachineOnline;

    public long ProcessedFiles => Interlocked.Read(ref _processedFiles);

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
                s.FirstSeenAt = _db.MinIngestTs(st.Machine) ?? st.LastHeartbeat;
            }
            catch (Exception ex) { Logger.Warning($"[聚合监听] 查询机台 {st.Machine} 状态失败: {ex.Message}"); }
            result.Add(s);
        }
        return result.OrderBy(x => x.Machine, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public List<AggFailRow> GetRecentFails(int limit = 200)
    {
        try { return _db.QueryFails(limit <= 0 ? 200 : limit); }
        catch (Exception ex) { Logger.Warning($"[聚合监听] 查询 FAIL 明细失败: {ex.Message}"); return new List<AggFailRow>(); }
    }

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

        var row = ParseFailJson(json, "", seq);
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
            if (TouchHttpMachine(row.Machine)) changed = true;
            if (changed) TryFireChanged();
        }
        catch (AggIngestException) { throw; }
        catch (Exception ex)
        {
            Logger.Error($"[聚合监听] HTTP fail 入库失败 machine={row.Machine} seq={row.Seq}: {ex.Message}");
            throw new AggIngestException(row.Machine, row.Seq, ex.Message);
        }
    }

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

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        if (_stopping) return;
        Enqueue(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (_stopping) return;
        Enqueue(e.FullPath);
    }

    private void Enqueue(string path)
    {
        try { _pending.Enqueue(new QueuedFile { Path = path }); }
        catch (Exception ex) { Logger.Warning($"[聚合监听] 入队失败: {ex.Message}"); }
    }

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
                Logger.Error($"[聚合监听] 处理线程异常: {ex.Message}");
                try { Thread.Sleep(QueueWaitMs); } catch { }
            }
        }
    }

    private void ProcessQueuedFile(QueuedFile item)
    {
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
            if (item.Retries < MaxRetries)
            {
                _pending.Enqueue(new QueuedFile { Path = item.Path, Retries = item.Retries + 1 });
                Logger.Warning($"[聚合监听] 读取失败({item.Retries + 1}/{MaxRetries})稍后重试 {item.Path}: {ex.Message}");
            }
            else Logger.Error($"[聚合监听] 重试 {MaxRetries} 次仍失败，丢弃 {item.Path}: {ex.Message}");
        }
        catch (JsonException ex)
        {
            Logger.Warning($"[聚合监听] JSON 解析失败，丢弃 {item.Path}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[聚合监听] 处理失败，丢弃 {item.Path}: {ex.Message}");
        }
    }

    private AggFailRow ParseFailFile(string path, long seq)
    {
        var dirMachine = MachineFromPath(path);
        return ParseFailJson(File.ReadAllText(path, Encoding.UTF8), dirMachine, seq);
    }

    private AggFailRow ParseFailJson(string json, string fallbackMachine, long seq)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var row = new AggFailRow { Seq = seq, Machine = fallbackMachine };

        if (root.TryGetProperty("machine", out var pm)) row.Machine = pm.GetString() ?? "";
        if (root.TryGetProperty("type", out var pt)) row.Type = pt.GetString() ?? "fail";
        if (root.TryGetProperty("ts", out var pts)) row.Ts = pts.GetString() ?? "";
        if (string.IsNullOrEmpty(row.Machine)) row.Machine = fallbackMachine;

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
            row.HasFailItems = Num(data, "has_fail_items") != 0;
            row.FileSize = Num(data, "file_size");
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

    private const int RescanIntervalSec = 60;
    private int _rescanCountdown;

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
            for (int i = 0; i < _pollSec && !_stopping; i++)
            {
                try { Thread.Sleep(1000); } catch { }
            }
            _rescanCountdown -= _pollSec;
        }
    }

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

    private void RefreshMachines()
    {
        if (!Directory.Exists(_shareRoot)) return;
        bool changed = false;
        foreach (var dir in Directory.EnumerateDirectories(_shareRoot))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name)) continue;

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

        lock (_lock)
        {
            var now = DateTime.Now;
            foreach (var st in _machines.Values)
            {
                if (!st.ViaHttp || st.LastSeen == DateTime.MinValue) continue;
                var online = (now - st.LastSeen).TotalSeconds <= _heartbeatTimeoutSec;
                if (online != st.Online)
                {
                    Logger.Info($"[聚合监听] 机台 {st.Machine} {(online ? "上线" : "离线")}（HTTP LastSeen 超时判定）");
                    st.Online = online;
                    changed = true;
                    if (online) FireMachineOnline(st.Machine, st.LastSeen);
                    else FireMachineOffline(st.Machine, st.LastSeen);
                }
            }
            foreach (var gone in _machines.Keys
                         .Where(k => !_machines[k].ViaHttp &&
                                     !Directory.Exists(Path.Combine(_shareRoot, k))).ToList())
                _machines.Remove(gone);
        }
        if (changed) TryFireChanged();
    }

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
                if ((DateTime.Now - hbWrite).TotalSeconds <= _heartbeatTimeoutSec)
                    st.LastSeen = DateTime.Now;
                var online = (DateTime.Now - st.LastSeen).TotalSeconds <= _heartbeatTimeoutSec;
                if (online != st.Online)
                {
                    Logger.Info($"[聚合监听] 机台 {st.Machine} {(online ? "上线" : "离线")}（心跳 {ts}）");
                    st.Online = online;
                    changed = true;
                    if (online) FireMachineOnline(st.Machine, st.LastSeen);
                    else FireMachineOffline(st.Machine, st.LastSeen);
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

    private static bool TryParseTs(string ts, out DateTime dt)
    {
        var norm = TimeUtil.Normalize(ts);
        if (norm.Length == 0) { dt = default; return false; }
        return DateTime.TryParseExact(norm, "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out dt);
    }

    private void TryFireChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Logger.Warning($"[聚合监听] Changed 回调异常: {ex.Message}"); }
    }

    private void FireMachineOffline(string machine, DateTime lastSeen)
    {
        try { MachineOffline?.Invoke(machine, lastSeen); }
        catch (Exception ex) { Logger.Warning($"[聚合监听] MachineOffline 回调异常: {ex.Message}"); }
    }

    private void FireMachineOnline(string machine, DateTime lastSeen)
    {
        try { MachineOnline?.Invoke(machine, lastSeen); }
        catch (Exception ex) { Logger.Warning($"[聚合监听] MachineOnline 回调异常: {ex.Message}"); }
    }

    public void Dispose() => Stop();
}

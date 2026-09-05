using System.Text;
using System.Text.Json;

namespace FctAggregator;

public sealed class MeshReceiver
{
    private readonly AggDatabase _db;
    private readonly object _lock = new();
    private readonly Dictionary<string, PeerView> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _heartbeatTimeoutSec;
    private readonly string _localMachine;

    public event Action? Changed;
    public event Action<string, DateTime>? PeerOffline;
    public event Action<string, DateTime>? PeerOnline;

    public MeshReceiver(AggDatabase db, int heartbeatTimeoutSec = 90, string localMachine = "")
    {
        _db = db;
        _heartbeatTimeoutSec = Math.Max(1, heartbeatTimeoutSec);
        _localMachine = localMachine;
    }

    public void HandleFail(string json)
    {
        long seq;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("seq", out var ps) ||
                ps.ValueKind != JsonValueKind.Number || !ps.TryGetInt64(out seq) || seq <= 0)
            { Logger.Warning("[Mesh接收] FAIL 缺少有效 seq，忽略"); return; }
        }
        catch (JsonException ex) { Logger.Warning($"[Mesh接收] FAIL JSON 解析失败，忽略: {ex.Message}"); return; }

        var row = ParseFailJson(json, seq);
        if (string.IsNullOrEmpty(row.Machine)) { Logger.Warning("[Mesh接收] FAIL 缺少 machine，忽略"); return; }
        try
        {
            int inserted = EnqueueAndWaitCommit(row);
            bool changed = false;
            if (inserted > 0) changed = true;
            if (TouchPeer(row.Machine)) changed = true;
            if (changed) TryFireChanged();
        }
        catch (Exception ex)
        {
            Logger.Error($"[Mesh接收] FAIL 入库失败 machine={row.Machine} seq={row.Seq}: {ex.Message}");
            throw new AggIngestException(row.Machine, row.Seq, ex.Message);
        }
    }

    private const int IngestMaxDelayMs = 50;
    private const int IngestBatchSize = 100;

    private sealed class PendingIngest
    {
        public AggFailRow Row = null!;
        public TaskCompletionSource<int> Done = null!;
    }

    private readonly object _ingestSync = new();
    private readonly List<PendingIngest> _ingestPending = new();
    private readonly AutoResetEvent _ingestSignal = new(false);
    private Thread? _ingestFlusher;
    private long _committedBatches;
    private long _committedRows;
    private long _receivedFails;
    private long _ignoredFails;

    public long CommittedBatches => Interlocked.Read(ref _committedBatches);
    public long CommittedRows => Interlocked.Read(ref _committedRows);
    public long ReceivedFails => Interlocked.Read(ref _receivedFails);
    public long IgnoredFails => Interlocked.Read(ref _ignoredFails);

    private int EnqueueAndWaitCommit(AggFailRow row)
    {
        EnsureFlusherStarted();
        var p = new PendingIngest
        {
            Row = row,
            Done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        lock (_ingestSync) _ingestPending.Add(p);
        _ingestSignal.Set();
        return p.Done.Task.GetAwaiter().GetResult();
    }

    private void EnsureFlusherStarted()
    {
        if (_ingestFlusher != null) return;
        lock (_ingestSync)
        {
            if (_ingestFlusher != null) return;
            _ingestFlusher = new Thread(IngestFlushLoop) { IsBackground = true, Name = "mesh-receiver-ingest" };
            _ingestFlusher.Start();
        }
    }

    private void IngestFlushLoop()
    {
        while (true)
        {
            try { _ingestSignal.WaitOne(IngestMaxDelayMs); } catch { }
            FlushPendingOnce();
        }
    }

    private void FlushPendingOnce()
    {
        while (true)
        {
            List<PendingIngest>? batch;
            lock (_ingestSync)
            {
                if (_ingestPending.Count == 0) return;
                batch = _ingestPending.GetRange(0, Math.Min(_ingestPending.Count, IngestBatchSize));
                _ingestPending.RemoveRange(0, batch.Count);
            }
            try
            {
                _db.InsertBatch(batch.Select(b => b.Row));
                foreach (var b in batch)
                    b.Done.TrySetResult(b.Row.Id > 0 ? 1 : 0);
                Interlocked.Increment(ref _committedBatches);
                Interlocked.Add(ref _committedRows, batch.Count);
                Interlocked.Add(ref _receivedFails, batch.Count);
                Interlocked.Add(ref _ignoredFails, batch.Count(b => b.Row.Id <= 0));
            }
            catch (Exception ex)
            {
                Logger.Error($"[Mesh接收] 组提交失败（{batch.Count} 条整批回滚，触发对端重推）: {ex.Message}");
                foreach (var b in batch)
                    b.Done.TrySetException(ex);
            }
        }
    }

    public void HandleInfo(string json)
    {
        try
        {
            var row = ParseDeviceInfo(json);
            if (string.IsNullOrEmpty(row.Machine)) { Logger.Warning("[Mesh接收] info 缺少 machine，忽略"); return; }
            try
            {
                _db.UpsertDeviceInfo(row);
                _db.InsertDeviceSample(new DeviceSampleRow
                {
                    Machine = row.Machine,
                    Ts = row.LastSeen,
                    CpuUsage = row.CpuUsage,
                    MemUsedMb = row.MemUsedMb,
                    DiskFreeGb = row.DiskFreeGb,
                });
            }
            catch (Exception ex) { Logger.Error($"[Mesh接收] device_info 落库失败 machine={row.Machine}: {ex.Message}"); }
            bool changed = false;
            lock (_lock)
            {
                if (!_peers.TryGetValue(row.Machine, out var st))
                {
                    st = new PeerView { Machine = row.Machine };
                    _peers[row.Machine] = st;
                    Logger.Info($"[Mesh接收] 发现邻居(info): {row.Machine}");
                    changed = true;
                }
                st.LastSeen = DateTime.Now;
                var online = (DateTime.Now - st.LastSeen).TotalSeconds <= _heartbeatTimeoutSec;
                if (online != st.Online) { st.Online = online; changed = true; if (online) FirePeerOnline(row.Machine, st.LastSeen); else FirePeerOffline(row.Machine, st.LastSeen); }
            }
            if (changed) TryFireChanged();
        }
        catch (JsonException ex) { Logger.Warning($"[Mesh接收] info JSON 解析失败，忽略: {ex.Message}"); }
    }

    public void HandleFctIni(string json)
    {
        try
        {
            var row = ParseDeviceFct(json);
            if (string.IsNullOrEmpty(row.Machine)) { Logger.Warning("[Mesh接收] fctini 缺少 machine，忽略"); return; }
            try { FctIniWatcher.CheckAndLog(_db, row); }
            catch (Exception ex) { Logger.Error($"[Mesh接收] device_fct 落库失败 machine={row.Machine}: {ex.Message}"); }
        }
        catch (JsonException ex) { Logger.Warning($"[Mesh接收] fctini JSON 解析失败，忽略: {ex.Message}"); }
    }

    private static DeviceInfoRow ParseDeviceInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var r = new DeviceInfoRow();
        r.Machine = root.TryGetProperty("machine", out var pm) ? pm.GetString() ?? "" : "";
        r.Hostname = root.TryGetProperty("hostname", out var v) ? v.GetString() ?? "" : "";
        r.Os = root.TryGetProperty("os", out var v2) ? v2.GetString() ?? "" : "";
        r.OsVersion = root.TryGetProperty("os_version", out var v3) ? v3.GetString() ?? "" : "";
        r.Ip = root.TryGetProperty("ip", out var v4) ? v4.GetString() ?? "" : "";
        r.Mac = root.TryGetProperty("mac", out var v5) ? v5.GetString() ?? "" : "";
        r.CpuModel = root.TryGetProperty("cpu_model", out var v6) ? v6.GetString() ?? "" : "";
        if (root.TryGetProperty("cpu_cores", out var vc) && vc.TryGetInt32(out var ci)) r.CpuCores = ci;
        if (root.TryGetProperty("cpu_usage", out var cu) && cu.TryGetDouble(out var cd)) r.CpuUsage = cd;
        if (root.TryGetProperty("mem_total_mb", out var mt) && mt.TryGetInt32(out var mi)) r.MemTotalMb = mi;
        if (root.TryGetProperty("mem_used_mb", out var mu) && mu.TryGetInt32(out var mui)) r.MemUsedMb = mui;
        if (root.TryGetProperty("disk_total_gb", out var dt) && dt.TryGetDouble(out var dd)) r.DiskTotalGb = dd;
        if (root.TryGetProperty("disk_free_gb", out var df) && df.TryGetDouble(out var ddf)) r.DiskFreeGb = ddf;
        if (root.TryGetProperty("uptime_sec", out var up) && up.TryGetInt64(out var ul)) r.UptimeSec = ul;
        r.ArgusVersion = root.TryGetProperty("argus_version", out var av) ? av.GetString() ?? "" : "";
        r.LastSeen = root.TryGetProperty("ts", out var ts) ? ts.GetString() ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        r.UpdatedAt = r.LastSeen;
        return r;
    }

    private static DeviceFctRow ParseDeviceFct(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var r = new DeviceFctRow();
        r.Machine = root.TryGetProperty("machine", out var pm) ? pm.GetString() ?? "" : "";
        r.IniPath = root.TryGetProperty("ini_path", out var v) ? v.GetString() ?? "" : "";
        if (root.TryGetProperty("found", out var vf))
            r.Found = vf.ValueKind == JsonValueKind.True || (vf.ValueKind == JsonValueKind.Number && vf.GetInt32() != 0);
        r.Error = root.TryGetProperty("error", out var ve) ? ve.GetString() : null;
        if (root.TryGetProperty("models", out var vm) && vm.ValueKind == JsonValueKind.Array)
            foreach (var e in vm.EnumerateArray()) if (e.ValueKind == JsonValueKind.String) r.Models.Add(e.GetString() ?? "");
        if (root.TryGetProperty("fw_versions", out var fw) && fw.ValueKind == JsonValueKind.Array)
            foreach (var e in fw.EnumerateArray())
            {
                var label = e.TryGetProperty("label", out var la) ? la.GetString() ?? "" : "";
                var ver = e.TryGetProperty("version", out var va) ? va.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(label)) r.FwVersions.Add((label, ver));
            }
        if (root.TryGetProperty("devices", out var dev) && dev.ValueKind == JsonValueKind.Array)
            foreach (var e in dev.EnumerateArray())
            {
                var di = new FctDeviceInfo();
                di.Name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                di.Port = e.TryGetProperty("port", out var p) ? p.GetString() ?? "" : "";
                di.Type = e.TryGetProperty("type", out var t) ? t.GetString() ?? "com" : "com";
                if (e.TryGetProperty("online", out var o)) di.Online = o.ValueKind == JsonValueKind.True || (o.ValueKind == JsonValueKind.Number && o.GetInt32() != 0);
                r.Devices.Add(di);
            }
        if (root.TryGetProperty("a2l_files", out var a2l) && a2l.ValueKind == JsonValueKind.Array)
            foreach (var e in a2l.EnumerateArray())
            {
                var label = e.TryGetProperty("label", out var la) ? la.GetString() ?? "" : "";
                var file = e.TryGetProperty("file", out var fa) ? fa.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(label)) r.A2lFiles.Add((label, file));
            }
        r.LastSeen = root.TryGetProperty("ts", out var ts) ? ts.GetString() ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        r.UpdatedAt = r.LastSeen;
        return r;
    }

    public void HandleHeartbeat(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("machine", out var pm) || string.IsNullOrEmpty(pm.GetString()))
            { Logger.Warning("[Mesh接收] 心跳缺少 machine，忽略"); return; }
            var machine = pm.GetString()!;
            var ts = root.TryGetProperty("ts", out var pts) ? pts.GetString() ?? "" : "";
            long lastSeq = 0; int queued = 0;
            if (root.TryGetProperty("last_seq", out var pl)) { if (pl.TryGetInt64(out var l)) lastSeq = l; }
            if (root.TryGetProperty("queued", out var pq)) { if (pq.TryGetInt32(out var q)) queued = q; }

            var todayStats = (present: false, date: "", total: 0, pass: 0, fail: 0, intr: 0, prod: 0);
            if (root.TryGetProperty("today", out var pt) && !string.IsNullOrEmpty(pt.GetString()))
            {
                todayStats.present = true;
                todayStats.date = pt.GetString()!;
                if (root.TryGetProperty("today_total", out var vt)) vt.TryGetInt32(out todayStats.total);
                if (root.TryGetProperty("today_pass", out var vp)) vp.TryGetInt32(out todayStats.pass);
                if (root.TryGetProperty("today_fail", out var vf)) vf.TryGetInt32(out todayStats.fail);
                if (root.TryGetProperty("today_interrupted", out var vi)) vi.TryGetInt32(out todayStats.intr);
                if (root.TryGetProperty("today_products", out var vprod)) vprod.TryGetInt32(out todayStats.prod);
            }
            var light = (present: false, cpu: 0.0, memUsed: 0, memTotal: 0);
            if (root.TryGetProperty("system", out var sys) && sys.ValueKind == JsonValueKind.Object)
            {
                light.present = true;
                if (sys.TryGetProperty("cpu_usage", out var vc) && vc.TryGetDouble(out var cd)) light.cpu = cd;
                else if (sys.TryGetProperty("cpu", out var vc2) && vc2.TryGetDouble(out var cd2)) light.cpu = cd2;
                if (sys.TryGetProperty("mem_used_mb", out var mu) && mu.TryGetInt32(out var mui)) light.memUsed = mui;
                if (sys.TryGetProperty("mem_total_mb", out var mt) && mt.TryGetInt32(out var mti)) light.memTotal = mti;
            }

            bool changed = false;
            bool statsDirty = false;
            lock (_lock)
            {
                if (!_peers.TryGetValue(machine, out var st))
                {
                    st = new PeerView { Machine = machine };
                    _peers[machine] = st;
                    Logger.Info($"[Mesh接收] 发现邻居(心跳): {machine}");
                    changed = true;
                }
                st.LastSeen = DateTime.Now;
                if (st.LastHeartbeat != ts) changed = true;
                st.LastHeartbeat = ts;
                st.LastSeq = lastSeq;
                st.Queued = queued;
                var online = (DateTime.Now - st.LastSeen).TotalSeconds <= _heartbeatTimeoutSec;
                if (online != st.Online)
                {
                    st.Online = online; changed = true;
                    if (online) FirePeerOnline(machine, st.LastSeen); else FirePeerOffline(machine, st.LastSeen);
                }
                if (todayStats.present)
                {
                    var cur = (todayStats.date, todayStats.total, todayStats.pass, todayStats.fail, todayStats.intr, todayStats.prod);
                    if (st.LastStats != cur)
                    {
                        st.LastStats = cur;
                        statsDirty = true;
                    }
                }
            }
            if (statsDirty && todayStats.present)
            {
                try
                {
                    _db.UpsertDailyStats(machine, todayStats.date,
                        new AggDatabase.DailyStats(todayStats.total, todayStats.pass, todayStats.fail, todayStats.intr, todayStats.prod));
                }
                catch (Exception ex) { Logger.Error($"[Mesh接收] yld_daily upsert 失败: {ex.Message}"); }
            }
            if (light.present)
            {
                try { _db.UpsertDeviceLight(machine, light.cpu, light.memUsed, light.memTotal); }
                catch (Exception ex) { Logger.Warning($"[Mesh接收] device light upsert 失败 machine={machine}: {ex.Message}"); }
            }
            if (changed) TryFireChanged();
        }
        catch (JsonException ex) { Logger.Warning($"[Mesh接收] 心跳 JSON 解析失败，忽略: {ex.Message}"); }
    }

    public Func<string, bool>? LocalReadValidator { get; set; }

    public static string XmlCacheRoot => Path.Combine(AppConfig.BaseDir, "data", "agg_xml");

    private static string XmlCachePathFor(AggFailRow row, long failId)
    {
        var date = row.TestDate;
        if (string.IsNullOrEmpty(date))
        {
            var ts = string.IsNullOrEmpty(row.Ts) ? row.IngestTs : row.Ts;
            date = ts.Length >= 10 ? ts.Replace("-", "").Replace("/", "").Replace(":", "").Substring(0, 8) : "unknown";
        }
        var machine = string.IsNullOrEmpty(row.Machine) ? "unknown" : SanitizeSegment(row.Machine);
        var d = SanitizeSegment(date);
        return Path.Combine(XmlCacheRoot, machine, d, $"{failId}.xml");
    }

    private static string SanitizeSegment(string s)
    {
        var chars = s.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray();
        var r = new string(chars);
        return r.Length == 0 ? "unknown" : r;
    }

    public string? FetchXmlForFail(long failId)
    {
        try
        {
            var row = _db.GetFailById(failId);
            if (row == null) return null;
            var cachePath = XmlCachePathFor(row, failId);
            if (File.Exists(cachePath))
                return File.ReadAllText(cachePath, Encoding.UTF8);
            string? content = null;
            if (!string.IsNullOrEmpty(row.XmlPath) && File.Exists(row.XmlPath))
            {
                var validator = LocalReadValidator;
                if (validator != null && !validator(row.XmlPath))
                {
                    Logger.Warning($"[Mesh接收] 拒绝读取白名单外的 xml_path（id={failId}）: {row.XmlPath}");
                    return null;
                }
                content = File.ReadAllText(row.XmlPath, Encoding.UTF8);
            }
            else if (!string.IsNullOrEmpty(row.Machine))
            {
                foreach (var peer in EnumeratePeerUrls())
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get,
                            peer + (peer.EndsWith("/") ? "" : "/") + $"api/file?id={failId}");
                        using var resp = MeshPusher.SendStatic(req);
                        if (resp.IsSuccessStatusCode)
                        {
                            content = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            break;
                        }
                    }
                    catch { }
                }
            }
            if (content != null && content.Length > 0)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    File.WriteAllText(cachePath, content, new UTF8Encoding(false));
                }
                catch (Exception ex) { Logger.Warning($"[Mesh接收] XML 容灾缓存写入失败 id={failId}: {ex.Message}"); }
            }
            return content;
        }
        catch (Exception ex) { Logger.Warning($"[Mesh接收] 拉取 XML 失败 id={failId}: {ex.Message}"); return null; }
    }

    private IEnumerable<string> EnumeratePeerUrls()
    {
        lock (_lock) { foreach (var u in _peerUrls) yield return u; }
    }
    private List<string> _peerUrls = new();
    public void SetPeerUrls(IEnumerable<string> urls) { lock (_lock) { _peerUrls = urls.ToList(); } }

    public List<PeerStatusDto> GetPeerStatuses()
    {
        List<PeerView> snap; lock (_lock) snap = _peers.Values.ToList();
        var result = new List<PeerStatusDto>(snap.Count + 1);
        if (!string.IsNullOrEmpty(_localMachine))
            result.Add(new PeerStatusDto { Machine = _localMachine, IsSelf = true, Online = true });
        foreach (var st in snap.OrderBy(x => x.Machine, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                result.Add(new PeerStatusDto
                {
                    Machine = st.Machine,
                    Online = st.Online,
                    LastHeartbeat = st.LastHeartbeat,
                    LastSeq = st.LastSeq,
                    Queued = st.Queued,
                    FailCount = _db.FailCountCached(st.Machine),
                });
            }
            catch (Exception ex) { Logger.Warning($"[Mesh接收] 查询邻居 {st.Machine} 状态失败: {ex.Message}"); }
        }
        return result;
    }

    private bool TouchPeer(string machine)
    {
        bool changed = false;
        lock (_lock)
        {
            if (!_peers.TryGetValue(machine, out var st))
            {
                st = new PeerView { Machine = machine };
                _peers[machine] = st;
                Logger.Info($"[Mesh接收] 发现邻居(FAIL): {machine}");
                changed = true;
            }
            st.LastSeen = DateTime.Now;
            var online = (DateTime.Now - st.LastSeen).TotalSeconds <= _heartbeatTimeoutSec;
            if (online != st.Online) { st.Online = online; changed = true; }
        }
        return changed;
    }

    private AggFailRow ParseFailJson(string json, long seq)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var row = new AggFailRow { Seq = seq, Machine = "" };
        if (root.TryGetProperty("machine", out var pm)) row.Machine = pm.GetString() ?? "";
        if (root.TryGetProperty("type", out var pt)) row.Type = pt.GetString() ?? "fail";
        if (root.TryGetProperty("ts", out var pts)) row.Ts = pts.GetString() ?? "";

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
            row.FixtureId = Str(data, "fixture_id");
            row.BatchTimestamp = Str(data, "batch_timestamp");
            row.HasFailItems = Num(data, "has_fail_items") != 0;
            row.FileSize = Num(data, "file_size");
        }
        row.IngestTs = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return row;
    }

    private static string Str(JsonElement data, string name) =>
        data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static long Num(JsonElement data, string name) =>
        data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private void TryFireChanged() { try { Changed?.Invoke(); } catch (Exception ex) { Logger.Warning($"[Mesh接收] Changed 回调异常: {ex.Message}"); } }
    private void FirePeerOffline(string m, DateTime t) { try { PeerOffline?.Invoke(m, t); } catch { } }
    private void FirePeerOnline(string m, DateTime t) { try { PeerOnline?.Invoke(m, t); } catch { } }

    private sealed class PeerView
    {
        public string Machine = "";
        public string LastHeartbeat = "";
        public DateTime LastSeen = DateTime.MinValue;
        public long LastSeq;
        public int Queued;
        public bool Online;
        public (string date, int total, int pass, int fail, int intr, int prod) LastStats;
    }
}

public sealed class PeerStatusDto
{
    public string Machine = "";
    public bool IsSelf;
    public bool Online;
    public string LastHeartbeat = "";
    public long LastSeq;
    public int Queued;
    public long FailCount;
}

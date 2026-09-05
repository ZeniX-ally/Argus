using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;

namespace FctAggregator;

public sealed class DeviceInfoCollector : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly string _machine;
    private readonly string[] _peers;
    private Thread? _thread;
    private volatile bool _stopping;
    private long _lastProcessTicks;
    private TimeSpan _lastProcTime;
    private readonly object _cpuLock = new();

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public DeviceInfoCollector(AppConfig cfg, string machine, IEnumerable<string> peers)
    {
        _cfg = cfg;
        _machine = string.IsNullOrEmpty(machine) ? "UNKNOWN" : machine;
        _peers = peers.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToArray();
        try
        {
            var proc = Process.GetCurrentProcess();
            _lastProcTime = proc.TotalProcessorTime;
            _lastProcessTicks = Environment.TickCount64;
        }
        catch { }
    }

    public void Start()
    {
        if (!_cfg.DeviceInfoEnabled) { Logger.Info("[设备采集] 已禁用（device_info_enabled=false）"); return; }
        if (_thread != null) return;
        _stopping = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "device-collector" };
        _thread.Start();
        Logger.Info($"[设备采集] 已启动: machine={_machine}, interval={_cfg.DeviceInfoIntervalSec}s, peers={_peers.Length}");
        Task.Run(() => { try { PushFull(); } catch (Exception ex) { Logger.Warning($"[设备采集] 首次推送失败: {ex.Message}"); } });
    }

    public void Stop()
    {
        _stopping = true;
        try { _thread?.Join(2000); } catch { }
        _thread = null;
    }

    public void Dispose() => Stop();

    private void Loop()
    {
        var intervalMs = Math.Max(30, _cfg.DeviceInfoIntervalSec) * 1000;
        while (!_stopping)
        {
            for (int i = 0; i < intervalMs / 1000 && !_stopping; i++)
            {
                try { Thread.Sleep(1000); } catch { }
            }
            if (_stopping) break;
            try { PushFull(); }
            catch (Exception ex) { Logger.Warning($"[设备采集] 推送失败: {ex.Message}"); }
        }
    }

    private void PushFull()
    {
        var row = CollectFull();
        var fct = CollectFct();
        var infoJson = SerializeDeviceInfo(row);
        var fctJson = SerializeFct(fct);
        foreach (var peer in _peers)
        {
            _ = TryPost(peer, "api/mesh/info", infoJson);
            _ = TryPost(peer, "api/mesh/fctini", fctJson);
        }
        Logger.Info($"[设备采集] 已推送 L1 快照（cpu={row.CpuUsage:F1}% mem={row.MemUsedMb}/{row.MemTotalMb}MB diskFree={row.DiskFreeGb:F1}GB）到 {_peers.Length} 个 peer");
    }

    private static async Task TryPost(string peer, string path, string json)
    {
        try
        {
            var url = peer.TrimEnd('/') + "/" + path;
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            var token = AppConfig.Instance.AggToken;
            if (!string.IsNullOrEmpty(token)) req.Headers.Add(MeshPusher.TokenHeader, token);
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                Logger.Warning($"[设备采集] POST {path} -> {peer} HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { Logger.Warning($"[设备采集] POST {path} -> {peer} 异常: {ex.Message}"); }
    }

    public (double cpuUsage, int memUsedMb, int memTotalMb) GetLightSnapshot()
    {
        try
        {
            var cpu = SampleCpu();
            var (total, used) = SampleMemory();
            return (cpu, used, total);
        }
        catch { return (0, 0, 0); }
    }

    public DeviceInfoRow CollectFull()
    {
        var row = new DeviceInfoRow { Machine = _machine };
        try { row.Hostname = Environment.MachineName; } catch { row.Hostname = _machine; }
        try { row.Os = Environment.OSVersion.Platform.ToString(); } catch { }
        try { row.OsVersion = Environment.OSVersion.VersionString; } catch { }
        try { row.Ip = ResolveIp(); } catch { }
        try { row.Mac = ResolveMac(); } catch { }
        try { var (model, cores) = ResolveCpuModel(); row.CpuModel = model; row.CpuCores = cores; } catch { row.CpuCores = Environment.ProcessorCount; }
        if (row.CpuCores <= 0) row.CpuCores = Environment.ProcessorCount;
        try { row.CpuUsage = SampleCpu(); } catch { }
        try { var (total, used) = SampleMemory(); row.MemTotalMb = total; row.MemUsedMb = used; } catch { }
        try { var (total, free) = SampleDisk(); row.DiskTotalGb = total; row.DiskFreeGb = free; } catch { }
        try { row.UptimeSec = Environment.TickCount64 / 1000; } catch { }
        try { row.ArgusVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? ""; } catch { }
        row.LastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        row.UpdatedAt = row.LastSeen;
        return row;
    }

    public DeviceFctRow CollectFct()
    {
        var row = new DeviceFctRow { Machine = _machine };
        try
        {
            var iniPath = _cfg.FctIniPath;
            var data = FctIni.Parse(iniPath);
            row.IniPath = data.IniPath;
            row.Found = data.Found;
            row.Error = data.Error;
            row.Models = data.Models.ToList();
            row.FwVersions = data.FwVersions.ToList();
            row.Devices = data.Devices.Select(d => new FctDeviceInfo { Name = d.Name, Port = d.Port, Type = d.Type, Online = d.Online }).ToList();
            row.A2lFiles = data.A2lFiles.ToList();
        }
        catch (Exception ex)
        {
            row.Found = false;
            row.Error = ex.Message;
        }
        row.LastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        row.UpdatedAt = row.LastSeen;
        return row;
    }

    private static string ResolveIp()
    {
        try
        {
            var host = Dns.GetHostName();
            var entry = Dns.GetHostEntry(host);
            foreach (var ip in entry.AddressList)
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    return ip.ToString();
            foreach (var ip in entry.AddressList)
                if (!IPAddress.IsLoopback(ip)) return ip.ToString();
        }
        catch { }
        return "";
    }

    private static string ResolveMac()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var addr = nic.GetPhysicalAddress();
                var bytes = addr.GetAddressBytes();
                if (bytes.Length == 0) continue;
                return string.Join(":", bytes.Select(b => b.ToString("X2")));
            }
        }
        catch { }
        return "";
    }

    private static (string model, int cores) ResolveCpuModel()
    {
        int cores = Environment.ProcessorCount;
        string model = "";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                var name = key?.GetValue("ProcessorNameString") as string;
                if (!string.IsNullOrWhiteSpace(name)) model = name.Trim();
            }
        }
        catch { }
        if (string.IsNullOrEmpty(model))
        {
            model = Environment.MachineName;
        }
        return (model, cores);
    }

    private double SampleCpu()
    {
        lock (_cpuLock)
        {
            try
            {
                var proc = Process.GetCurrentProcess();
                var nowTicks = Environment.TickCount64;
                var nowProc = proc.TotalProcessorTime;
                var elapsedMs = nowTicks - _lastProcessTicks;
                if (elapsedMs <= 0) return 0;
                var procMs = (nowProc - _lastProcTime).TotalMilliseconds;
                var cpu = procMs / elapsedMs / Environment.ProcessorCount * 100.0;
                _lastProcessTicks = nowTicks;
                _lastProcTime = nowProc;
                if (double.IsNaN(cpu) || double.IsInfinity(cpu)) return 0;
                return Math.Clamp(Math.Round(cpu, 1), 0, 100);
            }
            catch { return 0; }
        }
    }

    private static (int totalMb, int usedMb) SampleMemory()
    {
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            long totalBytes = gcInfo.TotalAvailableMemoryBytes;
            if (totalBytes <= 0)
            {
                var proc = Process.GetCurrentProcess();
                long used = proc.WorkingSet64;
                return (0, (int)(used / 1024 / 1024));
            }
            long usedBytes = GC.GetTotalMemory(false);
            try { usedBytes = Process.GetCurrentProcess().WorkingSet64; } catch { }
            int totalMb = (int)(totalBytes / 1024 / 1024);
            int usedMb = (int)(usedBytes / 1024 / 1024);
            return (totalMb, usedMb);
        }
        catch { return (0, 0); }
    }

    private static (double totalGb, double freeGb) SampleDisk()
    {
        try
        {
            var root = Path.GetPathRoot(AppConfig.BaseDir) ?? "C:\\";
            var drive = new DriveInfo(root);
            if (!drive.IsReady) drive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.DriveType == DriveType.Fixed) ?? drive;
            if (!drive.IsReady) return (0, 0);
            double total = Math.Round(drive.TotalSize / 1024.0 / 1024 / 1024, 2);
            double free = Math.Round(drive.AvailableFreeSpace / 1024.0 / 1024 / 1024, 2);
            return (total, free);
        }
        catch { return (0, 0); }
    }

    private static string SerializeDeviceInfo(DeviceInfoRow r)
    {
        var payload = new Dictionary<string, object?>
        {
            ["machine"] = r.Machine,
            ["hostname"] = r.Hostname,
            ["os"] = r.Os,
            ["os_version"] = r.OsVersion,
            ["ip"] = r.Ip,
            ["mac"] = r.Mac,
            ["cpu_model"] = r.CpuModel,
            ["cpu_cores"] = r.CpuCores,
            ["cpu_usage"] = r.CpuUsage,
            ["mem_total_mb"] = r.MemTotalMb,
            ["mem_used_mb"] = r.MemUsedMb,
            ["disk_total_gb"] = r.DiskTotalGb,
            ["disk_free_gb"] = r.DiskFreeGb,
            ["uptime_sec"] = r.UptimeSec,
            ["argus_version"] = r.ArgusVersion,
            ["ts"] = r.LastSeen,
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string SerializeFct(DeviceFctRow r)
    {
        var payload = new Dictionary<string, object?>
        {
            ["machine"] = r.Machine,
            ["ini_path"] = r.IniPath,
            ["found"] = r.Found,
            ["error"] = r.Error,
            ["models"] = r.Models,
            ["fw_versions"] = r.FwVersions.Select(x => new Dictionary<string, string> { ["label"] = x.Label, ["version"] = x.Version }).ToList(),
            ["devices"] = r.Devices.Select(d => new Dictionary<string, object> { ["name"] = d.Name, ["port"] = d.Port, ["type"] = d.Type, ["online"] = d.Online }).ToList(),
            ["a2l_files"] = r.A2lFiles.Select(x => new Dictionary<string, string> { ["label"] = x.Label, ["file"] = x.File }).ToList(),
            ["ts"] = r.LastSeen,
        };
        return JsonSerializer.Serialize(payload);
    }
}

using Microsoft.Win32;

namespace FctAggregator;

public class DeviceInfo
{
    public string Name { get; set; } = "";
    public string Port { get; set; } = "";
    public string Type { get; set; } = "com";
    public bool Online { get; set; }
}

public class FctIniData
{
    public bool Found { get; set; }
    public string IniPath { get; set; } = "";
    public string? Error { get; set; }
    public List<string> Models { get; set; } = new();
    public List<(string Label, string Version)> FwVersions { get; set; } = new();
    public List<DeviceInfo> Devices { get; set; } = new();
    public List<(string Label, string File)> A2lFiles { get; set; } = new();
    public DateTime ProbedAt { get; set; }
    /// <summary>系统里有、但 FCT.ini 未登记的 COM 数量（不进设备卡，避免「未知设备」刷屏）。</summary>
    public int ExtraSystemComCount { get; set; }
}

public static class FctIni
{
    private static readonly object _metaLock = new();
    private static string _metaPath = "";
    private static long _metaWrite;
    private static long _metaLen;
    private static FctIniData? _meta;
    private static FctIniData? _miss;
    private static DateTime _missUntil;

    // COM 在线探测缓存：注册表 + 串口枚举是内核调用，而 UI 每秒（设备页 200ms）都会问一次
    private static HashSet<string>? _comCache;
    private static DateTime _comCacheUntil;
    private static readonly object _comLock = new();

    public static void Invalidate()
    {
        lock (_metaLock)
        {
            _meta = null;
            _metaPath = "";
            _miss = null;
            _missUntil = default;
        }
        lock (_comLock) { _comCache = null; }
        lock (_autoLock) { _autoMissUntil = default; }   // 允许手动"重新探测"再扫一次
    }

    public static HashSet<string> GetActiveComPorts()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key != null)
                foreach (var name in key.GetValueNames())
                {
                    var v = key.GetValue(name)?.ToString();
                    if (!string.IsNullOrEmpty(v)) set.Add(v.ToUpperInvariant());
                }
        }
        catch { }
        return set;
    }

    public static HashSet<string> ScanSystemComPorts()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in System.IO.Ports.SerialPort.GetPortNames())
                set.Add(p.ToUpperInvariant());
        }
        catch
        {
            foreach (var p in GetActiveComPorts()) set.Add(p);
        }
        return set;
    }

    /// <summary>登记口是否当前挂在系统上。注册表 + GetPortNames 并集，任一侧见到即在线；不打开串口（避免抢 FTS）。
    /// 结果缓存 2 秒：调用方（总览每秒 / 设备页每 200ms）频率远高于设备热插拔，没必要按那个频率做内核枚举。</summary>
    public static HashSet<string> ProbePresentComPorts()
    {
        lock (_comLock)
        {
            if (_comCache != null && DateTime.UtcNow < _comCacheUntil)
                return new HashSet<string>(_comCache, StringComparer.OrdinalIgnoreCase);
            var set = GetActiveComPorts();
            foreach (var p in ScanSystemComPorts()) set.Add(p);
            _comCache = set;
            _comCacheUntil = DateTime.UtcNow.AddSeconds(2);
            return new HashSet<string>(set, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static IEnumerable<string> CandidatePaths(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
        yield return @"C:\FTS\Apps\PEU\Cfg\FCT.ini";
        yield return @"D:\FTS\Apps\PEU\Cfg\FCT.ini";
        yield return @"C:\FTS\Cfg\FCT.ini";
        yield return @"C:\FTS\FCT.ini";
    }

    private static string? _autoFound;
    private static DateTime _autoMissUntil;
    private static readonly object _autoLock = new();

    public static string? AutoFindIni()
    {
        lock (_autoLock)
        {
            if (_autoFound != null) return _autoFound;
            // 全盘深递归很贵，而调用方（每秒一次的 Snapshot）会反复触发：**失败结果也必须缓存**。
            // 否则 FCT.ini 路径一配错，就等于每 15 秒（LoadMeta 的 miss 缓存时长）全盘重扫一次。
            // 手动"重新探测"会走 Invalidate() 把这里清掉。
            if (DateTime.UtcNow < _autoMissUntil) return null;
        }

        foreach (var p in CandidatePaths(""))
            if (TryExists(p, out var real)) return CacheFound(real);

        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            var hit = SearchFtsTree(d.RootDirectory, 8);
            if (hit != null) return CacheFound(hit);
        }

        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            var hit = SearchShallow(d.RootDirectory, 5);
            if (hit != null) return CacheFound(hit);
        }

        lock (_autoLock) { _autoMissUntil = DateTime.UtcNow.AddMinutes(5); }
        return null;
    }

    private static string? CacheFound(string p)
    {
        lock (_autoLock) _autoFound ??= p;
        return _autoFound;
    }

    public static string? SearchFtsTree(DirectoryInfo root, int maxDepth = 8)
    {
        if (!root.Exists || maxDepth <= 0) return null;
        try
        {
            foreach (var f in root.EnumerateFiles())
                if (string.Equals(f.Name, "FCT.ini", StringComparison.OrdinalIgnoreCase)) return f.FullName;
            foreach (var d in root.EnumerateDirectories())
            {
                if (SkipDir(d.Name)) continue;
                var hit = SearchFtsTree(d, maxDepth - 1);
                if (hit != null) return hit;
            }
        }
        catch { }
        return null;
    }

    public static string? SearchShallow(DirectoryInfo root, int maxDepth)
    {
        if (!root.Exists || maxDepth <= 0) return null;
        try
        {
            foreach (var f in root.EnumerateFiles())
                if (string.Equals(f.Name, "FCT.ini", StringComparison.OrdinalIgnoreCase)) return f.FullName;
            if (maxDepth <= 1) return null;
            foreach (var d in root.EnumerateDirectories())
            {
                if (SkipDir(d.Name)) continue;
                var hit = SearchShallow(d, maxDepth - 1);
                if (hit != null) return hit;
            }
        }
        catch { }
        return null;
    }

    private static bool SkipDir(string name) => name.ToLowerInvariant() is
        "windows" or "$recycle.bin" or "system volume information" or "programdata" or
        "users" or "appdata" or "documents and settings" or "recovery" or "perflogs" or
        ".git" or "node_modules" or "msys64" or "cygwin64" or "windows kits" or "windowsapps";

    private static bool TryExists(string p, out string real)
    {
        real = "";
        try { if (File.Exists(p)) { real = p; return true; } }
        catch { }
        return false;
    }

    public static FctIniData Parse(string iniPath) => Snapshot(iniPath);

    /// <summary>INI 按 mtime 缓存；每次只重探 COM 在线。列表只含 FCT.ini 登记设备。</summary>
    public static FctIniData Snapshot(string iniPath)
    {
        var meta = LoadMeta(iniPath);
        var live = Clone(meta);
        if (!live.Found)
        {
            live.ProbedAt = DateTime.Now;
            return live;
        }
        var present = ProbePresentComPorts();
        int extra = 0;
        foreach (var p in present)
        {
            bool listed = false;
            foreach (var d in live.Devices)
            {
                if (d.Type == "com" && d.Port.Equals(p, StringComparison.OrdinalIgnoreCase))
                { listed = true; break; }
            }
            if (!listed) extra++;
        }
        live.ExtraSystemComCount = extra;
        foreach (var d in live.Devices)
        {
            if (d.Type == "com" || d.Port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                d.Online = present.Contains(d.Port.ToUpperInvariant());
            else
                d.Online = false;
        }
        live.ProbedAt = DateTime.Now;
        return live;
    }

    private static FctIniData Clone(FctIniData src)
    {
        var d = new FctIniData
        {
            Found = src.Found,
            IniPath = src.IniPath,
            Error = src.Error,
            Models = new List<string>(src.Models),
            FwVersions = new List<(string, string)>(src.FwVersions),
            A2lFiles = new List<(string, string)>(src.A2lFiles),
        };
        foreach (var x in src.Devices)
            d.Devices.Add(new DeviceInfo { Name = x.Name, Port = x.Port, Type = x.Type, Online = x.Online });
        return d;
    }

    private static FctIniData LoadMeta(string iniPath)
    {
        lock (_metaLock)
        {
            if (_miss != null && DateTime.UtcNow < _missUntil)
                return Clone(_miss);
        }

        var found = LocateIni(iniPath, out var diag, out var tried);
        if (found == null)
        {
            var miss = new FctIniData { IniPath = iniPath };
            var msg = "FCT.ini 未找到。已尝试常规路径与全盘自动识别:\n  " + string.Join("\n  ", tried);
            if (diag != null) msg += "\n\n诊断: " + diag;
            msg += "\n\n若测试软件装在非默认位置，请在 config.json 的 fct_ini_path 配置正确路径。";
            miss.Error = msg;
            lock (_metaLock)
            {
                _miss = miss;
                _missUntil = DateTime.UtcNow.AddSeconds(15);
            }
            Logger.Warning("[设备状态] FCT.ini 未找到" + (diag != null ? " | " + diag : ""));
            return miss;
        }

        long write = 0, len = 0;
        try
        {
            var fi = new FileInfo(found);
            write = fi.LastWriteTimeUtc.Ticks;
            len = fi.Length;
        }
        catch { }

        lock (_metaLock)
        {
            if (_meta != null && string.Equals(_metaPath, found, StringComparison.OrdinalIgnoreCase)
                && _metaWrite == write && _metaLen == len)
                return _meta;
        }

        var built = BuildMeta(found);
        lock (_metaLock)
        {
            _meta = built;
            _metaPath = found;
            _metaWrite = write;
            _metaLen = len;
            _miss = null;
        }
        Logger.Info($"[设备状态] 使用 FCT.ini: {found}（登记设备 {built.Devices.Count}）");
        return built;
    }

    private static string? LocateIni(string iniPath, out string? diag, out List<string> tried)
    {
        tried = new List<string>();
        diag = null;
        foreach (var p in CandidatePaths(iniPath))
        {
            tried.Add(p);
            try
            {
                if (File.Exists(p)) return p;
                var dir = Path.GetDirectoryName(p);
                if (dir != null && Directory.Exists(dir))
                {
                    try
                    {
                        var match = Directory.EnumerateFiles(dir, "*.ini")
                            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), "FCT.ini", StringComparison.OrdinalIgnoreCase));
                        if (match != null) return match;
                    }
                    catch (Exception exDir) { diag = $"目录枚举失败({dir}): {exDir.GetType().Name} {exDir.Message}"; }
                }
            }
            catch (UnauthorizedAccessException)
            {
                diag = $"无权限访问: {p}";
            }
            catch (Exception ex)
            {
                diag = $"检查失败({p}): {ex.GetType().Name} {ex.Message}";
            }
        }

        Logger.Info("[设备状态] 常规路径未命中，自动识别 FCT.ini…");
        var auto = AutoFindIni();
        if (auto != null) Logger.Info($"[设备状态] 自动识别到 FCT.ini: {auto}");
        return auto;
    }

    private static FctIniData BuildMeta(string iniPath)
    {
        var d = new FctIniData { IniPath = iniPath, Found = true };
        var sections = ParseIniFile(iniPath);

        if (sections.TryGetValue("Resource Name", out var rn))
        {
            foreach (var (key, val) in rn)
            {
                if (key == "8.2_SN")
                    d.Models = val.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                else if (key is "FW_Version_1" or "FW_Version_2" or "FW_Version_3")
                {
                    if (!string.IsNullOrWhiteSpace(val)) d.FwVersions.Add((key, val));
                }
                else
                {
                    var up = val.ToUpperInvariant();
                    if (up.StartsWith("COM"))
                        d.Devices.Add(new DeviceInfo { Name = key, Port = up, Type = "com" });
                    else if (up.StartsWith("USB"))
                        d.Devices.Add(new DeviceInfo { Name = key, Port = val, Type = "usb" });
                }
            }
        }

        if (sections.TryGetValue("A2L", out var a2l))
            foreach (var (key, val) in a2l)
                if (!key.StartsWith(";") && !string.IsNullOrWhiteSpace(val))
                    d.A2lFiles.Add((key, val));

        d.Devices = d.Devices
            .OrderBy(x => x.Type == "com" ? 0 : 1)
            .ThenBy(x => ComPortNum(x.Port))
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return d;
    }

    private static int ComPortNum(string p)
    {
        if (p.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && p.Length > 3
            && int.TryParse(p.AsSpan(3), out var n)) return n;
        return 9999;
    }

    private static Dictionary<string, List<(string, string)>> ParseIniFile(string path)
    {
        var result = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        var current = "";
        string[] lines;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs))
        {
            lines = sr.ReadToEnd().Replace("\r\n", "\n").Split('\n');
        }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";")) continue;
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                current = line[1..^1].Trim();
                if (!result.ContainsKey(current)) result[current] = new();
            }
            else if (current.Length > 0)
            {
                var eq = line.IndexOf('=');
                if (eq > 0)
                {
                    var key = line[..eq].Trim();
                    var val = line[(eq + 1)..].Trim();
                    result[current].Add((key, val));
                }
            }
        }
        return result;
    }
}

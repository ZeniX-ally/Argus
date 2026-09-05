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
}

public static class FctIni
{
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

    private static IEnumerable<string> CandidatePaths(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
        yield return @"C:\FTS\Apps\PEU\Cfg\FCT.ini";
        yield return @"D:\FTS\Apps\PEU\Cfg\FCT.ini";
        yield return @"C:\FTS\Cfg\FCT.ini";
        yield return @"C:\FTS\FCT.ini";
    }

    private static string? _autoFound;
    private static readonly object _autoLock = new();

    public static string? AutoFindIni()
    {
        lock (_autoLock) if (_autoFound != null) return _autoFound;

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

    public static FctIniData Parse(string iniPath)
    {
        var tried = new List<string>();
        string? found = null;
        string? diag = null;
        foreach (var p in CandidatePaths(iniPath))
        {
            tried.Add(p);
            try
            {
                if (File.Exists(p)) { found = p; break; }
                var dir = Path.GetDirectoryName(p);
                if (dir != null && Directory.Exists(dir))
                {
                    try
                    {
                        var match = Directory.EnumerateFiles(dir, "*.ini")
                            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), "FCT.ini", StringComparison.OrdinalIgnoreCase));
                        if (match != null) { found = match; break; }
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

        if (found == null)
        {
            Logger.Info("[设备状态] 常规路径未命中，自动识别 FCT.ini…");
            found = AutoFindIni();
            if (found != null) Logger.Info($"[设备状态] 自动识别到 FCT.ini: {found}");
        }

        var d = new FctIniData { IniPath = found ?? iniPath };
        if (found == null)
        {
            var msg = "FCT.ini 未找到。已尝试常规路径与全盘自动识别:\n  " + string.Join("\n  ", tried);
            if (diag != null) msg += "\n\n诊断: " + diag;
            msg += "\n\n若测试软件装在非默认位置，请在 config.json 的 fct_ini_path 配置正确路径。";
            d.Error = msg;
            Logger.Warning("[设备状态] FCT.ini 未找到" + (diag != null ? " | " + diag : ""));
            return d;
        }
        iniPath = found;
        Logger.Info($"[设备状态] 使用 FCT.ini: {iniPath}");
        var sections = ParseIniFile(iniPath);
        d.Found = true;

        var active = GetActiveComPorts();
        var systemPorts = ScanSystemComPorts();
        var iniComMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
                    if (up.StartsWith("COM")) iniComMap[up] = key;
                    else if (up.StartsWith("USB"))
                        d.Devices.Add(new DeviceInfo { Name = key, Port = val, Type = "usb", Online = false });
                }
            }
        }

        var allPorts = new HashSet<string>(systemPorts, StringComparer.OrdinalIgnoreCase);
        foreach (var p in iniComMap.Keys) allPorts.Add(p);
        var comDevices = allPorts
            .OrderBy(p => ComPortNum(p))
            .Select(p => new DeviceInfo
            {
                Name = iniComMap.TryGetValue(p, out var n) ? n : "(未知设备)",
                Port = p,
                Type = "com",
                Online = active.Contains(p),
            }).ToList();

        var usb = d.Devices.Where(x => x.Type == "usb").ToList();
        d.Devices = comDevices.Concat(usb).ToList();

        if (sections.TryGetValue("A2L", out var a2l))
            foreach (var (key, val) in a2l)
                if (!key.StartsWith(";") && !string.IsNullOrWhiteSpace(val))
                    d.A2lFiles.Add((key, val));

        return d;
    }

    private static int ComPortNum(string p)
    {
        if (p.Length > 3 && int.TryParse(p[3..], out var n)) return n;
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

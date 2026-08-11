using Microsoft.Win32;

namespace FctAggregator;

public class DeviceInfo
{
    public string Name { get; set; } = "";
    public string Port { get; set; } = "";
    public string Type { get; set; } = "com";  // com / usb
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

/// <summary>解析 FCT.ini + COM 口在线状态。</summary>
public static class FctIni
{
    /// <summary>当前活动 COM 口(注册表 SERIALCOMM)。</summary>
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

    /// <summary>系统所有 COM 口(SerialPort.GetPortNames, 无额外依赖)。</summary>
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
            // 回退到注册表活动口
            foreach (var p in GetActiveComPorts()) set.Add(p);
        }
        return set;
    }

    /// <summary>常见的 FCT.ini 候选路径(config 配的找不到时依次尝试)。</summary>
    private static IEnumerable<string> CandidatePaths(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
        yield return @"C:\FTS\Apps\PEU\Cfg\FCT.ini";
        yield return @"D:\FTS\Apps\PEU\Cfg\FCT.ini";
        yield return @"C:\FTS\Cfg\FCT.ini";
    }

    public static FctIniData Parse(string iniPath)
    {
        // 多路径自动查找 + 详细诊断
        var tried = new List<string>();
        string? found = null;
        string? diag = null;
        foreach (var p in CandidatePaths(iniPath))
        {
            tried.Add(p);
            try
            {
                if (File.Exists(p)) { found = p; break; }
                // File.Exists=false 可能是真不存在, 也可能是目录无权限枚举; 尝试列目录区分
                var dir = Path.GetDirectoryName(p);
                if (dir != null && Directory.Exists(dir))
                {
                    // 目录在, 但 File.Exists 说文件不在 -> 列一下目录看到底有啥(处理大小写/隐藏字符)
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

        var d = new FctIniData { IniPath = found ?? iniPath };
        if (found == null)
        {
            var msg = "FCT.ini 未找到。已尝试以下路径:\n  " + string.Join("\n  ", tried);
            if (diag != null) msg += "\n\n诊断: " + diag;
            msg += "\n\n请在 config.json 的 fct_ini_path 配置正确路径。";
            d.Error = msg;
            Logger.Warning("[设备状态] FCT.ini 未找到" + (diag != null ? " | " + diag : ""));
            return d;
        }
        iniPath = found;
        Logger.Info($"[设备状态] 使用 FCT.ini: {iniPath}");
        // 解析 ini 为 section -> (key,value) 列表(保留顺序)
        var sections = ParseIniFile(iniPath);
        d.Found = true;

        var active = GetActiveComPorts();
        var systemPorts = ScanSystemComPorts();
        var iniComMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // 端口 -> 设备名

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

        // 合并系统所有COM口 + ini配置的口
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

        // COM 在前, USB 在后
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

    /// <summary>解析 INI 文件为 section -> [(key,value)]。忽略 ; 注释行。</summary>
    private static Dictionary<string, List<(string, string)>> ParseIniFile(string path)
    {
        var result = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        var current = "";
        // 共享读取: 允许读取正被其他程序(如FCT测试软件)占用/写入的文件
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

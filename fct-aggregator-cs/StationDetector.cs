using System.Text.RegularExpressions;

namespace FctAggregator;

/// <summary>机台号(IP映射) + 型号识别。</summary>
public static class StationDetector
{
    private static readonly Dictionary<string, string> IpToStation = new()
    {
        ["172.28.55.11"] = "FCT1",
        ["172.28.55.12"] = "FCT2",
        ["172.28.55.13"] = "FCT3",
        ["172.28.55.14"] = "FCT4",
        ["172.28.55.15"] = "FCT5",
        ["172.28.55.16"] = "FCT6",
        ["172.28.55.18"] = "FCT7",
    };

    private static readonly Regex ModelRe = new(@"^E\d{7}$", RegexOptions.Compiled);

    public static bool IsValidModel(string name) => ModelRe.IsMatch(name);

    public static string? DetectStation()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var ip = ua.Address.ToString();
                    if (IpToStation.TryGetValue(ip, out var st))
                        return st;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>从 tester 字符串提取机台号 (含 FCTx)。</summary>
    public static string? ExtractStationFromTester(string? tester)
    {
        if (string.IsNullOrEmpty(tester)) return null;
        for (int i = 7; i >= 1; i--)
            if (Regex.IsMatch(tester, $@"\bFCT{i}\b") || tester.Contains($"FCT{i}"))
                return $"FCT{i}";
        return null;
    }
}

/// <summary>
/// 开机自启: 向"启动"文件夹放快捷方式(.lnk)。
/// 不写注册表 Run 键(那是杀毒敏感的病毒驻留手法)。启动文件夹是正常软件常用方式。
/// </summary>
public static class AutoStart
{
    private const string AppName = "FCT-Aggregator";

    private static string ExePath => Environment.ProcessPath ?? "";
    private static string DllPath => Path.Combine(AppConfig.BaseDir, "FCT-Aggregator.dll");
    private static bool IsRunningAsDll =>
        (Environment.ProcessPath ?? "").EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase);
    private static string MarkerPath => Path.Combine(AppConfig.BaseDir, "data", ".autostart_initialized");

    private static string StartupDir => Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    private static string ShortcutPath => Path.Combine(StartupDir, $"{AppName}.lnk");

    public static bool IsEnabled() => File.Exists(ShortcutPath);

    public static bool Enable()
    {
        try
        {
            // 快捷方式指向: 若用 dotnet 运行则指向 dotnet.exe + dll 参数; 若直接 exe 则指向 exe
            string target, args, workDir = AppConfig.BaseDir;
            if (IsRunningAsDll)
            {
                target = ExePath;          // dotnet.exe
                args = $"\"{DllPath}\"";
            }
            else
            {
                target = ExePath;          // 直接的 exe
                args = "";
            }
            if (string.IsNullOrEmpty(target)) return false;
            CreateShortcut(ShortcutPath, target, args, workDir);
            return File.Exists(ShortcutPath);
        }
        catch (Exception ex) { Logger.Error($"启用开机自启失败: {ex.Message}"); return false; }
    }

    public static bool Disable()
    {
        try
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            return true;
        }
        catch (Exception ex) { Logger.Error($"禁用开机自启失败: {ex.Message}"); return false; }
    }

    public static void EnsureFirstRun()
    {
        try
        {
            if (File.Exists(MarkerPath)) return;   // 非首次, 尊重用户设置
            var ok = Enable();
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, "1");
            Logger.Info(ok ? "首次运行: 已自动启用开机自启(启动文件夹快捷方式)" : "首次运行: 自动启用开机自启失败");
        }
        catch (Exception ex) { Logger.Error($"EnsureFirstRun 失败: {ex.Message}"); }
    }

    /// <summary>用 WScript.Shell COM(晚绑定, 无需编译期引用)创建 .lnk 快捷方式。</summary>
    private static void CreateShortcut(string lnkPath, string targetPath, string arguments, string workingDir)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) throw new InvalidOperationException("WScript.Shell 不可用");
        object? shell = Activator.CreateInstance(shellType);
        try
        {
            object? shortcut = shellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (shortcut == null) throw new InvalidOperationException("CreateShortcut 返回空");
            var st = shortcut.GetType();
            st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
            if (!string.IsNullOrEmpty(arguments))
                st.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
            st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
            st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "FCT-Aggregator 开机自启" });
            st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }
}

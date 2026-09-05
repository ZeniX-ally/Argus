using System.Text.RegularExpressions;

namespace FctAggregator;

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

    public static string? ExtractStationFromTester(string? tester)
    {
        if (string.IsNullOrEmpty(tester)) return null;
        for (int i = 7; i >= 1; i--)
            if (Regex.IsMatch(tester, $@"\bFCT{i}\b") || tester.Contains($"FCT{i}"))
                return $"FCT{i}";
        return null;
    }
}

public static class AutoStart
{
    private const string AppName = "Argus";

    private static string ExePath => Environment.ProcessPath ?? "";
    private static string DllPath => Path.Combine(AppConfig.BaseDir, "Argus.dll");
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
            string target, args, workDir = AppConfig.BaseDir;
            if (IsRunningAsDll)
            {
                target = ExePath;
                args = $"\"{DllPath}\"";
            }
            else
            {
                target = ExePath;
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
            if (File.Exists(MarkerPath)) return;
            var ok = Enable();
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, "1");
            Logger.Info(ok ? "首次运行: 已自动启用开机自启(启动文件夹快捷方式)" : "首次运行: 自动启用开机自启失败");
        }
        catch (Exception ex) { Logger.Error($"EnsureFirstRun 失败: {ex.Message}"); }
    }

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
            st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "Argus 开机自启" });
            st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }
}

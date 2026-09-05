using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FctAggregator;

public class AggDeployResult
{
    public bool ConfigOk = true;
    public bool FirewallOk;
    public bool AutoStartOk;
    public bool ServiceOk;
    public int Port = 8080;
    public string FirewallMsg = "";
    public string AutoStartMsg = "";
    public string ServiceMsg = "";
    public string NewToken = "";
    public List<string> Addresses = new();
    public bool FullSuccess => ConfigOk && ServiceOk;
}

public static class AggDeployer
{
    public static AggDeployResult Deploy()
    {
        var r = new AggDeployResult();
        try
        {
            var exePath = CurrentExePath();
            var configPath = Path.Combine(AppConfig.BaseDir, "config.json");

            if (File.Exists(configPath))
            {
                r.Port = ReadMeshPort(configPath);
            }
            else
            {
                File.WriteAllText(configPath, BuildDefaultConfigJson());
                r.Port = 8081;
                try
                {
                    var j = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
                    if (j.RootElement.TryGetProperty("agg_token", out var tv))
                        r.NewToken = tv.GetString() ?? "";
                }
                catch { }
            }
            r.ConfigOk = true;
            Logger.Info($"[聚合部署] config 就绪（port={r.Port}）");

            try
            {
                RunProcessWait("netsh.exe", "advfirewall firewall delete rule name=\"ArgusAggWeb\"");
                int code = RunProcessWait("netsh.exe",
                    "advfirewall firewall add rule name=\"ArgusAggWeb\" dir=in action=allow protocol=TCP" +
                    $" localport={r.Port}" +
                    " remoteip=10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,169.254.0.0/16" +
                    " profile=private,domain");
                if (code == 0)
                {
                    r.FirewallOk = true;
                    Logger.Info("[聚合部署] 防火墙规则 ArgusAggWeb 已就绪（仅内网网段 + private/domain）");
                }
                else
                {
                    r.FirewallMsg = $"netsh 退出码 {code}（需要管理员权限）";
                    Logger.Warning($"[聚合部署] 防火墙放行失败: {r.FirewallMsg}");
                }
            }
            catch (Exception ex)
            {
                r.FirewallMsg = ex.Message;
                Logger.Warning($"[聚合部署] 防火墙放行异常: {ex.Message}");
            }

            try
            {
                var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                var lnkPath = Path.Combine(startupDir, "Argus聚合服务.lnk");
                CreateShortcut(lnkPath, exePath, "agg --web", AppConfig.BaseDir);
                r.AutoStartOk = File.Exists(lnkPath);
                r.AutoStartMsg = r.AutoStartOk ? "" : "快捷方式未落盘";
            }
            catch (Exception ex)
            {
                r.AutoStartMsg = ex.Message;
                Logger.Warning($"[聚合部署] 开机自启失败: {ex.Message}");
            }

            try
            {
                using var svc = Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "agg --web",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppConfig.BaseDir,
                });
                r.ServiceOk = svc != null;
                r.ServiceMsg = svc == null ? "服务进程启动失败" : "";
            }
            catch (Exception ex)
            {
                r.ServiceMsg = ex.Message;
                Logger.Warning($"[聚合部署] 服务启动失败: {ex.Message}");
            }

            r.Addresses = LocalIPv4Addresses();
            Logger.Info($"[聚合部署] 完成: firewall={r.FirewallOk}, autostart={r.AutoStartOk}, service={r.ServiceOk}");
            return r;
        }
        catch (Exception ex)
        {
            Logger.Error($"[聚合部署] 部署异常: {ex.Message}");
            return r;
        }
    }

    public static string BuildDefaultConfigJson()
    {
        var cfg = new Dictionary<string, object?>
        {
            ["station_id"] = "AGG-NODE",
            ["results_root"] = @"D:\Results",
            ["mesh_port"] = 8081,
            ["peers"] = new List<string>(),
            ["agg_token"] = AppConfig.GenerateRandomToken(),
            ["agg_webhook_url"] = "",
            ["agg_summary_minutes"] = 60,
            ["log_level"] = "INFO",
        };
        return JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
    }

    public static bool IsAdmin()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static bool RelaunchAsAdmin()
    {
        try
        {
            var exe = CurrentExePath();
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "agg --install",
                WorkingDirectory = AppConfig.BaseDir,
                Verb = "runas",
                UseShellExecute = true,
            };
            return Process.Start(psi) != null;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[聚合部署] 提权重启失败（用户取消 UAC？）: {ex.Message}");
            return false;
        }
    }

    private static int ReadMeshPort(string configPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("mesh_port", out var v) &&
                v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var port) && port >= 1 && port <= 65535)
                return port;
        }
        catch { }
        return 8081;
    }

    private static string CurrentExePath()
    {
        var p = Environment.ProcessPath ?? "";
        if (Path.GetFileNameWithoutExtension(p).Equals("Argus", StringComparison.OrdinalIgnoreCase))
            return p;
        var loc = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
        return loc.Length > 0 ? loc : p;
    }

    private static int RunProcessWait(string fileName, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (p == null) return -1;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
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
            st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "Argus 聚合服务（开机自启）" });
            st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shell != null && Marshal.IsComObject(shell))
                Marshal.ReleaseComObject(shell);
        }
    }

    private static List<string> LocalIPv4Addresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var addr in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr))
                    list.Add(addr.ToString());
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[聚合部署] 枚举本机 IP 失败: {ex.Message}");
        }
        return list;
    }
}

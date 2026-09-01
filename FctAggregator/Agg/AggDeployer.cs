using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FctAggregator;

/// <summary>一键部署结果（每步是否成功 + 访问地址）。</summary>
public class AggDeployResult
{
    public bool ConfigOk = true;          // config.json 已就绪（生成或保留）
    public bool FirewallOk;               // 防火墙放行（需要管理员）
    public bool AutoStartOk;              // 开机自启快捷方式
    public bool ServiceOk;                // 聚合服务进程已启动
    public int Port = 8080;
    public string FirewallMsg = "";
    public string AutoStartMsg = "";
    public string ServiceMsg = "";
    /// <summary>本次部署新生成的随机 token（M-1：新装不再裸奔）；沿用现有 config 时为空。</summary>
    public string NewToken = "";
    public List<string> Addresses = new();
    /// <summary>是否完整成功（防火墙没放行也算"部分成功"，服务仍可本机用）。</summary>
    public bool FullSuccess => ConfigOk && ServiceOk;
}

/// <summary>
/// 聚合服务一键部署（v3.4.4 起内嵌在程序里，不再依赖外部 bat）。
///
/// 四步：config 生成（幂等）→ 防火墙放行（netsh，需管理员）→ 开机自启（启动文件夹快捷方式，
/// EDR 兼容不写注册表）→ 启动聚合服务（无窗口后台常驻）。
/// 由 <c>Argus.exe agg --install</c>（CLI）与主程序「聚合设置 → 一键成为聚合端」按钮共用；
/// GUI 按钮点击时若当前进程不是管理员，会以 runas 提权重启自身执行本部署。
/// </summary>
public static class AggDeployer
{
    /// <summary>执行一键部署并返回每步结果。不抛异常：失败进结果字段，调用方决定展示方式。</summary>
    public static AggDeployResult Deploy()
    {
        var r = new AggDeployResult();
        try
        {
            var exePath = CurrentExePath();
            var configPath = Path.Combine(AppConfig.BaseDir, "config.json");

            // ---- 步骤 1：config.json（幂等：已存在则保留） ----
            if (File.Exists(configPath))
            {
                r.Port = ReadMeshPort(configPath);
            }
            else
            {
                File.WriteAllText(configPath, BuildDefaultConfigJson());
                r.Port = 8081;
                // M-1 加固：新装配置即带随机 token（不再空 token 全网开放），部署窗口打印访问链接
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

            // ---- 步骤 2：防火墙放行（需管理员；失败只警告不中断） ----
            // M-2 加固：只对内网网段放行 + 限 private/domain 配置文件 —— 误连公用网络
            // （酒店/手机热点 Wi-Fi）时端口不再暴露给任意互联网主机。
            try
            {
                RunProcessWait("netsh.exe", "advfirewall firewall delete rule name=\"ArgusAggWeb\"");   // 清旧规则（不存在时失败属正常）
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

            // ---- 步骤 3：开机自启（启动文件夹快捷方式，不写注册表——EDR 兼容） ----
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

            // ---- 步骤 4：启动聚合服务（无窗口后台常驻） ----
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

            // ---- 步骤 5：访问地址 ----
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

    /// <summary>默认 Mesh 节点配置 JSON（首次生成用，v3.5.0 去中心化本机节点）。
    /// M-1 加固：agg_token 自动生成随机值（32 位 hex），部署后看板/推送均需该 token；
    /// 访问地址以 `?token=` 首次进入，之后由服务端 HttpOnly Cookie 接管。</summary>
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

    /// <summary>当前进程是否具有管理员权限（防火墙放行需要）。</summary>
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

    /// <summary>以管理员权限重启自身执行部署（触发 UAC）。成功启动返回 true。</summary>
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
                Verb = "runas",   // UAC 提权
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

    /// <summary>从现有 config.json 读 mesh_port（读不到/非法回落默认 8081）。</summary>
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

    /// <summary>当前进程对应的 Argus.exe 路径（dotnet 运行 DLL 时回落程序集路径）。</summary>
    private static string CurrentExePath()
    {
        var p = Environment.ProcessPath ?? "";
        if (Path.GetFileNameWithoutExtension(p).Equals("Argus", StringComparison.OrdinalIgnoreCase))
            return p;
        var loc = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
        return loc.Length > 0 ? loc : p;
    }

    /// <summary>同步运行外部命令，返回退出码（netsh 等系统工具用）。</summary>
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

    /// <summary>用 WScript.Shell COM(晚绑定)创建 .lnk 快捷方式（EDR 安全：启动文件夹而非注册表）。</summary>
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

    /// <summary>枚举本机 IPv4 非回环地址（打印浏览器访问地址用）；失败返回空表。</summary>
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

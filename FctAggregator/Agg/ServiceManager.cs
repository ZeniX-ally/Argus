using System.Diagnostics;

namespace FctAggregator;

/// <summary>
/// Windows Service 注册助手（P2-A 无头服务化，sc create 思路）。
/// 零第三方依赖，仅通过 sc.exe / net start/stop 与系统服务管理器交互；
/// 服务本体仍为 Argus.exe agg --web（Session 0 常驻，会话锁定不中断采集）；
/// 崩溃自愈靠服务配置 failure actions（sc failure 重启）。
/// </summary>
public static class ServiceManager
{
    public const string ServiceName = "ArgusAgg";
    public const string DisplayName = "Argus 聚合服务";
    public const string Description = "Argus FCT 聚合节点（Mesh 节点，无窗体常驻，采集+看板，自愈重启）";

    /// <summary>当前 exe 路径（与 AggDeployer 同逻辑，兼容 dotnet xxx.dll 启动）。</summary>
    private static string ExePath()
    {
        var p = Environment.ProcessPath ?? "";
        if (Path.GetFileNameWithoutExtension(p).Equals("Argus", StringComparison.OrdinalIgnoreCase)) return p;
        var loc = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
        return loc.Length > 0 ? loc : p;
    }

    private static string BinPath()
    {
        var exe = ExePath();
        // Windows Service 要求 binPath 带引号且含参数 agg --web
        return $"\"{exe}\" agg --web";
    }

    /// <summary>查询服务是否存在（sc query）。</summary>
    public static bool Exists()
    {
        try
        {
            var psi = new ProcessStartInfo("sc", $"query \"{ServiceName}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(3000);
            var outStr = p.StandardOutput.ReadToEnd();
            return outStr.IndexOf(ServiceName, StringComparison.OrdinalIgnoreCase) >= 0 && outStr.IndexOf("SERVICE_NAME", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    /// <summary>安装/注册服务（sc create + description + failure 重启）。需管理员。</summary>
    public static (bool ok, string msg) Install()
    {
        try
        {
            var bin = BinPath();
            // sc create "ArgusAgg" binPath= "\"C:\...\Argus.exe\" agg --web" start= auto DisplayName= "Argus 聚合服务"
            var createArgs = $"create \"{ServiceName}\" binPath= \"{bin}\" start= auto DisplayName= \"{DisplayName}\"";
            var (code, outStr, err) = RunSc(createArgs);
            if (code != 0 && !outStr.Contains("already exists", StringComparison.OrdinalIgnoreCase) && !err.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                return (false, $"sc create 失败({code}): {outStr} {err}".Trim());
            // 描述
            RunSc($"description \"{ServiceName}\" \"{Description}\"");
            // 失败重启：第一次/第二次/后续均 5000ms 后重启，重置周期 86400s（1 天）
            RunSc($"failure \"{ServiceName}\" reset= 86400 actions= restart/5000/restart/5000/restart/5000");
            // 失败后运行重启也 restart
            RunSc($"failureflag \"{ServiceName}\" 1");
            // 自启的快捷方式仍保留（双保险，服务未注册时仍可 via 启动文件夹）
            return (true, $"服务 {ServiceName} 已注册（binPath={bin}，auto start，失败 5s 重启）");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>卸载/删除服务（sc delete）。需先 stop，若不存在视为成功。</summary>
    public static (bool ok, string msg) Uninstall()
    {
        try
        {
            Stop();
            var (code, outStr, err) = RunSc($"delete \"{ServiceName}\"");
            if (code == 0) return (true, $"服务 {ServiceName} 已删除");
            if (outStr.Contains("does not exist", StringComparison.OrdinalIgnoreCase) || err.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                return (true, $"服务 {ServiceName} 不存在，无需删除");
            return (false, $"sc delete 失败({code}): {outStr} {err}".Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static (bool ok, string msg) Start()
    {
        try
        {
            var (code, outStr, err) = RunSc($"start \"{ServiceName}\"");
            if (code == 0) return (true, $"服务 {ServiceName} 启动中");
            if (outStr.Contains("already running", StringComparison.OrdinalIgnoreCase)) return (true, $"服务 {ServiceName} 已在运行");
            return (false, $"sc start 失败({code}): {outStr} {err}".Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static (bool ok, string msg) Stop()
    {
        try
        {
            var (code, outStr, err) = RunSc($"stop \"{ServiceName}\"");
            if (code == 0) return (true, $"服务 {ServiceName} 已停止");
            if (outStr.Contains("not started", StringComparison.OrdinalIgnoreCase) || outStr.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                return (true, outStr.Trim());
            return (false, $"sc stop 失败({code}): {outStr} {err}".Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static (bool ok, string msg) Status()
    {
        try
        {
            var (code, outStr, err) = RunSc($"query \"{ServiceName}\"");
            if (code != 0) return (false, $"服务 {ServiceName} 不存在或查询失败: {err}".Trim());
            return (true, outStr.Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static (int code, string stdout, string stderr) RunSc(string args)
    {
        var psi = new ProcessStartInfo("sc", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p == null) return (-1, "", "启动 sc 失败");
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit(10000);
        return (p.ExitCode, o, e);
    }
}

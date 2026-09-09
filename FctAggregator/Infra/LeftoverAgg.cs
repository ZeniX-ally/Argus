using System.Diagnostics;

namespace FctAggregator;

/// <summary>v3.31 已删聚合/无头服务。现场若 09-03 装过 sc 服务或防火墙规则，开机探测并告警，不自动删除。</summary>
public static class LeftoverAgg
{
    public static readonly string[] ServiceNames =
    {
        "ArgusAgg", "ArgusAggWeb", "ArgusHeadless", "ArgusAggService",
    };

    public const string FirewallRule = "ArgusAggWeb";

    public static List<string> Probe()
    {
        var hits = new List<string>();
        foreach (var name in ServiceNames)
        {
            var st = QueryService(name);
            if (st != null)
                hits.Add($"Windows 服务 {name} 状态={st}");
        }
        if (FirewallRuleExists(FirewallRule))
            hits.Add($"防火墙规则 {FirewallRule} 仍在");
        try
        {
            var cfgPath = Path.Combine(AppConfig.BaseDir, "config.json");
            if (File.Exists(cfgPath))
            {
                var t = File.ReadAllText(cfgPath);
                if (t.Contains("\"agg_", StringComparison.Ordinal) || t.Contains("agg_enabled", StringComparison.Ordinal))
                    hits.Add("config.json 仍含 agg_* 键（应用更新器白名单合并可丢掉）");
                if (t.Contains("parsers_path", StringComparison.Ordinal) && t.Contains("HookSpool", StringComparison.OrdinalIgnoreCase))
                    hits.Add("config.json parsers_path 指向 HookSpool（会劫持 P_/F_ 解析）");
            }
        }
        catch { }
        return hits;
    }

    public static void LogAtStartup()
    {
        var hits = Probe();
        if (hits.Count == 0)
        {
            Logger.Info("[残留] 未发现聚合 Windows 服务 / ArgusAggWeb 防火墙规则");
            return;
        }
        Logger.Warning("[残留] 发现聚合时代遗留（v3.31 起无头服务已删除，可 sc stop+delete / 删防火墙规则）：");
        foreach (var h in hits)
            Logger.Warning("  " + h);
    }

    /// <summary>服务不存在返回 null；存在返回 STATE 文本（RUNNING/STOPPED 等）。</summary>
    public static string? QueryService(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "query \"" + name.Replace("\"", "") + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            if (stdout.Contains("1060") || stdout.Contains("DOES_NOT_EXIST", StringComparison.OrdinalIgnoreCase))
                return null;
            if (stdout.IndexOf("SERVICE_NAME", StringComparison.OrdinalIgnoreCase) < 0)
                return null;
            foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = line.Trim();
                if (t.StartsWith("STATE", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    return parts.Length > 0 ? parts[^1] : "UNKNOWN";
                }
            }
            return "UNKNOWN";
        }
        catch { return null; }
    }

    public static bool FirewallRuleExists(string ruleName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "advfirewall firewall show rule name=\"" + ruleName.Replace("\"", "") + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            return stdout.Contains(ruleName, StringComparison.OrdinalIgnoreCase)
                && !stdout.Contains("No rules match", StringComparison.OrdinalIgnoreCase)
                && !stdout.Contains("没有与指定标准相匹配的规则");
        }
        catch { return false; }
    }
}

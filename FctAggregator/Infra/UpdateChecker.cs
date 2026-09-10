using System.IO.Compression;
using System.Text;

namespace FctAggregator;

public static class UpdateChecker
{
    private const string PromptedKey = "update_prompted_versions";
    private const string PendingKey  = "update_pending_zip";


    public static Version CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
        ?? new Version(0, 0, 0);

    public static Version? ParseZipVersion(string fileName)
    {
        var m = System.Text.RegularExpressions.Regex.Match(fileName,
            @"Argus[-_ ]v?(\d+\.\d+(?:\.\d+)?(?:\.\d+)?)(?:[-_ ]update)?\.zip",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success && Version.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    public static HashSet<Version> PromptedVersions(Database? db = null)
    {
        var set = new HashSet<Version>();
        try
        {
            var raw = (db ?? EngineDb()).GetMeta(PromptedKey);
            if (string.IsNullOrEmpty(raw)) return set;
            foreach (var s in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (Version.TryParse(s.Trim(), out var v)) set.Add(v);
        }
        catch {  }
        return set;
    }

    public static void MarkPrompted(Version ver, Database? db = null)
    {
        try
        {
            var d = db ?? EngineDb();
            var set = PromptedVersions(d);
            if (!set.Add(ver)) return;
            var joined = string.Join(",", set.OrderBy(v => v).Select(v => v.ToString()));
            d.SetMeta(PromptedKey, joined);
        }
        catch {  }
    }

    public static UpdateInfo? Scan(string? updateDir = null, Database? db = null)
    {
        try
        {
            var dir = ResolveScanDir(updateDir);
            if (!Directory.Exists(dir)) return null;
            var prompted = PromptedVersions(db);

            UpdateInfo? best = null;
            foreach (var f in Directory.EnumerateFiles(dir, "Argus-v*.zip", SearchOption.TopDirectoryOnly))
            {
                var ver = ParseZipVersion(Path.GetFileName(f));
                if (ver == null) continue;
                if (ver <= CurrentVersion) continue;
                if (prompted.Contains(ver)) continue;
                if (best == null || ver > best.Version)
                    best = new UpdateInfo { Version = ver, ZipPath = f };
            }
            return best;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[更新器] 扫描更新目录失败: {ex.Message}");
            return null;
        }
    }

    public static string ResolveScanDir(string? updateDir = null)
    {
        // 调用方显式指定目录优先（老接口/测试）；否则看只读源，未配源则回落本地 update_dir
        if (updateDir != null) return ResolveUpdateDir(updateDir);
        var src = AppConfig.Instance.UpdateSource;
        if (string.IsNullOrWhiteSpace(src)) return ResolveUpdateDir();
        return Path.IsPathRooted(src) ? src : Path.Combine(AppConfig.BaseDir, src);
    }

    public static string ResolveUpdateDir(string? updateDir = null)
    {
        var raw = updateDir ?? AppConfig.Instance.UpdateDir;
        return Path.IsPathRooted(raw) ? raw : Path.Combine(AppConfig.BaseDir, raw);
    }

    private static Database EngineDb()
    {
        var cfg = AppConfig.Instance;
        var dbName = string.IsNullOrEmpty(cfg.StationId) ? "fct" : cfg.StationId;
        var path = Path.Combine(AppConfig.BaseDir, "data", dbName + ".db");
        // 复用已打开的同一个库：Database 构造函数会跑整套建表/索引/迁移，只为读一个 meta 键不值得
        var cur = Database.Current;
        if (cur != null && string.Equals(cur.DbPath, path, StringComparison.OrdinalIgnoreCase)) return cur;
        return new Database(path);
    }

    public static string GetReleaseNotes(Version ver, string? updateDir = null)
    {
        try
        {
            var dir = ResolveScanDir(updateDir);
            var rel = Path.Combine(dir, "RELEASE.txt");
            if (!File.Exists(rel)) return "";
            var lines = File.ReadAllLines(rel, Encoding.UTF8);

            var target = ver.ToString(ver.Revision > 0 ? 4 : 3);
            var parts = target.Split('.');
            var escaped = string.Join(@"\.", parts.Select(p => System.Text.RegularExpressions.Regex.Escape(p)));
            var optionalTail = new System.Text.StringBuilder();
            for (int i = parts.Length - 1; i >= 1; i--)
            {
                if (parts[i] == "0") optionalTail.Append(@"(\.0)?");
                else break;
            }

            int start = -1;
            var headPat = $@"(^|[^0-9A-Za-z])[vV]?{escaped}{optionalTail}([^0-9A-Za-z]|$)";
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(t, headPat))
                { start = i; break; }
            }
            if (start < 0) return "";

            var sb = new StringBuilder();
            bool collecting = false;
            bool seenFeatures = false;
            for (int i = start + 1; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("====") || t.StartsWith("----"))
                {
                    if (collecting) break;
                    continue;
                }
                if (System.Text.RegularExpressions.Regex.IsMatch(t,
                        @"^\s*#{1,3}\s+[vV]?\d+\.\d+") ||
                    System.Text.RegularExpressions.Regex.IsMatch(t,
                        @"^[vV]?\d+\.\d+(\.\d+)?(\s|$)"))
                    break;
                if (!seenFeatures && (t.Contains("版本特点") || t.Contains("版本特性") || t == "特点"))
                { seenFeatures = true; continue; }
                if (seenFeatures)
                {
                    collecting = true;
                    sb.AppendLine(t.TrimEnd(' ', '　'));
                }
            }
            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[更新器] 读 RELEASE.txt 失败: {ex.Message}");
            return "";
        }
    }

    public static string StageUpdate(UpdateInfo info, Database? db = null)
    {
        if (!File.Exists(info.ZipPath))
            throw new FileNotFoundException("更新包不存在（可能已被移走）", info.ZipPath);

        var updatesRoot = ResolveUpdateDir();
        var verStr = info.Version.ToString(3);
        var stagingDir = Path.Combine(updatesRoot, "staging", verStr);
        var backupDir = Path.Combine(updatesRoot, "backup", verStr + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));

        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
        Directory.CreateDirectory(stagingDir);

        ZipFile.ExtractToDirectory(info.ZipPath, stagingDir, overwriteFiles: true);

        Directory.CreateDirectory(backupDir);
        var baseDir = AppConfig.BaseDir;
        if (Directory.Exists(Path.Combine(baseDir, "data")))
            CopyDir(Path.Combine(baseDir, "data"), Path.Combine(backupDir, "data"));
        var cfg = Path.Combine(baseDir, "config.json");
        if (File.Exists(cfg)) File.Copy(cfg, Path.Combine(backupDir, "config.json"), true);

        var d = db ?? EngineDb();
        d.SetMeta(PendingKey, info.ZipPath);

        Logger.Info($"[更新器] 更新 {verStr} 已暂存（{stagingDir}），备份在 {backupDir}，等待重启提交");
        return $"更新包 v{verStr} 已准备完成。\n程序将重启以完成安装（约几秒），期间不影响 data 目录数据。";
    }

    public static bool HasPendingUpdate(Database? db = null)
    {
        try { return !string.IsNullOrEmpty((db ?? EngineDb()).GetMeta(PendingKey)); }
        catch { return false; }
    }

    public static void ScheduleRestart(int delaySeconds = 3)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("无法定位当前程序路径");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c ping -n {delaySeconds + 1} 127.0.0.1 > nul & start \"\" \"{exe}\" --post-update",
            WorkingDirectory = AppConfig.BaseDir,
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        System.Diagnostics.Process.Start(psi);
        Logger.Info($"[更新器] 已排定 {delaySeconds}s 后自动重启并完成升级提交");
    }

    /// <summary>把暂存目录内容换装入 baseDir：未锁定文件直接覆盖；被运行进程锁定的文件
    /// （Argus.exe/Argus.dll/本地原生库等）先改名为 *.old（NTFS 允许重命名已加载映像）再覆盖。
    /// config.json 跳过（由 MergeConfig 站点合并，不整份覆盖）。</summary>
    public static void SwapIn(string stagingDir, string baseDir)
    {
        foreach (var f in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(stagingDir, f);
            if (rel.Equals("config.json", StringComparison.OrdinalIgnoreCase)) continue;
            var dest = Path.Combine(baseDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            try
            {
                File.Copy(f, dest, overwrite: true);
            }
            catch (IOException)
            {
                // 目标被占用（本进程已加载）：旧文件让位改名后重试覆盖；*.old 由下次启动 CleanupOldBinaries 清理
                var old = dest + ".old";
                try { if (File.Exists(old)) File.Delete(old); } catch { }
                File.Move(dest, old);
                File.Copy(f, dest, overwrite: true);
            }
        }
    }

    /// <summary>清理上次提交更新遗留的 *.old 旧映像（旧进程退出后锁即释放；失败忽略下次再清）。</summary>
    public static void CleanupOldBinaries()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppConfig.BaseDir, "*.old", SearchOption.TopDirectoryOnly))
            {
                try { File.Delete(f); Logger.Info($"[更新器] 已清理旧映像 {Path.GetFileName(f)}"); } catch { }
            }
        }
        catch { }
    }

    /// <summary>提交待更新：把暂存内容换装入程序目录并合并站点配置。
    /// 返回 true=已提交或本无待更新任务；false=提交失败（pending 保留，等待下次启动/检测重试）。
    /// 由"旧进程在退出前自行换装"调用（审计 C4：新进程无法覆盖自己已加载的映像文件）。</summary>
    public static bool TryCommitPending(Database? db = null)
    {
        Database d;
        try { d = db ?? EngineDb(); }
        catch { return false; }

        var pendingZip = d.GetMeta(PendingKey);
        if (string.IsNullOrEmpty(pendingZip)) return true; // 本无待更新任务，视为成功（可放心重启）

        var updatesRoot = ResolveUpdateDir();
        var ver = ParseZipVersion(Path.GetFileName(pendingZip));
        var verStr = ver?.ToString(3) ?? "unknown";
        var stagingDir = Path.Combine(updatesRoot, "staging", verStr);
        var backupDir = Directory.EnumerateDirectories(Path.Combine(updatesRoot, "backup"), verStr + "_*")
                                 .OrderByDescending(x => x).FirstOrDefault();
        var baseDir = AppConfig.BaseDir;

        try
        {
            if (!Directory.Exists(stagingDir))
            {
                d.SetMeta(PendingKey, "");
                Logger.Warning($"[更新器] 暂存目录丢失（{stagingDir}），已清除待更新标记");
                return true;
            }

            SwapIn(stagingDir, baseDir);
            MergeConfig(Path.Combine(stagingDir, "config.json"), Path.Combine(baseDir, "config.json"));

            try { Directory.Delete(stagingDir, true); } catch { }
            if (backupDir != null) try { Directory.Delete(backupDir, true); } catch { }
            d.SetMeta(PendingKey, "");
            Logger.Info($"[更新器] 已提交更新 v{verStr}，程序文件已替换");
            return true;
        }
        catch (Exception ex)
        {
            // 失败不再清除 pending（旧实现"标记已提示+清 pending"=该版本永久静默）：保留现场等待重试
            Logger.Error($"[更新器] 提交更新失败（pending 保留，将自动重试）: {ex.Message}");
            return false;
        }
    }

    /// <summary>启动时提交路径（--post-update / 检测到 pending）的兼容入口。</summary>
    public static void CommitPendingUpdate(Database? db = null)
    {
        if (!TryCommitPending(db))
            Logger.Warning("[更新器] 提交未完成，待更新标记保留，下次启动/检测时重试");
    }

    private static void MergeConfig(string pkgCfg, string siteCfg)
    {
        if (!File.Exists(pkgCfg)) return;
        System.Text.Json.JsonDocument pkg;
        try { pkg = System.Text.Json.JsonDocument.Parse(File.ReadAllText(pkgCfg)); }
        catch { return; }

        var site = new Dictionary<string, System.Text.Json.JsonElement>();
        if (File.Exists(siteCfg))
        {
            try
            {
                using var s = System.Text.Json.JsonDocument.Parse(File.ReadAllText(siteCfg));
                if (s.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var p in s.RootElement.EnumerateObject())
                        site[p.Name] = p.Value.Clone();
            }
            catch {  }
        }

        // 与 tools/deploy_update.ps1 的 carryKeys 保持一致（v3.31.1 审计定稿的现场保留键），
        // 避免自动更新与手工部署行为分叉：agg_token/聚合遗留键不保留，现场调参键（analyze_collect_pass/fct_ini_path 等）不丢失。
        var keep = new[] {
            "station_id", "results_root", "webhook_url", "feishu_banner_img_key",
            "fct_ini_path", "log_level", "todo_scan_days", "db_maintenance_hour",
            "notify_min_interval_sec", "desktop_notify", "auto_start",
            "learn_resource_sampling_enabled", "analyze_collect_pass",
            "update_source",
        };
        var used = new HashSet<string>();
        using var stream = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (var p in pkg.RootElement.EnumerateObject())
            {
                var name = p.Name;
                used.Add(name);
                if (Array.IndexOf(keep, name) >= 0 && site.TryGetValue(name, out var siteVal))
                {
                    w.WritePropertyName(name);
                    siteVal.WriteTo(w);
                }
                else
                {
                    w.WritePropertyName(name);
                    w.WriteRawValue(p.Value.GetRawText(), skipInputValidation: true);
                }
            }
            foreach (var kv in site)
            {
                if (used.Contains(kv.Key)) continue;
                w.WritePropertyName(kv.Key);
                kv.Value.WriteTo(w);
            }
            w.WriteEndObject();
        }
        File.WriteAllText(siteCfg, Encoding.UTF8.GetString(stream.ToArray()), Encoding.UTF8);
    }

    private static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(src, dest));
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(f, f.Replace(src, dest), overwrite: true);
    }
}

public sealed class UpdateInfo
{
    public required Version Version { get; init; }
    public required string ZipPath { get; init; }
}

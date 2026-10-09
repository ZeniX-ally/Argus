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
            foreach (var f in Directory.EnumerateFiles(dir, "Argus*.zip", SearchOption.TopDirectoryOnly))
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
        // setAsCurrent:false —— 次级实例不得顶掉运行中的主库句柄（审计）
        return new Database(path, setAsCurrent: false);
    }

    public static string GetReleaseNotes(Version ver, string? updateDir = null)
    {
        try
        {
            var dir = ResolveScanDir(updateDir);
            var rel = Path.Combine(dir, "RELEASE.txt");
            if (!File.Exists(rel)) return "";
            var lines = File.ReadAllLines(rel, Encoding.UTF8);

            // 审计修复：两段版本（如 3.49）没有 Build，ToString(>=3) 抛 ArgumentException → 发布说明读不出。
            var target = ver.Build < 0 ? ver.ToString(2) : ver.ToString(ver.Revision > 0 ? 4 : 3);
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
        // 审计修复：与 VersionText/TryCommitPending 同口径——两段版本没有 Build，ToString(3) 抛异常
        // 会让"发现新版本→暂存"必然失败（热升级永远装不上）。同文件的 VersionText 正是为此而写。
        var verStr = VersionText(info.Version);
        var stagingDir = Path.Combine(updatesRoot, "staging", verStr);
        var backupDir = Path.Combine(updatesRoot, "backup", verStr + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));

        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
        Directory.CreateDirectory(stagingDir);

        ZipFile.ExtractToDirectory(info.ZipPath, stagingDir, overwriteFiles: true);

        Directory.CreateDirectory(backupDir);
        var baseDir = AppConfig.BaseDir;
        // E11：备份的是运行中 SQLite 库。先 checkpoint 把 WAL 截断进主库再拷，
        // 否则回滚面可能缺近期提交（裸拷 .db 不含 -wal 里的事务）。
        var d = db ?? EngineDb();
        try { d.CheckpointTruncateForBackup(); }
        catch (Exception ex) { Logger.Warning($"[更新器] 备份前 WAL checkpoint 失败（继续备份，回滚面可能不含近期提交）: {ex.Message}"); }
        if (Directory.Exists(Path.Combine(baseDir, "data")))
            CopyDir(Path.Combine(baseDir, "data"), Path.Combine(backupDir, "data"));
        var cfg = Path.Combine(baseDir, "config.json");
        if (File.Exists(cfg)) File.Copy(cfg, Path.Combine(backupDir, "config.json"), true);

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
        var (fileName, args) = RestartCommand("--post-update");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c ping -n {delaySeconds + 1} 127.0.0.1 > nul & start \"\" \"{fileName}\" {args}",
            WorkingDirectory = AppConfig.BaseDir,
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        System.Diagnostics.Process.Start(psi);
        Logger.Info($"[更新器] 已排定 {delaySeconds}s 后自动重启并完成升级提交");
    }

    /// <summary>解析「重启自己」的启动目标。随包 启动.bat 以 `dotnet Argus.dll` 启动时
    /// Environment.ProcessPath 是 dotnet.exe——直接 `dotnet.exe --post-update` 起不来新实例
    /// （dotnet 把参数当自己的命令），换装已成功但程序静默消失。此时必须补上 DLL 路径。
    /// 纯函数（processPath 可注入），供自检断言。</summary>
    public static (string FileName, string Args) RestartCommand(string extraArgs, string? processPath = null)
    {
        var exe = processPath ?? Environment.ProcessPath ?? "";
        if (exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var dll = Path.Combine(AppConfig.BaseDir, "Argus.dll");
            return (exe, $"\"{dll}\" {extraArgs}".TrimEnd());
        }
        return (exe, extraArgs);
    }

    /// <summary>版本号安全文本化：两段版本（1.2）没有 Build，ToString(3) 会抛 ArgumentException。</summary>
    public static string VersionText(Version? ver)
    {
        if (ver == null) return "unknown";
        if (ver.Build >= 0) return ver.ToString(3);
        return ver.ToString(2);
    }

    /// <summary>更新包中**不得覆盖现场**的运行期路径：data/、logs/ 与数据库文件、config.json。
    /// 纯函数，供自检断言。</summary>
    public static bool IsRuntimePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        var p = relativePath.Replace('\\', '/').TrimStart('/');
        if (p.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith("logs/", StringComparison.OrdinalIgnoreCase)) return true;
        var name = Path.GetFileName(p);
        if (name.Equals("config.json", StringComparison.OrdinalIgnoreCase)) return true;
        return name.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把暂存目录内容换装入 baseDir：未锁定文件直接覆盖；被运行进程锁定的文件
    /// （Argus.exe/Argus.dll/本地原生库等）先改名为 *.old（NTFS 允许重命名已加载映像）再覆盖。
    /// config.json 与 data/logs/数据库一律跳过（现场数据不得被更新包覆盖）。</summary>
    public static void SwapIn(string stagingDir, string baseDir)
    {
        foreach (var f in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(stagingDir, f);
            if (IsRuntimePath(rel)) continue;
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

        var baseDir = AppConfig.BaseDir;

        try
        {
            // 审计：以下解析/枚举原先在 try 之外——backup 目录缺失抛 DirectoryNotFoundException、
            // 两段版本号 ToString(3) 抛 ArgumentException，都会逃出本方法破坏 bool 契约（"保留 pending 等重试"分支永不执行）。
            var updatesRoot = ResolveUpdateDir();
            var verStr = VersionText(ParseZipVersion(Path.GetFileName(pendingZip)));
            var stagingDir = Path.Combine(updatesRoot, "staging", verStr);
            var backupRoot = Path.Combine(updatesRoot, "backup");
            var backupDir = Directory.Exists(backupRoot)
                ? Directory.EnumerateDirectories(backupRoot, verStr + "_*").OrderByDescending(x => x).FirstOrDefault()
                : null;

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

    /// <summary>模板与现场 config 合并：双方都有的键保留现场值；模板没有的现场键除
    /// <see cref="ConfigKeepSiteOnlyKeys"/> 外丢弃（聚合时代遗留键）。纯函数，SelfTest 直测。</summary>
    public static readonly string[] ConfigKeepSiteOnlyKeys = { "auto_start", "fts" };

    public static string MergeConfigJson(string pkgJson, string siteJson)
    {
        using var pkg = System.Text.Json.JsonDocument.Parse(pkgJson);
        var site = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(siteJson))
        {
            try
            {
                using var s = System.Text.Json.JsonDocument.Parse(siteJson);
                if (s.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var p in s.RootElement.EnumerateObject())
                        site[p.Name] = p.Value.Clone();
            }
            catch { }
        }

        // 双方都有的键保留现场值（auto_update / feishu_* / learn_* / analyze_* 等调参不被模板盖掉）；
        // 模板没有的现场键仅保留 auto_start/fts（updater 消费），聚合遗留键丢弃。
        var keepSiteOnly = new HashSet<string>(ConfigKeepSiteOnlyKeys, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        using var stream = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (var p in pkg.RootElement.EnumerateObject())
            {
                var name = p.Name;
                used.Add(name);
                w.WritePropertyName(name);
                if (site.TryGetValue(name, out var siteVal))
                    siteVal.WriteTo(w);
                else
                    w.WriteRawValue(p.Value.GetRawText(), skipInputValidation: true);
            }
            foreach (var kv in site)
            {
                if (used.Contains(kv.Key) || !keepSiteOnly.Contains(kv.Key)) continue;
                w.WritePropertyName(kv.Key);
                kv.Value.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void MergeConfig(string pkgCfg, string siteCfg)
    {
        if (!File.Exists(pkgCfg)) return;
        string pkgJson;
        try { pkgJson = File.ReadAllText(pkgCfg); }
        catch { return; }
        var siteJson = File.Exists(siteCfg) ? File.ReadAllText(siteCfg) : "{}";
        string merged;
        try { merged = MergeConfigJson(pkgJson, siteJson); }
        catch { return; }
        File.WriteAllText(siteCfg, merged, Encoding.UTF8);
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

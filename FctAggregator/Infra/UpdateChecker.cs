using System.IO.Compression;
using System.Text;

namespace FctAggregator;

/// <summary>
/// 本地更新器（v3.6.0；v3.26.0 无感热升级）。
///
/// 机制（管理员把新包放进 data/updates 即可被检测）：
///   · 检测：扫描 <see cref="AppConfig.UpdateDir"/> 下的 <c>Argus-v{ver}-update.zip</c>，
///     只认版本号比当前程序集更新的包，且「已提示过」的不再弹（去重靠 app_meta）。
///   · 无感热升级（v3.26.0，config.auto_update 默认 true）：
///       Scan 发现新包 → StageUpdate 暂存+备份 → 托盘气泡告知 →
///       <see cref="ScheduleRestart"/> 派生分离进程等本进程退出后以 --post-update 拉起新版 →
///       启动时 <see cref="CommitPendingUpdate"/> 完成文件替换。全程零弹窗零人工。
///   · 弹窗模式（auto_update=false）：<see cref="UpdatePromptForm"/> 展示版本号 +
///     RELEASE.txt 版本特点，用户选「立即更新」/「稍后提醒」。
///   · 自包含两阶段更新（程序运行中 exe 被占用，不能就地覆盖）：
///       阶段1  StageUpdate：把 zip 解压到 data/updates/staging/&lt;ver&gt;/，
///               备份 data\ 与 config.json 到 data/updates/backup/&lt;ver&gt;+ts/，
///               写 app_meta.update_pending = zip 路径，提示「重启完成安装」。
///       阶段2  CommitPendingUpdate：**启动时最先执行**（此时旧 exe 已退出、文件未锁定），
///               把 staging 复制覆盖到程序目录，清理 staging/backup/pending，
///               完成升级。失败则回滚到备份并清 pending（绝不让机台卡在半更新状态）。
///   · 绝不动 data\ 现有数据库与 logs\；config.json 按包内模板合并（保留 station_id/results_root/webhook_url/agg_token）。
/// </summary>
public static class UpdateChecker
{
    // ==================== app_meta 键 ====================
    private const string PromptedKey = "update_prompted_versions";   // 已提示过的版本，逗号分隔
    private const string PendingKey  = "update_pending_zip";          // 已暂存、待重启提交的更新包路径

    // ==================== 版本与扫描 ====================

    /// <summary>当前运行版本（程序集版本，如 3.5.3）。</summary>
    public static Version CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
        ?? new Version(0, 0, 0);

    /// <summary>
    /// 解析 zip 文件名里的版本号。认 <c>Argus-v{ver}-update.zip</c>（不区分大小写），
    /// 也兼容 <c>Argus-v{ver}.zip</c>；解析失败返回 null。
    /// </summary>
    public static Version? ParseZipVersion(string fileName)
    {
        var m = System.Text.RegularExpressions.Regex.Match(fileName,
            @"Argus[-_ ]v?(\d+\.\d+(?:\.\d+)?(?:\.\d+)?)(?:[-_ ]update)?\.zip",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success && Version.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    /// <summary>当前已提示过的版本集合（app_meta）。</summary>
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
        catch { /* 记录失败不阻塞更新检测 */ }
        return set;
    }

    /// <summary>标记某版本已提示过（幂等：重复调用不重复记录）。</summary>
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
        catch { /* 记录失败不阻塞 */ }
    }

    /// <summary>
    /// 扫描更新目录，返回「应当提示」的更新信息。
    /// 规则：文件名为 Argus-v{ver}-update.zip、ver &gt; 当前版本、且该版本未被提示过。
    /// 有多个新包时取最高版本（一次只提示一个）。
    /// </summary>
    public static UpdateInfo? Scan(string? updateDir = null, Database? db = null)
    {
        try
        {
            var dir = ResolveUpdateDir(updateDir);
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

    /// <summary>更新目录绝对路径（config.update_dir 相对路径时拼到程序目录）。</summary>
    public static string ResolveUpdateDir(string? updateDir = null)
    {
        var raw = updateDir ?? AppConfig.Instance.UpdateDir;
        return Path.IsPathRooted(raw) ? raw : Path.Combine(AppConfig.BaseDir, raw);
    }

    private static Database EngineDb()
    {
        var cfg = AppConfig.Instance;
        var dbName = string.IsNullOrEmpty(cfg.StationId) ? "fct" : cfg.StationId;
        return new Database(Path.Combine(AppConfig.BaseDir, "data", dbName + ".db"));
    }

    // ==================== RELEASE.txt 版本特点 ====================

    /// <summary>
    /// 从更新目录的 RELEASE.txt 里取「版本特点」段。
    /// 识别形如 <c>v3.6.0</c> / <c>Argus 统一发布包 v3.6.0</c> / <c>## v3.6.0</c> 的标题行，
    /// 截到下一个版本标题或「====」分隔线为止。找不到返回空串。
    /// </summary>
    public static string GetReleaseNotes(Version ver, string? updateDir = null)
    {
        try
        {
            var dir = ResolveUpdateDir(updateDir);
            var rel = Path.Combine(dir, "RELEASE.txt");
            if (!File.Exists(rel)) return "";
            var lines = File.ReadAllLines(rel, Encoding.UTF8);

            var target = ver.ToString(ver.Revision > 0 ? 4 : 3); // 4.0.0 / 3.5.3.1
            // 转义版本号，但把「.0」尾段做成可选（RELEASE.txt 里可能写 v4 / v4.0 / v4.0.0）
            var parts = target.Split('.');
            var escaped = string.Join(@"\.", parts.Select(p => System.Text.RegularExpressions.Regex.Escape(p)));
            var optionalTail = new System.Text.StringBuilder();
            for (int i = parts.Length - 1; i >= 1; i--)      // 最多剥 3 次：4.0.0 -> 允许 4.0.0 / 4.0 / 4
            {
                if (parts[i] == "0") optionalTail.Append(@"(\.0)?");
                else break;
            }

            int start = -1;
            // 版本号两侧都要是「非字母数字」：避免把 v2.0.0 错配进 xv2.0.0y 之类，
            // 同时允许行首/空格/冒号等前缀（"Argus 统一发布包  v4.0.0"、"## v4.0.0"、"v4.0.0:"）
            var headPat = $@"(^|[^0-9A-Za-z])[vV]?{escaped}{optionalTail}([^0-9A-Za-z]|$)";
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                // 匹配："v4.0.0"、"Argus 统一发布包  v4.0.0"、"## v4.0.0"
                if (System.Text.RegularExpressions.Regex.IsMatch(t, headPat))
                { start = i; break; }
            }
            if (start < 0) return "";

            var sb = new StringBuilder();
            bool collecting = false;
            bool seenFeatures = false;   // 是否已见过「版本特点」小节标题（见到则只收它下面的行）
            for (int i = start + 1; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("====") || t.StartsWith("----"))
                {
                    if (collecting) break;          // 已进入正文区 → 分隔线 = 本节结束
                    continue;                       // 标题装饰段的分隔线 → 跳过
                }
                if (System.Text.RegularExpressions.Regex.IsMatch(t,
                        @"^\s*#{1,3}\s+[vV]?\d+\.\d+") ||
                    System.Text.RegularExpressions.Regex.IsMatch(t,
                        @"^[vV]?\d+\.\d+(\.\d+)?(\s|$)"))
                    break;                           // 到下一个版本标题
                // 优先找「版本特点」小节（RELEASE.txt 的标准结构），找到后只收它的内容
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

    // ==================== 阶段 1：暂存更新（弹窗里点「立即更新」）====================

    /// <summary>
    /// 把更新包暂存并备份，准备重启后提交。
    /// 返回给用户看的状态说明；异常会抛出由弹窗捕获提示。
    /// </summary>
    public static string StageUpdate(UpdateInfo info, Database? db = null)
    {
        if (!File.Exists(info.ZipPath))
            throw new FileNotFoundException("更新包不存在（可能已被移走）", info.ZipPath);

        var updatesRoot = ResolveUpdateDir();
        var verStr = info.Version.ToString(3);
        var stagingDir = Path.Combine(updatesRoot, "staging", verStr);
        var backupDir = Path.Combine(updatesRoot, "backup", verStr + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));

        // 1) 清旧 staging（同一版本重试时先删再解）
        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
        Directory.CreateDirectory(stagingDir);

        // 2) 解压更新包到 staging
        ZipFile.ExtractToDirectory(info.ZipPath, stagingDir, overwriteFiles: true);

        // 3) 备份现场 data\ 与 config.json（绝不让更新动现场数据；失败即回滚）
        Directory.CreateDirectory(backupDir);
        var baseDir = AppConfig.BaseDir;
        if (Directory.Exists(Path.Combine(baseDir, "data")))
            CopyDir(Path.Combine(baseDir, "data"), Path.Combine(backupDir, "data"));
        var cfg = Path.Combine(baseDir, "config.json");
        if (File.Exists(cfg)) File.Copy(cfg, Path.Combine(backupDir, "config.json"), true);

        // 4) 写 pending 标记（重启后提交的依据）
        var d = db ?? EngineDb();
        d.SetMeta(PendingKey, info.ZipPath);

        Logger.Info($"[更新器] 更新 {verStr} 已暂存（{stagingDir}），备份在 {backupDir}，等待重启提交");
        return $"更新包 v{verStr} 已准备完成。\n程序将重启以完成安装（约几秒），期间不影响 data 目录数据。";
    }

    /// <summary>当前是否有待重启提交的更新。</summary>
    public static bool HasPendingUpdate(Database? db = null)
    {
        try { return !string.IsNullOrEmpty((db ?? EngineDb()).GetMeta(PendingKey)); }
        catch { return false; }
    }

    /// <summary>
    /// 无感热升级（v3.26.0）：派生一个**分离的** cmd 中转进程——先等本进程退出
    /// （释放单实例互斥体与 exe 文件锁），再以 --post-update 拉起新实例
    /// （新实例启动时最先执行 <see cref="CommitPendingUpdate"/> 完成文件替换）。
    /// 必须用分离进程：本进程退出后自身线程随之消亡，无法再启动新版。
    /// </summary>
    public static void ScheduleRestart(int delaySeconds = 3)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("无法定位当前程序路径");
        // ping -n k ≈ 等 (k-1) 秒（比 timeout 更适合无窗口场景，无键盘交互副作用）
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

    // ==================== 阶段 2：启动时提交（Program 最先调用）====================

    /// <summary>
    /// 启动时提交暂存更新：把 staging 覆盖到程序目录。成功清理标记；
    /// 失败回滚备份并清标记，绝不让机台卡在半更新状态。幂等：无 pending 时直接返回。
    /// </summary>
    public static void CommitPendingUpdate(Database? db = null)
    {
        Database d;
        try { d = db ?? EngineDb(); }
        catch { return; }                       // 库都打不开就别折腾更新了

        var pendingZip = d.GetMeta(PendingKey);
        if (string.IsNullOrEmpty(pendingZip)) return;

        var updatesRoot = ResolveUpdateDir();
        var ver = ParseZipVersion(Path.GetFileName(pendingZip));
        var verStr = ver?.ToString(3) ?? "unknown";
        var stagingDir = Path.Combine(updatesRoot, "staging", verStr);
        var backupDir = Directory.EnumerateDirectories(Path.Combine(updatesRoot, "backup"), verStr + "_*")
                                 .OrderByDescending(x => x).FirstOrDefault();
        var baseDir = AppConfig.BaseDir;   // 提前到 try 外，回滚分支也要用

        try
        {
            if (!Directory.Exists(stagingDir))
            {
                // staging 丢失：清掉 pending，避免每次启动都试
                d.SetMeta(PendingKey, "");
                Logger.Warning($"[更新器] 暂存目录丢失（{stagingDir}），已清除待更新标记");
                return;
            }

            // 覆盖程序文件：跳过 config.json（稍后合并），其余全部覆盖
            foreach (var f in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(stagingDir, f);
                if (rel.Equals("config.json", StringComparison.OrdinalIgnoreCase)) continue;
                var dest = Path.Combine(baseDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(f, dest, overwrite: true);
            }
            // 合并 config.json：包内模板为基准，保留现场 station_id/results_root/webhook_url/agg_token
            MergeConfig(Path.Combine(stagingDir, "config.json"), Path.Combine(baseDir, "config.json"));

            // 清理 staging / 备份 / pending
            try { Directory.Delete(stagingDir, true); } catch { }
            if (backupDir != null) try { Directory.Delete(backupDir, true); } catch { }
            d.SetMeta(PendingKey, "");
            Logger.Info($"[更新器] 已提交更新 v{verStr}，程序文件已替换");
        }
        catch (Exception ex)
        {
            Logger.Error($"[更新器] 提交更新失败: {ex.Message}");
            // 回滚：把备份的 data\ 与 config.json 拷回去
            if (backupDir != null && Directory.Exists(backupDir))
            {
                try
                {
                    var bkData = Path.Combine(backupDir, "data");
                    if (Directory.Exists(bkData))
                        CopyDir(bkData, Path.Combine(baseDir, "data"));
                    var bkCfg = Path.Combine(backupDir, "config.json");
                    if (File.Exists(bkCfg)) File.Copy(bkCfg, Path.Combine(baseDir, "config.json"), true);
                }
                catch (Exception rex) { Logger.Error($"[更新器] 回滚失败: {rex.Message}"); }
            }
            d.SetMeta(PendingKey, "");       // 清标记，避免无限重试
        }
    }

    /// <summary>合并 config.json：包内模板为基准，保留现场 station_id/results_root/webhook_url/agg_token 与现场自定义字段。</summary>
    private static void MergeConfig(string pkgCfg, string siteCfg)
    {
        if (!File.Exists(pkgCfg)) return;
        System.Text.Json.JsonDocument pkg;
        try { pkg = System.Text.Json.JsonDocument.Parse(File.ReadAllText(pkgCfg)); }
        catch { return; }                                    // 包内模板损坏就不合并

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
            catch { /* 现场 config 损坏：以包内模板为准 */ }
        }

        var keep = new[] { "station_id", "results_root", "webhook_url", "agg_token" };
        var used = new HashSet<string>();
        using var stream = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (var p in pkg.RootElement.EnumerateObject())
            {
                var name = p.Name;
                used.Add(name);
                // 保留键：现场有值就写现场值（防升级把库名/凭据打没）；否则写包内模板值
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
            // 现场自定义字段（包内模板没有的）一并带上，避免升级丢配置
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

    // ==================== 工具 ====================

    private static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(src, dest));
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(f, f.Replace(src, dest), overwrite: true);
    }
}

/// <summary>更新信息：版本号 + 更新包路径。</summary>
public sealed class UpdateInfo
{
    public required Version Version { get; init; }
    public required string ZipPath { get; init; }
}

namespace FctDistributor;

/// <summary>执行分发：建目录 -> 复制/移动 -> 写文件时间戳 -> 写目录时间戳。</summary>
public sealed class Distributor
{
    public sealed class Options
    {
        public bool DryRun;
        public bool Overwrite;
        public bool Move;
        public bool StampFiles = true;
        public bool StampFolders = true;
        /// <summary>不新建任何目录：目标目录不存在就跳过（「只投放到已有目录」模式）。</summary>
        public bool NoCreateDirs;
    }

    public sealed class Report
    {
        public int Total;
        public int Copied;
        public int Skipped;          // 已存在且未勾选覆盖
        public int Failed;
        public int Invalid;          // 计划阶段就有 Error
        public int Warned;
        public int StampedFiles;
        public int Unchanged;        // 时间已正确、无需改写（大数据量重复跑时的主力）
        public int StampedDirs;
        public int TouchedDirs;
        public TimeSpan Elapsed;
        public long Bytes;

        public readonly Dictionary<string, int> ByStation = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> ByCategory = new(StringComparer.OrdinalIgnoreCase);
        public readonly SortedSet<string> Models = new(StringComparer.OrdinalIgnoreCase);
        public readonly SortedSet<string> Dates = new(StringComparer.Ordinal);
        public readonly List<string> Errors = new();
    }

    private readonly Random _rnd = new();

    /// <summary>逐条明细日志的上限（超过只报汇总，避免 UI 被日志刷死）。</summary>
    private const int MaxDetailLogs = 200;
    /// <summary>错误列表上限。</summary>
    private const int MaxErrors = 500;

    public Report Run(IReadOnlyList<PlanItem> items, Options o,
                      Action<string>? log = null,
                      Action<int, int>? progress = null,
                      CancellationToken ct = default)
    {
        var rep = new Report { Total = items.Count };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 目标日期目录 -> 内部最晚文件时间
        var dirLatest = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        int done = 0;
        foreach (var it in items)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            progress?.Invoke(done, items.Count);

            if (!it.Ok)
            {
                rep.Invalid++;
                rep.Errors.Add($"{it.FileName}: {it.Error}");
                log?.Invoke($"[无效] {it.FileName} -> {it.Error}");
                continue;
            }
            if (it.Skip)
            {
                rep.Skipped++;
                log?.Invoke($"[跳过] {it.Station} {it.DateDir} -> {it.SkipReason}");
                continue;
            }
            if (it.Warning != null) rep.Warned++;

            var targetDir = Path.GetDirectoryName(it.TargetPath)!;

            if (o.NoCreateDirs && !Directory.Exists(targetDir))
            {
                rep.Skipped++;
                log?.Invoke($"[跳过] 目录不存在（本模式不新建目录）: {it.Category}\\{it.Model}\\{it.DateDir}");
                continue;
            }

            if (File.Exists(it.TargetPath) && !o.Overwrite)
            {
                rep.Skipped++;
                log?.Invoke($"[跳过] 已存在: {it.Category}\\{it.Model}\\{it.DateDir}\\{it.FileName}");
                // 已存在的也纳入目录时间计算，保证目录时间正确
                Track(dirLatest, targetDir, it.Stamp);
                continue;
            }

            if (o.DryRun)
            {
                rep.Copied++;
                rep.Bytes += it.Size;
                Tally(rep, it);
                Track(dirLatest, targetDir, it.Stamp);
                log?.Invoke($"[预演] {it.Station} {it.DateDir} {it.FileName} -> {it.Category}\\{it.Model}\\{it.DateDir}\\");
                continue;
            }

            try
            {
                if (!o.NoCreateDirs) Directory.CreateDirectory(targetDir);
                if (it.IsGenerated)
                {
                    TemplateGenerator.Write(it.Gen!, it.TargetPath);
                    it.Size = new FileInfo(it.TargetPath).Length;
                }
                else if (o.Move) File.Move(it.SourcePath, it.TargetPath, overwrite: true);
                else File.Copy(it.SourcePath, it.TargetPath, overwrite: true);

                rep.Copied++;
                rep.Bytes += it.Size;
                Tally(rep, it);

                if (o.StampFiles)
                {
                    if (FileTimeUtil.SetFileTimes(it.TargetPath, it.Stamp, out var err))
                        rep.StampedFiles++;
                    else
                    {
                        rep.Errors.Add($"{it.FileName}: 设置时间戳失败 - {err}");
                        log?.Invoke($"[警告] 时间戳失败 {it.FileName}: {err}");
                    }
                }

                Track(dirLatest, targetDir, it.Stamp);
                log?.Invoke($"[{(it.IsGenerated ? "生成" : "OK")}] {it.Station} {it.DateDir} {it.Stamp:HH:mm:ss.fff} -> {it.Category}\\{it.Model}\\{it.DateDir}\\{it.FileName}");
            }
            catch (Exception ex)
            {
                rep.Failed++;
                rep.Errors.Add($"{it.FileName}: {ex.Message}");
                log?.Invoke($"[失败] {it.FileName}: {ex.Message}");
            }
        }

        // 目录时间戳（自下而上：日期目录 -> 型号目录 -> 类别目录）
        rep.TouchedDirs = dirLatest.Count;
        if (o.StampFolders && !o.DryRun)
        {
            // 先算好各级目录应有的时间（取子级最晚）
            var all = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var (dir, latest) in dirLatest)
            {
                var stamp = FileTimeUtil.DirStampFrom(latest, _rnd);
                Track(all, dir, stamp);
                // 向上冒泡（型号目录、类别目录）
                var p = Path.GetDirectoryName(dir);
                for (int i = 0; i < 2 && !string.IsNullOrEmpty(p); i++)
                {
                    Track(all, p, stamp);
                    p = Path.GetDirectoryName(p);
                }
            }
            // 深的先设，浅的后设（避免设置子目录时刷新父目录 mtime）
            rep.TouchedDirs = all.Count;
            foreach (var dir in all.Keys.OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar)))
            {
                if (!Directory.Exists(dir)) continue;
                if (FileTimeUtil.SetDirTimes(dir, all[dir], out var err)) rep.StampedDirs++;
                else rep.Errors.Add($"目录 {dir}: {err}");
            }
        }

        sw.Stop();
        rep.Elapsed = sw.Elapsed;
        return rep;
    }

    /// <summary>就地修复：只重设时间戳，不移动文件。用于已分发到机台后时间被覆盖的场景。</summary>
    public Report Restamp(string root, string[] exts, bool recursive, bool stampFolders,
                          Action<string>? log = null,
                          Action<int, int>? progress = null,
                          CancellationToken ct = default)
        => Restamp(new[] { root }, exts, recursive, stampFolders, false, null, log, progress, ct);

    /// <summary>
    /// 就地修复（**自选文件夹范围**）。为大数据量优化：
    ///   ① 枚举阶段也能取消（每 512 个文件查一次 token）
    ///   ② 日志/进度**限流**（否则每文件一次 BeginInvoke 会把 UI 消息队列打爆 -> 卡死、点不到中止）
    ///   ③ 时间已正确的文件直接跳过，不写盘
    ///   ④ 写盘时只 open 一次句柄
    ///   ⑤ 取消时返回**已完成的部分报告**，不丢结果
    /// </summary>
    /// <param name="dirs">要处理的文件夹（各自独立，可以只是其中几个日期目录）</param>
    /// <param name="recursive">是否连所列文件夹的子目录一起处理</param>
    /// <param name="bubbleParents">是否把目录时间向上冒泡到父目录（止于 bubbleRoot）</param>
    /// <param name="bubbleRoot">冒泡上界（只改它下面的目录，绝不碰它本身之上）</param>
    public Report Restamp(IReadOnlyList<string> dirs, string[] exts, bool recursive, bool stampFolders,
                          bool bubbleParents, string? bubbleRoot,
                          Action<string>? log = null,
                          Action<int, int>? progress = null,
                          CancellationToken ct = default)
    {
        var rep = new Report();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var extSet = new HashSet<string>(exts, StringComparer.OrdinalIgnoreCase);
        var tol = TimeSpan.FromSeconds(1);

        // ---------- ① 枚举（可取消 + 限流日志） ----------
        var opt = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<FileInfo>();
        var swScan = System.Diagnostics.Stopwatch.StartNew();
        long scanned = 0;
        try
        {
            foreach (var d in dirs)
            {
                var di = new DirectoryInfo(d);
                if (!di.Exists)
                {
                    rep.Errors.Add($"目录不存在: {d}");
                    log?.Invoke($"[跳过] 目录不存在: {d}");
                    continue;
                }
                foreach (var f in di.EnumerateFiles("*", opt))
                {
                    if ((++scanned & 0x1FF) == 0)          // 每 512 个查一次取消
                    {
                        ct.ThrowIfCancellationRequested();
                        if (swScan.ElapsedMilliseconds > 1500)
                        {
                            log?.Invoke($"[扫描] 已扫 {scanned:N0} 个文件，命中 {files.Count:N0} 个…");
                            swScan.Restart();
                        }
                    }
                    if (!extSet.Contains(f.Extension.TrimStart('.'))) continue;
                    if (seen.Add(f.FullName)) files.Add(f);   // 目录有嵌套时去重
                }
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            rep.Elapsed = sw.Elapsed;
            log?.Invoke($"[中止] 枚举阶段被取消（已扫 {scanned:N0} 个文件）");
            return rep;
        }

        files.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase));
        rep.Total = files.Count;
        log?.Invoke($"[范围] 文件夹 {dirs.Count} 个{(recursive ? "（含子目录）" : "")} -> " +
                    $"扫过 {scanned:N0} 个文件，待处理 {files.Count:N0} 个");
        progress?.Invoke(0, Math.Max(1, files.Count));

        // ---------- ② 逐文件设置（限流日志/进度 + 跳过已正确） ----------
        var dirLatest = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        int done = 0, detailLogged = 0;
        var swTick = System.Diagnostics.Stopwatch.StartNew();
        bool canceled = false;

        foreach (var f in files)
        {
            if ((done & 0xFF) == 0 && ct.IsCancellationRequested) { canceled = true; break; }
            done++;

            var dt = ExtractStampFromName(f.Name);
            if (dt == null)
            {
                rep.Skipped++;
                if (detailLogged < MaxDetailLogs) { log?.Invoke($"[跳过] 无法提取时间戳: {f.Name}"); detailLogged++; }
                continue;
            }

            Track(dirLatest, f.DirectoryName!, dt.Value);

            if (FileTimeUtil.TimesMatch(f, dt.Value, tol))
            {
                rep.Unchanged++;
                rep.Copied++;
                continue;                                   // 已经是对的，不写盘
            }

            if (FileTimeUtil.SetFileTimes(f, dt.Value, out var err))
            {
                rep.StampedFiles++;
                rep.Copied++;
                if (detailLogged < MaxDetailLogs)
                { log?.Invoke($"[OK] {dt.Value:yyyy-MM-dd HH:mm:ss.fff}  {f.Name}"); detailLogged++; }
            }
            else
            {
                rep.Failed++;
                if (rep.Errors.Count < MaxErrors) rep.Errors.Add($"{f.Name}: {err}");
                if (detailLogged < MaxDetailLogs) { log?.Invoke($"[失败] {f.Name}: {err}"); detailLogged++; }
            }

            // 限流：每 400ms 报一次进度 + 一行汇总（不是每文件都报）
            if (swTick.ElapsedMilliseconds >= 400)
            {
                progress?.Invoke(done, files.Count);
                log?.Invoke($"[进度] {done:N0}/{files.Count:N0}  已改 {rep.StampedFiles:N0}  " +
                            $"已正确 {rep.Unchanged:N0}  跳过 {rep.Skipped:N0}  失败 {rep.Failed:N0}");
                swTick.Restart();
            }
        }
        progress?.Invoke(done, Math.Max(1, files.Count));
        if (canceled) log?.Invoke($"[中止] 已处理 {done:N0}/{files.Count:N0} 个文件后停止");

        // ---------- ③ 目录时间 ----------
        rep.TouchedDirs = dirLatest.Count;
        if (stampFolders && !ct.IsCancellationRequested)
        {
            // 先算好每个目录应有的时间（内部最晚文件 + 随机 1~300s）
            var all = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var (dir, latest) in dirLatest)
                Track(all, dir, FileTimeUtil.DirStampFrom(latest, _rnd));

            // 可选：向上冒泡到父目录（型号 / 类别 / …），止于 bubbleRoot
            if (bubbleParents)
            {
                var stop = string.IsNullOrWhiteSpace(bubbleRoot)
                    ? null : Path.GetFullPath(bubbleRoot!).TrimEnd(Path.DirectorySeparatorChar);
                foreach (var (dir, stamp) in all.ToList())
                {
                    var p = Path.GetDirectoryName(dir);
                    while (!string.IsNullOrEmpty(p))
                    {
                        var norm = Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar);
                        if (stop != null &&
                            !norm.StartsWith(stop, StringComparison.OrdinalIgnoreCase)) break;
                        Track(all, p, stamp);
                        if (stop != null && norm.Equals(stop, StringComparison.OrdinalIgnoreCase)) break;
                        p = Path.GetDirectoryName(p);
                    }
                }
            }

            rep.TouchedDirs = all.Count;
            // 深的先设，浅的后设（避免设子目录时又刷新父目录 mtime）
            foreach (var dir in all.Keys.OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar)))
            {
                if (!Directory.Exists(dir)) continue;
                if (FileTimeUtil.SetDirTimes(dir, all[dir], out var err)) rep.StampedDirs++;
                else if (rep.Errors.Count < MaxErrors) rep.Errors.Add($"目录 {dir}: {err}");
            }
        }

        sw.Stop();
        rep.Elapsed = sw.Elapsed;
        log?.Invoke($"[汇总] 文件 {rep.Total:N0}：已改 {rep.StampedFiles:N0}，" +
                    $"本来就对 {rep.Unchanged:N0}，跳过 {rep.Skipped:N0}，失败 {rep.Failed:N0}，" +
                    $"目录 {rep.StampedDirs}/{rep.TouchedDirs}，耗时 {rep.Elapsed.TotalSeconds:F1}s");
        return rep;
    }

    /// <summary>从文件名中任意位置的 14~17 位数字提取时间戳（SN 等无法解析的自动忽略）。</summary>
    public static DateTime? ExtractStampFromName(string name)
    {
        DateTime? best = null;
        int bestLen = 0;
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(name, @"\d{14,17}"))
        {
            var s = m.Value;
            foreach (var len in new[] { 17, 15, 14 })
            {
                if (s.Length < len) continue;
                var dt = NameParser.ParseStamp(s[..len]);
                if (dt == null) continue;
                if (dt.Value.Year is < 1990 or > 2100) continue;
                if (len > bestLen) { best = dt; bestLen = len; }
                break;
            }
        }
        return best;
    }

    private static void Tally(Report rep, PlanItem it)
    {
        rep.ByStation[it.Station] = rep.ByStation.GetValueOrDefault(it.Station) + 1;
        rep.ByCategory[it.Category] = rep.ByCategory.GetValueOrDefault(it.Category) + 1;
        rep.Models.Add(it.Model);
        rep.Dates.Add(it.DateDir);
    }

    private static void Track(Dictionary<string, DateTime> map, string key, DateTime dt)
    {
        if (!map.TryGetValue(key, out var cur) || dt > cur) map[key] = dt;
    }
}

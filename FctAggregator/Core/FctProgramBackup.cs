using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace FctAggregator;

public sealed class FctProgramBackupRecord
{
    public long Id { get; init; }
    public string CreatedAt { get; init; } = "";
    public string ZipPath { get; init; } = "";
    public string SourceRoot { get; init; } = "";
    public long ZipBytes { get; init; }
    public int FileCount { get; init; }
    public string ZipSha256 { get; init; } = "";
    public string StationId { get; init; } = "";
    public string Trigger { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Note { get; init; }
    public bool ZipExists { get; init; }
}

public sealed class FctProgramBackupResult
{
    public bool Ok;
    public string Message = "";
    public FctProgramBackupRecord? Record;
}

/// <summary>将 C:\FTS 测试程序目录高压缩备份到 D:\backup，并落库留痕。</summary>
public static class FctProgramBackup
{
    public const string TriggerManual = "manual";
    public const string TriggerScheduled = "scheduled";

    private static readonly HashSet<string> SkipFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Thumbs.db", "desktop.ini",
    };

    public static FctProgramBackupResult Run(Database db, AppConfig cfg, string stationId, string trigger = TriggerManual)
    {
        var result = new FctProgramBackupResult();
        if (!cfg.FctProgramBackupEnabled)
        {
            result.Message = "测试程序备份已关闭（fct_program_backup_enabled=false）";
            return result;
        }

        var source = Path.GetFullPath(cfg.FctProgramSourceRoot.TrimEnd('\\', '/'));
        var destDir = Path.GetFullPath(cfg.FctProgramBackupDir.TrimEnd('\\', '/'));

        if (!Directory.Exists(source))
        {
            result.Message = $"测试程序目录不存在: {source}";
            Logger.Warning($"[FTS备份] {result.Message}");
            return result;
        }

        try { Directory.CreateDirectory(destDir); }
        catch (Exception ex)
        {
            result.Message = $"无法创建备份目录 {destDir}: {ex.Message}";
            Logger.Warning($"[FTS备份] {result.Message}");
            return result;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var sid = string.IsNullOrWhiteSpace(stationId) ? "UNKNOWN" : stationId.Trim();
        var zipName = $"FTS-backup_{sid}_{stamp}.zip";
        var zipPath = Path.Combine(destDir, zipName);
        var manifestPath = zipPath + ".json";

        int fileCount = 0;
        long rawBytes = 0;
        try
        {
            Logger.Info($"[FTS备份] 开始压缩 {source} → {zipPath}");
            using (var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var file in EnumerateFilesSafe(source))
                {
                    if (ShouldSkip(file)) continue;
                    var rel = Path.GetRelativePath(source, file).Replace('\\', '/');
                    var entry = archive.CreateEntry(rel, CompressionLevel.SmallestSize);
                    try
                    {
                        using var entryStream = entry.Open();
                        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        try { rawBytes += fs.Length; } catch { }
                        fs.CopyTo(entryStream);
                        fileCount++;
                    }
                    catch (Exception exFile)
                    {
                        Logger.Warning($"[FTS备份] 跳过无法读取的文件: {rel} ({exFile.Message})");
                    }
                }
            }

            if (fileCount == 0)
            {
                TryDelete(zipPath);
                result.Message = $"备份失败: {source} 下没有可读文件";
                return result;
            }

            var zipBytes = new FileInfo(zipPath).Length;
            var sha = ComputeSha256(zipPath);
            var fwNote = BuildFwNote(cfg);

            var manifest = new
            {
                created_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                source_root = source,
                zip_path = zipPath,
                zip_bytes = zipBytes,
                raw_bytes = rawBytes,
                file_count = fileCount,
                zip_sha256 = sha,
                station_id = sid,
                trigger,
                fw_versions = fwNote,
                compression = "SmallestSize",
            };
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            var id = db.InsertFctProgramBackup(new FctProgramBackupRecord
            {
                CreatedAt = manifest.created_at,
                ZipPath = zipPath,
                SourceRoot = source,
                ZipBytes = zipBytes,
                FileCount = fileCount,
                ZipSha256 = sha,
                StationId = sid,
                Trigger = trigger,
                Status = "ok",
                Note = fwNote,
                ZipExists = true,
            });

            PurgeOldArchives(db, cfg, destDir);

            result.Ok = true;
            result.Record = new FctProgramBackupRecord
            {
                Id = id,
                CreatedAt = manifest.created_at,
                ZipPath = zipPath,
                SourceRoot = source,
                ZipBytes = zipBytes,
                FileCount = fileCount,
                ZipSha256 = sha,
                StationId = sid,
                Trigger = trigger,
                Status = "ok",
                Note = fwNote,
                ZipExists = true,
            };
            result.Message = $"备份完成 {ByteUtil.MbGb(zipBytes)}（{fileCount} 个文件，压缩比约 {CompressionRatio(rawBytes, zipBytes)}）";
            Logger.Info($"[FTS备份] {result.Message} → {zipPath}");
        }
        catch (Exception ex)
        {
            TryDelete(zipPath);
            TryDelete(manifestPath);
            try
            {
                db.InsertFctProgramBackup(new FctProgramBackupRecord
                {
                    CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ZipPath = zipPath,
                    SourceRoot = source,
                    StationId = sid,
                    Trigger = trigger,
                    Status = "failed",
                    Note = ex.Message,
                    ZipExists = false,
                });
            }
            catch { }
            result.Message = $"备份失败: {ex.Message}";
            Logger.Warning($"[FTS备份] {result.Message}");
        }

        return result;
    }

    public static void PurgeOldArchives(Database db, AppConfig cfg, string? destDir = null)
    {
        int keep = Math.Clamp(cfg.FctProgramBackupKeep, 1, 100);
        destDir ??= Path.GetFullPath(cfg.FctProgramBackupDir.TrimEnd('\\', '/'));
        var records = db.ListFctProgramBackups(500).Where(r => r.Status == "ok" && r.ZipExists).ToList();
        var toPurge = records.Skip(keep).ToList();
        foreach (var r in toPurge)
        {
            if (!string.IsNullOrEmpty(r.ZipPath))
            {
                TryDelete(r.ZipPath);
                TryDelete(r.ZipPath + ".json");
            }
            db.MarkFctProgramBackupZipDeleted(r.Id);
        }
        if (toPurge.Count > 0)
            Logger.Info($"[FTS备份] 已清理超出保留份数({keep})的旧包 {toPurge.Count} 个（记录仍保留）");
    }

    /// <summary>本月尚未成功备份则执行（自然月，每月最多一次）。</summary>
    public static bool ShouldRunScheduled(Database db, AppConfig cfg)
    {
        if (!cfg.FctProgramBackupEnabled) return false;
        var last = db.GetLatestFctProgramBackupOk();
        if (last == null) return true;
        if (!DateTime.TryParse(last.CreatedAt, out var dt)) return true;
        var now = DateTime.Now;
        return dt.Year != now.Year || dt.Month != now.Month;
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            List<string> subDirs;
            List<string> files;
            try
            {
                // 审计：Directory.Enumerate* 是惰性的，异常发生在 foreach/MoveNext 而不是调用处——
                // 原来的 try 包不住，单个不可读目录会把整次备份判为 failed 并删掉 zip。
                // 这里在 try 内物化，真正做到"跳过该目录"。
                subDirs = new List<string>(Directory.EnumerateDirectories(dir));
                files = new List<string>(Directory.EnumerateFiles(dir));
            }
            catch (Exception ex)
            {
                Logger.Warning($"[FTS备份] 跳过目录 {dir}: {ex.Message}");
                continue;
            }
            foreach (var f in files) yield return f;
            foreach (var d in subDirs)
            {
                if (ShouldSkipDir(d)) continue;
                stack.Push(d);
            }
        }
    }

    private static bool ShouldSkipDir(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
               || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSkip(string filePath)
    {
        var name = Path.GetFileName(filePath);
        if (SkipFileNames.Contains(name)) return true;
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string BuildFwNote(AppConfig cfg)
    {
        try
        {
            var ini = FctIni.Snapshot(cfg.FctIniPath);
            if (!ini.Found || ini.FwVersions.Count == 0) return "";
            return string.Join("; ", ini.FwVersions.Select(v => $"{v.Label}={v.Version}"));
        }
        catch { return ""; }
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string CompressionRatio(long raw, long zip)
    {
        if (raw <= 0 || zip <= 0) return "—";
        return $"{zip * 100.0 / raw:F0}%";
    }
}

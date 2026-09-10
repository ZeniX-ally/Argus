using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FctAggregator;

public enum SeqChangeKind { Added, Removed, Modified }

public sealed class SeqFileEntry
{
    public string RelativePath { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string LastWrite { get; set; } = "";
}

public sealed class SeqFileChange
{
    public SeqChangeKind Kind;
    public string RelativePath = "";
    public long OldSize;
    public long NewSize;
    public string? OldSha256;
    public string? NewSha256;
}

public sealed class SeqChangeReport
{
    public string BaselineAt = "";
    public string DetectedAt = "";
    public string SeqRoot = "";
    public List<SeqFileChange> Changes = new();
    public int Added => Changes.Count(c => c.Kind == SeqChangeKind.Added);
    public int Removed => Changes.Count(c => c.Kind == SeqChangeKind.Removed);
    public int Modified => Changes.Count(c => c.Kind == SeqChangeKind.Modified);
    public bool HasChanges => Changes.Count > 0;

    public string Fingerprint()
    {
        var sb = new StringBuilder();
        foreach (var c in Changes.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
            sb.Append((char)c.Kind).Append(c.RelativePath).Append('|')
              .Append(c.OldSha256).Append('|').Append(c.NewSha256).Append(';');
        return sb.ToString();
    }
}

/// <summary>PEU Seq 测试程序目录快照与对比（基线存 data/seq_baseline）。</summary>
public static class FctSeqSnapshot
{
    public const string BaselineSubDir = "seq_baseline";
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".seq", ".txt", ".ini", ".xml", ".cfg", ".json", ".bat", ".csv", ".st", ".prg",
        ".lua", ".vbs", ".log", ".md",
    };

    public static string ResolveSeqRoot(AppConfig cfg, bool discoverFallback = false)
    {
        var configured = Path.GetFullPath(string.IsNullOrWhiteSpace(cfg.FctSeqRoot)
            ? Path.Combine(cfg.FctProgramSourceRoot.TrimEnd('\\', '/'), "Apps", "PEU", "Seq")
            : cfg.FctSeqRoot.TrimEnd('\\', '/'));
        if (Directory.Exists(configured) || !discoverFallback) return configured;
        var hits = DiscoverSeqRoots(cfg.FctProgramSourceRoot);
        return hits.Count == 1 ? hits[0] : configured;
    }

    /// <summary>在测试程序根下找名为 Seq 的目录。只扫 Apps 下一层和根下一层，避免整盘递归。</summary>
    public static List<string> DiscoverSeqRoots(string? sourceRoot, int max = 20)
    {
        var hits = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p) || hits.Count >= max) return;
            try
            {
                p = Path.GetFullPath(p.TrimEnd('\\', '/'));
                if (Directory.Exists(p) && seen.Add(p)) hits.Add(p);
            }
            catch { }
        }
        if (string.IsNullOrWhiteSpace(sourceRoot)) return hits;
        string root;
        try { root = Path.GetFullPath(sourceRoot.TrimEnd('\\', '/')); }
        catch { return hits; }
        if (!Directory.Exists(root)) return hits;

        Add(Path.Combine(root, "Seq"));
        var apps = Path.Combine(root, "Apps");
        Add(Path.Combine(apps, "Seq"));
        try
        {
            if (Directory.Exists(root))
            {
                foreach (var child in Directory.EnumerateDirectories(root))
                    Add(Path.Combine(child, "Seq"));
            }
        }
        catch { }
        try
        {
            if (Directory.Exists(apps))
            {
                foreach (var app in Directory.EnumerateDirectories(apps))
                    Add(Path.Combine(app, "Seq"));
            }
        }
        catch { }
        return hits;
    }

    public static string BaselineRoot => Path.Combine(AppConfig.BaseDir, "data", BaselineSubDir);
    public static string BaselineFilesDir => Path.Combine(BaselineRoot, "files");
    public static string BaselineManifestPath => Path.Combine(BaselineRoot, "manifest.json");

    public static bool HasBaseline() =>
        File.Exists(BaselineManifestPath) && Directory.Exists(BaselineFilesDir);

    public static Dictionary<string, SeqFileEntry> ScanDirectory(string root)
    {
        var map = new Dictionary<string, SeqFileEntry>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return map;
        foreach (var file in EnumerateFilesSafe(root))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (ShouldSkip(rel)) continue;
            try
            {
                var fi = new FileInfo(file);
                map[rel] = new SeqFileEntry
                {
                    RelativePath = rel,
                    Size = fi.Length,
                    Sha256 = HashFile(file),
                    LastWrite = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                };
            }
            catch { }
        }
        return map;
    }

    public static void CaptureBaseline(string seqRoot)
    {
        Directory.CreateDirectory(BaselineFilesDir);
        var scan = ScanDirectory(seqRoot);
        // 清空旧基线文件
        if (Directory.Exists(BaselineFilesDir))
        {
            foreach (var f in Directory.EnumerateFiles(BaselineFilesDir, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(f); } catch { }
            }
        }
        foreach (var kv in scan)
        {
            var src = Path.Combine(seqRoot, kv.Key.Replace('/', Path.DirectorySeparatorChar));
            var dest = Path.Combine(BaselineFilesDir, kv.Key.Replace('/', Path.DirectorySeparatorChar));
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
            File.Copy(src, dest, overwrite: true);
        }
        var manifest = new
        {
            captured_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            seq_root = seqRoot,
            files = scan.Values.OrderBy(x => x.RelativePath).ToList(),
        };
        File.WriteAllText(BaselineManifestPath, JsonSerializer.Serialize(manifest,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = null }));
        Logger.Info($"[Seq监控] 已建立基线快照: {scan.Count} 个文件 → {BaselineRoot}");
    }

    public static Dictionary<string, SeqFileEntry> LoadBaselineManifest()
    {
        if (!File.Exists(BaselineManifestPath)) return new Dictionary<string, SeqFileEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(BaselineManifestPath));
            var map = new Dictionary<string, SeqFileEntry>(StringComparer.OrdinalIgnoreCase);
            if (!doc.RootElement.TryGetProperty("files", out var arr)) return map;
            foreach (var f in arr.EnumerateArray())
            {
                var e = new SeqFileEntry
                {
                    RelativePath = ReadJsonString(f, "RelativePath") ?? "",
                    Size = ReadJsonLong(f, "Size"),
                    Sha256 = ReadJsonString(f, "Sha256") ?? "",
                    LastWrite = ReadJsonString(f, "LastWrite") ?? "",
                };
                if (!string.IsNullOrEmpty(e.RelativePath)) map[e.RelativePath] = e;
            }
            return map;
        }
        catch { return new Dictionary<string, SeqFileEntry>(StringComparer.OrdinalIgnoreCase); }
    }

    public static SeqChangeReport CompareToBaseline(string seqRoot)
    {
        var report = new SeqChangeReport
        {
            SeqRoot = seqRoot,
            DetectedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        if (File.Exists(BaselineManifestPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(BaselineManifestPath));
                report.BaselineAt = doc.RootElement.TryGetProperty("captured_at", out var c)
                    ? c.GetString() ?? "" : "";
            }
            catch { }
        }

        var baseline = LoadBaselineManifest();
        var current = ScanDirectory(seqRoot);

        foreach (var (rel, cur) in current)
        {
            if (!baseline.TryGetValue(rel, out var old))
            {
                report.Changes.Add(new SeqFileChange
                {
                    Kind = SeqChangeKind.Added, RelativePath = rel, NewSize = cur.Size, NewSha256 = cur.Sha256,
                });
            }
            else if (!string.Equals(old.Sha256, cur.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                report.Changes.Add(new SeqFileChange
                {
                    Kind = SeqChangeKind.Modified,
                    RelativePath = rel,
                    OldSize = old.Size,
                    NewSize = cur.Size,
                    OldSha256 = old.Sha256,
                    NewSha256 = cur.Sha256,
                });
            }
        }
        foreach (var (rel, old) in baseline)
        {
            if (!current.ContainsKey(rel))
            {
                report.Changes.Add(new SeqFileChange
                {
                    Kind = SeqChangeKind.Removed,
                    RelativePath = rel,
                    OldSize = old.Size,
                    OldSha256 = old.Sha256,
                });
            }
        }
        report.Changes = report.Changes
            .OrderBy(c => c.Kind).ThenBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        return report;
    }

    public static string? ReadBaselineText(string relativePath)
    {
        var path = Path.Combine(BaselineFilesDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? ReadTextSafe(path) : null;
    }

    public static string? ReadCurrentText(string seqRoot, string relativePath)
    {
        var path = Path.Combine(seqRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? ReadTextSafe(path) : null;
    }

    public static bool IsTextFile(string relativePath) =>
        TextExtensions.Contains(Path.GetExtension(relativePath));

    public static string BaselineFilePath(string relativePath) =>
        Path.Combine(BaselineFilesDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string ReadTextSafe(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            var bytes = ms.ToArray();
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            try { return Encoding.UTF8.GetString(bytes); }
            catch { return Encoding.GetEncoding(936).GetString(bytes); }
        }
        catch { return ""; }
    }

    private static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var list = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files;
            IEnumerable<string> dirs;
            try
            {
                files = Directory.EnumerateFiles(dir);
                dirs = Directory.EnumerateDirectories(dir);
            }
            catch { continue; }
            foreach (var f in files) list.Add(f);
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                    continue;
                stack.Push(d);
            }
        }
        return list;
    }

    private static string? ReadJsonString(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v)) return v.GetString();
        var camel = char.ToLowerInvariant(name[0]) + name[1..];
        return el.TryGetProperty(camel, out v) ? v.GetString() : null;
    }

    private static long ReadJsonLong(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v) && v.TryGetInt64(out var n)) return n;
        var camel = char.ToLowerInvariant(name[0]) + name[1..];
        return el.TryGetProperty(camel, out v) && v.TryGetInt64(out n) ? n : 0;
    }

    private static bool ShouldSkip(string rel)
    {
        var name = Path.GetFileName(rel);
        return name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
               || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
    }
}

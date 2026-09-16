using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;

namespace FctAggregator;

public static class Logger
{
    private static readonly object _fileLock = new();
    private static readonly ConcurrentQueue<string> _guiLogs = new();
    private static string _level = "INFO";
    private const int MaxGuiLogs = 5000;

    public static string LogDirectory =>
        Path.Combine(AppConfig.BaseDir, "logs");

    private static readonly string LogPath =
        Path.Combine(AppConfig.BaseDir, "logs", "app.log");

    public static string CurrentLogPath => LogPath;

    private static long MaxLogBytes = 20L * 1024 * 1024;
    private const int MaxLogFiles = 7;
    private static long _writtenBytes;
    private static bool _bytesInited;

    public static void SetLevel(string level) => _level = level.ToUpperInvariant();

    private static readonly Dictionary<string, int> LevelRank = new()
    {
        ["DEBUG"] = 10, ["INFO"] = 20, ["WARNING"] = 30, ["ERROR"] = 40,
    };

    private static void Write(string level, string message)
    {
        if (LevelRank.GetValueOrDefault(level, 20) < LevelRank.GetValueOrDefault(_level, 20))
            return;

        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var line = $"{ts} | {level,-7} | {message}";

        try
        {
            lock (_fileLock)
            {
                // 审计 M16：滚动字节计数不随重启初始化——低日志量机台 _writtenBytes 永远到不了阈值，
                // app.log 跨重启无限膨胀。首次写前用现有文件长度初始化。
                if (!_bytesInited)
                {
                    try { _writtenBytes = new FileInfo(LogPath).Length; } catch { _writtenBytes = 0; }
                    _bytesInited = true;
                }
                var dir = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(dir);
                // 审计：原按 line.Length（UTF-16 字符数）累加，中文日志实际磁盘占用约为其 3 倍，
                // MaxLogBytes(20MB) 形同虚设（单文件可涨到 ~60MB）。改按 UTF-8 实际字节数。
                _writtenBytes += Encoding.UTF8.GetByteCount(line) + 2;
                if (_writtenBytes > MaxLogBytes)
                {
                    _writtenBytes = 0;
                    CloseWriter();       // 滚动要先关掉自己的句柄，否则 File.Move 必然失败
                    RotateIfNeeded();
                }
                Writer().WriteLine(line);
                }
        }
        catch {  }

        var guiLine = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        _guiLogs.Enqueue(guiLine);
        while (_guiLogs.Count > MaxGuiLogs && _guiLogs.TryDequeue(out _)) { }
    }

    private static StreamWriter? _writer;

    /// <summary>复用同一个 StreamWriter。原来每行都走 <c>File.AppendAllText</c>（打开→写→关闭），
    /// 历史扫描/每条入库都写 INFO（`Engine.cs` 热路径）时文件开关与持锁开销可观。
    /// AutoFlush 保证崩溃时日志不丢；FileShare.ReadWrite 保证 ReadTail / 导出日志 / 复制仍能读。</summary>
    private static StreamWriter Writer()
    {
        if (_writer != null) return _writer;
        var dir = Path.GetDirectoryName(LogPath)!;
        Directory.CreateDirectory(dir);
        var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
        return _writer;
    }

    private static void CloseWriter()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            var fi = new FileInfo(LogPath);
            if (!fi.Exists || fi.Length < MaxLogBytes) return;
            var oldest = Path.Combine(dir, $"app.log.{MaxLogFiles}");
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int i = MaxLogFiles - 1; i >= 1; i--)
            {
                var src = Path.Combine(dir, $"app.log.{i}");
                var dst = Path.Combine(dir, $"app.log.{i + 1}");
                if (File.Exists(src)) File.Move(src, dst, overwrite: true);
            }
            File.Move(LogPath, Path.Combine(dir, "app.log.1"), overwrite: true);
        }
        catch {  }
    }

    public static void Debug(string m) => Write("DEBUG", m);
    public static void Info(string m) => Write("INFO", m);
    public static void Warning(string m) => Write("WARNING", m);
    public static void Error(string m) => Write("ERROR", m);

    public static List<string> SnapshotGuiLogs() => _guiLogs.ToArray().ToList();

    /// <summary>读当前 app.log 尾部（UTF-8），供调试页预览。文件不存在返回提示串。</summary>
    public static string ReadTail(int maxBytes = 120_000)
    {
        if (maxBytes < 1024) maxBytes = 1024;
        lock (_fileLock)
        {
            if (!File.Exists(LogPath)) return "(尚无日志文件)";
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var len = fs.Length;
            var take = (int)Math.Min(len, maxBytes);
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            var n = fs.Read(buf, 0, take);
            var text = Encoding.UTF8.GetString(buf, 0, n);
            if (len > maxBytes)
            {
                var nl = text.IndexOf('\n');
                if (nl >= 0 && nl + 1 < text.Length) text = text[(nl + 1)..];
                text = $"(仅显示末尾 {take} 字节)\n" + text;
            }
            return text;
        }
    }

    /// <summary>打包 logs/ 下 app.log* + diagnostic.txt + 内存尾部。不含 config.json（防 webhook 泄露）。返回写出的 zip 绝对路径。</summary>
    public static string ExportZip(string destZip)
    {
        if (string.IsNullOrWhiteSpace(destZip)) throw new ArgumentException("导出路径为空", nameof(destZip));
        destZip = Path.GetFullPath(destZip);
        if (!destZip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            destZip += ".zip";
        Directory.CreateDirectory(Path.GetDirectoryName(destZip)!);

        var staging = Path.Combine(Path.GetTempPath(), "argus_logs_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            lock (_fileLock)
            {
                if (Directory.Exists(LogDirectory))
                {
                    foreach (var f in Directory.GetFiles(LogDirectory, "app.log*"))
                    {
                        try { File.Copy(f, Path.Combine(staging, Path.GetFileName(f)), overwrite: true); }
                        catch { }
                    }
                }
            }
            File.WriteAllText(Path.Combine(staging, "diagnostic.txt"), BuildDiagnostic(), Encoding.UTF8);
            var mem = SnapshotGuiLogs();
            if (mem.Count > 0)
                File.WriteAllLines(Path.Combine(staging, "gui-tail.txt"), mem.TakeLast(2000), Encoding.UTF8);

            if (File.Exists(destZip)) File.Delete(destZip);
            ZipFile.CreateFromDirectory(staging, destZip, CompressionLevel.SmallestSize, includeBaseDirectory: false);
            return destZip;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    public static string SuggestedExportName()
    {
        var sid = "";
        try { sid = AppConfig.Instance.StationId; } catch { }
        if (string.IsNullOrWhiteSpace(sid)) sid = "station";
        foreach (var c in Path.GetInvalidFileNameChars()) sid = sid.Replace(c, '_');
        return $"Argus-logs-{sid}-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
    }

    internal static string BuildDiagnostic()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Argus diagnostic (no secrets)");
        sb.AppendLine($"exported_at={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        try
        {
            var ver = typeof(Logger).Assembly.GetName().Version;
            sb.AppendLine($"version={ver}");
        }
        catch { sb.AppendLine("version=?"); }
        sb.AppendLine($"os={Environment.OSVersion}");
        sb.AppendLine($"pid={Environment.ProcessId}");
        sb.AppendLine($"base_dir={AppConfig.BaseDir}");
        sb.AppendLine($"log_file={LogPath}");
        try
        {
            var c = AppConfig.Instance;
            sb.AppendLine($"station_id={c.StationId}");
            sb.AppendLine($"log_level={c.LogLevel}");
            sb.AppendLine($"results_root={c.ResultsRoot}");
            sb.AppendLine($"learn_normal_enabled={c.LearnNormalEnabled}");
            sb.AppendLine($"analyze_tdms_enabled={c.AnalyzeTdmsEnabled}");
            sb.AppendLine($"analyze_collect_pass={c.AnalyzeCollectPass}");
            sb.AppendLine($"auto_update={c.AutoUpdate}");
            sb.AppendLine($"webhook_configured={!string.IsNullOrWhiteSpace(c.WebhookUrl)}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"config_read_error={ex.Message}");
        }
        sb.AppendLine("log_files=");
        if (Directory.Exists(LogDirectory))
        {
            foreach (var f in Directory.GetFiles(LogDirectory, "app.log*").OrderBy(x => x))
            {
                try
                {
                    var fi = new FileInfo(f);
                    sb.AppendLine($"  {fi.Name} {fi.Length}B");
                }
                catch { }
            }
        }
        else sb.AppendLine("  (none)");
        return sb.ToString();
    }
}

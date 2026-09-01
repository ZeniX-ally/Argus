using System.Collections.Concurrent;

namespace FctAggregator;

/// <summary>
/// 简单日志: 写文件 logs/app.log + 内存队列(供 GUI 显示)。线程安全。
/// </summary>
public static class Logger
{
    private static readonly object _fileLock = new();
    private static readonly ConcurrentQueue<string> _guiLogs = new();
    private static string _level = "INFO";
    private const int MaxGuiLogs = 5000;

    private static readonly string LogPath =
        Path.Combine(AppConfig.BaseDir, "logs", "app.log");

    // v3.10.0 P1-4 日志滚动：单文件超 20MB 滚动，保留 7 份（app.log.1~7）
    // ⚠ 刻意用 static 字段而非 const：selftest 反射调小阈值即可低成本触发滚动断言
    private static long MaxLogBytes = 20L * 1024 * 1024;
    private const int MaxLogFiles = 7;
    private static long _writtenBytes;   // 本进程内累计写入字节（近似计数，滚动前用真实大小校准）

    public static void SetLevel(string level) => _level = level.ToUpperInvariant();

    private static readonly Dictionary<string, int> LevelRank = new()
    {
        ["DEBUG"] = 10, ["INFO"] = 20, ["WARNING"] = 30, ["ERROR"] = 40,
    };

    private static void Write(string level, string message)
    {
        // 级别过滤
        if (LevelRank.GetValueOrDefault(level, 20) < LevelRank.GetValueOrDefault(_level, 20))
            return;

        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var line = $"{ts} | {level,-7} | {message}";

        // 写文件
        try
        {
            lock (_fileLock)
            {
                var dir = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(dir);
                // 近似计数到阈值才检查真实大小（避免每次写都 FileInfo 一次 IO）
                _writtenBytes += line.Length + 2;
                if (_writtenBytes > MaxLogBytes)
                {
                    _writtenBytes = 0;
                    RotateIfNeeded();
                }
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch { /* 文件写失败不影响运行 */ }

        // GUI 队列(短格式)
        var guiLine = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        _guiLogs.Enqueue(guiLine);
        while (_guiLogs.Count > MaxGuiLogs && _guiLogs.TryDequeue(out _)) { }
    }

    /// <summary>
    /// 按大小滚动日志（v3.10.0 P1-4）：真实文件 ≥20MB 时依次平移
    /// app.log.6→.7 … app.log→.1，保留最近 7 份，最老删除。在 _fileLock 内调用（写路径串行），
    /// 失败静默不影响写日志。
    /// </summary>
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
        catch { /* 滚动失败不影响写日志 */ }
    }

    public static void Debug(string m) => Write("DEBUG", m);
    public static void Info(string m) => Write("INFO", m);
    public static void Warning(string m) => Write("WARNING", m);
    public static void Error(string m) => Write("ERROR", m);

    /// <summary>取出所有 GUI 日志快照。</summary>
    public static List<string> SnapshotGuiLogs() => _guiLogs.ToArray().ToList();

    public static void ClearGuiLogs()
    {
        while (_guiLogs.TryDequeue(out _)) { }
    }
}

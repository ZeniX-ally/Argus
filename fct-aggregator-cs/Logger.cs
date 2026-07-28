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
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch { /* 文件写失败不影响运行 */ }

        // GUI 队列(短格式)
        var guiLine = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        _guiLogs.Enqueue(guiLine);
        while (_guiLogs.Count > MaxGuiLogs && _guiLogs.TryDequeue(out _)) { }
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

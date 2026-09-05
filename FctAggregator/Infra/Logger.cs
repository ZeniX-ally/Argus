using System.Collections.Concurrent;

namespace FctAggregator;

public static class Logger
{
    private static readonly object _fileLock = new();
    private static readonly ConcurrentQueue<string> _guiLogs = new();
    private static string _level = "INFO";
    private const int MaxGuiLogs = 5000;

    private static readonly string LogPath =
        Path.Combine(AppConfig.BaseDir, "logs", "app.log");

    private static long MaxLogBytes = 20L * 1024 * 1024;
    private const int MaxLogFiles = 7;
    private static long _writtenBytes;

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
                var dir = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(dir);
                _writtenBytes += line.Length + 2;
                if (_writtenBytes > MaxLogBytes)
                {
                    _writtenBytes = 0;
                    RotateIfNeeded();
                }
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch {  }

        var guiLine = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        _guiLogs.Enqueue(guiLine);
        while (_guiLogs.Count > MaxGuiLogs && _guiLogs.TryDequeue(out _)) { }
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

    public static void ClearGuiLogs()
    {
        while (_guiLogs.TryDequeue(out _)) { }
    }
}

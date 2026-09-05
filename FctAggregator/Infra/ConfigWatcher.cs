using System.Text.Json;

namespace FctAggregator;

public sealed class ConfigWatcher : IDisposable
{
    private readonly FileSystemWatcher _fsw;
    private readonly string _path;
    private readonly System.Timers.Timer _debounce;
    private bool _disposed;

    public event EventHandler<string>? ConfigChanged;

    public ConfigWatcher(string configPath, int debounceMs = 1000)
    {
        _path = configPath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
        _fsw = new FileSystemWatcher(dir, Path.GetFileName(configPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _fsw.Changed += OnFsChanged;

        _debounce = new System.Timers.Timer(Math.Max(10, debounceMs)) { AutoReset = false };
        _debounce.Elapsed += (_, _) => ValidateAndNotify();
    }

    private void OnFsChanged(object? sender, FileSystemEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void ValidateAndNotify()
    {
        try
        {
            var json = File.ReadAllText(_path);
            using var doc = JsonDocument.Parse(json);
            ConfigChanged?.Invoke(this, json);
            Logger.Info("[ConfigWatcher] config.json 变更已通过校验");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[ConfigWatcher] config.json 变更未通过校验，保持旧配置：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce.Dispose();
        _fsw.Dispose();
    }
}

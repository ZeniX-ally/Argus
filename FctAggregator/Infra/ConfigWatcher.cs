using System.Text.Json;

namespace FctAggregator;

public sealed class ConfigWatcher : IDisposable
{
    private readonly FileSystemWatcher _fsw;
    private readonly string _path;
    private readonly System.Timers.Timer _debounce;
    private readonly int _debounceMs;
    private bool _disposed;

    public event EventHandler<string>? ConfigChanged;

    public ConfigWatcher(string configPath, int debounceMs = 1000)
    {
        _path = configPath;
        _debounceMs = Math.Max(10, debounceMs);
        var dir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
        _fsw = new FileSystemWatcher(dir, Path.GetFileName(configPath))
        {
            // 审计 M1：原子替换式保存（临时文件 + Rename，如 VS Code）只产生 Renamed 事件，
            // 原先只订阅 LastWrite/Size 会整次错过——补 FileName 过滤器并订阅 Renamed
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _fsw.Changed += OnFsChanged;
        _fsw.Renamed += OnFsChanged;

        _debounce = new System.Timers.Timer(Math.Max(10, debounceMs)) { AutoReset = false };
        _debounce.Elapsed += (_, _) => ValidateAndNotify();
    }

    private void OnFsChanged(object? sender, FileSystemEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private int _retryCount; // 审计 M1：校验失败短退避重试（保存未写完的中间态可能读到半份 JSON）

    private void ValidateAndNotify()
    {
        try
        {
            var json = File.ReadAllText(_path);
            using var doc = JsonDocument.Parse(json);
            _retryCount = 0;
            // 审计修复：成功后必须还原失败期间放大的 debounce 间隔——否则失败 1~3 次后成功，Interval
            // 永久停在 2x/3x/4x（只有"连败 4 次"那一支才还原），此后每次热加载都晚 1~3 秒生效。
            if (_debounce.Interval != _debounceMs) _debounce.Interval = _debounceMs;
            ConfigChanged?.Invoke(this, json);
            Logger.Info("[ConfigWatcher] config.json 变更已通过校验");
        }
        catch (Exception ex)
        {
            // 审计 M1：失败不再一次性放弃——短退避重试至多 3 次（读到半份写入的常见窗口）
            if (_retryCount < 3)
            {
                _retryCount++;
                _debounce.Interval = _debounceMs * (_retryCount + 1);
                _debounce.Stop();
                _debounce.Start();
                Logger.Warning($"[ConfigWatcher] config.json 校验失败，{_debounce.Interval / 1000.0:0.#}s 后重试({_retryCount}/3)：{ex.Message}");
            }
            else
            {
                _retryCount = 0;
                _debounce.Interval = _debounceMs;
                Logger.Warning($"[ConfigWatcher] config.json 变更未通过校验（重试耗尽），保持旧配置：{ex.Message}");
            }
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

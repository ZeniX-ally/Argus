using System.Text.Json;

namespace FctAggregator;

/// <summary>
/// 配置热更新监听（v3.21.1 P1）：FileSystemWatcher 盯 config.json，
/// 变更后先做 JSON 语法校验：合法才触发 <see cref="ConfigChanged"/>；
/// 非法配置直接拒绝（旧配置保持生效 = 天然回滚），并记 Warning。
/// 最小实现：只做「校验+通知」，字段级生效由消费方决定（AppConfig 属性重载/引擎参数刷新）。
/// </summary>
public sealed class ConfigWatcher : IDisposable
{
    private readonly FileSystemWatcher _fsw;
    private readonly string _path;
    private readonly System.Timers.Timer _debounce;
    private bool _disposed;

    /// <summary>配置文件变更（已通过 JSON 语法校验）。第二参数 = 新配置原文。</summary>
    public event EventHandler<string>? ConfigChanged;

    /// <param name="configPath">config.json 绝对/相对路径。</param>
    /// <param name="debounceMs">变更防抖毫秒（默认 1000；自检注入短值加速）。</param>
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

        // 编辑器保存 config.json 常连发多次 Changed 事件；防抖后只校验一次
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
            using var doc = JsonDocument.Parse(json); // 语法校验，非法即抛
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

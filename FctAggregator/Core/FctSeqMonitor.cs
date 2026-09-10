namespace FctAggregator;

/// <summary>监视 PEU Seq 目录；有变更时防抖合并为一次弹窗，对比基线快照展示差异。</summary>
public sealed class FctSeqMonitor : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly Database? _db;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce;
    private readonly object _gate = new();
    private string? _lastAlertFingerprint;
    private volatile bool _checking;
    private bool _disposed;

    public static FctSeqMonitor? Instance { get; private set; }

    public static FctSeqMonitor Start(AppConfig cfg, Database? db = null)
    {
        Instance?.Dispose();
        var m = new FctSeqMonitor(cfg, db);
        m.StartInternal();
        Instance = m;
        return m;
    }

    private FctSeqMonitor(AppConfig cfg, Database? db)
    {
        _cfg = cfg;
        _db = db;
    }

    private void StartInternal()
    {
        if (!_cfg.FctSeqMonitorEnabled) return;
        var configured = FctSeqSnapshot.ResolveSeqRoot(_cfg);
        var root = FctSeqSnapshot.ResolveSeqRoot(_cfg, discoverFallback: true);
        if (!Directory.Exists(configured) && Directory.Exists(root)
            && !string.Equals(configured, root, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Info($"[Seq监控] 配置目录不存在（{configured}），改用发现的 {root}");
            _cfg.FctSeqRoot = root;
            try { _cfg.Save(); } catch (Exception ex) { Logger.Warning($"[Seq监控] 回写发现路径失败: {ex.Message}"); }
        }
        if (!Directory.Exists(root))
        {
            var extra = FctSeqSnapshot.DiscoverSeqRoots(_cfg.FctProgramSourceRoot);
            var hint = extra.Count == 0
                ? ""
                : "；候选: " + string.Join(" | ", extra.Take(5));
            Logger.Warning($"[Seq监控] 目录不存在，跳过监视: {root}{hint}");
            return;
        }
        if (!FctSeqSnapshot.HasBaseline())
        {
            try { FctSeqSnapshot.CaptureBaseline(root); }
            catch (Exception ex) { Logger.Warning($"[Seq监控] 建立初始基线失败: {ex.Message}"); }
        }
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                 | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFsEvent;
            _watcher.Created += OnFsEvent;
            _watcher.Deleted += OnFsEvent;
            _watcher.Renamed += OnFsEvent;
            Logger.Info($"[Seq监控] 已监视测试程序目录: {root}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Seq监控] 无法挂载 FileSystemWatcher: {ex.Message}");
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => ScheduleCheck();

    private void ScheduleCheck()
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = new System.Threading.Timer(_ => RunCheck(), null, 2500, Timeout.Infinite);
        }
    }

    private void RunCheck()
    {
        if (_checking || _disposed || !_cfg.FctSeqMonitorEnabled) return;
        _checking = true;
        try
        {
            var root = FctSeqSnapshot.ResolveSeqRoot(_cfg);
            if (!Directory.Exists(root)) return;
            var report = FctSeqSnapshot.CompareToBaseline(root);
            if (!report.HasChanges) return;

            var fp = report.Fingerprint();
            if (string.Equals(fp, _lastAlertFingerprint, StringComparison.Ordinal))
                return;

            _lastAlertFingerprint = fp;
            try { _db?.InsertFctSeqChangeLog(report); } catch { }
            Logger.Warning($"[Seq监控] 测试程序已修改: +{report.Added} -{report.Removed} ~{report.Modified}");
            UiBridge.ShowSeqChange(report, _cfg, () => _lastAlertFingerprint = null);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Seq监控] 检查失败: {ex.Message}");
        }
        finally { _checking = false; }
    }

    public void ResetAlertFingerprint() => _lastAlertFingerprint = null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) { _debounce?.Dispose(); _debounce = null; }
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        if (Instance == this) Instance = null;
    }
}

/// <summary>后台监视线程回调 UI 的桥（由 MainForm 注册）。</summary>
public static class UiBridge
{
    public static Form? MainForm { get; set; }

    public static void ShowSeqChange(SeqChangeReport report, AppConfig cfg, Action? onBaselineUpdated = null)
    {
        var main = MainForm;
        if (main == null || main.IsDisposed)
        {
            Logger.Info($"[Seq监控] 无 UI，变更摘要: +{report.Added} -{report.Removed} ~{report.Modified}");
            return;
        }
        void Show()
        {
            try
            {
                using var dlg = new SeqChangeForm(report, cfg, onBaselineUpdated);
                dlg.ShowDialog(main);
            }
            catch (Exception ex) { Logger.Warning($"[Seq监控] 弹窗失败: {ex.Message}"); }
        }
        if (main.InvokeRequired) main.BeginInvoke(Show);
        else Show();
    }
}

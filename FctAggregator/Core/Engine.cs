using System.Collections.Concurrent;

namespace FctAggregator;

public class Engine
{
    private readonly AppConfig _cfg;
    private readonly Database _db;
    private readonly string _stationId;
    private MeshNode _mesh;
    private TodoSync _todoSync;
    private readonly List<FileSystemWatcher> _watchers = new();
    private volatile bool _initialScanComplete = false;
    private CancellationTokenSource _cts = new();

    private const int MaxStableRetries = 3;
    private const int StableRetryDelayMs = 30000;
    private readonly ConcurrentQueue<(string Path, int Attempt, long DueAt)> _retryQueue = new();
    private Thread? _retryThread;
    private static readonly HashSet<string> _seenFailReasons = new(StringComparer.OrdinalIgnoreCase);

    public Database Db => _db;
    public string ResolvedStationId => _stationId;
    public AppConfig Config => _cfg;
    public MeshPusher Pusher => _mesh.Pusher;
    public MeshNode Mesh => _mesh;
    public AggDatabase AggDb => _mesh.AggDb;

    public Engine(AppConfig cfg, AggDatabase? sharedAggDb = null)
    {
        _cfg = cfg;

        var sid = cfg.StationId;
        if (string.IsNullOrEmpty(sid))
        {
            sid = StationDetector.DetectStation() ?? "";
            if (!string.IsNullOrEmpty(sid)) Logger.Info($"IP 识别机台号: {sid}");
        }
        _stationId = sid;

        var dataDir = Path.Combine(AppConfig.BaseDir, "data");
        Directory.CreateDirectory(dataDir);
        var dbFile = Path.Combine(dataDir, $"{(string.IsNullOrEmpty(sid) ? "fct" : sid)}.db");
        _db = new Database(dbFile);

        var aggDb = sharedAggDb ?? new AggDatabase(Path.Combine(dataDir, "mesh_agg.db"));
        _mesh = new MeshNode(cfg, _stationId, _db, aggDb, cfg.Peers);
        _todoSync = _mesh.TodoSync;

        _mesh.Start();

        try { DeviceSampleRecorder.Instance.Start(); } catch { }

        AppState.StationId = sid;
        AppState.WebhookConfigured = !string.IsNullOrEmpty(cfg.WebhookUrl);

        Logger.Info($"服务启动 | station_id={(string.IsNullOrEmpty(sid) ? "Auto" : sid)} | results_root={cfg.ResultsRoot}");
        Logger.Info($"数据库: {dbFile}");
        Logger.Info($"Webhook: {(AppState.WebhookConfigured ? "已配置" : "未配置")}");
        Logger.Info($"FCT.ini: {FctIni.AutoFindIni() ?? "未找到(设备状态页将显示诊断)"} | config={cfg.FctIniPath}");
        Logger.Info($"程序目录(BaseDir): {AppConfig.BaseDir}");

        _db.MaintenanceStatusChanged += (rec, from, to) =>
        {
            try
            {
                Logger.Info($"[飞书推送] 待办 #{rec.Id} 状态: {MaintenanceMeta.ZhOf(from)} -> {MaintenanceMeta.ZhOf(to)}");
                var url = _cfg.WebhookUrl;
                Task.Run(() => FeishuNotifier.SendStatusChangeAlert(url, rec, from, to));
            }
            catch (Exception ex) { Logger.Error($"[错误] 待办状态变更推送失败: {ex.Message}"); }
        };
    }

    public void Start()
    {
        var root = _cfg.ResultsRoot;
        if (!Directory.Exists(root))
        {
            Logger.Error($"结果目录不存在: {root}，请检查 config.json");
            Logger.Error("⚠ 数据采集无法开始！请创建目录后重启");
            AppState.SetStatus("error");
            _initialScanComplete = true;
            return;
        }

        var models = DiscoverModels(root);
        AppState.ModelsCount = models.Count;
        AppState.SetStatus("running");
        Logger.Info($"发现型号目录: {string.Join(", ", models)}");

        _retryThread = new Thread(RetryLoop) { IsBackground = true, Name = "stable-retry" };
        _retryThread.Start();

        foreach (var model in models)
            foreach (var cat in new[] { "Online", "Offline" })
            {
                var dir = Path.Combine(root, cat, model);
                Directory.CreateDirectory(dir);
                var w = new FileSystemWatcher(dir, "*.xml")
                {
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                };
                w.Created += OnFileCreated;
                w.Renamed += OnFileRenamed;
                _watchers.Add(w);
                Logger.Info($"监控已启动: {dir}");
            }

        if (models.Count > 0 && !_cfg.SkipHistoricalScan)
            Task.Run(() => HistoricalScan(models, root));
        else
        {
            _initialScanComplete = true;
            AppState.HistoricalScanComplete = true;
            if (_cfg.SkipHistoricalScan) Logger.Info("历史扫描已跳过");
        }
    }

    public void Stop()
    {
        try { DeviceSampleRecorder.Instance.Stop(); } catch { }
        _todoSync.Stop();
        _mesh.Stop();
        _cts.Cancel();
        try { _retryThread?.Join(3000); } catch { }
        foreach (var w in _watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
        _watchers.Clear();
    }

    public void RestartPusher()
    {
        try { _todoSync.Stop(); } catch { }
        try { _mesh.Stop(); } catch (Exception ex) { Logger.Warning($"旧节点停止异常: {ex.Message}"); }
        var dataDir = Path.Combine(AppConfig.BaseDir, "data");
        var aggDb = new AggDatabase(Path.Combine(dataDir, "mesh_agg.db"));
        _mesh = new MeshNode(_cfg, _stationId, _db, aggDb, _cfg.Peers);
        _todoSync = _mesh.TodoSync;
        _mesh.Start();
        Logger.Info($"[Mesh节点] 已按新配置重启（peers={_cfg.Peers.Count}, port={_cfg.MeshPort}）");
    }

    private List<string> DiscoverModels(string root)
    {
        var models = new HashSet<string>();
        foreach (var cat in new[] { "Online", "Offline" })
        {
            var catDir = Path.Combine(root, cat);
            if (!Directory.Exists(catDir)) continue;
            foreach (var d in Directory.GetDirectories(catDir))
            {
                var name = Path.GetFileName(d);
                  if (StationDetector.IsValidModel(name)) models.Add(name);
                  else if (name.Length >= 3 && name.Any(char.IsLetterOrDigit) && !name.StartsWith("."))
                      Logger.Info($"[pending_review] 未知型号目录: {name} at {catDir}");
            }
        }
        return models.OrderBy(x => x).ToList();
    }

    private void HistoricalScan(List<string> models, string root)
    {
        try
        {
            var sid = _stationId;
            if (string.IsNullOrEmpty(sid))
            {
                sid = "UNKNOWN";
                Logger.Warning("[历史扫描] station_id 未配置且无法自动检测, 使用 UNKNOWN 继续扫描入库");
            }
            var processor = new Processor(_cfg, _stationId, Parsing.ParserRegistry.Instance, _db);

            AppState.SetScanProgress(phase: "scanning", total: 0, parsed: 0);
            var allFiles = new List<string>();
            foreach (var cat in new[] { "Online", "Offline" })
                foreach (var model in models)
                {
                    var modelDir = Path.Combine(root, cat, model);
                    if (!Directory.Exists(modelDir)) continue;
                    foreach (var f in Directory.EnumerateFiles(modelDir, "*.xml", SearchOption.AllDirectories))
                    {
                        allFiles.Add(f);
                        if (allFiles.Count % 200 == 0)
                            AppState.SetScanProgress(total: allFiles.Count);
                    }
                }
            AppState.SetScanProgress(total: allFiles.Count);
            Logger.Info($"[历史扫描][扫描阶段完成] 总文件={allFiles.Count}");

            AppState.SetScanProgress(phase: "parsing", total: allFiles.Count, parsed: 0);
            const int batchSize = 100;
            int processed = 0, totalInserted = 0;
            for (int i = 0; i < allFiles.Count; i += batchSize)
            {
                if (_cts.IsCancellationRequested) return;
                var take = Math.Min(batchSize, allFiles.Count - i);
                var batch = allFiles.GetRange(i, take).Select(Path.GetFullPath).ToList();
                var existing = _db.GetExistingPaths(batch);

                var records = new List<TestRecord>();
                foreach (var p in batch)
                {
                    if (existing.Contains(p))
                    {
                        processed++;
                        continue;
                    }
                    try
                    {
                        var rec = processor.ParseAndClassify(p);
                        if (rec != null)
                        {
                            records.Add(rec);
                            Logger.Info($"[解析] {rec.Model} | {rec.Result} | {Path.GetFileName(p)}");
                        }
                    }
                    catch (Exception ex) { Logger.Error($"[历史扫描] 解析失败: {p} | {ex.Message}"); }
                    processed++;
                }
                totalInserted += _db.BatchInsert(records);
                AppState.SetScanProgress(parsed: processed);
                RefreshStats();
            }
            Logger.Info($"[历史扫描] 结束 | xml={allFiles.Count} | 插入={totalInserted}");
            SyncTodos("历史扫描后");
        }
        catch (Exception ex)
        {
            Logger.Error($"历史扫描异常: {ex.Message}");
        }
        finally
        {
            AppState.SetScanProgress(phase: "done");
            _initialScanComplete = true;
            AppState.HistoricalScanComplete = true;
            RefreshStats();
            Logger.Info("历史扫描完成，FAIL 告警推送已启用");
        }
    }

    private void RefreshStats() => AppState.RefreshStats(_db, _stationId, _cfg.ResultsRoot);

    private void SyncTodos(string tag)
    {
        try
        {
            var n = _db.SyncTodoItems(_cfg.TodoScanDays);
            if (n > 0) Logger.Info($"[待办]{tag} 新登记 {n} 条待办(近 {_cfg.TodoScanDays} 天、同类项已合并)");
        }
        catch (Exception ex) { Logger.Warning($"[待办]{tag} 同步失败: {ex.Message}"); }
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e) => ScheduleProcess(e.FullPath);
    private void OnFileRenamed(object sender, RenamedEventArgs e) => ScheduleProcess(e.FullPath);

    private void ScheduleProcess(string path) => ScheduleProcess(path, 0);

    private void ScheduleProcess(string path, int attempt)
    {
        Task.Run(() =>
        {
            try { ProcessRealtime(path, attempt); }
            catch (Exception ex) { Logger.Error($"[错误] 处理异常: {path} | {ex.Message}"); }
        });
    }

    private void ProcessRealtime(string path, int attempt)
    {
        if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return;
        if (!WaitForStable(path))
        {
            if (attempt < MaxStableRetries)
            {
                _retryQueue.Enqueue((path, attempt, Environment.TickCount64 + StableRetryDelayMs));
                Logger.Warning($"[重试] 文件未稳定，{StableRetryDelayMs / 1000}s 后重试({attempt + 1}/{MaxStableRetries}): {path}");
            }
            else
                Logger.Warning($"[跳过] 文件持续未稳定，已达重试上限({MaxStableRetries}): {path}");
            return;
        }

            var processor = new Processor(_cfg, _stationId, Parsing.ParserRegistry.Instance, _db);
        var rec = processor.ParseAndClassify(path);
        if (rec == null) return;

        if (rec.HasFailItems)
        {
            foreach (var ft in rec.FailedTests)
            {
                var name = string.IsNullOrWhiteSpace(ft.Name) ? rec.FailReason : ft.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;
                lock (_seenFailReasons)
                {
                    if (_seenFailReasons.Add(name!))
                        Logger.Info($"[pending_review] 新不良项: {name} at {rec.Model} / {rec.StationId}");
                }
            }
            if (rec.FailedTests.Count == 0 && !string.IsNullOrWhiteSpace(rec.FailReason))
            {
                lock (_seenFailReasons)
                {
                    if (_seenFailReasons.Add(rec.FailReason!))
                        Logger.Info($"[pending_review] 新不良项: {rec.FailReason} at {rec.Model} / {rec.StationId}");
                }
            }
        }

        try
        {
            _db.InsertOne(rec);
            Logger.Info($"[入库] {rec.Model} | {rec.Result} | {Path.GetFileName(path)}");
        }
        catch (Exception ex) { Logger.Error($"[错误] 入库失败: {path} | {ex.Message}"); return; }

        RefreshStats();

        if (rec.Result == "FAIL")
        {
            SyncTodos("实时");
            if (rec.StationId == "UNKNOWN")
                Logger.Info($"[跳过推送-无机台号] FAIL / {rec.Model} / {rec.Sn}");
            else if (!_initialScanComplete)
                Logger.Info($"[跳过推送-扫描中] FAIL / {rec.Model} / {rec.Sn}");
            else
            {
                try { DesktopNotifier.NotifyFail(rec); }
                catch (Exception ex) { Logger.Warning($"桌面提示失败: {ex.Message}"); }
                try
                {
                    var url = _cfg.WebhookUrl;
                    Task.Run(async () =>
                    {
                        try
                        {
                            await FeishuNotifier.SendFailAlert(url, rec);
                            Logger.Info($"[飞书推送] FAIL / {rec.Model} / {rec.Sn}");
                        }
                        catch (Exception ex) { Logger.Error($"[错误] 飞书推送失败: {Path.GetFileName(rec.XmlPath)} | {ex.Message}"); }
                    });
                }
                catch (Exception ex) { Logger.Warning($"飞书推送任务启动失败: {ex.Message}"); }
            }
        }
    }

    private bool WaitForStable(string path, int stableChecks = 0, int intervalMs = 500, int timeoutMs = 10000)
    {
        long prev;
        try { prev = new FileInfo(path).Length; }
        catch { return false; }

        if (stableChecks <= 0)
            stableChecks = prev < (100 * 1024) ? 2 : (prev <= (1024 * 1024) ? 3 : 4);

        int same = 0;
        var start = Environment.TickCount64;
        while (same < stableChecks)
        {
            long size;
            try { size = new FileInfo(path).Length; }
            catch { return false; }
            if (size == 0) { same = 0; prev = size; }
            else if (size == prev) same++;
            else { same = 0; prev = size; }

            if (Environment.TickCount64 - start > timeoutMs) return false;
            Thread.Sleep(intervalMs);
        }
        return true;
    }

    private void RetryLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var now = Environment.TickCount64;
                var due = new List<(string Path, int Attempt)>();
                while (_retryQueue.TryPeek(out var head) && head.DueAt <= now)
                {
                    if (!_retryQueue.TryDequeue(out var item)) break;
                    due.Add((item.Path, item.Attempt + 1));
                }
                foreach (var (p, attempt) in due)
                    if (File.Exists(p)) ScheduleProcess(p, attempt);
                Thread.Sleep(5000);
            }
        }
        catch (Exception ex) { Logger.Error($"[重试线程] 异常: {ex.Message}"); }
    }
}

namespace FctAggregator;

/// <summary>
/// 后台引擎: 历史扫描 + 实时文件监控 + FAIL 推送。
/// </summary>
public class Engine
{
    private readonly AppConfig _cfg;
    private readonly Database _db;
    private readonly string _stationId;
    private readonly List<FileSystemWatcher> _watchers = new();
    private volatile bool _initialScanComplete = false;
    private CancellationTokenSource _cts = new();

    public Database Db => _db;
    public string ResolvedStationId => _stationId;
    public AppConfig Config => _cfg;

    public Engine(AppConfig cfg)
    {
        _cfg = cfg;

        // 解析机台号: config 优先, 否则 IP 识别
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

        AppState.StationId = sid;
        AppState.WebhookConfigured = !string.IsNullOrEmpty(cfg.WebhookUrl);

        Logger.Info($"服务启动 | station_id={(string.IsNullOrEmpty(sid) ? "Auto" : sid)} | results_root={cfg.ResultsRoot}");
        Logger.Info($"数据库: {dbFile}");
        Logger.Info($"Webhook: {(AppState.WebhookConfigured ? "已配置" : "未配置")}");
        Logger.Info($"FCT.ini 路径(config): {cfg.FctIniPath}");
        Logger.Info($"程序目录(BaseDir): {AppConfig.BaseDir}");
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

        // 挂载实时监控(每个 category/model 目录)
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

        // 后台历史扫描
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
        _cts.Cancel();
        foreach (var w in _watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
        _watchers.Clear();
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
            }
        }
        return models.OrderBy(x => x).ToList();
    }

    // ==================== 历史扫描(两阶段) ====================
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
            var processor = new Processor(_cfg, _stationId);

            // 阶段一: 扫描收集所有文件
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

            // 阶段二: 分批解析入库(逐条日志 + 实时刷新统计)
            AppState.SetScanProgress(phase: "parsing", total: allFiles.Count, parsed: 0);
            const int batchSize = 100;   // 小批次, 统计/进度更新更频繁
            int processed = 0, totalInserted = 0;
            for (int i = 0; i < allFiles.Count; i += batchSize)
            {
                if (_cts.IsCancellationRequested) return;
                var batch = allFiles.Skip(i).Take(batchSize).Select(Path.GetFullPath).ToList();
                var existing = _db.GetExistingPaths(batch);

                var records = new List<TestRecord>();
                foreach (var p in batch)
                {
                    if (existing.Contains(p))
                    {
                        processed++;
                        continue; // 已入库, 跳过
                    }
                    try
                    {
                        var rec = processor.ParseAndClassify(p);
                        if (rec != null)
                        {
                            records.Add(rec);
                            // 逐条"跑码"日志
                            Logger.Info($"[解析] {rec.Model} | {rec.Result} | {Path.GetFileName(p)}");
                        }
                    }
                    catch (Exception ex) { Logger.Error($"[历史扫描] 解析失败: {p} | {ex.Message}"); }
                    processed++;
                }
                totalInserted += _db.BatchInsert(records);
                AppState.SetScanProgress(parsed: processed);
                // 每批刷新一次统计, 左侧计数随之增长
                RefreshStats();
            }
            Logger.Info($"[历史扫描] 结束 | xml={allFiles.Count} | 插入={totalInserted}");
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

    // ==================== 实时监控 ====================
    private void OnFileCreated(object sender, FileSystemEventArgs e) => ScheduleProcess(e.FullPath);
    private void OnFileRenamed(object sender, RenamedEventArgs e) => ScheduleProcess(e.FullPath);

    private void ScheduleProcess(string path)
    {
        Task.Run(() =>
        {
            try { ProcessRealtime(path); }
            catch (Exception ex) { Logger.Error($"[错误] 处理异常: {path} | {ex.Message}"); }
        });
    }

    private void ProcessRealtime(string path)
    {
        if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return;
        if (!WaitForStable(path)) { Logger.Warning($"[跳过] 文件未稳定: {path}"); return; }

        var processor = new Processor(_cfg, _stationId);
        var rec = processor.ParseAndClassify(path);
        if (rec == null) return;

        try
        {
            _db.InsertOne(rec);
            Logger.Info($"[入库] {rec.Model} | {rec.Result} | {Path.GetFileName(path)}");
        }
        catch (Exception ex) { Logger.Error($"[错误] 入库失败: {path} | {ex.Message}"); return; }

        RefreshStats();

        // FAIL 推送 + 桌面提示(扫描中/UNKNOWN 不推)
        if (rec.Result == "FAIL")
        {
            if (rec.StationId == "UNKNOWN")
                Logger.Info($"[跳过推送-无机台号] FAIL / {rec.Model} / {rec.Sn}");
            else if (!_initialScanComplete)
                Logger.Info($"[跳过推送-扫描中] FAIL / {rec.Model} / {rec.Sn}");
            else
            {
                // 先弹本机桌面提示（不依赖网络，就算 webhook 没配也能看到）
                try { DesktopNotifier.NotifyFail(rec); }
                catch (Exception ex) { Logger.Warning($"桌面提示失败: {ex.Message}"); }
                try
                {
                    FeishuNotifier.SendFailAlert(_cfg.WebhookUrl, rec).GetAwaiter().GetResult();
                    Logger.Info($"[飞书推送] FAIL / {rec.Model} / {rec.Sn}");
                }
                catch (Exception ex) { Logger.Error($"[错误] 飞书推送失败: {path} | {ex.Message}"); }
            }
        }
    }

    /// <summary>等待文件大小稳定(避免读到写一半的文件)。</summary>
    private bool WaitForStable(string path, int stableChecks = 2, int intervalMs = 500, int timeoutMs = 10000)
    {
        long prev = -1;
        int same = 0;
        var start = Environment.TickCount64;
        while (same < stableChecks)
        {
            if (Environment.TickCount64 - start > timeoutMs) return false;
            long size;
            try { size = new FileInfo(path).Length; } catch { return false; }
            if (size == 0) { same = 0; prev = size; Thread.Sleep(intervalMs); continue; }
            if (size == prev) same++;
            else { same = 0; prev = size; }
            Thread.Sleep(intervalMs);
        }
        return true;
    }
}

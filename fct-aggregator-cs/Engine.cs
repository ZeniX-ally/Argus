using System.Collections.Concurrent;

namespace FctAggregator;

/// <summary>
/// 后台引擎: 历史扫描 + 实时文件监控 + FAIL 推送。
/// </summary>
public class Engine
{
    private readonly AppConfig _cfg;
    private readonly Database _db;
    private readonly string _stationId;
    private readonly AgentPusher _pusher;
    private readonly List<FileSystemWatcher> _watchers = new();
    private volatile bool _initialScanComplete = false;
    private CancellationTokenSource _cts = new();

    // 未稳定文件重试：写入超时(WaitForStable 10s)不直接丢弃，延迟重试几轮再放弃（防大文件/慢盘实时路径永久漏数据）
    private const int MaxStableRetries = 3;
    private const int StableRetryDelayMs = 30000;
    private readonly ConcurrentQueue<(string Path, int Attempt, long DueAt)> _retryQueue = new();
    private Thread? _retryThread;

    public Database Db => _db;
    public string ResolvedStationId => _stationId;
    public AppConfig Config => _cfg;
    public AgentPusher Pusher => _pusher;

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

        // 机台聚合推送器（Phase 1）：FAIL 事件文件 + 心跳；未配置时静默不动作
        _pusher = new AgentPusher(cfg, _stationId, _db);
        _pusher.Init();

        AppState.StationId = sid;
        AppState.WebhookConfigured = !string.IsNullOrEmpty(cfg.WebhookUrl);

        Logger.Info($"服务启动 | station_id={(string.IsNullOrEmpty(sid) ? "Auto" : sid)} | results_root={cfg.ResultsRoot}");
        Logger.Info($"数据库: {dbFile}");
        Logger.Info($"Webhook: {(AppState.WebhookConfigured ? "已配置" : "未配置")}");
        Logger.Info($"FCT.ini: {FctIni.AutoFindIni() ?? "未找到(设备状态页将显示诊断)"} | config={cfg.FctIniPath}");
        Logger.Info($"程序目录(BaseDir): {AppConfig.BaseDir}");

        // 待办/维修状态变更 → 飞书推送（v3.2.0：卡片格式与 FAIL 告警一致）
        // 订阅在构造期：所有改状态的路径（看板拖放/列表右键/编辑表单/补填）都经 Database 的
        // UpdateMaintenance* 两个方法，事件在「状态真的变了」时才触发，天然去重。
        _db.MaintenanceStatusChanged += (rec, from, to) =>
        {
            try
            {
                Logger.Info($"[飞书推送] 待办 #{rec.Id} 状态: {MaintenanceMeta.ZhOf(from)} -> {MaintenanceMeta.ZhOf(to)}");
                // 状态变更发生在 UI 线程（拖放/菜单），网络 POST 放后台，避免卡界面
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

        // 未稳定文件重试后台线程（每 5s 检查一次到期项）
        _retryThread = new Thread(RetryLoop) { IsBackground = true, Name = "stable-retry" };
        _retryThread.Start();

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
        _pusher.Stop();
        _cts.Cancel();
        try { _retryThread?.Join(3000); } catch { }
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
                // ⚠ 用 GetRange 而非 Skip(i).Take()：Skip 每次从头迭代是 O(i)，
                // 百万级文件时总代价 O(n²)（曾拖慢历史扫描）；GetRange 是 O(batch)。
                var take = Math.Min(batchSize, allFiles.Count - i);
                var batch = allFiles.GetRange(i, take).Select(Path.GetFullPath).ToList();
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
            // 历史扫描完就把近 N 天的不良并入待办登记表（大项合并、永久保留）
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

    /// <summary>
    /// 把「近 <c>todo_scan_days</c> 天」的新不良并入待办登记表。
    /// 增量(靠水位线)、按大项合并、登记后永久保留。失败不影响采集主流程。
    /// </summary>
    private void SyncTodos(string tag)
    {
        try
        {
            var n = _db.SyncTodoItems(_cfg.TodoScanDays);
            if (n > 0) Logger.Info($"[待办]{tag} 新登记 {n} 条待办(近 {_cfg.TodoScanDays} 天、同类项已合并)");
        }
        catch (Exception ex) { Logger.Warning($"[待办]{tag} 同步失败: {ex.Message}"); }
    }

    // ==================== 实时监控 ====================
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
            // 新不良立即并入待办登记表 —— 就算用户不开待办页也不会漏登记
            SyncTodos("实时");
            if (rec.StationId == "UNKNOWN")
                Logger.Info($"[跳过推送-无机台号] FAIL / {rec.Model} / {rec.Sn}");
            else if (!_initialScanComplete)
                Logger.Info($"[跳过推送-扫描中] FAIL / {rec.Model} / {rec.Sn}");
            else
            {
                // 先弹本机桌面提示（不依赖网络，就算 webhook 没配也能看到）
                try { DesktopNotifier.NotifyFail(rec); }
                catch (Exception ex) { Logger.Warning($"桌面提示失败: {ex.Message}"); }
                // 飞书 POST 放后台：同步等待最坏 10s 超时 × 3 次重试 ≈ 30s+，FAIL 风暴时会把实时处理线程堵住
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

    /// <summary>
    /// 等待文件大小稳定(避免读到写一半的文件)。
    /// ⚠ 坑：首次采样(prev=-1)必然"不同"，所以"连续 <paramref name="stableChecks"/> 次相同"
    /// 实际要采样 stableChecks+1 次 —— 旧实现稳定检查 2 次要 3 次采样(1.5s)，
    /// 每个实时事件都白等 1.5s。修法：先做一次"基线"采样当 prev，再进入稳定计数。
    /// </summary>
    private bool WaitForStable(string path, int stableChecks = 2, int intervalMs = 500, int timeoutMs = 10000)
    {
        long prev;
        try { prev = new FileInfo(path).Length; }
        catch { return false; }

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

    /// <summary>未稳定文件重试线程：每 5s 扫一遍到期项重新入处理队列。</summary>
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

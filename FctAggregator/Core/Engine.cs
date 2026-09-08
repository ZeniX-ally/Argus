using System.Collections.Concurrent;
using System.Text.Json;

namespace FctAggregator;

public class Engine
{
    private readonly AppConfig _cfg;
    private readonly Database _db;
    private readonly DbMaintenance _maintenance;
    private readonly string _stationId;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _watcherLock = new();
    private readonly HashSet<string> _watchedDirs = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _modelRescanTimer; // 审计 A3：周期重扫型号目录，运行期新型号动态挂监控
    private long _lastStatsRefreshTicks; // 审计 A2：入库热路径统计刷新节流
    private volatile bool _initialScanComplete = false;
    private CancellationTokenSource _cts = new();

    private readonly ConcurrentDictionary<string, byte> _inFlight = new(); // 在途路径去重（Changed 高频连发防护）
    private readonly string _retryQueueFile; // 重试队列落盘文件（Stop 写 / Start 读回，治退出即丢）

    private const int MaxStableRetries = 3;
    private const int StableRetryDelayMs = 30000;
    private readonly ConcurrentQueue<(string Path, int Attempt, long DueAt)> _retryQueue = new();
    private Thread? _retryThread;
    private static readonly HashSet<string> _seenFailReasons = new(StringComparer.OrdinalIgnoreCase);

    public Database Db => _db;
    public string ResolvedStationId => _stationId;
    public AppConfig Config => _cfg;
    public Engine(AppConfig cfg)
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
        _retryQueueFile = Path.Combine(dataDir, "retry_queue.json");
        var dbFile = Path.Combine(dataDir, $"{(string.IsNullOrEmpty(sid) ? "fct" : sid)}.db");
        _db = new Database(dbFile);
        _maintenance = DbMaintenance.StartFor(cfg, _db);

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
            StartAutoBackfillSn(); // 无扫描可做，仍触发一次 SN 补账（幂等）
            return;
        }

        var models = DiscoverModels(root);
        AppState.ModelsCount = models.Count;
        AppState.SetStatus("running");
        Logger.Info($"发现型号目录: {string.Join(", ", models)}");

        LoadRetryQueue(); // 先恢复上次停机落盘的未处理重试条目
        _retryThread = new Thread(RetryLoop) { IsBackground = true, Name = "stable-retry" };
        _retryThread.Start();

        foreach (var model in models)
            foreach (var cat in new[] { "Online", "Offline" })
            {
                var dir = Path.Combine(root, cat, model);
                Directory.CreateDirectory(dir);
                AttachWatcherFor(dir);
            }

        // 审计 A3：watcher 仅在启动时对已发现型号建立，运行期新增型号目录曾永不监控静默丢数——周期重扫补挂
        _modelRescanTimer = new System.Threading.Timer(_ => RescanModels(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        if (models.Count > 0 && !_cfg.SkipHistoricalScan)
        {
            Task.Run(() => HistoricalScan(models, root));
        }
        else
        {
            // 开机只补今日：skip 历史全扫或当前无可扫型号时，至少把"程序打开前已产出的今日文件"扫入。
            // 否则今日 KPI 只随实时事件增长，早于本次启动的文件不显示（线上反馈 v3.31.0 bug）。
            var today = DateTime.Now.ToString("yyyyMMdd");
            if (_cfg.SkipHistoricalScan) Logger.Info($"历史扫描已跳过，仅补扫今日({today})目录…");
            Task.Run(() => HistoricalScan(models, root, today));
        }
    }

    private void AutoBackfillSn()
    {
        try
        {
            var processor = new Processor(_cfg, _stationId, Parsing.ParserRegistry.Instance, _db);
            var n = BackfillTool.BackfillMissingSn(_db, processor, _cts.Token);
            if (n > 0) RefreshStats();
        }
        catch (Exception ex) { Logger.Warning($"[SN补账] 自动回填异常: {ex.Message}"); }
    }

    private void StartAutoBackfillSn()
    {
        // SN 自动补账：启动后补齐历史空 SN 记录（幂等，无缺失秒回；完成后刷新主页统计）。
        // 延迟到历史扫描完成后触发，避免与扫描批次写入竞争 SQLite 写锁（M7）。
        Task.Run(AutoBackfillSn);
    }

    public void Stop()
    {
        try { DeviceSampleRecorder.Instance.Stop(); } catch { }
        _maintenance.Stop();
        _cts.Cancel();
        try { _retryThread?.Join(3000); } catch { }
        try { _modelRescanTimer?.Dispose(); } catch { }
        foreach (var w in _watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
        _watchers.Clear();
        lock (_watcherLock) _watchedDirs.Clear();
        SaveRetryQueue(); // 未处理重试条目落盘，下次启动恢复（M5：退出即丢）
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

    private void HistoricalScan(List<string> models, string root, string? onlyDate = null)
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
                    var scanDir = onlyDate == null ? modelDir : Path.Combine(modelDir, onlyDate);
                    if (!Directory.Exists(scanDir)) continue;
                    foreach (var f in Directory.EnumerateFiles(scanDir, "*.xml", SearchOption.AllDirectories))
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
                try { totalInserted += _db.BatchInsert(records); }
                catch (Exception ex)
                {
                    // 批量级保护：单批写锁冲突（database is locked）不中止整轮扫描，短等待后重试一次，再失败放弃该批继续下一批
                    Logger.Error($"[历史扫描] 批次入库失败(起始索引 {i}, {records.Count} 条): {ex.Message}，2s 后重试一次");
                    Thread.Sleep(2000);
                    try { totalInserted += _db.BatchInsert(records); }
                    catch (Exception ex2) { Logger.Error($"[历史扫描] 批次重试仍失败(起始索引 {i})，放弃该批继续: {ex2.Message}"); }
                }
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
            StartAutoBackfillSn(); // 扫描结束后再补 SN，避免写锁竞争
        }
    }

    private void RefreshStats() => AppState.RefreshStats(_db, _stationId, _cfg.ResultsRoot);

    /// <summary>审计 A2：入库热路径统计刷新节流（10s 一次）——RefreshStats 每条实时记录都全盘枚举 XML + 3 条聚合 SQL，
    /// 网络共享时可秒级阻塞采集。KPI 实时性由 UI 周期 RefreshDbStats（库内轻量刷新）兜底，磁盘计数低频化。</summary>
    private void RefreshStatsThrottled()
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastStatsRefreshTicks);
        if (nowTicks - last < TimeSpan.FromSeconds(10).Ticks) return;
        if (Interlocked.CompareExchange(ref _lastStatsRefreshTicks, nowTicks, last) != last) return;
        RefreshStats();
    }

    /// <summary>审计 A3：挂接型号目录监控（幂等，已挂目录跳过），供启动与周期重扫共用。</summary>
    private void AttachWatcherFor(string dir)
    {
        lock (_watcherLock)
        {
            if (!_watchedDirs.Add(dir)) return;
            var w = new FileSystemWatcher(dir, "*.xml")
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            w.Created += OnFileCreated;
            w.Changed += OnFileChanged; // 半成品文件写完后再触发一次（配合 WaitForStable + 入库幂等，重复触发无害）
            w.Renamed += OnFileRenamed;
            _watchers.Add(w);
        }
        Logger.Info($"监控已启动: {dir}");
    }

    /// <summary>审计 A3：周期重扫型号目录，为运行期新增型号补挂监控（幂等；不重复挂已监控目录）。</summary>
    private void RescanModels()
    {
        try
        {
            var root = _cfg.ResultsRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
            var models = DiscoverModels(root);
            AppState.ModelsCount = models.Count;
            foreach (var model in models)
                foreach (var cat in new[] { "Online", "Offline" })
                {
                    var dir = Path.Combine(root, cat, model);
                    if (!Directory.Exists(dir)) continue;
                    AttachWatcherFor(dir);
                }
        }
        catch (Exception ex) { Logger.Warning($"[监控] 新型号周期重扫失败: {ex.Message}"); }
    }

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
    private void OnFileChanged(object sender, FileSystemEventArgs e) => ScheduleProcess(e.FullPath);
    private void OnFileRenamed(object sender, RenamedEventArgs e) => ScheduleProcess(e.FullPath);

    private void ScheduleProcess(string path) => ScheduleProcess(path, 0);

    private void ScheduleProcess(string path, int attempt)
    {
        // Changed 事件高频连发防护：同一路径在途时忽略本次触发；
        // 漏触发由 WaitForStable 未稳定 / 解析暂时失败入重试队列兜底，入库本身 xml_path UNIQUE 幂等
        if (attempt == 0 && !_inFlight.TryAdd(path, 0)) return;
        Task.Run(() =>
        {
            try { ProcessRealtime(path, attempt); }
            catch (Exception ex) { Logger.Error($"[错误] 处理异常: {path} | {ex.Message}"); }
            finally { if (attempt == 0) _inFlight.TryRemove(path, out _); }
        });
    }

    private void ProcessRealtime(string path, int attempt)
    {
        if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return;
        if (!WaitForStable(path))
        {
            if (attempt < MaxStableRetries)
            {
                if (EnqueueRetry(path, attempt))
                    Logger.Warning($"[重试] 文件未稳定，{StableRetryDelayMs / 1000}s 后重试({attempt + 1}/{MaxStableRetries}): {path}");
            }
            else
                Logger.Warning($"[跳过] 文件持续未稳定，已达重试上限({MaxStableRetries}): {path}");
            return;
        }

        var processor = new Processor(_cfg, _stationId, Parsing.ParserRegistry.Instance, _db);
        var rec = processor.ParseAndClassify(path, out var transientFailure);
        if (rec == null)
        {
            if (transientFailure)
            {
                // 暂时性失败（半成品 XML/读取被占用）：入重试队列，文件写完后再试，避免永久漏采；永久性失败保持原样不重试
                if (attempt < MaxStableRetries)
                {
                    if (EnqueueRetry(path, attempt))
                        Logger.Warning($"[重试] 解析暂时失败，{StableRetryDelayMs / 1000}s 后重试({attempt + 1}/{MaxStableRetries}): {path}");
                }
                else
                    Logger.Warning($"[跳过] 解析持续失败，已达重试上限({MaxStableRetries}): {path}");
            }
            return;
        }

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

        long insertedId = 0; // 审计 A2：复用入库返回 id，免去 TDMS 链路逐条反查
        try
        {
            insertedId = _db.InsertOneReturnId(rec);
            Logger.Info($"[入库] {rec.Model} | {rec.Result} | {Path.GetFileName(path)}");
        }
        catch (Exception ex) { Logger.Error($"[错误] 入库失败: {path} | {ex.Message}"); return; }

        // 正常态自学习：PASS 测量值入模（开关关 no-op，吞异常不阻塞采集）
        NormalLearners.ObserveMeasurement(_db, _cfg, rec);

        // 规格06期3：TDMS 特征化实时入队（单消费者队列，不阻塞采集链路；开关关/tdms_root 空静默）
        if (_cfg.AnalyzeTdmsEnabled && !string.IsNullOrWhiteSpace(_cfg.TdmsRoot))
        {
            try
            {
                // 审计 A2：优先复用 InsertOne 返回的 id，仅在 INSERT OR IGNORE 未命中（重复事件）时才反查
                var rid = insertedId > 0 ? insertedId : _db.GetRecordIdsByPaths(new[] { Path.GetFullPath(path) }).Values.FirstOrDefault();
                if (rid > 0) TdmsFeatureCollector.Enqueue(rec, rid);
            }
            catch (Exception ex) { Logger.Warning($"[TDMS] 特征化入队失败: {ex.Message}"); }
        }

        RefreshStatsThrottled();

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

    private bool EnqueueRetry(string path, int attempt)
    {
        // 队列去重：同路径已有未处理条目时跳过，防止 Changed 高频触发塞爆队列
        if (_retryQueue.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
            return false;
        _retryQueue.Enqueue((path, attempt, Environment.TickCount64 + StableRetryDelayMs));
        return true;
    }

    // ── 重试队列落盘持久化：Stop 写 data/retry_queue.json（原子替换），Start 读回入队（治 M5 退出即丢） ──

    private void LoadRetryQueue()
    {
        try
        {
            if (!File.Exists(_retryQueueFile)) return;
            var items = DeserializeRetryQueue(File.ReadAllText(_retryQueueFile));
            foreach (var (p, attempt) in items)
                _retryQueue.Enqueue((p, attempt, Environment.TickCount64 + StableRetryDelayMs));
            if (items.Count > 0)
            {
                Logger.Info($"[重试队列] 启动恢复 {items.Count} 条未处理路径（{StableRetryDelayMs / 1000}s 后陆续重试）");
                File.Delete(_retryQueueFile);
            }
        }
        catch (Exception ex) { Logger.Warning($"[重试队列] 恢复失败，忽略: {ex.Message}"); } // 文件损坏不阻断启动
    }

    private void SaveRetryQueue()
    {
        var items = new List<(string Path, int Attempt)>();
        while (_retryQueue.TryDequeue(out var it)) items.Add((it.Path, it.Attempt));
        if (items.Count == 0) { try { File.Delete(_retryQueueFile); } catch { } return; }
        try
        {
            var tmp = _retryQueueFile + ".tmp";
            File.WriteAllText(tmp, SerializeRetryQueue(items));
            File.Move(tmp, _retryQueueFile, overwrite: true); // 原子替换
            Logger.Info($"[重试队列] 停机落盘 {items.Count} 条未处理路径: {_retryQueueFile}");
        }
        catch (Exception ex)
        {
            // 写盘失败不能丢条目：回灌内存队列（进程仍在时还能被处理；下次 Stop 再试落盘）
            foreach (var it in items) _retryQueue.Enqueue((it.Path, it.Attempt, Environment.TickCount64 + StableRetryDelayMs));
            Logger.Warning($"[重试队列] 落盘失败，已回灌 {items.Count} 条到内存队列: {ex.Message}");
        }
    }

    /// <summary>重试队列序列化（纯函数，SelfTest 往返校验用）。</summary>
    public static string SerializeRetryQueue(List<(string Path, int Attempt)> items)
    {
        var dtos = items.Select(x => new RetryItemDto { path = x.Path, attempt = x.Attempt }).ToList();
        return JsonSerializer.Serialize(dtos);
    }

    /// <summary>重试队列反序列化（纯函数）：坏 JSON/空内容返回空列表，不抛异常。</summary>
    public static List<(string Path, int Attempt)> DeserializeRetryQueue(string json)
    {
        var items = new List<(string Path, int Attempt)>();
        try
        {
            var dtos = JsonSerializer.Deserialize<List<RetryItemDto>>(json);
            if (dtos != null)
                foreach (var d in dtos)
                    if (!string.IsNullOrEmpty(d.path))
                        items.Add((d.path, d.attempt));
        }
        catch { }
        return items;
    }

    private sealed class RetryItemDto
    {
        public string path { get; set; } = "";
        public int attempt { get; set; }
    }
}

using System.Collections.Concurrent;
using System.Text.Json;

namespace FctAggregator;

public class Engine
{
    private readonly AppConfig _cfg;
    private readonly Database _db;
    private readonly DbMaintenance _maintenance;
    private readonly FeishuFailBatcher _failBatcher; // FAIL 告警批处理（窗口内合并成一张卡，治批量不良刷屏）
    private readonly string _stationId;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _watcherLock = new();
    private readonly HashSet<string> _watchedDirs = new(StringComparer.OrdinalIgnoreCase);
    private bool _watchersStopped;
    private System.Threading.Timer? _modelRescanTimer; // 审计 A3：周期重扫型号目录，运行期新型号动态挂监控
    private System.Threading.Timer? _collectHealthTimer; // 采集健康巡检（漏采/解析失败堆积主动告警）
    private long _lastStatsRefreshTicks; // 审计 A2：入库热路径统计刷新节流
    private volatile bool _initialScanComplete = false;
    private CancellationTokenSource _cts = new();

    private readonly ConcurrentDictionary<string, byte> _inFlight = new(InFlightPathComparer); // 在途路径去重（Changed 高频连发防护）
    private readonly ConcurrentDictionary<string, byte> _loggedUnknownDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _retryQueueFile; // 重试队列落盘文件（Stop 写 / Start 读回，治退出即丢）

    // 审计修复：原为 3 次 × 30s ≈ 3.5 分钟，覆盖不了本类注释自己声明的「VACUUM 持写锁数分钟」
    // （Database.Vacuum 的 CommandTimeout=600s）——超过预算的批次会被判「已达重试上限」直接放弃、
    // 不再入队，本次进程内静默漏采。提到 20 次 ≈ 10 分钟，与 VACUUM 上限对齐。
    private const int MaxStableRetries = 20;
    private const int StableRetryDelayMs = 30000;
    private readonly ConcurrentQueue<(string Path, int Attempt, long DueAt)> _retryQueue = new();
    private Thread? _retryThread;
    private static readonly HashSet<string> _seenFailReasons = new(StringComparer.OrdinalIgnoreCase);

    public Database Db => _db;
    public string ResolvedStationId => _stationId;
    public AppConfig Config => _cfg;
    /// <summary>重试队列当前积压条数（采集异常告警的触发维度之一）。</summary>
    public int RetryQueueDepth => _retryQueue.Count;

    /// <summary>在途路径比较器：Windows 下同一文件大小写不同视为一条。</summary>
    public static StringComparer InFlightPathComparer { get; } = StringComparer.OrdinalIgnoreCase;

    /// <summary>占在途坑。attempt 不跳过（重试与实时 Changed 共用），失败表示同路径已在处理。</summary>
    public static bool TryEnterInFlight(ConcurrentDictionary<string, byte> map, string path, int attempt)
    {
        _ = attempt;
        return map.TryAdd(path, 0);
    }

    public static void ExitInFlight(ConcurrentDictionary<string, byte> map, string path)
        => map.TryRemove(path, out _);

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

        // 审计修复：把（可能来自 IP 识别的）机台号注入解析器注册表。Program.cs 初始化注册表时只有
        // cfg.StationId，IP 识别结果从不回注 → 记录 station_id 落 UNKNOWN → FAIL 告警被跳过、
        // 机台维度良率/历史查询全部错位。
        try { Parsing.ParserRegistry.DefaultStation = sid; } catch { }

        var dataDir = Path.Combine(AppConfig.BaseDir, "data");
        Directory.CreateDirectory(dataDir);
        _retryQueueFile = Path.Combine(dataDir, "retry_queue.json");
        var dbFile = Path.Combine(dataDir, $"{(string.IsNullOrEmpty(sid) ? "fct" : sid)}.db");
        _db = new Database(dbFile);
        _maintenance = DbMaintenance.StartFor(cfg, _db);
        // 每日摘要卡需要「重试队列积压」与「已解析机台号」，两者只有 Engine 知道
        _maintenance.RetryQueueDepthProvider = () => RetryQueueDepth;
        _maintenance.StationIdProvider = () => _stationId;
        _failBatcher = new FeishuFailBatcher(cfg, _db);

        try { DeviceSampleRecorder.Instance.Start(); } catch { }

        AppState.StationId = sid;
        AppState.WebhookConfigured = !string.IsNullOrEmpty(cfg.WebhookUrl);

        Logger.Info($"服务启动 | station_id={(string.IsNullOrEmpty(sid) ? "Auto" : sid)} | results_root={cfg.ResultsRoot}");
        Logger.Info($"数据库: {dbFile}");
        try
        {
            var st = _db.GetStorageStats();
            Logger.Info($"[存储] {st.Format()}");
            var health = StorageOptimizer.Evaluate(_db, _cfg);
            Logger.Info($"[存储] {health.FormatHeadline()}");
            if (health.Score < 70)
            {
                foreach (var issue in health.Issues.Take(3))
                    Logger.Warning($"[存储] {issue}");
                Logger.Warning("[存储] 请到「调试工具 → 智能优化」立即处理，或等待定时维护自动执行。");
            }
        }
        catch { }
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
        // 审计修复：LoadRetryQueue 必须在任何早退之前。原实现位于 results_root 存在性检查之后——
        // 早退时内存队列为空，Stop 的 SaveRetryQueue 会把磁盘上遗留的 retry_queue.json 静默删除
        // （无任何日志），那批待重试路径就此漏采（skip_historical_scan=true 时永不补回）。
        LoadRetryQueue();

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

        // 采集健康巡检：漏采/解析失败堆积此前只能靠翻日志发现（parse_failure_log 只进不出），
        // 这里周期评估并主动推飞书。首轮延后 5 分钟，避开启动扫描高峰。
        _collectHealthTimer = new System.Threading.Timer(_ => CheckCollectHealth(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        if (models.Count > 0)
        {
            RefreshStats();
            Task.Run(() => RunStartupScans(models, root));
        }
        else
        {
            // 审计修复：型号目录为空时原实现从不置 _initialScanComplete=true。之后 RescanModels 会补挂
            // watcher 让文件正常入库，但本进程内每条 FAIL 都被 [跳过推送-扫描中] 吞掉——告警通道静默
            // 死亡直到重启。无可扫即视为扫描完成，放行实时告警。
            Logger.Warning("[启动扫描] 未发现型号目录，跳过启动扫描（新增目录由 5 分钟周期重扫补挂监控）");
            FinishHistoricalScan();
        }
    }

    /// <summary>启动扫描：先今日目录（可覆盖重解析 + 补推 FAIL），再按需全量历史（跳过已入库路径）。</summary>
    private void RunStartupScans(List<string> models, string root)
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        Logger.Info($"[历史扫描] 优先补扫今日({today})目录（含已入库路径重解析）…");
        HistoricalScan(models, root, today, reparseExisting: true, markScanComplete: false);
        FlushTodayFailAlerts(today);
        RefreshStats();

        if (!_cfg.SkipHistoricalScan)
        {
            Logger.Info("[历史扫描] 今日补扫完成，开始全量历史扫描（已入库路径跳过）…");
            HistoricalScan(models, root, onlyDate: null, reparseExisting: false, markScanComplete: true);
        }
        else
        {
            Logger.Info($"历史扫描已跳过全量，仅完成今日({today})补扫");
            FinishHistoricalScan();
        }
    }

    private void FinishHistoricalScan()
    {
        AppState.SetScanProgress(phase: "done");
        _initialScanComplete = true;
        AppState.HistoricalScanComplete = true;
        RefreshStats();
        Logger.Info("历史扫描完成，FAIL 告警推送已启用");
        StartAutoBackfillSn();
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
        // 审计修复：_cts.Cancel() 不会唤醒 RetryLoop 的 Thread.Sleep(5000)，Join(3000) 必然超时返回，
        // 于是 SaveRetryQueue 可能在重试线程仍存活时 drain 队列——之后该线程回存的条目只留在内存里、
        // 随进程消失。显式 Interrupt 让循环立刻退出（其 Sleep 已 catch ThreadInterruptedException 并 break）。
        try { _retryThread?.Interrupt(); } catch { }
        try { _retryThread?.Join(3000); } catch { }
        try { _modelRescanTimer?.Dispose(); } catch { }
        try { _collectHealthTimer?.Dispose(); } catch { }
        // 审计：_watchers 的写受 _watcherLock 保护，这里原来无锁遍历 + Clear，
        // 与 RescanModels（5 分钟周期）并发时 foreach 会抛 InvalidOperationException 逃出 Stop→Program.Main；
        // 且 Clear 之后新加的 watcher 永不释放（句柄泄漏且仍向已停止的引擎投递事件）。
        lock (_watcherLock)
        {
            _watchersStopped = true;
            foreach (var w in _watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
            _watchers.Clear();
            _watchedDirs.Clear();
        }
        SaveRetryQueue(); // 未处理重试条目落盘，下次启动恢复（M5：退出即丢）

        // 冲刷未发出的 FAIL 告警（有界等待：在途一轮没发完就放弃，未标记的记录由下次启动的「今日补推」兜底）
        try
        {
            if (_failBatcher.PendingCount > 0)
            {
                if (_failBatcher.FlushAsync().Wait(TimeSpan.FromSeconds(3)))
                    Logger.Info($"[飞书推送] 退出前已冲刷 FAIL 告警队列");
                else
                    Logger.Warning($"[飞书推送] 退出前仍有 {_failBatcher.PendingCount} 条 FAIL 未发出，下次启动补推");
            }
        }
        catch (Exception ex) { Logger.Warning($"[飞书推送] 退出冲刷异常: {ex.Message}"); }
        _failBatcher.Dispose();
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
                  {
                      var key = catDir + "|" + name;
                      if (_loggedUnknownDirs.TryAdd(key, 0))
                          Logger.Warning($"[pending_review] 未知型号目录: {name} at {catDir}（只报一次，不入库、不计良率）");
                  }
            }
        }
        return models.OrderBy(x => x).ToList();
    }

    private void HistoricalScan(List<string> models, string root, string? onlyDate = null,
        bool reparseExisting = false, bool markScanComplete = true)
    {
        try
        {
            if (string.IsNullOrEmpty(_stationId))
                Logger.Warning("[历史扫描] station_id 未配置且无法自动检测, 记录将使用解析器回落值");
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
            Logger.Info($"[历史扫描][扫描阶段完成] 总文件={allFiles.Count} onlyDate={onlyDate ?? "ALL"} reparse={reparseExisting}");

            AppState.SetScanProgress(phase: "parsing", total: allFiles.Count, parsed: 0);
            const int batchSize = 100;
            int processed = 0, totalInserted = 0, totalUpdated = 0;
            var scanCancelled = false;
            _db.RunWithDeferredFailMonthCsv(() =>
            {
            for (int i = 0; i < allFiles.Count; i += batchSize)
            {
                if (_cts.IsCancellationRequested) { scanCancelled = true; return; }
                var take = Math.Min(batchSize, allFiles.Count - i);
                var batch = allFiles.GetRange(i, take).Select(Path.GetFullPath).ToList();
                HashSet<string>? existing = reparseExisting ? null : _db.GetExistingPaths(batch);

                var records = new List<TestRecord>();
                foreach (var p in batch)
                {
                    if (existing != null && existing.Contains(p))
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
                            Logger.Debug($"[解析] {rec.Model} | {rec.Result} | {Path.GetFileName(p)}");
                        }
                    }
                    catch (Exception ex) { Logger.Error($"[历史扫描] 解析失败: {p} | {ex.Message}"); }
                    processed++;
                }
                var batchOutcomes = new List<IngestResult>();
                try
                {
                    totalInserted += _db.BatchUpsert(records, null, batchOutcomes);
                    totalUpdated += batchOutcomes.Count(o => o.WasUpdated);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[历史扫描] 批次入库失败(起始索引 {i}, {records.Count} 条): {ex.Message}，2s 后重试一次");
                    Thread.Sleep(2000);
                    try
                    {
                        batchOutcomes.Clear();
                        totalInserted += _db.BatchUpsert(records, null, batchOutcomes);
                        totalUpdated += batchOutcomes.Count(o => o.WasUpdated);
                    }
                    catch (Exception ex2)
                    {
                        // B7/A3：不再「放弃该批继续」——整批路径入重试队列（BatchUpsert 按 xml_path 幂等，
                        // 重试不会重复入库；队列落盘，重启也不丢）。VACUUM 持写锁数分钟时这是唯一的兜底。
                        Logger.Error($"[历史扫描] 批次重试仍失败(起始索引 {i})，{records.Count} 条全部转入重试队列: {ex2.Message}");
                        foreach (var rec in records)
                            if (!string.IsNullOrEmpty(rec.XmlPath)) EnqueueRetry(rec.XmlPath, 0);
                        records.Clear();
                    }
                }
                for (int ri = 0; ri < records.Count && ri < batchOutcomes.Count; ri++)
                {
                    var outcome = batchOutcomes[ri];
                    if (outcome.RecordId <= 0) continue;
                    AfterIngest(records[ri], outcome, pushFail: false);
                }
                AppState.SetScanProgress(parsed: processed);
                // 每批都全量重算统计会得到 批数 x 6 条聚合 SQL（含整表 COUNT）；改走 10s 节流，收尾由 FinishHistoricalScan 刷终值
                RefreshStatsThrottled();
            }
            });
            if (scanCancelled) return;
            Logger.Info($"[历史扫描] 结束 | xml={allFiles.Count} | 新插入={totalInserted} | 覆盖更新={totalUpdated}");
            SyncTodos("历史扫描后");
        }
        catch (Exception ex)
        {
            Logger.Error($"历史扫描异常: {ex.Message}");
        }
        finally
        {
            if (markScanComplete) FinishHistoricalScan();
        }
    }

    private void FlushTodayFailAlerts(string todayYmd)
    {
        var pending = _db.ListTodayFailsNeedingAlert(todayYmd, string.IsNullOrEmpty(_stationId) ? null : _stationId);
        if (pending.Count == 0) return;
        Logger.Info($"[补推] 今日未推送 FAIL {pending.Count} 条，开始补发告警…");
        foreach (var rec in pending)
            TryPushFailAlert(rec, allowDuringScan: true);
    }

    private void RefreshStats() => AppState.RefreshStats(_db, _stationId, _cfg.ResultsRoot);

    /// <summary>审计 A2：入库热路径统计刷新节流（10s 一次）。RefreshStats 实际**只做库内聚合**——AppState.RefreshStats 会丢弃传进来的
    /// resultsRoot 参数，不枚举磁盘；但一次要跑 6 条聚合 SQL，其中 FetchGlobalStats 是无日期条件的整表 COUNT，产量大时仍有开销。
    /// 注意：历史扫描批循环与实时入库共用本字段的节流时间戳。</summary>
    private void RefreshStatsThrottled()
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastStatsRefreshTicks);
        if (nowTicks - last < TimeSpan.FromSeconds(10).Ticks) return;
        if (Interlocked.CompareExchange(ref _lastStatsRefreshTicks, nowTicks, last) != last) return;
        RefreshStats();
    }

    /// <summary>审计 A3：挂接型号目录监控（幂等，已挂目录跳过），供启动与周期重扫共用。</summary>
    private bool AttachWatcherFor(string dir)
    {
        lock (_watcherLock)
        {
            if (_watchersStopped) return false; // 已停止：不再新建（否则新 watcher 无人 Dispose，且向死引擎投递事件）
            if (!_watchedDirs.Add(dir)) return false;
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
        return true;
    }

    /// <summary>审计修复：新挂上监控的目录里「挂载前已落盘」的 XML 既没有 Created 事件、又不在启动快照的
    /// models 里（历史扫描只枚举启动时发现的型号）→ 永久漏采且无任何日志。这里补一次有界补扫：
    /// 只捞最近 7 天写入的文件，最多 2000 个，避免超大目录一次性灌爆处理队列。</summary>
    private void BackfillNewlyWatchedDir(string dir)
    {
        try
        {
            var since = DateTime.Now.AddDays(-7);
            int n = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*.xml", SearchOption.AllDirectories))
            {
                try { if (File.GetLastWriteTime(f) < since) continue; } catch { continue; }
                ScheduleProcess(f);
                if (++n >= 2000) break;
            }
            if (n > 0) Logger.Info($"[监控] 新目录补扫 {n} 个近 7 天文件: {dir}");
        }
        catch (Exception ex) { Logger.Warning($"[监控] 新目录补扫失败: {ex.Message}"); }
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
                    // 新挂上的目录要补扫挂载前已存在的文件（启动时挂的目录由历史扫描负责，不重复）
                    if (AttachWatcherFor(dir)) BackfillNewlyWatchedDir(dir);
                }
        }
        catch (Exception ex) { Logger.Warning($"[监控] 新型号周期重扫失败: {ex.Message}"); }
    }

    /// <summary>周期健康巡检：采集异常（漏采/解析失败堆积）+ 章节群挂 → 飞书主动告警。</summary>
    private void CheckCollectHealth()
    {
        try { CollectHealthMonitor.RunOnce(_db, _cfg, RetryQueueDepth); }
        catch (Exception ex) { Logger.Warning($"[采集健康] 巡检调度异常: {ex.Message}"); }
        try { LearnAlertMonitor.RunGroupAlerts(_db, _cfg); }
        catch (Exception ex) { Logger.Warning($"[自学习] 群挂巡检调度异常: {ex.Message}"); }
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
        if (!TryEnterInFlight(_inFlight, path, attempt)) return;
        Task.Run(() =>
        {
            try { ProcessRealtime(path, attempt); }
            catch (Exception ex) { Logger.Error($"[错误] 处理异常: {path} | {ex.Message}"); }
            finally { ExitInFlight(_inFlight, path); }
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

        IngestResult outcome;
        try { outcome = _db.UpsertTestRecord(rec); }
        catch (Exception ex)
        {
            // 原来直接 return = 永久漏采（漏 FAIL 会让良率偏高、且现场无从发现）。改走与"解析暂时失败"同一条重试队列兜底。
            Logger.Error($"[错误] 入库失败(将重试): {path} | {ex.Message}");
            if (attempt < MaxStableRetries)
            {
                if (EnqueueRetry(path, attempt))
                    Logger.Warning($"[重试] 入库失败，{StableRetryDelayMs / 1000}s 后重试({attempt + 1}/{MaxStableRetries}): {path}");
            }
            else
                Logger.Warning($"[跳过] 入库持续失败，已达重试上限({MaxStableRetries}): {path}");
            return;
        }
        if (outcome.RecordId <= 0) return;

        if (outcome.IsNew || outcome.WasUpdated)
            Logger.Info($"[入库] {rec.Model} | {rec.Result} | {Path.GetFileName(path)}{(outcome.WasUpdated ? " (更新)" : "")}");

        AfterIngest(rec, outcome, pushFail: true);
        RefreshStatsThrottled();

        if (rec.Result == "FAIL") SyncTodos("实时");
    }

    private void AfterIngest(TestRecord rec, IngestResult outcome, bool pushFail)
    {
        if (outcome.IsNew || outcome.WasUpdated)
        {
            DeviationScorer.EvaluateMeasurementRecord(_db, _cfg, rec);
            NormalLearners.ObserveMeasurement(_db, _cfg, rec);
            LearnBackfill.AdvanceWatermark(_db, LearnBackfill.MetaMeas, outcome.RecordId);

            if (_cfg.AnalyzeTdmsEnabled && !string.IsNullOrWhiteSpace(_cfg.TdmsRoot))
            {
                try { TdmsFeatureCollector.Enqueue(rec, outcome.RecordId); }
                catch (Exception ex) { Logger.Warning($"[TDMS] 特征化入队失败: {ex.Message}"); }
            }
        }

        if (!pushFail) return;
        if (outcome.NeedsFailAlert || (rec.Result == "FAIL" && !_db.IsFailAlerted(rec.XmlPath)))
            TryPushFailAlert(rec, allowDuringScan: false);
    }

    private void TryPushFailAlert(TestRecord rec, bool allowDuringScan)
    {
        if (rec.Result != "FAIL") return;
        if (rec.StationId == "UNKNOWN")
        {
            Logger.Info($"[跳过推送-无机台号] FAIL / {rec.Model} / {rec.Sn}");
            return;
        }
        if (!_initialScanComplete && !allowDuringScan)
        {
            Logger.Info($"[跳过推送-扫描中] FAIL / {rec.Model} / {rec.Sn}");
            return;
        }
        if (_db.IsFailAlerted(rec.XmlPath)) return;

        try { DesktopNotifier.NotifyFail(rec); }
        catch (Exception ex) { Logger.Warning($"桌面提示失败: {ex.Message}"); }

        // 入批处理队列：单条走原卡、窗口内多条合并成一张卡（推送成功才 MarkFailAlerted，
        // 失败留待下次启动的 FlushTodayFailAlerts 补推，不新增落盘状态）
        if (_failBatcher.Enqueue(rec))
            Logger.Info($"[飞书推送] FAIL 入队 / {rec.Model} / {rec.Sn}（待发 {_failBatcher.PendingCount}）");
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
        // 审计：catch 原在 while 之外——循环体内任何一次异常都会让本线程直接结束，
        // 此后所有入队条目直到下次重启才被处理且无任何"线程已死"标志。改为逐轮兜底。
        while (!_cts.IsCancellationRequested)
        {
            try
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
            }
            catch (Exception ex)
            {
                Logger.Error($"[重试线程] 本轮异常（线程继续）: {ex.Message}");
            }
            try { Thread.Sleep(5000); } catch (ThreadInterruptedException) { break; }
        }
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

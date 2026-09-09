using System.Globalization;

namespace FctAggregator;

public sealed class BackfillReport
{
    public long ScannedFiles;
    public long InsertedRecords;
    public long InsertedMeasurements;
    public long InsertedFailItems;
    public long TdmsRows;
    public int SnFixed;
    public bool Skipped;
    public string? SkipReason;
    public TimeSpan Elapsed;
}

/// <summary>规格06：backfill 子命令——为存量 PASS 文件补采测量值（按天水位增量 + INSERT OR IGNORE 幂等），并为历史 SN 缺失记录重读 XML 补 SN。</summary>
public static class BackfillTool
{
    public const string MetaWatermark = "backfill_last_day";

    /// <summary>从 results_root 为存量 PASS/FAIL 补采测量值、失败项、TDMS（按天水位增量，幂等）。</summary>
    public static BackfillReport RunMeasurementBackfill(
        Database db, AppConfig cfg, Processor processor, int days,
        Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var report = new BackfillReport();
        days = Math.Clamp(days, 1, 3650);
        if (!cfg.AnalyzeCollectPass && !(cfg.AnalyzeTdmsEnabled && !string.IsNullOrWhiteSpace(cfg.TdmsRoot)))
        {
            report.Skipped = true;
            report.SkipReason = "analyze_collect_pass=false 且 TDMS 未启用";
            return report;
        }

        var today = DateTime.Now.Date;
        var from = today.AddDays(-(days - 1));
        var wm = db.GetMeta(MetaWatermark);
        if (DateTime.TryParseExact(wm, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var wmd) &&
            wmd >= from && wmd < today)
            from = wmd.AddDays(1);
        // 审计：水位只升不降（多 Online/Offline×机型 目录遍历顺序下，末次写入可能小于已处理的最大日期，导致每轮整窗重扫）
        var wmMax = wm;

        var root = cfg.ResultsRoot;
        if (!Directory.Exists(root))
        {
            report.Skipped = true;
            report.SkipReason = $"results_root 不存在: {root}";
            return report;
        }

        onProgress?.Invoke($"窗口 {from:yyyy-MM-dd} ~ {today:yyyy-MM-dd}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var cat in new[] { "Online", "Offline" })
        {
            var catDir = Path.Combine(root, cat);
            if (!Directory.Exists(catDir)) continue;
            foreach (var modelDir in Directory.GetDirectories(catDir).OrderBy(p => p, StringComparer.Ordinal))
            {
                foreach (var dateDir in Directory.GetDirectories(modelDir).OrderBy(p => p, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    var dirName = Path.GetFileName(dateDir);
                    if (dirName.Length != 8 || !DateTime.TryParseExact(dirName, "yyyyMMdd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
                    if (day < from || day > today) continue;

                    var files = Directory.EnumerateFiles(dateDir, "*.xml", SearchOption.TopDirectoryOnly)
                        .OrderBy(p => p, StringComparer.Ordinal).ToList();
                    if (files.Count == 0) continue;

                    long dayRecords = 0, dayMeasurements = 0, dayFailItems = 0, dayTdmsFiles = 0, dayTdmsRows = 0;
                    for (int i = 0; i < files.Count; i += 100)
                    {
                        ct.ThrowIfCancellationRequested();
                        var chunk = files.Skip(i).Take(100).Select(Path.GetFullPath).ToList();
                        var existing = db.GetExistingPaths(chunk);
                        var ids = db.GetRecordIdsByPaths(chunk, "PASS");
                        var failIds = db.GetRecordIdsByPaths(chunk, "FAIL");
                        var newRecords = new List<TestRecord>();
                        var measureRows = new List<(TestRecord, long)>();
                        var failItemRows = new List<(TestRecord, long)>();
                        foreach (var p in chunk)
                        {
                            report.ScannedFiles++;
                            try
                            {
                                if (!existing.Contains(p))
                                {
                                    var rec = processor.ParseAndClassify(p);
                                    if (rec != null) newRecords.Add(rec);
                                }
                                else if (ids.TryGetValue(p, out var rid))
                                {
                                    var rec = processor.ParseAndClassify(p);
                                    if (rec != null && rec.Measurements.Count > 0)
                                        measureRows.Add((rec, rid));
                                }
                                else if (failIds.TryGetValue(p, out var fid))
                                {
                                    var rec = processor.ParseAndClassify(p);
                                    if (rec != null && rec.FailedTests.Count > 0)
                                        failItemRows.Add((rec, fid));
                                }
                            }
                            catch (Exception ex) { Logger.Warning($"[回填] 解析失败 {p}: {ex.Message}"); }
                        }
                        if (newRecords.Count > 0)
                            dayRecords += db.BatchInsert(newRecords);
                        if (measureRows.Count > 0)
                        {
                            db.InsertMeasurementsFor(measureRows);
                            dayMeasurements += measureRows.Sum(r => r.Item1.Measurements.Count);
                        }
                        if (failItemRows.Count > 0)
                        {
                            db.InsertFailItemsFor(failItemRows);
                            dayFailItems += failItemRows.Sum(r => r.Item1.FailedTests.Count);
                        }
                        if (cfg.AnalyzeTdmsEnabled && !string.IsNullOrWhiteSpace(cfg.TdmsRoot))
                        {
                            try
                            {
                                var briefs = db.GetRecordBriefsByPaths(chunk);
                                var (tf, tr) = TdmsFeatureCollector.CaptureBatch(db, cfg, briefs);
                                dayTdmsFiles += tf;
                                dayTdmsRows += tr;
                            }
                            catch (Exception ex) { Logger.Warning($"[回填] TDMS 特征补采失败: {ex.Message}"); }
                        }
                        if (report.ScannedFiles % 200 == 0) Thread.Sleep(100);
                    }
                    report.InsertedRecords += dayRecords;
                    report.InsertedMeasurements += dayMeasurements;
                    report.InsertedFailItems += dayFailItems;
                    report.TdmsRows += dayTdmsRows;
                    onProgress?.Invoke($"{day:yyyy-MM-dd} {Path.GetFileName(modelDir)}: 文件 {files.Count}，补测量 {dayMeasurements}");
                    var dayStr = day.ToString("yyyy-MM-dd");
                    if (string.CompareOrdinal(dayStr, wmMax) > 0)
                    {
                        db.SetMeta(MetaWatermark, dayStr);
                        wmMax = dayStr;
                    }
                }
            }
        }
        report.Elapsed = sw.Elapsed;
        return report;
    }

    public static int Run(string[] args, string? dbPathOverride = null)
    {
        var cfg = AppConfig.Instance;
        try { Logger.SetLevel(cfg.LogLevel); } catch { }

        int days = 30;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--days", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var d) && d >= 1)
                days = Math.Min(d, 3650);

        if (!cfg.AnalyzeCollectPass && !(cfg.AnalyzeTdmsEnabled && !string.IsNullOrWhiteSpace(cfg.TdmsRoot)))
            Console.WriteLine("analyze_collect_pass=false 且 TDMS 特征化未启用，测量值/TDMS 回填跳过（SN 补采仍执行）。");

        var sid = cfg.StationId;
        if (string.IsNullOrEmpty(sid))
        {
            try { sid = StationDetector.DetectStation() ?? ""; } catch { }
        }
        var dbFile = dbPathOverride ?? Path.Combine(AppConfig.BaseDir, "data", $"{(string.IsNullOrEmpty(sid) ? "fct" : sid)}.db");
        if (!File.Exists(dbFile))
        {
            Console.WriteLine($"本机库不存在，先运行主程序完成采集后再回填: {dbFile}");
            return 1;
        }
        var db = new Database(dbFile);
        var processor = new Processor(cfg, sid, Parsing.ParserRegistry.Instance, db);

        // SN 补采：历史 PASS 记录文件名无 SN 段导致 sn 为空，重读 XML DUT ID 补齐（独立于测量值开关，debug 文件不入库故天然排除）
        int snFixed = BackfillMissingSn(db, processor);
        if (!cfg.AnalyzeCollectPass && !(cfg.AnalyzeTdmsEnabled && !string.IsNullOrWhiteSpace(cfg.TdmsRoot)))
        {
            Console.WriteLine($"回填完成: SN 补采 {snFixed} 条");
            return 0;
        }

        Console.WriteLine($"测量值回填: {dbFile}");
        var report = RunMeasurementBackfill(db, cfg, processor, days, msg => Console.WriteLine($"  {msg}"));
        if (report.Skipped)
        {
            Console.WriteLine(report.SkipReason ?? "测量回填跳过");
            return string.IsNullOrEmpty(report.SkipReason) || report.SkipReason.StartsWith("results_root") ? 1 : 0;
        }
        Console.WriteLine($"回填完成: 扫描 {report.ScannedFiles} 文件，新记录 {report.InsertedRecords}，补测量值 {report.InsertedMeasurements} 行，补失败项 {report.InsertedFailItems} 行，SN 补采 {snFixed} 条，耗时 {report.Elapsed:hh\\:mm\\:ss}");
        return 0;
    }

    /// <summary>SN 补采：对库中 sn 为空的记录重读其 XML 提取 DUT ID 回填（文件已清理/解析失败/无 SN 的跳过，仅写仍为空的行，幂等）。
    /// 主程序启动时自动调用（幂等、无缺失秒回），CLI backfill 子命令亦可手动触发。</summary>
    public static int BackfillMissingSn(Database db, Processor processor, CancellationToken ct = default)
    {
        var missing = db.ListRecordsMissingSn();
        if (missing.Count == 0)
        {
            Console.WriteLine("SN 补采: 无缺失记录");
            Logger.Info("[SN补账] 无缺失记录，跳过");
            return 0;
        }
        Console.WriteLine($"SN 补采: 发现 {missing.Count} 条 SN 缺失记录，开始重读 XML 提取…");
        Logger.Info($"[SN补账] 发现 {missing.Count} 条 SN 缺失记录，开始重读 XML 提取…");
        int fixedCount = 0, skipped = 0, handled = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var batch = new List<(long Id, string Sn)>();
        foreach (var (id, xmlPath) in missing)
        {
            if (ct.IsCancellationRequested) break;
            handled++;
            try
            {
                if (!File.Exists(xmlPath)) { skipped++; continue; }
                var rec = processor.ParseAndClassify(xmlPath);
                if (rec == null || string.IsNullOrWhiteSpace(rec.Sn)) { skipped++; continue; }
                batch.Add((id, rec.Sn));
            }
            catch (Exception ex)
            {
                Logger.Warning($"[回填] SN 补采失败 {xmlPath}: {ex.Message}");
                skipped++;
            }
            // 每 500 条攒一批单事务提交（避免逐条独立事务每条一次 fsync），节流保持每 500 条一次
            if (batch.Count >= 500)
            {
                fixedCount += db.UpdateMissingSnBatch(batch);
                batch.Clear();
                Thread.Sleep(50);
            }
            if (handled % 2000 == 0)
            {
                Console.WriteLine($"  SN 补采进度: {handled}/{missing.Count}，已补 {fixedCount}");
                Logger.Info($"[SN补账] 进度: {handled}/{missing.Count}，已补 {fixedCount}");
            }
        }
        // 不足一批的收尾提交
        if (batch.Count > 0)
            fixedCount += db.UpdateMissingSnBatch(batch);
        Console.WriteLine($"SN 补采完成: 补 {fixedCount} 条，跳过 {skipped} 条，耗时 {sw.Elapsed:hh\\:mm\\:ss}");
        Logger.Info($"[SN补账] 完成: 补 {fixedCount} 条，跳过 {skipped} 条，耗时 {sw.Elapsed:hh\\:mm\\:ss}");
        return fixedCount;
    }
}

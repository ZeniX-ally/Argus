using System.Globalization;

namespace FctAggregator;

/// <summary>规格06：backfill 子命令——为存量 PASS 文件补采测量值（按天水位增量 + INSERT OR IGNORE 幂等）。</summary>
public static class BackfillTool
{
    public const string MetaWatermark = "backfill_last_day";

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
        {
            Console.WriteLine("analyze_collect_pass=false 且 TDMS 特征化未启用，无内容可回填。");
            return 0;
        }

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

        var today = DateTime.Now.Date;
        var from = today.AddDays(-(days - 1));
        var wm = db.GetMeta(MetaWatermark);
        if (DateTime.TryParseExact(wm, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var wmd) &&
            wmd >= from && wmd < today)
            from = wmd.AddDays(1);

        var root = cfg.ResultsRoot;
        if (!Directory.Exists(root))
        {
            Console.WriteLine($"results_root 不存在: {root}");
            return 1;
        }

        Console.WriteLine($"测量值回填: {dbFile}");
        Console.WriteLine($"窗口: {from:yyyy-MM-dd} ~ {today:yyyy-MM-dd}（--days {days}，已处理天数按水位跳过）");

        long insertedRecords = 0, insertedMeasurements = 0, insertedFailItems = 0, scanned = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var cat in new[] { "Online", "Offline" })
        {
            var catDir = Path.Combine(root, cat);
            if (!Directory.Exists(catDir)) continue;
            foreach (var modelDir in Directory.GetDirectories(catDir).OrderBy(p => p, StringComparer.Ordinal))
            {
                foreach (var dateDir in Directory.GetDirectories(modelDir).OrderBy(p => p, StringComparer.Ordinal))
                {
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
                        var chunk = files.Skip(i).Take(100).Select(Path.GetFullPath).ToList();
                        var existing = db.GetExistingPaths(chunk);
                        var ids = db.GetRecordIdsByPaths(chunk, "PASS");
                        var failIds = db.GetRecordIdsByPaths(chunk, "FAIL");
                        var newRecords = new List<TestRecord>();
                        var measureRows = new List<(TestRecord, long)>();
                        var failItemRows = new List<(TestRecord, long)>();
                        foreach (var p in chunk)
                        {
                            scanned++;
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
                        // 规格06期3：TDMS 特征补采档（PASS+FAIL 全量，UNIQUE 幂等；失败不影响测量值回填）
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
                        if (scanned % 200 == 0) Thread.Sleep(100);
                    }
                    insertedRecords += dayRecords;
                    insertedMeasurements += dayMeasurements;
                    insertedFailItems += dayFailItems;
                    Console.WriteLine($"  {day:yyyy-MM-dd} {Path.GetFileName(modelDir)}: 文件 {files.Count}，新记录 {dayRecords}，补测量 {dayMeasurements}，补失败项 {dayFailItems}{(dayTdmsRows > 0 ? $"，TDMS {dayTdmsRows} 行/{dayTdmsFiles} 文件" : "")}");
                    db.SetMeta(MetaWatermark, day.ToString("yyyy-MM-dd"));
                }
            }
        }

        Console.WriteLine($"回填完成: 扫描 {scanned} 文件，新记录 {insertedRecords}，补测量值 {insertedMeasurements} 行，补失败项 {insertedFailItems} 行，耗时 {sw.Elapsed:hh\\:mm\\:ss}");
        return 0;
    }
}

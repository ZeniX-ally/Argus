using System.Diagnostics;
using FctAggregator;
using Microsoft.Data.Sqlite;

/// <summary>CLI: dotnet run --project selftest -- --perf [输出.md]</summary>
static class PerfReport
{
    sealed record BenchRow(string Name, string Scenario, double Ms, string Note);

    public static int Run(string[] args)
    {
        var outPath = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : "";
        var rows = new List<BenchRow>();
        var work = Path.Combine(Path.GetTempPath(), "argus_perf_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        try
        {
            foreach (var (passes, items, tag) in new (int, int, string)[]
                     {
                         (50, 20, "小"),
                         (190, 80, "中"),
                         (400, 200, "大"),
                     })
            {
                var dbPath = Path.Combine(work, $"perf_{passes}_{items}.db");
                SeedChartDb(dbPath, passes, items);
                var db = new Database(dbPath);
                var cfg = new AppConfig { LearnNormalEnabled = true, LearnNormalMinSamples = 5, LearnNormalStaleDays = 30 };
                rows.Add(Time($"偏离折线 Build（{tag}）", tag, () =>
                {
                    var r = DeviationSeriesBuilder.Build(db, cfg, 0);
                    return $"{r.Series.Count} 线 / {r.PassLabels.Count} PASS";
                }));
                rows.Add(Time($"测项名 DISTINCT 查询（{tag}）", tag, () =>
                {
                    var range = db.GetPassMeasurementDateRange();
                    var from = range?.Min ?? "2026-01-01";
                    var to = range?.Max ?? "2026-12-31";
                    return $"{db.ListPassTestNamesInWindow(from, to).Count} 项";
                }));
                rows.Add(Time($"测量点拉取（{tag}）", tag, () =>
                {
                    var range = db.GetPassMeasurementDateRange();
                    var from = range?.Min ?? "2026-01-01";
                    var to = range?.Max ?? "2026-12-31";
                    return $"{db.ListPassMeasurementPoints(from, to, null, 500_000).Count} 行";
                }));
                rows.Add(Time($"自学习快照 Capture（{tag}）", tag, () =>
                {
                    var s = LearningSnapshot.Capture(db, cfg);
                    return $"ready={s.Ready}/{s.Total}";
                }));
                SqliteConnection.ClearAllPools();
            }

            // v3.36.0：看板/月度聚合 + 库清理基准（60 天混合库）
            var dashDbPath = Path.Combine(work, "perf_dash.db");
            SeedMonthlyDb(dashDbPath, days: 60, perDay: 40);
            var dashD = new Database(dashDbPath);
            string curYm = DateTime.Now.ToString("yyyyMM");
            string curYmd = DateTime.Now.ToString("yyyyMMdd");
            rows.Add(Time("月度良率 FetchMonthlyStats", "60 天库", () =>
            {
                var s = dashD.FetchMonthlyStats("", curYm);
                return $"{s.Pass}P/{s.Fail}F/{s.TodayProductCount}产品";
            }));
            rows.Add(Time("看板三连查（当日）", "60 天库", () =>
            {
                var h = dashD.FetchDailyHourlyStats("", curYmd);
                var t = dashD.FetchDailyTopFails("", curYmd, 5);
                var a = dashD.FetchRecentFailAlerts("", 10);
                return $"{h.Count}槽/{t.Count}项/{a.Count}告警";
            }));
            SqliteConnection.ClearAllPools();

            var purgeDb = Path.Combine(work, "perf_purge.db");
            SeedPurgeDb(purgeDb, oldDays: 40, recentDays: 5, perDay: 200);
            rows.Add(Time("库清理 Purge 模拟", "40天旧+5天新×200", () =>
            {
                var pdb = new Database(purgeDb);
                int del = pdb.PurgeOldMeasurements(5);
                return $"删除 {del} 行";
            }));
            SqliteConnection.ClearAllPools();

            var failDb = Path.Combine(work, "perf_fail.db");
            SeedFailDb(failDb, 2000);
            var failD = new Database(failDb);
            rows.Add(Time("FAIL 列表 AllFails", "2000 条", () =>
            {
                return $"{failD.AllFails("").Count} 条";
            }));
            SqliteConnection.ClearAllPools();

            var md = RenderMarkdown(rows, work);
            Console.WriteLine(md);
            if (!string.IsNullOrWhiteSpace(outPath))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outPath, md, System.Text.Encoding.UTF8);
                Console.WriteLine();
                Console.WriteLine($"[OK] 已写入 {outPath}");
            }
            return 0;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* best effort */ }
        }
    }

    static BenchRow Time(string name, string scenario, Func<string> work)
    {
        const int warm = 2, runs = 5;
        for (int i = 0; i < warm; i++) work();
        var sw = Stopwatch.StartNew();
        string note = "";
        for (int i = 0; i < runs; i++) note = work();
        sw.Stop();
        double ms = sw.Elapsed.TotalMilliseconds / runs;
        return new BenchRow(name, scenario, ms, note);
    }

    static void SeedChartDb(string path, int passCount, int itemCount)
    {
        if (File.Exists(path)) File.Delete(path);
        var db = new Database(path);
        var cfg = new AppConfig { LearnNormalEnabled = true, LearnNormalMinSamples = 5 };
        var baseDate = new DateTime(2026, 7, 22);
        for (int t = 0; t < itemCount; t++)
        {
            for (int s = 0; s < 8; s++)
                NormalModelStore.Observe(db, 5, "measurement", "G49", $"T_{t:D3}", 10.0 + t * 0.01, baseDate.AddDays(s).ToString("yyyy-MM-dd HH:mm:ss"));
        }
        var batch = new List<TestRecord>(passCount);
        for (int p = 0; p < passCount; p++)
        {
            var ts = baseDate.AddMinutes(p * 3).ToString("yyyy-MM-dd HH:mm:ss");
            var meas = new List<MeasurementRow>(itemCount);
            for (int t = 0; t < itemCount; t++)
                meas.Add(new MeasurementRow { TestName = $"T_{t:D3}", Value = 10.0 + t * 0.01 + (p % 7) * 0.02 });
            batch.Add(new TestRecord
            {
                StationId = "FCT1", Model = "G49", TestDate = ts[..10].Replace("-", ""),
                Sn = $"SN{p:D5}", Result = "PASS", BatchTimestamp = ts, Measurements = meas,
                XmlPath = $"X:\\perf_pass_{p:D5}.xml",
            });
        }
        db.BatchInsert(batch);
        SqliteConnection.ClearAllPools();
    }

    static void SeedFailDb(string path, int count)
    {
        if (File.Exists(path)) File.Delete(path);
        var db = new Database(path);
        var batch = new List<TestRecord>(count);
        for (int i = 0; i < count; i++)
        {
            batch.Add(new TestRecord
            {
                StationId = "FCT1", Model = "G49", Sn = $"F{i:D5}", Result = "FAIL",
                BatchTimestamp = $"2026-08-{(i % 28 + 1):D2} 12:00:00",
                FailReason = "STEP_A timeout", XmlPath = $"X:\\fail_{i}.xml",
            });
        }
        db.BatchInsert(batch);
        SqliteConnection.ClearAllPools();
    }

    /// <summary>看板/月度基准库：days 天 × perDay 条混合 PASS/FAIL 记录，日期相对今天。</summary>
    static void SeedMonthlyDb(string path, int days, int perDay)
    {
        if (File.Exists(path)) File.Delete(path);
        var db = new Database(path);
        var batch = new List<TestRecord>(days * perDay);
        for (int d = 0; d < days; d++)
        {
            var day = DateTime.Now.Date.AddDays(-d);
            for (int i = 0; i < perDay; i++)
            {
                bool pass = (i % 4) != 0;
                batch.Add(new TestRecord
                {
                    StationId = "FCT1", Model = "G49",
                    TestDate = day.ToString("yyyyMMdd"),
                    Sn = $"SN{d:D3}_{i:D3}",
                    Result = pass ? "PASS" : "FAIL",
                    FailReason = pass ? "" : "STEP_A timeout",
                    BatchTimestamp = day.AddMinutes(i * 3).ToString("yyyy-MM-dd HH:mm:ss"),
                    XmlPath = $"X:\\m_{d}_{i}.xml",
                });
            }
        }
        db.BatchInsert(batch);
        SqliteConnection.ClearAllPools();
    }

    /// <summary>库清理基准库：oldDays+recentDays 天的 PASS 记录，各带 perDay 条测量值。</summary>
    static void SeedPurgeDb(string path, int oldDays, int recentDays, int perDay)
    {
        if (File.Exists(path)) File.Delete(path);
        var db = new Database(path);
        var batch = new List<TestRecord>(oldDays + recentDays);
        for (int d = 0; d < oldDays + recentDays; d++)
        {
            var day = DateTime.Now.Date.AddDays(-d);
            var rec = new TestRecord
            {
                StationId = "FCT1", Model = "G49",
                TestDate = day.ToString("yyyyMMdd"),
                Result = "PASS",
                XmlPath = $"X:\\p_{d}.xml",
                BatchTimestamp = day.ToString("yyyy-MM-dd") + " 10:00:00",
                Measurements = new List<MeasurementRow>(perDay),
            };
            for (int i = 0; i < perDay; i++)
                rec.Measurements.Add(new MeasurementRow { TestName = $"T{i % 5}", Value = 10.0 + i * 0.01 });
            batch.Add(rec);
        }
        db.BatchInsert(batch);
        SqliteConnection.ClearAllPools();
    }

    static string RenderMarkdown(List<BenchRow> rows, string workDir)
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Argus v3.35.4 性能报告");
        sb.AppendLine();
        sb.AppendLine($"生成时间：{now}");
        sb.AppendLine($"环境：{Environment.OSVersion.VersionString} · {Environment.ProcessorCount} 逻辑核 · .NET {Environment.Version}");
        sb.AppendLine($"基准方式：每项预热 2 次后取 5 次均值（毫秒）");
        sb.AppendLine();
        sb.AppendLine("## 1. 版本性能相关变更摘要");
        sb.AppendLine();
        sb.AppendLine("| 版本 | 变更 | 预期效果 |");
        sb.AppendLine("|------|------|----------|");
        sb.AppendLine("| v3.35.3 | 分析页去掉切页同步 `AnalysisEngine.RunOnce`；表格/折线/自学习后台 `Task.Run`；2.5s 刷新节流 | 切到「分析」页 UI 线程不再阻塞数秒 |");
        sb.AppendLine("| v3.35.3 | FAIL 页 `AllFails` 异步加载 + 搜索 250ms 防抖 | 大 FAIL 库切页不卡死 |");
        sb.AppendLine("| v3.35.3 | 自学习嵌入模式取消双定时器；DataGrid 取消 AllCells 自动列宽 | 分析页滚动/重绘减轻 |");
        sb.AppendLine("| v3.35.3 | 总览切页不再跑完整 `Tick()` 全量刷新 | 页签切换更轻 |");
        sb.AppendLine("| v3.35.4 | 偏离折线由最多 6 条改为窗口内**全部测项** | 后台构建耗时随测项数上升；UI 仍异步 |");
        sb.AppendLine("| v3.36.0 | 表达式/复合索引 + PRAGMA（NORMAL/cache_size/temp_store），零 SQL 改动 | 窗口/看板查询走索引，消除整表扫描 |");
        sb.AppendLine("| v3.36.0 | 偏离折线 N+1 批量模型缓存 + 主页看板 5s 节流 | 折线 Build 中 ~44ms、大 ~188ms（较 v3.35 降 8 倍+） |");
        sb.AppendLine();
        sb.AppendLine("## 2. 微基准（合成数据，本机实测）");
        sb.AppendLine();
        sb.AppendLine("| 操作 | 规模 | 均值 (ms) | 结果 |");
        sb.AppendLine("|------|------|-----------|------|");
        foreach (var r in rows)
            sb.AppendLine($"| {r.Name} | {r.Scenario} | {r.Ms:F1} | {r.Note} |");
        sb.AppendLine();
        sb.AppendLine("## 3. 解读与建议");
        sb.AppendLine();
        var chartMed = rows.FirstOrDefault(r => r.Name.Contains("偏离折线") && r.Scenario == "中");
        var chartLarge = rows.FirstOrDefault(r => r.Name.Contains("偏离折线") && r.Scenario == "大");
        if (chartMed != null)
            sb.AppendLine($"- **典型产线规模（中）**：约 **{chartMed.Ms:F0} ms** / {chartMed.Note}——在后台线程完成，切页不卡；首次进分析页会有短暂「空图→出图」。");
        if (chartLarge != null)
            sb.AppendLine($"- **上限压力（大）**：约 **{chartLarge.Ms:F0} ms** / {chartLarge.Note}——建议用「近 30/90 天」窗口而非「自动」全量，或等 PASS 数 <300 时再开全窗口。");
        sb.AppendLine("- **UI 线程原则**：v3.35.3 起分析/FAIL/自学习的数据库读取均在 `Task.Run` + `BeginInvoke` 回写，主线程仅做绘制。");
        sb.AppendLine("- **折线图绘制**：测项 >60 时自动隐藏散点、降低线宽与透明度，减轻 GDI+ 重绘成本（绘制仍在 UI 线程，测项极多时缩放窗口可体感更顺）。");
        sb.AppendLine("- **刷新节流**：分析页 `Refresh2` 默认 2.5s 内不重复拉数；用户点「刷新」可 `force` 跳过。");
        sb.AppendLine("- **若仍慢**：优先查杀毒实时扫描 `fct.db`、磁盘是否为机械盘；测量缓存行数见分析页自学习区状态行。");
        sb.AppendLine();
        sb.AppendLine("## 4. UI 响应性（设计目标，非墙钟实测）");
        sb.AppendLine();
        sb.AppendLine("| 场景 | v3.35.2 及以前 | v3.35.3+ |");
        sb.AppendLine("|------|----------------|----------|");
        sb.AppendLine("| 切换到「分析」页 | UI 线程同步跑引擎 + 读库，易冻结 1–5s | 仅触发异步刷新，切页 <50ms 量级 |");
        sb.AppendLine("| FAIL 页 2000+ 条 | 同步 `AllFails` 阻塞 | 后台加载，首屏先显示旧数据 |");
        sb.AppendLine("| 搜索框连续输入 | 每次键入全量过滤 | 250–300ms 防抖 |");
        sb.AppendLine();
        sb.AppendLine($"_临时库目录：{workDir}_");
        return sb.ToString();
    }
}

using FctAggregator;

/// <summary>
/// xlsx 目视核对工具（配套 v2.9.3「xlsx 写出器 4 份 → 1 份」的验收）。
///
/// 拿同一份 fixture，把各个导出口各导一次真实 xlsx。
/// 本工程刻意**只调用高层导出入口**（不碰 Cell/Sheet/Xlsx），
/// 这样同一份源码在 v2.9.2（去重前）的代码上也能编译，可做新旧逐字节对比。
///
/// 用法：
///   XlsxDump &lt;输出目录&gt; &lt;fixture目录&gt; [维修记录db]
///     fixture 目录须含 Results\ 与 "TDMS Log"\（由 tools\xlsx_check.ps1 准备）
///     db 省略或不存在时跳过维修记录那张表
///
/// ⚠ v3.0.0：数据分发已拆成独立程序，它的「金银板 SN 台账」不再在这里核对，
/// 改由 fct-distributor 自己的自检负责。本工具现在盯 3 个导出口。
/// </summary>
public static class XlsxDump
{
    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: XlsxDump <输出目录> <fixture目录> [维修记录db]");
            return 2;
        }
        string outDir = Path.GetFullPath(args[0]);
        string fx = Path.GetFullPath(args[1]);
        string db = args.Length > 2 ? Path.GetFullPath(args[2]) : "";
        Directory.CreateDirectory(outDir);

        string results = Path.Combine(fx, "Results");
        string tdms = Path.Combine(fx, "TDMS Log");
        var start = new DateTime(2026, 07, 01);
        var end = new DateTime(2026, 07, 31);

        int ok = 0, fail = 0;
        void Step(string title, Action body)
        {
            Console.Write($"  {title} ... ");
            try { body(); Console.WriteLine("OK"); ok++; }
            catch (Exception ex) { Console.WriteLine("失败: " + ex.Message); fail++; }
        }

        Console.WriteLine($"[XlsxDump] 输出 -> {outDir}");
        Console.WriteLine($"[XlsxDump] fixture -> {fx}");

        // ---------- 1) FAIL 排行报表 ----------
        Step("1 FAIL排行报表", () =>
        {
            var recs = FctFailRanker.XmlScanner.Scan(results, start, end);
            var (summary, ranks) = FctFailRanker.CsvExporter.Aggregate(recs);
            var p = Path.Combine(outDir, "1-FAIL排行报表.xlsx");
            FctFailRanker.XlsxExporter.Export(p, start, end, recs, summary, ranks);
            Console.Write($"{recs.Count} 条记录 / {ranks.Count} 个不良项 -> ");
        });

        // ---------- 2) 取数打包：捞取清单 ----------
        Step("2 取数清单", () =>
        {
            var cfg = new FctFetcher.Config
            {
                ResultsRoot = results,
                TdmsRoot = tdms,
                OutputDir = outDir,
                PackFiles = false,
                Categories = new[] { "Offline" },
            };
            var recs = FctFetcher.Scanner.Scan(cfg, start, end, out var stats);
            var got = FctFetcher.Exporter.Export(recs, cfg, start, end, outDir);
            var p = Path.Combine(outDir, "2-取数-捞取清单.xlsx");
            if (File.Exists(p)) File.Delete(p);
            File.Move(got, p);
            Console.Write($"{recs.Count} 条 -> ");
        });

        // ---------- 3) 维修记录 ----------
        if (db.Length > 0 && File.Exists(db))
        {
            Step("3 维修记录", () =>
            {
                var d = new Database(db);
                SeedMaintenance(d, db);
                var recs = d.ListMaintenance("", 500);
                var p = Path.Combine(outDir, "3-维修记录.xlsx");
                MaintenanceExporter.ExportXlsx(p, recs);
                Console.Write($"{recs.Count} 条 -> ");
            });
        }
        else
        {
            Console.WriteLine("  3 维修记录 ... 跳过（没给到 db 或文件不存在）");
        }

        Console.WriteLine($"[XlsxDump] 完成：成功 {ok}，失败 {fail}");
        return fail == 0 ? 0 : 1;
    }
}

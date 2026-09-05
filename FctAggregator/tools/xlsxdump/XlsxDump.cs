using FctAggregator;

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

        Step("1 FAIL排行报表", () =>
        {
            var recs = FctFailRanker.XmlScanner.Scan(results, start, end);
            var (summary, ranks) = FctFailRanker.CsvExporter.Aggregate(recs);
            var p = Path.Combine(outDir, "1-FAIL排行报表.xlsx");
            FctFailRanker.XlsxExporter.Export(p, start, end, recs, summary, ranks);
            Console.Write($"{recs.Count} 条记录 / {ranks.Count} 个不良项 -> ");
        });

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

        if (db.Length > 0 && File.Exists(db))
        {
            Step("3 维修记录", () =>
            {
                var d = new Database(db);
                SeedMaintenance(d);
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

    static void SeedMaintenance(Database d)
    {
        if (d.ListMaintenance("", 1).Count > 0) return;
        var rows = new[]
        {
            new MaintenanceRecord { StationId = "FCT1", EquipmentModel = "E3002624", EquipmentSn = "SN0001",
                FailItem = "Voltage Test", FailReason = "读数超上限", Severity = "critical", Status = "open",
                Notes = "目视核对种子数据", CreatedAt = "2026-07-01 08:00:00" },
            new MaintenanceRecord { StationId = "FCT2", EquipmentModel = "E3002757", EquipmentSn = "SN0002",
                FailItem = "Current Test", FailReason = "接触不良", Severity = "major", Status = "in_progress",
                Resolver = "张三", Notes = "目视核对种子数据", CreatedAt = "2026-07-02 09:30:00" },
            new MaintenanceRecord { StationId = "FCT3", EquipmentModel = "E3002781", EquipmentSn = "SN0003",
                FailItem = "USB Detect", FailReason = "识别不到设备", Severity = "minor", Status = "resolved",
                Resolver = "李四", Resolution = "更换 USB 线", Notes = "目视核对种子数据", CreatedAt = "2026-07-03 14:20:00" },
        };
        foreach (var m in rows) d.CreateMaintenance(m);
    }
}

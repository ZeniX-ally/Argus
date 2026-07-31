using FctAggregator;

/// <summary>
/// xlsx 目视核对工具（配套 v2.9.3「xlsx 写出器 4 份 → 1 份」的验收）。
///
/// 拿同一份 fixture，把四个导出口各导一次真实 xlsx。
/// 本工程刻意**只调用高层导出入口**（不碰 Cell/Sheet/Xlsx），
/// 这样同一份源码在 v2.9.2（去重前）的代码上也能编译，可做新旧逐字节对比。
///
/// 用法：
///   XlsxDump &lt;输出目录&gt; &lt;fixture目录&gt; [维修记录db]
///     fixture 目录须含 Results\ 与 "TDMS Log"\（由 tools\xlsx_check.ps1 准备）
///     db 省略或不存在时跳过第 4 张表
/// </summary>
public static class XlsxDump
{
    // 台账 fixture：写死，保证新旧两次跑数据完全一致。SN 用真实金/银板。
    private static List<FctDistributor.SnExporter.Board> Boards()
    {
        string gold = FctDistributor.SnExporter.SnGolden;
        string silver = FctDistributor.SnExporter.SnSilvery;
        string other = "E3002781AFV75236898002K30500999";   // 非金银板，应被排除
        var list = new List<FctDistributor.SnExporter.Board>();

        void Add(string station, string date, string sn, string prefix, int hh, string status)
            => list.Add(new FctDistributor.SnExporter.Board(
                station, date, sn[..8], sn, prefix,
                $"{prefix}_Fts_PEU_G49_{station}_{sn}_{date}{hh:00}0000000_{date}000000.xml",
                DateTime.ParseExact(date, "yyyyMMdd", null).AddHours(hh),
                status));

        Add("FCT1", "20260722", gold, "P", 8, "已生成");
        Add("FCT1", "20260722", silver, "O", 9, "已生成");
        Add("FCT2", "20260722", gold, "P", 10, "已存在");
        Add("FCT2", "20260722", other, "F", 11, "已生成");     // 被排除
        Add("FCT3", "20260723", gold, "P", 8, "待生成");
        Add("FCT3", "20260723", silver, "P", 8, "待生成");
        Add("FCT4", "20260724", silver, "O", 13, "已存在");
        return list;
    }

    /// <summary>
    /// 现场真实库里 maintenance_records 是 0 条（没人录过），空表看不出数据行长相。
    /// 所以在**副本**上补一批样例：故障项名取自真实 test_records 的 fail_reason，
    /// 覆盖三档严重度、四种状态，并特意混入 &amp; &lt; &gt; 与换行，一并检验转义。
    /// 幂等：已有记录就不动；时间戳事后固定，保证新旧两次跑逐字节可比。
    /// </summary>
    private static void SeedMaintenance(Database d, string dbPath)
    {
        if (d.ListMaintenance("", 5).Count > 0) return;

        var seeds = new (string Item, string Model, string Sn, string Reason, string Sev,
                         string Status, string Resolver, string Resolution, string Notes)[]
        {
            ("6.1.2.2.16 KL30_FILT_2(DMM)", "E3002781", "E30027811V3493WXA74KGZLMA5VPEXB",
             "KL30 滤波后电压偏低，实测 11.2V < 下限 11.5V", "critical", "open",
             "", "", "连续 14 次，疑似 DMM 通道接触不良"),
            ("6.4.1.2.3 Vref_3V3(DMM)", "E3002781", "E30027816Q9F2LMXJ1UQAC66U4VFEL6",
             "3V3 基准电压超差（3.41V > 3.40V）", "major", "in_progress",
             "张工", "已换测试针，等复测 20 片确认", "探针氧化"),
            ("6.2.1.3.5 Iq_Offset(DMM)", "E3002781", "E30027815EEJFYAA2JMFB1T1S2TDT8B",
             "Iq 偏置漂移，量程 <10mA 时读数不稳", "major", "investigating",
             "李工", "", "夹具地线阻抗待测"),
            ("6.5.3.1.1 CAN_Comm(XCP)", "E3002781", "E30027812H3ZRU7EERUVL1R36Q6RF5R",
             "CAN 通讯建立失败（超时 500ms）", "critical", "resolved",
             "王工", "更换 CAN 收发线束 & 重烧 A2L", "根因：线束 D 型头针脚退针"),
            ("6.3.2.1.8 Temp_Sensor(XCP)", "E3002781", "E300278175LXWF1U2UAMA754LB58CSC",
             "温度传感器读数 < -40℃（断路）", "minor", "resolved",
             "张工", "重新压接端子", "备注含换行：\n第二行文字，用来看行高与自动换行"),
            ("6.1.2.4.2 BSW_v_Kl30UC (KL30_LS_UC)(XCP)", "E3002781", "E300278164DQKA2XJ1UQAC66U4VFEL6",
             "KL30 低边采样 UC 值异常", "minor", "open",
             "", "", "特殊字符检验：a<b & c>d \"quoted\" 'single'"),
        };

        foreach (var s in seeds)
            d.CreateMaintenance(new MaintenanceRecord
            {
                StationId = "FCT2",
                FailItem = s.Item,
                EquipmentModel = s.Model,
                EquipmentSn = s.Sn,
                FailReason = s.Reason,
                Severity = s.Sev,
                Status = s.Status,
                Resolver = s.Resolver,
                Resolution = s.Resolution,
                Notes = s.Notes,
            });

        // 时间戳固定，否则新旧两次跑的「创建/更新时间」不同，会淹没真正的样式差异
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE maintenance_records SET created_at='2026-07-22 09:30:00', " +
                          "updated_at='2026-07-28 16:45:00'";
        cmd.ExecuteNonQuery();
    }

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

        // ---------- 1) 数据分发：金银板 SN 台账 ----------
        Step("1 分发SN台账", () =>
        {
            var p = Path.Combine(outDir, "1-分发-金银板SN台账.xlsx");
            var r = FctDistributor.SnExporter.Export(p, Boards());
            Console.Write($"{r.Rows} 行 / 金 {r.Golden} 银 {r.Silvery} 排除 {r.Excluded} -> ");
        });

        // ---------- 2) FAIL 排行报表 ----------
        Step("2 FAIL排行报表", () =>
        {
            var recs = FctFailRanker.XmlScanner.Scan(results, start, end);
            var (summary, ranks) = FctFailRanker.CsvExporter.Aggregate(recs);
            var p = Path.Combine(outDir, "2-FAIL排行报表.xlsx");
            FctFailRanker.XlsxExporter.Export(p, start, end, recs, summary, ranks);
            Console.Write($"{recs.Count} 条记录 / {ranks.Count} 个不良项 -> ");
        });

        // ---------- 3) 取数打包：捞取清单 ----------
        Step("3 取数清单", () =>
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
            var p = Path.Combine(outDir, "3-取数-捞取清单.xlsx");
            if (File.Exists(p)) File.Delete(p);
            File.Move(got, p);
            Console.Write($"{recs.Count} 条 -> ");
        });

        // ---------- 4) 维修记录 ----------
        if (db.Length > 0 && File.Exists(db))
        {
            Step("4 维修记录", () =>
            {
                var d = new Database(db);
                SeedMaintenance(d, db);
                var recs = d.ListMaintenance("", 500);
                var p = Path.Combine(outDir, "4-维修记录.xlsx");
                MaintenanceExporter.ExportXlsx(p, recs);
                Console.Write($"{recs.Count} 条 -> ");
            });
        }
        else
        {
            Console.WriteLine("  4 维修记录 ... 跳过（没给到 db 或文件不存在）");
        }

        Console.WriteLine($"[XlsxDump] 完成：成功 {ok}，失败 {fail}");
        return fail == 0 ? 0 : 1;
    }
}

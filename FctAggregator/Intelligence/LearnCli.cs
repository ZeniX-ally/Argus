namespace FctAggregator;

/// <summary>Argus.exe learn --backfill|--replay</summary>
public static class LearnCli
{
    public static int Run(string[] args, string? dbPathOverride = null)
    {
        var cfg = AppConfig.Instance;
        try { Logger.SetLevel(cfg.LogLevel); } catch { }

        bool replay = args.Any(a => a.Equals("--replay", StringComparison.OrdinalIgnoreCase));
        bool synthetic = args.Any(a => a.Equals("--synthetic", StringComparison.OrdinalIgnoreCase));
        int days = 28;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--days", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var d) && d >= 1)
                days = Math.Clamp(d, 7, 3650);
        }
        var anomalyDates = ParseAnomalyDates(args);

        if (replay)
        {
            if (synthetic)
            {
                Console.WriteLine("合成回放（selftest 口径）。");
                var path = dbPathOverride ?? Path.Combine(Path.GetTempPath(), "argus-learn-replay.db");
                try { File.Delete(path); } catch { }
                var rp = ReplayRunner.RunSynthetic(path);
                PrintReplayReport(rp);
                return rp.Pass ? 0 : 2;
            }

            var sid = cfg.StationId;
            if (string.IsNullOrEmpty(sid))
            {
                try { sid = StationDetector.DetectStation() ?? ""; } catch { }
            }
            var dbFile = dbPathOverride ?? Path.Combine(AppConfig.BaseDir, "data", $"{(string.IsNullOrEmpty(sid) ? "fct" : sid)}.db");
            if (!File.Exists(dbFile))
            {
                Console.WriteLine($"本机库不存在: {dbFile}（可用 --synthetic 跑合成回放）");
                return 1;
            }
            if (!cfg.LearnNormalEnabled)
                Console.WriteLine("注意: learn_normal_enabled=false，回放仍按当前配置评分。");
            Console.WriteLine($"现场回放: {dbFile}  --days {days}" + (anomalyDates.Count > 0 ? $"  异常日 {string.Join(",", anomalyDates)}" : "  异常日=评分周 FAIL 峰推断"));
            var field = ReplayRunner.RunField(dbFile, cfg, days, anomalyDates.Count > 0 ? anomalyDates : null);
            PrintReplayReport(field);
            return field.Pass ? 0 : 2;
        }

        if (!cfg.LearnNormalEnabled)
        {
            Console.WriteLine("learn_normal_enabled=false，回填跳过。");
            return 0;
        }

        var sid2 = cfg.StationId;
        if (string.IsNullOrEmpty(sid2))
        {
            try { sid2 = StationDetector.DetectStation() ?? ""; } catch { }
        }
        var dbFile2 = dbPathOverride ?? Path.Combine(AppConfig.BaseDir, "data", $"{(string.IsNullOrEmpty(sid2) ? "fct" : sid2)}.db");
        if (!File.Exists(dbFile2))
        {
            Console.WriteLine($"本机库不存在: {dbFile2}");
            return 1;
        }
        var db = new Database(dbFile2);
        Console.WriteLine($"正常态回填: {dbFile2}  --days {days}");
        var n = LearnBackfill.Run(db, cfg, days);
        Console.WriteLine($"回填完成: 处理窗口 {n}");
        return 0;
    }

    private static void PrintReplayReport(ReplayReport rp)
    {
        Console.WriteLine($"检出率 {rp.DetectionRate:P0}  误报 {rp.FalsePosPerDay:F2}/天  覆盖 {rp.ReadyCoverage:P0}  事件 {rp.EventsTotal}  评分 {rp.ScoreDays} 天  异常日 {rp.AnomalyDays}");
        Console.WriteLine(rp.Pass ? "三指标达标" : "三指标未达标（调 min_samples/vote/event_score 后重考）");
    }

    private static List<string> ParseAnomalyDates(string[] args)
    {
        var list = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals("--anomaly-dates", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= args.Length) break;
            foreach (var part in args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Length >= 8) list.Add(part.Length >= 10 ? part[..10] : part);
            }
            break;
        }
        return list;
    }
}

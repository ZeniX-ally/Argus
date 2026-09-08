namespace FctAggregator;

/// <summary>Argus.exe learn --backfill|--replay</summary>
public static class LearnCli
{
    public static int Run(string[] args, string? dbPathOverride = null)
    {
        var cfg = AppConfig.Instance;
        try { Logger.SetLevel(cfg.LogLevel); } catch { }

        bool replay = args.Any(a => a.Equals("--replay", StringComparison.OrdinalIgnoreCase));
        int days = 30;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--days", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var d) && d >= 1)
                days = Math.Min(d, 3650);

        if (replay)
        {
            Console.WriteLine("合成回放（selftest 口径）。现场库回放请先 --backfill 再在自学习页查看事件。");
            var path = dbPathOverride ?? Path.Combine(Path.GetTempPath(), "argus-learn-replay.db");
            try { File.Delete(path); } catch { }
            var rp = ReplayRunner.RunSynthetic(path);
            Console.WriteLine($"检出率 {rp.DetectionRate:P0}  误报 {rp.FalsePosPerDay:F2}/天  覆盖 {rp.ReadyCoverage:P0}  事件 {rp.EventsTotal}");
            Console.WriteLine(rp.Pass ? "三指标达标" : "三指标未达标（调 min_samples/vote/event_score 后重考）");
            return rp.Pass ? 0 : 2;
        }

        if (!cfg.LearnNormalEnabled)
        {
            Console.WriteLine("learn_normal_enabled=false，回填跳过。");
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
            Console.WriteLine($"本机库不存在: {dbFile}");
            return 1;
        }
        var db = new Database(dbFile);
        Console.WriteLine($"正常态回填: {dbFile}  --days {days}");
        var n = LearnBackfill.Run(db, cfg, days);
        Console.WriteLine($"回填完成: 处理窗口 {n}");
        return 0;
    }
}

namespace FctAggregator;

public static class DeviceHealthScorer
{
    public sealed class HealthReport
    {
        public List<MachineHealth> Machines = new();
        public HealthSummary Summary = new();
        public DateTime GeneratedAt = DateTime.Now;
    }

    public sealed class MachineHealth
    {
        public string Machine = "";
        public double Health;
        public string Level = "ok";
        public List<ComponentScore> Components = new();
        public string? TopConcern;
        public string? Recommendation;
    }

    public sealed class ComponentScore
    {
        public string Name = "";
        public double Score;
        public double Weight;
        public string Raw = "";
        public string Trend = "";
    }

    public sealed class HealthSummary
    {
        public int Ok;
        public int Warn;
        public int Critical;
    }

    private static string RecommendFor(string component) => component switch
    {
        "cpu" => "检查高 CPU 进程/重启服务",
        "disk" => "清理 data/archive 或扩容",
        "offline" => "检查网络/服务存活",
        "memory" => "检查内存占用/重启服务",
        _ => "检查机台状态",
    };

    public static HealthReport Score(AggDatabase db, AppConfig cfg)
    {
        var report = new HealthReport();
        List<DeviceInfoRow> infos;
        try { infos = db.ListDeviceInfos(); }
        catch { return report; }

        var from = DateTime.Now.AddDays(-7).ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var info in infos)
        {
            try
            {
                var samples = db.QueryDeviceSamples(info.Machine, 2000, from, null);
                var cpuTrend = samples.Count >= 3 ? TrendPerDay(samples, "cpu") : double.NaN;
                var diskTrend = samples.Count >= 3 ? TrendPerDay(samples, "disk") : double.NaN;
                int? daysToExhaust = DaysToExhaust(info.DiskFreeGb, diskTrend);
                double gapSec = DateTime.TryParse(info.LastSeen, out var lastDt)
                    ? Math.Max(0, (DateTime.Now - lastDt).TotalSeconds) : 0;
                double hbStd = HeartbeatStd(samples);

                var comps = new List<ComponentScore>
                {
                    new() { Name="cpu", Weight=cfg.HealthWeightCpu,
                            Score=CpuScore(info.CpuUsage, double.IsNaN(cpuTrend)?0:cpuTrend, cfg),
                            Raw=$"{info.CpuUsage:0.0}%", Trend=double.IsNaN(cpuTrend)?"stable":$"{cpuTrend:+0.0;-0.0}%/天" },
                    new() { Name="disk", Weight=cfg.HealthWeightDisk,
                            Score=DiskScore(info.DiskFreeGb, daysToExhaust, cfg),
                            Raw=info.DiskFreeGb>0 ? $"{info.DiskFreeGb:0.0}GB" : "未知",
                            Trend=daysToExhaust.HasValue ? $"约 {daysToExhaust} 天耗尽" : "stable" },
                    new() { Name="memory", Weight=cfg.HealthWeightMemory,
                            Score=MemoryScore(info.MemUsedMb, info.MemTotalMb),
                            Raw=info.MemTotalMb>0 ? $"{(double)info.MemUsedMb/info.MemTotalMb*100:0}%" : "未知", Trend="stable" },
                    new() { Name="offline", Weight=cfg.HealthWeightOffline,
                            Score=OfflineScore(gapSec, hbStd, cfg),
                            Raw=$"心跳延迟 {gapSec:0}s", Trend=hbStd>0 ? $"std={hbStd:0}s" : "stable" },
                };

                double health = comps.Sum(c => c.Score * c.Weight);
                health = Math.Clamp(health, 0, 100);
                var top = comps.OrderBy(c => c.Score * c.Weight).First();
                var level = health < cfg.HealthCriticalThreshold ? "critical"
                          : health < cfg.HealthWarnThreshold ? "warn" : "ok";

                report.Machines.Add(new MachineHealth
                {
                    Machine = info.Machine,
                    Health = Math.Round(health, 1),
                    Level = level,
                    Components = comps,
                    TopConcern = top.Name,
                    Recommendation = level == "ok" ? null : RecommendFor(top.Name),
                });
            }
            catch {  }
        }
        foreach (var m in report.Machines)
        {
            if (m.Level == "critical") report.Summary.Critical++;
            else if (m.Level == "warn") report.Summary.Warn++;
            else report.Summary.Ok++;
        }
        return report;
    }

    private static int? DaysToExhaust(double freeGb, double diskTrendPerDay)
    {
        if (freeGb <= 0 || double.IsNaN(diskTrendPerDay) || diskTrendPerDay >= -0.05) return null;
        return (int)Math.Ceiling(freeGb / -diskTrendPerDay);
    }

    private static double HeartbeatStd(List<DeviceSampleRow> samples)
    {
        if (samples.Count < 5) return 0;
        var intervals = new List<double>();
        for (int i = 1; i < samples.Count; i++)
        {
            var a = DateTime.TryParse(samples[i - 1].Ts, out var ta) ? ta : DateTime.Now;
            var b = DateTime.TryParse(samples[i].Ts, out var tb) ? tb : DateTime.Now;
            intervals.Add((b - a).TotalSeconds);
        }
        if (intervals.Count == 0) return 0;
        var avg = intervals.Average();
        return Math.Sqrt(intervals.Average(v => (v - avg) * (v - avg)));
    }

    private static double TrendPerDay(List<DeviceSampleRow> samples, string metric)
        => DevicePredictor.TrendPerDay(samples, metric);

    public static double CpuScore(double current, double trendPerDay, AppConfig cfg)
    {
        var score = 100 - Math.Clamp(current, 0, 100);
        if (trendPerDay > 0.5)
        {
            var predicted3d = current + trendPerDay * 3;
            if (predicted3d > 95) score -= 30;
            else if (predicted3d > 80) score -= 15;
        }
        return Math.Clamp(score, 0, 100);
    }

    public static double DiskScore(double currentFreeGb, int? daysToExhaust, AppConfig cfg)
    {
        double score;
        var th = cfg.DeviceAlertDiskFreeGb > 0 ? cfg.DeviceAlertDiskFreeGb : 10;
        if (currentFreeGb <= 0) score = 0;
        else if (currentFreeGb >= th * 2) score = 100;
        else if (currentFreeGb >= th) score = 70;
        else if (currentFreeGb >= th * 0.5) score = 40;
        else score = 10;
        if (daysToExhaust.HasValue)
        {
            if (daysToExhaust <= 3) score -= 30;
            else if (daysToExhaust <= 7) score -= 15;
        }
        return Math.Clamp(score, 0, 100);
    }

    public static double MemoryScore(int usedMb, int totalMb)
    {
        if (totalMb <= 0) return 100;
        var pct = (double)usedMb / totalMb * 100;
        if (pct < 70) return 100;
        if (pct < 85) return 80;
        if (pct < 95) return 50;
        return 20;
    }

    public static double OfflineScore(double gapSec, double heartbeatStd, AppConfig cfg)
    {
        double score;
        if (gapSec > 300) score = 0;
        else if (gapSec > 90) score = 40;
        else if (gapSec > 60) score = 70;
        else if (gapSec > 30) score = 90;
        else score = 100;
        if (heartbeatStd > 60) score -= 20;
        return Math.Clamp(score, 0, 100);
    }
}

namespace FctAggregator;

/// <summary>
/// 规格 04 设备健康综合分（v3.22.0）：每台机台算一个 0..100 健康分
/// = cpu 0.30 + disk 0.30 + memory 0.15 + offline 0.25（权重可配）。
/// 替代 HighlightEngine 的单指标阈值平铺告警：单指标各自超阈时只出一条综合告警 + 扣分排名。
/// 纯加权打分零 ML 零 NuGet；每个子项 raw + score 全可解释；阈值/权重 config 可调，
/// health_score_enabled=false 时 HighlightEngine 秒级回退 v3.19.0 单指标逻辑。
/// </summary>
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
        public double Health;            // 0..100
        public string Level = "ok";      // ok/warn/critical
        public List<ComponentScore> Components = new();
        public string? TopConcern;       // 最拖后腿的子项名（score×weight 最小）
        public string? Recommendation;
    }

    public sealed class ComponentScore
    {
        public string Name = "";         // cpu/disk/memory/offline
        public double Score;             // 0..100
        public double Weight;            // 0..1
        public string Raw = "";          // "92%"
        public string Trend = "";        // "+1.2%/天" / "stable"
    }

    public sealed class HealthSummary
    {
        public int Ok;
        public int Warn;
        public int Critical;
    }

    /// <summary>把 machine 名映射到 AlertHealer 口径的建议规则（cpu/disk/offline/memory）。</summary>
    private static string RecommendFor(string component) => component switch
    {
        "cpu" => "检查高 CPU 进程/重启服务",
        "disk" => "清理 data/archive 或扩容",
        "offline" => "检查网络/服务存活",
        "memory" => "检查内存占用/重启服务",
        _ => "检查机台状态",
    };

    /// <summary>计算所有机台健康分（30 台 × 4 指标 &lt; 50ms；查询异常的机台跳过不拖垮整体）。</summary>
    public static HealthReport Score(AggDatabase db, AppConfig cfg)
    {
        var report = new HealthReport();
        List<DeviceInfoRow> infos;
        try { infos = db.ListDeviceInfos(); }
        catch { return report; }    // 聚合端无 device_info → 空 machines，summary 全 0（规格 §9）

        var from = DateTime.Now.AddDays(-7).ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var info in infos)
        {
            try
            {
                var samples = db.QueryDeviceSamples(info.Machine, 2000, from, null);
                // 趋势：<3 条样本 trend 记 stable，只用当前值打分（规格 §9）
                var cpuTrend = samples.Count >= 3 ? TrendPerDay(samples, "cpu") : double.NaN;
                var diskTrend = samples.Count >= 3 ? TrendPerDay(samples, "disk") : double.NaN;
                int? daysToExhaust = DaysToExhaust(info.DiskFreeGb, diskTrend);
                // 心跳：gap = 距 last_seen 秒数；std = 采样间隔标准差（≥5 条样本才算，与 PredictOffline 同口径）
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
                // top_concern = score×weight 最低子项（并列取先出现者，规格 §3.7）
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
            catch { /* 单机台异常不影响整体 */ }
        }
        foreach (var m in report.Machines)
        {
            if (m.Level == "critical") report.Summary.Critical++;
            else if (m.Level == "warn") report.Summary.Warn++;
            else report.Summary.Ok++;
        }
        return report;
    }

    /// <summary>磁盘剩余按日消耗斜率外推耗尽天数；斜率非负（未消耗）或余量未知 → null。</summary>
    private static int? DaysToExhaust(double freeGb, double diskTrendPerDay)
    {
        if (freeGb <= 0 || double.IsNaN(diskTrendPerDay) || diskTrendPerDay >= -0.05) return null;
        return (int)Math.Ceiling(freeGb / -diskTrendPerDay);
    }

    /// <summary>心跳间隔标准差（秒）；&lt;5 条样本返回 0（不惩罚）。</summary>
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

    // ===== 4 个子分（公开静态：AlertPredictor/AlertHealer/自检复用）=====

    /// <summary>CPU 子分：100 − 当前值线性衰减；3 天外推 >95 扣 30、>80 扣 15（规格 §3.2）。</summary>
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

    /// <summary>磁盘子分：阶梯映射（≥2×阈值 100 / ≥阈值 70 / ≥0.5×阈值 40 / 否则 10，未知 0）；
    /// 耗尽预期 ≤3 天扣 30、≤7 天扣 15（规格 §3.3 + §9 未知扣到底）。</summary>
    public static double DiskScore(double currentFreeGb, int? daysToExhaust, AppConfig cfg)
    {
        double score;
        var th = cfg.DeviceAlertDiskFreeGb > 0 ? cfg.DeviceAlertDiskFreeGb : 10;
        if (currentFreeGb <= 0) score = 0;                       // 未知（0）→ 保守扣到底
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

    /// <summary>内存子分：占用率阶梯（&lt;70% 100 / &lt;85% 80 / &lt;95% 50 / 其余 20）；total=0 → 100 防除零（规格 §3.4/§9）。</summary>
    public static double MemoryScore(int usedMb, int totalMb)
    {
        if (totalMb <= 0) return 100;
        var pct = (double)usedMb / totalMb * 100;
        if (pct < 70) return 100;
        if (pct < 85) return 80;
        if (pct < 95) return 50;
        return 20;
    }

    /// <summary>离线/心跳子分：gap 阶梯（>300s 0 / >90s 40 / >60s 70 / >30s 90 / 其余 100）；
    /// 心跳 std>60s 额外扣 20（链路不稳，规格 §3.5）。</summary>
    public static double OfflineScore(double gapSec, double heartbeatStd, AppConfig cfg)
    {
        double score;
        if (gapSec > 300) score = 0;         // 真离线
        else if (gapSec > 90) score = 40;
        else if (gapSec > 60) score = 70;
        else if (gapSec > 30) score = 90;
        else score = 100;
        if (heartbeatStd > 60) score -= 20;
        return Math.Clamp(score, 0, 100);
    }
}

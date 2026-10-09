using System.Text.Json;

namespace FctAggregator;

/// <summary>归因聚合源（SQL 产物）：全局计数 + 三维桶 + 逐小时失败数。</summary>
public sealed class AttrBucketRow
{
    public string Dim = "";
    public string Key = "";
    public long N;
    public long Fails;
    public long PanelNg;
}

public sealed class AttributionSource
{
    public long TotalN;
    public long TotalFails;
    public List<AttrBucketRow> Buckets = new();
    public Dictionary<int, long> HourlyFails = new();
}

public sealed class TesterStatsRow
{
    public string Model = "";
    public string TestName = "";
    public string Tester = "";
    public int N;
    public double Mean;
    public double Sigma;
    public double? Lolim;
    public double? Hilim;
    public string? Unit;
}

public sealed class AttributionBucketItem
{
    public string Dim { get; set; } = "";
    public string Key { get; set; } = "";
    public int N { get; set; }
    public int Fails { get; set; }
    public double FailRate { get; set; }
    public double GlobalRate { get; set; }
    public double Ratio { get; set; }
    public double PanelNgRate { get; set; }
    public string Hint { get; set; } = "";
}

public sealed class ResourceCorrelationItem
{
    public string Metric { get; set; } = "";
    public double R { get; set; }
    public int Days { get; set; }
}

public sealed class DeviationEvidenceItem
{
    public string Model { get; set; } = "";
    public string TestName { get; set; } = "";
    public double Score { get; set; }
    public double Value { get; set; }
    public double Mean { get; set; }
}

public sealed class AttributionState
{
    public string Date { get; set; } = "";
    public int WindowDays { get; set; }
    public int TotalN { get; set; }
    public double GlobalFailRate { get; set; }
    public List<AttributionBucketItem> Buckets { get; set; } = new();
    public List<ResourceCorrelationItem> ResourceCorrelations { get; set; } = new();
    public List<DeviationEvidenceItem> DeviationEvidence { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this);
    public static AttributionState? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<AttributionState>(json); }
        catch { return null; }
    }
}

/// <summary>规格06期2：失败时段归因（纯函数）。hour 桶失败率 vs 全局，偏差比超阈值报异常；资源采样与失败数 Pearson 相关。</summary>
public static class FailAttributor
{
    public const int MaxBuckets = 50;
    public const int MinCorrelationHours = 6;
    public const double CorrelationThreshold = 0.6;

    public static AttributionState Run(Database db, AppConfig cfg, DateTime? now = null)
    {
        var today = (now ?? DateTime.Now).Date;
        var w = Math.Max(1, cfg.AnalyzeWindowDays);
        var src = db.FetchAttributionStats(today.AddDays(-(w - 1)).ToString("yyyy-MM-dd"));

        double globalRate = src.TotalN > 0 ? (double)src.TotalFails / src.TotalN : 0;
        var buckets = new List<AttributionBucketItem>();
        if (globalRate > 0)
        {
            foreach (var b in src.Buckets)
            {
                if (!string.Equals(b.Dim, "hour", StringComparison.OrdinalIgnoreCase)) continue;
                if (b.N < cfg.AnalyzeAttrMinBucket) continue;
                double rate = (double)b.Fails / b.N;
                double ratio = rate / globalRate;
                if (ratio < cfg.AnalyzeAttrDevRatio) continue;
                buckets.Add(new AttributionBucketItem
                {
                    Dim = b.Dim,
                    Key = b.Key,
                    N = (int)b.N,
                    Fails = (int)b.Fails,
                    FailRate = Math.Round(rate, 4),
                    GlobalRate = Math.Round(globalRate, 4),
                    Ratio = Math.Round(ratio, 2),
                    PanelNgRate = b.N > 0 ? Math.Round((double)b.PanelNg / b.N, 4) : 0,
                    Hint = $"失败率 {rate:P1} vs 全局 {globalRate:P1}（样本 {b.N}，失败 {b.Fails}，panel NG {b.PanelNg}）",
                });
            }
        }
        buckets = buckets.OrderByDescending(b => b.Ratio).Take(MaxBuckets).ToList();

        var state = new AttributionState
        {
            Date = today.ToString("yyyy-MM-dd"),
            WindowDays = w,
            TotalN = (int)src.TotalN,
            GlobalFailRate = Math.Round(globalRate, 4),
            Buckets = buckets,
            ResourceCorrelations = ComputeResourceCorrelations(db, src, w),
            DeviationEvidence = CollectDeviationEvidence(db, cfg, today),
        };
        if (state.Buckets.Count > 0)
            Logger.Warning($"[分析] 失败归因异常桶 {state.Buckets.Count} 项（窗口 {w} 天，全局失败率 {globalRate:P1}）");
        return state;
    }

    /// <summary>FAIL 测项对本机正常态的偏离分（只读模型，开关关返回空）。</summary>
    public static List<DeviationEvidenceItem> CollectDeviationEvidence(Database db, AppConfig cfg, DateTime today)
    {
        var list = new List<DeviationEvidenceItem>();
        if (db == null || cfg == null || !cfg.LearnNormalEnabled) return list;
        try
        {
            var from = today.AddDays(-(Math.Max(1, cfg.AnalyzeWindowDays) - 1)).ToString("yyyy-MM-dd");
            var now = today.AddHours(12);
            foreach (var (model, name, value) in db.ListFailItemValuesSince(from))
            {
                var row = db.GetNormalModel(NormalModelStore.SourceMeasurement, model, name);
                if (row == null) continue;
                var score = DeviationScorer.ScoreOne(row, value, now, cfg.LearnNormalStaleDays);
                if (score is not { } s) continue;
                list.Add(new DeviationEvidenceItem
                {
                    Model = model, TestName = name, Score = s, Value = value, Mean = row.Mean,
                });
            }
            return list.OrderByDescending(x => x.Score).Take(20).ToList();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[分析] 偏离证据采集失败: {ex.Message}");
            return list;
        }
    }

    /// <summary>小时均值 CPU/内存 × 逐小时失败数 Pearson 相关（手写，零依赖）。小时数不足或零方差跳过。</summary>
    public static List<ResourceCorrelationItem> ComputeResourceCorrelations(
        Database db, AttributionSource src, int windowDays)
    {
        var result = new List<ResourceCorrelationItem>();
        try
        {
            var samples = db.GetLocalDeviceSamples(windowDays);
            if (samples.Count == 0) return result;
            var cpuAcc = new Dictionary<int, (double Sum, int N)>();
            var memAcc = new Dictionary<int, (double Sum, int N)>();
            foreach (var s in samples)
            {
                if (s.Ts.Length < 13 || !int.TryParse(s.Ts.Substring(11, 2), out var h) || h is < 0 or > 23) continue;
                // 审计 C-I1：零失败小时必须参与样本（fails=0 保留）——此前剔除导致 r 系统性虚高，易输出虚假相关
                src.HourlyFails.TryGetValue(h, out var fails);
                cpuAcc[h] = (cpuAcc.GetValueOrDefault(h).Sum + s.Cpu, cpuAcc.GetValueOrDefault(h).N + 1);
                memAcc[h] = (memAcc.GetValueOrDefault(h).Sum + s.Mem, memAcc.GetValueOrDefault(h).N + 1);
            }
            var hours = cpuAcc.Keys.OrderBy(h => h).ToList();
            if (hours.Count < MinCorrelationHours) return result;
            var failSeries = hours.Select(h => (double)src.HourlyFails.GetValueOrDefault(h)).ToList();
            AddIfCorrelated(result, "cpu", Pearson(hours.Select(h => cpuAcc[h].Sum / cpuAcc[h].N).ToList(), failSeries), windowDays);
            AddIfCorrelated(result, "mem", Pearson(hours.Select(h => memAcc[h].Sum / memAcc[h].N).ToList(), failSeries), windowDays);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[分析] 资源关联计算失败: {ex.Message}");
        }
        return result;
    }

    private static void AddIfCorrelated(List<ResourceCorrelationItem> list, string metric, double r, int days)
    {
        if (double.IsNaN(r) || Math.Abs(r) < CorrelationThreshold) return;
        list.Add(new ResourceCorrelationItem { Metric = metric, R = Math.Round(r, 3), Days = days });
    }

    /// <summary>手写 Pearson 积矩相关系数（供归因与自检复用）；零方差返回 NaN。</summary>
    public static double Pearson(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        int n = Math.Min(xs.Count, ys.Count);
        if (n < 2) return double.NaN;
        double mx = 0, my = 0;
        for (int i = 0; i < n; i++) { mx += xs[i]; my += ys[i]; }
        mx /= n; my /= n;
        double sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = xs[i] - mx, dy = ys[i] - my;
            sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
        }
        if (sxx <= 0 || syy <= 0) return double.NaN;
        return sxy / Math.Sqrt(sxx * syy);
    }
}

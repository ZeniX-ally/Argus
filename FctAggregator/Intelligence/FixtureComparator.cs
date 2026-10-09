using System.Text.Json;

namespace FctAggregator;

public sealed class EffectItem
{
    public string Model { get; set; } = "";
    public string TestName { get; set; } = "";
    public string TesterA { get; set; } = "";
    public string TesterB { get; set; } = "";
    public double MeanA { get; set; }
    public double MeanB { get; set; }
    public int N1 { get; set; }
    public int N2 { get; set; }
    public double D { get; set; }
    public string Hint { get; set; } = "";
}

public sealed class FixtureState
{
    public string Date { get; set; } = "";
    public int WindowDays { get; set; }
    public int AnalyzedTests { get; set; }
    public List<EffectItem> Effects { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this);
    public static FixtureState? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<FixtureState>(json); }
        catch { return null; }
    }
}

/// <summary>规格06期2：双 tester 同测项效应量对比（纯函数）。d = 均值差 / 合并σ，|d|≥阈值报大效应。</summary>
public static class FixtureComparator
{
    public const int MaxEffects = 50;

    public static FixtureState Run(Database db, AppConfig cfg, DateTime? now = null)
    {
        var today = (now ?? DateTime.Now).Date;
        var w = Math.Max(1, cfg.AnalyzeWindowDays);
        var rows = db.FetchTesterMeasurementStats(today.AddDays(-(w - 1)).ToString("yyyy-MM-dd"));

        var groups = new Dictionary<(string, string), List<TesterStatsRow>>();
        foreach (var r in rows)
        {
            if (r.N < cfg.AnalyzeMinSamples) continue;
            var k = (r.Model, r.TestName);
            if (!groups.TryGetValue(k, out var list)) groups[k] = list = new List<TesterStatsRow>();
            list.Add(r);
        }

        var effects = new List<EffectItem>();
        foreach (var ((model, testName), testers) in groups)
        {
            if (testers.Count < 2) continue;
            var top = testers.OrderByDescending(t => t.N).Take(2).ToList();
            var a = top[0];
            var b = top[1];
            // 审计 M6：双单样本时 N1+N2-2=0，0/0=NaN 曾绕过下方两道护栏直通序列化，炸掉整个 tester 对比——前置护栏
            if (a.N + b.N - 2 <= 0) continue;
            double sPooled = Math.Sqrt(((a.N - 1) * a.Sigma * a.Sigma + (b.N - 1) * b.Sigma * b.Sigma) / (a.N + b.N - 2));
            double range = a.Hilim.HasValue && a.Lolim.HasValue && a.Hilim.Value > a.Lolim.Value
                ? a.Hilim.Value - a.Lolim.Value
                : Math.Abs((a.Mean + b.Mean) / 2);
            if (sPooled < range * 0.01 || sPooled <= 0) continue;
            double d = (b.Mean - a.Mean) / sPooled;
            if (Math.Abs(d) < cfg.AnalyzeEffectD) continue;
            effects.Add(new EffectItem
            {
                Model = model,
                TestName = testName,
                TesterA = a.Tester,
                TesterB = b.Tester,
                MeanA = a.Mean,
                MeanB = b.Mean,
                N1 = a.N,
                N2 = b.N,
                D = Math.Round(d, 2),
                Hint = $"{a.Tester}({a.N} 样本 均值 {a.Mean:F4}) vs {b.Tester}({b.N} 样本 均值 {b.Mean:F4})，效应量 d={d:F2}",
            });
        }

        var state = new FixtureState
        {
            Date = today.ToString("yyyy-MM-dd"),
            WindowDays = w,
            AnalyzedTests = groups.Count,
            Effects = effects.OrderByDescending(e => Math.Abs(e.D)).Take(MaxEffects).ToList(),
        };
        if (state.Effects.Count > 0)
            Logger.Warning($"[分析] tester 效应量异常 {state.Effects.Count} 项（窗口 {w} 天，分析 {state.AnalyzedTests} 测项）");
        return state;
    }
}

namespace FctAggregator;

public sealed class PassDeviationPoint
{
    public long RecordId { get; set; }
    public string Ts { get; set; } = "";
    public string TestName { get; set; } = "";
    public string Model { get; set; } = "";
    public double Value { get; set; }
    public double Z { get; set; }
}

public sealed class DeviationSeries
{
    public string TestName { get; set; } = "";
    public string Model { get; set; } = "";
    public List<(int PassIndex, PassDeviationPoint Point)> Points { get; set; } = new();
}

/// <summary>按 PASS 时间序构建多测项 |z| 序列（纵轴 z，横轴每条 PASS）。</summary>
public static class DeviationSeriesBuilder
{
    static readonly string[] Palette =
    {
        "#00B4FF", "#27AE60", "#E74C3C", "#F39C12", "#9B59B6", "#1ABC9C", "#E67E22", "#3498DB",
        "#E84393", "#00CEC9", "#6C5CE7", "#FD79A8", "#55EFC4", "#74B9FF", "#A29BFE", "#FAB1A0",
    };

    /// <summary>图表窗口：0=自动（库内全量 PASS 测量），1=7，2=14，3=30，4=90（相对库内最新 PASS 日期）。</summary>
    public static (DateTime From, DateTime To, int Days) ResolveChartRange(Database db, int windowIndex)
    {
        var range = db.GetPassMeasurementDateRange();
        var to = DateTime.Now.Date;
        if (range is { } rg && DateTime.TryParseExact(rg.Max, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var maxD))
            to = maxD;

        if (windowIndex == 0 && range is { } rg2 &&
            DateTime.TryParseExact(rg2.Min, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var minD))
        {
            int span = Math.Max(1, (int)(to - minD).TotalDays + 1);
            return (minD, to, Math.Min(span, 3650));
        }

        int days = windowIndex switch { 1 => 7, 2 => 14, 3 => 30, _ => 90 };
        var from = to.AddDays(-(days - 1));
        return (from, to, days);
    }

    public static (List<string> PassLabels, List<DeviationSeries> Series, string? EmptyHint) Build(
        Database db, AppConfig cfg, int windowIndex = 0, int maxPasses = 400, DateTime? anchor = null)
    {
        if (db == null || cfg == null) return (new(), new(), null);
        maxPasses = Math.Clamp(maxPasses, 50, 2000);

        if (db.CountPassMeasurementRows() == 0)
            return (new(), new(), "测量缓存为空——请在下方点「回填历史」补采 XML 测量值。");

        var (from, to, _) = ResolveChartRange(db, windowIndex);
        var fromYmd = from.ToString("yyyy-MM-dd");
        var toYmd = to.ToString("yyyy-MM-dd");
        var scoreAt = anchor ?? to.AddHours(12);

        var testNames = db.ListPassTestNamesInWindow(fromYmd, toYmd);
        if (testNames.Count == 0)
            return (new(), new(), $"窗口 {fromYmd}~{toYmd} 无 PASS 测量——请扩大窗口或回填历史。");

        int rowLimit = Math.Min(500_000, Math.Max(100_000, maxPasses * Math.Max(testNames.Count, 1)));
        var raw = db.ListPassMeasurementPoints(fromYmd, toYmd, null, maxRows: rowLimit);
        if (raw.Count == 0)
            return (new(), new(), $"窗口 {fromYmd}~{toYmd} 无 PASS 测量——请扩大窗口或回填历史。");

        var passOrder = raw
            .Select(r => (r.RecordId, r.Ts))
            .Distinct()
            .OrderBy(p => p.Ts, StringComparer.Ordinal)
            .ThenBy(p => p.RecordId)
            .Take(maxPasses)
            .ToList();
        var passIndex = new Dictionary<(long, string), int>();
        var passLabels = new List<string>(passOrder.Count);
        for (int i = 0; i < passOrder.Count; i++)
        {
            passIndex[passOrder[i]] = i;
            var ts = passOrder[i].Ts;
            passLabels.Add(ts.Length >= 16 ? ts[5..16] : ts);
        }

        // v3.36.0 性能：一次性加载全部 measurement 模型（原逐点 GetNormalModel 为 N+1，大窗口上万次查询）
        var modelDict = new Dictionary<(string, string), NormalModelRow>();
        foreach (var m in db.ListNormalModels(NormalModelStore.SourceMeasurement, limit: 1_000_000))
            modelDict[(m.Model ?? "", m.SignalKey ?? "")] = m;

        var bySignal = new Dictionary<string, DeviationSeries>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in raw)
        {
            if (!passIndex.TryGetValue((row.RecordId, row.Ts), out var xi)) continue;
            if (!bySignal.TryGetValue(row.TestName, out var ser))
            {
                ser = new DeviationSeries { TestName = row.TestName, Model = row.Model };
                bySignal[row.TestName] = ser;
            }
            modelDict.TryGetValue((row.Model, row.TestName), out var mdl);
            var z = ComputeAbsZ(mdl, cfg, row.Value, scoreAt);
            if (z == null) continue;
            ser.Points.Add((xi, new PassDeviationPoint
            {
                RecordId = row.RecordId, Ts = row.Ts, TestName = row.TestName,
                Model = row.Model, Value = row.Value, Z = z.Value,
            }));
        }

        var series = bySignal.Values
            .Where(s => s.Points.Count > 0)
            .OrderBy(s => s.TestName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var ser in series)
            ser.Points.Sort((a, b) => a.PassIndex.CompareTo(b.PassIndex));

        if (series.Count == 0)
        {
            return (passLabels, series,
                $"窗口内 {testNames.Count} 个测项均无 ready 正常态模型——先回填历史并积累样本（默认 ≥{cfg.LearnNormalMinSamples} 条 PASS）。");
        }

        string? hint = null;
        if (series.Count < testNames.Count)
            hint = $"已绘 {series.Count}/{testNames.Count} 条（其余测项模型未 ready，无 |z|）";
        return (passLabels, series, hint);
    }

    private static double? ComputeAbsZ(NormalModelRow? row, AppConfig cfg, double value, DateTime now)
    {
        if (row == null) return null;
        if (NormalModelStore.EffectiveStatus(row, now, cfg.LearnNormalStaleDays) != "ready") return null;
        var sigma = Math.Max(row.Sigma, DeviationScorer.SigmaFloor);
        if (!double.IsFinite(sigma) || sigma <= 0) return null;
        var z = Math.Abs(value - row.Mean) / sigma;
        return double.IsFinite(z) ? Math.Round(z, 3) : null;
    }

    public static string SeriesColorHex(int index) => Palette[Math.Abs(index) % Palette.Length];

    public static string SeriesColorHex(string? testName)
    {
        if (string.IsNullOrEmpty(testName)) return Palette[0];
        int h = 0;
        foreach (var c in testName.ToUpperInvariant())
            h = unchecked(h * 31 + c);
        return Palette[(h & int.MaxValue) % Palette.Length];
    }

    public static double MaxAbsZ(DeviationSeries series)
    {
        if (series?.Points == null || series.Points.Count == 0) return 0;
        double max = 0;
        foreach (var p in series.Points)
            if (p.Point.Z > max) max = p.Point.Z;
        return max;
    }

    /// <summary>勾选过滤：names 为空则一条都不画（让用户自己选）。</summary>
    public static List<DeviationSeries> Filter(List<DeviationSeries> series, IEnumerable<string>? names)
    {
        if (series == null || series.Count == 0) return new();
        if (names == null) return new();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
            if (!string.IsNullOrEmpty(n)) set.Add(n);
        if (set.Count == 0) return new();
        return series.Where(s => set.Contains(s.TestName)).ToList();
    }

    /// <summary>测项很多时默认勾选 |z| 最大的若干条，避免全部叠在一起看不清。</summary>
    public static List<string> DefaultVisibleNames(List<DeviationSeries> series, int max = 8)
    {
        if (series == null || series.Count == 0) return new();
        max = Math.Max(1, max);
        IEnumerable<DeviationSeries> take = series.Count <= max
            ? series
            : series.OrderByDescending(MaxAbsZ).ThenBy(s => s.TestName, StringComparer.OrdinalIgnoreCase).Take(max);
        return take.Select(s => s.TestName).ToList();
    }
}


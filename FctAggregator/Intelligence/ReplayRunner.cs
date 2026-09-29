namespace FctAggregator;

public sealed class ReplayReport
{
    public double DetectionRate { get; set; }
    public double FalsePosPerDay { get; set; }
    public double ReadyCoverage { get; set; }
    public int EventsTotal { get; set; }
    public int AnomalyDays { get; set; }
    public int ScoreDays { get; set; }
    public bool Pass => DetectionRate >= 0.80 && FalsePosPerDay <= 0.20 && ReadyCoverage >= 0.90;
}

/// <summary>
/// 历史回放：前 3 周喂学习器，后 1 周逐条评分，对照已知异常日判卷。
/// selftest 走 RunSynthetic；现场库走 RunField（复制到临时库，不动原库）。
/// </summary>
public static class ReplayRunner
{
    public static ReplayReport RunSynthetic(string dbPath)
    {
        // 审计修复：回放用的是临时/副本库，绝不能顶掉运行中的主库句柄（同 UpdateChecker 的纪律）。
        var db = new Database(dbPath, setAsCurrent: false);
        var cfg = new AppConfig
        {
            LearnNormalEnabled = true,
            LearnNormalMinSamples = 30,
            LearnNormalStaleDays = 14,
            LearnNormalVoteSignals = 3,
            LearnNormalEventScore = 75,
            LearnNormalMaxEventsPerDay = 10,
        };
        var trainStart = new DateTime(2026, 8, 1);
        var trainEnd = trainStart.AddDays(21); // exclusive
        var scoreEnd = trainEnd.AddDays(7);
        var anomaly = new DateTime(2026, 8, 25); // within score week (Aug 22–28)

        string[] keys = { "S1", "S2", "S3", "S4", "S5" };
        double[] means = { 10, 20, 30, 40, 50 };

        for (var d = trainStart; d < trainEnd; d = d.AddDays(1))
        {
            for (int k = 0; k < 8; k++)
            {
                var rec = new TestRecord
                {
                    Model = "G49", Result = "PASS",
                    BatchTimestamp = d.AddHours(8 + k).ToString("yyyy-MM-dd HH:mm:ss"),
                };
                for (int i = 0; i < keys.Length; i++)
                    rec.Measurements.Add(new MeasurementRow
                    {
                        TestName = keys[i],
                        Value = means[i] * (1.0 + 0.04 * (k - 3.5) / 3.5),
                    });
                NormalLearners.ObserveMeasurement(db, cfg, rec);
            }
        }

        for (var d = trainEnd; d < scoreEnd; d = d.AddDays(1))
        {
            bool bad = d.Date == anomaly.Date;
            var rec = new TestRecord
            {
                Model = "G49", Result = "PASS",
                BatchTimestamp = d.AddHours(10).ToString("yyyy-MM-dd HH:mm:ss"),
            };
            var samples = new List<(string, double)>();
            for (int i = 0; i < keys.Length; i++)
            {
                var v = bad ? means[i] + 6.0 : means[i];
                rec.Measurements.Add(new MeasurementRow { TestName = keys[i], Value = v });
                samples.Add((keys[i], v));
            }
            DeviationScorer.EvaluateWindow(db, cfg, NormalModelStore.SourceMeasurement, "G49", rec.BatchTimestamp, samples, d.AddHours(10));
        }

        var anomalyDates = new HashSet<string> { anomaly.ToString("yyyy-MM-dd") };
        int hitDays = 0, fp = 0, scoreDays = 0;
        for (var d = trainEnd; d < scoreEnd; d = d.AddDays(1))
        {
            scoreDays++;
            var n = db.CountDeviationEventsOnDay(d.ToString("yyyy-MM-dd"));
            if (anomalyDates.Contains(d.ToString("yyyy-MM-dd")))
            {
                if (n > 0) hitDays++;
            }
            else fp += n;
        }
        var cov = db.CountNormalModels(scoreEnd, cfg.LearnNormalStaleDays);
        double readyPct = cov.Total == 0 ? 0 : (double)cov.Ready / cov.Total;
        int events = 0;
        for (var d = trainEnd; d < scoreEnd; d = d.AddDays(1))
            events += db.CountDeviationEventsOnDay(d.ToString("yyyy-MM-dd"));

        return new ReplayReport
        {
            DetectionRate = anomalyDates.Count == 0 ? 0 : (double)hitDays / anomalyDates.Count,
            FalsePosPerDay = scoreDays <= 1 ? fp : (double)fp / (scoreDays - anomalyDates.Count),
            ReadyCoverage = readyPct,
            EventsTotal = events,
            AnomalyDays = anomalyDates.Count,
            ScoreDays = scoreDays,
        };
    }

    /// <summary>
    /// 现场库回放：复制到临时库 → 清模型/事件 → 3/4 训练 + 1/4 评分（评分周冻结入模）。
    /// anomalyDates 为空时按评分周 FAIL 日峰推断异常日。
    /// </summary>
    public static ReplayReport RunField(string sourceDbPath, AppConfig cfg, int days = 28, IReadOnlyCollection<string>? anomalyDates = null)
    {
        days = Math.Clamp(days, 7, 365);
        if (!File.Exists(sourceDbPath))
            return new ReplayReport();

        var tempPath = Path.Combine(Path.GetTempPath(), $"argus-replay-{Guid.NewGuid():N}.db");
        try
        {
            try { new Database(sourceDbPath, setAsCurrent: false).CheckpointForCopy(); }
            catch { /* 只读库或已关闭仍尝试复制 */ }
            File.Copy(sourceDbPath, tempPath, true);
            // 审计修复：原实现用会写 Database.Current 的构造，回放结束后 Current 永久指向这个临时库
            // （finally 只删库不恢复）→ 之后的设备采样/TDMS 特征/水位全部写进已删除的临时库，
            // 主库 device_samples_local 断档到下次重启，且 SQLite 会静默重建临时库而不报错。
            var db = new Database(tempPath, setAsCurrent: false);
            db.ClearReplayLearningTables();

            var range = db.GetPassMeasurementDateRange();
            if (range == null)
                return new ReplayReport();

            if (!DateTime.TryParse(range.Value.Max, out var endDate))
                return new ReplayReport();
            if (!DateTime.TryParse(range.Value.Min, out var minDate))
                minDate = endDate;

            var startDate = endDate.AddDays(-(days - 1));
            if (startDate < minDate) startDate = minDate;
            int spanDays = Math.Max(1, (int)(endDate - startDate).TotalDays + 1);
            int trainDays = Math.Max(1, spanDays * 3 / 4);
            var trainEnd = startDate.AddDays(trainDays);
            var scoreFrom = trainEnd;
            var scoreTo = endDate;

            var trainFromYmd = startDate.ToString("yyyy-MM-dd");
            var trainToYmd = trainEnd.ToString("yyyy-MM-dd");
            var scoreToYmd = scoreTo.ToString("yyyy-MM-dd");
            var scoreFromYmd = scoreFrom.ToString("yyyy-MM-dd");

            var rows = db.ListPassMeasurementsBetween(trainFromYmd, scoreTo.AddDays(1).ToString("yyyy-MM-dd"));
            foreach (var grp in rows.GroupBy(r => r.RecordId))
            {
                var day = DayOf(grp.First().Ts);
                if (day.Length == 0) continue;
                if (string.CompareOrdinal(day, trainToYmd) < 0)
                {
                    var first = grp.First();
                    var rec = new TestRecord
                    {
                        Model = first.Model, Result = "PASS", BatchTimestamp = first.Ts,
                        Measurements = grp.Select(g => new MeasurementRow { TestName = g.TestName, Value = g.Value }).ToList(),
                    };
                    NormalLearners.ObserveMeasurement(db, cfg, rec);
                }
                else if (string.CompareOrdinal(day, scoreFromYmd) >= 0 && string.CompareOrdinal(day, scoreToYmd) <= 0)
                {
                    var first = grp.First();
                    var samples = grp.Select(g => (g.TestName, g.Value)).ToList();
                    if (DateTime.TryParse(first.Ts, out var ts))
                        DeviationScorer.EvaluateWindow(db, cfg, NormalModelStore.SourceMeasurement, first.Model, first.Ts, samples, ts);
                }
            }

            var anomalies = ResolveAnomalyDates(db, scoreFromYmd, scoreToYmd, anomalyDates);
            return Run(db, cfg, trainEnd, scoreFrom, scoreTo, anomalies);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private static string DayOf(string ts)
        => ts.Length >= 10 ? ts[..10] : "";

    private static HashSet<string> ResolveAnomalyDates(Database db, string scoreFromYmd, string scoreToYmd, IReadOnlyCollection<string>? provided)
    {
        if (provided != null && provided.Count > 0)
        {
            return new HashSet<string>(
                provided.Select(d => d.Length >= 10 ? d[..10] : d.Trim()),
                StringComparer.Ordinal);
        }
        var fails = db.CountDailyFailsBetween(scoreFromYmd, scoreToYmd);
        if (fails.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
        var vals = fails.Values.Select(v => (double)v).ToList();
        double mean = vals.Average();
        double var = vals.Count > 1 ? vals.Select(v => (v - mean) * (v - mean)).Average() : 0;
        double std = Math.Sqrt(var);
        double thr = mean + 2 * std;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in fails)
        {
            if (kv.Value >= Math.Max(1, thr)) set.Add(kv.Key);
        }
        return set;
    }

    public static ReplayReport Run(Database db, AppConfig cfg, DateTime trainEnd, DateTime scoreFrom, DateTime scoreTo, IReadOnlyCollection<string> anomalyDates)
    {
        var anomalies = new HashSet<string>(anomalyDates ?? Array.Empty<string>(), StringComparer.Ordinal);
        int hitDays = 0, fp = 0, scoreDays = 0;
        for (var d = scoreFrom.Date; d <= scoreTo.Date; d = d.AddDays(1))
        {
            scoreDays++;
            var n = db.CountDeviationEventsOnDay(d.ToString("yyyy-MM-dd"));
            var key = d.ToString("yyyy-MM-dd");
            if (anomalies.Contains(key)) { if (n > 0) hitDays++; }
            else fp += n;
        }
        var cov = db.CountNormalModels(scoreTo, cfg.LearnNormalStaleDays);
        int normalDays = Math.Max(1, scoreDays - anomalies.Count);
        return new ReplayReport
        {
            DetectionRate = anomalies.Count == 0 ? 0 : (double)hitDays / anomalies.Count,
            FalsePosPerDay = (double)fp / normalDays,
            ReadyCoverage = cov.Total == 0 ? 0 : (double)cov.Ready / cov.Total,
            EventsTotal = db.ListDeviationEvents(1000).Count,
            AnomalyDays = anomalies.Count,
            ScoreDays = scoreDays,
        };
    }
}

using System.Collections.Concurrent;
using FctTdmsViewer;

namespace FctAggregator;

/// <summary>backfill 用记录简报（test_records 行冗余，免重解析 XML）。</summary>
public sealed class TdmsRecordBrief
{
    public long Id;
    public string XmlPath = "";
    public string StationId = "";
    public string Model = "";
    public string Category = "";
    public string Sn = "";
    public string TestDate = "";
    public string Ts = "";
}

/// <summary>
/// 规格06期3：TDMS 特征采集编排（实时单消费者队列 + backfill 批量档）。
/// 全程吞异常只计数，绝不阻塞/拖垮主采集链路；开关关或 tdms_root 空一律静默 no-op。
/// </summary>
public static class TdmsFeatureCollector
{
    private const int QueueCapacity = 256;
    private static readonly ConcurrentQueue<(TestRecord Rec, long RecordId)> _queue = new();
    private static int _running;

    public static int FilesOk;
    public static int FilesSkipped;
    public static int Dropped;
    public static string LastError = "";
    public static string LastErrorPath = "";

    /// <summary>实时入队（Engine.ProcessRealtime 调用）：护栏不过直接丢，队列满丢弃计数，由 backfill 幂等补采兜底。</summary>
    public static void Enqueue(TestRecord rec, long recordId)
    {
        var cfg = AppConfig.Instance;
        if (!cfg.AnalyzeTdmsEnabled || string.IsNullOrWhiteSpace(cfg.TdmsRoot)) return;
        if (string.IsNullOrWhiteSpace(rec.Sn) || recordId <= 0) return;
        if (_queue.Count >= QueueCapacity) { Interlocked.Increment(ref Dropped); return; }
        _queue.Enqueue((rec, recordId));
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        Task.Run(DrainLoop);
    }

    /// <summary>单消费者循环：复位 _running 后复查队列仍有余项则重启消费——
    /// 审计 M9：原实现 CAS 失败/复位竞态会让余项滞留，空闲期特征延迟数小时。</summary>
    private static void DrainLoop()
    {
        try
        {
            while (_queue.TryDequeue(out var item))
                CaptureOne(Database.Current, AppConfig.Instance, item.Rec, item.RecordId);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            if (_queue.Count > 0 && Interlocked.CompareExchange(ref _running, 1, 0) == 0)
                Task.Run(DrainLoop);
        }
    }

    /// <summary>单记录特征化：护栏链 → 定位 → 大小护栏 → TdmsDoc 读通道 → 纯函数统计 → 落表（db 可为 null 仅计数）。</summary>
    public static int CaptureOne(Database? db, AppConfig cfg, TestRecord rec, long recordId)
    {
        if (!cfg.AnalyzeTdmsEnabled || string.IsNullOrWhiteSpace(cfg.TdmsRoot)) return 0;
        if (!Database.IsWithinRetention(rec.TestDate, LearnPipeline.EffectiveMeasureRetentionDays(cfg, db)))
        { Interlocked.Increment(ref FilesSkipped); return 0; }
        var sn = rec.Sn ?? "";
        if (string.IsNullOrWhiteSpace(sn) || recordId <= 0) { Interlocked.Increment(ref FilesSkipped); return 0; }
        var path = TdmsFeatureAnalyzer.LocateTdmsFile(cfg.TdmsRoot, rec.Category ?? "", rec.Model ?? "", rec.TestDate ?? "", sn);
        if (string.IsNullOrEmpty(path)) { Interlocked.Increment(ref FilesSkipped); return 0; }
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > (long)cfg.AnalyzeTdmsMaxKb * 1024) { Interlocked.Increment(ref FilesSkipped); return 0; }

            var channels = new List<TdmsFeatureAnalyzer.TdmsChannelData>();
            using (var doc = TdmsDoc.Load(path))
            {
                int taken = 0;
                foreach (var g in doc.Groups)
                {
                    if (taken >= cfg.AnalyzeTdmsMaxChannels) break;
                    foreach (var ci in g.Channels)
                    {
                        if (taken >= cfg.AnalyzeTdmsMaxChannels) break;
                        if (!ci.Numeric || !ci.HasData || ci.Count <= 0) continue;
                        var data = doc.GetData(ci);
                        if (data.Length == 0) continue;
                        channels.Add(new TdmsFeatureAnalyzer.TdmsChannelData { Group = g.Name, Channel = ci.Name, Data = data });
                        taken++;
                    }
                }
            }
            var rows = TdmsFeatureAnalyzer.Compute(channels, cfg.AnalyzeTdmsMaxChannels);
            Interlocked.Increment(ref FilesOk);
            if (rows.Count == 0 || db == null) return rows.Count;
            db.InsertTdmsFeaturesFor(rec, recordId, path, rows);
            DeviationScorer.EvaluateWaveform(db, cfg, rec, rows);
            NormalLearners.ObserveWaveform(db, cfg, rec, rows);
            // 审计：实时喂样推进波形回填水位，避免下次回填重复喂同一 record 双计
            LearnBackfill.AdvanceWatermark(db, LearnBackfill.MetaTdms, recordId);
            return rows.Count;
        }
        catch (Exception ex) { NoteSkip(path, ex); return 0; }
    }

    /// <summary>backfill 批量档：200 文件睡 100ms 防 IO 打满（照期 1 节流）。</summary>
    public static (int Files, int Rows) CaptureBatch(Database db, AppConfig cfg, List<TdmsRecordBrief> briefs)
    {
        int files = 0, rows = 0, sinceSleep = 0;
        foreach (var b in briefs)
        {
            var rec = new TestRecord
            {
                XmlPath = b.XmlPath,
                StationId = b.StationId,
                Model = b.Model,
                Category = b.Category,
                Sn = b.Sn,
                TestDate = b.TestDate,
                BatchTimestamp = b.Ts,
            };
            var n = CaptureOne(db, cfg, rec, b.Id);
            if (n > 0) { files++; rows += n; }
            if (++sinceSleep >= 200) { Thread.Sleep(100); sinceSleep = 0; }
        }
        return (files, rows);
    }

    private static void NoteSkip(string path, Exception ex)
    {
        Interlocked.Increment(ref FilesSkipped);
        LastError = ex.Message;
        LastErrorPath = path;
        Logger.Warning($"[TDMS] 特征化跳过 {Path.GetFileName(path)}: {ex.Message}");
    }
}

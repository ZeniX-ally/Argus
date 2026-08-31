namespace FctFetcher;

/// <summary>
/// CSV / TDMS 定位。
///
/// CSV：与 XML **同目录、同名**，仅扩展名不同（一个 XML 对应一个 CSV，同时生成）。
///   例 F_Fts_PEU_G49_FCT6_{SN}_20260728104558141_202672824558242.xml
///    -> F_Fts_PEU_G49_FCT6_{SN}_20260728104558141_202672824558242.csv
///
/// TDMS：根目录不同（默认 D:\TDMS Log），其下层级与 Results 相同，
///   文件名格式 {SN}_{yyyyMMddHHmmss}.tdms —— SN 在最前面。
///   例 D:\TDMS Log\Offline\E3002781\20260707\E3002781AGV...K70600006_20260707165507.tdms
///
/// 注意 TDMS 的时间戳与 XML 的**不一定相同**（精度不同、可能有偏差），
/// 所以只按 SN 匹配，不做时间比对。同一 SN 若重测会命中多个，全部返回。
/// </summary>
public static class FileLocator
{
    /// <summary>同目录同名换扩展名。找不到返回空串。</summary>
    public static string LocateCsv(Record rec)
    {
        try
        {
            var csv = Path.ChangeExtension(rec.XmlPath, ".csv");
            return File.Exists(csv) ? csv : "";
        }
        catch { return ""; }
    }

    /// <summary>Results 下的相对层级 {Category}\{Model}\{Date} 映射到 TDMS 根目录。</summary>
    public static string MirrorDir(Config cfg, Record rec)
        => Path.Combine(cfg.TdmsRoot, rec.Category, rec.Model, rec.Date);

    /// <summary>
    /// 先在镜像目录按 {SN}_*.tdms 找；找不到且允许回退时，用全局索引按 SN 查。
    /// </summary>
    public static List<string> LocateTdms(Record rec, Config cfg,
                                          Dictionary<string, List<string>>? globalIndex)
    {
        var hits = new List<string>();
        if (string.IsNullOrEmpty(rec.Sn)) return hits;

        var dir = MirrorDir(cfg, rec);
        if (Directory.Exists(dir))
        {
            try
            {
                foreach (var p in Directory.EnumerateFiles(dir, rec.Sn + "_*.tdms"))
                    hits.Add(p);
            }
            catch { /* 目录访问异常时走回退 */ }
        }

        if (hits.Count == 0 && cfg.TdmsFallbackGlobal && globalIndex != null
            && globalIndex.TryGetValue(rec.Sn.ToUpperInvariant(), out var list))
        {
            hits.AddRange(list);
        }
        return hits;
    }

    /// <summary>
    /// 全量索引 TdmsRoot。key = 文件名首段（即 SN，大写）。
    /// 一次遍历换 O(1) 查找，避免每个 SN 都全盘搜。
    /// </summary>
    public static Dictionary<string, List<string>> BuildTdmsIndex(Config cfg, Action<string>? log = null)
    {
        var idx = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(cfg.TdmsRoot) || !Directory.Exists(cfg.TdmsRoot))
        {
            log?.Invoke($"[警告] TDMS 目录不存在, 将无法捞取 TDMS: {cfg.TdmsRoot}");
            return idx;
        }

        int n = 0;
        foreach (var p in Directory.EnumerateFiles(cfg.TdmsRoot, "*.tdms", SearchOption.AllDirectories))
        {
            var stem = Path.GetFileNameWithoutExtension(p);
            int us = stem.IndexOf('_');
            var sn = us > 0 ? stem[..us] : stem;      // {SN}_{ts} -> SN
            if (!idx.TryGetValue(sn, out var list))
                idx[sn] = list = new List<string>();
            list.Add(p);
            n++;
        }
        log?.Invoke($"索引 TDMS: {cfg.TdmsRoot} -> {n} 个文件, {idx.Count} 个不同 SN");
        return idx;
    }

    /// <summary>批量填充 CsvPath / TdmsPaths。</summary>
    public static void Attach(List<Record> recs, Config cfg, Action<string>? log = null)
    {
        var idx = cfg.TdmsFallbackGlobal ? BuildTdmsIndex(cfg, log) : null;
        if (!cfg.TdmsFallbackGlobal && !Directory.Exists(cfg.TdmsRoot))
            log?.Invoke($"[警告] TDMS 目录不存在: {cfg.TdmsRoot}");

        foreach (var r in recs)
        {
            r.CsvPath = LocateCsv(r);
            r.TdmsPaths = LocateTdms(r, cfg, idx);
        }
    }
}

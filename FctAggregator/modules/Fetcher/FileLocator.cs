namespace FctFetcher;

public static class FileLocator
{
    public static string LocateCsv(Record rec)
    {
        try
        {
            var csv = Path.ChangeExtension(rec.XmlPath, ".csv");
            return File.Exists(csv) ? csv : "";
        }
        catch { return ""; }
    }

    public static string MirrorDir(Config cfg, Record rec)
        => Path.Combine(cfg.TdmsRoot, rec.Category, rec.Model, rec.Date);

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
            catch {  }
        }

        if (hits.Count == 0 && cfg.TdmsFallbackGlobal && globalIndex != null
            && globalIndex.TryGetValue(rec.Sn.ToUpperInvariant(), out var list))
        {
            hits.AddRange(list);
        }
        return hits;
    }

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
            var sn = us > 0 ? stem[..us] : stem;
            if (!idx.TryGetValue(sn, out var list))
                idx[sn] = list = new List<string>();
            list.Add(p);
            n++;
        }
        log?.Invoke($"索引 TDMS: {cfg.TdmsRoot} -> {n} 个文件, {idx.Count} 个不同 SN");
        return idx;
    }

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

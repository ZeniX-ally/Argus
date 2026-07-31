namespace FctDistributor;

/// <summary>一条分发计划。</summary>
public sealed class PlanItem
{
    public string SourcePath = "";
    public string TargetPath = "";
    public string Station = "";        // FCT4
    public string Model = "";          // E3002781
    public string Category = "";        // 固定 Offline
    public string DateDir = "";         // 20260716
    public string Sn = "";
    public string Prefix = "";          // P / O / F
    public DateTime Stamp;              // 文件名时间戳
    public long Size;
    public bool TargetExists;
    public string? Warning;             // 校验类问题（不阻止分发）
    public string? Error;               // 致命问题（不分发）

    /// <summary>模板生成规格。非 null = 生成模式（不从源目录拷文件）。</summary>
    public GenSpec? Gen;
    /// <summary>生成模式下的目标文件名（与模板文件名不同）。</summary>
    public string? FileNameOverride;
    /// <summary>主动跳过（不是错误），如该日期目录已有同 SN 文件 / 已达每日上限。</summary>
    public string? SkipReason;
    public bool Skip => SkipReason != null;

    public string FileName => FileNameOverride ?? Path.GetFileName(SourcePath);
    public bool Ok => Error == null;
    public bool IsGenerated => Gen != null;
}

/// <summary>扫描 FCT_Batch 源目录，生成分发计划。</summary>
public sealed class Planner
{
    public sealed class Options
    {
        public string SourceRoot = "";
        public string TargetRoot = "";
        public string? StationFilter;    // null = 全部
        public DateTime? DateFrom;
        public DateTime? DateTo;
        public bool CheckXml;
        public double XmlTolSeconds = 60;
    }

    public sealed class Result
    {
        public List<PlanItem> Items = new();
        public List<string> Messages = new();
        public int SkippedByFilter;
    }

    public Result Build(Options o, Action<string>? log = null, CancellationToken ct = default)
    {
        var res = new Result();
        var src = new DirectoryInfo(o.SourceRoot);
        if (!src.Exists)
        {
            res.Messages.Add($"源目录不存在: {o.SourceRoot}");
            return res;
        }

        // 源结构: {SourceRoot}\FCT{n}\{yyyyMMdd}\*.xml
        var stationDirs = src.GetDirectories()
            .Where(d => System.Text.RegularExpressions.Regex.IsMatch(d.Name, @"^FCT\d+$"))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (stationDirs.Count == 0)
            res.Messages.Add("源目录下未找到 FCT{n} 机台文件夹");

        foreach (var sd in stationDirs)
        {
            ct.ThrowIfCancellationRequested();
            if (o.StationFilter != null &&
                !sd.Name.Equals(o.StationFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var dd in sd.GetDirectories().OrderBy(d => d.Name, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (!NameParser.IsDateDir(dd.Name))
                {
                    log?.Invoke($"[跳过] 非日期目录: {sd.Name}\\{dd.Name}");
                    continue;
                }

                if (!DateTime.TryParseExact(dd.Name, "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var dirDate))
                {
                    log?.Invoke($"[跳过] 日期目录无法解析: {sd.Name}\\{dd.Name}");
                    continue;
                }
                if (o.DateFrom != null && dirDate < o.DateFrom.Value.Date) { res.SkippedByFilter++; continue; }
                if (o.DateTo != null && dirDate > o.DateTo.Value.Date) { res.SkippedByFilter++; continue; }

                foreach (var f in dd.GetFiles("*.xml").OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    var item = BuildItem(f, sd.Name, dd.Name, o);
                    res.Items.Add(item);
                }
            }
        }
        return res;
    }

    private PlanItem BuildItem(FileInfo f, string stationDir, string dateDir, Options o)
    {
        var it = new PlanItem
        {
            SourcePath = f.FullName,
            DateDir = dateDir,
            Size = f.Length,
        };

        var p = NameParser.Parse(f.Name);
        if (p.Error != null) { it.Error = p.Error; return it; }

        it.Prefix = p.Prefix;
        it.Station = p.Station;
        it.Sn = p.Sn;
        it.Model = p.Model;
        it.Stamp = p.Stamp;
        it.Category = NameParser.Category;      // 固定 Offline

        // 一致性：文件名机台号 与 所在机台目录
        if (!p.Station.Equals(stationDir, StringComparison.OrdinalIgnoreCase))
            it.Error = $"文件名机台号 {p.Station} 与目录 {stationDir} 不一致";

        // 一致性：文件名时间戳日期 与 所在日期目录
        if (it.Error == null && p.Ts1.Length >= 8 && p.Ts1[..8] != dateDir)
            it.Error = $"文件名日期 {p.Ts1[..8]} 与目录 {dateDir} 不一致";

        if (it.Error != null) return it;

        // 目标路径: {TargetRoot}\Offline\{型号}\{yyyyMMdd}\{文件名}
        it.TargetPath = Path.Combine(o.TargetRoot, it.Category, it.Model, it.DateDir, f.Name);
        it.TargetExists = File.Exists(it.TargetPath);

        // 可选：校验文件名时间戳 与 XML 内容是否对应
        if (o.CheckXml)
        {
            var rng = XmlTimeScanner.Scan(f.FullName);
            if (rng == null)
                it.Warning = "XML 内无可解析的 TIMESTAMP";
            else if (rng.Max.Date != it.Stamp.Date)
                it.Warning = $"日期不符 (文件名 {it.Stamp:yyyy-MM-dd} / XML {rng.Max:yyyy-MM-dd})";
            else
            {
                var diff = (it.Stamp - rng.Max).TotalSeconds;
                if (Math.Abs(diff) > o.XmlTolSeconds)
                    it.Warning = $"偏差 {diff:+0.000;-0.000}s 超出容差 " +
                                 $"(文件名 {it.Stamp:HH:mm:ss.fff} / XML最晚 {rng.Max:HH:mm:ss.fff})";
            }
        }

        return it;
    }
}

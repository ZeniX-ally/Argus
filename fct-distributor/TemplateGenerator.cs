using System.Globalization;
using System.Xml.Linq;

namespace FctDistributor;

/// <summary>
/// 模板：一个真实的 FCT XML 样本，连同它的固有特征。
///
/// 生成规则（严格）：
///   只改「时间部分」，其余属性/结构/文本一律照抄模板。
///   测试总时长在 ±jitter 秒内随机 -> 得到唯一缩放因子 k = 新TESTTIME / 模板TESTTIME。
///   k 同时驱动三处，保证时长与时间分布严格对应：
///     ① 所有 TIMESTAMP 相对测试开始的偏移 × k
///     ② PANEL/DUT 的 TESTTIME
///     ③ 所有 GROUP 的 TOTALTIME / MODULETIME
///   于是模板里的一切时间比例（TESTTIME/跨度、各步骤占比、Σ顶层==TESTTIME）全部原样保留。
/// </summary>
public sealed class Template
{
    public string Path = "";
    public string FileName = "";
    public string Prefix = "";              // P / O / F
    public string Sn = "";
    public string Model = "";
    public string[] HeadParts = [];         // 文件名去掉双时间戳后的前段（含机台位）
    public int StationPartIndex = -1;       // HeadParts 中机台段的下标
    public int SnPartIndex = -1;            // HeadParts 中 SN 段的下标（机台段之后）
    public string TemplateUser = "";        // 模板里的 FACTORY@USER（为 Debug 时需告警）
    public long Size;                       // 模板字节数（预估生成量用）
    public bool Embedded;                   // 是否来自内置资源

    public XDocument Doc = new();
    public XDeclaration? Declaration;

    /// <summary>文档序遍历得到的所有带 TIMESTAMP 的元素及其原始时间/时区后缀。</summary>
    public List<(DateTime Dt, string Tz)> Stamps = [];
    public int RootStampIndex = -1;          // BATCH 根元素在 Stamps 中的下标

    public DateTime DStart;                  // 测试窗口开始（不含根元素）
    public DateTime DEnd;                    // 测试窗口结束
    public DateTime NameDt;                  // 文件名时间戳
    public TimeSpan NameOffset;              // NameDt - DEnd（O_ 为负，是源数据固有特征）
    public double PanelTestTime;

    public TimeSpan Span => DEnd - DStart;
}

/// <summary>单个待生成文件的规格（扫描阶段算好，执行阶段落盘）。</summary>
public sealed class GenSpec
{
    public Template Tpl = null!;
    public DateTime NewStart;                // 新的测试开始时刻
    public double Scale = 1.0;               // 时间缩放因子 k
    public DateTime BatchStamp;              // BATCH 根元素时间（session 打开，单独推算）
    public double TestTime;                  // 新的 PANEL/DUT TESTTIME = 模板值 × k
    public string Station = "";              // FCT{n}
    public string FileName = "";             // 最终文件名
    public string? User;                     // 非 null 时覆写 FACTORY@USER
}

/// <summary>模板加载 + 生成计划 + 落盘。</summary>
public sealed class TemplateGenerator
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed class Options
    {
        /// <summary>
        /// **使用内置模板**（默认）：模板已编进 exe（tpl\*.xml 作为 EmbeddedResource），
        /// 现场无需携带或指定模板目录。为 false 时才读 <see cref="TemplateDir"/>。
        /// </summary>
        public bool UseEmbeddedTemplates = true;
        public string TemplateDir = "";
        public string TargetRoot = "";
        public List<string> Stations = [];        // FCT1..FCT7
        public DateTime DateFrom;
        public DateTime DateTo;
        public bool SkipSunday = true;
        public bool SkipSaturday;
        public (int H, int M) WindowFrom = (8, 0);
        public (int H, int M) WindowTo = (11, 45);
        public (int H, int M) SessionFloor = (8, 0);
        public double SessionGapMinLo = 1;
        public double SessionGapMinHi = 15;
        /// <summary>测试总时长随机幅度 ±秒。同时缩放 TIMESTAMP 分布与所有 GROUP 耗时。</summary>
        public double TestTimeJitterSec = 20;
        /// <summary>固定为 1：每个模板每天只生成一个文件（业务硬规则）。</summary>
        public const int CopiesPerDay = 1;
        public int? Seed;
        /// <summary>
        /// **只投放到已存在的目录**（默认）：不新建任何目录，扫描目标根下现有的
        /// Offline\{型号}\{yyyyMMdd} 目录，把生成的 XML 放进去（Online 不扫）。
        /// 关闭时退回旧行为：按机台 × 日期范围推算目录路径并自动创建。
        /// </summary>
        public bool ExistingDirsOnly = true;
        /// <summary>
        /// 是否改写 FACTORY@USER。**默认 false，保留模板的 Debug**。
        /// 注意：USER=debug 的数据 FCT-Aggregator 会跳过不入库（Classifier.IsDebug）。
        /// </summary>
        public string User = "Operator";
        public bool ForceUser = false;
    }

    public sealed class PlanResult
    {
        public List<PlanItem> Items = [];
        public List<Template> Templates = [];
        public List<string> Messages = [];
    }

    private Random _rnd = new();

    // ==================== 模板加载 ====================

    /// <summary>内置资源名前缀（RootNamespace + 目录）。</summary>
    public const string EmbeddedPrefix = "FctDistributor.tpl.";

    /// <summary>加载编进 exe 的内置模板（tpl\*.xml）。</summary>
    public static List<Template> LoadEmbedded(Action<string>? onError = null)
    {
        var list = new List<Template>();
        var asm = typeof(TemplateGenerator).Assembly;
        foreach (var name in asm.GetManifestResourceNames()
                                .Where(n => n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                                .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) { onError?.Invoke($"内置资源读取失败: {name}"); continue; }
            using var r = new StreamReader(s, System.Text.Encoding.UTF8, true);
            var text = r.ReadToEnd();

            var fileName = name.StartsWith(EmbeddedPrefix, StringComparison.OrdinalIgnoreCase)
                ? name[EmbeddedPrefix.Length..] : name;
            var t = LoadText(fileName, text, out var err);
            if (t == null) { onError?.Invoke(err!); continue; }
            t.Embedded = true;
            t.Path = @"<内置>\" + fileName;
            list.Add(t);
        }
        return list;
    }

    public static Template? Load(string path, out string? error)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex)
        {
            error = $"{System.IO.Path.GetFileName(path)}: 读取失败 - {ex.Message}";
            return null;
        }
        var t = LoadText(System.IO.Path.GetFileName(path), text, out error);
        if (t != null)
        {
            t.Path = path;
            t.Size = new FileInfo(path).Length;
        }
        return t;
    }

    /// <summary>从 XML 文本解析模板（磁盘文件与内置资源共用）。</summary>
    public static Template? LoadText(string fileName, string xmlText, out string? error)
    {
        error = null;
        var t = new Template
        {
            Path = fileName,
            FileName = fileName,
            Size = System.Text.Encoding.UTF8.GetByteCount(xmlText),
        };

        var p = NameParser.Parse(t.FileName);
        if (p.Error != null) { error = $"{t.FileName}: {p.Error}"; return null; }
        t.Prefix = p.Prefix;
        t.Sn = p.Sn;
        t.Model = p.Model;
        t.NameDt = p.Stamp;

        var stem = System.IO.Path.GetFileNameWithoutExtension(t.FileName);
        var parts = stem.Split('_');
        t.HeadParts = parts[..^2];
        t.StationPartIndex = Array.FindIndex(t.HeadParts,
            s => System.Text.RegularExpressions.Regex.IsMatch(s, @"^FCT\d+$"));
        if (t.StationPartIndex < 0) { error = $"{t.FileName}: 文件名中找不到 FCT 机台段"; return null; }
        t.SnPartIndex = t.StationPartIndex + 1;      // 仅用于校验文件名结构（SN 段从不改写）
        if (t.SnPartIndex >= t.HeadParts.Length) { error = $"{t.FileName}: 机台段之后缺少 SN 段"; return null; }

        try { t.Doc = XDocument.Parse(xmlText, LoadOptions.PreserveWhitespace); }
        catch (Exception ex) { error = $"{t.FileName}: XML 解析失败 - {ex.Message}"; return null; }
        t.Declaration = t.Doc.Declaration;

        var els = StampElements(t.Doc);
        if (els.Count == 0) { error = $"{t.FileName}: XML 内无可解析的 TIMESTAMP"; return null; }

        var root = t.Doc.Root!;
        DateTime? dStart = null, dEnd = null;
        for (int i = 0; i < els.Count; i++)
        {
            var raw = els[i].Attribute("TIMESTAMP")!.Value;
            if (!TryParseStamp(raw, out var dt, out var tz))
            { error = $"{t.FileName}: TIMESTAMP 格式无法识别 - {raw}"; return null; }
            t.Stamps.Add((dt, tz));
            if (ReferenceEquals(els[i], root)) { t.RootStampIndex = i; continue; }
            if (dStart == null || dt < dStart) dStart = dt;
            if (dEnd == null || dt > dEnd) dEnd = dt;
        }
        t.DStart = dStart ?? t.Stamps.Min(s => s.Dt);
        t.DEnd = dEnd ?? t.Stamps.Max(s => s.Dt);
        t.NameOffset = t.NameDt - t.DEnd;

        var panel = t.Doc.Descendants("PANEL").FirstOrDefault();
        var tt = panel?.Attribute("TESTTIME")?.Value;
        t.PanelTestTime = tt != null && double.TryParse(tt, NumberStyles.Float, Inv, out var v) ? v : 0;
        if (t.PanelTestTime <= 0) { error = $"{t.FileName}: PANEL@TESTTIME 缺失或非正数"; return null; }

        t.TemplateUser = t.Doc.Descendants("FACTORY").FirstOrDefault()?.Attribute("USER")?.Value ?? "";

        return t;
    }

    private static List<XElement> StampElements(XDocument doc) =>
        doc.Descendants().Where(e => e.Attribute("TIMESTAMP") != null).ToList();

    private static bool TryParseStamp(string raw, out DateTime dt, out string tz)
    {
        dt = default; tz = "";
        int t = raw.IndexOf('T');
        if (t < 0 || raw.Length < 23) return false;
        int dot = raw.IndexOf('.', t);
        if (dot < 0) return false;
        int end = dot + 1;
        while (end < raw.Length && char.IsDigit(raw[end])) end++;
        tz = raw[end..];
        var core = raw[..Math.Min(end, dot + 4)];
        return DateTime.TryParseExact(core, "yyyy-MM-dd'T'HH:mm:ss.fff", Inv,
                                      DateTimeStyles.None, out dt);
    }

    // ==================== 现有目录扫描 ====================

    /// <summary>目标根下一个已存在的日期目录（固定在 Offline 下）。</summary>
    public sealed record ExistingDir(string Path, string Category, string Model, DateTime Date)
    {
        public string Rel => $"{Category}\\{Model}\\{Date:yyyyMMdd}";
    }

    /// <summary>
    /// 扫描目标根下现有的 **Offline**\{型号}\{yyyyMMdd} 目录。**不创建任何目录**。
    ///
    /// 两个模板（O_ 中断 / P_）在真实产线上只会出现在 Offline，所以 Online 根本不扫。
    /// </summary>
    /// <param name="modelFilter">
    /// 只要这个型号的目录（如 E3002781）。两个模板的 SN 固定、**绝不改写**，
    /// 所以只能放进自己型号的目录；null = 不限（仅供导出/查看用）。
    /// </param>
    public static List<ExistingDir> ScanExistingDateDirs(string root, string? modelFilter = null)
    {
        var list = new List<ExistingDir>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return list;

        foreach (var catDir in Directory.EnumerateDirectories(root))
        {
            var cat = Path.GetFileName(catDir);
            if (!NameParser.IsOfflineDirName(cat)) continue;        // 只认 Offline

            foreach (var modelDir in Directory.EnumerateDirectories(catDir))
            {
                var model = Path.GetFileName(modelDir);
                if (!NameParser.IsValidModel(model)) continue;
                if (modelFilter != null &&
                    !model.Equals(modelFilter, StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var dateDir in Directory.EnumerateDirectories(modelDir))
                {
                    var d = Path.GetFileName(dateDir);
                    if (!NameParser.IsDateDir(d)) continue;
                    if (!DateTime.TryParseExact(d, "yyyyMMdd", Inv, DateTimeStyles.None, out var dt)) continue;
                    list.Add(new ExistingDir(dateDir, cat, model, dt));
                }
            }
        }
        return list.OrderBy(x => x.Date)
                   .ThenBy(x => x.Model, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ==================== 生成计划 ====================

    public PlanResult BuildPlan(Options o, Action<string>? log = null, CancellationToken ct = default)
    {
        var res = new PlanResult();
        _rnd = o.Seed.HasValue ? new Random(o.Seed.Value) : new Random();

        if (o.UseEmbeddedTemplates)
        {
            res.Templates.AddRange(LoadEmbedded(m => res.Messages.Add($"[内置模板] {m}")));
            log?.Invoke($"[模板] 使用**内置模板** {res.Templates.Count} 个（已编进 exe，无需模板目录）");
        }
        else if (!Directory.Exists(o.TemplateDir))
        {
            res.Messages.Add($"模板目录不存在: {o.TemplateDir}");
            return res;
        }
        else
        {
            foreach (var f in Directory.GetFiles(o.TemplateDir, "*.xml").OrderBy(x => x, StringComparer.Ordinal))
            {
                var t = Load(f, out var err);
                if (t == null) { res.Messages.Add($"[忽略模板] {err}"); continue; }
                res.Templates.Add(t);
            }
        }

        foreach (var t in res.Templates)
        {
            log?.Invoke($"[模板] {t.FileName}");
            log?.Invoke($"        前缀 {t.Prefix}_  型号 {t.Model}  TIMESTAMP {t.Stamps.Count} 个  " +
                        $"USER={t.TemplateUser}");
            log?.Invoke($"        TESTTIME {t.PanelTestTime:F6}s  时间分布跨度 {t.Span.TotalSeconds:F3}s  " +
                        $"比值 {t.PanelTestTime / t.Span.TotalSeconds:F6}  " +
                        $"文件名偏移 {t.NameOffset.TotalSeconds:+0.000;-0.000}s");
            if (IsDebugUser(t.TemplateUser))
                log?.Invoke(o.ForceUser
                    ? $"[注意] 模板 USER=\"{t.TemplateUser}\"，已按设置改写为 \"{o.User}\""
                    : $"[注意] 模板 USER=\"{t.TemplateUser}\" 保留不改；" +
                      $"FCT-Aggregator 会把 USER=debug 的数据跳过不入库（如需入库请勾选改写 USER）");
        }
        if (res.Templates.Count == 0)
        {
            res.Messages.Add(o.UseEmbeddedTemplates
                ? "内置模板丢失（exe 被篡改？），请重新部署"
                : "模板目录中没有可用的 XML 模板");
            return res;
        }
        if (o.Stations.Count == 0)
        {
            res.Messages.Add("未选择任何机台");
            return res;
        }

        var dates = EnumerateDates(o).ToList();
        if (o.ExistingDirsOnly)
            return BuildPlanForExistingDirs(o, res, dates, log, ct);

        if (dates.Count == 0)
        {
            res.Messages.Add("日期范围内没有可用日期（检查是否把周末全跳过了）");
            return res;
        }
        log?.Invoke($"[计划] 机台 {o.Stations.Count} 台 × 日期 {dates.Count} 天 × 模板 {res.Templates.Count} 个 × 每日 1 份 = " +
                    $"{o.Stations.Count * dates.Count * res.Templates.Count} 个文件");
        log?.Invoke($"[计划] 测试时长随机 ±{o.TestTimeJitterSec:g}s，同步缩放 TIMESTAMP 分布与全部 GROUP 耗时");
        log?.Invoke($"[规则] 分类目录固定 Offline；每模板每日 1 个；" +
                    $"目录里已有同 SN 文件则跳过，没有就加进去（不限目录内文件总数）");
        if (o.ForceUser)
            log?.Invoke($"[规则] FACTORY@USER 改写为 \"{o.User}\"");
        else
            log?.Invoke($"[规则] FACTORY@USER 保留模板原值（Debug）");

        var usedTs = new HashSet<string>(StringComparer.Ordinal);
        var usedTail = new HashSet<string>(StringComparer.Ordinal);
        // 同一日期目录内已占用的 SN（计划内去重）
        var dirSn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var station in o.Stations)
            foreach (var date in dates)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var tpl in res.Templates)
                    res.Items.Add(MakeItem(tpl, station, date, o, usedTs, usedTail, dirSn));
            }
        return res;
    }

    private static IEnumerable<DateTime> EnumerateDates(Options o)
    {
        for (var d = o.DateFrom.Date; d <= o.DateTo.Date; d = d.AddDays(1))
        {
            if (o.SkipSunday && d.DayOfWeek == DayOfWeek.Sunday) continue;
            if (o.SkipSaturday && d.DayOfWeek == DayOfWeek.Saturday) continue;
            yield return d;
        }
    }

    /// <summary>
    /// 【只投放到已有目录】扫描目标根下现有的日期目录，每个目录里生成对应模板的文件。
    /// 不新建目录；目录的类别/型号/日期就是文件的类别/型号/日期。
    /// </summary>
    private PlanResult BuildPlanForExistingDirs(Options o, PlanResult res, List<DateTime> dates,
                                                Action<string>? log, CancellationToken ct)
    {
        // 两个模板的 SN 固定（金/银板），前 8 位型号就是它们只能去的目录
        var models = res.Templates.Select(t => t.Model)
                                  .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (models.Count > 1)
            res.Messages.Add($"内置模板型号不一致（{string.Join(", ", models)}），请检查 tpl\\ 里的样本");
        var model = models[0];

        var all = ScanExistingDateDirs(o.TargetRoot, model);
        if (all.Count == 0)
        {
            res.Messages.Add($"目标根目录下没有找到任何现有的 Offline\\{model}\\{{yyyyMMdd}} 目录：{o.TargetRoot}");
            res.Messages.Add($"两个模板的 SN 固定为 {model}开头且绝不改写，所以**只能**放进 Offline\\{model}\\；" +
                             "本工具只看 Offline、且不新建目录。");
            return res;
        }

        var dateSet = dates.Select(d => d.Date).ToHashSet();
        var picked = all.Where(d => dateSet.Contains(d.Date)).ToList();

        log?.Invoke($"[扫目录] Offline\\{model}\\ 下现有日期目录 {all.Count} 个，" +
                    $"在日期范围（含周末过滤）内 {picked.Count} 个");
        if (picked.Count == 0)
        {
            res.Messages.Add($"现有目录 {all.Count} 个，但没有落在 {o.DateFrom:yyyy-MM-dd}~{o.DateTo:yyyy-MM-dd} 范围内的" +
                             $"（或被跳周六/周日排除）；现有目录日期：" +
                             string.Join(", ", all.Select(d => d.Date.ToString("yyyyMMdd")).Distinct().Take(12)));
            return res;
        }

        log?.Invoke($"[扫目录] 型号固定 {model}（SN 不改写，其它型号目录根本不看）   " +
                    $"日期 {picked.Select(d => d.Date).Distinct().Count()} 天");
        log?.Invoke($"[规则] 只看 Offline、只往现有目录里放，不新建目录；" +
                    $"目录里已有同 SN 文件则跳过，没有就加进去（不限目录内文件总数）");
        log?.Invoke($"[规则] SN 固定不改（金板 {SnExporter.SnGolden} / 银板 {SnExporter.SnSilvery}）");
        log?.Invoke(o.ForceUser ? $"[规则] FACTORY@USER 改写为 \"{o.User}\""
                                : "[规则] FACTORY@USER 保留模板原值（Debug）");

        var usedTs = new HashSet<string>(StringComparer.Ordinal);
        var usedTail = new HashSet<string>(StringComparer.Ordinal);
        var dirSn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var station in o.Stations)
            foreach (var dir in picked)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var tpl in res.Templates)
                    res.Items.Add(MakeItem(tpl, station, dir.Date, o, usedTs, usedTail, dirSn, dir));
            }

        log?.Invoke($"[计划] 机台 {o.Stations.Count} 台 × Offline 现有目录 {picked.Count} 个 -> 待生成条目 {res.Items.Count} 个");
        return res;
    }

    private PlanItem MakeItem(Template tpl, string station, DateTime date, Options o,
                              HashSet<string> usedTs, HashSet<string> usedTail,
                              HashSet<string> dirSn, ExistingDir? dir = null)
    {
        var it = new PlanItem
        {
            SourcePath = tpl.Path,
            Station = station,
            Model = dir?.Model ?? tpl.Model,
            Sn = tpl.Sn,
            Prefix = tpl.Prefix,
            Category = dir?.Category ?? NameParser.Category,      // 固定 Offline
            DateDir = date.ToString("yyyyMMdd"),
            Size = tpl.Size,
        };

        // ---- ① SN 绝不改写：两个模板就是金/银板，所以只能进自己型号的目录
        string sn = tpl.Sn;
        if (dir != null && !dir.Model.Equals(tpl.Model, StringComparison.OrdinalIgnoreCase))
        {
            it.SkipReason = $"目录型号 {dir.Model} ≠ 模板型号 {tpl.Model}（SN 固定不改写，不能放进去）";
            return it;
        }

        // ---- ② 硬规则：目录里已有**同 SN** 文件就跳过；没有就加进去
        //      （不限制目录内文件总数 —— 真实目录里本来就可能已有很多其它 SN 的文件）
        var targetDir = dir?.Path ?? Path.Combine(o.TargetRoot, it.Category, it.Model, it.DateDir);
        var snKey = targetDir + "|" + sn;

        if (Directory.Exists(targetDir))
        {
            foreach (var f in Directory.EnumerateFiles(targetDir, "*.xml"))
            {
                var pp = NameParser.Parse(Path.GetFileName(f));
                if (pp.Error == null &&
                    pp.Sn.Equals(sn, StringComparison.OrdinalIgnoreCase))
                {
                    it.SkipReason = $"{it.DateDir} 已存在同 SN 文件: {Path.GetFileName(f)}";
                    return it;
                }
            }
        }
        else if (dir == null && o.ExistingDirsOnly)
        {
            it.SkipReason = $"目录不存在且本模式不新建: {it.Category}\\{it.Model}\\{it.DateDir}";
            return it;
        }
        if (!dirSn.Add(snKey))
        {
            it.SkipReason = $"{it.DateDir} 计划内已有同 SN({sn[..8]}…) 文件，每模板每日只生成 1 个";
            return it;
        }
        // ---- ③ 测试总时长：模板值 ± jitter -> 唯一缩放因子 k
        double jitter = (_rnd.NextDouble() * 2 - 1) * o.TestTimeJitterSec;
        double newTt = Math.Round(Math.Max(0.001, tpl.PanelTestTime + jitter), 6);
        double k = newTt / tpl.PanelTestTime;

        // ---- ④ 新的时间分布跨度 = 模板跨度 × k
        var newSpan = TimeSpan.FromMilliseconds(Math.Round(tpl.Span.TotalMilliseconds * k));

        // ---- ⑤ 在窗口内随机放置，并保证文件名时间戳全局唯一
        var lo = date.Date.AddHours(o.WindowFrom.H).AddMinutes(o.WindowFrom.M);
        var hi = date.Date.AddHours(o.WindowTo.H).AddMinutes(o.WindowTo.M)
                 - newSpan - TimeSpan.FromSeconds(1);
        if (hi < lo) hi = lo;
        double rangeMs = (hi - lo).TotalMilliseconds;

        DateTime newStart = default, nameDt = default;
        string? ts1 = null;
        for (int attempt = 0; attempt < 400; attempt++)
        {
            var cand = lo.AddMilliseconds(_rnd.NextDouble() * Math.Max(0, rangeMs));
            // 文件名时间 = 新的测试结束 + 模板固有偏移（保持文件名与 XML 的对应关系）
            var nd = cand + newSpan + tpl.NameOffset;
            if (nd.Date != date.Date) continue;
            var key = nd.ToString("yyyyMMddHHmmssfff");
            if (usedTs.Add(key)) { newStart = cand; nameDt = nd; ts1 = key; break; }
        }
        if (ts1 == null)
        {
            it.Error = "无法生成唯一时间戳（窗口太窄或文件数过多）";
            return it;
        }

        // ---- ⑥ BATCH 根元素：测试开始前 gap 分钟，钳在 [下限, 测试开始-1ms]
        var floor = date.Date.AddHours(o.SessionFloor.H).AddMinutes(o.SessionFloor.M);
        var gap = o.SessionGapMinLo + _rnd.NextDouble() * Math.Max(0, o.SessionGapMinHi - o.SessionGapMinLo);
        var batch = newStart.AddMinutes(-gap);
        if (batch < floor) batch = floor;
        if (batch >= newStart) batch = newStart.AddMilliseconds(-1);

        // ---- ⑦ 文件名第二个时间戳：日期 + 6 位随机（同日唯一）
        string tail = "000000";
        for (int i = 0; i < 600; i++)
        {
            var c = _rnd.Next(0, 1000000).ToString("D6");
            if (usedTail.Add(it.DateDir + c)) { tail = c; break; }
        }

        var head = (string[])tpl.HeadParts.Clone();
        head[tpl.StationPartIndex] = station;      // 只改机台段，SN 段原封不动
        it.FileNameOverride = string.Join("_", head.Append(ts1).Append(it.DateDir + tail)) + ".xml";
        it.Stamp = nameDt;
        it.TargetPath = Path.Combine(targetDir, it.FileNameOverride);
        it.TargetExists = File.Exists(it.TargetPath);
        it.Gen = new GenSpec
        {
            Tpl = tpl,
            NewStart = newStart,
            Scale = k,
            BatchStamp = batch,
            TestTime = newTt,
            Station = station,
            FileName = it.FileNameOverride,
            User = o.ForceUser ? o.User : null,
        };
        return it;
    }

    /// <summary>与 FCT-Aggregator 的 Classifier.IsDebug() 同口径。</summary>
    public static bool IsDebugUser(string? user)
        => (user ?? "").Trim().ToLowerInvariant() == "debug";

    // ==================== 落盘 ====================

    /// <summary>
    /// 按规格生成一个 XML。只改时间相关属性，其余（含结构、空白、所有非时间属性）照抄模板。
    /// </summary>
    public static void Write(GenSpec g, string targetPath)
    {
        var tpl = g.Tpl;
        var doc = new XDocument(tpl.Doc);
        double k = g.Scale;

        // ---- ① TIMESTAMP：相对测试开始的偏移 × k，整体落到 NewStart
        var els = StampElements(doc);
        for (int i = 0; i < els.Count && i < tpl.Stamps.Count; i++)
        {
            var (dt, tz) = tpl.Stamps[i];
            DateTime nd;
            if (i == tpl.RootStampIndex)
            {
                nd = g.BatchStamp;                                  // session 打开时间，单独推算
            }
            else
            {
                var offMs = (dt - tpl.DStart).TotalMilliseconds * k;
                nd = g.NewStart.AddMilliseconds(Math.Round(offMs));
            }
            els[i].SetAttributeValue("TIMESTAMP",
                nd.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", Inv) + tz);
        }

        // ---- ② TESTER（+ 可选改写 USER，避开 debug 跳过规则）
        var factory = doc.Descendants("FACTORY").FirstOrDefault();
        factory?.SetAttributeValue("TESTER", $"PEU_G49_{g.Station}");
        if (g.User != null) factory?.SetAttributeValue("USER", g.User);

        // ---- ③ PANEL / DUT 的 TESTTIME（两处必须相同）
        var ttStr = Fmt(g.TestTime);
        foreach (var name in new[] { "PANEL", "DUT" })
        {
            var e = doc.Descendants(name).FirstOrDefault();
            if (e?.Attribute("TESTTIME") != null) e.SetAttributeValue("TESTTIME", ttStr);
        }

        // ---- ④ 所有 GROUP 的 TOTALTIME / MODULETIME 同乘 k
        //      同一个 k 驱动 ①③④，所以模板里的一切时间比例原样保留：
        //      Σ顶层TOTALTIME == TESTTIME、TESTTIME/跨度、各步骤占比 全部不变
        foreach (var e in doc.Descendants())
            foreach (var attr in new[] { "TOTALTIME", "MODULETIME" })
            {
                var a = e.Attribute(attr);
                if (a == null) continue;
                if (!double.TryParse(a.Value, NumberStyles.Float, Inv, out var v)) continue;
                a.Value = Fmt(Quantize(v * k));
            }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var outDoc = new XDocument(tpl.Declaration, doc.Root);
        var settings = new System.Xml.XmlWriterSettings
        {
            Encoding = new System.Text.UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = false,
        };
        using var w = System.Xml.XmlWriter.Create(targetPath, settings);
        outDoc.Save(w);
    }

    /// <summary>与模板一致的数值写法：非科学计数、最多 6 位小数、去掉多余尾零但保留 "0.0" 形态。</summary>
    /// <summary>
    /// 取整策略：≥ 1e-4 的值保留 6 位小数（与模板写法一致）；
    /// 更小的值改按 6 位有效数字，避免「1e-05 类步骤耗时被压成 8e-06」的缩放失真。
    /// </summary>
    private static double Quantize(double v)
    {
        if (v == 0) return 0;
        if (Math.Abs(v) >= 1e-4) return Math.Round(v, 6);
        int exp = (int)Math.Floor(Math.Log10(Math.Abs(v)));
        double scale = Math.Pow(10, 5 - exp);          // 6 位有效数字
        return Math.Round(v * scale) / scale;
    }

    /// <summary>与模板一致的数值写法（Python float repr 风格）：极小值用 1e-05，其余用小数，整数追加 .0。</summary>
    private static string Fmt(double v)
    {
        if (v == 0) return "0.0";
        if (Math.Abs(v) >= 1e-4)
        {
            var d = v.ToString("0.######", Inv);
            return d.Contains('.') ? d : d + ".0";
        }
        // 极小值：用最短往返表示 + Python 风格指数（小写 e，至少 2 位）
        var s = v.ToString("R", Inv).Replace("E", "e");
        int i = s.IndexOf('e');
        if (i < 0) return s.Contains('.') ? s : s + ".0";
        var mant = s[..i];
        bool neg = s[i + 1] == '-';
        var digits = s[(i + 2)..].TrimStart('0');
        if (digits.Length == 0) digits = "0";
        if (digits.Length < 2) digits = "0" + digits;
        return $"{mant}e{(neg ? "-" : "+")}{digits}";
    }
}

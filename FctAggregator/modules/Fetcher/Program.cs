using System.Globalization;
using System.Runtime.InteropServices;

namespace FctFetcher;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_HIDE = 0;

    private static void HideConsoleWindow()
    {
        var h = GetConsoleWindow();
        if (h != IntPtr.Zero) ShowWindow(h, SW_HIDE);
    }

    public static string ExeDir =>
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)
        ?? AppContext.BaseDirectory;

    public static string ConfigPath => Path.Combine(ExeDir, "config.json");

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            HideConsoleWindow();
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            return 0;
        }

        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        return RunCli(args);
    }

    internal static int RunCliEntry(string[] args) => RunCli(args);

    private static int RunCli(string[] args)
    {
        if (args.Length > 0 && args[0] == "--diag")
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("[错误] --diag 需要一个 xml 文件或目录路径");
                return 2;
            }
            var target = args[1];
            string? diagOut = null;
            for (int i = 2; i < args.Length; i++)
                if (args[i] == "--out" && i + 1 < args.Length) diagOut = args[i + 1];

            var dfiles = new List<string>();
            if (Directory.Exists(target))
                dfiles.AddRange(Directory.EnumerateFiles(target, "*.xml", SearchOption.AllDirectories));
            else if (File.Exists(target))
                dfiles.Add(target);
            else
            {
                Console.Error.WriteLine($"[错误] 路径不存在: {target}");
                return 2;
            }

            TextWriter dw = diagOut != null
                ? new StreamWriter(diagOut, false, System.Text.Encoding.UTF8)
                : Console.Out;
            try
            {
                dw.WriteLine($"FCT-Fetcher 诊断报告  共 {dfiles.Count} 个文件");
                dw.WriteLine();
                foreach (var f in dfiles) Diagnostics.DumpFile(f, dw);
            }
            finally
            {
                if (diagOut != null) { dw.Flush(); dw.Dispose(); }
            }
            if (diagOut != null) Console.WriteLine($"诊断报告已写入: {diagOut}");
            return 0;
        }

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        string? startS = null, endS = null, cfgPath = null, outDir = null,
                resultsRoot = null, tdmsRoot = null, categories = null;
        bool pack = false, noPack = false, keepStage = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "--start": startS = Next(); break;
                case "--end": endS = Next(); break;
                case "--config": cfgPath = Next(); break;
                case "--out": outDir = Next(); break;
                case "--results-root": resultsRoot = Next(); break;
                case "--tdms-root": tdmsRoot = Next(); break;
                case "--pack": pack = true; break;
                case "--no-pack": noPack = true; break;
                case "--keep-stage": keepStage = true; break;
                case "--categories": categories = Next(); break;
                default:
                    Console.Error.WriteLine($"[错误] 未知参数: {a}");
                    Console.WriteLine(Usage);
                    return 2;
            }
        }

        if (startS == null || endS == null)
        {
            Console.Error.WriteLine("[错误] --start 与 --end 必填");
            Console.WriteLine(Usage);
            return 2;
        }
        if (!TryDate(startS, out var start) || !TryDate(endS, out var end))
        {
            Console.Error.WriteLine("[错误] 日期格式应为 yyyyMMdd 或 yyyy-MM-dd");
            return 2;
        }
        if (start > end)
        {
            Console.Error.WriteLine("[错误] --start 晚于 --end");
            return 2;
        }

        var cfg = Config.Load(cfgPath ?? ConfigPath);
        if (resultsRoot != null) cfg.ResultsRoot = resultsRoot;
        if (tdmsRoot != null) cfg.TdmsRoot = tdmsRoot;
        if (outDir != null) cfg.OutputDir = outDir;
        if (pack) cfg.PackFiles = true;
        if (noPack) cfg.PackFiles = false;
        if (keepStage) cfg.KeepStageDir = true;
        if (categories != null)
            cfg.Categories = categories.Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                    StringSplitOptions.TrimEntries);

        void Log(string s) => Console.WriteLine(s);

        Console.WriteLine($"日期区间: {start:yyyy-MM-dd} ~ {end:yyyy-MM-dd} (含首尾)");
        var recs = Scanner.Scan(cfg, start, end, out var st, Log);
        if (recs.Count == 0)
        {
            Console.WriteLine("未捞到任何含 fail 项的记录。");
            PrintStats(st, 0, recs, cfg);
            return 1;
        }

        FileLocator.Attach(recs, cfg, Log);

        var od = cfg.ResolveOutputDir(ExeDir);
        var xlsx = Exporter.Export(recs, cfg, start, end, od);
        Console.WriteLine($"\n清单已输出: {xlsx}");

        if (cfg.PackFiles)
        {
            var pr = Packager.Pack(recs, od, start, end, xlsx, cfg.KeepStageDir, Log);
            Console.WriteLine($"已打包: {pr.ZipPath}");
            Console.WriteLine($"  共 {pr.Total} 个文件 (xml {pr.Xml} / csv {pr.Csv} / tdms {pr.Tdms}), " +
                              $"压缩后 {Packager.HumanSize(pr.ZipBytes)}");
        }

        PrintStats(st, recs.Select(r => r.Sn).Distinct().Count(), recs, cfg);
        return 0;
    }

    private static void PrintStats(ScanStats st, int snCount, List<Record> recs, Config cfg)
    {
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"扫描 XML 总数      : {st.XmlTotal}");
        Console.WriteLine($"  路径不合规跳过   : {st.SkipBadPath}");
        Console.WriteLine($"  日期区间外       : {st.SkipRange}");
        Console.WriteLine($"  区间内           : {st.InRange}");
        Console.WriteLine($"    无 fail 项跳过  : {st.SkipNoFail}");
        Console.WriteLine($"    debug 跳过     : {st.SkipDebug}");
        if (st.SkipParseError > 0)
            Console.WriteLine($"    XML 解析失败   : {st.SkipParseError}");
        Console.WriteLine($"  >> 命中(含fail项) : {st.Fail}  (去重 SN {snCount} 个)");
        Console.WriteLine(new string('=', 60));
        if (recs.Count > 0)
        {
            int csv = recs.Count(r => r.CsvPath.Length > 0);
            int tdms = recs.Count(r => r.TdmsPaths.Count > 0);
            Console.WriteLine($"CSV  命中: {csv}/{recs.Count}");
            Console.WriteLine($"TDMS 命中: {tdms}/{recs.Count}" +
                (Directory.Exists(cfg.TdmsRoot) ? "" : $"   [目录不存在: {cfg.TdmsRoot}]"));
        }
    }

    public static bool TryDate(string s, out DateTime d)
    {
        s = s.Trim().Replace("-", "").Replace("/", "").Replace(".", "");
        return DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture,
                                      DateTimeStyles.None, out d);
    }

    private const string Usage = """
FCT-Fetcher — 按日期区间捞取有 fail 的 XML，并同步捞取对应 SN 的 CSV / TDMS

用法:
  FCT-Fetcher.exe                                     无参数 -> 打开图形界面
  FCT-Fetcher.exe --start 20260722 --end 20260724
  FCT-Fetcher.exe --start 2026-07-22 --end 2026-07-24 --no-pack

参数:
  --start <日期>       起始日期 yyyyMMdd 或 yyyy-MM-dd (必填)
  --end   <日期>       结束日期，含当天 (必填)
  --results-root <路径>  覆盖配置的 Results 根目录
  --tdms-root <路径>     覆盖配置的 TDMS 根目录
  --out <路径>         覆盖输出目录
  --pack               按类型分入 xml/csv/tdms 三个文件夹并打包为 {日期}.zip (默认开)
  --no-pack            不打包, 只出清单 xlsx
  --keep-stage         打包后保留未压缩的中间目录
  --categories <列表>  要扇的分类，逗号分隔，默认 Offline（生产环境 Online 全 pass）
  --config <路径>      指定配置文件
  -h, --help           显示本帮助

诊断（当“XML 里有失败项但工具没识别到”时用）:
  FCT-Fetcher.exe --diag <文件.xml>
  FCT-Fetcher.exe --diag <目录> --out diag.txt
    输出元素×STATUS 分布、失败 GROUP 的层级与子节点结构、所有失败 TEST 的属性，
    以及本工具实际识别出的项 —— 用于定位结构差异
""";
}

using System.Runtime.InteropServices;

namespace FctTdmsViewer;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_HIDE = 0;

    private static void HideConsole()
    {
        var h = GetConsoleWindow();
        if (h != IntPtr.Zero) ShowWindow(h, SW_HIDE);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        bool cliMode = args.Length > 0 && args[0].StartsWith("--");

        if (!cliMode)
        {
            HideConsole();
            ApplicationConfiguration.Initialize();
            var form = new MainForm();
            if (args.Length > 0) form.Shown += (_, _) => form.LoadFile(args[0]);
            Application.Run(form);
            return 0;
        }

        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        return RunCli(args);
    }

    internal static int RunCliEntry(string[] args) => RunCli(args);

    private static int RunCli(string[] args)
    {
        if (args[0] is "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        string? path = null, outPath = null, group = null, channel = null;
        bool info = false, summary = false, dumpJson = false;

        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--info": info = true; path = Next(); break;
                case "--summary": summary = true; path = Next(); break;
                case "--dump-json": dumpJson = true; path = Next(); break;
                case "--out": outPath = Next(); break;
                case "--group": group = Next(); break;
                case "--channel": channel = Next(); break;
                default:
                    Console.Error.WriteLine($"[错误] 未知参数: {args[i]}");
                    Console.WriteLine(Usage);
                    return 2;
            }
        }

        if (path == null)
        {
            Console.Error.WriteLine("[错误] 缺少 tdms 文件路径");
            Console.WriteLine(Usage);
            return 2;
        }
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"[错误] 文件不存在: {path}");
            return 2;
        }

        using var doc = TdmsDoc.Load(path);

        if (info)
        {
            Console.WriteLine($"文件: {path}");
            Console.WriteLine($"大小: {doc.FileBytes / 1024.0 / 1024:F2} MB");
            Console.WriteLine($"组数: {doc.Groups.Count}   通道总数: {doc.TotalChannels}");
            foreach (var p in doc.Properties)
                Console.WriteLine($"  [文件属性] {p.Key} = {p.Value}");
            Console.WriteLine();
            Console.WriteLine($"{"序",-4}{"Group",-44} {"通道",5} {"点数",7}");
            Console.WriteLine(new string('-', 64));
            foreach (var g in doc.Groups)
                Console.WriteLine($"{g.Seq,-4}{g.Name,-44} {g.Channels.Count,5} {g.SampleCount,7}");

            if (group != null)
            {
                var g = doc.Groups.FirstOrDefault(x => x.Name == group);
                if (g == null) { Console.Error.WriteLine($"[错误] 无此组: {group}"); return 1; }
                Console.WriteLine();
                Console.WriteLine($"=== [{g.Seq:00}] {g.Name} ===");
                Console.WriteLine($"{"序",-5}{"通道",-38} {"类型",-10} {"点数",6} {"最小",12} {"最大",12} {"末值",12}");
                int ci = 0;
                foreach (var c in g.Channels)
                {
                    ci++;
                    if (channel != null &&
                        !c.Name.Contains(channel, StringComparison.OrdinalIgnoreCase)) continue;
                    var st = c.Numeric ? TdmsDoc.Describe(doc.GetData(c)) : null;
                    Console.WriteLine($"{ci,-5}{Trunc(c.Name, 38),-38} {c.TypeName,-10} {c.Count,6} " +
                        (st == null ? "" :
                         $"{st.Min,12:G6} {st.Max,12:G6} {st.Last,12:G6}"));
                }
            }
            return 0;
        }

        if (dumpJson)
        {
            var o = outPath ?? Path.ChangeExtension(path, ".struct.json");
            JsonDumper.Dump(doc, o);
            Console.WriteLine($"结构快照已导出: {o}");
            Console.WriteLine($"  {doc.Groups.Count} 组 / {doc.TotalChannels} 通道 / " +
                              $"{doc.Groups.Sum(g => g.Channels.Sum(c => c.Count))} 个数据点");
            return 0;
        }

        if (summary)
        {
            var o = outPath ?? Path.ChangeExtension(path, ".summary.csv");
            Exporter.ExportSummary(doc, o);
            Console.WriteLine($"结构清单已导出: {o}");
            return 0;
        }

        Console.WriteLine(Usage);
        return 0;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private const string Usage = """
FCT-TdmsViewer — TDMS 文件查看器

图形界面:
  FCT-TdmsViewer.exe                        打开空界面（可拖入 .tdms 文件）
  FCT-TdmsViewer.exe <文件.tdms>            打开并载入该文件

命令行:
  FCT-TdmsViewer.exe --info <文件.tdms>                     打印组/通道概览
  FCT-TdmsViewer.exe --info <文件> --group "6.1 Power Test" 列出该组各通道统计
  FCT-TdmsViewer.exe --info <文件> --group "<组>" --channel KL30   按名字过滤通道
  FCT-TdmsViewer.exe --summary <文件> [--out x.csv]         导出全部通道统计为 CSV
  -h, --help                                               显示帮助

界面操作:
  左侧勾选通道叠加显示波形（最多 8 条）；选中组可看该组全通道统计表
  搜索框过滤通道名（160 个通道时很有用）
  波形图: 滚轮缩放 · 拖拽平移 · 双击复位 · 鼠标十字线读值
""";
}

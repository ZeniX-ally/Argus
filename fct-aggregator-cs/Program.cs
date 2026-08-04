using System.Runtime.InteropServices;
using System.Threading;

namespace FctAggregator;

/// <summary>
/// 套件唯一入口（v2.9.0 起）。
///
/// 五个原独立程序合成一个 exe，各工具自己的 <c>Program.Main</c> 仍在源码里
/// （里面有 CLI 逻辑），靠 csproj 的 <c>&lt;StartupObject&gt;</c> 指定本类为真入口。
///
/// 用法：
/// <code>
/// Argus.exe                 主程序 GUI（采集聚合 + 工具箱）
/// Argus.exe fetch --diag    取数工具 CLI（原 FCT-Fetcher.exe）
/// Argus.exe tdms --help     TDMS 工具 CLI（原 FCT-TdmsViewer.exe）
/// Argus.exe tdms x.tdms     直接用 TDMS 窗口打开文件
/// Argus.exe --help          子命令一览
/// </code>
/// </summary>
static class Program
{
    private static Mutex? _singleInstanceMutex;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
    private const int AttachParentProcess = -1;

    /// <summary>
    /// 本程序是 WinExe（GUI 优先，不闪控制台窗口），跑 CLI 子命令时得把父进程的控制台接过来。
    /// ⚠ 只 AttachConsole 不够 —— 必须再 <see cref="Console.SetOut"/> 到
    /// <see cref="Console.OpenStandardOutput"/>，否则 .NET 早已把 Console.Out 绑成
    /// TextWriter.Null，管道/重定向下输出会全部丢失（原 fetcher/tdms 就是为此才做成控制台程序）。
    /// </summary>
    private static void EnsureConsole()
    {
        try { AttachConsole(AttachParentProcess); } catch { }
        try
        {
            var so = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(so);
            var se = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetError(se);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch { }
    }

    [STAThread]
    static int Main(string[] args)
    {
        // ===== 子命令（CLI）=====
        if (args.Length > 0)
        {
            var sub = args[0].Trim().ToLowerInvariant();
            var rest = args.Skip(1).ToArray();
            switch (sub)
            {
                case "fetch":
                    EnsureConsole();
                    return FctFetcher.Program.RunCliEntry(rest.Length == 0 ? new[] { "--help" } : rest);

                case "tdms":
                    // 带 --xxx 走 CLI；给个文件路径就直接用 GUI 打开
                    if (rest.Length > 0 && rest[0].StartsWith("-"))
                    {
                        EnsureConsole();
                        return FctTdmsViewer.Program.RunCliEntry(rest);
                    }
                    return RunToolGui(() =>
                    {
                        var f = new FctTdmsViewer.MainForm();
                        if (rest.Length > 0) f.Shown += (_, _) => f.LoadFile(rest[0]);
                        return f;
                    });

                case "rank":
                    return RunToolGui(() => new FctFailRanker.MainForm());

                case "-h":
                case "--help":
                case "help":
                    EnsureConsole();
                    PrintUsage();
                    return 0;
            }
            // 不认识的参数：当成 TDMS 文件拖到 exe 上（保持原 tdms-viewer 的行为）
            if (File.Exists(args[0]) && args[0].EndsWith(".tdms", StringComparison.OrdinalIgnoreCase))
                return RunToolGui(() =>
                {
                    var f = new FctTdmsViewer.MainForm();
                    f.Shown += (_, _) => f.LoadFile(args[0]);
                    return f;
                });
            EnsureConsole();
            Console.Error.WriteLine($"未知子命令: {args[0]}");
            PrintUsage();
            return 2;
        }

        // ===== 主程序 GUI =====
        // 单实例保护（只管 GUI；CLI 子命令不受限，否则主程序开着就没法跑命令行）
        _singleInstanceMutex = new Mutex(true, @"Global\Argus_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("程序已在运行，请勿重复打开。", "FCT 工具套件",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();

        var cfg = AppConfig.Instance;
        Logger.SetLevel(cfg.LogLevel);

        // 开机自启改为手动(Debug页提供开关): 启动时自动写自启项会被 EDR(如SentinelOne)当作
        // “自我持久化”行为拦截。需要自启时由运维手动开启或把快捷方式拖到启动文件夹。
        // try { AutoStart.EnsureFirstRun(); } catch { }

        // 启动后台引擎
        var engine = new Engine(cfg);
        engine.Start();

        // 开启动画（v3.1.0）：splash 先于主窗口播放；主窗口显示 450ms 后收起（视觉无缝）
        var splashImg = SplashForm.TryLoadEmbedded();
        SplashForm? splash = null;
        if (splashImg != null)
        {
            splash = new SplashForm(splashImg);
            splash.Start();
        }

        // 预构造主窗口（布局就绪即可零延迟显示）
        var form = new MainForm(engine);

        if (splash != null)
        {
            form.Shown += (_, _) =>
            {
                // 主窗口已显示：450ms 后隐藏 splash（此时动画通常已缩成圆点或接近尾声）
                var t = new System.Windows.Forms.Timer { Interval = 450 };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    t.Dispose();
                    splash!.Hide();
                    splash.Dispose();
                };
                t.Start();
            };
        }

        Application.Run(form);

        engine.Stop();
        GC.KeepAlive(_singleInstanceMutex);
        return 0;
    }

    /// <summary>单独把某个工具窗口作为独立进程跑起来（不启动采集引擎）。</summary>
    private static int RunToolGui(Func<Form> factory)
    {
        ApplicationConfiguration.Initialize();
        try { Logger.SetLevel(AppConfig.Instance.LogLevel); } catch { }
        Application.Run(factory());
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
FCT 工具套件 (Argus.exe)

  Argus.exe                    主程序：采集聚合 / 待办维修 / 工具箱
  Argus.exe rank               FAIL 排行（窗口）
  Argus.exe tdms [文件.tdms]   TDMS 波形查看（窗口）
  Argus.exe tdms --help        TDMS 工具的命令行用法
  Argus.exe fetch --help       取数打包工具的命令行用法
  Argus.exe --help             本帮助

四个工具也能在主程序左侧「工具箱」里直接点开。
""");
    }
}

using System.Runtime.InteropServices;
using System.Threading;

namespace FctAggregator;

static class Program
{
    private static Mutex? _singleInstanceMutex;
    private static ConfigWatcher? _configWatcher;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
    private const int AttachParentProcess = -1;

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
        bool debugMode = args.Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase));
        args = args.Where(a => !a.Equals("--debug", StringComparison.OrdinalIgnoreCase)).ToArray();

        // 审计：--post-update 由 UpdateChecker.ScheduleRestart / UpdatePromptForm.RestartApp 传入，
        // 必须在子命令分发之前剥离；否则它会被当成未知子命令 return 2，升级重启后 GUI 永不启动。
        bool postUpdate = args.Any(a => a.Equals("--post-update", StringComparison.OrdinalIgnoreCase));
        args = args.Where(a => !a.Equals("--post-update", StringComparison.OrdinalIgnoreCase)).ToArray();

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

                case "upgrade":
                    return FctAggregator.modules.Upgrader.UpgradeEntry.Run();

                case "backfill":
                    EnsureConsole();
                    return BackfillTool.Run(rest);

                case "learn":
                    EnsureConsole();
                    return LearnCli.Run(rest);

                case "logs":
                    EnsureConsole();
                    return LogsCli.Run(rest);

                case "-h":
                case "--help":
                case "help":
                    EnsureConsole();
                    PrintUsage();
                    return 0;
            }
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

        if (postUpdate || UpdateChecker.HasPendingUpdate())
        {
            try
            {
                Logger.Info("[更新器] 检测到待提交更新，启动时执行…");
                UpdateChecker.CommitPendingUpdate();
            }
            catch (Exception ex)
            {
                Logger.Error($"[更新器] 提交失败: {ex.Message}");
            }
        }
        UpdateChecker.CleanupOldBinaries(); // 上次热升级换装遗留的 *.old 旧映像（旧进程退出后锁已释放）

        // UI-W10：必须在任何窗口（含下面「已在运行」提示框）之前初始化——它才应用 PerMonitorV2。
        // 原先放在互斥量之后：非 100% 缩放下那句重复启动提示是不感知 DPI 的，会被系统位图拉伸（发虚）。
        ApplicationConfiguration.Initialize();

        _singleInstanceMutex = new Mutex(true, @"Global\Argus_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("程序已在运行，请勿重复打开。", "FCT 工具套件",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        // 全局异常兜底：UI 线程异常记录日志并弹窗提示（不退出），非 UI 线程异常尽量落日志后放行
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Logger.Error($"[全局异常] UI 线程: {e.Exception.GetType().FullName}: {e.Exception.Message}\n{e.Exception.StackTrace}");
            // 现场只报「有时候 UI 错误」时，没有这一行就只能靠猜：记录是哪一页、窗口多大、哪个屏、什么 DPI、是否远端会话
            try { Logger.Error($"[全局异常] 现场: {UiScreenFit.DescribeActiveForm()}"); } catch { }
            try
            {
                MessageBox.Show("发生界面异常，详情见日志。", "FCT 工具套件",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Logger.Error($"[全局异常] 非UI线程(是否终止进程={e.IsTerminating}): {ex?.GetType().FullName}: {ex?.Message}\n{ex?.StackTrace}");
        };

        var cfg = AppConfig.Instance;
        Logger.SetLevel(cfg.LogLevel);
        try
        {
            var ver = typeof(Program).Assembly.GetName().Version;
            Logger.Info($"Argus v{ver} 启动 pid={Environment.ProcessId} log={Logger.CurrentLogPath}");
        }
        catch { }
        try { Task.Run(() => { try { LeftoverAgg.LogAtStartup(); } catch { } }); } catch { }

        try
        {
            var pPath = Path.Combine(AppConfig.BaseDir, cfg.ParsersPath);
            string? pJson = File.Exists(pPath) ? File.ReadAllText(pPath) : null;
            Parsing.ParserRegistry.Load(pJson, cfg.StationId);
            Logger.Info($"[解析] 注册表已初始化{(pJson != null ? $"（规则文件 {cfg.ParsersPath}）" : "（仅内置默认规则）")}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[解析] 注册表初始化失败，回落内置默认: {ex.Message}");
        }

        var engine = new Engine(cfg);

        try
        {
            _configWatcher = new ConfigWatcher(Path.Combine(AppConfig.BaseDir, "config.json"));
            _configWatcher.ConfigChanged += (_, _) =>
            {
                if (!AppConfig.ReloadFromDisk()) return;
                try { DeviceSampleRecorder.ApplyConfig(); } catch { }
                Logger.Info("[ConfigWatcher] 新配置已应用到运行中实例");
            };
        }
        catch (Exception ex)
        {
            Logger.Warning($"[ConfigWatcher] 启动失败：{ex.Message}（将继续运行但无法热更新）");
        }

        engine.Start();

        Application.Run(new MainForm(engine, debugMode));

        engine.Stop();
        GC.KeepAlive(_singleInstanceMutex);
        return 0;
    }

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

  Argus.exe                    主程序：采集 / 待办维修
  Argus.exe rank               FAIL 排行（窗口）
  Argus.exe tdms [文件.tdms]   TDMS 波形查看（窗口）
  Argus.exe tdms --help        TDMS 工具的命令行用法
  Argus.exe fetch --help       取数打包工具的命令行用法
  Argus.exe backfill [--days N]  存量 PASS 文件测量值补采（规格06，幂等可续跑）
  Argus.exe learn --backfill [--days N]  正常态模型历史回填（learn_normal_enabled）
  Argus.exe learn --replay [--days N] [--anomaly-dates yyyy-MM-dd,...]  现场库回放三指标（复制临时库）
  Argus.exe learn --replay --synthetic  合成回放三指标（selftest 口径）
  Argus.exe logs --export [zip]  导出运行日志包（debug 用）
  Argus.exe logs --tail          打印 app.log 末尾
  Argus.exe --help             本帮助

三个工具也能在主程序左侧「工具箱」里直接点开。
""");
    }
}

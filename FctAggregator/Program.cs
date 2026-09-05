using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
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

                case "agg":
                    if (rest.Any(a => a.Equals("--install", StringComparison.OrdinalIgnoreCase)))
                    {
                        EnsureConsole();
                        return RunAggInstall();
                    }
                    if (rest.Any(a => a.Equals("--web", StringComparison.OrdinalIgnoreCase)))
                    {
                        EnsureConsole();
                        return RunAggWebService();
                    }
                    if (rest.Any(a => a.Equals("--service", StringComparison.OrdinalIgnoreCase)))
                    {
                        EnsureConsole();
                        return RunAggService(rest);
                    }
                    EnsureConsole();
                    Console.WriteLine("用法: Argus.exe agg --web       启动本机 Mesh 节点（Web 看板，无窗体）");
                    Console.WriteLine("      Argus.exe agg --install  一键部署本机 Mesh 节点（防火墙/开机自启/启动）");
                    Console.WriteLine("      Argus.exe agg --service install|uninstall|start|stop|status  Windows 服务注册与启停（需管理员，Session 0 常驻）");
                    return 0;

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

        bool postUpdate = args.Any(a => a.Equals("--post-update", StringComparison.OrdinalIgnoreCase));
        args = args.Where(a => !a.Equals("--post-update", StringComparison.OrdinalIgnoreCase)).ToArray();
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
            _configWatcher.ConfigChanged += (sender, newCfg) =>
            {
                Logger.Info("[ConfigWatcher] 新配置已生效");
            };
        }
        catch (Exception ex)
        {
            Logger.Warning($"[ConfigWatcher] 启动失败：{ex.Message}（将继续运行但无法热更新）");
        }

        engine.Start();

        var splashImg = SplashForm.TryLoadEmbedded();
        SplashForm? splash = null;
        if (splashImg != null)
        {
            splash = new SplashForm(splashImg);
            splash.Start();
        }

        var form = new MainForm(engine, debugMode);

        if (splash != null)
        {
            form.Shown += (_, _) =>
            {
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

    private static int RunToolGui(Func<Form> factory)
    {
        ApplicationConfiguration.Initialize();
        try { Logger.SetLevel(AppConfig.Instance.LogLevel); } catch { }
        Application.Run(factory());
        return 0;
    }

    private static int RunAggWebService()
    {
        var cfg = AppConfig.Instance;
        try { Logger.SetLevel(cfg.LogLevel); } catch { }

        using var svc = new HeadlessService(cfg);
        svc.Start();

        if (svc.Listening)
        {
            Console.WriteLine("Mesh 节点已启动，浏览器访问:");
            foreach (var ip in LocalIPv4Addresses())
                Console.WriteLine($"  http://{ip}:{cfg.MeshPort}/");
            Console.WriteLine($"  监听端口 {cfg.MeshPort}，副本库 {svc.Db.DbPath}");
            if (cfg.Peers.Count == 0)
                Console.WriteLine("  提示: 未配置 peers，本机为单节点模式（在 config.json 的 peers 添加邻居后重启互联）");
            Console.WriteLine("  按 Ctrl+C 退出");
        }
        else
        {
            Console.WriteLine("警告: Mesh 节点启动失败——端口被占用或缺少监听权限（尝试以管理员身份运行）");
        }

        var running = true;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            running = false;
        };
        while (running) Thread.Sleep(500);

        Console.WriteLine("Mesh 节点已退出");
        return 0;
    }

    private static int RunAggInstall()
    {
        Console.WriteLine("聚合服务一键部署");
        Console.WriteLine();

        var r = AggDeployer.Deploy();

        Console.WriteLine($"步骤 1/5: config.json 已就绪（mesh_port={r.Port}）");
        if (r.FirewallOk)
            Console.WriteLine("步骤 2/5: 防火墙规则 ArgusAggWeb 已就绪");
        else
            Console.WriteLine($"步骤 2/5: 防火墙放行失败——{r.FirewallMsg}（请以管理员身份重新运行）");
        if (r.AutoStartOk)
            Console.WriteLine("步骤 3/5: 开机自启已添加（启动文件夹）");
        else
            Console.WriteLine($"步骤 3/5: 开机自启设置失败: {r.AutoStartMsg}");
        if (r.ServiceOk)
            Console.WriteLine("步骤 4/5: Mesh 节点已启动（无窗口常驻）");
        else
            Console.WriteLine($"步骤 4/5: 服务启动失败: {r.ServiceMsg}（可手动运行 Argus.exe agg --web）");

        Console.WriteLine("步骤 5/5: 浏览器访问地址");
        foreach (var ip in r.Addresses)
            Console.WriteLine($"  http://{ip}:{r.Port}/{(r.NewToken.Length > 0 ? $"?token={r.NewToken}" : "")}");
        if (r.Addresses.Count == 0)
            Console.WriteLine($"  http://本机IP:{r.Port}/（未枚举到 IP，请用本机实际 IP）" +
                (r.NewToken.Length > 0 ? $"?token={r.NewToken}" : ""));
        if (r.NewToken.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"访问令牌(agg_token): {r.NewToken}");
            Console.WriteLine("（首次用上面带 ?token= 的地址打开即可；机台端推送需配同一串 token）");
        }
        Console.WriteLine("邻居机台 config 配置: peers=[http://邻居IP:8081/, ...] + mesh_port=8081 + agg_token=同串");
        Console.WriteLine();
        Console.WriteLine("部署完成。浏览器打开上面任一地址即可查看全产线看板（本机即 Mesh 节点）。");
        return 0;
    }

    private static int RunAggService(string[] args)
    {
        var op = args.FirstOrDefault(a => !a.Equals("--service", StringComparison.OrdinalIgnoreCase) && !a.StartsWith("-"))?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(op) || op == "help")
        {
            Console.WriteLine("用法: Argus.exe agg --service install    注册 Windows 服务 ArgusAgg（auto start，失败 5s 重启，Session 0 常驻）");
            Console.WriteLine("      Argus.exe agg --service uninstall  卸载服务（先 stop）");
            Console.WriteLine("      Argus.exe agg --service start      启动服务");
            Console.WriteLine("      Argus.exe agg --service stop       停止服务");
            Console.WriteLine("      Argus.exe agg --service status     查询服务状态");
            Console.WriteLine("      Argus.exe agg --service run        直接以无头模式常驻（非服务，用于调试，与 --web 同但走 HeadlessService 全量）");
            return 0;
        }
        if (op == "run")
        {
            Console.WriteLine("[Service] 直接以无头模式常驻（调试用，与 agg --web 同）...");
            return RunAggWebService();
        }
        (bool ok, string msg) res = op switch
        {
            "install" => ServiceManager.Install(),
            "uninstall" => ServiceManager.Uninstall(),
            "start" => ServiceManager.Start(),
            "stop" => ServiceManager.Stop(),
            "status" => ServiceManager.Status(),
            _ => (false, $"未知动作: {op}（可用 install/uninstall/start/stop/status/run）"),
        };
        Console.WriteLine(res.msg);
        if (!res.ok && op is "install" or "start" or "stop" or "uninstall")
        {
            if (!AggDeployer.IsAdmin())
                Console.WriteLine("提示：此操作需要管理员权限（以管理员身份运行命令行）");
        }
        return res.ok ? 0 : 1;
    }

    internal static string BuildDefaultAggConfigJson() => AggDeployer.BuildDefaultConfigJson();

    private static List<string> LocalIPv4Addresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var addr in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr))
                    list.Add(addr.ToString());
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[聚合服务] 枚举本机 IP 失败: {ex.Message}");
        }
        return list;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
FCT 工具套件 (Argus.exe)

  Argus.exe                    主程序：采集聚合 / 待办维修 / 工具箱
  Argus.exe rank               FAIL 排行（窗口）
  Argus.exe agg --web         本机 Mesh 节点(无窗口,浏览器访问机台IP:端口看全产线)
  Argus.exe agg --install     一键部署本机 Mesh 节点(生成配置/防火墙/开机自启/启动)
  Argus.exe agg --service install|start|stop  Windows 服务（Session 0 常驻，会话锁定不中断，崩溃自愈）
  Argus.exe tdms [文件.tdms]   TDMS 波形查看（窗口）
  Argus.exe tdms --help        TDMS 工具的命令行用法
  Argus.exe fetch --help       取数打包工具的命令行用法
  Argus.exe --help             本帮助

四个工具也能在主程序左侧「工具箱」里直接点开。
""");
    }
}

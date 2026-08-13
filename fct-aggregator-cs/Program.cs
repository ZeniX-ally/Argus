using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
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
        // ===== 启动参数 =====
        // --debug（大小写不敏感）：显示侧边栏「调试工具」入口（清库/推送测试/自启开关，产线不应能进）。
        // 先剥离再分发子命令，否则 "--debug" 会被当成未知子命令拒掉。
        bool debugMode = args.Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase));
        args = args.Where(a => !a.Equals("--debug", StringComparison.OrdinalIgnoreCase)).ToArray();

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

                case "agg":
                    // 一键部署：config 生成 / 防火墙放行 / 开机自启 / 启动服务（优先于 --web）
                    if (rest.Any(a => a.Equals("--install", StringComparison.OrdinalIgnoreCase)))
                    {
                        EnsureConsole();
                        return RunAggInstall();
                    }
                    // 纯 Web 服务模式：无窗体常驻，浏览器访问 http://机台IP:端口 看板
                    if (rest.Any(a => a.Equals("--web", StringComparison.OrdinalIgnoreCase)))
                    {
                        EnsureConsole();
                        return RunAggWebService();
                    }
                    // 聚合端看板：共享目录宿主机专用。AggShareRoot 为空时由窗体自报"未配置"，不在此报错。
                    return RunToolGui(() => new AggCenterForm(
                        AppConfig.Instance.AggShareRoot,
                        Path.Combine(AppConfig.BaseDir, "data", "agg_center.db")));

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
        var form = new MainForm(engine, debugMode);

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

    /// <summary>
    /// 聚合端纯 Web 服务模式（argus agg --web）：聚合监听 + HttpListener 看板，无窗体常驻。
    /// 主线程阻塞等待 Ctrl+C 优雅退出；Ctrl+C 只置标志，退出路径统一走 using Dispose 停全部组件。
    /// </summary>
    private static int RunAggWebService()
    {
        var cfg = AppConfig.Instance;
        try { Logger.SetLevel(cfg.LogLevel); } catch { }

        using var db = new AggDatabase(Path.Combine(AppConfig.BaseDir, "data", "agg_center.db"));
        try { db.Open(); }
        catch (Exception ex) { Logger.Error($"[聚合服务] 聚合库打开失败（后续插入会再试）: {ex.Message}"); }

        using var watcher = new AggWatcher(cfg.AggShareRoot, db, heartbeatTimeoutSec: 90, pollSec: 10);
        using var server = new WebAggServer(cfg.AggHttpPort, watcher, db, cfg.ResultsRoot, cfg.AggShareRoot, cfg.AggToken);
        var alert = new AggAlertService(watcher, db, cfg.AggWebhookUrl, cfg.AggSummaryMinutes);

        watcher.Start();
        server.Start();
        alert.Start();

        // Web 设置页保存后热生效：告警服务按新 webhook/汇总间隔重建（token 由 server 内部自更新）
        server.SettingsChanged += () =>
        {
            try
            {
                var c2 = AppConfig.Instance;
                alert.Stop();
                alert = new AggAlertService(watcher, db, c2.AggWebhookUrl, c2.AggSummaryMinutes);
                alert.Start();
                Logger.Info($"[聚合服务] Web 设置已热生效（webhook={(c2.AggWebhookUrl.Length > 0 ? "on" : "off")}, 汇总={c2.AggSummaryMinutes} 分钟）");
            }
            catch (Exception ex) { Logger.Warning($"[聚合服务] 设置热生效失败: {ex.Message}"); }
        };

        if (server.Listening)
        {
            Console.WriteLine("聚合服务已启动，浏览器访问:");
            foreach (var ip in LocalIPv4Addresses())
                Console.WriteLine($"  http://{ip}:{cfg.AggHttpPort}/");
            Console.WriteLine($"  监听端口 {cfg.AggHttpPort}，聚合库 {db.DbPath}");
            if (string.IsNullOrEmpty(cfg.AggShareRoot))
                Console.WriteLine("  警告: 未配置 agg_share_root，文件监听未启用（HTTP 推送仍可用）");
            Console.WriteLine("  按 Ctrl+C 退出");
        }
        else
        {
            Console.WriteLine("警告: 聚合服务启动失败——端口被占用或缺少监听权限（尝试以管理员身份运行）");
        }

        // Ctrl+C 优雅退出：e.Cancel 拦截默认终止，置标志让主循环退出，随后走 using Dispose
        var running = true;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            running = false;
        };
        while (running) Thread.Sleep(500);

        watcher.Stop();
        server.Stop();
        Console.WriteLine("聚合服务已退出");
        return 0;
    }

    /// <summary>
    /// 一键部署聚合服务（argus agg --install）：config 生成 / 防火墙放行 / 开机自启 / 启动服务。
    /// 产线维护者双击「一键部署聚合服务.bat」后经提权调用本命令。
    /// 已完成的步骤不回滚；防火墙失败只警告不中断（服务仍可启动，只是机台访问会受限）。
    /// ⚠ EDR(SentinelOne) 兼容：开机自启用「启动文件夹快捷方式」，不写注册表 Run/服务键。
    /// </summary>
    private static int RunAggInstall()
    {
        try
        {
            Console.WriteLine("聚合服务一键部署");
            Console.WriteLine();

            var exePath = CurrentExePath();
            var configPath = Path.Combine(AppConfig.BaseDir, "config.json");

            // ---- 步骤 1/5：生成 config.json（幂等：已存在则保留） ----
            int port;
            Console.WriteLine("步骤 1/5: 检查 config.json ...");
            if (File.Exists(configPath))
            {
                port = ReadAggHttpPort(configPath);
                Console.WriteLine($"  config.json 已存在，保留现有配置（当前 agg_http_port={port}）");
            }
            else
            {
                File.WriteAllText(configPath, BuildDefaultAggConfigJson());
                port = 8080;
                Console.WriteLine($"  已生成默认 config.json（agg_http_port={port}）");
            }

            // ---- 步骤 2/5：防火墙放行（需要管理员；失败不中断后续步骤） ----
            Console.WriteLine($"步骤 2/5: 防火墙放行 TCP {port} 入站 ...");
            try
            {
                RunProcessWait("netsh.exe", "advfirewall firewall delete rule name=\"ArgusAggWeb\"");   // 先清旧规则（不存在时失败属正常，忽略）
                int code = RunProcessWait("netsh.exe",
                    $"advfirewall firewall add rule name=\"ArgusAggWeb\" dir=in action=allow protocol=TCP localport={port}");
                if (code == 0)
                    Console.WriteLine("  防火墙规则 ArgusAggWeb 已就绪");
                else
                    Console.WriteLine($"  防火墙放行失败——请以管理员身份重新运行（右键 bat → 以管理员身份运行）(退出码 {code})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  防火墙放行失败——请以管理员身份重新运行（右键 bat → 以管理员身份运行）: {ex.Message}");
            }

            // ---- 步骤 3/5：开机自启（启动文件夹快捷方式，不写注册表——EDR 兼容） ----
            Console.WriteLine("步骤 3/5: 添加开机自启（启动文件夹快捷方式）...");
            try
            {
                var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                var lnkPath = Path.Combine(startupDir, "Argus聚合服务.lnk");
                CreateShortcut(lnkPath, exePath, "agg --web", AppConfig.BaseDir);
                Console.WriteLine(File.Exists(lnkPath)
                    ? "  已添加开机自启（启动文件夹）"
                    : "  警告: 快捷方式创建失败（文件未落盘）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  警告: 开机自启设置失败: {ex.Message}");
            }

            // ---- 步骤 4/5：启动服务（无窗口后台常驻） ----
            Console.WriteLine("步骤 4/5: 启动聚合服务 ...");
            try
            {
                using var svc = Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "agg --web",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppConfig.BaseDir,
                });
                Console.WriteLine(svc == null
                    ? "  警告: 服务进程启动失败（请手动运行 Argus.exe agg --web）"
                    : "  服务已启动");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  警告: 服务启动失败: {ex.Message}");
            }

            // ---- 步骤 5/5：打印访问地址 ----
            Console.WriteLine("步骤 5/5: 浏览器访问地址");
            var addrs = LocalIPv4Addresses();
            foreach (var ip in addrs)
                Console.WriteLine($"  http://{ip}:{port}/");
            if (addrs.Count == 0)
                Console.WriteLine($"  http://本机IP:{port}/（未枚举到 IP，请用本机实际 IP）");
            Console.WriteLine("机台端 config 配置: agg_enabled=true + agg_transport=http + agg_http_url=http://本机IP:8080/");
            Console.WriteLine();
            Console.WriteLine("部署完成。浏览器打开上面任一地址即可查看看板。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"部署失败: {ex.Message}");
            return 1;
        }
    }

    /// <summary>默认聚合端配置 JSON（agg --install 首次生成用，保持最小字段集）。</summary>
    internal static string BuildDefaultAggConfigJson()
    {
        var cfg = new Dictionary<string, object?>
        {
            ["station_id"] = "AGG",
            ["results_root"] = @"D:\Results",
            ["agg_transport"] = "http",
            ["agg_http_port"] = 8080,
            ["agg_share_root"] = "",
            ["agg_token"] = "",
            ["agg_webhook_url"] = "",
            ["agg_summary_minutes"] = 60,
            ["log_level"] = "INFO",
        };
        return JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>从现有 config.json 读 agg_http_port（读不到/非法回落默认 8080）。</summary>
    private static int ReadAggHttpPort(string configPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("agg_http_port", out var v) &&
                v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var port) && port >= 1 && port <= 65535)
                return port;
        }
        catch { }
        return 8080;
    }

    /// <summary>当前进程对应的 Argus.exe 路径（dotnet 运行 DLL 时回落程序集路径）。</summary>
    private static string CurrentExePath()
    {
        var p = Environment.ProcessPath ?? "";
        if (Path.GetFileNameWithoutExtension(p).Equals("Argus", StringComparison.OrdinalIgnoreCase))
            return p;
        var loc = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
        return loc.Length > 0 ? loc : p;
    }

    /// <summary>同步运行外部命令，返回退出码（netsh 等系统工具用）。</summary>
    private static int RunProcessWait(string fileName, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (p == null) return -1;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>
    /// 用 WScript.Shell COM(晚绑定)创建 .lnk 快捷方式（与 AutoStart 同款写法，EDR 安全：启动文件夹而非注册表）。
    /// </summary>
    private static void CreateShortcut(string lnkPath, string targetPath, string arguments, string workingDir)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) throw new InvalidOperationException("WScript.Shell 不可用");
        object? shell = Activator.CreateInstance(shellType);
        try
        {
            object? shortcut = shellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (shortcut == null) throw new InvalidOperationException("CreateShortcut 返回空");
            var st = shortcut.GetType();
            st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
            if (!string.IsNullOrEmpty(arguments))
                st.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
            st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
            st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "Argus 聚合服务（开机自启）" });
            st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shell != null && Marshal.IsComObject(shell))
                Marshal.ReleaseComObject(shell);
        }
    }

    /// <summary>枚举本机 IPv4 非回环地址（打印浏览器访问地址用）；失败返回空表。</summary>
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
  Argus.exe agg               多机台聚合看板（共享目录宿主）
  Argus.exe agg --web         聚合服务(无窗口,浏览器访问机台IP:端口)
  Argus.exe agg --install     一键部署聚合服务(生成配置/防火墙/开机自启/启动)
  Argus.exe tdms [文件.tdms]   TDMS 波形查看（窗口）
  Argus.exe tdms --help        TDMS 工具的命令行用法
  Argus.exe fetch --help       取数打包工具的命令行用法
  Argus.exe --help             本帮助

四个工具也能在主程序左侧「工具箱」里直接点开。
""");
    }
}

using System.Threading;

namespace FctAggregator;

static class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    static void Main()
    {
        // 单实例保护
        _singleInstanceMutex = new Mutex(true, @"Global\FCT_Aggregator_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("程序已在运行，请勿重复打开。", "FCT-Aggregator",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
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

        var form = new MainForm(engine);
        Application.Run(form);

        engine.Stop();
        GC.KeepAlive(_singleInstanceMutex);
    }
}

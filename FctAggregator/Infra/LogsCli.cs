namespace FctAggregator;

/// <summary>Argus.exe logs --export [path.zip] | --tail</summary>
public static class LogsCli
{
    public static int Run(string[] args)
    {
        var cfg = AppConfig.Instance;
        try { Logger.SetLevel(cfg.LogLevel); } catch { }

        if (args.Any(a => a is "-h" or "--help" or "help"))
        {
            Console.WriteLine("""
Argus.exe logs --export [path.zip]   打包 logs/app.log* + diagnostic.txt
Argus.exe logs --tail                打印当前 app.log 末尾
不带参数等价于 --export（默认写桌面）
""");
            return 0;
        }

        bool tail = args.Any(a => a.Equals("--tail", StringComparison.OrdinalIgnoreCase));
        if (tail)
        {
            Console.WriteLine(Logger.ReadTail());
            return 0;
        }

        string? dest = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--export", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) dest = args[i + 1];
            }
            else if (!args[i].StartsWith('-') && dest == null)
                dest = args[i];
        }
        dest ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Logger.SuggestedExportName());
        try
        {
            var path = Logger.ExportZip(dest);
            Console.WriteLine(path);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"导出失败: {ex.Message}");
            return 1;
        }
    }
}

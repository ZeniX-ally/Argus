using FctAggregator;

public static class IniDiag
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string path;
        if (args.Length > 0) path = args[0];
        else
        {
            var cfg = AppConfig.Load();
            path = string.IsNullOrEmpty(cfg.FctIniPath) ? @"C:\FTS\Apps\PEU\Cfg\FCT.ini" : cfg.FctIniPath;
            Console.WriteLine($"config.json 的 fct_ini_path = \"{cfg.FctIniPath}\"");
        }
        Console.WriteLine($"请求解析路径 = \"{path}\"");
        Console.WriteLine($"File.Exists  = {File.Exists(path)}");
        Console.WriteLine(new string('=', 70));

        var d = FctIni.Parse(path);

        Console.WriteLine($"Found    = {d.Found}");
        Console.WriteLine($"IniPath  = {d.IniPath}");
        Console.WriteLine($"Error    = {(string.IsNullOrEmpty(d.Error) ? "(无)" : "\n" + d.Error)}");
        Console.WriteLine(new string('-', 70));

        Console.WriteLine($"型号 Models ({d.Models.Count} 个): {string.Join(" / ", d.Models)}");
        Console.WriteLine($"固件 FwVersions ({d.FwVersions.Count} 条):");
        foreach (var (label, ver) in d.FwVersions) Console.WriteLine($"    {label} = {ver}");

        Console.WriteLine($"设备 Devices ({d.Devices.Count} 个):");
        foreach (var dev in d.Devices)
            Console.WriteLine($"    [{(dev.Online ? "在线" : "离线")}] {dev.Type,-3} {dev.Port,-12} {dev.Name}");

        Console.WriteLine($"A2L 文件 ({d.A2lFiles.Count} 个):");
        foreach (var (label, file) in d.A2lFiles) Console.WriteLine($"    {label} = {file}");

        Console.WriteLine(new string('-', 70));
        Console.WriteLine("系统 COM 口 (SerialPort.GetPortNames): " +
                          string.Join(", ", FctIni.ScanSystemComPorts().OrderBy(x => x)));
        Console.WriteLine("注册表活动 COM 口 (SERIALCOMM):        " +
                          string.Join(", ", FctIni.GetActiveComPorts().OrderBy(x => x)));

        Console.WriteLine(new string('=', 70));
        if (!d.Found) { Console.WriteLine("结论：**没读到文件**（看上面 Error 里列出的尝试路径）"); return 1; }
        if (d.Devices.Count == 0) { Console.WriteLine("结论：文件读到了，但**一个设备都没解析出来** —— 看 [Resource Name] 段"); return 2; }
        Console.WriteLine("结论：读取与解析都正常。");
        return 0;
    }
}

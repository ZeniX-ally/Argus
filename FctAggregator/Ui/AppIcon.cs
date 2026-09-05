namespace FctAggregator;

public static class AppIcon
{
    private static Icon? _cached;
    private static bool _tried;

    public const string ResourceSuffix = "app_icon.ico";

    public static Icon Load()
    {
        if (_tried) return _cached ?? SystemIcons.Application;
        _tried = true;

        try
        {
            var asm = typeof(AppIcon).Assembly;
            var name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase));
            if (name != null)
            {
                using var s = asm.GetManifestResourceStream(name);
                if (s != null) { _cached = new Icon(s); return _cached; }
            }
        }
        catch (Exception ex) { Logger.Warning($"读嵌入图标失败: {ex.Message}"); }

        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, ResourceSuffix);
            if (File.Exists(p)) { _cached = new Icon(p); return _cached; }
        }
        catch (Exception ex) { Logger.Warning($"读图标文件失败: {ex.Message}"); }

        Logger.Warning("找不到应用图标，退回系统默认图标");
        return SystemIcons.Application;
    }

    public static Icon Load(int size)
    {
        var big = Load();
        try { return new Icon(big, new Size(size, size)); }
        catch { return big; }
    }

    public static void Apply(Form form)
    {
        try { form.Icon = Load(); } catch { }
    }
}

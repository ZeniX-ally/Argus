namespace FctDistributor;

/// <summary>
/// 图标唯一取用口（分发器自己的一份）。
///
/// ⚠ <b>这是副本，不是共享引用</b>。v3.0.0 把分发器从「FCT 工具套件」里彻底拆出来后，
/// 两个程序不再共享任何源文件（用户明确要求彻底分家）。主程序那份在
/// <c>fct-aggregator-cs/AppIcon.cs</c>，命名空间是 <c>FctAggregator</c>。
///
/// 与主程序那份的差异（有意为之）：
///   · 不依赖 Logger（分发器没有文件日志设施），失败只是静默退到系统图标；
///   · 嵌入资源名前缀按本程序集匹配（<c>app_icon.ico</c> 结尾即可，不写死程序集名）。
///
/// 改动本文件时**顺手看一眼主程序那份是否也该改**。
/// </summary>
public static class AppIcon
{
    private static Icon? _cache;
    private static readonly Dictionary<int, Icon> _sized = new();

    /// <summary>取应用图标。嵌入资源 → 同目录文件 → 系统图标。永不抛异常。</summary>
    public static Icon Load()
    {
        if (_cache != null) return _cache;

        // 1) 嵌入资源（发布包里不一定有 .ico 文件，所以嵌入资源优先）
        try
        {
            var asm = typeof(AppIcon).Assembly;
            var name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith("app_icon.ico", StringComparison.OrdinalIgnoreCase));
            if (name != null)
            {
                using var s = asm.GetManifestResourceStream(name);
                if (s != null) return _cache = new Icon(s);
            }
        }
        catch { }

        // 2) 程序目录下的文件
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, "app_icon.ico");
            if (File.Exists(p)) return _cache = new Icon(p);
        }
        catch { }

        // 3) 系统图标兜底
        return _cache = SystemIcons.Application;
    }

    /// <summary>按尺寸取（托盘要 16×16，否则系统拿大图缩、发虚）。</summary>
    public static Icon Load(int size)
    {
        if (_sized.TryGetValue(size, out var got)) return got;
        try { return _sized[size] = new Icon(Load(), size, size); }
        catch { return Load(); }
    }

    /// <summary>给窗体上图标。</summary>
    public static void Apply(Form form)
    {
        try { form.Icon = Load(); } catch { }
    }
}

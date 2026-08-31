namespace FctAggregator;

/// <summary>
/// 应用图标的**唯一**取用口（v2.9.4）。
///
/// 之前六处各写一遍加载逻辑，还分两种口径：
///   · 主窗口 / 桌面提示 / 数据分发 / FAIL 排行 → 读 <c>BaseDir\app_icon.ico</c> 文件
///   · 取数打包 / TDMS → 读嵌入资源
/// 问题是**打包脚本从来没把 app_icon.ico 放进发布包**，所以现场那四处全都读不到：
/// 窗口退回 exe 内嵌图标，托盘更惨 —— 退回 <see cref="SystemIcons.Application"/>（灰色通用图标）。
///
/// 现在统一成：**嵌入资源优先**（编译进 dll，永远存在）→ 退文件 → 退系统图标。
/// 顺带做了缓存，避免每个窗口都反序列化一遍 160KB 的多尺寸 ico。
/// </summary>
public static class AppIcon
{
    private static Icon? _cached;
    private static bool _tried;

    /// <summary>资源名以此结尾即认为是应用图标（与各工具原有的匹配口径一致）。</summary>
    public const string ResourceSuffix = "app_icon.ico";

    /// <summary>应用图标；取不到时返回系统默认图标（永不为 null、永不抛）。</summary>
    public static Icon Load()
    {
        if (_tried) return _cached ?? SystemIcons.Application;
        _tried = true;

        // ① 嵌入资源（首选：跟 dll 走，发布包漏文件也不影响）
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

        // ② 同目录文件（兼容手工替换图标的场景）
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, ResourceSuffix);
            if (File.Exists(p)) { _cached = new Icon(p); return _cached; }
        }
        catch (Exception ex) { Logger.Warning($"读图标文件失败: {ex.Message}"); }

        Logger.Warning("找不到应用图标，退回系统默认图标");
        return SystemIcons.Application;
    }

    /// <summary>
    /// 取指定尺寸的图标（托盘要 16×16，多尺寸 ico 交给系统挑往往会挑大的再缩，发虚）。
    /// </summary>
    public static Icon Load(int size)
    {
        var big = Load();
        try { return new Icon(big, new Size(size, size)); }
        catch { return big; }
    }

    /// <summary>给窗体上图标；失败静默（图标从来不该让程序崩）。</summary>
    public static void Apply(Form form)
    {
        try { form.Icon = Load(); } catch { }
    }
}

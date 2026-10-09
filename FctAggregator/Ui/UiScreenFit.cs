namespace FctAggregator;

/// <summary>
/// 屏幕尺寸适配纯函数（DPI 缩放感知）——本工程唯一入口，别在窗体里另算一套。
///
/// <para>背景（v3.57.4）：<c>MainForm</c> / <c>XmlViewerForm</c> 用 <c>Screen.*.WorkingArea</c>（**设备像素**）
/// 直接限制 <c>Width</c> / <c>MinimumSize</c>，但本工程所有窗体都走 <see cref="Theme.ApplyDpi"/>
/// （<c>AutoScaleMode.Dpi</c> + 96dpi 基准）。WinForms 的 <c>ContainerControl.PerformAutoScale</c>
/// 在 OnLayout 里按 <c>AutoScaleFactor</c>（= 当前 DPI / 96）再乘一次这几个尺寸，于是：</para>
/// <list type="bullet">
///   <item>150% 缩放 + 1920 宽屏：Width 算成 1408，实际落地 2112 设备像素 —— 窗口伸出屏幕右缘；</item>
///   <item>150% 缩放 + 1024×768 机台：MinimumSize 被撑到 1536×1080，工作区只有 1024×728 —— 右侧控件直接看不到。</item>
/// </list>
/// <para>这正是 <c>FctAggregator/更新日志.md</c> 里 v3.35.x 修过的老问题（当时只按 100% 缩放修，
/// 非 100% 缩放的机台上从未真正生效）。</para>
///
/// <para>修法：先用本类把工作区折算回「AutoScale 之前的逻辑像素」，再照原样取 <c>Math.Min</c>——
/// 缩放之后即恰好等于设备像素预算。100% 缩放（系数 ≤ 1）时逐位不变，老机台零视觉改动。</para>
/// </summary>
internal static class UiScreenFit
{
    /// <summary>设计基准 DPI，与 <see cref="Theme.ApplyDpi"/> 里写入的 96,96 必须一致。</summary>
    public const float BaseDpi = 96f;

    /// <summary>
    /// 设备像素 → AutoScale 之前的逻辑值（向下取整，宁小勿大：小一点只会让窗口更保守地放得下）。
    /// 系数非法或 ≤ 100% 时原样返回，保证 100% 缩放行为与修复前逐位一致。
    /// </summary>
    public static int ToLogical(int devicePx, float scale)
    {
        if (devicePx <= 0) return 0;
        if (!float.IsFinite(scale) || scale <= 1f) return devicePx;
        return (int)MathF.Floor(devicePx / scale);
    }

    /// <summary>工作区（设备像素）→ AutoScale 之前的逻辑工作区。</summary>
    public static Size LogicalWorkArea(Size workAreaPx, float scale)
        => new(ToLogical(workAreaPx.Width, scale), ToLogical(workAreaPx.Height, scale));

    /// <summary>
    /// 该窗体即将被 AutoScale 乘上的系数（= CurrentAutoScaleDimensions / AutoScaleDimensions）。
    /// 刻意取 WinForms 自己用的那个比值，而不是另找 DPI 来源——两套口径必然会对不上。
    /// </summary>
    public static float AutoScaleFactor(ContainerControl? owner)
    {
        try
        {
            if (owner is null) return 1f;
            var basis = owner.AutoScaleDimensions;
            var current = owner.CurrentAutoScaleDimensions;
            if (basis.Width <= 0f || basis.Height <= 0f) return 1f;
            float s = current.Width / basis.Width;
            return float.IsFinite(s) && s > 0f ? s : 1f;
        }
        catch { return 1f; }
    }

    /// <summary>
    /// 现场 UI 兼容排查用的一行事实。Windows 10 与 11 的差异大多落在「缩放比例 / 会话类型 /
    /// 字体族实际解析结果 / 工作区与窗口谁大」这几项上，出问题时这一行就能定性，不必让现场猜。
    ///
    /// 为什么连字体也算进来：本工程字体族写死 "Microsoft YaHei UI"，若目标机没有该族，
    /// GDI+ **静默回退**到别的字体，度量随之全变——而大量标签与自绘矩形是按原字体写死宽高的，
    /// 于是表现为「文字被截断 / 控件重叠」。打印实际族名与像素高即可一眼看出是否回退。
    /// </summary>
    public static string Describe(Size workAreaPx, float scale, Size window, Size minSize, int deviceDpi)
    {
        string os;
        try
        {
            int build = Environment.OSVersion.Version.Build;
            os = build >= 22000 ? "Win11" : "Win10";
            os += $"/{build}";
        }
        catch { os = "未知"; }

        string mode;
        try { mode = Application.HighDpiMode.ToString(); }
        catch { mode = "未知"; }

        int screens;
        try { screens = Screen.AllScreens.Length; }
        catch { screens = 0; }

        // 会话类型：WS_EX_COMPOSITED（BufferedTabControl）在远程/终端服务会话下是最经典的翻车场景
        string session;
        try { session = SystemInformation.TerminalServerSession ? "远程(终端服务)" : "本地"; }
        catch { session = "未知"; }

        // 字体实际解析结果：回退时族名会变、像素高会变，这是 Win10 与 Win11 最容易分叉的一处
        string fonts;
        try { fonts = $"{Theme.Body.FontFamily.Name} Body={Theme.Body.Height}px Small={Theme.Small.Height}px"; }
        catch { fonts = "未知"; }

        var logical = LogicalWorkArea(workAreaPx, scale);
        return $"[UI] OS={os} 高DPI模式={mode} 会话={session} 显示器={screens} 缩放={MathF.Round(scale * 100f)}%"
             + $" DeviceDpi={deviceDpi} 主工作区={workAreaPx.Width}x{workAreaPx.Height}(设备像素)"
             + $" 折算后={logical.Width}x{logical.Height}"
             + $" 窗口={window.Width}x{window.Height} 最小={minSize.Width}x{minSize.Height} 字体={fonts}";
    }

    /// <summary>
    /// UI 异常现场：出问题的那一刻，是哪个窗体/哪一页、窗口多大、在哪个屏、什么 DPI。
    /// 现场只报「有时候 UI 错误」时，没有这一行就只能靠猜（C 类渲染问题尤其如此）。
    /// </summary>
    public static string DescribeActiveForm()
    {
        try
        {
            Form? f = null;
            foreach (Form candidate in Application.OpenForms)
                if (candidate.Visible) f = candidate;   // 取最后一个可见窗体（栈顶/主窗）
            if (f is null) return "无可见窗体";

            string page = f is MainForm mf ? $" 页={mf.CurrentPageIndex}" : "";
            var scr = Screen.FromControl(f);
            int pct = f.DeviceDpi > 0 ? f.DeviceDpi * 100 / 96 : 100;
            return $"{f.GetType().Name} {f.Width}x{f.Height}{page} DeviceDpi={f.DeviceDpi} 缩放={pct}%"
                 + $" 屏={scr.DeviceName} {scr.Bounds.Width}x{scr.Bounds.Height} 远端会话={SystemInformation.TerminalServerSession}";
        }
        catch (Exception ex) { return $"上下文采集失败: {ex.Message}"; }
    }
}

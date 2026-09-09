namespace FctAggregator;

/// <summary>WinForms UI 异步刷新与防抖小工具。</summary>
internal static class UiAsync
{
    public static System.Windows.Forms.Timer Debounce(Control owner, int ms, Action action)
    {
        var t = new System.Windows.Forms.Timer { Interval = Math.Max(50, ms) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (!owner.IsDisposed) action();
        };
        return t;
    }

    public static void RunGridUpdate(DataGridView grid, Action fill)
    {
        grid.SuspendLayout();
        try { fill(); }
        finally { grid.ResumeLayout(); }
    }

    public static void Post(Control owner, Action action)
    {
        if (owner.IsDisposed) return;
        if (owner.IsHandleCreated) owner.BeginInvoke(action);
        else action();
    }
}

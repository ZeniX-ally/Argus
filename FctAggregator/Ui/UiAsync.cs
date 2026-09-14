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
        // 调用方通常只持有引用、不负责释放：随宿主控件一起销毁，避免 Timer 在控件销毁后继续持有闭包
        owner.Disposed += (_, _) =>
        {
            try { t.Stop(); t.Dispose(); } catch { }
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
        void Queue()
        {
            if (owner.IsDisposed || !owner.IsHandleCreated) return;
            try { owner.BeginInvoke(action); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }
        if (owner.IsHandleCreated) { Queue(); return; }
        void OnCreated(object? s, EventArgs e)
        {
            owner.HandleCreated -= OnCreated;
            Queue();
        }
        owner.HandleCreated += OnCreated;
        if (owner.IsHandleCreated)
        {
            owner.HandleCreated -= OnCreated;
            Queue();
        }
    }
}

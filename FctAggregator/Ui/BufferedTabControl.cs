namespace FctAggregator;

/// <summary>
/// 主窗切页用 TabControl：仅本控件开 WS_EX_COMPOSITED，减轻切页闪白。
/// 不套整窗——和 DataGridView 叠在一起会拖慢滚动。
/// </summary>
internal sealed class BufferedTabControl : TabControl
{
    private const int WsExComposited = 0x02000000;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExComposited;
            return cp;
        }
    }
}

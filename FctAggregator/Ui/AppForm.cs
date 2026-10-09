namespace FctAggregator;

/// <summary>
/// 全项目窗体基类：统一 DPI 基准（UI-4）与无障碍角色（UI-9）。
/// 用基类而不是每个窗体各写两行，是为了能被自检用反射兜住——将来新增窗体漏设 AutoScaleMode 会被断言直接打回。
/// </summary>
public class AppForm : Form
{
    protected AppForm()
    {
        Theme.ApplyDpi(this);
        if (AccessibleRole == AccessibleRole.Default) AccessibleRole = AccessibleRole.Dialog;
    }
}

using System.Drawing.Drawing2D;

namespace FctAggregator;

/// <summary>
/// 卡片详情预览（**单击卡片**弹出）。
///
/// 看板卡片就那么大，长故障项名、故障描述、维修措施、以及**被合并的 fail 项清单**
/// 在卡上必然显示不全（只能靠 tooltip，且不能选中复制）。
/// 这个轻量弹窗把完整内容摊开：
///   · 上半部分 = 键值明细（只读 TextBox，**可选中复制**测试项名）
///   · 下半部分 = 「合并的 fail 项」列表，带各自的真实不良次数
///
/// 行为像个 popup：失焦 / Esc / 打开下一个预览 / 看板刷新 都会自动关掉，
/// 全局只保留一个实例，不会糊满屏。
/// </summary>
public class CardPreviewForm : Form
{
    private static CardPreviewForm? _open;

    /// <summary>关掉当前预览（看板 Reload、切页时调用，避免留着过期内容）。</summary>
    public static void CloseCurrent()
    {
        try { _open?.Close(); } catch { }
        _open = null;
    }

    /// <summary>
    /// 在 <paramref name="anchor"/> 旁边弹出预览。
    /// </summary>
    /// <param name="rows">键值明细（键会右对齐补齐）。</param>
    /// <param name="mergedItems">合并的 fail 项 → 次数（null / 空则不显示该区）。</param>
    public static void ShowFor(Control anchor, string title, string subtitle, Color accent,
                               IEnumerable<(string k, string v)> rows,
                               IEnumerable<(string item, int count)>? mergedItems,
                               string footer)
    {
        CloseCurrent();
        var f = new CardPreviewForm(title, subtitle, accent, rows, mergedItems, footer);
        _open = f;
        f.FormClosed += (_, _) => { if (ReferenceEquals(_open, f)) _open = null; };
        f.PositionNear(anchor);
        f.Show(anchor.FindForm());
        f.Activate();
    }

    private readonly Color _accent;
    private readonly TextBox _detail;

    private CardPreviewForm(string title, string subtitle, Color accent,
                            IEnumerable<(string k, string v)> rows,
                            IEnumerable<(string item, int count)>? mergedItems,
                            string footer)
    {
        _accent = accent;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        MinimizeBox = false; MaximizeBox = false;
        Width = 470;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Theme.Surface;
        KeyPreview = true;
        Padding = new Padding(1);

        // ===== 标题条 =====
        var head = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = accent };
        var titleLabel = new Label
        {
            Dock = DockStyle.Fill, Text = title, ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold),
            Padding = new Padding(12, 6, 30, 0), AutoEllipsis = true,
        };
        var subLabel = new Label
        {
            Dock = DockStyle.Bottom, Height = 18, Text = subtitle,
            ForeColor = Color.FromArgb(203, 213, 225),
            Padding = new Padding(12, 0, 12, 2), AutoEllipsis = true,
            Font = new Font("Microsoft YaHei UI", 8.5F),
        };
        var close = new Label
        {
            Text = "✕", Width = 26, Height = 24, Dock = DockStyle.Right,
            ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand, Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        };
        close.Click += (_, _) => Close();
        head.Controls.Add(titleLabel);
        head.Controls.Add(subLabel);
        head.Controls.Add(close);

        // ===== 明细（可选中复制）=====
        // ⚠ TabStop=false：WinForms 的 TextBox 一旦因“窗口激活/Tab”拿到焦点就会**自动全选**，
        // popup 一弹出文字全蓝了，看着就像“单击就选中了文字”。
        // 靠鼠标点进去的焦点不会全选（只放光标），所以手动划选复制不受影响。
        var detail = new TextBox
        {
            Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface, Dock = DockStyle.Top, ScrollBars = ScrollBars.Vertical,
            Font = new Font("Microsoft YaHei UI", 9F), TabStop = false,
            Text = string.Join(Environment.NewLine, rows.Select(r => $"{r.k}  {r.v}")),
        };
        _detail = detail;
        detail.GotFocus += (_, _) => ClearAutoSelection();     // 任何途径拿到焦点都不开局全选
        var lineCount = Math.Max(1, detail.Text.Split('\n').Length);
        detail.Height = Math.Min(300, 6 + lineCount * 19);

        var detailHost = new Panel { Dock = DockStyle.Top, Padding = new Padding(12, 8, 12, 6) };
        detailHost.Height = detail.Height + 14;
        detailHost.Controls.Add(detail);

        Controls.Add(detailHost);
        Controls.Add(head);

        int total = head.Height + detailHost.Height;

        // ===== 合并的 fail 项 =====
        var merged = mergedItems?.ToList() ?? new List<(string item, int count)>();
        if (merged.Count > 0)
        {
            var mergedTitle = new Label
            {
                Dock = DockStyle.Top, Height = 24, Padding = new Padding(12, 2, 12, 0),
                Text = merged.Count > 1 ? $"合并的 fail 项（{merged.Count} 项）" : "来源 fail 项",
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(203, 213, 225),
            };
            var list = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
                GridLines = false, HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BorderStyle = BorderStyle.FixedSingle, Font = new Font("Microsoft YaHei UI", 8.5F),
                TabStop = false,                       // 同理：不抢焦点、不自动高亮首行
            };
            list.Columns.Add("次数", 52, HorizontalAlignment.Center);
            list.Columns.Add("原始测试项", 372);
            foreach (var (item, count) in merged.OrderByDescending(x => x.count))
            {
                var it = new ListViewItem(count > 0 ? count.ToString() : "—");
                it.SubItems.Add(item);
                list.Items.Add(it);
            }
            var mergedHost = new Panel { Dock = DockStyle.Top, Padding = new Padding(12, 0, 12, 6) };
            mergedHost.Height = Math.Min(190, 8 + merged.Count * 19 + 24);
            mergedHost.Controls.Add(list);

            Controls.Add(mergedHost);
            Controls.Add(mergedTitle);
            mergedTitle.BringToFront();
            mergedHost.BringToFront();
            detailHost.BringToFront();
            head.BringToFront();
            total += mergedTitle.Height + mergedHost.Height;
        }

        // ===== 底部提示 =====
        var foot = new Label
        {
            Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(12, 4, 12, 4),
            Text = footer, ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Microsoft YaHei UI", 8.5F),
            BackColor = Theme.Bg, AutoEllipsis = true,
        };
        Controls.Add(foot);
        total += foot.Height + 2;

        Height = Math.Min(620, total);

        Deactivate += (_, _) => Close();
        Activated += (_, _) => ClearAutoSelection();       // 激活时 WinForms 可能又把焦点塞给输入框
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    /// <summary>自检用：明细区当前选中了多少字符（刚弹出时必须是 0）。</summary>
    internal int DetailSelectionLength => _detail.SelectionLength;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ClearAutoSelection();
    }

    /// <summary>清掉“因激活而全选”的选区，并把焦点从输入控件上拿走。</summary>
    private void ClearAutoSelection()
    {
        try
        {
            _detail.SelectionStart = 0;
            _detail.SelectionLength = 0;
            if (ActiveControl != null) ActiveControl = null;   // 焦点给窗口本身，Esc 仍然能关
        }
        catch { }
    }

    /// <summary>贴着卡片右侧弹出；越界就翻到左侧 / 贴边，保证完整可见。</summary>
    private void PositionNear(Control anchor)
    {
        var screen = Screen.FromControl(anchor).WorkingArea;
        var at = anchor.PointToScreen(new Point(anchor.Width + 8, -4));
        if (at.X + Width > screen.Right) at.X = anchor.PointToScreen(Point.Empty).X - Width - 8;
        if (at.X < screen.Left) at.X = screen.Left + 4;
        if (at.Y + Height > screen.Bottom) at.Y = screen.Bottom - Height - 4;
        if (at.Y < screen.Top) at.Y = screen.Top + 4;
        Location = at;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // 无边框窗口自己画一圈边，否则白底会和看板糊在一起
        using var pen = new Pen(_accent);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

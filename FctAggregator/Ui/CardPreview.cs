using System.Drawing.Drawing2D;

namespace FctAggregator;

public class CardPreviewForm : Form
{
    private static CardPreviewForm? _open;

    public static void CloseCurrent()
    {
        try { _open?.Close(); } catch { }
        _open = null;
    }

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
            ForeColor = Color.FromArgb(242, 242, 242),
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

        var detail = new TextBox
        {
            Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface, Dock = DockStyle.Top, ScrollBars = ScrollBars.Vertical,
            Font = new Font("Microsoft YaHei UI", 9F), TabStop = false,
            Text = string.Join(Environment.NewLine, rows.Select(r => $"{r.k}  {r.v}")),
        };
        _detail = detail;
        detail.GotFocus += (_, _) => ClearAutoSelection();
        var lineCount = Math.Max(1, detail.Text.Split('\n').Length);
        detail.Height = Math.Min(300, 6 + lineCount * 19);

        var detailHost = new Panel { Dock = DockStyle.Top, Padding = new Padding(12, 8, 12, 6) };
        detailHost.Height = detail.Height + 14;
        detailHost.Controls.Add(detail);

        Controls.Add(detailHost);
        Controls.Add(head);

        int total = head.Height + detailHost.Height;

        var merged = mergedItems?.ToList() ?? new List<(string item, int count)>();
        if (merged.Count > 0)
        {
            var mergedTitle = new Label
            {
                Dock = DockStyle.Top, Height = 24, Padding = new Padding(12, 2, 12, 0),
                Text = merged.Count > 1 ? $"合并的 fail 项（{merged.Count} 项）" : "来源 fail 项",
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                ForeColor = Theme.TextSub,
            };
            var list = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
                GridLines = false, HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BorderStyle = BorderStyle.FixedSingle, Font = new Font("Microsoft YaHei UI", 8.5F),
                TabStop = false,
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

        var foot = new Label
        {
            Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(12, 4, 12, 4),
            Text = footer, ForeColor = Color.FromArgb(140, 140, 140),
            Font = new Font("Microsoft YaHei UI", 8.5F),
            BackColor = Theme.Bg, AutoEllipsis = true,
        };
        Controls.Add(foot);
        total += foot.Height + 2;

        Height = Math.Min(620, total);

        Deactivate += (_, _) => Close();
        Activated += (_, _) => ClearAutoSelection();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    internal int DetailSelectionLength => _detail.SelectionLength;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ClearAutoSelection();
    }

    private void ClearAutoSelection()
    {
        try
        {
            _detail.SelectionStart = 0;
            _detail.SelectionLength = 0;
            if (ActiveControl != null) ActiveControl = null;
        }
        catch { }
    }

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
        using var pen = new Pen(_accent);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

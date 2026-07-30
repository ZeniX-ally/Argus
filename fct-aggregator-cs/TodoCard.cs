using System.Drawing.Drawing2D;

namespace FctAggregator;

/// <summary>
/// 「待办」列里的**未确认不良**卡片。
///
/// v2.5.0 起待办不再是独立页面，而是直接长在看板的「待办」列里：
///   · 数据源 = test_records 实时聚合（<see cref="Database.ListPendingTodos"/>），不是 maintenance_records
///   · 双击 = 确认问题（建维修记录，留在待办列）
///   · 拖到其它列 = 确认问题并直接置为该列状态
///   · 右键 = 确认 / 忽略（忽略后记入 dismissed_todos，不再出现）
///
/// 视觉上与维修记录卡刻意不同：暖色底 + 虚线框 + 左侧红条，表示“还没人认领”。
/// </summary>
public sealed class TodoCard : BoardCardBase
{
    public TodoItem Item { get; }

    /// <summary>待办卡片永远属于「待办」列。</summary>
    public override string ColumnKey => MaintenanceMeta.DefaultStatus;

    private static readonly Font TitleFont = new("Microsoft YaHei UI", 9F, FontStyle.Bold);
    private static readonly Font BodyFont = new("Microsoft YaHei UI", 8.25F);
    private static readonly Font MetaFont = new("Microsoft YaHei UI", 8F);
    private static readonly Font TagFont = new("Microsoft YaHei UI", 7.5F, FontStyle.Bold);

    private static readonly Color Accent = Color.FromArgb(197, 48, 48);

    public TodoCard(TodoItem item)
    {
        Item = item;
        Tip.SetToolTip(this, BuildTooltip());
    }

    private string BuildTooltip()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"【未确认不良】{Item.FailItem}");
        sb.AppendLine($"出现次数: {Item.OccurrenceCount}");
        sb.AppendLine($"首次出现: {(string.IsNullOrEmpty(Item.FirstSeen) ? "—" : Item.FirstSeen)}");
        sb.AppendLine($"最近出现: {(string.IsNullOrEmpty(Item.LastSeen) ? "—" : Item.LastSeen)}");
        if (!string.IsNullOrWhiteSpace(Item.Model)) sb.AppendLine($"型号: {Item.Model}");
        if (!string.IsNullOrWhiteSpace(Item.StationId)) sb.AppendLine($"机台: {Item.StationId}");
        sb.AppendLine();
        sb.Append("双击=确认问题(建维修记录) · 拖到其它列=确认并置为该状态 · 右键=忽略");
        return sb.ToString();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);

        using (var path = MaintenanceDraw.Rounded(r, 5))
        {
            using (var bg = new SolidBrush(Hover ? Color.FromArgb(255, 249, 245) : Color.FromArgb(255, 245, 240)))
                g.FillPath(bg, path);
            using var pen = new Pen(Hover ? Accent : Color.FromArgb(236, 176, 160), Hover ? 1.4f : 1f)
            { DashStyle = Hover ? DashStyle.Solid : DashStyle.Dash };
            g.DrawPath(pen, path);
        }

        // 左侧红色色条 = 未确认
        using (var bar = new SolidBrush(Accent))
            g.FillRectangle(bar, new Rectangle(1, 4, 4, Height - 9));

        const int left = 12;
        const int tagW = 54;

        // 第 1 行: 故障项目 + 右上角「未确认」标签
        TextRenderer.DrawText(g, Item.FailItem, TitleFont,
            new Rectangle(left, 7, Math.Max(20, Width - left - tagW - 10), 18),
            Color.FromArgb(32, 40, 52), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        var tag = new Rectangle(Width - tagW - 6, 8, tagW, 16);
        using (var tp = MaintenanceDraw.Rounded(tag, 8))
        using (var tb = new SolidBrush(Accent))
            g.FillPath(tb, tp);
        TextRenderer.DrawText(g, "未确认", TagFont, tag, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        // 第 2 行: 出现次数 · 最近出现
        TextRenderer.DrawText(g, $"出现 {Item.OccurrenceCount} 次 · 最近 {ShortTime(Item.LastSeen)}", BodyFont,
            new Rectangle(left, 29, Math.Max(20, Width - left - 8), 16),
            Color.FromArgb(150, 70, 60), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // 第 3 行: 型号 · 机台 + 操作提示
        var model = string.IsNullOrWhiteSpace(Item.Model) ? "—" : Item.Model;
        var station = string.IsNullOrWhiteSpace(Item.StationId) ? "—" : Item.StationId;
        TextRenderer.DrawText(g, $"{model} · {station}", MetaFont,
            new Rectangle(left, 50, Math.Max(20, Width - left - 96), 16),
            Color.FromArgb(150, 130, 128), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, "双击确认", MetaFont,
            new Rectangle(Width - 92, 50, 86, 16),
            Color.FromArgb(180, 120, 110), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }
}

// ==================================================================
//  确认问题补填框（轻量版，只填维修人 + 严重度）
// ==================================================================
/// <summary>
/// 从待办确认问题时弹出，补填维修人与严重度。
/// 确定 → 创建一条维修记录，进入看板工作流。
/// </summary>
public class TodoConfirmForm : Form
{
    public string Resolver => ResolverUtil.Normalize(_resolver.Text);
    public string Severity => MaintenanceMeta.SeverityKeyOf(_severity.SelectedItem?.ToString());

    private readonly ComboBox _resolver;
    private readonly ComboBox _severity;

    public TodoConfirmForm(TodoItem todo, Database? db = null, string? targetStatusZh = null)
    {
        Text = $"确认问题 — {todo.FailItem}";
        Width = 460; Height = 260;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9F);

        var head = new Label
        {
            Dock = DockStyle.Top, Height = 52, Padding = new Padding(14, 8, 14, 0),
            Text = $"故障项：{todo.FailItem}\n已出现 {todo.OccurrenceCount} 次，最近 {ShortTime(todo.LastSeen)}",
            Font = new Font("Microsoft YaHei UI", 9.5F),
            ForeColor = Color.FromArgb(60, 70, 88),
            AutoEllipsis = true,
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(14, 6, 14, 6),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        _resolver = new ComboBox
        {
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown,
            Margin = new Padding(3, 5, 3, 5),
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems,
        };
        if (db != null)
        {
            try
            {
                var pool = db.ListResolvers();
                if (pool.Count > 0) _resolver.Items.AddRange(pool.Cast<object>().ToArray());
            }
            catch { }
        }
        _severity = new ComboBox
        {
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(3, 5, 3, 5),
        };
        _severity.Items.AddRange(MaintenanceMeta.SeverityOrderZh.Cast<object>().ToArray());
        _severity.SelectedIndex = 0;

        layout.Controls.Add(MakeLabel("维修人员:"), 0, 0);
        layout.Controls.Add(_resolver, 1, 0);
        layout.Controls.Add(MakeLabel("严重程度:"), 0, 1);
        layout.Controls.Add(_severity, 1, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 8, 10, 8),
        };
        var ok = new Button { Text = "确认，建维修记录", Width = 148, Height = 28, DialogResult = DialogResult.None };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_resolver.Text))
            {
                MessageBox.Show("请填写或选择维修人员。", "提示");
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        var cancel = new Button { Text = "取消", Width = 80, Height = 28, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange(new Control[] { ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;

        var note = new Label
        {
            Dock = DockStyle.Bottom, Height = 20,
            Padding = new Padding(14, 0, 14, 0),
            ForeColor = Color.FromArgb(140, 150, 168),
            Text = string.IsNullOrEmpty(targetStatusZh)
                 ? "确定后自动创建「待办」维修记录，可继续拖动处理。"
                 : $"确定后自动创建维修记录并置为「{targetStatusZh}」。",
        };

        Controls.Add(layout);
        Controls.Add(note);
        Controls.Add(buttons);
        Controls.Add(head);
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private static string ShortTime(string? ts)
    {
        if (string.IsNullOrWhiteSpace(ts)) return "—";
        if (!DateTime.TryParse(ts, out var dt)) return ts.Length > 16 ? ts[..16] : ts;
        return dt.ToString("MM-dd HH:mm");
    }
}

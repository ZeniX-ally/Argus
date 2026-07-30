using System.Drawing.Drawing2D;

namespace FctAggregator;

/// <summary>
/// 「待办」列里的**未确认不良**卡片（v2.6.0 起一张卡 = 一个**合并后的大项**）。
///
/// 数据来自 `todo_items` 登记表（<see cref="Database.ListTodoView"/>）：
///   · **只扫近一个月**的不良入库（`todo_scan_days`），避免一次灌进几千张卡
///   · **同类测试项自动合并**（`6.1.1.1 5V_Rail` / `6.1.1.2 5V_Rail` / `..._CH1/CH2` → 一张卡）
///   · **一旦登记就永久保留**：哪怕不良早已滑出扫描窗口，卡片仍在，直到被处理完
///   · **按 fail 次数排序**并标注优先级（高/中/低），列顶就是最该先修的
///
/// 交互：
///   · 双击 = 确认问题（建维修记录，留在「待办」列）
///   · 拖到其它列 = 确认问题并直接置为该列状态
///   · 右键 = 确认 / 确认并置为某状态（**没有忽略、没有删除**）
///
/// v2.5.1 起策略：待办是真实 FAIL 数据的投影，不可忽略 / 不可删除，
/// 唯一出口是确认 → 处理 → 拖到「已完成」。
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
        // 高度随内容变：合并了多项时多留两行列出原始测试项（卡上也要看得到合并了什么）
        Height = 88 + VariantLineCount * 16;
        Tip.SetToolTip(this, BuildTooltip());
    }

    /// <summary>卡上最多列几行原始项（0/1/2）。</summary>
    private int VariantLineCount =>
        Item.VariantCount <= 1 ? 0 : Math.Min(2, Item.Variants.Count);

    private string BuildTooltip()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"【未确认不良】{Item.Title}");
        sb.AppendLine($"优先级: {Item.PriorityZh}（按 fail 次数）");
        sb.AppendLine($"区间内: {Item.RangeCount} 次   累计: {Item.TotalCount} 次");
        if (Item.VariantCount > 1)
        {
            sb.AppendLine($"已合并 {Item.VariantCount} 个同类测试项：");
            foreach (var v in Item.Variants.Take(10)) sb.AppendLine($"   · {v}");
            if (Item.Variants.Count > 10) sb.AppendLine($"   …另有 {Item.Variants.Count - 10} 项");
        }
        sb.AppendLine($"首次出现: {(string.IsNullOrEmpty(Item.FirstSeen) ? "—" : Item.FirstSeen)}");
        sb.AppendLine($"最近出现: {(string.IsNullOrEmpty(Item.LastSeen) ? "—" : Item.LastSeen)}");
        if (!string.IsNullOrWhiteSpace(Item.Model)) sb.AppendLine($"型号: {Item.Model}");
        if (!string.IsNullOrWhiteSpace(Item.StationId)) sb.AppendLine($"机台: {Item.StationId}");
        sb.AppendLine();
        sb.AppendLine("单击=预览详情(含合并清单、可复制) · 双击=确认问题 · 拖到其它列=确认并置为该状态");
        sb.Append("⚠ 来自真实不良，不可忽略/删除；处理完拖到「已完成」它就不在了");
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

        // 左侧色条 = 优先级（高红 / 中橙 / 低灰）
        var prioColor = TodoGrouping.PriorityColorOf(Item.SortCount);
        using (var bar = new SolidBrush(prioColor))
            g.FillRectangle(bar, new Rectangle(1, 4, 4, Height - 9));

        const int left = 12;

        // ---- 第 1 行: 大项名 + 右上角「未确认」 ----
        const int tagW = 54;
        TextRenderer.DrawText(g, Item.Title, TitleFont,
            new Rectangle(left, 6, Math.Max(20, Width - left - tagW - 10), 18),
            Color.FromArgb(32, 40, 52), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        var tag = new Rectangle(Width - tagW - 6, 7, tagW, 16);
        using (var tp = MaintenanceDraw.Rounded(tag, 8))
        using (var tb = new SolidBrush(Accent))
            g.FillPath(tb, tp);
        TextRenderer.DrawText(g, "未确认", TagFont, tag, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        // ---- 第 2 行: 优先级徽章 + 次数 + 合并数 ----
        var prio = $"优先级 {Item.PriorityZh}";
        var pw = TextRenderer.MeasureText(prio, TagFont).Width + 12;
        var pr = new Rectangle(left, 28, pw, 16);
        using (var pp = MaintenanceDraw.Rounded(pr, 8))
        using (var pb = new SolidBrush(prioColor))
            g.FillPath(pb, pp);
        TextRenderer.DrawText(g, prio, TagFont, pr, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        var counts = Item.RangeCount == Item.TotalCount
            ? $"{Item.TotalCount} 次不良"
            : $"区间 {Item.RangeCount} 次 · 累计 {Item.TotalCount} 次";
        TextRenderer.DrawText(g, counts, BodyFont,
            new Rectangle(pr.Right + 6, 28, Math.Max(20, Width - pr.Right - 14), 16),
            Color.FromArgb(150, 70, 60), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // ---- 第 3 行: 合并信息 / 型号 · 机台 ----
        var merged = Item.VariantCount > 1 ? $"已合并 {Item.VariantCount} 个同类项 · " : "";
        var model = string.IsNullOrWhiteSpace(Item.Model) ? "—" : Item.Model;
        var station = string.IsNullOrWhiteSpace(Item.StationId) ? "—" : Item.StationId;
        TextRenderer.DrawText(g, $"{merged}{model} · {station}", MetaFont,
            new Rectangle(left, 48, Math.Max(20, Width - left - 8), 16),
            Color.FromArgb(140, 120, 118), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // ---- 合并的 fail 项（卡上直接列前两项，剩下的用 +N 提示，完整清单看单击预览）----
        int y = 64;
        int lines = VariantLineCount;
        for (int i = 0; i < lines; i++)
        {
            var text = "· " + Item.Variants[i];
            // 最后一行带上“还有几项”
            if (i == lines - 1 && Item.VariantCount > lines)
                text += $"   +{Item.VariantCount - lines} 项";
            TextRenderer.DrawText(g, text, MetaFont,
                new Rectangle(left + 2, y, Math.Max(20, Width - left - 10), 16),
                Color.FromArgb(120, 96, 96), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            y += 16;
        }

        // ---- 最后一行: 最近出现 + 操作提示 ----
        TextRenderer.DrawText(g, $"最近 {ShortTime(Item.LastSeen)}", MetaFont,
            new Rectangle(left, y + 2, Math.Max(20, Width - left - 96), 16),
            Color.FromArgb(150, 130, 128), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, Hover ? "单击预览" : "必须确认", MetaFont,
            new Rectangle(Width - 92, y + 2, 86, 16),
            Color.FromArgb(180, 120, 110), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    /// <summary>预览弹窗用的内容（与卡片同一份数据，避免两处写法走歧）。</summary>
    public (string title, string subtitle, List<(string, string)> rows, string footer) BuildPreview()
    {
        var rows = new List<(string, string)>
        {
            ("状　　态:", "未确认不良（待办）"),
            ("优 先 级:", $"{Item.PriorityZh}（按 fail 次数：高≥{TodoGrouping.HighThreshold} / 中≥{TodoGrouping.MediumThreshold}）"),
            ("区间内次数:", $"{Item.RangeCount} 次"),
            ("累计次数:", $"{Item.TotalCount} 次"),
            ("合并项数:", Item.VariantCount > 1 ? $"{Item.VariantCount} 个同类测试项" : "未合并（1 项）"),
            ("首次出现:", string.IsNullOrEmpty(Item.FirstSeen) ? "—" : Item.FirstSeen),
            ("最近出现:", string.IsNullOrEmpty(Item.LastSeen) ? "—" : Item.LastSeen),
            ("型　　号:", string.IsNullOrWhiteSpace(Item.Model) ? "—" : Item.Model),
            ("机　　台:", string.IsNullOrWhiteSpace(Item.StationId) ? "—" : Item.StationId),
            ("归并键:", Item.GroupKey),
        };
        return (Item.Title,
                $"未确认不良 · 优先级{Item.PriorityZh} · {Item.SortCount} 次",
                rows,
                "双击卡片=确认问题 · 拖到其它列=确认并置为该状态 · 不可忽略/删除");
    }
}

// ==================================================================
//  确认问题补填框（轻量版，只填维修人 + 严重度）
// ==================================================================
/// <summary>
/// 从待办确认问题时弹出，补填维修人与严重度。
/// 确定 → 创建一条维修记录并与待办行关联，进入看板工作流。
/// </summary>
public class TodoConfirmForm : Form
{
    public string Resolver => ResolverUtil.Normalize(_resolver.Text);
    public string Severity => MaintenanceMeta.SeverityKeyOf(_severity.SelectedItem?.ToString());

    private readonly ComboBox _resolver;
    private readonly ComboBox _severity;

    public TodoConfirmForm(TodoItem todo, Database? db = null, string? targetStatusZh = null)
    {
        Text = $"确认问题 — {todo.Title}";
        Width = 480; Height = 300;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9F);

        var mergedLine = todo.VariantCount > 1
            ? $"\n已合并 {todo.VariantCount} 个同类测试项（悬停卡片可看清单）"
            : "";
        var head = new Label
        {
            Dock = DockStyle.Top, Height = 88, Padding = new Padding(14, 8, 14, 0),
            Text = $"故障项：{todo.Title}\n" +
                   $"优先级 {todo.PriorityZh} · 区间内 {todo.RangeCount} 次 · 累计 {todo.TotalCount} 次\n" +
                   $"最近 {ShortTime(todo.LastSeen)}（首次 {ShortTime(todo.FirstSeen)}）{mergedLine}",
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
        // 次数多的默认按更严重来选（高优先级 -> 严重）
        _severity.SelectedItem = todo.SortCount >= TodoGrouping.HighThreshold ? "严重" : "一般";
        if (_severity.SelectedIndex < 0) _severity.SelectedIndex = 0;

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
                 ? "确定后自动创建「待办」维修记录；不良本身不可忽略，只能处理掉。"
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

// ==================================================================
//  待办时间区间选择
// ==================================================================
/// <summary>待办时间区间的预设与自定义。</summary>
public sealed class TodoRange
{
    public string Label { get; }
    public DateTime? From { get; }
    public DateTime? To { get; }

    public TodoRange(string label, DateTime? from, DateTime? to)
    {
        Label = label; From = from; To = to;
    }

    public override string ToString() => Label;

    /// <summary>下拉里的预设项（最后一项是「自定义…」占位）。</summary>
    public static TodoRange[] Presets(int defaultDays) => new[]
    {
        OfDays("近 7 天", 7),
        OfDays($"近 {defaultDays} 天", defaultDays),
        OfDays("近 90 天", 90),
        new TodoRange("全部（永久保留）", null, null),
        new TodoRange("自定义…", DateTime.Today, DateTime.Today),
    };

    public static TodoRange OfDays(string label, int days) =>
        new(label, DateTime.Today.AddDays(-Math.Max(0, days - 1)), DateTime.Today);
}

/// <summary>自定义时间区间对话框（两个日期选择器）。</summary>
public class TodoRangeForm : Form
{
    public DateTime From => _from.Value.Date;
    public DateTime To => _to.Value.Date;

    private readonly DateTimePicker _from;
    private readonly DateTimePicker _to;

    public TodoRangeForm(DateTime? from, DateTime? to)
    {
        Text = "选择待办时间区间";
        Width = 380; Height = 200;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(16, 12, 16, 6),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        _from = new DateTimePicker
        {
            Dock = DockStyle.Fill, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd",
            Value = from ?? DateTime.Today.AddDays(-29), Margin = new Padding(3, 5, 3, 5),
        };
        _to = new DateTimePicker
        {
            Dock = DockStyle.Fill, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd",
            Value = to ?? DateTime.Today, Margin = new Padding(3, 5, 3, 5),
        };
        layout.Controls.Add(new Label { Text = "开始:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        layout.Controls.Add(_from, 1, 0);
        layout.Controls.Add(new Label { Text = "结束:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        layout.Controls.Add(_to, 1, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 8, 10, 8),
        };
        var ok = new Button { Text = "确定", Width = 84, Height = 28, DialogResult = DialogResult.None };
        ok.Click += (_, _) =>
        {
            if (_from.Value.Date > _to.Value.Date)
            {
                MessageBox.Show("开始日期不能晚于结束日期。", "提示");
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        var cancel = new Button { Text = "取消", Width = 84, Height = 28, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange(new Control[] { ok, cancel });
        AcceptButton = ok; CancelButton = cancel;

        var note = new Label
        {
            Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(16, 0, 16, 0),
            ForeColor = Color.FromArgb(140, 150, 168),
            Text = "只影响「待办」列的显示范围，不会删除任何待办。",
        };

        Controls.Add(layout);
        Controls.Add(note);
        Controls.Add(buttons);
    }
}

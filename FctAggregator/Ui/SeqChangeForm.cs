namespace FctAggregator;

/// <summary>测试程序 Seq 变更弹窗：对比基线快照，无需填写修改原因。</summary>
public sealed class SeqChangeForm : Form
{
    private readonly SeqChangeReport _report;
    private readonly AppConfig _cfg;
    private readonly Action? _onBaselineUpdated;
    private ListView _list = null!;
    private Label _summary = null!;
    private TextBox _diff = null!;

    public SeqChangeForm(SeqChangeReport report, AppConfig cfg, Action? onBaselineUpdated = null)
    {
        _report = report;
        _cfg = cfg;
        _onBaselineUpdated = onBaselineUpdated;
        Text = "测试程序 Seq 已修改";
        Size = new Size(920, 620);
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.Body;
        BackColor = Theme.Bg;
        AppIcon.Apply(this);
        BuildUi();
        PopulateList();
    }

    private void BuildUi()
    {
        _summary = new Label
        {
            Dock = DockStyle.Top,
            Height = 56,
            Padding = new Padding(12, 10, 12, 0),
            Text = BuildSummaryText(),
            ForeColor = Theme.TextMain,
        };
        var tip = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Padding = new Padding(12, 0, 12, 0),
            Text = "以下为相对上次基线快照的变更（无需填写原因）。可选中文件查看文本对比。",
            ForeColor = Theme.TextFaint,
            Font = Theme.Tiny,
        };
        _list = new ListView
        {
            Dock = DockStyle.Top,
            Height = 220,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            Font = Theme.Mono,
        };
        _list.Columns.Add("类型", 72);
        _list.Columns.Add("文件", 520);
        _list.Columns.Add("大小变化", 120);
        _list.SelectedIndexChanged += (_, _) => ShowSelectedDiff();

        _diff = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Font = Theme.Mono,
            WordWrap = false,
            BackColor = Theme.Surface,
        };

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
        var btnDiff = Theme.MakeButton("文本对比", 100);
        btnDiff.Location = new Point(8, 8);
        btnDiff.Click += (_, _) => ShowSelectedDiff();
        var btnBaseline = Theme.MakeButton("接受为新基线", 120);
        btnBaseline.Location = new Point(116, 8);
        btnBaseline.Click += (_, _) => AcceptBaseline();
        var btnClose = Theme.MakeButton("关闭", 80);
        btnClose.Location = new Point(820, 8);
        btnClose.Click += (_, _) => Close();
        bar.Controls.AddRange(new Control[] { btnDiff, btnBaseline, btnClose });

        Controls.Add(_diff);
        Controls.Add(bar);
        Controls.Add(_list);
        Controls.Add(tip);
        Controls.Add(_summary);
    }

    private string BuildSummaryText()
    {
        var root = _report.SeqRoot;
        var baseAt = string.IsNullOrEmpty(_report.BaselineAt) ? "（未知）" : _report.BaselineAt;
        return $"目录: {root}\n基线时间: {baseAt}  ·  检测: {_report.DetectedAt}  ·  新增 {_report.Added}  删除 {_report.Removed}  修改 {_report.Modified}";
    }

    private void PopulateList()
    {
        _list.Items.Clear();
        foreach (var c in _report.Changes)
        {
            var kind = c.Kind switch
            {
                SeqChangeKind.Added => "新增",
                SeqChangeKind.Removed => "删除",
                _ => "修改",
            };
            var size = c.Kind switch
            {
                SeqChangeKind.Added => FormatSize(c.NewSize),
                SeqChangeKind.Removed => FormatSize(c.OldSize),
                _ => $"{FormatSize(c.OldSize)} → {FormatSize(c.NewSize)}",
            };
            var item = new ListViewItem(kind) { Tag = c };
            item.SubItems.Add(c.RelativePath);
            item.SubItems.Add(size);
            _list.Items.Add(item);
        }
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
    }

    private void ShowSelectedDiff()
    {
        if (_list.SelectedItems.Count == 0) { _diff.Text = "请先在列表中选择一个文件。"; return; }
        var c = (SeqFileChange)_list.SelectedItems[0].Tag!;
        if (!FctSeqSnapshot.IsTextFile(c.RelativePath))
        {
            _diff.Text = $"「{c.RelativePath}」非文本文件。\n修改前 SHA256: {c.OldSha256 ?? "—"}\n修改后 SHA256: {c.NewSha256 ?? "—"}";
            return;
        }
        var oldText = FctSeqSnapshot.ReadBaselineText(c.RelativePath) ?? "（基线中无此文件）";
        string? newText;
        if (c.Kind == SeqChangeKind.Removed)
            newText = "（文件已删除）";
        else
            newText = FctSeqSnapshot.ReadCurrentText(_report.SeqRoot, c.RelativePath) ?? "（无法读取）";

        _diff.Text = BuildUnifiedDiff(c.RelativePath, oldText, newText);
    }

    private static string BuildUnifiedDiff(string path, string oldText, string newText)
    {
        var oldLines = oldText.Replace("\r\n", "\n").Split('\n');
        var newLines = newText.Replace("\r\n", "\n").Split('\n');
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== {path} ===");
        sb.AppendLine();
        int max = Math.Max(oldLines.Length, newLines.Length);
        for (int i = 0; i < max; i++)
        {
            var o = i < oldLines.Length ? oldLines[i] : null;
            var n = i < newLines.Length ? newLines[i] : null;
            if (o == n) sb.AppendLine($"  {o}");
            else
            {
                if (o != null) sb.AppendLine($"- {o}");
                if (n != null) sb.AppendLine($"+ {n}");
            }
        }
        return sb.ToString();
    }

    private void AcceptBaseline()
    {
        if (MessageBox.Show("将当前 Seq 目录保存为新基线，后续变更将相对此次快照对比。继续？",
                "更新基线", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        try
        {
            FctSeqSnapshot.CaptureBaseline(_report.SeqRoot);
            _onBaselineUpdated?.Invoke();
            FctSeqMonitor.Instance?.ResetAlertFingerprint();
            MessageBox.Show("基线已更新。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"更新基线失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string FormatSize(long b) =>
        b >= 1_000_000 ? $"{b / 1_000_000.0:F1} MB" : $"{b / 1_000.0:F1} KB";
}

using System.Xml;
using System.Windows.Forms;
using System.Drawing.Drawing2D;
using FctAggregator;

namespace FctFailRanker;

public class XmlViewerForm : AppForm
{
    private const string IgnoredHint = "不计入不良";
    private static readonly string[] IgnoredFailSteps = { "Get Unit Information", "UUT Status Err" };

    private static readonly Color CBg        = Theme.Surface;
    private static readonly Color CCard      = Theme.Surface;
    private static readonly Color CCardLine  = Theme.Border;
    private static readonly Color CText      = Theme.ToolFixed;
    private static readonly Color CTextDim   = Theme.ToolDim;
    private static readonly Color CAccent    = Theme.ToolSummary;
    private static readonly Color CGreen     = Theme.ToolFixed;
    private static readonly Color CRed       = Theme.ToolSummary;
    private static readonly Color CAmber     = Theme.ToolDim;

    private readonly string _path;
    private readonly ReportData _data;
    private readonly Database? _db;
    private readonly Dictionary<string, int> _monthCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<FailItemDetail>> _recent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _itemShift = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<XmlParser.ReportChapter> _priorChapters = new();
    private UnitRetest.Place _place = new();
    private DataGridView _report = null!;

    // 审计：这几处字体原先在 Paint / 逐行追加里 new 出来从不释放——每次重绘/每追加一行泄漏一个 HFONT。
    // Paint 路径用静态缓存；富文本路径按需 new 后立即 Dispose（RichTextBox 只复制字符格式，不持有 Font）。
    private static readonly Font BadgeFont = new(Theme.Body.FontFamily, 12F, FontStyle.Bold);
    private static readonly Font BigValueFont = new(Theme.Body.FontFamily, 20F, FontStyle.Bold);

    public XmlViewerForm(string path, Database? db = null)
    {
        _path = path;
        _db = db;
        _data = Parse(path);
        LoadRepeatContext();
        LoadUnitContext();

        Text = "测试报告 - " + Path.GetFileName(path);
        // 同 MainForm：工作区是设备像素，AutoScale 会再乘一次缩放系数，必须先折算回逻辑像素
        var uiScale = UiScreenFit.AutoScaleFactor(this);
        var wa = UiScreenFit.LogicalWorkArea(
            (Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800)).Size, uiScale);
        ClientSize = new Size(Math.Min(1080, Math.Max(860, wa.Width - 80)), Math.Min(860, Math.Max(640, wa.Height - 80)));
        MinimumSize = new Size(Math.Min(820, wa.Width), Math.Min(560, wa.Height));
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.Body;
        BackColor = CBg;
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "app_icon.ico");
            if (File.Exists(ico)) Icon = new Icon(ico);
        }
        catch { }

        BuildHeader();
        BuildBody();
        BuildFooter();

        Controls.SetChildIndex(_body, 0);
    }

    private void BuildHeader()
    {
        bool pass = _data.PanelStatus.Equals("Passed", StringComparison.OrdinalIgnoreCase);
        var header = new Panel { Dock = DockStyle.Top, Height = 88, BackColor = Color.White };
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(pass ? CGreen : CRed, 3);
            e.Graphics.DrawLine(pen, 0, header.Height - 2, header.Width, header.Height - 2);
        };

        string badgeText = pass ? "PASS" : (_data.PanelStatus == "" ? "UNKNOWN" : _data.PanelStatus.ToUpperInvariant());
        Color badgeColor = pass ? CGreen : CRed;
        var statusHost = new Panel
        {
            Dock = DockStyle.Right, Width = 148, BackColor = Color.White,
            Padding = new Padding(8, 22, 24, 22),
        };
        var status = new Label
        {
            Text = badgeText, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Font = BadgeFont, ForeColor = badgeColor,
            BackColor = pass ? Color.FromArgb(232, 245, 233) : Color.FromArgb(255, 235, 238),
        };
        statusHost.Controls.Add(status);

        var textHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(24, 12, 8, 8) };
        var lblKicker = new Label
        {
            Text = "FCT 测试报告", Dock = DockStyle.Top, Height = 22,
            Font = Theme.Body, ForeColor = CTextDim, TextAlign = ContentAlignment.BottomLeft,
        };
        var lblSn = new Label
        {
            Text = string.IsNullOrEmpty(_data.Sn) ? Path.GetFileName(_path) : _data.Sn,
            Dock = DockStyle.Fill, AutoEllipsis = true,
            Font = Theme.CachedFont(Theme.Body.FontFamily.Name, 16F, FontStyle.Bold),
            ForeColor = CText, TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.IBeam,
        };
        var snMenu = new ContextMenuStrip();
        snMenu.Items.Add("复制 SN", null, (_, _) => TrySetClipboard(_data.Sn));
        snMenu.Items.Add("复制文件名", null, (_, _) => TrySetClipboard(Path.GetFileName(_path)));
        snMenu.Items.Add("复制完整路径", null, (_, _) => TrySetClipboard(_path));
        lblSn.ContextMenuStrip = snMenu;
        textHost.Controls.Add(lblSn);
        textHost.Controls.Add(lblKicker);

        header.Controls.Add(textHost);
        header.Controls.Add(statusHost);
        Controls.Add(header);
    }

    private void LoadRepeatContext()
    {
        if (_db == null) return;
        try
        {
            var (from, to) = RepeatFailMonth.Range(DateTime.Today);
            foreach (var kv in _db.CountFailDetailsByName(from, to))
                _monthCounts[kv.Key] = kv.Value;
            foreach (var t in _data.Tests)
            {
                if (!t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(t.Name) || _recent.ContainsKey(t.Name)) continue;
                _recent[t.Name] = _db.ListRecentFailDetails(t.Name, from, to, 5);
            }
        }
        catch (Exception ex) { Logger.Warning($"[报告] 本月重复次数加载失败: {ex.Message}"); }
    }

    private void LoadUnitContext()
    {
        if (_db == null || string.IsNullOrWhiteSpace(_data.Sn)) return;
        try
        {
            var runs = _db.ListUnitRuns(_data.Sn);
            _place = UnitRetest.Locate(runs, _path);
            var order = (_data.Timestamp ?? "").Replace('T', ' ');
            foreach (var t in _data.Tests)
            {
                if (!t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) || IsIgnored(t.Name)) continue;
                if (string.IsNullOrWhiteSpace(t.Name) || _itemShift.ContainsKey(t.Name)) continue;
                var prior = _db.FindPriorItem(_data.Sn, t.Name, _path, order);
                if (prior == null) continue;
                var hasCur = UnitRetest.TryNum(t.Value, out var cur);
                double? lo = UnitRetest.TryNum(t.Lolim, out var loV) ? loV : prior.Lo;
                double? hi = UnitRetest.TryNum(t.Hilim, out var hiV) ? hiV : prior.Hi;
                var move = UnitRetest.Judge(hasCur ? cur : null, prior.Value, lo, hi);
                var text = UnitRetest.FormatItemShift(prior.ValueText, prior.Value, move);
                if (text.Length > 0) _itemShift[t.Name] = text;
            }
            if (_place.Previous != null && _data.Chapters.Count > 0 && File.Exists(_place.Previous.XmlPath))
            {
                var prev = XmlParser.ParseReport(_place.Previous.XmlPath);
                if (!prev.Error) _priorChapters.AddRange(prev.Chapters);
            }
        }
        catch (Exception ex) { Logger.Warning($"[报告] 同台上次对照加载失败: {ex.Message}"); }
    }

    private Panel _body = null!;

    private void BuildBody()
    {
        var body = new Panel { Dock = DockStyle.Fill, BackColor = CBg, Padding = new Padding(24, 18, 24, 8) };
        _body = body;

        var kpi = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = CBg };
        int totalTests = _data.Tests.Count;
        int failed = _data.FailCount;
        int ignored = _data.Tests.Count(t => t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) && IsIgnored(t.Name));
        int passed = _data.Tests.Count(t => t.Status.Equals("Passed", StringComparison.OrdinalIgnoreCase));
        var cards = new (string label, string value, Color color)[]
        {
            ("测试项总数", totalTests.ToString(), CAccent),
            ("失败(计入不良)", failed.ToString(), failed > 0 ? CRed : CGreen),
            ("排除项", ignored.ToString(), CAmber),
            ("通过项", passed.ToString(), CGreen),
        };
        kpi.Controls.Add(BuildKpiRow(cards));
        body.Controls.Add(kpi);

        var timing = BuildTiming();

        var lvHost = new Panel { Dock = DockStyle.Fill, BackColor = CBg, Padding = new Padding(0, 10, 0, 0) };
        var lblTable = new Label
        {
            Text = "测试项明细", Dock = DockStyle.Top, Height = 30,
            Font = Theme.SectionTitle,
            ForeColor = CText, Padding = new Padding(2, 4, 0, 0),
        };
        var lblHint = new Label
        {
            Text = "选中单元格后 Ctrl+C 复制", Dock = DockStyle.Top, Height = 18,
            Font = Theme.Small,
            ForeColor = CTextDim, Padding = new Padding(3, 0, 0, 2),
        };
        _report = BuildReport();
        lvHost.Controls.Add(_report);
        lvHost.Controls.Add(lblHint);
        lvHost.Controls.Add(lblTable);
        lvHost.Controls.SetChildIndex(lblTable, 2);
        lvHost.Controls.SetChildIndex(lblHint, 1);
        lvHost.Controls.SetChildIndex(_report, 0);
        body.Controls.Add(lvHost);

        var infoHost = new Panel { Dock = DockStyle.Top, Height = 168, BackColor = CBg, Padding = new Padding(0, 10, 0, 6) };
        infoHost.Controls.Add(BuildInfoGrid());
        body.Controls.Add(infoHost);
        var priorHost = BuildPriorStrip();
        body.Controls.Add(priorHost);
        body.Controls.Add(timing);

        body.Controls.SetChildIndex(kpi, 4);
        body.Controls.SetChildIndex(infoHost, 3);
        body.Controls.SetChildIndex(priorHost, 2);
        body.Controls.SetChildIndex(timing, 1);
        body.Controls.SetChildIndex(lvHost, 0);

        Controls.Add(body);
    }

    private Panel BuildKpiRow((string label, string value, Color color)[] cards)
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = CBg };
        var tl = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = cards.Length, RowCount = 1, BackColor = CBg,
        };
        for (int i = 0; i < cards.Length; i++)
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cards.Length));

        for (int i = 0; i < cards.Length; i++)
        {
            var (label, value, color) = cards[i];
            var card = new Panel { Dock = DockStyle.Fill, Margin = new Padding(i == 0 ? 0 : 6, 0, i == cards.Length - 1 ? 0 : 6, 0), BackColor = CCard };
            card.Paint += (s, e) =>
            {
                var p = (Panel)s!;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, p.Width - 1, p.Height - 1);
                using var path = RoundRect(r, 8);
                using var pen = new Pen(CCardLine, 1);
                e.Graphics.DrawPath(pen, path);
                using var bar = new SolidBrush(color);
                e.Graphics.FillRectangle(bar, 0, 10, 4, p.Height - 20);
                TextRenderer.DrawText(e.Graphics, value, BigValueFont,
                    new Rectangle(18, 10, p.Width - 24, 40), color, TextFormatFlags.Left);
                TextRenderer.DrawText(e.Graphics, label, Theme.Body,
                    new Rectangle(18, 52, p.Width - 24, 22), CTextDim, TextFormatFlags.Left);
            };
            tl.Controls.Add(card, i, 0);
        }
        host.Controls.Add(tl);
        return host;
    }

    private Panel BuildInfoGrid()
    {
        var info = new (string k, string v)[]
        {
            ("机台 TESTER", _data.Tester),
            ("操作模式", _data.User),
            ("测试时间", _data.Timestamp),
            ("整体状态", _data.PanelStatus),
            ("SN", _data.Sn),
            ("文件名", Path.GetFileName(_path)),
        };
        var card = new Panel { Dock = DockStyle.Fill, BackColor = CCard };
        var infoMenu = new ContextMenuStrip();
        infoMenu.Items.Add("复制全部信息", null, (_, _) =>
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (k, v) in info) sb.AppendLine($"{k}\t{v}");
            TrySetClipboard(sb.ToString());
        });
        card.ContextMenuStrip = infoMenu;
        card.Paint += (s, e) =>
        {
            var p = (Panel)s!;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundRect(new Rectangle(0, 0, p.Width - 1, p.Height - 1), 8);
            using var pen = new Pen(CCardLine, 1);
            e.Graphics.DrawPath(pen, path);

            int cols = 3;
            int padx = 18, pady = 10;
            int cw = Math.Max(40, (p.Width - padx * 2) / cols);
            int rowH = 46;
            var fk = Theme.Small;
            var fv = Theme.BodyBold;
            for (int i = 0; i < info.Length - 1; i++)
            {
                int c = i % cols, r = i / cols;
                int x = padx + c * cw, yy = pady + r * rowH;
                TextRenderer.DrawText(e.Graphics, info[i].k, fk,
                    new Rectangle(x, yy, cw - 12, 16), CTextDim, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                var val = string.IsNullOrEmpty(info[i].v) ? "—" : info[i].v;
                bool failed = info[i].k == "整体状态" && val.Equals("Failed", StringComparison.OrdinalIgnoreCase);
                TextRenderer.DrawText(e.Graphics, val, fv,
                    new Rectangle(x, yy + 18, cw - 12, 22), failed ? CRed : CText,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
            int fileY = pady + 2 * rowH;
            TextRenderer.DrawText(e.Graphics, info[^1].k, fk,
                new Rectangle(padx, fileY, p.Width - padx * 2, 16), CTextDim, TextFormatFlags.Left);
            var fileName = string.IsNullOrEmpty(info[^1].v) ? "—" : info[^1].v;
            TextRenderer.DrawText(e.Graphics, fileName, fv,
                new Rectangle(padx, fileY + 18, p.Width - padx * 2, 22), CText,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        };
        return card;
    }

    private Panel BuildTiming()
    {
        var host = new Panel { Dock = DockStyle.Top, BackColor = CBg, Padding = new Padding(0, 8, 0, 0) };
        string title = _data.TotalSeconds is double total
            ? $"章节耗时    共 {total:0.0} 秒"
            : "章节耗时";
        var lbl = new Label
        {
            Text = title, Dock = DockStyle.Top, Height = 30,
            Font = Theme.SectionTitle, ForeColor = CText, Padding = new Padding(2, 4, 0, 0),
        };
        if (_data.Chapters.Count == 0)
        {
            var empty = new Label
            {
                Text = "这份报告没有章节耗时", Dock = DockStyle.Top, Height = 22,
                Font = Theme.Body, ForeColor = CTextDim, Padding = new Padding(2, 0, 0, 0),
            };
            host.Height = 62;
            host.Controls.Add(empty);
            host.Controls.Add(lbl);
            return host;
        }

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToResizeRows = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false, BorderStyle = BorderStyle.None,
            BackgroundColor = Color.White, GridColor = CCardLine,
            EnableHeadersVisualStyles = false, Font = Theme.Body,
            ColumnHeadersHeight = 34, RowTemplate = { Height = 32 },
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
            ScrollBars = ScrollBars.Vertical,
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = CText;
        grid.ColumnHeadersDefaultCellStyle.Font = Theme.BodyBold;
        void Col(string name, string header, float weight, int min, DataGridViewContentAlignment align)
        {
            var c = new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, FillWeight = weight, MinimumWidth = min,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            };
            c.DefaultCellStyle.Alignment = align;
            c.HeaderCell.Style.Alignment = align;
            grid.Columns.Add(c);
        }
        Col("name", "章节", 46, 160, DataGridViewContentAlignment.MiddleLeft);
        Col("sec", "耗时", 16, 72, DataGridViewContentAlignment.MiddleRight);
        Col("share", "占比", 12, 60, DataGridViewContentAlignment.MiddleRight);
        Col("prev", "上次", 16, 72, DataGridViewContentAlignment.MiddleRight);
        Col("delta", "差值", 16, 72, DataGridViewContentAlignment.MiddleRight);
        foreach (var c in _data.Chapters)
        {
            var share = XmlParser.ChapterShare(c.Seconds, _data.TotalSeconds);
            var priorSec = UnitRetest.PriorChapterSeconds(_priorChapters, c.Name);
            grid.Rows.Add(
                c.Name,
                $"{c.Seconds:0.0} 秒",
                share is double p ? $"{p:0.0}%" : "—",
                priorSec is double ps ? $"{ps:0.0} 秒" : "—",
                priorSec is double pd ? UnitRetest.FormatDelta(c.Seconds, pd) : "—");
        }
        int visible = Math.Min(_data.Chapters.Count, 6);
        host.Height = 8 + 30 + 34 + visible * 32 + 2;
        host.Controls.Add(grid);
        host.Controls.Add(lbl);
        return host;
    }

    private Panel BuildPriorStrip()
    {
        var host = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = CBg, Padding = new Padding(2, 4, 0, 0) };
        string text;
        if (string.IsNullOrWhiteSpace(_data.Sn))
            text = "没有序列号，不能对照上次";
        else if (_db == null)
            text = "未连接记录库，不能对照上次";
        else if (!_place.Found)
            text = "库里没有这台的测试记录";
        else if (_place.Previous == null)
            text = UnitRetest.FormatOrdinal(_place) + "，没有更早的测试";
        else
        {
            var day = UnitRetest.DayKey(_place.Current?.Timestamp, _place.Current?.TestDate);
            text = UnitRetest.FormatOrdinal(_place) + "    " + UnitRetest.FormatPrevious(_place.Previous, day);
        }
        var lbl = new Label
        {
            Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Body, ForeColor = CText, AutoEllipsis = true,
        };
        host.Controls.Add(lbl);
        var prevPath = _place.Previous?.XmlPath;
        if (!string.IsNullOrEmpty(prevPath) && File.Exists(prevPath))
        {
            var link = new LinkLabel
            {
                Text = "打开上次报告", Dock = DockStyle.Right, Width = 110, TextAlign = ContentAlignment.MiddleRight,
                Font = Theme.Body, LinkColor = CAccent, ActiveLinkColor = CAccent, VisitedLinkColor = CAccent,
            };
            link.LinkClicked += (_, _) =>
            {
                try
                {
                    using var dlg = new XmlViewerForm(prevPath, _db);
                    dlg.ShowDialog(this);
                }
                catch (Exception ex) { MessageBox.Show("打开失败: " + ex.Message); }
            };
            host.Controls.Add(link);
        }
        return host;
    }

    private DataGridView BuildReport()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToResizeRows = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false, BorderStyle = BorderStyle.None,
            BackgroundColor = Color.White, GridColor = CCardLine,
            EnableHeadersVisualStyles = false, Font = Theme.Body,
            ColumnHeadersHeight = 34, RowTemplate = { Height = 32 },
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
            ShowCellToolTips = true,
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = CText;
        grid.ColumnHeadersDefaultCellStyle.Font = Theme.BodyBold;
        grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(32, 32, 32);
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);

        void Col(string name, string header, float weight, int min, DataGridViewContentAlignment align)
        {
            var c = new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, FillWeight = weight, MinimumWidth = min,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            };
            c.DefaultCellStyle.Alignment = align;
            c.HeaderCell.Style.Alignment = align;
            grid.Columns.Add(c);
        }
        Col("idx", "#", 8, 36, DataGridViewContentAlignment.MiddleCenter);
        Col("name", "测试项", 40, 200, DataGridViewContentAlignment.MiddleLeft);
        Col("month", "本月", 12, 88, DataGridViewContentAlignment.MiddleCenter);
        Col("value", "测量值", 14, 88, DataGridViewContentAlignment.MiddleRight);
        Col("lo", "下限", 12, 72, DataGridViewContentAlignment.MiddleRight);
        Col("hi", "上限", 12, 72, DataGridViewContentAlignment.MiddleRight);
        Col("unit", "单位", 8, 52, DataGridViewContentAlignment.MiddleCenter);
        Col("status", "状态", 12, 72, DataGridViewContentAlignment.MiddleCenter);
        Col("shift", "较上次", 18, 120, DataGridViewContentAlignment.MiddleLeft);

        int idx = 1;
        foreach (var t in _data.Tests)
        {
            bool isFail = t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase);
            bool ignored = isFail && IsIgnored(t.Name);
            string status = isFail ? (ignored ? "排除" : "FAILED")
                                   : (string.IsNullOrEmpty(t.Status) ? "—" : t.Status.ToUpperInvariant());
            int monthN = isFail ? RepeatFailMonth.Lookup(_monthCounts, t.Name) : 0;
            string monthText = monthN > 0 ? $"本月 {monthN} 次" : "";
            _itemShift.TryGetValue(t.Name, out var shift);
            int rowIndex = grid.Rows.Add(
                idx.ToString(),
                string.IsNullOrEmpty(t.Name) ? "—" : t.Name,
                monthText,
                string.IsNullOrEmpty(t.Value) ? "—" : t.Value,
                string.IsNullOrEmpty(t.Lolim) ? "—" : t.Lolim,
                string.IsNullOrEmpty(t.Hilim) ? "—" : t.Hilim,
                string.IsNullOrEmpty(t.Unit) ? "—" : t.Unit,
                ignored ? $"{status}·{IgnoredHint}" : status,
                string.IsNullOrEmpty(shift) ? "" : shift);
            var row = grid.Rows[rowIndex];
            if (isFail && !ignored)
            {
                row.DefaultCellStyle.BackColor = Color.FromArgb(255, 241, 242);
                row.DefaultCellStyle.ForeColor = CRed;
                row.DefaultCellStyle.Font = Theme.BodyBold;
            }
            else if (ignored)
            {
                row.DefaultCellStyle.BackColor = Color.FromArgb(255, 248, 235);
                row.DefaultCellStyle.ForeColor = CAmber;
            }
            else
            {
                row.DefaultCellStyle.BackColor = idx % 2 == 0 ? Color.FromArgb(250, 250, 250) : Color.White;
                row.DefaultCellStyle.ForeColor = CText;
            }
            row.Cells["status"].ToolTipText = ignored ? IgnoredHint : status;
            row.Cells["name"].ToolTipText = t.Name;
            if (!string.IsNullOrEmpty(shift)) row.Cells["shift"].ToolTipText = shift;
            if (monthN > 0 && _recent.TryGetValue(t.Name, out var recent) && recent.Count > 0)
            {
                row.Cells["month"].ToolTipText = string.Join("\n", recent.Select(r =>
                    $"{r.Ts}   {r.Sn}   {r.ValueDisplay}"));
            }
            idx++;
        }

        var menu = new ContextMenuStrip();
        menu.Items.Add("复制选中行", null, (_, _) =>
        {
            if (grid.CurrentRow == null) return;
            var parts = grid.CurrentRow.Cells.Cast<DataGridViewCell>().Select(c => c.Value?.ToString() ?? "");
            TrySetClipboard(string.Join("\t", parts));
        });
        menu.Items.Add("复制全部", null, (_, _) =>
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("#\t测试项\t本月\t测量值\t下限\t上限\t单位\t状态");
            foreach (DataGridViewRow row in grid.Rows)
                sb.AppendLine(string.Join("\t", row.Cells.Cast<DataGridViewCell>().Select(c => c.Value?.ToString() ?? "")));
            TrySetClipboard(sb.ToString());
        });
        grid.ContextMenuStrip = menu;
        return grid;
    }

    private static void TrySetClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch { try { Clipboard.SetDataObject(text, true); } catch { } }
    }

    private void BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Color.White };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(CCardLine, 1);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };

        Button MakeBtn(string text, Color accent, bool primary)
        {
            var b = new Button
            {
                Text = text, Height = 32, Width = 130, FlatStyle = FlatStyle.Flat,
                ForeColor = primary ? Color.White : CText,
                BackColor = primary ? accent : Color.White,
                Font = Theme.Body, Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = CCardLine;
            b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(accent) : Color.FromArgb(239, 239, 239);
            return b;
        }

        var btnRaw = MakeBtn("查看原始 XML", CAccent, false);
        btnRaw.Left = 24; btnRaw.Top = 10;
        btnRaw.Click += (_, _) => ShowRaw();

        var btnExt = MakeBtn("用默认程序打开", CAccent, false);
        btnExt.Left = 160; btnExt.Top = 10; btnExt.Width = 140;
        btnExt.Click += (_, _) => OpenExternal();

        var btnClose = MakeBtn("关闭", CAccent, true);
        btnClose.Top = 10; btnClose.Width = 100;
        btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        btnClose.Left = footer.Width - btnClose.Width - 24;
        btnClose.Click += (_, _) => Close();
        footer.Resize += (_, _) => btnClose.Left = footer.Width - btnClose.Width - 24;

        footer.Controls.Add(btnRaw);
        footer.Controls.Add(btnExt);
        footer.Controls.Add(btnClose);
        Controls.Add(footer);
        CancelButton = btnClose;
    }

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void ShowRaw()
    {
        var f = new Form
        {
            Text = "原始 XML - " + Path.GetFileName(_path),
            ClientSize = new Size(800, 620), StartPosition = FormStartPosition.CenterParent,
            BackColor = CBg,
        };
        try { var ico = Path.Combine(AppContext.BaseDirectory, "app_icon.ico"); if (File.Exists(ico)) f.Icon = new Icon(ico); } catch { }
        var tb = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Both, WordWrap = false,             Font = Theme.CachedFont(Theme.Mono.FontFamily.Name, 9.5F),
            BackColor = Color.White, ForeColor = Color.FromArgb(20, 20, 20),
            BorderStyle = BorderStyle.None,
        };
        try { tb.Text = File.ReadAllText(_path); }
        catch (Exception ex) { tb.Text = "读取失败: " + ex.Message; }
        f.Controls.Add(tb);
        f.ShowDialog(this);
    }

    private void OpenExternal()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = _path, UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show("打开失败: " + ex.Message); }
    }

    private static bool IsIgnored(string name)
    {
        foreach (var ig in IgnoredFailSteps)
            if (name.Contains(ig, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private class ReportData
    {
        public string Timestamp = "", User = "", Tester = "", PanelStatus = "", Sn = "";
        public int FailCount;
        public double? TotalSeconds;
        public List<XmlParser.ReportChapter> Chapters = new();
        public List<TestItem> Tests = new();
    }
    private class TestItem
    {
        public string Name = "", Value = "", Lolim = "", Hilim = "", Unit = "", Status = "";
    }

    private static ReportData Parse(string path)
    {
        var src = XmlParser.ParseReport(path);
        var d = new ReportData
        {
            Timestamp = src.BatchTimestamp,
            User = src.FactoryUser,
            Tester = src.Tester,
            PanelStatus = src.PanelStatus,
            Sn = src.Sn,
            TotalSeconds = src.TotalSeconds,
            Chapters = src.Chapters,
        };
        foreach (var t in src.Tests)
        {
            d.Tests.Add(new TestItem
            {
                Name = t.Name, Value = t.Value, Lolim = t.Lolim, Hilim = t.Hilim, Unit = t.Unit, Status = t.Status,
            });
            if (t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) && !IsIgnored(t.Name))
                d.FailCount++;
        }
        return d;
    }
}

using System.Xml;
using System.Windows.Forms;
using System.Drawing.Drawing2D;
using FctAggregator;

namespace FctFailRanker;

/// <summary>
/// XML 测试报告查看器 - 深色仪表盘风格。
/// 顶部 SN 标题栏 + 状态徽章、KPI 统计卡、信息网格、自绘测试项表格。
/// </summary>
public class XmlViewerForm : Form
{
    private const string IgnoredHint = "不计入不良";
    private static readonly string[] IgnoredFailSteps = { "Get Unit Information", "UUT Status Err" };

    // ---- 配色 (亮色主题, v3.4.12) ----
    private static readonly Color CBg        = Color.FromArgb(243, 244, 246);  // 主背景
    private static readonly Color CCard      = Color.White;                    // 卡片背景
    private static readonly Color CCardLine  = Color.FromArgb(209, 213, 219); // 卡片描边
    private static readonly Color CText      = Color.FromArgb(31, 41, 55);    // 主文字
    private static readonly Color CTextDim   = Color.FromArgb(107, 114, 128); // 次要文字
    private static readonly Color CAccent    = Color.FromArgb(26, 115, 232);  // 强调蓝
    private static readonly Color CGreen     = Color.FromArgb(22, 163, 74);
    private static readonly Color CRed       = Color.FromArgb(220, 38, 38);
    private static readonly Color CAmber     = Color.FromArgb(217, 119, 6);

    private readonly string _path;
    private readonly ReportData _data;
    private RichTextBox _report = null!;

    public XmlViewerForm(string path)
    {
        _path = path;
        _data = Parse(path);

        Text = "测试报告 - " + Path.GetFileName(path);
        ClientSize = new Size(940, 780);
        MinimumSize = new Size(720, 560);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
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

        // 修正停靠 z-order: Fill(body) 必须最后填充
        //   停靠按 index 高->低 依次占位, Fill 应最后占 => body 置于最低 index(0)
        Controls.SetChildIndex(_body, 0);
    }

    // ========================= 顶部标题栏 =========================
    private void BuildHeader()
    {
        bool pass = _data.PanelStatus.Equals("Passed", StringComparison.OrdinalIgnoreCase);
        var header = new Panel { Dock = DockStyle.Top, Height = 104, BackColor = Color.White };
        header.Paint += (_, e) =>
        {
            // 底部强调色细线
            using var pen = new Pen(pass ? CGreen : CRed, 3);
            e.Graphics.DrawLine(pen, 0, header.Height - 2, header.Width, header.Height - 2);
        };

        var lblKicker = new Label
        {
            Text = "FCT 测试报告",
            Font = new Font("Microsoft YaHei UI", 9F),
            ForeColor = CTextDim, Left = 28, Top = 18, AutoSize = true,
        };
        var lblSn = new Label
        {
            Text = string.IsNullOrEmpty(_data.Sn) ? Path.GetFileName(_path) : _data.Sn,
            Font = new Font("Consolas", 16F, FontStyle.Bold),
            ForeColor = CText, Left = 28, Top = 44, AutoSize = false,
            Height = 30, AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleLeft,
            Width = ClientSize.Width - 28 - 132 - 40,   // 预留右侧徽章区域
        };
        header.Controls.Add(lblKicker);
        header.Controls.Add(lblSn);

        // SN 可右键复制
        var snMenu = new ContextMenuStrip();
        snMenu.Items.Add("复制 SN", null, (_, _) => TrySetClipboard(_data.Sn));
        snMenu.Items.Add("复制文件名", null, (_, _) => TrySetClipboard(Path.GetFileName(_path)));
        snMenu.Items.Add("复制完整路径", null, (_, _) => TrySetClipboard(_path));
        lblSn.ContextMenuStrip = snMenu;
        lblSn.Cursor = Cursors.IBeam;

        // 状态徽章(右上, 自绘圆角)
        var badge = new Panel
        {
            Width = 132, Height = 44, Top = 30,
            Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = Color.Transparent,
        };
        string badgeText = pass ? "PASS" : (_data.PanelStatus == "" ? "UNKNOWN" : _data.PanelStatus.ToUpperInvariant());
        Color badgeColor = pass ? CGreen : CRed;
        badge.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, badge.Width - 1, badge.Height - 1);
            using var path = RoundRect(r, 10);
            using var fill = new SolidBrush(Color.FromArgb(30, badgeColor));
            using var pen = new Pen(badgeColor, 1.6f);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(pen, path);
            // 圆点
            using var dot = new SolidBrush(badgeColor);
            e.Graphics.FillEllipse(dot, 16, badge.Height / 2 - 5, 10, 10);
            TextRenderer.DrawText(e.Graphics, badgeText, new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
                new Rectangle(30, 0, badge.Width - 34, badge.Height), badgeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        };
        header.Controls.Add(badge);
        // 初始定位 + 随 header 尺寸变化重定位(右对齐), 避免初始 width 为 0 导致徽章被遮
        void PlaceBadge() => badge.Left = Math.Max(header.ClientSize.Width - badge.Width - 28, lblSn.Right + 12);
        header.SizeChanged += (_, _) => PlaceBadge();

        Controls.Add(header);
        PlaceBadge();   // header 已入 Controls 且完成停靠布局, 此时 Width 才准确
    }

    private Panel _body = null!;

    // ========================= 主体 =========================
    private void BuildBody()
    {
        var body = new Panel { Dock = DockStyle.Fill, BackColor = CBg, Padding = new Padding(24, 18, 24, 8) };
        _body = body;

        // ---- KPI 卡片行 ----
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

        // ---- 表格标题 + 表格 ----
        var lvHost = new Panel { Dock = DockStyle.Fill, BackColor = CBg, Padding = new Padding(0, 10, 0, 0) };
        var lblTable = new Label
        {
            Text = "测试项明细", Dock = DockStyle.Top, Height = 30,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
            ForeColor = CText, Padding = new Padding(2, 4, 0, 0),
        };
        var lblHint = new Label
        {
            Text = "可直接拖动鼠标选中文字(蓝色选区), Ctrl+C 复制 ・ 右键菜单全选/复制", Dock = DockStyle.Top, Height = 18,
            Font = new Font("Microsoft YaHei UI", 8F),
            ForeColor = CTextDim, Padding = new Padding(3, 0, 0, 2),
        };
        _report = BuildReport();
        lvHost.Controls.Add(_report);
        lvHost.Controls.Add(lblHint);
        lvHost.Controls.Add(lblTable);
        // 自上而下: 标题, 提示, 报告文本(Fill) => 标题 index 最高
        lvHost.Controls.SetChildIndex(lblTable, 2);
        lvHost.Controls.SetChildIndex(lblHint, 1);
        lvHost.Controls.SetChildIndex(_report, 0);
        body.Controls.Add(lvHost);

        // ---- 信息网格(放中间, KPI 下方) ----
        var infoHost = new Panel { Dock = DockStyle.Top, Height = 120, BackColor = CBg, Padding = new Padding(0, 10, 0, 6) };
        infoHost.Controls.Add(BuildInfoGrid());
        body.Controls.Add(infoHost);

        // Dock 布局: 停靠按 z-order 逆序占位(index 最高者最先停靠)。
        // 期望自上而下: KPI, 信息, 表格(Fill)
        //   => KPI 索引最高, 信息居中, Fill 最低
        body.Controls.SetChildIndex(kpi, 2);
        body.Controls.SetChildIndex(infoHost, 1);
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
                // 左侧强调竖条
                using var bar = new SolidBrush(color);
                e.Graphics.FillRectangle(bar, 0, 10, 4, p.Height - 20);
                // 数值
                TextRenderer.DrawText(e.Graphics, value, new Font("Microsoft YaHei UI", 20F, FontStyle.Bold),
                    new Rectangle(18, 10, p.Width - 24, 40), color, TextFormatFlags.Left);
                // 标签
                TextRenderer.DrawText(e.Graphics, label, new Font("Microsoft YaHei UI", 9F),
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
            ("文件名", Path.GetFileName(_path)),
            ("SN", _data.Sn),
        };
        var card = new Panel { Dock = DockStyle.Fill, BackColor = CCard };
        // 信息卡右键可复制全部头部信息
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

            int cols = 3, rows = 2;
            int padx = 18, pady = 12;
            int cw = (p.Width - padx * 2) / cols;
            int ch = (p.Height - pady * 2) / rows;
            if (ch < 44) ch = 44;   // 保证每行足够高, 避免标签与值重叠
            var fk = new Font("Microsoft YaHei UI", 8.5F);
            var fv = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            for (int i = 0; i < info.Length; i++)
            {
                int c = i % cols, r = i / cols;
                int x = padx + c * cw, yy = pady + r * ch;
                TextRenderer.DrawText(e.Graphics, info[i].k, fk,
                    new Rectangle(x, yy, cw - 12, 16), CTextDim, TextFormatFlags.Left);
                var val = string.IsNullOrEmpty(info[i].v) ? "—" : info[i].v;
                TextRenderer.DrawText(e.Graphics, val, fv,
                    new Rectangle(x, yy + 18, cw - 12, 22), CText,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
        };
        return card;
    }

    // ========================= 自绘表格 =========================
    private RichTextBox BuildReport()
    {
        var rtb = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true, BorderStyle = BorderStyle.None,
            BackColor = CCard, ForeColor = CText,
            Font = new Font("NSimSun", 10.5F),
            WordWrap = false, ScrollBars = RichTextBoxScrollBars.Both,
            DetectUrls = false, HideSelection = false,
            Cursor = Cursors.IBeam,
        };

        // 列宽(按显示宽度: 中文=2, 其它=1), 用等宽字体对齐
        const int wIdx = 4, wName = 48, wVal = 12, wLo = 10, wHi = 10, wUnit = 6;
        // 字符显示宽度: CJK / 全角 算 2, 其余算 1
        static int DispW(string s)
        {
            int w = 0;
            foreach (var ch in s) w += IsWide(ch) ? 2 : 1;
            return w;
        }
        static bool IsWide(char c)
            => (c >= 0x1100 && c <= 0x115F) ||     // 韩文字母
               (c >= 0x2E80 && c <= 0xA4CF) ||     // CJK 及部首、日文等
               (c >= 0xAC00 && c <= 0xD7A3) ||     // 韩文音节
               (c >= 0xF900 && c <= 0xFAFF) ||     // CJK 兼容
               (c >= 0xFE30 && c <= 0xFE4F) ||     // CJK 兼容形式
               (c >= 0xFF00 && c <= 0xFF60) ||     // 全角 ASCII
               (c >= 0xFFE0 && c <= 0xFFE6);       // 全角符号
        // 按显示宽度截断
        static string Clip(string s, int w)
        {
            if (DispW(s) <= w) return s;
            var sb = new System.Text.StringBuilder();
            int cur = 0;
            foreach (var ch in s)
            {
                int cw = IsWide(ch) ? 2 : 1;
                if (cur + cw > w - 1) break;   // 预留 1 位给省略号
                sb.Append(ch); cur += cw;
            }
            sb.Append('…');
            return sb.ToString();
        }
        string Col(string s, int w, bool right = false)
        {
            s ??= "";
            s = Clip(s, w);
            int pad = w - DispW(s);
            if (pad < 0) pad = 0;
            return right ? new string(' ', pad) + s : s + new string(' ', pad);
        }
        string Row(string a, string b, string c, string d, string e, string f, string g)
            => Col(a, wIdx) + Col(b, wName) + Col(c, wVal, true) + Col(d, wLo, true)
             + Col(e, wHi, true) + Col(f, wUnit, true) + "  " + g;

        // 表头(不用粗体, 避免合成粗体宽度差异破坏对齐)
        AppendColored(rtb, Row("#", "测试项", "测量值", "下限", "上限", "单位", "状态") + "\n", CAccent, false);
        AppendColored(rtb, new string('─', wIdx + wName + wVal + wLo + wHi + wUnit + 12) + "\n", Color.FromArgb(156, 163, 175), false);

        int idx = 1;
        foreach (var t in _data.Tests)
        {
            bool isFail = t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase);
            bool ignored = isFail && IsIgnored(t.Name);
            string status = isFail ? (ignored ? $"排除·{IgnoredHint}" : "FAILED")
                                   : (string.IsNullOrEmpty(t.Status) ? "-" : t.Status.ToUpperInvariant());
            var line = Row(
                idx.ToString(),
                t.Name,
                string.IsNullOrEmpty(t.Value) ? "-" : t.Value,
                string.IsNullOrEmpty(t.Lolim) ? "-" : t.Lolim,
                string.IsNullOrEmpty(t.Hilim) ? "-" : t.Hilim,
                string.IsNullOrEmpty(t.Unit) ? "-" : t.Unit,
                status) + "\n";
            Color fg = isFail ? (ignored ? CAmber : CRed) : CText;
            AppendColored(rtb, line, fg, false);   // 不用粗体, 保证列对齐; 用颜色区分失败项
            idx++;
        }

        rtb.SelectionStart = 0;
        rtb.SelectionLength = 0;

        // 右键菜单: 复制选中 / 全选 / 复制全部
        var menu = new ContextMenuStrip();
        menu.Items.Add("复制选中", null, (_, _) =>
        {
            if (rtb.SelectionLength > 0) TrySetClipboard(rtb.SelectedText);
            else TrySetClipboard(rtb.Text);
        });
        menu.Items.Add("全选", null, (_, _) => rtb.SelectAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("复制全部", null, (_, _) => TrySetClipboard(rtb.Text));
        rtb.ContextMenuStrip = menu;

        return rtb;
    }

    /// <summary>向 RichTextBox 追加一段带颜色/粗体的文字。</summary>
    private static void AppendColored(RichTextBox rtb, string text, Color color, bool bold)
    {
        int start = rtb.TextLength;
        rtb.AppendText(text);
        rtb.Select(start, text.Length);
        rtb.SelectionColor = color;
        var baseFont = rtb.Font;
        rtb.SelectionFont = new Font(baseFont, bold ? FontStyle.Bold : FontStyle.Regular);
        rtb.SelectionLength = 0;
    }

    private static void TrySetClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch { try { Clipboard.SetDataObject(text, true); } catch { } }
    }

    // ========================= 底部工具条 =========================
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
                Font = new Font("Microsoft YaHei UI", 9F), Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = CCardLine;
            b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(accent) : Color.FromArgb(240, 242, 246);
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

    // ========================= 辅助 =========================
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
            ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9.5F),
            BackColor = Color.White, ForeColor = Color.FromArgb(31, 41, 55),
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
        public List<TestItem> Tests = new();
    }
    private class TestItem
    {
        public string Name = "", Value = "", Lolim = "", Hilim = "", Unit = "", Status = "";
    }

    private static ReportData Parse(string path)
    {
        var d = new ReportData();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };
        using var reader = XmlReader.Create(path, settings);
        bool panelSet = false, snSet = false;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            switch (reader.Name)
            {
                case "BATCH":
                    d.Timestamp = TimeUtil.Normalize(reader.GetAttribute("TIMESTAMP") ?? "");
                    break;
                case "FACTORY":
                    d.User = reader.GetAttribute("USER") ?? "";
                    d.Tester = reader.GetAttribute("TESTER") ?? "";
                    break;
                case "PANEL":
                    if (!panelSet) { d.PanelStatus = reader.GetAttribute("STATUS") ?? ""; panelSet = true; }
                    break;
                case "DUT":
                    if (!snSet) { d.Sn = reader.GetAttribute("ID") ?? ""; snSet = true; }
                    break;
                case "TEST":
                    var t = new TestItem
                    {
                        Name = reader.GetAttribute("NAME") ?? "",
                        Value = reader.GetAttribute("VALUE") ?? "",
                        Lolim = reader.GetAttribute("LOLIM") ?? "",
                        Hilim = reader.GetAttribute("HILIM") ?? "",
                        Unit = reader.GetAttribute("UNIT") ?? "",
                        Status = reader.GetAttribute("STATUS") ?? "",
                    };
                    d.Tests.Add(t);
                    if (t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) && !IsIgnored(t.Name))
                        d.FailCount++;
                    break;
            }
        }
        return d;
    }
}

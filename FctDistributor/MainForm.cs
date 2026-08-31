using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace FctDistributor;

public class MainForm : Form
{
    private const string Ver = "1.9.0";

    // ---- 数据来源
    private readonly RadioButton _rbSource = new();
    private readonly RadioButton _rbTemplate = new();

    // ---- 顶部输入
    private readonly Label _lblSrc = new();
    private readonly TextBox _txtSrc = new();
    private readonly Label _lblEmbedded = new();      // 模板直生模式下代替“模板目录”输入框
    private readonly TextBox _txtDst = new();
    private readonly Button _btnSrcBrowse = new();
    private readonly Button _btnDstBrowse = new();
    private readonly ComboBox _cbStation = new();
    private readonly CheckedListBox _clbStations = new();
    private readonly Label _lblDetect = new();
    private readonly Label _lblDateCap = new();
    private readonly CheckBox _chkDateFilter = new();
    private readonly DateTimePicker _dtFrom = new();
    private readonly DateTimePicker _dtTo = new();

    // ---- 模板生成参数
    private readonly GroupBox _boxGen = new();
    private readonly TextBox _txtWindow = new();
    private readonly TextBox _txtFloor = new();
    private readonly TextBox _txtGap = new();
    private readonly NumericUpDown _numJitter = new();
    private readonly NumericUpDown _numCopies = new();
    private readonly CheckBox _chkSkipSun = new();
    private readonly CheckBox _chkSkipSat = new();
    private readonly TextBox _txtSeed = new();
    private readonly CheckBox _chkForceUser = new();
    private readonly TextBox _txtUser = new();
    private readonly CheckBox _chkExistingOnly = new();

    // ---- 选项
    private readonly CheckBox _chkDryRun = new();
    private readonly CheckBox _chkOverwrite = new();
    private readonly CheckBox _chkCheckXml = new();
    private readonly CheckBox _chkStampDirs = new();
    private readonly CheckBox _chkMove = new();

    // ---- 动作
    private readonly Button _btnScan = new();
    private readonly Button _btnRun = new();
    private readonly Button _btnRestamp = new();
    private readonly Button _btnExport = new();
    private readonly Button _btnCancel = new();
    private readonly ProgressBar _progress = new();

    // ---- 成果面板
    private readonly Label _lblHeadline = new();
    private readonly ListView _lvResult = new();
    private readonly TextBox _txtLog = new();
    private readonly Label _lblStatus = new();

    private List<PlanItem> _plan = new();
    private bool _planExecuted;          // 计划是否已真正落盘（非预演）
    private CancellationTokenSource? _cts;

    // ---- 日志批量刷新：工作线程只入队，UI 定时批量 AppendText
    //      （原来每条日志一次 BeginInvoke，大数据量下会把消息队列打爆 -> 界面卡死、点不到中止）
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logQueue = new();
    private readonly System.Windows.Forms.Timer _logTimer = new() { Interval = 120 };
    private int _logLines;
    private int _lastPct = -1;
    private long _lastProgressTick;
    private string _lastSourceDir = "";

    public MainForm()
    {
        Text = $"Argus | FCT 数据分发器 v{Ver}";
        Width = 1180;
        Height = 906;
        MinimumSize = new Size(1040, 790);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        TryLoadIcon();

        _logTimer.Tick += (_, _) => FlushLog();
        _logTimer.Start();

        BuildSourceMode();
        BuildTop();
        BuildGenBox();
        BuildOptions();
        BuildActions();
        BuildResultArea();
        ApplyMode();

        Log($"FCT 数据分发器 v{Ver} 就绪");
        Log("模式一・源目录分发: {源}\\FCT{n}\\{yyyyMMdd}\\*.xml  —— 拷现成文件");
        Log("模式二・模板直生: 使用**内置模板**（已编进 exe），现场只需选目标根目录");
        Log("内置模板: " + EmbeddedTemplateSummary());
        Log("目标目录结构:   {目标}\\Offline\\{型号}\\{yyyyMMdd}\\*.xml（**只看 Offline**）");
        Log("模板直生默认「只投放到已有目录」: 只扫现有日期目录并把生成的 XML 放进去，不新建任何目录");
        Log("写入规则: 目录里已有**同 SN**文件 -> 跳过；没有 -> 加进去（不限目录内文件总数）");
        Log("分类规则: 固定 Offline —— 这两个文件（O_ 中断 / P_）不会出现在 Online；型号取 SN 前 8 位");
    }

    // ==================== UI 构建 ====================

    private void BuildSourceMode()
    {
        var box = new GroupBox
        {
            Text = "数据来源", Left = 12, Top = 8, Width = 1148, Height = 48,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _rbSource.Text = "源目录分发（已有 FCT_Batch，拷现成文件）";
        _rbSource.Left = 14; _rbSource.Top = 18; _rbSource.Width = 300;
        _rbSource.Checked = true;
        _rbSource.CheckedChanged += (_, _) => ApplyMode();

        _rbTemplate.Text = "模板直生（模板已内置，现场只选目标根目录）";
        _rbTemplate.Left = 330; _rbTemplate.Top = 18; _rbTemplate.Width = 380;
        _rbTemplate.ForeColor = Color.MidnightBlue;
        _rbTemplate.Font = new Font(Font, FontStyle.Bold);

        box.Controls.AddRange(new Control[] { _rbSource, _rbTemplate });
        Controls.Add(box);
    }

    private void BuildTop()
    {
        _lblSrc.Text = "源目录:"; _lblSrc.Left = 12; _lblSrc.Top = 65; _lblSrc.Width = 74;
        _lblSrc.TextAlign = ContentAlignment.MiddleRight;
        _txtSrc.Left = 92; _txtSrc.Top = 62; _txtSrc.Width = 830;
        _txtSrc.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _txtSrc.Text = GuessSourceRoot();
        _btnSrcBrowse.Text = "浏览..."; _btnSrcBrowse.Left = 930; _btnSrcBrowse.Top = 61; _btnSrcBrowse.Width = 90;
        _btnSrcBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnSrcBrowse.Click += (_, _) => Browse(_txtSrc, "选择 FCT_Batch 源目录");

        // 模板直生模式：模板已编进 exe，这里只显示不可改
        _lblEmbedded.Left = 92; _lblEmbedded.Top = 65; _lblEmbedded.Width = 930; _lblEmbedded.Height = 20;
        _lblEmbedded.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _lblEmbedded.ForeColor = Color.MidnightBlue;
        _lblEmbedded.TextAlign = ContentAlignment.MiddleLeft;
        _lblEmbedded.Text = "内置（已编进 exe，无需携带/选择模板目录）：" + EmbeddedTemplateSummary();
        _lblEmbedded.Visible = false;

        var lblDst = new Label { Text = "目标根目录:", Left = 12, Top = 97, Width = 74, TextAlign = ContentAlignment.MiddleRight };
        _txtDst.Left = 92; _txtDst.Top = 94; _txtDst.Width = 830;
        _txtDst.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _txtDst.Text = LoadDefaultResultsRoot();
        _btnDstBrowse.Text = "浏览..."; _btnDstBrowse.Left = 930; _btnDstBrowse.Top = 93; _btnDstBrowse.Width = 90;
        _btnDstBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnDstBrowse.Click += (_, _) => Browse(_txtDst, "选择结果根目录 (如 D:\\Results)");

        var lblSt = new Label { Text = "机台:", Left = 12, Top = 129, Width = 74, TextAlign = ContentAlignment.MiddleRight };
        _cbStation.Left = 92; _cbStation.Top = 126; _cbStation.Width = 110;
        _cbStation.DropDownStyle = ComboBoxStyle.DropDownList;
        _cbStation.Items.Add("全部机台");
        for (int i = 1; i <= 7; i++) _cbStation.Items.Add($"FCT{i}");
        _cbStation.SelectedIndex = 0;

        // 模板模式也用单选 —— 目标结构没有机台层级，多台会撑破「每日期目录最多 2 个」
        _clbStations.Left = 92; _clbStations.Top = 126; _clbStations.Width = 300; _clbStations.Height = 20;
        _clbStations.MultiColumn = true; _clbStations.ColumnWidth = 62;
        _clbStations.CheckOnClick = true;
        _clbStations.BorderStyle = BorderStyle.FixedSingle;
        _clbStations.IntegralHeight = false;
        for (int i = 1; i <= 7; i++) _clbStations.Items.Add($"FCT{i}");
        _clbStations.Visible = false;

        var detected = StationDetector.DetectStation();   // v3.0.0: 分发器自己那份（彻底分家，IP 表两份，自检盯一致性）
        _lblDetect.Left = 400; _lblDetect.Top = 129; _lblDetect.Width = 250; _lblDetect.Height = 20;
        if (detected != null)
        {
            _lblDetect.Text = $"● 本机识别为 {detected}（已自动选中）";
            _lblDetect.ForeColor = Color.SeaGreen;
            int idx = _cbStation.Items.IndexOf(detected);
            if (idx >= 0) _cbStation.SelectedIndex = idx;
            int j = _clbStations.Items.IndexOf(detected);
            if (j >= 0) _clbStations.SetItemChecked(j, true);
        }
        else
        {
            _lblDetect.Text = "○ 未能按 IP 识别，请手动选机台";
            _lblDetect.ForeColor = Color.DarkGray;
            _clbStations.SetItemChecked(0, true);
        }

        _chkDateFilter.Text = "启用日期过滤"; _chkDateFilter.Left = 656; _chkDateFilter.Top = 127; _chkDateFilter.Width = 106;
        _chkDateFilter.CheckedChanged += (_, _) =>
        {
            if (_rbSource.Checked) _dtFrom.Enabled = _dtTo.Enabled = _chkDateFilter.Checked;
        };
        _lblDateCap.Left = 656; _lblDateCap.Top = 129; _lblDateCap.Width = 66; _lblDateCap.Height = 20;
        _lblDateCap.Text = "生成日期:";
        _lblDateCap.Visible = false;

        _dtFrom.Left = 768; _dtFrom.Top = 126; _dtFrom.Width = 120; _dtFrom.Enabled = false;
        _dtFrom.Format = DateTimePickerFormat.Custom; _dtFrom.CustomFormat = "yyyy-MM-dd";
        _dtFrom.Value = DateTime.Today.AddDays(-30);
        var lblTo = new Label { Text = "~", Left = 892, Top = 129, Width = 14, TextAlign = ContentAlignment.MiddleCenter };
        _dtTo.Left = 908; _dtTo.Top = 126; _dtTo.Width = 120; _dtTo.Enabled = false;
        _dtTo.Format = DateTimePickerFormat.Custom; _dtTo.CustomFormat = "yyyy-MM-dd";
        _dtTo.Value = DateTime.Today;

        Controls.AddRange(new Control[]
        {
            _lblSrc, _txtSrc, _lblEmbedded, _btnSrcBrowse, lblDst, _txtDst, _btnDstBrowse,
            lblSt, _cbStation, _clbStations, _lblDetect,
            _chkDateFilter, _lblDateCap, _dtFrom, lblTo, _dtTo
        });
    }

    private void BuildGenBox()
    {
        _boxGen.Text = "模板生成参数";
        _boxGen.Left = 12; _boxGen.Top = 154; _boxGen.Width = 1148; _boxGen.Height = 78;
        _boxGen.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        int x = 12;
        _boxGen.Controls.Add(new Label { Text = "测试窗口:", Left = x, Top = 22, Width = 58, TextAlign = ContentAlignment.MiddleRight });
        _txtWindow.Left = x + 62; _txtWindow.Top = 19; _txtWindow.Width = 100; _txtWindow.Text = "08:00-11:45";
        _boxGen.Controls.Add(_txtWindow);

        x = 186;
        _boxGen.Controls.Add(new Label { Text = "BATCH下限:", Left = x, Top = 22, Width = 66, TextAlign = ContentAlignment.MiddleRight });
        _txtFloor.Left = x + 70; _txtFloor.Top = 19; _txtFloor.Width = 58; _txtFloor.Text = "08:00";
        _boxGen.Controls.Add(_txtFloor);

        x = 328;
        _boxGen.Controls.Add(new Label { Text = "提前(分):", Left = x, Top = 22, Width = 56, TextAlign = ContentAlignment.MiddleRight });
        _txtGap.Left = x + 60; _txtGap.Top = 19; _txtGap.Width = 62; _txtGap.Text = "1-15";
        _boxGen.Controls.Add(_txtGap);

        x = 464;
        _boxGen.Controls.Add(new Label { Text = "测试时长±(秒):", Left = x, Top = 22, Width = 86, TextAlign = ContentAlignment.MiddleRight });
        _numJitter.Left = x + 90; _numJitter.Top = 19; _numJitter.Width = 54;
        _numJitter.DecimalPlaces = 1; _numJitter.Minimum = 0; _numJitter.Maximum = 300;
        _numJitter.Increment = 1M; _numJitter.Value = 20M;
        _boxGen.Controls.Add(_numJitter);

        x = 620;
        _boxGen.Controls.Add(new Label { Text = "每模板/日:", Left = x, Top = 22, Width = 64, TextAlign = ContentAlignment.MiddleRight });
        _numCopies.Left = x + 68; _numCopies.Top = 19; _numCopies.Width = 48;
        _numCopies.Minimum = 1; _numCopies.Maximum = 1; _numCopies.Value = 1;
        _numCopies.Enabled = false;      // 业务硬规则：每模板每天只 1 个
        _boxGen.Controls.Add(_numCopies);

        _chkSkipSun.Text = "跳过周日"; _chkSkipSun.Left = 748; _chkSkipSun.Top = 21; _chkSkipSun.Width = 82;
        _chkSkipSun.Checked = true;
        _chkSkipSat.Text = "跳过周六"; _chkSkipSat.Left = 834; _chkSkipSat.Top = 21; _chkSkipSat.Width = 82;
        _boxGen.Controls.AddRange(new Control[] { _chkSkipSun, _chkSkipSat });

        _boxGen.Controls.Add(new Label { Text = "随机种子:", Left = 926, Top = 22, Width = 58, TextAlign = ContentAlignment.MiddleRight });
        _txtSeed.Left = 988; _txtSeed.Top = 19; _txtSeed.Width = 70;
        _txtSeed.PlaceholderText = "空=随机";
        _boxGen.Controls.Add(_txtSeed);

        // ---- 第二行：分类目录（固定 Offline）+ USER
        _boxGen.Controls.Add(new Label { Text = "分类目录:", Left = 12, Top = 48, Width = 58, TextAlign = ContentAlignment.MiddleRight });
        _boxGen.Controls.Add(new Label
        {
            Text = "Offline（固定）—— 这两个文件不会出现在 Online",
            Left = 74, Top = 48, Width = 250, Height = 20,
            ForeColor = Color.MidnightBlue, TextAlign = ContentAlignment.MiddleLeft,
        });

        _chkForceUser.Text = "改写 FACTORY@USER 为:";
        _chkForceUser.Left = 330; _chkForceUser.Top = 47; _chkForceUser.Width = 158;
        _chkForceUser.Checked = false;                 // 默认不改写，保留Debug，防误入库
        _chkForceUser.CheckedChanged += (_, _) => _txtUser.Enabled = _chkForceUser.Checked;
        _txtUser.Left = 490; _txtUser.Top = 44; _txtUser.Width = 90; _txtUser.Text = "Operator";
        _txtUser.Enabled = false;
        _boxGen.Controls.AddRange(new Control[] { _chkForceUser, _txtUser });
        new ToolTip { AutoPopDelay = 8000, InitialDelay = 400 }.SetToolTip(_chkForceUser, "默认不勾选，保留模板原值 Debug（会被 Aggregator 跳过）；需要入库时请手动勾选");
        new ToolTip { AutoPopDelay = 8000, InitialDelay = 400 }.SetToolTip(_txtUser, "勾选后生效的值，默认 Operator");

        _boxGen.Controls.Add(new Label
        {
            Text = "每模板每日 1 个・目录里有同 SN 就跳过・没有就加进去",
            Left = 964, Top = 47, Width = 184, Height = 20,
            ForeColor = Color.DimGray,
        });

        // ---- 只投放到已有目录（默认）：不新建任何目录
        _chkExistingOnly.Text = "只投放到已有目录(不新建)";
        _chkExistingOnly.Left = 592; _chkExistingOnly.Top = 47; _chkExistingOnly.Width = 176;
        _chkExistingOnly.Checked = true;
        _chkExistingOnly.ForeColor = Color.MidnightBlue;
        _chkExistingOnly.Font = new Font(Font, FontStyle.Bold);
        _chkExistingOnly.CheckedChanged += (_, _) =>
        {
            if (!_chkExistingOnly.Checked)
                Log("[注意] 已关闭「只投放到已有目录」：将按日期范围**新建** Offline\\{型号}\\{日期} 目录");
            else
                Log("[模式] 只往目标根下**已存在**的日期目录里放文件，不新建目录");
        };

        _boxGen.Controls.Add(new Label
        {
            Text = "型号固定 E3002781（SN 不改写）",
            Left = 776, Top = 48, Width = 186, Height = 20,
            ForeColor = Color.MidnightBlue, TextAlign = ContentAlignment.MiddleLeft,
        });
        _boxGen.Controls.Add(_chkExistingOnly);

        Controls.Add(_boxGen);
    }

    /// <summary>根据数据来源切换控件可用性与文案。</summary>
    private void ApplyMode()
    {
        bool tpl = _rbTemplate.Checked;

        // 模板直生：模板已编进 exe，不需要也不允许选模板目录；仅源目录模式用输入框
        if (tpl) _lastSourceDir = _txtSrc.Text;
        else _txtSrc.Text = !string.IsNullOrWhiteSpace(_lastSourceDir) ? _lastSourceDir : GuessSourceRoot();

        _lblSrc.Text = tpl ? "模板:" : "源目录:";
        _txtSrc.Visible = _btnSrcBrowse.Visible = !tpl;
        _lblEmbedded.Visible = tpl;
        _boxGen.Enabled = tpl;

        _cbStation.Visible = !tpl;
        _clbStations.Visible = tpl;
        _chkDateFilter.Visible = !tpl;
        _lblDateCap.Visible = tpl;

        if (tpl)
        {
            _dtFrom.Enabled = _dtTo.Enabled = true;
            _chkMove.Enabled = false;
            _chkMove.Checked = false;
            _chkCheckXml.Enabled = false;   // 生成的数据天然一致，无需交叉校验
        }
        else
        {
            _dtFrom.Enabled = _dtTo.Enabled = _chkDateFilter.Checked;
            _chkMove.Enabled = true;
            _chkCheckXml.Enabled = true;
        }

        _btnScan.Text = tpl ? "1. 扫模板生计划" : "1. 扫描生成计划";
        _btnRun.Text = tpl ? "2. 一键生成" : "2. 一键分发";
        _plan.Clear();
        _btnRun.Enabled = false;
    }

    private void BuildOptions()
    {
        var box = new GroupBox
        {
            Text = "选项", Left = 12, Top = 238, Width = 600, Height = 56,
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };

        _chkDryRun.Text = "预演(不写盘)"; _chkDryRun.Left = 12; _chkDryRun.Top = 22; _chkDryRun.Width = 110;
        _chkDryRun.Checked = true;
        _chkOverwrite.Text = "覆盖已存在"; _chkOverwrite.Left = 128; _chkOverwrite.Top = 22; _chkOverwrite.Width = 100;
        _chkCheckXml.Text = "校验XML时间"; _chkCheckXml.Left = 234; _chkCheckXml.Top = 22; _chkCheckXml.Width = 110;
        _chkCheckXml.Checked = true;
        _chkStampDirs.Text = "修目录时间"; _chkStampDirs.Left = 350; _chkStampDirs.Top = 22; _chkStampDirs.Width = 100;
        _chkStampDirs.Checked = true;
        _chkMove.Text = "移动(而非复制)"; _chkMove.Left = 456; _chkMove.Top = 22; _chkMove.Width = 120;
        _chkMove.ForeColor = Color.Firebrick;

        box.Controls.AddRange(new Control[] { _chkDryRun, _chkOverwrite, _chkCheckXml, _chkStampDirs, _chkMove });
        Controls.Add(box);
    }

    private void BuildActions()
    {
        _btnScan.Text = "1. 扫描生成计划"; _btnScan.Left = 620; _btnScan.Top = 252; _btnScan.Width = 120; _btnScan.Height = 32;
        _btnScan.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnScan.Click += async (_, _) => await ScanAsync();

        _btnRun.Text = "2. 一键分发"; _btnRun.Left = 748; _btnRun.Top = 252; _btnRun.Width = 104; _btnRun.Height = 32;
        _btnRun.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnRun.Enabled = false;
        _btnRun.Font = new Font(Font, FontStyle.Bold);
        _btnRun.Click += async (_, _) => await RunAsync();

        _btnRestamp.Text = "仅修复时间戳"; _btnRestamp.Left = 860; _btnRestamp.Top = 252; _btnRestamp.Width = 104; _btnRestamp.Height = 32;
        _btnRestamp.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnRestamp.BackColor = Color.FromArgb(250, 235, 238);   // 黑白红：改时间戳=谨慎操作，主红浅调底
        _btnRestamp.Click += async (_, _) => await RestampAsync();

        _btnExport.Text = "导出 xlsx"; _btnExport.Left = 972; _btnExport.Top = 252; _btnExport.Width = 116; _btnExport.Height = 32;
        _btnExport.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnExport.BackColor = Color.White;
        _btnExport.Click += (_, _) => ExportXlsx();

        _btnCancel.Text = "中止"; _btnCancel.Left = 1096; _btnCancel.Top = 252; _btnCancel.Width = 64; _btnCancel.Height = 32;
        _btnCancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnCancel.Enabled = false;
        _btnCancel.Click += (_, _) => _cts?.Cancel();

        _progress.Left = 12; _progress.Top = 300; _progress.Width = 1148; _progress.Height = 14;
        _progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        Controls.AddRange(new Control[] { _btnScan, _btnRun, _btnRestamp, _btnExport, _btnCancel, _progress });
    }

    private void BuildResultArea()
    {
        // ---- 成果标题栏
        _lblHeadline.Left = 12; _lblHeadline.Top = 320; _lblHeadline.Width = 1148; _lblHeadline.Height = 52;
        _lblHeadline.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _lblHeadline.BorderStyle = BorderStyle.FixedSingle;
        _lblHeadline.BackColor = Color.FromArgb(247, 247, 247);
        _lblHeadline.Padding = new Padding(8, 6, 8, 6);
        _lblHeadline.Font = new Font("Microsoft YaHei UI", 9.5F);
        _lblHeadline.Text = "选择数据来源、设好目录后，点【1. 扫描生成计划】";
        _lblHeadline.ForeColor = Color.DimGray;

        // ---- 左右分栏：成果列表 / 日志
        var split = new SplitContainer
        {
            Left = 12, Top = 380, Width = 1148, Height = 416,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Orientation = Orientation.Vertical,
            SplitterDistance = 660,
        };

        _lvResult.Dock = DockStyle.Fill;
        _lvResult.View = View.Details;
        _lvResult.FullRowSelect = true;
        _lvResult.GridLines = true;
        _lvResult.HideSelection = false;
        _lvResult.Columns.Add("机台", 60);
        _lvResult.Columns.Add("日期", 80);
        _lvResult.Columns.Add("类别", 60);
        _lvResult.Columns.Add("型号", 80);
        _lvResult.Columns.Add("时间", 90);
        _lvResult.Columns.Add("说明（同日期已合并）", 300);
        split.Panel1.Controls.Add(_lvResult);

        _txtLog.Dock = DockStyle.Fill;
        _txtLog.Multiline = true;
        _txtLog.ScrollBars = ScrollBars.Vertical;
        _txtLog.ReadOnly = true;
        _txtLog.BackColor = Color.FromArgb(17, 17, 17);   // 黑白红：日志窗墨黑底（与主程序侧栏同色）
        _txtLog.ForeColor = Color.Gainsboro;
        _txtLog.Font = new Font("Consolas", 8.5F);
        _txtLog.WordWrap = false;
        split.Panel2.Controls.Add(_txtLog);

        _lblStatus.Left = 12; _lblStatus.Top = 830; _lblStatus.Width = 1148; _lblStatus.Height = 20;
        _lblStatus.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _lblStatus.Text = "就绪";
        _lblStatus.ForeColor = Color.DimGray;

        Controls.AddRange(new Control[] { _lblHeadline, split, _lblStatus });
    }

    // ==================== 动作 ====================

    private async Task ScanAsync()
    {
        if (_rbTemplate.Checked) { await ScanTemplateAsync(); return; }

        if (!ValidateDirs(needTarget: true)) return;

        var opt = new Planner.Options
        {
            SourceRoot = _txtSrc.Text.Trim(),
            TargetRoot = _txtDst.Text.Trim(),
            StationFilter = _cbStation.SelectedIndex <= 0 ? null : (string)_cbStation.SelectedItem!,
            DateFrom = _chkDateFilter.Checked ? _dtFrom.Value : null,
            DateTo = _chkDateFilter.Checked ? _dtTo.Value : null,
            CheckXml = _chkCheckXml.Checked,
        };

        Busy(true);
        _lvResult.Items.Clear();
        Log("──────── 开始扫描 ────────");
        Log($"源: {opt.SourceRoot}");
        Log($"目标: {opt.TargetRoot}");
        Log($"机台: {opt.StationFilter ?? "全部"}" +
            (opt.DateFrom != null ? $"   日期: {opt.DateFrom:yyyy-MM-dd} ~ {opt.DateTo:yyyy-MM-dd}" : ""));
        if (opt.CheckXml) Log("已启用 XML 内容时间校验（会读取每个 XML，稍慢）");

        Planner.Result res;
        try
        {
            res = await Task.Run(() =>
                new Planner().Build(opt, Log, _cts!.Token), _cts!.Token);
        }
        catch (OperationCanceledException) { Log("[中止] 扫描已取消"); Busy(false); return; }
        catch (Exception ex) { Log($"[错误] {ex.Message}"); Busy(false); return; }

        _plan = res.Items;
        _planExecuted = false;
        foreach (var m in res.Messages) Log($"[提示] {m}");

        RenderPlan();
        Busy(false);
        _btnRun.Enabled = _plan.Any(i => i.Ok && !i.Skip);
    }

    private async Task ScanTemplateAsync()
    {
        if (!ValidateDirs(needTarget: true)) return;

        var stations = _clbStations.CheckedItems.Cast<string>().ToList();
        if (stations.Count == 0)
        {
            MessageBox.Show("请至少勾选一个机台。", "提示");
            return;
        }
        if (stations.Count > 1)
        {
            var r = MessageBox.Show(
                $"已勾选 {stations.Count} 个机台：{string.Join(", ", stations)}\n\n" +
                "但目标结构 Offline\\{型号}\\{日期}\\ 里没有机台层级，\n" +
                "多台只是文件名里的 FCT{n} 不同、**SN 完全相同**，\n" +
                "所以除第一台外其它机台会全部命中「同目录已有同 SN」而被跳过。\n\n" +
                "现场使用应只选本机机台。确定继续？",
                "机台选择提醒", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (r != DialogResult.OK) return;
        }
        if (!TryParseWindow(_txtWindow.Text, out var wf, out var wt)) return;
        if (!TryParseHhmm(_txtFloor.Text, out var floor, "BATCH下限")) return;
        if (!TryParseRange(_txtGap.Text, out var gLo, out var gHi)) return;
        if (_dtTo.Value.Date < _dtFrom.Value.Date)
        {
            MessageBox.Show("结束日期早于起始日期。", "提示");
            return;
        }

        int? seed = null;
        if (int.TryParse(_txtSeed.Text.Trim(), out var sv)) seed = sv;

        var o = new TemplateGenerator.Options
        {
            UseEmbeddedTemplates = true,          // 模板已编进 exe，不读外部目录
            TargetRoot = _txtDst.Text.Trim(),
            Stations = stations,
            DateFrom = _dtFrom.Value.Date,
            DateTo = _dtTo.Value.Date,
            SkipSunday = _chkSkipSun.Checked,
            SkipSaturday = _chkSkipSat.Checked,
            WindowFrom = wf,
            WindowTo = wt,
            SessionFloor = floor,
            SessionGapMinLo = gLo,
            SessionGapMinHi = gHi,
            TestTimeJitterSec = (double)_numJitter.Value,
            Seed = seed,
            ExistingDirsOnly = _chkExistingOnly.Checked,
            ForceUser = _chkForceUser.Checked,
            User = string.IsNullOrWhiteSpace(_txtUser.Text) ? "Operator" : _txtUser.Text.Trim(),
        };

        Busy(true);
        _lvResult.Items.Clear();
        Log("──────── 模板生成计划 ────────");
        Log("模板: 内置（已编进 exe，无需模板目录）");
        Log($"目标: {o.TargetRoot}");
        Log($"机台: {string.Join(",", stations)}   日期: {o.DateFrom:yyyy-MM-dd} ~ {o.DateTo:yyyy-MM-dd}" +
            $"{(o.SkipSunday ? " 跳周日" : "")}{(o.SkipSaturday ? " 跳周六" : "")}");
        Log(o.ExistingDirsOnly
            ? "目录模式: 只投放到已有目录（不新建）；SN 固定不改写 -> 只能进 Offline\\E3002781\\"
            : "目录模式: 按日期范围新建目录（旧行为）");
        Log($"测试窗口: {wf.H:D2}:{wf.M:D2}~{wt.H:D2}:{wt.M:D2}   " +
            $"BATCH: 测试前 {gLo:g}~{gHi:g} 分钟且不早于 {floor.H:D2}:{floor.M:D2}");

        TemplateGenerator.PlanResult pres;
        try
        {
            pres = await Task.Run(() =>
                new TemplateGenerator().BuildPlan(o, Log, _cts!.Token), _cts!.Token);
        }
        catch (OperationCanceledException) { Log("[中止] 已取消"); Busy(false); return; }
        catch (Exception ex) { Log($"[错误] {ex.Message}"); Busy(false); return; }

        _plan = pres.Items;
        _planExecuted = false;
        foreach (var m in pres.Messages) Log($"[提示] {m}");
        RenderPlan();
        Busy(false);
        _btnRun.Enabled = _plan.Any(i => i.Ok && !i.Skip);
    }

    private bool TryParseWindow(string txt, out (int H, int M) from, out (int H, int M) to)
    {
        from = to = (0, 0);
        var parts = txt.Trim().Split('-');
        if (parts.Length != 2)
        {
            MessageBox.Show("测试窗口格式应为 HH:MM-HH:MM，如 07:46-11:45", "提示");
            return false;
        }
        return TryParseHhmm(parts[0], out from, "窗口起始") &&
               TryParseHhmm(parts[1], out to, "窗口结束");
    }

    private bool TryParseHhmm(string txt, out (int H, int M) v, string what)
    {
        v = (0, 0);
        var m = Regex.Match(txt.Trim(), @"^(\d{1,2}):(\d{2})$");
        if (!m.Success)
        {
            MessageBox.Show($"{what} 格式应为 HH:MM，得到: {txt}", "提示");
            return false;
        }
        v = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        return true;
    }

    private bool TryParseRange(string txt, out double lo, out double hi)
    {
        lo = hi = 0;
        var parts = txt.Trim().Split('-');
        if (parts.Length == 1 && double.TryParse(parts[0], out lo)) { hi = lo; return true; }
        if (parts.Length == 2 && double.TryParse(parts[0], out lo) && double.TryParse(parts[1], out hi))
            return true;
        MessageBox.Show($"提前分钟数格式应为 MIN-MAX（如 1-15），得到: {txt}", "提示");
        return false;
    }

    private async Task RunAsync()
    {
        if (_plan.Count == 0) { MessageBox.Show("请先扫描生成计划。", "提示"); return; }
        if (!ValidateDirs(needTarget: true)) return;

        var o = new Distributor.Options
        {
            DryRun = _chkDryRun.Checked,
            Overwrite = _chkOverwrite.Checked,
            Move = _chkMove.Checked,
            StampFiles = true,
            StampFolders = _chkStampDirs.Checked,
            // 模板直生 + 只投放到已有目录：执行阶段也绝不新建目录
            NoCreateDirs = _rbTemplate.Checked && _chkExistingOnly.Checked,
        };

        if (!o.DryRun)
        {
            var valid = _plan.Count(i => i.Ok && !i.Skip);
            bool gen = _plan.Any(i => i.IsGenerated);
            var msg = new StringBuilder();
            msg.AppendLine(gen
                ? $"即将生成 {valid} 个文件到："
                : $"即将{(o.Move ? "移动" : "复制")} {valid} 个文件到：");
            msg.AppendLine($"  {_txtDst.Text.Trim()}");
            msg.AppendLine();
            msg.AppendLine($"覆盖已存在: {(o.Overwrite ? "是" : "否（跳过）")}");
            msg.AppendLine($"修改文件时间戳: 是（按文件名）");
            msg.AppendLine($"修改目录时间戳: {(o.StampFolders ? "是" : "否")}");
            if (o.NoCreateDirs) msg.AppendLine("目录: 只放进已存在的目录（不新建）");
            if (o.Move) msg.AppendLine("\n⚠ 移动模式会从源目录删除文件！");
            msg.AppendLine("\n确认执行？");
            if (MessageBox.Show(msg.ToString(), gen ? "确认生成" : "确认分发",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;
        }

        Busy(true);
        Log(o.DryRun ? "──────── 开始预演（不写盘）────────" : "──────── 开始分发 ────────");

        Distributor.Report rep;
        try
        {
            rep = await Task.Run(() => new Distributor().Run(
                _plan, o, Log, ReportProgress, _cts!.Token), _cts!.Token);
        }
        catch (OperationCanceledException) { Log("[中止] 分发已取消"); Busy(false); return; }
        catch (Exception ex) { Log($"[错误] {ex.Message}"); Busy(false); return; }

        // D-L7：有失败项不算「已执行」—— 导出台账不能把没落盘的文件写成已生成
        if (!o.DryRun && rep.Failed == 0) _planExecuted = true;
        RenderReport(rep, o.DryRun);
        Busy(false);
    }

    private async Task RestampAsync()
    {
        var root = _txtDst.Text.Trim();
        if (!Directory.Exists(root))
        {
            MessageBox.Show($"目标根目录不存在：\n{root}", "提示");
            return;
        }

        var exts = new[] { "xml", "csv", "log", "txt" };

        // ---- 先选范围：窗口弹出后在**后台线程**扫目录（可停），不卡 UI
        using (var dlg = new RestampScopeDialog(root, exts))
        {
            // 内嵌到主窗口后 this 不是顶层窗；注意 FindForm() 在 Form 自身上会返回 this，得用 TopLevelControl
        if (dlg.ShowDialog((IWin32Window)(TopLevelControl ?? this)) != DialogResult.OK) { Log("[修时间戳] 已取消（未选范围）"); return; }

            var dirs = dlg.SelectedDirs;
            bool recursive = dlg.Recursive;
            bool bubble = dlg.BubbleParents;

            if (MessageBox.Show(
                    "将按文件名里的时间戳重设下列范围内文件的创建/访问/修改时间：\n\n" +
                    dlg.Summary() + "\n" +
                    $"文件类型: {string.Join(" / ", exts)}\n" +
                    $"含子目录: {(recursive ? "是" : "否")}\n" +
                    $"修目录时间: {(_chkStampDirs.Checked ? "是" : "否")}" +
                    $"{(_chkStampDirs.Checked && bubble ? "（并向上冒泡到父目录）" : "")}\n\n确认执行？",
                    "确认修复时间戳", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            Busy(true);
            _lvResult.Items.Clear();
            Log("──────── 开始就地修复时间戳 ────────");
            Log($"根目录: {root}");
            Log($"范围: 已选 {dirs.Count} 个文件夹" +
                (recursive ? "（含子目录）" : "") +
                (_chkStampDirs.Checked && bubble ? "，目录时间向上冒泡" : ""));
            foreach (var d in dirs.Take(20)) Log($"        {Path.GetRelativePath(root, d)}");
            if (dirs.Count > 20) Log($"        … 其余 {dirs.Count - 20} 个");

            Distributor.Report rep;
            try
            {
                rep = await Task.Run(() => new Distributor().Restamp(
                    dirs, exts, recursive, _chkStampDirs.Checked, bubble, root,
                    Log, ReportProgress, _cts!.Token), _cts!.Token);
            }
            catch (OperationCanceledException) { Log("[中止] 已取消"); Busy(false); return; }
            catch (Exception ex) { Log($"[错误] {ex.Message}"); Busy(false); return; }

            bool stopped = _cts!.IsCancellationRequested;
            _lblHeadline.ForeColor = rep.Failed > 0 ? Color.Firebrick
                                   : stopped ? Color.DarkOrange : Color.SeaGreen;
            _lblHeadline.Text =
                $"{(stopped ? "时间戳修复已中止（以下是已完成部分）" : "时间戳修复完成")}    " +
                $"文件夹 {dirs.Count:N0} 个    共 {rep.Total:N0} 个文件    已改 {rep.StampedFiles:N0}    " +
                $"本来就对 {rep.Unchanged:N0}    跳过 {rep.Skipped:N0}    失败 {rep.Failed:N0}\r\n" +
                $"目录 {rep.StampedDirs}/{rep.TouchedDirs}    耗时 {rep.Elapsed.TotalSeconds:F1}s" +
                (rep.Total > 0 ? $"    平均 {rep.Total / Math.Max(0.001, rep.Elapsed.TotalSeconds):N0} 个/秒" : "");
            foreach (var e in rep.Errors.Take(200)) AddRow("", "", "", "", "", e, Color.Firebrick);
            Log($"完成: 已改 {rep.StampedFiles:N0}, 本来就对 {rep.Unchanged:N0}, 跳过 {rep.Skipped:N0}, " +
                $"失败 {rep.Failed:N0}, 目录 {rep.StampedDirs}/{rep.TouchedDirs}");
            Busy(false);
            _lblStatus.Text = stopped
                ? $"已中止：已处理 {rep.StampedFiles + rep.Unchanged:N0} 个文件"
                : $"时间戳已修复：已改 {rep.StampedFiles:N0}，本来就对 {rep.Unchanged:N0}，目录 {rep.StampedDirs:N0}";
        }
    }

    // ==================== 渲染 ====================

    private void RenderPlan()
    {
        _lvResult.BeginUpdate();
        _lvResult.Items.Clear();

        int ok = _plan.Count(i => i.Ok && !i.Skip);
        int skip = _plan.Count(i => i.Ok && i.Skip);
        int bad = _plan.Count(i => !i.Ok);
        int warn = _plan.Count(i => i.Ok && !i.Skip && i.Warning != null);
        int exists = _plan.Count(i => i.Ok && !i.Skip && i.TargetExists);

        // 按【日期 + 型号】合并成一行 —— 不再逐个文件列，一屏看得完
        var groups = _plan.Where(i => i.Ok)
            .GroupBy(i => (i.DateDir, i.Model, i.Category))
            .OrderBy(g => g.Key.DateDir, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Model, StringComparer.Ordinal);

        foreach (var g in groups)
        {
            var live = g.Where(i => !i.Skip).ToList();
            var skipped = g.Where(i => i.Skip).ToList();
            var stations = string.Join(",", g.Select(i => i.Station).Distinct().OrderBy(s => s));

            string time = live.Count switch
            {
                0 => "",
                1 => live[0].Stamp.ToString("HH:mm:ss"),
                _ => $"{live.Min(i => i.Stamp):HH:mm}~{live.Max(i => i.Stamp):HH:mm}",
            };

            var note = new StringBuilder();
            note.Append(live.Count > 0 ? $"待生成 {live.Count} 个文件" : "无待生成");
            if (skipped.Count > 0)
                note.Append($"    跳过 {skipped.Count} 个（{ShortReason(skipped[0].SkipReason)}）");
            int ex = live.Count(i => i.TargetExists);
            if (ex > 0) note.Append($"    目标已存在 {ex} 个");
            int w = live.Count(i => i.Warning != null);
            if (w > 0) note.Append($"    ⚠ {w} 个告警");

            var color = live.Count == 0 ? Color.SlateGray
                      : w > 0 ? Color.DarkOrange
                      : ex > 0 ? Color.DarkGoldenrod
                      : Color.Black;
            AddRow(stations, g.Key.DateDir, g.Key.Category, g.Key.Model, time, note.ToString(), color);
        }

        // 无效条目仍逐条列出（必须能看到具体是哪个文件、错在哪）
        foreach (var it in _plan.Where(i => !i.Ok).Take(200))
            AddRow(it.Station, it.DateDir, "", it.Model, "",
                   $"无效 — {it.FileName} → {it.Error}", Color.Firebrick);

        _lvResult.EndUpdate();

        var stationList = _plan.Where(i => i.Ok && !i.Skip).Select(i => i.Station).Distinct().OrderBy(s => s).ToList();
        var dates = _plan.Where(i => i.Ok && !i.Skip).Select(i => i.DateDir).Distinct().OrderBy(s => s).ToList();
        var models = _plan.Where(i => i.Ok && !i.Skip).Select(i => i.Model).Distinct().OrderBy(s => s).ToList();
        long bytes = _plan.Where(i => i.Ok && !i.Skip).Sum(i => i.Size);

        _lblHeadline.ForeColor = bad > 0 ? Color.Firebrick : Color.MidnightBlue;
        _lblHeadline.Text =
            $"计划就绪：待生成 {ok} 个文件（{Human(bytes)}）    跳过 {skip}    无效 {bad}    告警 {warn}    目标已存在 {exists}\r\n" +
            $"机台 [{string.Join(",", stationList)}]    " +
            $"日期 {dates.Count} 天" + (dates.Count > 0 ? $" [{dates[0]} ~ {dates[^1]}]" : "") + "    " +
            $"型号 [{string.Join(",", models)}]    全部 Offline    " +
            $"列表 {_lvResult.Items.Count} 行（同日期同型号已合并）";

        Log($"扫描完成: 待生成 {ok}, 跳过 {skip}, 无效 {bad}, 告警 {warn}, 目标已存在 {exists}");
        _lblStatus.Text = ok > 0
            ? "计划已生成（按日期+型号合并显示）—— 建议先保持【预演】勾选跑一次，确认无误后取消预演再执行"
            : "没有可分发的文件";
    }

    /// <summary>把跳过原因缩成一句话（合并行里只需看个大概，明细在日志区）。</summary>
    private static string ShortReason(string? reason)
    {
        var r = (reason ?? "").Trim();
        if (r.Contains("同 SN")) return "已有同 SN 文件";
        if (r.Contains("目录不存在")) return "目录不存在";
        if (r.Contains("型号")) return "型号不符";
        return r.Length <= 20 ? r : r[..20] + "…";
    }

    private void RenderReport(Distributor.Report rep, bool dry)
    {
        _lvResult.BeginUpdate();
        _lvResult.Items.Clear();

        // 按【日期 + 型号】合并（与计划列表一致的粒度）
        var doneGroups = _plan.Where(i => i.Ok && !i.Skip)
            .GroupBy(i => (i.DateDir, i.Model, i.Category))
            .OrderBy(g => g.Key.DateDir, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Model, StringComparer.Ordinal);
        var verb = dry ? "预演" : (_rbTemplate.Checked ? "已生成" : "已分发");
        foreach (var g in doneGroups)
        {
            var stations = string.Join(",", g.Select(i => i.Station).Distinct().OrderBy(s => s));
            AddRow(stations, g.Key.DateDir, g.Key.Category, g.Key.Model,
                   g.Count() == 1 ? g.First().Stamp.ToString("HH:mm:ss")
                                  : $"{g.Min(i => i.Stamp):HH:mm}~{g.Max(i => i.Stamp):HH:mm}",
                   $"{verb} {g.Count()} 个文件", dry ? Color.MidnightBlue : Color.SeaGreen);
        }

        // 跳过按日期合并
        foreach (var g in _plan.Where(i => i.Ok && i.Skip)
                               .GroupBy(i => (i.DateDir, i.Model))
                               .OrderBy(g => g.Key.DateDir, StringComparer.Ordinal))
            AddRow("", g.Key.DateDir, "", g.Key.Model, "",
                   $"跳过 {g.Count()} 个（{ShortReason(g.First().SkipReason)}）", Color.SlateGray);

        // 总计一行
        AddRow("合计", $"{rep.Dates.Count} 天", "Offline", $"{rep.Models.Count} 个型号", "",
               $"{verb} {rep.Copied} 个文件（{Human(rep.Bytes)}）    跳过 {rep.Skipped}    失败 {rep.Failed}",
               Color.Teal);

        // 错误
        foreach (var e in rep.Errors.Take(200))
            AddRow("", "", "", "", "", e, Color.Firebrick);

        _lvResult.EndUpdate();

        var tag = dry ? "预演完成（未写盘）" : (_rbTemplate.Checked ? "生成完成" : "分发完成");
        _lblHeadline.ForeColor = (rep.Failed > 0 || rep.Invalid > 0) ? Color.Firebrick : Color.SeaGreen;
        _lblHeadline.Text =
            $"{tag}    成功 {rep.Copied}    跳过 {rep.Skipped}    失败 {rep.Failed}    " +
            $"无效 {rep.Invalid}    告警 {rep.Warned}    共 {Human(rep.Bytes)}    耗时 {rep.Elapsed.TotalSeconds:F1}s\r\n" +
            $"时间戳: 文件 {rep.StampedFiles}    目录 {rep.StampedDirs}/{rep.TouchedDirs}    " +
            $"机台 [{string.Join(",", rep.ByStation.Keys.OrderBy(k => k))}]    " +
            $"日期 {rep.Dates.Count} 天" + (rep.Dates.Count > 0 ? $" [{rep.Dates.Min} ~ {rep.Dates.Max}]" : "") +
            $"    型号 [{string.Join(",", rep.Models)}]";

        Log($"{tag}: 成功 {rep.Copied}, 跳过 {rep.Skipped}, 失败 {rep.Failed}, 无效 {rep.Invalid}");
        _lblStatus.Text = dry
            ? "预演结束 —— 取消勾选【预演(不写盘)】后再点【2. 一键分发】即可真正执行"
            : $"已完成，用时 {rep.Elapsed.TotalSeconds:F1} 秒";

        if (!dry && rep.Failed == 0 && rep.Invalid == 0)
            MessageBox.Show(
                $"分发完成！\n\n" +
                $"文件: {rep.Copied} 个（{Human(rep.Bytes)}）\n" +
                $"跳过: {rep.Skipped} 个\n" +
                $"时间戳: 文件 {rep.StampedFiles} 个，目录 {rep.StampedDirs} 个\n" +
                $"机台: {string.Join(", ", rep.ByStation.Keys.OrderBy(k => k))}\n" +
                $"日期: {rep.Dates.Count} 天\n" +
                $"耗时: {rep.Elapsed.TotalSeconds:F1} 秒",
                "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void AddRow(string station, string date, string cat, string model,
                        string time, string note, Color color)
    {
        var lvi = new ListViewItem(new[] { station, date, cat, model, time, note })
        { ForeColor = color };
        _lvResult.Items.Add(lvi);
    }

    // ==================== 辅助 ====================

    /// <summary>内置模板一行摘要（数量 + 前缀）。</summary>
    private static string EmbeddedTemplateSummary()
    {
        try
        {
            var ts = TemplateGenerator.LoadEmbedded();
            if (ts.Count == 0) return "⚠ 未找到内置模板，请重新部署";
            return $"{ts.Count} 个模板  " +
                   string.Join("、", ts.Select(t => $"{t.Prefix}_ ({t.Stamps.Count} 个 TIMESTAMP, " +
                                                     $"{t.PanelTestTime:F1}s)"));
        }
        catch (Exception ex) { return "⚠ " + ex.Message; }
    }

    private bool ValidateDirs(bool needTarget)
    {
        // 模板直生：模板内置，无需源目录
        if (!_rbTemplate.Checked)
        {
            var src = _txtSrc.Text.Trim();
            if (!Directory.Exists(src))
            {
                MessageBox.Show($"源目录不存在：\n{src}", "提示");
                return false;
            }
        }
        if (needTarget && string.IsNullOrWhiteSpace(_txtDst.Text))
        {
            MessageBox.Show("请填写目标根目录。", "提示");
            return false;
        }
        return true;
    }

    // ==================== 导出 xlsx ====================

    /// <summary>
    /// 导出金银板 SN 台账：机台号 / 日期 / 金板 SN(Golden) / 银板 SN(Silvery)。
    /// 有计划时导出计划（这一批投放了什么）；没计划时扫目标目录（机台上实际有什么）。
    /// </summary>
    private async void ExportXlsx()
    {
        var target = _txtDst.Text.Trim();
        List<SnExporter.Board> boards;

        if (_plan.Any(i => i.Ok && !i.Skip))
        {
            boards = SnExporter.FromPlan(_plan, _planExecuted);
            Log($"[导出] 数据源 = 当前计划（{(_planExecuted ? "已执行落盘" : "尚未执行 / 仅预演")}），{boards.Count} 个文件");
        }
        else
        {
            if (!Directory.Exists(target))
            {
                MessageBox.Show("没有计划可导出，也找不到目标根目录：\n" + target +
                                "\n\n请先点【扫描】生成计划，或把目标根目录指向机台的结果目录。", "导出");
                return;
            }
            // D-L8：扫目标目录放后台线程（大目录不再冻结界面）
            _btnExport.Enabled = false;
            UseWaitCursor = true;
            try { boards = await Task.Run(() => SnExporter.FromTarget(target, Log)); }
            catch (Exception ex)
            {
                Log($"[导出] 扫描目标目录失败: {ex.Message}");
                MessageBox.Show("扫描目标目录失败：\n" + ex.Message, "导出");
                return;
            }
            finally
            {
                UseWaitCursor = false;
                _btnExport.Enabled = true;
            }
            Log($"[导出] 数据源 = 目标目录现有文件，{boards.Count} 个文件");
        }

        int gs = boards.Count(b => b.Kind != SnExporter.Kind.None);
        if (gs == 0)
        {
            MessageBox.Show("没有可导出的金/银板数据。\n\n" +
                            "台账只收这两块板：\n" +
                            $"  金板 Golden 　{SnExporter.SnGolden}\n" +
                            $"  银板 Silvery　{SnExporter.SnSilvery}\n\n" +
                            "先点【1. 扫模板生计划】，或确认目标根目录下确实有这两块板的 xml。", "导出");
            return;
        }

        using var sfd = new SaveFileDialog
        {
            Title = "导出金银板 SN 台账 (xlsx)",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            FileName = $"金银板SN台账_{DateTime.Now:yyyyMMdd_HHmm}.xlsx",
            InitialDirectory = Directory.Exists(target)
                ? target : Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            OverwritePrompt = true,
            AddExtension = true,
            DefaultExt = "xlsx",
        };
        if (sfd.ShowDialog() != DialogResult.OK) return;

        try
        {
            var rep = SnExporter.Export(sfd.FileName, boards, Log);
            Log($"[导出] 已写入 {rep.Path}");
            Log($"[导出] {rep.Rows} 行（机台×日期），金板 {rep.Golden} 块、银板 {rep.Silvery} 块" +
                (rep.Excluded > 0 ? $"，已排除非金银板 {rep.Excluded} 个" : ""));
            _lblStatus.Text = $"已导出 {rep.Rows} 行（金 {rep.Golden} / 银 {rep.Silvery}）→ {Path.GetFileName(rep.Path)}";

            var r = MessageBox.Show(
                $"导出完成\n\n" +
                $"文件：{rep.Path}\n" +
                $"台账：{rep.Rows} 行（机台号 / 日期 / 金板 SN / 银板 SN）\n" +
                $"金板 Golden：{rep.Golden} 块\n" +
                $"银板 Silvery：{rep.Silvery} 块\n" +
                (rep.Excluded > 0 ? $"已排除非金银板：{rep.Excluded} 个\n" : "") +
                $"\n现在打开吗？",
                "导出完成", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(rep.Path)
                { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"[导出] 失败: {ex.Message}");
            MessageBox.Show("导出失败：\n" + ex.Message +
                            "\n\n（若文件正被 Excel 打开，先关闭再试）", "导出");
        }
    }

    private void Busy(bool on)
    {
        if (on)
        {
            _cts = new CancellationTokenSource();
            _progress.Value = 0;
            _lastPct = -1;
            _lastProgressTick = 0;
        }
        else FlushLogAll();
        _btnScan.Enabled = _btnRestamp.Enabled = _btnExport.Enabled = !on;
        _btnRun.Enabled = !on && _plan.Any(i => i.Ok && !i.Skip);
        _btnCancel.Enabled = on;
        UseWaitCursor = on;
        if (!on) _progress.Value = 0;
    }

    /// <summary>
    /// 进度限流：只有百分比变了、且距上次刷新 >= 80ms 才真的 BeginInvoke。
    /// 大数据量下原来的“每文件一次 BeginInvoke”是卡死主因之一。
    /// </summary>
    private void ReportProgress(int done, int total)
    {
        if (total <= 0) return;
        int pct = Math.Min(100, (int)(done * 100L / total));

        var now = Environment.TickCount64;
        if (pct == _lastPct && now - _lastProgressTick < 400) return;
        if (now - _lastProgressTick < 80 && pct != 100) return;
        _lastPct = pct;
        _lastProgressTick = now;

        if (_progress.InvokeRequired) _progress.BeginInvoke(() => SetPct(pct));
        else SetPct(pct);
    }

    private void SetPct(int pct)
    {
        _progress.Value = pct;
        _lblStatus.Text = pct >= 100 ? _lblStatus.Text : $"处理中… {pct}%   （可随时点【中止】）";
    }

    /// <summary>
    /// 只入队，绝不碰 UI —— 工作线程可以随便刷。UI 由 <see cref="FlushLog"/> 每 120ms 批量取。
    /// </summary>
    private void Log(string msg)
    {
        _logQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] {msg}");
        if (_logQueue.Count > 20000) _logQueue.TryDequeue(out _);   // 极端情况下丢最老的，绝不涨爆内存
    }

    /// <summary>UI 线程：一次取一批拼成一个字符串写进去（AppendText 调用次数从 N 降到 1）。</summary>
    private void FlushLog()
    {
        if (_logQueue.IsEmpty) return;

        var sb = new StringBuilder(4096);
        int n = 0;
        while (n < 400 && _logQueue.TryDequeue(out var line))
        {
            sb.Append(line).Append(Environment.NewLine);
            n++;
        }
        if (n == 0) return;

        _logLines += n;
        if (_logLines > 4000)          // 用计数器判断，不再用 _txtLog.Lines（那是 O(n) 分配）
        {
            _txtLog.Clear();
            _logLines = n;
            _txtLog.AppendText("（日志过长，已清屏，仅保留最近部分）" + Environment.NewLine);
        }
        _txtLog.AppendText(sb.ToString());
    }

    /// <summary>立刻把队列里剩下的都刷出来（收尾时用）。</summary>
    private void FlushLogAll()
    {
        for (int i = 0; i < 200 && !_logQueue.IsEmpty; i++) FlushLog();
    }

    private void Browse(TextBox target, string desc)
    {
        using var d = new FolderBrowserDialog { Description = desc, UseDescriptionForTitle = true };
        if (Directory.Exists(target.Text.Trim())) d.SelectedPath = target.Text.Trim();
        if (d.ShowDialog() == DialogResult.OK) target.Text = d.SelectedPath;
    }

    private static string Human(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):F2} GB" :
        b >= 1L << 20 ? $"{b / (double)(1L << 20):F1} MB" :
        b >= 1L << 10 ? $"{b / (double)(1L << 10):F0} KB" : $"{b} B";

    /// <summary>猜测源目录：程序目录下的 FCT_Batch，或上级目录。</summary>
    private static string GuessSourceRoot()
    {
        var b = AppContext.BaseDirectory;
        foreach (var cand in new[]
        {
            Path.Combine(b, "FCT_Batch"),
            Path.Combine(b, "..", "FCT_Batch"),
            Path.Combine(b, "..", "..", "FCT_Batch"),
        })
        {
            if (Directory.Exists(cand)) return Path.GetFullPath(cand);
        }
        return Path.Combine(b, "FCT_Batch");
    }

    /// <summary>猜测模板目录：程序目录下的 templates，否则上级目录的 source。</summary>
    /// <summary>读同目录 config.json 的 results_root，否则 D:\Results。</summary>
    private static string LoadDefaultResultsRoot()
    {
        try
        {
            var cfg = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(cfg))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
                if (doc.RootElement.TryGetProperty("results_root", out var v))
                {
                    var s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s!;
                }
            }
        }
        catch (Exception ex)
        {
            // D-L9：config.json 损坏不再静默吞掉 —— 至少留痕，现场排查「改了路径不生效」有据可查
            Console.Error.WriteLine($"[配置] 读取 config.json 失败，回落默认结果根目录: {ex.Message}");
        }
        return @"D:\Results";
    }

    private void TryLoadIcon()
    {
        try
        {
            AppIcon.Apply(this);   // v3.0.0: 分发器自己的 AppIcon（嵌入资源优先，已含文件兵底）
        }
        catch { }
    }
}



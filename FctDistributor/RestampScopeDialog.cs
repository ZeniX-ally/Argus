using System.Text;

namespace FctDistributor;

/// <summary>
/// 「修时间戳」的范围选择对话框：列出根目录下**所有直接含目标文件的文件夹**，
/// 勾选哪些就只修哪些。支持关键字过滤、日期范围快速勾选、全选/清空/反选。
///
/// 大数据量注意点：
///   ① 扫目录在**后台线程**跑，随时可取消，窗口不会卡住
///   ② 一次枚举搞定「文件数 + 最新时间」，用 FileInfo 缓存属性，不额外 stat
///   ③ 列表最多显示 <see cref="MaxDisplay"/> 项（勾选/全选仍按过滤结果全量生效）
/// </summary>
public sealed class RestampScopeDialog : Form
{
    /// <summary>列表最多显示多少项（再多就靠关键字过滤，CheckedListBox 塞几万项会很卡）。</summary>
    public const int MaxDisplay = 3000;

    /// <summary>一个候选文件夹。</summary>
    public sealed class DirItem
    {
        public string Path = "";
        public string Rel = "";
        public int Files;
        public DateTime Latest;          // 目录内文件的最新 mtime（给人判断用）
        public string DateGuess = "";    // 目录名是 yyyyMMdd 时取出来，供日期范围筛选

        public override string ToString() =>
            $"{Rel}    —    {Files} 个文件" +
            (Latest == default ? "" : $"，最新 {Latest:yyyy-MM-dd HH:mm}");
    }

    private readonly string _root;
    private readonly string[] _exts;
    private readonly List<DirItem> _all = new();
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    private bool _binding;               // 正在绑定列表（屏蔽 ItemCheck 回写）

    private readonly CheckedListBox _list = new();
    private readonly TextBox _txtFilter = new();
    private readonly Label _lblStatus = new();
    private readonly CheckBox _chkRecursive = new();
    private readonly CheckBox _chkBubble = new();
    private readonly DateTimePicker _dtFrom = new();
    private readonly DateTimePicker _dtTo = new();
    private readonly Button _btnOk = new();
    private readonly Button _btnStopScan = new();

    private CancellationTokenSource? _scanCts;
    private bool _scanning;

    /// <summary>勾选的文件夹（确定后有效）。</summary>
    public List<string> SelectedDirs { get; private set; } = new();
    /// <summary>是否连所选文件夹的子目录一起处理。</summary>
    public bool Recursive => _chkRecursive.Checked;
    /// <summary>是否把时间往上冒泡到父目录（型号 / 类别 / 根）。</summary>
    public bool BubbleParents => _chkBubble.Checked;

    public RestampScopeDialog(string root, string[] exts)
    {
        _root = root;
        _exts = exts;

        Text = "选择要修时间戳的文件夹";
        Width = 900;
        Height = 620;
        MinimumSize = new Size(700, 480);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = true;

        BuildUi();
        Shown += (_, _) => BeginScan();          // 扫描放到窗口显示之后，且在后台线程
        FormClosing += (_, _) => _scanCts?.Cancel();
    }

    private void BuildUi()
    {
        var lblRoot = new Label
        {
            Left = 12, Top = 10, Width = 850, Height = 20,
            Text = $"根目录：{_root}        文件类型：{string.Join(" / ", _exts)}",
            ForeColor = Color.MidnightBlue,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        // ---- 过滤 + 日期范围
        var lblF = new Label { Left = 12, Top = 40, Width = 58, Text = "关键字:", TextAlign = ContentAlignment.MiddleRight };
        _txtFilter.Left = 74; _txtFilter.Top = 37; _txtFilter.Width = 200;
        _txtFilter.PlaceholderText = "型号 / 日期 / 路径片段";
        _txtFilter.TextChanged += (_, _) => Rebind();

        var lblD = new Label { Left = 290, Top = 40, Width = 58, Text = "日期:", TextAlign = ContentAlignment.MiddleRight };
        _dtFrom.Left = 352; _dtFrom.Top = 37; _dtFrom.Width = 108; _dtFrom.Format = DateTimePickerFormat.Short;
        var lblT = new Label { Left = 462, Top = 40, Width = 16, Text = "~", TextAlign = ContentAlignment.MiddleCenter };
        _dtTo.Left = 480; _dtTo.Top = 37; _dtTo.Width = 108; _dtTo.Format = DateTimePickerFormat.Short;

        var btnByDate = new Button { Left = 596, Top = 36, Width = 108, Height = 25, Text = "按日期勾选" };
        btnByDate.Click += (_, _) => CheckByDate();

        var btnClearFilter = new Button { Left = 710, Top = 36, Width = 64, Height = 25, Text = "清过滤" };
        btnClearFilter.Click += (_, _) => _txtFilter.Clear();

        _btnStopScan.Left = 780; _btnStopScan.Top = 36; _btnStopScan.Width = 92; _btnStopScan.Height = 25;
        _btnStopScan.Text = "停止扫描";
        _btnStopScan.Enabled = false;
        _btnStopScan.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnStopScan.Click += (_, _) => _scanCts?.Cancel();

        // ---- 列表
        _list.Left = 12; _list.Top = 68; _list.Width = 860; _list.Height = 420;
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _list.CheckOnClick = true;
        _list.IntegralHeight = false;
        _list.ItemCheck += (_, e) =>
        {
            if (_binding) return;                                   // Rebind 里的程序化勾选不算用户操作
            if (e.Index < 0 || e.Index >= _list.Items.Count) return;
            if (_list.Items[e.Index] is not DirItem it) return;
            // ItemCheck 在状态变更前触发，用 NewValue
            if (e.NewValue == CheckState.Checked) _checked.Add(it.Path);
            else _checked.Remove(it.Path);
            // 窗口句柄未创建时不能 BeginInvoke（构造阶段就会走到这）
            if (IsHandleCreated) BeginInvoke(UpdateStatus); else UpdateStatus();
        };

        // ---- 底部按钮
        var btnAll = new Button { Left = 12, Top = 498, Width = 76, Height = 28, Text = "全选" };
        btnAll.Click += (_, _) => SetFilteredChecked(true);
        var btnNone = new Button { Left = 94, Top = 498, Width = 76, Height = 28, Text = "清空" };
        btnNone.Click += (_, _) => SetFilteredChecked(false);
        var btnInv = new Button { Left = 176, Top = 498, Width = 76, Height = 28, Text = "反选" };
        btnInv.Click += (_, _) => InvertFiltered();

        _chkRecursive.Left = 264; _chkRecursive.Top = 502; _chkRecursive.Width = 150;
        _chkRecursive.Text = "含所选目录的子目录";

        _chkBubble.Left = 420; _chkBubble.Top = 502; _chkBubble.Width = 190;
        _chkBubble.Text = "同时更新父目录时间";
        _chkBubble.Checked = true;

        _lblStatus.Left = 12; _lblStatus.Top = 532; _lblStatus.Width = 620; _lblStatus.Height = 34;
        _lblStatus.ForeColor = Color.DimGray;
        _lblStatus.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        _btnOk.Left = 690; _btnOk.Top = 528; _btnOk.Width = 90; _btnOk.Height = 32;
        _btnOk.Text = "确定"; _btnOk.DialogResult = DialogResult.OK;
        _btnOk.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _btnOk.Font = new Font(Font, FontStyle.Bold);
        _btnOk.Click += (_, _) =>
        {
            SelectedDirs = _all.Where(d => _checked.Contains(d.Path)).Select(d => d.Path).ToList();
            if (SelectedDirs.Count == 0)
            {
                MessageBox.Show("一个文件夹都没勾选。", "提示");
                DialogResult = DialogResult.None;
            }
        };
        var btnCancel = new Button { Left = 786, Top = 528, Width = 86, Height = 32, Text = "取消", DialogResult = DialogResult.Cancel };
        btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

        AcceptButton = _btnOk;
        CancelButton = btnCancel;

        Controls.AddRange(new Control[]
        {
            lblRoot, lblF, _txtFilter, lblD, _dtFrom, lblT, _dtTo, btnByDate, btnClearFilter, _btnStopScan,
            _list, btnAll, btnNone, btnInv, _chkRecursive, _chkBubble, _lblStatus, _btnOk, btnCancel,
        });
    }

    // ==================== 扫描（后台线程，可取消） ====================

    /// <summary>一次枚举收集「每目录的文件数 + 最新时间」。不在 UI 线程跑。
    /// D-M2 加固：取消**不再抛 OCE**，而是返回已扫到的部分结果 ——
    /// 「停止扫描」立即用部分结果出列表（旧实现会丢弃部分结果再不可取消地全量重扫一遍）。</summary>
    public static List<DirItem> ScanCore(string root, string[] exts,
                                         Action<long, int>? progress = null,
                                         CancellationToken ct = default)
        => Scan(root, exts, progress, ct).Items;

    /// <summary>扫描核心：返回（部分或完整结果, 是否被取消）。取消检查点在每 512 个文件。</summary>
    public static (List<DirItem> Items, bool Canceled) Scan(string root, string[] exts,
                                         Action<long, int>? progress = null,
                                         CancellationToken ct = default)
    {
        var extSet = new HashSet<string>(exts, StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, DirItem>(StringComparer.OrdinalIgnoreCase);
        var opt = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        long scanned = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var di = new DirectoryInfo(root);
        if (!di.Exists) return (new List<DirItem>(), false);

        foreach (var f in di.EnumerateFiles("*", opt))
        {
            if ((++scanned & 0x1FF) == 0)
            {
                if (ct.IsCancellationRequested)
                    return (Order(map), true);
                if (sw.ElapsedMilliseconds >= 250) { progress?.Invoke(scanned, map.Count); sw.Restart(); }
            }
            if (!extSet.Contains(f.Extension.TrimStart('.'))) continue;

            var dir = f.DirectoryName;
            if (string.IsNullOrEmpty(dir)) continue;
            if (!map.TryGetValue(dir, out var it))
            {
                var name = Path.GetFileName(dir);
                it = new DirItem
                {
                    Path = dir,
                    Rel = RelOf(root, dir),
                    DateGuess = NameParser.IsDateDir(name) ? name : "",
                };
                map[dir] = it;
            }
            it.Files++;
            // FileInfo 的时间是枚举时一起拿到的缓存值，读它不额外产生系统调用
            DateTime t;
            try { t = f.LastWriteTime; } catch { t = default; }
            if (t > it.Latest) it.Latest = t;
        }
        progress?.Invoke(scanned, map.Count);

        return (Order(map), false);

        static List<DirItem> Order(Dictionary<string, DirItem> m) =>
            m.Values.OrderBy(d => d.Rel, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string RelOf(string root, string dir)
    {
        try
        {
            var r = Path.GetRelativePath(root, dir);
            return r == "." ? "(根目录本身)" : r;
        }
        catch { return dir; }
    }

    private async void BeginScan()
    {
        _scanning = true;
        _btnOk.Enabled = false;
        _btnStopScan.Enabled = true;
        _lblStatus.Text = "正在扫描文件夹…（可点【停止扫描】用已扫到的部分）";
        UseWaitCursor = true;

        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        long lastFiles = 0;
        var report = new Progress<(long files, int dirs)>(p =>
        {
            if (IsDisposed || Disposing) return;   // D-M2：窗体已关，不再碰控件
            lastFiles = p.files;
            _lblStatus.Text = $"正在扫描…已扫 {p.files:N0} 个文件，找到 {p.dirs:N0} 个文件夹" +
                              "\r\n（可点【停止扫描】用已扫到的部分，或直接等它扫完）";
        });

        List<DirItem> result;
        bool canceled;
        try
        {
            // D-M2：取消返回部分结果（不再抛 OCE 后全量重扫一遍）
            (result, canceled) = await Task.Run(() => Scan(_root, _exts,
                (files, dirs) => ((IProgress<(long, int)>)report).Report((files, dirs)), ct));
        }
        catch (Exception ex)
        {
            if (IsDisposed || Disposing) return;   // D-M2：续体先查释放，防 ObjectDisposedException 崩进程
            UseWaitCursor = false;
            _scanning = false;
            _btnStopScan.Enabled = false;
            _btnOk.Enabled = true;
            MessageBox.Show("扫描文件夹出错：\n" + ex.Message, "修时间戳");
            return;
        }

        if (IsDisposed || Disposing) return;       // D-M2：扫描期间窗口被关 → 直接退出，不碰已释放控件

        _all.Clear();
        _all.AddRange(result);
        _checked.Clear();
        foreach (var d in _all) _checked.Add(d.Path);      // 默认全选

        var dates = _all.Where(d => d.DateGuess.Length == 8)
                        .Select(d => d.DateGuess).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (dates.Count > 0)
        {
            if (TryDate(dates[0], out var lo)) _dtFrom.Value = lo;
            if (TryDate(dates[^1], out var hi)) _dtTo.Value = hi;
        }

        UseWaitCursor = false;
        _scanning = false;
        _btnStopScan.Enabled = false;
        _btnOk.Enabled = true;
        Rebind();
        if (canceled)
            _lblStatus.Text = "已停止扫描 —— 使用已扫到的部分结果。\r\n" + _lblStatus.Text;
    }

    private static bool TryDate(string yyyymmdd, out DateTime dt) =>
        DateTime.TryParseExact(yyyymmdd, "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out dt);

    // ==================== 列表绑定 ====================

    /// <summary>当前关键字过滤命中的全部项（不受显示条数限制）。</summary>
    private IEnumerable<DirItem> Filtered()
    {
        var kw = _txtFilter.Text.Trim();
        return kw.Length == 0
            ? _all
            : _all.Where(d => d.Rel.Contains(kw, StringComparison.OrdinalIgnoreCase));
    }

    private void Rebind()
    {
        var filtered = Filtered().ToList();
        _binding = true;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var d in filtered.Take(MaxDisplay))
            {
                int i = _list.Items.Add(d);
                _list.SetItemChecked(i, _checked.Contains(d.Path));
            }
        }
        finally
        {
            _list.EndUpdate();
            _binding = false;
        }
        UpdateStatus(filtered.Count);
    }

    private void UpdateStatus() => UpdateStatus(null);

    private void UpdateStatus(int? filteredCount)
    {
        int fc = filteredCount ?? Filtered().Count();
        var sel = _all.Where(d => _checked.Contains(d.Path)).ToList();
        var more = fc > MaxDisplay ? $"，仅显示前 {MaxDisplay:N0} 项（请用关键字过滤）" : "";
        _lblStatus.Text =
            $"共 {_all.Count:N0} 个文件夹，过滤命中 {fc:N0} 个{more}    " +
            $"已勾选 {sel.Count:N0} 个 / {sel.Sum(d => (long)d.Files):N0} 个文件\r\n" +
            "提示：默认全部勾选 —— 想全修就直接点【确定】；只修部分就先【清空】再勾。" +
            "【全选/清空/反选】作用于当前过滤命中的**全部**项。";
    }

    /// <summary>对当前过滤命中的全部项（不只是显示出来的）勾选 / 取消。</summary>
    private void SetFilteredChecked(bool on)
    {
        foreach (var it in Filtered())
        {
            if (on) _checked.Add(it.Path); else _checked.Remove(it.Path);
        }
        Rebind();
    }

    private void InvertFiltered()
    {
        foreach (var it in Filtered().ToList())
        {
            if (_checked.Contains(it.Path)) _checked.Remove(it.Path);
            else _checked.Add(it.Path);
        }
        Rebind();
    }

    /// <summary>按日期范围勾选（只影响目录名是 yyyyMMdd 的项；其它项不动）。</summary>
    private void CheckByDate()
    {
        if (_scanning) { MessageBox.Show("还在扫描，稍等一下。", "提示"); return; }

        var lo = _dtFrom.Value.Date;
        var hi = _dtTo.Value.Date;
        if (hi < lo) (lo, hi) = (hi, lo);

        int hit = 0, noDate = 0;
        foreach (var d in _all)
        {
            if (d.DateGuess.Length != 8 || !TryDate(d.DateGuess, out var dt)) { noDate++; continue; }
            bool inRange = dt >= lo && dt <= hi;
            if (inRange) { _checked.Add(d.Path); hit++; }
            else _checked.Remove(d.Path);
        }
        Rebind();
        MessageBox.Show($"按日期 {lo:yyyy-MM-dd} ~ {hi:yyyy-MM-dd} 勾选了 {hit} 个文件夹。" +
                        (noDate > 0 ? $"\n（另有 {noDate} 个目录名不是 yyyyMMdd，未参与，保持原状）" : ""),
                        "按日期勾选");
    }

    // ==================== 自检用接口（不走 UI 也能验证扫目录/过滤/勾选） ====================

    /// <summary>扫到的全部候选文件夹。</summary>
    public IReadOnlyList<DirItem> AllItems => _all;
    /// <summary>当前勾选数。</summary>
    public int CheckedCount => _checked.Count;
    /// <summary>当前列表里可见的项数。</summary>
    public int VisibleCount => _list.Items.Count;
    /// <summary>直接灌入扫描结果（自检用，跳过后台扫描）。</summary>
    public void LoadItemsForTest(IEnumerable<DirItem> items)
    {
        _all.Clear();
        _all.AddRange(items);
        _checked.Clear();
        foreach (var d in _all) _checked.Add(d.Path);
        Rebind();
    }
    /// <summary>设过滤关键字并重绑（自检用）。</summary>
    public void SetFilter(string kw) { _txtFilter.Text = kw; }
    /// <summary>对当前过滤命中的项全选 / 清空（自检用）。</summary>
    public void SetVisibleChecked(bool on) => SetFilteredChecked(on);
    /// <summary>取当前勾选的路径（自检用）。</summary>
    public List<string> CheckedPaths() =>
        _all.Where(d => _checked.Contains(d.Path)).Select(d => d.Path).ToList();

    /// <summary>把勾选结果整理成一段人能读的摘要（确认框里用）。</summary>
    public string Summary()
    {
        var sel = _all.Where(d => _checked.Contains(d.Path)).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"文件夹 {sel.Count:N0} 个，文件 {sel.Sum(d => (long)d.Files):N0} 个");
        foreach (var d in sel.Take(8)) sb.AppendLine($"  {d.Rel}  ({d.Files})");
        if (sel.Count > 8) sb.AppendLine($"  … 其余 {sel.Count - 8:N0} 个");
        return sb.ToString();
    }
}

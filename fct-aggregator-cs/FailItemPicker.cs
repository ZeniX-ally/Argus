using System.Text;

namespace FctAggregator;

/// <summary>
/// 「从 FAIL 记录里挑故障项」对话框 —— 维修记录的故障项目来源。
///
/// 设计要点：
///   · **去重**：同一个失败项只出现一行，带出现次数 / 最近出现 / 涉及型号·机台
///   · **不显示 SN**：维修记录只关心「哪个测试项坏了」，SN 与它无关
///   · 打开就有内容：先用库里现成的 `fail_reason`（每条记录的**首个**失败项）秒出列表；
///     再在后台深扫 XML 把**其余失败项**补进来（可取消，进度限流，不卡界面）
///   · 勾一个或多个 → 确定 → 交给 MaintenanceForm 批量建单
/// </summary>
public class FailItemPickerForm : Form
{
    /// <summary>勾选的故障项（确定后有效）。</summary>
    public List<string> SelectedItems { get; private set; } = new();

    private sealed class Stat
    {
        public string Item = "";
        public int Count;
        public string LastSeen = "";
        public readonly SortedSet<string> Models = new(StringComparer.OrdinalIgnoreCase);
        public readonly SortedSet<string> Stations = new(StringComparer.OrdinalIgnoreCase);
        public bool FromXml;            // 是否由深扫 XML 补出来的（库里 fail_reason 没有）
    }

    private readonly Engine _engine;
    private readonly Dictionary<string, Stat> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);

    private readonly ListView _list = new();
    private readonly TextBox _filter = new();
    private readonly Label _status = new();
    private readonly Button _btnDeep = new();
    private readonly Button _btnOk = new();
    private readonly CheckBox _onlyThisStation = new();

    private CancellationTokenSource? _deepCts;
    private bool _busy;
    private bool _binding;

    public FailItemPickerForm(Engine engine)
    {
        _engine = engine;

        Text = "选择故障项（来自 FAIL 记录，已去重）";
        Width = 880;
        Height = 620;
        MinimumSize = new Size(680, 460);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        ShowInTaskbar = false;
        MinimizeBox = false;

        BuildUi();
        Shown += (_, _) => LoadFast();
        FormClosing += (_, _) => _deepCts?.Cancel();
    }

    private void BuildUi()
    {
        var tip = new Label
        {
            Left = 12, Top = 10, Width = 840, Height = 34,
            Text = "勾选一个或多个故障项 → 【确定】即可写入维修记录的「故障项目」。\r\n" +
                   "统计范围：近 " + ScanDays + " 天内的 FAIL（每条记录只取第一条失败项，已按故障项去重）；「深扫 XML」从源文件复核一遍。",
            ForeColor = Color.FromArgb(90, 100, 115),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        var lblF = new Label { Left = 12, Top = 50, Width = 44, Text = "过滤:", TextAlign = ContentAlignment.MiddleRight };
        _filter.Left = 60; _filter.Top = 47; _filter.Width = 220;
        _filter.PlaceholderText = "输入测试项关键字";
        _filter.TextChanged += (_, _) => Rebind();

        _onlyThisStation.Left = 292; _onlyThisStation.Top = 50; _onlyThisStation.Width = 150;
        _onlyThisStation.Text = "只看本机台";
        _onlyThisStation.Checked = true;
        _onlyThisStation.CheckedChanged += (_, _) => { if (!_busy) LoadFast(); };

        _btnDeep.Left = 452; _btnDeep.Top = 46; _btnDeep.Width = 132; _btnDeep.Height = 26;
        _btnDeep.Text = "深扫 XML(更全)";
        _btnDeep.Click += (_, _) => { if (_busy) _deepCts?.Cancel(); else StartDeepScan(); };

        var btnAll = new Button { Left = 592, Top = 46, Width = 62, Height = 26, Text = "全选" };
        btnAll.Click += (_, _) => SetFilteredChecked(true);
        var btnNone = new Button { Left = 660, Top = 46, Width = 62, Height = 26, Text = "清空" };
        btnNone.Click += (_, _) => SetFilteredChecked(false);

        _list.Left = 12; _list.Top = 80; _list.Width = 840; _list.Height = 448;
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _list.View = View.Details;
        _list.CheckBoxes = true;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.HideSelection = false;
        _list.Columns.Add("故障项目（测试项）", 420);
        _list.Columns.Add("出现次数", 80, HorizontalAlignment.Center);
        _list.Columns.Add("最近出现", 130, HorizontalAlignment.Center);
        _list.Columns.Add("型号", 110, HorizontalAlignment.Center);
        _list.Columns.Add("机台", 80, HorizontalAlignment.Center);
        _list.ItemChecked += (_, e) =>
        {
            if (_binding) return;
            if (e.Item.Tag is not Stat st) return;
            if (e.Item.Checked) _checked.Add(st.Item); else _checked.Remove(st.Item);
            UpdateStatus();
        };
        // 双击某行 = 勾/取消，省得去点小方框
        _list.MouseDoubleClick += (_, e) =>
        {
            var hit = _list.HitTest(e.Location);
            if (hit.Item != null) hit.Item.Checked = !hit.Item.Checked;
        };

        _status.Left = 12; _status.Top = 536; _status.Width = 620; _status.Height = 32;
        _status.ForeColor = Color.DimGray;
        _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        _btnOk.Left = 668; _btnOk.Top = 534; _btnOk.Width = 90; _btnOk.Height = 30;
        _btnOk.Text = "确定"; _btnOk.DialogResult = DialogResult.OK;
        _btnOk.Font = new Font(Font, FontStyle.Bold);
        _btnOk.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _btnOk.Click += (_, _) =>
        {
            SelectedItems = _stats.Values.Where(s => _checked.Contains(s.Item))
                                         .OrderByDescending(s => s.Count)
                                         .Select(s => s.Item).ToList();
            if (SelectedItems.Count == 0)
            {
                MessageBox.Show("一个故障项都没勾选。", "提示");
                DialogResult = DialogResult.None;
            }
        };
        var btnCancel = new Button
        {
            Left = 764, Top = 534, Width = 88, Height = 30, Text = "取消",
            DialogResult = DialogResult.Cancel,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };

        AcceptButton = _btnOk;
        CancelButton = btnCancel;
        Controls.AddRange(new Control[]
        {
            tip, lblF, _filter, _onlyThisStation, _btnDeep, btnAll, btnNone,
            _list, _status, _btnOk, btnCancel,
        });
    }

    // ==================== 取数 ====================

    /// <summary>时间窗口（天）：只统计近 N 天内的 FAIL。与待办同步 todo_scan_days 一致。</summary>
    private int ScanDays => _engine.Config.TodoScanDays;

    /// <summary>型号识别：借鉴 Processor 的入库规则 —— 文件名 SN 前 8 位优先，fallback 目录型号。</summary>
    private static string ResolveModel(FailItemSource src)
    {
        if (string.IsNullOrWhiteSpace(src.XmlPath)) return src.Model ?? "";
        var info = Processor.ParsePath(src.XmlPath);
        if (info == null) return src.Model ?? "";
        return info.ModelFromName ?? info.Model;
    }

    private string StationFilter() =>
        _onlyThisStation.Checked ? _engine.ResolvedStationId : "";

    /// <summary>秒出：库里现成的 fail_reason（每条 FAIL 的首个失败项），近 N 天。</summary>
    private void LoadFast()
    {
        _stats.Clear();
        try
        {
            foreach (var src in _engine.Db.FailItemSources(StationFilter(), ScanDays))
            {
                if (string.IsNullOrWhiteSpace(src.FirstFailItem)) continue;
                Bump(src.FirstFailItem, src, fromXml: false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"读取 FAIL 故障项失败: {ex.Message}");
            MessageBox.Show("读取 FAIL 记录失败: " + ex.Message, "提示");
        }
        Rebind();
    }

    private void Bump(string item, FailItemSource src, bool fromXml)
    {
        item = item.Trim();
        if (item.Length == 0) return;
        if (!_stats.TryGetValue(item, out var st))
        {
            st = new Stat { Item = item, FromXml = fromXml };
            _stats[item] = st;
        }
        st.Count++;
        // 时间统一规范化（与待办同步同口径）：yyyyMMdd -> yyyy-MM-dd 00:00:00，带 T 的转成空格。
        // 否则 batch_timestamp(带T) 与 test_date(纯数字) 混在一起字符串比较会错乱 → “最近出现”显示错
        var when = Database.NormalizeTs(string.IsNullOrWhiteSpace(src.Timestamp) ? src.TestDate : src.Timestamp);
        if (string.CompareOrdinal(when, st.LastSeen) > 0) st.LastSeen = when;
        var model = ResolveModel(src);
        if (!string.IsNullOrWhiteSpace(model)) st.Models.Add(model);
        if (!string.IsNullOrWhiteSpace(src.StationId)) st.Stations.Add(src.StationId);
    }

    /// <summary>深扫：解析近 N 天内每条 FAIL 的 XML，只取第一条失败项补进来（与库里 fail_reason 口径一致）。后台线程 + 可取消 + 进度限流。</summary>
    private async void StartDeepScan()
    {
        var sources = _engine.Db.FailItemSources(StationFilter(), ScanDays);
        var withXml = sources.Where(s => !string.IsNullOrWhiteSpace(s.XmlPath)).ToList();
        if (withXml.Count == 0)
        {
            MessageBox.Show("库里没有可解析的 XML 路径。", "深扫");
            return;
        }

        _busy = true;
        _btnDeep.Text = "停止深扫";
        _btnOk.Enabled = false;
        _onlyThisStation.Enabled = false;
        _deepCts = new CancellationTokenSource();
        var ct = _deepCts.Token;

        int done = 0, missing = 0, added = 0;
        var found = new List<(string item, FailItemSource src)>();

        var progress = new Progress<(int done, int total, int added)>(p =>
            _status.Text = $"深扫 XML… {p.done}/{p.total}，已补出 {p.added} 个新故障项\r\n（可点【停止深扫】用已扫到的部分）");

        try
        {
            await Task.Run(() =>
            {
                var rep = (IProgress<(int, int, int)>)progress;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var seen = new HashSet<string>(_stats.Keys, StringComparer.OrdinalIgnoreCase);

                foreach (var src in withXml)
                {
                    ct.ThrowIfCancellationRequested();
                    done++;
                    if (!File.Exists(src.XmlPath)) { missing++; continue; }

                    var data = XmlParser.ParseReport(src.XmlPath);
                    if (data.Error) continue;
                    // 只取**第一条** FAIL：与库 fail_reason（每条记录的首个失败项）口径一致。
                    // 排除逻辑沿用 XmlParser（Get Unit Information 等不入 Tests）。
                    var firstFail = data.Tests.FirstOrDefault(t =>
                        t.Status.Contains("Fail", StringComparison.OrdinalIgnoreCase));
                    if (firstFail == null) continue;
                    var name = firstFail.Name.Trim();
                    if (name.Length == 0) continue;
                    lock (found)
                    {
                        found.Add((name, src));
                        if (seen.Add(name)) added++;
                    }
                    // 限流：每 300ms 报一次，别把 UI 消息队列打爆
                    if (sw.ElapsedMilliseconds >= 300)
                    {
                        rep.Report((done, withXml.Count, added));
                        sw.Restart();
                    }
                }
                rep.Report((done, withXml.Count, added));
            }, ct);
        }
        catch (OperationCanceledException) { /* 用已扫到的部分 */ }
        catch (Exception ex)
        {
            Logger.Error($"深扫 XML 失败: {ex.Message}");
            MessageBox.Show("深扫失败: " + ex.Message, "提示");
        }

        // 深扫结果重建统计：XML 里每条只取第一条失败项，与库 fail_reason 口径一致，用它覆盖「首项」口径
        if (found.Count > 0)
        {
            _stats.Clear();
            foreach (var (item, src) in found) Bump(item, src, fromXml: true);
            // XML 缺失的记录仍用库里的首项兜底
            foreach (var src in withXml.Where(s => !File.Exists(s.XmlPath)))
                if (!string.IsNullOrWhiteSpace(src.FirstFailItem))
                    Bump(src.FirstFailItem, src, fromXml: false);
        }

        _busy = false;
        _btnDeep.Text = "深扫 XML(更全)";
        _btnOk.Enabled = true;
        _onlyThisStation.Enabled = true;
        Rebind();
        Logger.Info($"[维修依据] 深扫 XML {done} 个（缺失 {missing}），去重后故障项 {_stats.Count} 个");
        if (missing > 0)
            _status.Text += $"\r\n注意：{missing} 个 XML 文件已不在原路径（用库里的首个失败项兜底）";
    }

    // ==================== 列表 ====================

    private IEnumerable<Stat> Filtered()
    {
        var kw = _filter.Text.Trim();
        var q = _stats.Values.AsEnumerable();
        if (kw.Length > 0)
            q = q.Where(s => s.Item.Contains(kw, StringComparison.OrdinalIgnoreCase));
        return q.OrderByDescending(s => s.Count).ThenBy(s => s.Item, StringComparer.OrdinalIgnoreCase);
    }

    private void Rebind()
    {
        _binding = true;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var st in Filtered())
            {
                var it = new ListViewItem(st.Item) { Tag = st, Checked = _checked.Contains(st.Item) };
                it.SubItems.Add(st.Count.ToString());
                it.SubItems.Add(ShortTime(st.LastSeen));
                it.SubItems.Add(st.Models.Count switch
                {
                    0 => "—",
                    1 => st.Models.First(),
                    _ => $"{st.Models.First()} 等 {st.Models.Count} 个",
                });
                it.SubItems.Add(st.Stations.Count switch
                {
                    0 => "—",
                    1 => st.Stations.First(),
                    _ => $"{st.Stations.Count} 台",
                });
                _list.Items.Add(it);
            }
        }
        finally
        {
            _list.EndUpdate();
            _binding = false;
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        int shown = _list.Items.Count;
        _status.Text =
            $"去重后共 {_stats.Count} 个故障项（近 {ScanDays} 天，当前显示 {shown} 个），已勾选 {_checked.Count} 个\r\n" +
            (_stats.Values.Any(s => s.FromXml)
                ? "来源：XML 深扫（每条记录只取第一条失败项）"
                : $"来源：数据库 fail_reason（每条 FAIL 只取首个失败项，近 {ScanDays} 天）—— 需要复核源文件请点【深扫 XML】");
    }

    private void SetFilteredChecked(bool on)
    {
        foreach (var st in Filtered())
        {
            if (on) _checked.Add(st.Item); else _checked.Remove(st.Item);
        }
        Rebind();
    }

    private static string ShortTime(string? ts) => TimeUtil.Short(ts);

    // ==================== 自检用 ====================

    /// <summary>把 FAIL 来源行聚合成「去重后的故障项 + 次数」（纯函数，供自检直接验去重逻辑）。</summary>
    public static List<(string Item, int Count)> Aggregate(IEnumerable<FailItemSource> sources)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sources)
        {
            var k = (s.FirstFailItem ?? "").Trim();
            if (k.Length == 0) continue;
            d[k] = d.GetValueOrDefault(k) + 1;
        }
        return d.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => (kv.Key, kv.Value)).ToList();
    }
}

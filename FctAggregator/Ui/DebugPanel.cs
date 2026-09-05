using System.Text;

namespace FctAggregator;

public class DebugPanel : Panel
{
    private readonly Engine _engine;
    private TextBox _output = null!;

    public DebugPanel(Engine engine)
    {
        _engine = engine;
        BuildUi();
    }

    private void BuildUi()
    {
        Padding = new Padding(Theme.Gap);
        BackColor = Theme.Bg;

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 78, WrapContents = true, AutoScroll = false, BackColor = Theme.Bg,
        };

        void AddTag(string t)
        {
            bar.Controls.Add(new Label
            {
                Text = t, AutoSize = true, ForeColor = Theme.Primary, Font = Theme.BodyBold,
                Margin = new Padding(14, 9, 4, 0),
            });
        }
        void AddBtn(string t, Action a)
        {
            var b = Theme.MakeButton(t, 104);
            b.Margin = new Padding(2, 4, 2, 4);
            b.Click += (_, _) => { try { a(); } catch (Exception ex) { Print($"错误: {ex.Message}"); } };
            bar.Controls.Add(b);
        }

        AddTag("测试");
        AddBtn("推送测试", TestPush);
        AddBtn("桌面提示测试", TestDesktopNotify);
        AddBtn("数据库状态", TestDbStatus);
        AddTag("检测");
        AddBtn("机台检测", TestStation);
        AddBtn("型号发现", TestModels);
        AddBtn("设备状态", TestDevices);
        AddTag("查询");
        AddBtn("最近10条FAIL", QueryFails);
        AddBtn("今日统计", QueryToday);
        AddBtn("最大SN", QueryMaxSn);
        AddTag("待办");
        AddBtn("待办同步", TodoSync);
        AddBtn("合并预览", TodoPreview);
        AddTag("配置");
        AddBtn("查看配置", ViewConfig);
        AddBtn("桌面提示开关", ToggleDesktopNotify);
        AddBtn("开机自启", ToggleAutoStart);
        AddTag("数据");
        AddBtn("清空数据库", ClearDb);

        _output = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
            Font = Theme.Mono, WordWrap = false, BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Surface,
        };

        Controls.Add(_output);
        Controls.Add(bar);
    }

    private void Print(string text)
    {
        _output.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
    }

    private void TestPush()
    {
        var cfg = _engine.Config;
        if (string.IsNullOrEmpty(cfg.WebhookUrl)) { Print("Webhook 未配置"); return; }
        var rec = new TestRecord
        {
            StationId = _engine.ResolvedStationId, Model = "TEST", Category = "Offline",
            Sn = "TESTSN00000000", Result = "FAIL", BatchTimestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            XmlPath = "test.xml",
            FailedTests = { new FailedTest { Name = "测试项 6.1.1.1", Value = "9.9", Lolim = "10", Hilim = "12", Unit = "V", Rule = "GELE" } }
        };
        Print("正在发送测试推送到飞书...");
        Task.Run(async () =>
        {
            await FeishuNotifier.SendFailAlert(cfg.WebhookUrl, rec);
            BeginInvoke(() => Print("测试推送已发送(查看飞书群)"));
        });
    }

    private void TestDbStatus()
    {
        var total = _engine.Db.TotalRecords();
        var g = _engine.Db.FetchGlobalStats(_engine.ResolvedStationId);
        Print($"数据库记录总数: {total}");
        Print($"  PASS={g.Pass} FAIL={g.Fail} 中断={g.Interrupted} Invalid={g.Invalid}");
        Print($"  产品去重: {g.ProductCount}");
    }

    private void TestStation()
    {
        var detected = StationDetector.DetectStation();
        Print($"config station_id: '{_engine.Config.StationId}'");
        Print($"IP识别机台号: {detected ?? "(未识别)"}");
        Print($"当前使用: {(string.IsNullOrEmpty(_engine.ResolvedStationId) ? "UNKNOWN" : _engine.ResolvedStationId)}");
    }

    private void TestModels()
    {
        var root = _engine.Config.ResultsRoot;
        if (!Directory.Exists(root)) { Print($"结果目录不存在: {root}"); return; }
        var found = new List<string>();
        foreach (var cat in new[] { "Online", "Offline" })
        {
            var d = Path.Combine(root, cat);
            if (!Directory.Exists(d)) continue;
            foreach (var sub in Directory.GetDirectories(d))
            {
                var n = Path.GetFileName(sub);
                if (StationDetector.IsValidModel(n)) found.Add($"{cat}/{n}");
            }
        }
        Print($"发现型号目录 ({found.Count}):");
        foreach (var f in found) Print($"  {f}");
    }

    private void TestDevices()
    {
        var data = FctIni.Parse(_engine.Config.FctIniPath);
        if (!data.Found) { Print(data.Error ?? "FCT.ini 未找到"); return; }
        Print($"FCT.ini: {data.IniPath}");
        Print($"型号: {string.Join(", ", data.Models)}");
        Print($"软件版本: {string.Join(", ", data.FwVersions.Select(v => v.Version))}");
        Print("设备:");
        foreach (var dev in data.Devices)
            Print($"  {dev.Name,-16} {dev.Port,-10} {(dev.Type == "com" ? (dev.Online ? "在线" : "离线") : "USB")}");
    }

    private void QueryFails()
    {
        var fails = _engine.Db.RecentFails(10);
        Print($"最近 {fails.Count} 条 FAIL:");
        foreach (var (sn, result, model, ts, path) in fails)
            Print($"  {model} | SN={sn} | {ts} | {Path.GetFileName(path)}");
    }

    private void QueryToday()
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        var d = _engine.Db.FetchDailyStats(_engine.ResolvedStationId, today);
        var yield = d.Pass + d.Fail > 0 ? d.Pass * 100.0 / (d.Pass + d.Fail) : 0;
        Print($"今日({DateTime.Now:yyyy-MM-dd}) 统计: PASS={d.Pass} FAIL={d.Fail} 中断={d.Interrupted} 良率={yield:F1}%");
    }

    private void QueryMaxSn()
    {
        Print($"最大SN: {_engine.Db.MaxSn() ?? "(无)"}");
    }

    private void TodoSync()
    {
        var days = _engine.Config.TodoScanDays;
        var n = _engine.Db.SyncTodoItems(days);
        Print($"待办同步完成（扫描窗口 {days} 天）：新登记 {n} 条；当前未确认 {_engine.Db.CountPendingTodos()} 条");
        Print($"水位线(已并入的 test_records 最大 id): {_engine.Db.GetMeta("todo_sync_last_id") ?? "(未设置)"}");
    }

    private void TodoPreview()
    {
        var srcs = _engine.Db.FailItemSources("");
        var agg = FailItemPickerForm.Aggregate(srcs);
        if (agg.Count == 0) { Print("库里没有 FAIL 故障项。"); return; }
        var groups = agg.GroupBy(a => TodoGrouping.KeyOf(a.Item))
                        .OrderByDescending(g => g.Sum(x => x.Count)).ToList();
        Print($"故障项 {agg.Count} 个 -> 合并为 {groups.Count} 个待办大项（按 fail 次数倒序 = 处理优先级）:");
        foreach (var g in groups.Take(40))
        {
            var total = g.Sum(x => x.Count);
            var merged = g.Count() > 1 ? $"   ← 合并 {g.Count()} 项" : "";
            Print($"  {total,4}x [优先级{TodoGrouping.PriorityZhOf(total)}] {TodoGrouping.TitleOf(g.Select(x => x.Item))}{merged}");
            if (g.Count() > 1)
                foreach (var v in g) Print($"          · {v.Count}x {v.Item}");
        }
        if (groups.Count > 40) Print($"  …另有 {groups.Count - 40} 个大项未列出");
    }

    private void ViewConfig()
    {
        var c = _engine.Config;
        Print("当前配置:");
        Print($"  station_id: '{c.StationId}'");
        Print($"  results_root: {c.ResultsRoot}");
        Print($"  fct_ini_path: {c.FctIniPath}");
        Print($"  webhook: {(string.IsNullOrEmpty(c.WebhookUrl) ? "未配置" : c.WebhookUrl[..Math.Min(50, c.WebhookUrl.Length)] + "...")}");
        Print($"  log_level: {c.LogLevel}");
        Print($"  desktop_notify: {(c.DesktopNotify ? "开" : "关")}（当前运行中: {(DesktopNotifier.Enabled ? "开" : "关")}）");
        Print($"  notify_min_interval_sec: {c.NotifyMinIntervalSec}");
        Print($"  todo_scan_days: {c.TodoScanDays}（待办只扫近 {c.TodoScanDays} 天；已登记的永久保留）");
    }

    private void TestDesktopNotify()
    {
        if (!DesktopNotifier.Enabled)
        {
            Print("桌面提示当前是关闭的，先点「桌面提示开关」或在 config.json 里把 desktop_notify 设为 true。");
            return;
        }
        DesktopNotifier.NotifyRaw($"测试提示 · {DateTime.Now:HH:mm:ss}\n测试项 6.1.1.1 = 9.9V (下限 10V)");
        Print("已发送一条桌面提示（若没看到：检查 Windows 设置-系统-通知 与专注助手/勿扰）。");
        Print("注意：节流机制下两条提示至少间隔 " + DesktopNotifier.MinIntervalSeconds + " 秒。");
    }

    private void ToggleDesktopNotify()
    {
        DesktopNotifier.Enabled = !DesktopNotifier.Enabled;
        Print($"桌面提示已{(DesktopNotifier.Enabled ? "开启" : "关闭")}（本次运行有效；永久生效请改 config.json 的 desktop_notify）");
    }

    private void ToggleAutoStart()
    {
        if (AutoStart.IsEnabled())
        {
            AutoStart.Disable();
            Print("已关闭开机自启(删除启动文件夹快捷方式)");
        }
        else
        {
            var ok = AutoStart.Enable();
            Print(ok ? "已开启开机自启(启动文件夹快捷方式)" : "开启失败(可能被杀毒拦截, 请手动把快捷方式拖到启动文件夹)");
        }
    }

    private void ClearDb()
    {
        if (MessageBox.Show("确定清空数据库？所有记录将删除。", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            var dataDir = Path.Combine(AppConfig.BaseDir, "data");
            foreach (var f in Directory.GetFiles(dataDir, "*.db")) File.Delete(f);
            Print("数据库已清空，请重启软件重新扫描。");
        }
        catch (Exception ex) { Print($"清空失败: {ex.Message}"); }
    }
}

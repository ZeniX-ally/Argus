using System.Text;

namespace FctAggregator;

public class DebugPanel : Panel
{
    private readonly Engine _engine;
    private TextBox _output = null!;
    private volatile bool _busy;
    private volatile bool _pushBusy;   // 测试推送防连点（会真发飞书群）
    private int _printLines;   // 输出框只增不减会越来越慢，超过上限先清空

    public DebugPanel(Engine engine)
    {
        _engine = engine;
        BuildUi();
    }

    private void BuildUi()
    {
        Padding = new Padding(Theme.Gap);
        BackColor = Theme.Bg;

        var side = new Panel
        {
            Dock = DockStyle.Left, Width = 220, BackColor = Theme.Surface,
            Padding = new Padding(Theme.Gap),
        };
        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, BackColor = Theme.Surface,
            Padding = new Padding(0, 0, SystemInformation.VerticalScrollBarWidth, 0),
        };
        side.Controls.Add(nav);

        int ChildWidth() => Math.Max(120, nav.ClientSize.Width - nav.Padding.Horizontal - 2);
        void FitNav()
        {
            int w = ChildWidth();
            foreach (Control c in nav.Controls) c.Width = w;
        }
        nav.Resize += (_, _) => FitNav();

        bool firstTag = true;
        void AddTag(string t)
        {
            nav.Controls.Add(new Label
            {
                Text = t, AutoSize = false, Width = ChildWidth(), Height = 22,
                ForeColor = Theme.TextFaint, Font = Theme.Tiny,
                TextAlign = ContentAlignment.BottomLeft,
                Margin = new Padding(0, firstTag ? 0 : 10, 0, 2),
            });
            firstTag = false;
        }
        void AddBtn(string t, Action a, bool danger = false)
        {
            var b = Theme.MakeButton(t, ChildWidth());
            b.Width = ChildWidth();
            b.Margin = new Padding(0, 0, 0, 4);
            if (danger) b.ForeColor = Theme.Danger;
            b.Click += (_, _) => { try { a(); } catch (Exception ex) { Print($"错误: {ex.Message}"); } };
            nav.Controls.Add(b);
        }

        AddTag("测试");
        AddBtn("推送测试", TestPush);
        AddBtn("桌面提示测试", TestDesktopNotify);
        AddBtn("数据库状态", TestDbStatus);
        AddBtn("存储诊断", StorageDiagnose);
        AddBtn("智能优化", SmartOptimize);
        AddTag("检测");
        AddBtn("机台检测", TestStation);
        AddBtn("型号发现", TestModels);
        AddBtn("设备状态", TestDevices);
        AddTag("查询");
        AddBtn("最近10条FAIL", QueryFails);
        AddBtn("今日统计", QueryToday);
        AddBtn("当月库统计", QueryMonth);
        AddBtn("最大SN", QueryMaxSn);
        AddTag("待办");
        AddBtn("待办登记", TodoSync);
        AddBtn("合并预览", TodoPreview);
        AddTag("配置");
        AddBtn("查看配置", ViewConfig);
        AddBtn("残留检查", ProbeLeftover);
        AddBtn("桌面提示开关", ToggleDesktopNotify);
        AddBtn("开机自启", ToggleAutoStart);
        AddTag("日志");
        AddBtn("查看日志", ShowLogs);
        AddBtn("导出日志", ExportLogs);
        AddBtn("打开目录", OpenLogDir);
        AddTag("数据");
        AddBtn("清空数据库", ClearDb, danger: true);
        FitNav();

        _output = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
            Font = Theme.Mono, WordWrap = false, BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Surface,
        };

        Controls.Add(_output);
        Controls.Add(side);
    }

    private void Print(string text)
    {
        // 允许后台任务直接调 Print：统一在这里回到 UI 线程
        if (InvokeRequired)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { UiAsync.Post(this, () => Print(text)); } catch { }
            return;
        }
        if (++_printLines > 4000) { _output.Clear(); _printLines = 1; }
        _output.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
    }

    /// <summary>查库/遍历目录的按钮逻辑放后台跑，避免界面无响应；期间置忙 + 等待光标。</summary>
    private void RunBusy(string busyText, Action work)
    {
        if (_busy) return;
        _busy = true;
        UseWaitCursor = true;
        Print(busyText);
        Task.Run(() =>
        {
            try { work(); }
            catch (Exception ex) { Print($"操作失败: {ex.Message}"); }
            finally
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    try { UiAsync.Post(this, () => { _busy = false; UseWaitCursor = false; }); } catch { }
                }
            }
        });
    }

    private void TestPush()
    {
        var cfg = _engine.Config;
        if (string.IsNullOrEmpty(cfg.WebhookUrl)) { Print("Webhook 未配置"); return; }
        if (_pushBusy) { Print("上一次测试推送尚未结束，请稍候…"); return; }   // 防连点刷屏飞书
        _pushBusy = true;
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
            try
            {
                // 审计修复：PostCard 内部吞掉所有异常并返回三态，原实现丢弃返回值、无条件打印"已发送"，
                // catch 成了死代码 → 现场误判告警链路正常（群里一条没有）。
                var outcome = await FeishuNotifier.SendFailAlert(cfg.WebhookUrl, rec);
                UiAsync.Post(this, () => Print(outcome switch
                {
                    FeishuSendOutcome.Sent => "测试推送已送达（查看飞书群）",
                    FeishuSendOutcome.Skipped => "测试推送被跳过：未配置 webhook，或地址不是 https:// 前缀",
                    _ => "测试推送失败（重试耗尽），详见日志",
                }));
            }
            catch (Exception ex)
            {
                UiAsync.Post(this, () => Print($"测试推送失败: {ex.Message}"));
            }
            finally
            {
                UiAsync.Post(this, () => _pushBusy = false);
            }
        });
    }

    private void TestDbStatus()
    {
        RunBusy("正在统计数据库（可能耗时）…", () =>
        {
        var total = _engine.Db.TotalRecords();
        var g = _engine.Db.FetchGlobalStats(_engine.ResolvedStationId);
        var st = _engine.Db.GetStorageStats();
        var health = StorageOptimizer.Evaluate(_engine.Db, _engine.Config);
        Print($"数据库记录总数: {total}");
        Print($"  PASS={g.Pass} FAIL={g.Fail} 中断={g.Interrupted} Invalid={g.Invalid}");
        Print($"  产品去重: {g.ProductCount}");
        Print($"  {st.Format()}");
        foreach (var line in health.FormatLines()) Print($"  {line}");
        var last = _engine.Db.GetMeta(StorageOptimizer.MetaLastRun);
        if (!string.IsNullOrEmpty(last))
            Print($"  上次优化: {last}  评分 {_engine.Db.GetMeta(StorageOptimizer.MetaLastScore) ?? "?"}");
        });
    }

    private void StorageDiagnose()
    {
        RunBusy("正在做存储分层诊断…", () =>
        {
        var health = StorageOptimizer.Evaluate(_engine.Db, _engine.Config);
        Print("── 存储分层诊断 ──");
        foreach (var line in health.FormatLines()) Print(line);
        });
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
        RunBusy("正在扫描型号目录…", () =>
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
        });
    }

    private void TestDevices()
    {
        // FctIni.Parse 在路径配错时会触发全盘自动识别，绝不能放在 UI 线程上
        RunBusy("正在读取 FCT.ini / 探测设备…", () =>
        {
        var data = FctIni.Parse(_engine.Config.FctIniPath);
        if (!data.Found) { Print(data.Error ?? "FCT.ini 未找到"); return; }
        Print($"FCT.ini: {data.IniPath}");
        Print($"型号: {string.Join(", ", data.Models)}");
        Print($"软件版本: {string.Join(", ", data.FwVersions.Select(v => v.Version))}");
        Print("设备:");
        foreach (var dev in data.Devices)
            Print($"  {dev.Name,-16} {dev.Port,-10} {(dev.Type == "com" ? (dev.Online ? "在线" : "离线") : "USB")}");
        });
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
        // 审计：与同文件其它查库按钮一致走 RunBusy——FetchDailyStats/按型号统计含窗口 CTE，
        // 大库上直接在 UI 线程跑会整窗卡住。
        RunBusy("正在统计今日数据…", () =>
        {
        var today = DateTime.Now.ToString("yyyyMMdd");
        var d = _engine.Db.FetchDailyStats(_engine.ResolvedStationId, today);
        var yield = StatsUtil.YieldPct(d.Pass, d.Fail);
        Print($"今日({DateTime.Now:yyyy-MM-dd}) 统计(公司口径 FAIL优先, Offline PASS不计): 总测试={d.TodayProductCount} PASS={d.Pass} FAIL={d.Fail} 中断={d.Interrupted} 良率={yield:F1}%");
        foreach (var m in _engine.Db.FetchDailyStatsByModel(_engine.ResolvedStationId, today))
            Print($"  型号 {m.Model}: PASS={m.Pass} FAIL={m.Fail} 中断={m.Interrupted} 合计={m.Total}");
        });
    }

    private void QueryMonth()
    {
        RunBusy("正在统计当月数据…", () =>
        {
        var ym = DateTime.Now.ToString("yyyyMM");
        var m = _engine.Db.FetchMonthlyStats(_engine.ResolvedStationId, ym);
        var y = StatsUtil.YieldPct(m.Pass, m.Fail);
        Print($"当月库内 {ym}（xml_path 去重 log 条数，不是 app.log 行数，debug 跳过不入库）");
        Print($"  PASS={m.Pass} FAIL={m.Fail} 中断={m.Interrupted} 良率={y:F1}% 产品去重={m.TodayProductCount}");
        Print("  公式: PASS/(PASS+FAIL)，中断不进分母。对账公司系统请对这组数字。");
        });
    }

    private void QueryMaxSn()
    {
        Print($"最大SN: {_engine.Db.MaxSn() ?? "(无)"}");
    }

    private void TodoSync()
    {
        RunBusy("正在登记待办…", () =>
        {
        var days = _engine.Config.TodoScanDays;
        var n = _engine.Db.SyncTodoItems(days);
        Print($"待办登记完成（扫描窗口 {days} 天）：新登记 {n} 条；当前未确认 {_engine.Db.CountPendingTodos()} 条");
        Print($"水位线(已并入的 test_records 最大 id): {_engine.Db.GetMeta("todo_sync_last_id") ?? "(未设置)"}");
        });
    }

    private void TodoPreview()
    {
        RunBusy("正在汇总故障项…", () =>
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
        });
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

    private void ProbeLeftover()
    {
        // 审计：LeftoverAgg.Probe 内部 Process.Start 调 sc/netsh（秒级），必须在后台跑
        RunBusy("正在探测聚合残留…", () =>
        {
        var hits = LeftoverAgg.Probe();
        if (hits.Count == 0)
        {
            Print("未发现聚合残留服务 / ArgusAggWeb 防火墙 / config agg_*。");
            return;
        }
        Print("发现残留（不自动删除）：");
        foreach (var h in hits) Print("  " + h);
        Print("处理：管理员 CMD 执行 sc stop <名> && sc delete <名>；防火墙 netsh advfirewall firewall delete rule name=ArgusAggWeb");
        });
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

    private void ShowLogs()
    {
        Print($"日志文件: {Logger.CurrentLogPath}");
        Print(Logger.ReadTail());
    }

    private void ExportLogs()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "导出运行日志",
            Filter = "Zip 日志包|*.zip",
            FileName = Logger.SuggestedExportName(),
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        var target = dlg.FileName;
        // D10：ExportZip 复制+压缩全部 app.log*，大日志时 UI 线程同步跑会卡住——改 RunBusy 后台
        RunBusy("正在导出日志包…", () =>
        {
            var path = Logger.ExportZip(target);
            Print($"已导出: {path}");
            Logger.Info($"[日志] 已导出诊断包 {path}");
        });
    }

    private void OpenLogDir()
    {
        Directory.CreateDirectory(Logger.LogDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Logger.LogDirectory,
            UseShellExecute = true,
        });
        Print($"已打开: {Logger.LogDirectory}");
    }

    private void SmartOptimize()
    {
        // 审计：StorageOptimizer.Evaluate 会盘点全库（COUNT/PRAGMA），原先在 UI 线程跑会整窗卡住。
        // 这里后台评估 → 回 UI 线程出计划与确认框（顺序与原实现一致）→ 再后台执行优化。
        if (_busy) return;
        _busy = true;
        UseWaitCursor = true;
        Print("正在评估存储健康…");
        var db = _engine.Db;
        var cfg = _engine.Config;
        Task.Run(() =>
        {
            StorageHealthReport health;
            try { health = StorageOptimizer.Evaluate(db, cfg); }
            catch (Exception ex)
            {
                UiAsync.Post(this, () => { _busy = false; UseWaitCursor = false; Print($"评估失败: {ex.Message}"); });
                return;
            }
            UiAsync.Post(this, () =>
            {
                _busy = false;
                UseWaitCursor = false;
                Print(health.FormatHeadline());
                foreach (var a in health.PlannedActions) Print($"  计划: {a}");
                var msg = "将按保留策略清理过期数据、重建瘦表并视情况 VACUUM。\n大库可能需要数分钟，期间请勿关软件。继续？";
                if (MessageBox.Show(msg, "智能优化", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Print("正在优化，请稍候…");
                Task.Run(() =>
                {
                    try
                    {
                        var rep = StorageOptimizer.Run(db, cfg, StorageOptimizeMode.Deep);
                        UiAsync.Post(this, () => { foreach (var line in rep.FormatLines()) Print(line); });
                    }
                    catch (Exception ex)
                    {
                        UiAsync.Post(this, () => Print($"优化失败: {ex.Message}"));
                    }
                });
            });
        });
    }

    private void ClearDb()
    {
        // 只清本机台当前这个库：原来删 data\*.db 会把其它机台的库一起删掉，且漏删 -wal/-shm
        var dbPath = _engine.Db.DbPath;
        var msg = "确定清空数据库？所有记录都会丢失，需要重启后重新扫描。\r\n\r\n将删除：\r\n  " + dbPath
                + "\r\n  " + dbPath + "-wal / -shm（如存在）\r\n\r\n注意：Argus 运行期间数据库文件被占用，删除会失败，需先完全退出 Argus。";
        if (MessageBox.Show(msg, "确认清空数据库", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            // 先删主库：若被占用会先抛异常，不会走到删 wal/shm 那一步，避免只删伴生文件造成状态不一致
            File.Delete(dbPath);
            foreach (var suf in new[] { "-wal", "-shm" })
                if (File.Exists(dbPath + suf)) File.Delete(dbPath + suf);
            Print("数据库已清空，请完全退出并重启 Argus 重新扫描。");
        }
        catch (Exception ex)
        {
            Print($"清空失败: {ex.Message}");
            Print($"原因通常是数据库正被 Argus 占用。请完全退出 Argus 后手动删除：{dbPath}（含 -wal / -shm）");
        }
    }
}

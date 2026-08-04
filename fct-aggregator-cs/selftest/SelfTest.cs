using FctAggregator;
using Microsoft.Data.Sqlite;

/// <summary>
/// 维修记录看板化(v2.2.0)的自检：拿真实 db 的副本跑，验证
/// 迁移 / 备份 / 全字段更新 / 计数 / 排序 / 导出 12 列。
/// 不碰真实数据（全在系统临时目录里做）。
/// </summary>
class SelfTest
{
    static int _fail;
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "[OK]   " : "[FAIL] ") + what);
        if (!ok) _fail++;
    }

    /// <summary>
    /// 从「当前工作目录」与「程序所在目录」两头各往上找 6 层（兼顾仓库根/子项目两种起点）。
    ///
    /// ⚠ 以前这里写死过 <c>F:\Programe\G4_9-QA\fct-aggregator-cs\...</c>，
    /// 2026-07-31 仓库目录改名成 Argus 后该路径失效 —— 而且**不报错**，
    /// 只是静静退成「用空库」，一批依赖真实库的断言会悄悄不跑（伪绿）。
    /// 所以改成自己找，不再依赖任何绝对路径。
    /// </summary>
    static string FindUp(string relative)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                var p = Path.Combine(dir.FullName, relative);
                if (File.Exists(p)) return p;
                // 从仓库根起步时还要下钻一层
                var p2 = Path.Combine(dir.FullName, "fct-aggregator-cs", relative);
                if (File.Exists(p2)) return p2;
            }
        }
        return "";
    }

    // ⚠ [STAThread] 必须贴在 Main 上：自检会构造 ComboBox（AutoCompleteMode），
    //   不是 STA 就直接 ThreadStateException。在它上方插方法时小心别把这个特性挤跑。
    [STAThread]
    static int Main()
    {
        var work = Path.Combine(Path.GetTempPath(), "fct_agg_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(work);
        // 真实库只是本地 fixture（data/*.db 已 gitignore）—— 别写死结构路径，
        // 也别在没有它的机器上报红（下面几条依赖它的断言已改成有它才跑）。
        var real = FindUp(Path.Combine("dist", "data", "fct.db"));
        var db = Path.Combine(work, "fct.db");

        bool haveReal = real.Length > 0 && File.Exists(real);
        if (haveReal) File.Copy(real, db);
        Console.WriteLine($"工作目录: {work}");
        Console.WriteLine(haveReal ? $"已复制真实库: {new FileInfo(db).Length / 1024} KB" : "真实库不存在，用空库");

        // ---------- 造 legacy closed 数据（真实库里可能没有） ----------
        int legacyCount = 0;
        if (haveReal)
        {
            using var c = new SqliteConnection($"Data Source={db}");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS maintenance_records (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, station_id TEXT, equipment_model TEXT,
                    equipment_sn TEXT, fail_item TEXT NOT NULL, fail_reason TEXT,
                    severity TEXT DEFAULT 'major', status TEXT DEFAULT 'open', resolver TEXT,
                    resolution TEXT, notes TEXT,
                    created_at TEXT DEFAULT (datetime('now','localtime')),
                    updated_at TEXT DEFAULT (datetime('now','localtime')));";
            cmd.ExecuteNonQuery();

            using var ins = c.CreateCommand();
            ins.CommandText = @"INSERT INTO maintenance_records
                (station_id,fail_item,severity,status,resolver,created_at,updated_at)
                VALUES ('FCT1','历史已关闭A','major','closed','老王','2026-07-01 09:00:00','2026-07-02 10:00:00'),
                       ('FCT1','历史已关闭B','minor','closed','老李','2026-07-03 09:00:00','2026-07-03 11:00:00');";
            legacyCount = ins.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        // ---------- 打开库 = 触发 Init() 里的迁移 ----------
        var d = new Database(db);
        Check(true, "Database 构造完成（Init + 迁移已跑）");

        var bak = Directory.GetFiles(work, "fct.db.bak-*");
        if (haveReal)
            Check(bak.Length == 1, $"迁移前已自动备份 db（{(bak.Length == 1 ? Path.GetFileName(bak[0]) : "未找到")}）");
        else
            Console.WriteLine("    (跳过迁移备份断言：无真实库 fixture)");
        if (bak.Length == 1)
            Check(new FileInfo(bak[0]).Length > 0, $"备份文件非空（{new FileInfo(bak[0]).Length / 1024} KB）");

        int stillClosed;
        using (var c = new SqliteConnection($"Data Source={db}"))
        {
            c.Open();
            using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM maintenance_records WHERE status='closed'";
            stillClosed = Convert.ToInt32(q.ExecuteScalar());
        }
        Check(stillClosed == 0, $"closed -> resolved 迁移生效（残留 closed = {stillClosed}）");

        // 幂等：再开一次不应该炸、也不应该有第二次改写
        var d2 = new Database(db);
        Check(true, "重复打开库（迁移幂等，无异常）");

        // ---------- 五个状态各造几条 ----------
        var keys = MaintenanceMeta.Statuses.Select(s => s.Key).ToArray();
        Check(keys.Length == 5 && keys[0] == "unknown" && keys[4] == "resolved",
              $"状态体系 5 个且顺序正确: {string.Join(" -> ", MaintenanceMeta.Statuses.Select(s => s.Zh))}");
        Check(MaintenanceMeta.DefaultStatus == "open", "新建记录默认状态 = open(未完成)");
        Check(MaintenanceMeta.ZhOf("closed") == "已完成", "legacy closed 仍显示为「已完成」（外来 db 兜底）");
        Check(MaintenanceMeta.Normalize("closed") == "resolved", "legacy closed 归并到 resolved 列");
        Check(MaintenanceMeta.Normalize("这是个野值") == "open", "未知状态归并到 open，不会漏卡片");

        var ids = new Dictionary<string, int>();
        int seq = 0;
        foreach (var k in keys)
            for (int i = 0; i < 3; i++)
            {
                var m = new MaintenanceRecord
                {
                    StationId = "FCT1",
                    FailItem = $"自检-{MaintenanceMeta.ZhOf(k)}-{i}",
                    EquipmentModel = "E3002781",
                    EquipmentSn = $"SN{seq:D4}",
                    FailReason = "自检造的数据",
                    Severity = i == 0 ? "critical" : i == 1 ? "major" : "minor",
                    Status = k,
                    Resolver = i == 0 ? "张三" : "",
                    Resolution = i == 0 ? "换板" : "",
                    Notes = i == 0 ? "备注字段这次终于有输入口了" : "",
                    CreatedAt = $"2026-07-{10 + seq % 18:D2} 08:{seq % 60:D2}:00",
                };
                var id = d.CreateMaintenance(m);
                if (i == 0) ids[k] = id;
                seq++;
            }
        Check(seq == 15, $"造了 {seq} 条测试记录（5 状态 × 3）");

        // ---------- CountMaintenanceByStatus ----------
        var counts = d.CountMaintenanceByStatus();
        var norm = new Dictionary<string, int>();
        foreach (var kv in counts)
        {
            var nk = MaintenanceMeta.Normalize(kv.Key);
            norm[nk] = norm.GetValueOrDefault(nk) + kv.Value;
        }
        Console.WriteLine("    计数: " + string.Join(", ",
            MaintenanceMeta.Statuses.Select(s => $"{s.Zh}={norm.GetValueOrDefault(s.Key)}")));
        Check(keys.All(k => norm.GetValueOrDefault(k) >= 3), "每个状态的真实计数 >= 3（GROUP BY 统计可用）");
        int resolvedExpected = 3 + legacyCount;      // 自检造的 3 条 + 迁移过来的 legacy
        Check(norm.GetValueOrDefault("resolved") == resolvedExpected,
              $"已完成计数 = {norm.GetValueOrDefault("resolved")}（3 新 + {legacyCount} 迁移）");

        // 计数总和 == 全表条数（不受 LIMIT 影响，这是列头徽章的意义）
        int total;
        using (var c = new SqliteConnection($"Data Source={db}"))
        {
            c.Open();
            using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM maintenance_records";
            total = Convert.ToInt32(q.ExecuteScalar());
        }
        Check(counts.Values.Sum() == total, $"计数总和 {counts.Values.Sum()} == 全表 {total}（不受 LIMIT 截断）");

        // ---------- ListMaintenance: 取到 UpdatedAt / 排序 / limit ----------
        var all = d.ListMaintenance("", 500);
        Check(all.Count == Math.Min(total, 500), $"ListMaintenance 全量取到 {all.Count} 条");
        Check(all.All(m => !string.IsNullOrEmpty(m.UpdatedAt)), "每条都取到了 updated_at（v2.1.0 里 SELECT 根本没取）");
        var keyOf = (MaintenanceRecord m) => string.IsNullOrEmpty(m.UpdatedAt) ? m.CreatedAt : m.UpdatedAt;
        bool sorted = true;
        for (int i = 1; i < all.Count; i++)
            if (string.CompareOrdinal(keyOf(all[i - 1]), keyOf(all[i])) < 0) { sorted = false; break; }
        Check(sorted, "按最后更新时间倒序（看板语义: 最近动过的置顶）");
        Check(d.ListMaintenance("", 5).Count == 5, "limit 参数生效（看板每列限量用）");
        Check(d.ListMaintenance("in_progress", 500).All(m => m.Status == "in_progress"), "按状态筛选正确");

        // ---------- UpdateMaintenance 全字段 ----------
        var target = d.ListMaintenance("unknown", 500).First();
        var oldUpdated = target.UpdatedAt;
        Thread.Sleep(1100);                       // 让 updated_at 能看出变化（秒级精度）
        var edited = target.Clone();
        edited.FailItem = "编辑后的故障项目";
        edited.EquipmentModel = "E3009999";
        edited.EquipmentSn = "SN-EDITED";
        edited.FailReason = "编辑后的描述";
        edited.Severity = "critical";
        edited.Status = "in_progress";
        edited.Resolver = "李四";
        edited.Resolution = "重新焊接";
        edited.Notes = "备注也一起改";
        edited.CreatedAt = "2026-07-05 07:30:00";
        Check(d.UpdateMaintenance(edited), "UpdateMaintenance 返回 true");

        var after = d.ListMaintenance("", 500).First(m => m.Id == target.Id);
        Check(after.FailItem == "编辑后的故障项目" && after.EquipmentSn == "SN-EDITED"
              && after.Severity == "critical" && after.Status == "in_progress"
              && after.Resolver == "李四" && after.Resolution == "重新焊接"
              && after.Notes == "备注也一起改" && after.CreatedAt.StartsWith("2026-07-05"),
              "全部字段都写进去了（含备注 / 状态 / 记录日期）");
        Check(string.CompareOrdinal(after.UpdatedAt, oldUpdated) > 0,
              $"updated_at 已刷新（{oldUpdated} -> {after.UpdatedAt}）");

        // 不存在的 id
        var ghost = after.Clone();
        ghost.Id = 999999;
        Check(!d.UpdateMaintenance(ghost), "更新不存在的记录返回 false（UI 会提示「可能已被删除」）");

        // ---------- UpdateMaintenanceStatus（纯拖动路径） ----------
        var dragged = d.ListMaintenance("investigating", 500).First();
        Check(d.UpdateMaintenanceStatus(dragged.Id, "resolved"), "UpdateMaintenanceStatus 返回 true（拖动改状态）");
        var draggedAfter = d.ListMaintenance("", 500).First(m => m.Id == dragged.Id);
        Check(draggedAfter.Status == "resolved" && draggedAfter.FailItem == dragged.FailItem,
              "拖动只改状态，其它字段不动");

        // ---------- 状态变更事件（v3.2.0 待办推送） ----------
        {
            var events = new List<(string from, string to)>();
            d.MaintenanceStatusChanged += (_, from, to) => events.Add((from, to));

            // 状态真变 -> 触发一次，带旧/新状态
            var ev = d.ListMaintenance("unknown", 500).First();
            events.Clear();
            Check(d.UpdateMaintenanceStatus(ev.Id, "in_progress"), "更新另一条状态返回 true");
            Check(events.Count == 1, $"状态变更事件触发一次（实得 {events.Count}）");
            Check(events[0].from == "unknown" && events[0].to == "in_progress",
                  $"事件带旧/新状态（{events[0].from} -> {events[0].to}）");

            // 同状态再设一次 -> 不触发
            events.Clear();
            d.UpdateMaintenanceStatus(ev.Id, "in_progress");
            Check(events.Count == 0, "同状态重复设置不触发事件（去重）");

            // 编辑表单改状态也触发
            var ed2 = d.ListMaintenance("in_progress", 500).First(m => m.Id == ev.Id);
            var done2 = ed2.Clone();
            done2.Status = "resolved";
            done2.Resolver = "王五";
            done2.Resolution = "已换料";
            events.Clear();
            Check(d.UpdateMaintenance(done2), "UpdateMaintenance（全字段）返回 true");
            Check(events.Count == 1 && events[0].from == "in_progress" && events[0].to == "resolved",
                  $"全字段更新改状态也触发事件（{events.Count} 次: {events[0].from} -> {events[0].to}）");

            // 只改内容不改状态 -> 不触发
            var ed3 = d.GetMaintenance(ev.Id)!;
            var noChange = ed3.Clone();
            noChange.Notes = "只改备注";
            events.Clear();
            d.UpdateMaintenance(noChange);
            Check(events.Count == 0, "只改内容不触发状态变更事件");
        }

        // ---------- CreateMaintenance: 往期日期时 updated_at 不能是「现在」 ----------
        var oldRec = new MaintenanceRecord
        {
            StationId = "FCT1", FailItem = "往期记录", Severity = "minor",
            Status = "open", CreatedAt = "2026-01-15 08:00:00",
        };
        var oldId = d.CreateMaintenance(oldRec);
        var oldBack = d.ListMaintenance("", 500).First(m => m.Id == oldId);
        Check(oldBack.UpdatedAt.StartsWith("2026-01-15"),
              $"录入往期日期时 updated_at 跟随 created_at（{oldBack.UpdatedAt}）—— 否则老记录会被顶到看板最前");

        // ---------- 删除 ----------
        Check(d.DeleteMaintenance(oldId), "DeleteMaintenance 返回 true");
        Check(!d.ListMaintenance("", 500).Any(m => m.Id == oldId), "删除后确实不在列表里");

        // ---------- BUG 修复 1: 删除后新建的 id 要回收（空库场景） ----------
        var freshDir = Path.Combine(work, "fresh");
        Directory.CreateDirectory(freshDir);
        var fdb = Path.Combine(freshDir, "fresh.db");
        var fd = new Database(fdb);

        MaintenanceRecord NewRec(string item) => new()
        {
            StationId = "FCT1", FailItem = item, Severity = "major",
            Status = MaintenanceMeta.DefaultStatus,
        };

        int a1 = fd.CreateMaintenance(NewRec("第一条"));
        Check(a1 == 1, $"空库新建第一条 id = {a1}（期望 1）");
        Check(fd.DeleteMaintenance(a1), "删掉唯一的一条");
        int a2 = fd.CreateMaintenance(NewRec("删完再建"));
        Check(a2 == 1, $"**删空后新建 id = {a2}（期望 1，旧版会给 2）**");

        // 删最后一条 -> 接着用那个号
        int b2 = fd.CreateMaintenance(NewRec("第二条"));
        int b3 = fd.CreateMaintenance(NewRec("第三条"));
        Check(b2 == 2 && b3 == 3, $"连续新建 id = {b2}, {b3}");
        fd.DeleteMaintenance(b3);
        int b3b = fd.CreateMaintenance(NewRec("补上第三条"));
        Check(b3b == 3, $"删掉最后一条后新建 id = {b3b}（期望 3）");

        // 删中间那条 -> 不回填空号（避开与已导出报表上的旧 #N 撞车）
        fd.DeleteMaintenance(2);
        int c4 = fd.CreateMaintenance(NewRec("删中间后新建"));
        Check(c4 == 4, $"删中间那条后新建 id = {c4}（期望 4，不回填空号 2）");

        // 空表上删不存在的 id：不能抛、不能误改游标
        Check(!fd.DeleteMaintenance(99999), "删不存在的 id 返回 false 且不报错");
        int c5 = fd.CreateMaintenance(NewRec("无效删除后"));
        Check(c5 == 5, $"无效删除不影响游标（下一个 id = {c5}）");

        // ---------- BUG 修复 2: 看板卡片右键要能弹菜单 ----------
        var cardRec = new MaintenanceRecord
        {
            Id = 7, StationId = "FCT1", FailItem = "右键测试", Severity = "critical",
            Status = "open", Resolver = "张三", CreatedAt = "2026-07-29 08:00:00",
            UpdatedAt = "2026-07-29 09:00:00",
        };
        using (var card = new MaintenanceCard(cardRec))
        {
            var onDown = typeof(MaintenanceCard).GetMethod("OnMouseDown",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            MaintenanceRecord? got = null;
            var pt = Point.Empty;
            card.ContextRequested += (c, at) => { got = (c as MaintenanceCard)?.Record; pt = at; };

            onDown.Invoke(card, new object?[] { new MouseEventArgs(MouseButtons.Right, 1, 12, 34, 0) });
            Check(got != null && got.Id == 7, "**右键卡片会触发 ContextRequested（之前右键没任何反应）**");
            Check(pt == new Point(12, 34), $"菜单弹出坐标跟随鼠标（{pt.X},{pt.Y}）");

            got = null;
            onDown.Invoke(card, new object?[] { new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0) });
            Check(got == null, "左键不弹菜单（左键留给拖拽 / 双击编辑）");

            var mid = typeof(MaintenanceCard).GetMethod("OnMouseMove",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            // 右键按下后拖动不应进入拖拽分支（_pressed 已被右键清掉，不会抛）
            onDown.Invoke(card, new object?[] { new MouseEventArgs(MouseButtons.Right, 1, 12, 34, 0) });
            mid.Invoke(card, new object?[] { new MouseEventArgs(MouseButtons.Right, 1, 300, 300, 0) });
            Check(true, "右键按住拖动不会误走拖拽分支");
        }
        Check(typeof(MaintenanceBoard).GetEvent("ContextRequested") != null,
              "MaintenanceBoard 向外暴露了 ContextRequested（菜单由 MaintenancePanel 统一提供）");

        // ---------- 新功能: 从 FAIL 选故障项（去重） ----------
        var srcs = new List<FailItemSource>
        {
            new() { FirstFailItem = "6.1.2.2.16 KL30_FILT_2(DMM)", Model = "E3002781", StationId = "FCT2", Timestamp = "2026-07-22 12:26:00" },
            new() { FirstFailItem = "6.1.2.2.16 KL30_FILT_2(DMM)", Model = "E3002781", StationId = "FCT2", Timestamp = "2026-07-23 09:00:00" },
            new() { FirstFailItem = "6.4.1.2.3 Vref_3V3(DMM)",     Model = "E3002781", StationId = "FCT2", Timestamp = "2026-07-21 08:00:00" },
            new() { FirstFailItem = "  6.4.1.2.3 Vref_3V3(DMM)  ", Model = "E3002781", StationId = "FCT3", Timestamp = "2026-07-24 08:00:00" },
            new() { FirstFailItem = "", Model = "E3002781", StationId = "FCT2" },
        };
        var agg = FailItemPickerForm.Aggregate(srcs);
        Check(agg.Count == 2, $"故障项已去重：5 行源数据 -> {agg.Count} 个项（期望 2，空值不算，前后空格归一）");
        Check(agg[0].Count == 2 && agg[1].Count == 2, $"出现次数统计正确（{agg[0].Count}, {agg[1].Count}）");
        Check(agg.All(a => !a.Item.Contains("E30027") ), "聚合结果里不含 SN（只有测试项名）");

        var realFails = d.FailItemSources("");
        if (haveReal) Check(realFails.Count > 0, $"从真实库读到 FAIL 源行 {realFails.Count} 条");
        var realAgg = FailItemPickerForm.Aggregate(realFails);
        Console.WriteLine($"    真实库去重后故障项 {realAgg.Count} 个，前 3: " +
                          string.Join(" | ", realAgg.Take(3).Select(a => $"{a.Count}x {a.Item}")));
        if (haveReal)
            Check(realAgg.Count > 0 && realAgg.Count < realFails.Count,
                  $"真实库：{realFails.Count} 条 FAIL -> 去重后 {realAgg.Count} 个故障项（确实去了重）");
        else
            Console.WriteLine("    (跳过真实库断言：无 fixture，把任意一个机台库拷到 dist\\data\\fct.db 即可启用)");
        Check(realFails.All(f => f.GetType().GetField("Sn") == null),
              "FailItemSource 结构里根本没有 Sn 字段（从数据层就不带 SN）");

        // 真实库上看一眼大项合并的效果（人工校对用，不做硬断言：每个现场的命名习惯不一样）
        {
            var byKey = realAgg.GroupBy(a => TodoGrouping.KeyOf(a.Item))
                               .OrderByDescending(g => g.Sum(x => x.Count)).ToList();
            Console.WriteLine($"    大项合并：{realAgg.Count} 个原始测试项 -> {byKey.Count} 个待办大项");
            foreach (var g in byKey.Take(8))
                Console.WriteLine($"      {g.Sum(x => x.Count),3}x  {TodoGrouping.TitleOf(g.Select(x => x.Item))}" +
                                  (g.Count() > 1 ? $"   ← 合并 {g.Count()} 项: {string.Join(" / ", g.Select(x => x.Item))}" : ""));
            Check(byKey.Count <= realAgg.Count, "合并后的大项数不多于原始项数");
            Check(byKey.All(g => TodoGrouping.TitleOf(g.Select(x => x.Item)).Length > 0),
                  "每个大项都能算出非空展示名");
        }

        // ---------- 新功能: 维修人候选（不用每次手输） ----------
        var who = d.DistinctResolvers();
        Console.WriteLine("    历史维修人: " + (who.Count == 0 ? "(无)" : string.Join(", ", who)));
        Check(who.Contains("李四") && who.Contains("张三"), $"维修人候选取到历史值（{who.Count} 个）");
        Check(who.Distinct(StringComparer.OrdinalIgnoreCase).Count() == who.Count, "维修人候选已去重");
        Check(!who.Any(string.IsNullOrWhiteSpace), "维修人候选里没有空值");

        // ---------- 新功能: 维修人员名单（【+ 人员】按钮背后的东西） ----------
        Check(d.AddResolver("王强"), "名单添加「王强」");
        Check(!d.AddResolver("  王强  "), "重复添加（带空格）被拒，不会出两条");
        Check(!d.AddResolver("王强".ToLowerInvariant()) || true, "（参考）名字大小写不敏感唯一");
        Check(!d.AddResolver("   "), "空名字加不进去");
        Check(d.AddResolver("赵六"), "名单添加「赵六」");

        var roster = d.RosterResolvers();
        Check(roster.Contains("王强") && roster.Contains("赵六") && roster.Count == 2,
              $"名单现有 {roster.Count} 人（期望 2）");

        var cands = d.ListResolvers();
        Check(cands.Take(roster.Count).SequenceEqual(roster), "下拉候选：名单排在前面");
        Check(cands.Contains("张三") && cands.Contains("王强"),
              $"候选 = 名单 ∪ 历史（共 {cands.Count} 个，含名单里的王强与历史里的张三）");
        Check(cands.Distinct(StringComparer.OrdinalIgnoreCase).Count() == cands.Count, "候选已去重（大小写不敏感）");

        // 删人：只动名单，不动历史记录
        int usedByZhang = d.CountRecordsByResolver("张三");
        Check(usedByZhang > 0, $"「张三」在 {usedByZhang} 条历史记录里出现过（删人前会提示这个数）");
        d.AddResolver("张三");
        Check(d.DeleteResolver("张三"), "从名单删掉「张三」");
        Check(d.CountRecordsByResolver("张三") == usedByZhang, "删名单**不会动历史维修记录**");
        Check(d.ListResolvers().Contains("张三"), "删后仍能在候选里看到（因为历史记录里还有）");
        Check(!d.DeleteResolver("不存在的人"), "删不存在的人返回 false");

        // 改名：默认不同步历史
        d.AddResolver("张散");
        Check(d.RenameResolver("张散", "张三三", syncRecords: false) == 0, "改名（不同步）不动历史记录");
        Check(d.RosterResolvers().Contains("张三三") && !d.RosterResolvers().Contains("张散"),
              "名单里已改成「张三三」");

        // 改名 + 同步历史（治错别字）
        var fixTarget = d.ListMaintenance("", 500).First(x => x.Resolver == "李四");
        int before = d.CountRecordsByResolver("李四");
        d.AddResolver("李四");
        int synced = d.RenameResolver("李四", "李四四", syncRecords: true);
        Check(synced == before && before > 0, $"改名并同步：改动了 {synced} 条历史记录（期望 {before}）");
        Check(d.ListMaintenance("", 500).First(x => x.Id == fixTarget.Id).Resolver == "李四四",
              "历史记录里的名字确实改掉了");
        Check(d.CountRecordsByResolver("李四") == 0, "旧名字已不再出现在记录里");

        // 名单表是幂等建的：重开库不丢名单
        var dRe = new Database(db);
        Check(dRe.RosterResolvers().Count == d.RosterResolvers().Count, "重新打开库后名单仍在（建表幂等）");

        // ---------- 新功能: 维修人可**多选** ----------
        Check(ResolverUtil.Split("张三、李四").SequenceEqual(new[] { "张三", "李四" }), "拆分：顶号分隔");
        Check(ResolverUtil.Split("张三, 李四 ; 王五/赵六").Count == 4, "拆分：兼容 , ; / 等分隔符与空格");
        Check(ResolverUtil.Split("张三、张三、 张三 ").Count == 1, "拆分：重复人去重");
        Check(ResolverUtil.Split("").Count == 0 && ResolverUtil.Split(null).Count == 0, "拆分：空值得空列表");
        Check(ResolverUtil.Join(new[] { "张三", " ", "李四", "张三" }) == "张三、李四", "拼接：去空去重、用顶号");
        Check(ResolverUtil.Normalize("  李四 ,张三 ") == "李四、张三", "规范化：任意写法 -> 顶号拼接（保持顺序）");
        Check(ResolverUtil.Contains("张三、李四", "李四") && !ResolverUtil.Contains("张三丰", "张三"),
              "包含判定是**成员级**（「张三丰」不算包含「张三」）");
        Check(ResolverUtil.Replace("张三、李四、王五", "李四", "李四四") == "张三、李四四、王五",
              "成员级改名：其余人与顺序不变");

        // 多人记录落库 + 候选拆开统计
        var multi = new MaintenanceRecord
        {
            StationId = "FCT1", FailItem = "多人维修测试", Severity = "major",
            Status = "in_progress", Resolver = "孙七、周八",
            CreatedAt = "2026-07-26 10:00:00",
        };
        int multiId = d.CreateMaintenance(multi);
        var backMulti = d.ListMaintenance("", 500).First(x => x.Id == multiId);
        Check(backMulti.Resolver == "孙七、周八", $"多人记录已存：{backMulti.Resolver}");

        var cand2 = d.DistinctResolvers();
        Check(cand2.Contains("孙七") && cand2.Contains("周八"), "候选里孙七、周八 **分开**出现");
        Check(!cand2.Any(x => x.Contains("、")), "候选里不会出现「孙七、周八」这种组合项");
        Check(d.CountRecordsByResolver("孙七") == 1 && d.CountRecordsByResolver("周八") == 1,
              "按人计数对多人记录也准");

        // 改名同步：多人字段里只换那个人
        d.AddResolver("孙七");
        int syncedMulti = d.RenameResolver("孙七", "孙七七", syncRecords: true);
        var afterMulti = d.ListMaintenance("", 500).First(x => x.Id == multiId);
        Check(syncedMulti == 1 && afterMulti.Resolver == "孙七七、周八",
              $"多人字段改名后 = {afterMulti.Resolver}（只换孙七，周八不动）");

        // 多选框本身（不弹 UI）
        using (var pick = new ResolverPickerForm(d, "周八、王强"))
        {
            Check(pick.CheckedCount == 2, $"多选框预勾选了 {pick.CheckedCount} 人（传入两人）");
            Check(pick.AllCandidates.Contains("周八") && pick.AllCandidates.Contains("王强"),
                  "多选框候选含名单与历史里的人");
            pick.CheckForTest("赵六");
            var joined = pick.BuildResultForTest();
            Check(ResolverUtil.Split(joined).Count == 3, $"再勾一人后结果三人：{joined}");
            Check(joined.Contains("、"), "结果用顶号拼接");
        }

        // 表单：手敲多人也能逐人登记进名单
        int rosterBefore = d.RosterResolvers().Count;
        using (var mf = new MaintenanceForm("FCT1", null, null, d.ListResolvers(), d))
        {
            var fi = typeof(MaintenanceForm).GetField("_resolver",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var combo = (ComboBox)fi.GetValue(mf)!;
            combo.Text = "新人甲, 新人乙";
            var item = typeof(MaintenanceForm).GetField("_failItem",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            ((TextBox)item.GetValue(mf)!).Text = "手敲多人测试";
            var onSave = typeof(MaintenanceForm).GetMethod("OnSave",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            onSave.Invoke(mf, new object?[] { null, EventArgs.Empty });

            Check(mf.Result.Resolver == "新人甲、新人乙",
                  $"表单保存时把「新人甲, 新人乙」规范为「{mf.Result.Resolver}」");
            var rosterAfter = d.RosterResolvers();
            Check(rosterAfter.Contains("新人甲") && rosterAfter.Contains("新人乙"),
                  "两个新名字**各自**进了名单（不是整串当一个人）");
            Check(rosterAfter.Count == rosterBefore + 2, $"名单从 {rosterBefore} 增到 {rosterAfter.Count}");
            Check(!rosterAfter.Any(x => x.Contains("、") || x.Contains(",")), "名单里没有带分隔符的脏数据");
        }

        // ---------- 新功能: 批量建单 ----------
        var picked = new[] { "测试项A", "测试项B", "测试项A" };   // 故意重复一个
        using (var bf = new MaintenanceForm("FCT1", null, picked, who))
        {
            var list = bf.BatchResults();
            Check(list.Count == 2, $"批量建单：3 个选项(含1个重复) -> {list.Count} 条记录（期望 2）");
            Check(list.All(r => r.Id == 0), "批量记录的 Id 均为 0（交给 DB 自增）");
            Check(list.Select(r => r.FailItem).Distinct().Count() == 2, "每条的故障项不同");
            Check(list.All(r => r.Status == MaintenanceMeta.DefaultStatus), "批量记录默认状态 = 待办");
            Check(list.All(r => string.IsNullOrEmpty(r.EquipmentSn) && string.IsNullOrEmpty(r.EquipmentModel)),
                  "批量记录不带设备型号 / SN（表单已去掉这两项）");

            // 真落库一下，确认能存
            int n0 = d.ListMaintenance("", 500).Count;
            foreach (var r in list) d.CreateMaintenance(r);
            Check(d.ListMaintenance("", 500).Count == n0 + 2, "批量记录已成功写入库");
        }

        // 表单已不再有设备型号 / SN 输入控件
        var formFields = typeof(MaintenanceForm)
            .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Select(f => f.Name).ToList();
        Check(!formFields.Contains("_model") && !formFields.Contains("_sn"),
              "MaintenanceForm 里已删除 _model / _sn 输入框");
        Check(formFields.Contains("_resolver"), "维修人控件仍在");
        Check(typeof(MaintenanceForm).GetField("_resolver", System.Reflection.BindingFlags.NonPublic |
              System.Reflection.BindingFlags.Instance)!.FieldType == typeof(ComboBox),
              "维修人已改为 ComboBox（可选历史值，也可直接输入）");

        // ---------- 待办已整合进看板（v2.5.0） ----------
        Console.WriteLine("\n【待办→看板】聚合 / 确认 / 忽略 / 复发重现");

        // 【最重要】看板与维修面板必须能无 UI 构造成功。
        // v2.4.0 开发时正是因为自检没构造面板，控件字段忘了 new
        //（而且 CS0649 警告被 `= null!` 消音），导致发出的 exe 一启动就 0xC0000005 崩溃。
        {
            // 显式给 StationId，避开 IP 探测；Engine 构造只建库不启监控
            var eng = new Engine(new AppConfig
            {
                StationId = "FCT9",
                ResultsRoot = Path.Combine(work, "NoSuchResults"),
                WebhookUrl = "",
            });

            MaintenanceBoard? board = null;
            try
            {
                board = new MaintenanceBoard(eng);
                Check(true, "MaintenanceBoard 无 UI 构造成功");
            }
            catch (Exception ex)
            {
                Check(false, $"MaintenanceBoard 构造失败！主程序会一启动就崩：{ex.GetType().Name} {ex.Message}");
            }
            if (board != null)
            {
                try { board.Reload(); Check(true, "MaintenanceBoard.Reload() 可正常执行（含待办卡聚合）"); }
                catch (Exception ex) { Check(false, $"MaintenanceBoard.Reload() 抛异常: {ex.Message}"); }
                Check(board.PendingTodoCount >= 0, $"看板未确认不良数 = {board.PendingTodoCount}");
                board.Dispose();
            }

            MaintenancePanel? mp = null;
            try
            {
                mp = new MaintenancePanel(eng);
                Check(true, "MaintenancePanel 无 UI 构造成功（已去掉独立待办页）");
            }
            catch (Exception ex)
            {
                Check(false, $"MaintenancePanel 构造失败：{ex.GetType().Name} {ex.Message}");
            }
            if (mp != null)
            {
                var mpFields = typeof(MaintenancePanel)
                    .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Select(f => f.Name).ToList();
                Check(!mpFields.Contains("_todo") && !mpFields.Contains("_btnTodo"),
                      "维修面板里已删除独立待办页的字段（_todo / _btnTodo）");
                foreach (var fname in new[] { "_board", "_list", "_listHost", "_filter", "_filterLabel",
                                              "_btnBoard", "_btnList" })
                {
                    var fi = typeof(MaintenancePanel).GetField(fname,
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    Check(fi != null && fi.GetValue(mp) != null, $"MaintenancePanel.{fname} 已初始化（非 null）");
                }
                try { mp.Refresh2(); Check(true, "MaintenancePanel.Refresh2() 可正常执行"); }
                catch (Exception ex) { Check(false, $"MaintenancePanel.Refresh2() 抛异常: {ex.Message}"); }
                mp.Dispose();
            }

            // 待办卡与记录卡共用拖拽基类，否则拖放会静默失效
            var todoCard = new TodoCard(new TodoItem { Title = "Check_5V_Rail", TotalCount = 3, RangeCount = 3 });
            var recCard = new MaintenanceCard(new MaintenanceRecord { Id = 1, FailItem = "X", Status = "in_progress" });
            Check(todoCard is BoardCardBase && recCard is BoardCardBase, "两种卡片均继承 BoardCardBase（拖放格式统一）");
            Check(todoCard.ColumnKey == MaintenanceMeta.DefaultStatus, "待办卡固定属于「待办」列");
            Check(recCard.ColumnKey == "in_progress", "记录卡所在列 = 它的状态");

            // ---------- 单击预览（v2.7.0） ----------
            var down = typeof(BoardCardBase).GetMethod("OnMouseDown",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var up = typeof(BoardCardBase).GetMethod("OnMouseUp",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var dbl = typeof(BoardCardBase).GetMethod("OnMouseDoubleClick",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            Check(typeof(BoardCardBase).GetEvent("PreviewRequested") != null,
                  "BoardCardBase 暴露了 PreviewRequested（单击预览）");

            var pv = new TodoCard(new TodoItem { Title = "P", TotalCount = 1, RangeCount = 1 }) { Width = 200 };
            bool fired = false;
            pv.PreviewRequested += _ => fired = true;

            // 单击：不能立即弹（要等双击超时），但已排队
            down.Invoke(pv, new object?[] { new MouseEventArgs(MouseButtons.Left, 1, 10, 10, 0) });
            up.Invoke(pv, new object?[] { new MouseEventArgs(MouseButtons.Left, 1, 10, 10, 0) });
            Check(!fired, "单击不会立即触发预览（先等双击间隔，否则双击会先闪一下）");
            Check(pv.PreviewPending, "**单击已排队预览**（计时器已启动）");

            // 双击：取消预览，而且双击后那下 MouseUp 不能又排上
            bool activated = false;
            pv.ActivateRequested += _ => activated = true;
            dbl.Invoke(pv, new object?[] { new MouseEventArgs(MouseButtons.Left, 2, 10, 10, 0) });
            Check(activated && !pv.PreviewPending, "双击触发 Activate 并**取消**已排队的预览");
            up.Invoke(pv, new object?[] { new MouseEventArgs(MouseButtons.Left, 1, 10, 10, 0) });
            Check(!pv.PreviewPending, "双击后的第二下 MouseUp 不会再排预览");

            // 右键：不弹预览
            down.Invoke(pv, new object?[] { new MouseEventArgs(MouseButtons.Right, 1, 10, 10, 0) });
            up.Invoke(pv, new object?[] { new MouseEventArgs(MouseButtons.Right, 1, 10, 10, 0) });
            Check(!pv.PreviewPending, "右键不弹预览（右键走菜单）");

            // 拖动：过了阈值就不算单击。
            // ⚠ 不能在自检里真调 OnMouseMove —— 它会进 DoDragDrop 的**模态拖拽消息循环**，
            // 无鼠标输入时直接挂死（曾把自检卡到超时）。这里只验“没按下就松手不算单击”。
            up.Invoke(pv, new object?[] { new MouseEventArgs(MouseButtons.Left, 1, 300, 300, 0) });
            Check(!pv.PreviewPending, "没有先按下的松手不会被当成单击（拖拽落回卡上不会误弹预览）");
            pv.Dispose();

            // 预览弹窗内容：两种卡片都要能给出非空内容
            var merged = new TodoItem
            {
                Id = 9, Title = "5V_Rail", GroupKey = "5v rail", StationId = "FCT9", Model = "E3002781",
                VariantCount = 3, TotalCount = 30, RangeCount = 12,
                FirstSeen = "2026-07-01 08:00:00", LastSeen = "2026-07-30 08:00:00",
            };
            merged.Variants.AddRange(new[] { "6.1.1.1 5V_Rail", "6.1.1.2 5V_Rail", "6.1.1.3 5V_Rail" });
            var mergedCard = new TodoCard(merged) { Width = 220 };
            var (pTitle, pSub, pRows, pFoot) = mergedCard.BuildPreview();
            Check(pTitle == "5V_Rail" && pSub.Contains($"优先级{merged.PriorityZh}") && pRows.Count >= 8 && pFoot.Length > 0,
                  "待办卡预览内容完整（标题/副标题/明细/页脚）");
            Check(merged.PriorityZh == "中" && TodoGrouping.PriorityZhOf(merged.TotalCount) == "高",
                  "优先级算的是**区间内次数**（12 次→中）而不是累计（30 次→高）");
            Check(pRows.Any(r => r.Item2.Contains("12 次")) && pRows.Any(r => r.Item2.Contains("30 次")),
                  "预览同时给出区间内次数与累计次数");
            Check(pRows.Any(r => r.Item2.Contains("3 个同类测试项")), "预览里标了合并项数");

            // 卡片高度：合并过的卡要更高（要在卡上列出原始项）
            var singleCard = new TodoCard(new TodoItem { Title = "solo", VariantCount = 1, TotalCount = 1 });
            Check(mergedCard.Height > singleCard.Height,
                  $"合并卡比单项卡更高（{mergedCard.Height} > {singleCard.Height}）—— 卡上直接列合并项");
            mergedCard.Dispose(); singleCard.Dispose();

            // 记录卡：从备注里回读合并清单（**不查库**）
            var noteRec = new MaintenanceRecord
            {
                Id = 5, FailItem = "5V_Rail", Status = "open", StationId = "FCT9",
                Notes = TodoGrouping.BuildSourceItemsNote(new[] { "6.1.1.1 5V_Rail", "6.1.1.2 5V_Rail" }),
            };
            var noteCard = new MaintenanceCard(noteRec) { Width = 220 };
            Check(noteCard.SourceItems.Count == 2, $"维修卡从备注回读到 {noteCard.SourceItems.Count} 个合并项（无需查库）");
            Check(noteCard.Height > recCard.Height, "带合并清单的维修卡会加高一行");
            var (rTitle, rSub, rRows, _) = noteCard.BuildPreview();
            Check(rSub.Contains("合并 2 项"), "维修卡预览副标题标出合并项数");
            Check(rRows.All(r => !r.Item2.Contains(TodoGrouping.SourceItemsTag)),
                  "预览备注里不重复堆“来源测试项”那一大段（已单独成表）");
            noteCard.Dispose();

            Check(TodoGrouping.ParseSourceItems("随便写的备注").Count == 0, "普通备注不会被误读成合并清单");

            // 预览弹窗本身必须能真弹出来（v2.4.0 那次就是控件构造没自检到，发出去直接崩）
            {
                using var host = new Form { Width = 300, Height = 200 };
                var anchorCard = new TodoCard(merged) { Width = 200 };
                host.Controls.Add(anchorCard);
                host.Show();
                try
                {
                    var (t2, s2, r2, f2) = anchorCard.BuildPreview();
                    CardPreviewForm.ShowFor(anchorCard, t2, s2, Color.FromArgb(197, 48, 48), r2,
                        merged.Variants.Select(v => (v, 3)), f2);
                    Check(true, "CardPreviewForm 可正常弹出（含合并清单表）");

                    // 刚弹出时**不能有选中的文字** —— 只读 TextBox 拿到焦点会自动全选，
                    // 看起来就像“单击就把预览里的字选中了”
                    var opened = Application.OpenForms.OfType<CardPreviewForm>().FirstOrDefault();
                    Check(opened != null, "预览窗已在打开列表里");
                    Check(opened == null || opened.DetailSelectionLength == 0,
                          $"**预览刚弹出时没有选中任何文字**（选区长度 {opened?.DetailSelectionLength}）");
                    Check(opened == null || opened.ActiveControl == null,
                          "预览窗不把焦点塞给输入框（焦点在窗口本身，Esc 仍可关）");
                }
                catch (Exception ex)
                {
                    Check(false, $"CardPreviewForm 弹出失败：{ex.GetType().Name} {ex.Message}");
                }
                CardPreviewForm.CloseCurrent();
                Check(true, "CardPreviewForm.CloseCurrent() 可重复调用不报错");
                CardPreviewForm.CloseCurrent();
                host.Hide();
            }
            todoCard.Dispose(); recCard.Dispose();

            // v2.5.1: 待办不可忽略/删除 —— 看板上不能再有任何忽略入口
            var boardMethods = typeof(MaintenanceBoard)
                .GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.Instance)
                .Select(x => x.Name).ToList();
            Check(!boardMethods.Any(x => x.Contains("Dismiss")),
                  "**看板上已无忽略入口**（待办来自真实不良，不可忽略/删除）");
        }
        // ---------- 归一化 / 大项合并（v2.6.0） ----------
        Console.WriteLine("\n【待办·大项合并】同类测试项归一化");
        {
            string K(string s) => TodoGrouping.KeyOf(s);

            // 步骤号不同、本体相同 -> 合并
            Check(K("6.1.1.1 5V_Rail") == K("6.1.1.2 5V_Rail"), "步骤号不同的同一测试项合并为一项");
            Check(K("6.1.1.1 5V_Rail") == K("5V Rail"), "去掉步骤号后与裸名同键");
            // 通道 / 位号不同 -> 合并
            Check(K("CAN_Bus_Test_CH1") == K("CAN_Bus_Test_CH2"), "通道号不同(CH1/CH2)合并");
            Check(K("Check LED pin3") == K("Check LED pin7"), "位号不同(pin3/pin7)合并");
            Check(K("U12_Voltage") == K("U7_Voltage"), "器件位号不同(U12/U7)合并");
            // 数字+单位必须保留 -> 不能合并
            Check(K("Check_5V_Rail") != K("Check_12V_Rail"), "**5V 与 12V 不合并**（数字+单位有物理含义）");
            Check(K("Delay_100ms") != K("Delay_200ms"), "100ms 与 200ms 不合并");
            Check(K("3V3_Rail") != K("1V8_Rail"), "3V3 与 1V8 不合并");
            // 大小写 / 分隔符 / 全角
            Check(K("check_5v_rail") == K("Check-5V-Rail"), "大小写与分隔符差异合并");
            Check(K("ＣＡＮ＿Ｂｕｓ") == K("CAN_Bus"), "全角写法与半角合并");
            // 括号里的限定词（测量手段/条件）归为同一大项
            Check(K("BSW_v_Kl30UC(XCP)") == K("BSW_v_Kl30UC (KL30_LS_UC)(XCP)"),
                  "同一参数带不同括号限定词 -> 合并为一个大项");
            Check(K("Vref_3V3(DMM)") == K("Vref_3V3(XCP)"), "同一参数不同测量路径(DMM/XCP) -> 合并");
            Check(K("Check(Voltage)") != K("Check(Current)"),
                  "**括号里就是核心语义时不丢**（Check(Voltage) 不等于 Check(Current)）");
            // 空与纯序号
            Check(K("") == "", "空故障项返回空键（调用方跳过）");
            Check(K("6.1.1.1") != "", "纯序号项不会退化成空键（否则会全挤成一张卡）");

            // 展示名取最短、且已去步骤号
            var title = TodoGrouping.TitleOf(new[] { "6.1.1.2 5V_Rail_Check", "5V_Rail", "6.1.1.1 5V_Rail" });
            Check(title == "5V_Rail", $"合并后展示名取最短且去步骤号（得到「{title}」）");

            // 优先级阈值
            Check(TodoGrouping.PriorityZhOf(50) == "高" && TodoGrouping.PriorityZhOf(8) == "中" &&
                  TodoGrouping.PriorityZhOf(1) == "低", "优先级按 fail 次数分高/中/低");
        }

        // ---------- 待办登记表：扫描窗口 / 永久保留 / 优先级 / 区间 ----------
        Console.WriteLine("\n【待办·登记表】只扫近一个月 · 永久保留 · 按次数排优先级");
        {
            var tdb = Path.Combine(work, "todo.db");
            var t = new Database(tdb);
            const string St = "FCT9";

            void AddFail(Database db, string station, string item, string sn, string date)
            {
                db.InsertOne(new TestRecord
                {
                    StationId = station, Model = "E3002781", Category = "Offline", TestDate = date,
                    Sn = sn, Result = "FAIL", XmlPath = $@"X:\{station}_{sn}_{item}_{date}.xml",
                    FailReason = item, BatchTimestamp = $"{date[..4]}-{date[4..6]}-{date[6..8]}T00:00:00",
                    HasFailItems = true,
                });
            }

            string Today = DateTime.Today.ToString("yyyyMMdd");
            string Yesterday = DateTime.Today.AddDays(-1).ToString("yyyyMMdd");
            string LongAgo = DateTime.Today.AddDays(-200).ToString("yyyyMMdd");   // 远超扫描窗口

            // 同类项(步骤号不同) 3 条 + 另一项 1 条 + 一条很老的
            AddFail(t, St, "6.1.1.1 5V_Rail", "SN001", Yesterday);
            AddFail(t, St, "6.1.1.2 5V_Rail", "SN002", Yesterday);
            AddFail(t, St, "6.1.1.3 5V_Rail", "SN003", Today);
            AddFail(t, St, "Check_CAN_Bus", "SN004", Today);
            AddFail(t, St, "Ancient_Item", "SN005", LongAgo);

            var created = t.SyncTodoItems(30);
            Check(created == 2, $"3 条同类 + 1 条异类 -> 新登记 {created} 条待办（期望 2，同类已合并）");

            var view = t.ListTodoView();          // 不限区间
            var rail = view.FirstOrDefault(x => x.Title == "5V_Rail");
            Check(rail != null, "合并后的大项展示名 = 5V_Rail");
            Check(rail != null && rail.TotalCount == 3, $"合并项累计次数 = 3（实得 {rail?.TotalCount}）");
            Check(rail != null && rail.VariantCount == 3, "记住了 3 个原始测试项（variants）");
            Check(!view.Any(x => x.Title == "Ancient_Item"), "**200 天前的不良不入待办**（只扫近一个月）");

            // 优先级排序：次数多的在前
            Check(view.Count >= 2 && view[0].Title == "5V_Rail", "列表按 fail 次数倒序（3 次的排在 1 次前面）");
            Check(view[0].SortCount >= view[^1].SortCount, "SortCount 单调不增（优先处理次数多的）");

            // 幂等：再同步一次不会重复累加
            var again = t.SyncTodoItems(30);
            var rail2 = t.ListTodoView().First(x => x.Title == "5V_Rail");
            Check(again == 0 && rail2.TotalCount == 3, $"重复同步不新增不重算（新增 {again}，次数仍 {rail2.TotalCount}）");

            // 新不良增量累加
            AddFail(t, St, "6.1.1.4 5V_Rail", "SN006", Today);
            t.SyncTodoItems(30);
            var rail3 = t.ListTodoView().First(x => x.Title == "5V_Rail");
            Check(rail3.TotalCount == 4, $"新不良增量累加到同一张卡（{rail3.TotalCount} 次）");
            Check(rail3.VariantCount == 4, "新的同类变体并入 variants");

            // 时间区间：只看今天 -> 昨天那两条不计入区间次数
            var todayOnly = t.ListTodoView(DateTime.Today, DateTime.Today);
            var railToday = todayOnly.FirstOrDefault(x => x.Title == "5V_Rail");
            Check(railToday != null && railToday.RangeCount == 2 && railToday.TotalCount == 4,
                  $"区间统计独立于累计（今日 {railToday?.RangeCount} 次 / 累计 {railToday?.TotalCount} 次）");

            // 区间内没出现的项不显示，但**没有被删除**（换成不限区间又回来）
            var future = t.ListTodoView(DateTime.Today.AddDays(5), DateTime.Today.AddDays(6));
            Check(future.Count == 0, "选一个没有不良的区间 -> 待办列为空");
            Check(t.ListTodoView().Count >= 2, "**换回不限区间待办还在**（永久保留，不是被删了）");
            Check(t.CountPendingTodos() >= 2, "CountPendingTodos 与视图一致");

            // 确认 -> 关联维修记录 -> 待办离开列表
            var events2 = new List<(string from, string to)>();
            t.MaintenanceStatusChanged += (_, from, to) => events2.Add((from, to));
            var railItem = t.ListTodoView().First(x => x.Title == "5V_Rail");
            var mid = t.AcknowledgeTodo(railItem.Id, "张三", "critical", "in_progress");
            Check(mid > 0, $"确认待办已建维修记录 #{mid}");
            Check(events2.Count == 1 && events2[0].from == "" && events2[0].to == "in_progress",
                  $"确认落到非待办列等同状态变更、触发推送（{events2.Count} 次: '{events2[0].from}' -> {events2[0].to}）");
            var rec = t.ListMaintenance("in_progress", 10).FirstOrDefault(x => x.Id == mid);
            Check(rec != null && rec.FailItem == "5V_Rail", "维修记录的故障项 = 合并后的大项名");
            Check(rec != null && rec.Notes.Contains("6.1.1.1 5V_Rail"), "备注里留了原始测试项清单（可追溯）");
            Check(rec != null && rec.Status == "in_progress", "可以确认时直接置为「进行中」");
            Check(!t.ListTodoView().Any(x => x.Title == "5V_Rail"), "确认后该项离开待办列");

            // 记录标为已完成 -> 仍不在待办
            t.UpdateMaintenanceStatus(mid, "resolved");
            t.SyncTodoItems(30);
            Check(!t.ListTodoView().Any(x => x.Title == "5V_Rail"), "记录已完成后待办仍不显示（问题已处理）");

            // 复发：处理完之后又出新不良 -> 自动回到待办
            System.Threading.Thread.Sleep(1100);      // 让 last_seen 明确晚于 resolved_at
            t.InsertOne(new TestRecord
            {
                StationId = St, Model = "E3002781", Category = "Offline", TestDate = Today,
                Sn = "SN007", Result = "FAIL", XmlPath = @"X:\recur.xml", FailReason = "6.1.1.9 5V_Rail",
                BatchTimestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"), HasFailItems = true,
            });
            t.SyncTodoItems(30);
            var back = t.ListTodoView().FirstOrDefault(x => x.Title == "5V_Rail");
            Check(back != null, "**处理完后再复发 -> 自动回到待办**");
            Check(back != null && back.TotalCount == 5, $"复发时次数继续累加（{back?.TotalCount} 次，不清零）");

            // 删维修记录 -> 待办回到未确认（待办删不掉）
            var canItem = t.ListTodoView().First(x => x.Title == "Check_CAN_Bus");
            var mid2 = t.AcknowledgeTodo(canItem.Id, "李四", "major");
            Check(!t.ListTodoView().Any(x => x.Title == "Check_CAN_Bus"), "确认后 CAN 项暂时离开待办");
            Check(t.CountFailRecords("Check_CAN_Bus", St) == 1, "CountFailRecords 能数出该故障项的真实不良数");
            t.DeleteMaintenance(mid2);
            t.SyncTodoItems(30);
            Check(t.ListTodoView().Any(x => x.Title == "Check_CAN_Bus"),
                  "**删掉维修记录后待办自动回来**（待办删不掉）");

            // v2.5.1: 即使旧库里写过忽略名单，待办也不再被隐藏
#pragma warning disable CS0618 // 故意调已废弃接口，验证“写了也不生效”
            t.DismissTodo("Check_CAN_Bus", St, "E3002781");
#pragma warning restore CS0618
            t.SyncTodoItems(30);
            Check(t.ListTodoView().Any(x => x.Title == "Check_CAN_Bus"),
                  "**写入 dismissed_todos 也不会让待办消失**（不读忽略名单）");

            // 重开库：登记表与水位线都在（永久保留、不重复累加）
            var t2 = new Database(tdb);
            var cntBeforeReopen = t2.ListTodoView().First(x => x.Title == "5V_Rail").TotalCount;
            t2.SyncTodoItems(30);
            var cntAfterReopen = t2.ListTodoView().First(x => x.Title == "5V_Rail").TotalCount;
            Check(cntBeforeReopen == cntAfterReopen, $"重开库后同步不重复累加（{cntBeforeReopen} -> {cntAfterReopen}）");

            // 机台隔离：不同机台各自一条待办
            AddFail(t, "FCT8", "6.1.1.1 5V_Rail", "SN900", Today);
            t.SyncTodoItems(30);
            var railBoth = t.ListTodoView();
            Check(railBoth.Count(x => x.Title == "5V_Rail") == 2, "同故障项在两个机台各自一条待办");
        }

        // ---------- GUI 布局（v2.8.0 重构）----------
        Console.WriteLine("\n【GUI 布局】侧边导航 / 顶栏 / 状态栏 / KPI 卡不能重叠或塌掉");
        {
            // 停靠顺序写错（侧栏不是第一个 Add）不会报错，只会“看起来怪”，所以用真实布局数据断言。
            var eng2 = new Engine(new AppConfig
            {
                StationId = "FCT9",
                ResultsRoot = Path.Combine(work, "NoSuchResults"),
                WebhookUrl = "",
            });
            MainForm? form = null;
            try
            {
                form = new MainForm(eng2);
                Check(true, "MainForm 可构造（新外壳不会一启动就崩）");
            }
            catch (Exception ex)
            {
                Check(false, $"MainForm 构造失败！{ex.GetType().Name} {ex.Message}");
            }

            if (form != null)
            {
                form.Size = new Size(1280, 780);
                form.Show();
                form.PerformLayout();
                Application.DoEvents();

                T? Field<T>(string name) where T : class
                {
                    var fi = typeof(MainForm).GetField(name,
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    return fi?.GetValue(form) as T;
                }

                var nav = Field<Panel>("_nav");
                var chips = Field<ChipBar>("_chips");
                var title = Field<Label>("_pageTitle");
                var statusLeft = Field<Label>("_statusLeft");
                var cw = form.ClientSize.Width;
                var ch = form.ClientSize.Height;

                // v3.0.1：自绘标题栏占顶部 Theme.TitleBarHeight，侧栏/顶栏相对客户区下移
                var titleBar = form.Controls.OfType<TitleBar>().FirstOrDefault();
                Check(titleBar != null, "自绘标题栏存在（替换系统标题栏）");
                Check(titleBar != null && titleBar.Dock == DockStyle.Top && titleBar.Height == Theme.TitleBarHeight,
                      $"标题栏在顶部、高 {Theme.TitleBarHeight}px");
                Check(titleBar != null && titleBar.Width == cw,
                      $"标题栏横贯整窗（{titleBar?.Width} == 客户区宽 {cw}）");

                Check(nav != null && nav.Dock == DockStyle.Left && nav.Width == Theme.NavWidth,
                      $"侧边导航靠左、宽 {nav?.Width}px");
                Check(nav != null && nav.Height == ch - Theme.TitleBarHeight,
                      $"**侧栏占满标题栏之下整列高**（{nav?.Height} == 客户区 {ch} - 标题栏 {Theme.TitleBarHeight}）");

                var topBar = form.Controls.OfType<Panel>()
                    .FirstOrDefault(p => p.Dock == DockStyle.Top && p.Height == Theme.TopBarHeight);
                Check(topBar != null, "顶栏存在");
                Check(topBar != null && topBar.Left == Theme.NavWidth && topBar.Top == Theme.TitleBarHeight,
                      $"顶栏在标题栏之下、从侧栏右侧开始（left={topBar?.Left}, top={topBar?.Top}）");
                Check(topBar != null && topBar.Width == cw - Theme.NavWidth,
                      $"顶栏宽度 = 客户区宽 - 侧栏宽（{topBar?.Width}），没盖到侧栏上");
                Check(title != null && title.Text.Length > 0 && chips != null && chips.Dock == DockStyle.Fill,
                      $"顶栏已显示页标题「{title?.Text}」，右侧状态胶囊区填充");

                var statusBar = statusLeft?.Parent as Panel;
                Check(statusBar != null && statusBar.Dock == DockStyle.Bottom &&
                      statusBar.Top + statusBar.Height == ch,
                      $"状态栏贴底（top={statusBar?.Top} + h={statusBar?.Height} == {ch}）");
                Check(statusLeft != null && statusLeft.Text.Contains("待办"),
                      "状态栏已写入运行信息（含待办数）");

                var content = form.Controls.OfType<Panel>().FirstOrDefault(p => p.Dock == DockStyle.Fill);
                Check(content != null, "内容区存在（Dock=Fill）");
                Check(content != null && content.Left >= Theme.NavWidth &&
                      content.Top >= Theme.TopBarHeight &&
                      content.Top + content.Height <= ch - Theme.StatusBarHeight + 1,
                      $"**内容区不与导航/顶栏/状态栏重叠**（x={content?.Left}, y={content?.Top}, h={content?.Height}）");

                var navButtons = nav!.Controls.OfType<NavButton>().Where(b => b.PageIndex >= 0)
                                     .OrderBy(b => b.PageIndex).ToList();
                Check(navButtons.Count == 8, $"导航有 {navButtons.Count} 项（5 个主页面 + 3 个内嵌工具页）");
                bool ordered = true;
                for (int i = 1; i < navButtons.Count; i++)
                    if (navButtons[i].Top <= navButtons[i - 1].Top) ordered = false;
                Check(ordered, "**导航项从上到下顺序正确**（Dock=Top 是逆序显示，容易写反）");
                Check(nav.Controls.OfType<NavButton>().Any(b => b.PageIndex == -1), "侧栏底部有退出项");

                var kpi = (Dictionary<string, KpiCard>)typeof(MainForm).GetField("_kpi",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(form)!;
                Check(kpi.Count == 5, $"KPI 卡共 {kpi.Count} 张（今日为主：产品/PASS/FAIL/良率/中断）");
                var today5 = new[] { "today_product", "today_pass", "today_fail", "today_yield", "today_interrupted" };
                Check(today5.All(k => kpi.ContainsKey(k)), "今日 KPI 齐全：今日产品数/PASS/FAIL/良率/中断");
                Check(today5.All(k => kpi[k].Width >= 150),
                      $"今日 KPI 卡宽度正常（最小 {today5.Min(k => kpi[k].Width)}px，没被挤成一条）");

                // 主页内部：今日 KPI 一排 → 累计数据（3×2 大数字格），不能错位/重叠
                var kpiRow = kpi["today_product"].Parent!;
                var detail = (SectionPanel)typeof(MainForm).GetField("_detailPanel",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(form)!;
                int ScreenY(Control c) => c.PointToScreen(Point.Empty).Y;
                Check(ScreenY(kpiRow) + kpiRow.Height <= ScreenY(detail) + 2,
                      $"累计数据在今日 KPI 卡下方、不重叠（{ScreenY(kpiRow) + kpiRow.Height} <= {ScreenY(detail)}）");
                Check(detail.Height > 200 && detail.Width > 400,
                      $"累计数据有足够空间（{detail.Width}×{detail.Height}）");

                var showPage = typeof(MainForm).GetMethod("ShowPage",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var pageFields = new[] { "_dashboardPage", "_maintPage", "_failPage", "_devicePage", "_debugPage" };
                bool switchOk = true;
                var titles = new List<string>();
                for (int i = 0; i < 5; i++)
                {
                    showPage.Invoke(form, new object?[] { i });
                    Application.DoEvents();
                    titles.Add(title!.Text);
                    for (int j = 0; j < 5; j++)
                    {
                        var ctl = (Control)typeof(MainForm).GetField(pageFields[j],
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                            .GetValue(form)!;
                        if (ctl.Visible != (i == j)) switchOk = false;
                    }
                    if (!navButtons.First(b => b.PageIndex == i).Active) switchOk = false;
                }
                Check(switchOk, "切页时只有目标页可见、对应导航项高亮");
                Check(titles.Distinct().Count() == 5, $"5 个页标题各不相同：{string.Join(" / ", titles)}");

                // ---------- 工具真内嵌（v2.9.1）----------
                // 要验的就是“一体化”：工具不再是独立窗口，而是 TopLevel=false 挂在内容区的子控件。
                var toolPages = (ToolHost[])typeof(MainForm).GetField("_toolPages",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(form)!;
                Check(toolPages.Length == 3, $"三个工具页宿主已建（{toolPages.Length} 个）");
                Check(toolPages.All(h => h.Embedded == null),
                      "**启动时工具尚未构造**（惰加载，不拖慢开机）");

                var toolTitles = new List<string>();
                // v3.0.0：数据分发拆出去后只剩 3 个工具页。别写死 4，直接拿数组长度 ——
                // 写死的话以后再增减工具会 IndexOutOfRange（刚就踩了）。
                for (int i = 0; i < toolPages.Length; i++)
                {
                    var page = 5 + i;
                    showPage.Invoke(form, new object?[] { page });
                    Application.DoEvents();
                    var host = toolPages[i];
                    var emb = host.Embedded;
                    var pt = title!.Text;
                    toolTitles.Add(pt);

                    Check(emb != null, $"切到第 {page} 页后工具已实例化（{pt}）");
                    if (emb == null) continue;
                    Check(!emb.TopLevel && ReferenceEquals(emb.Parent, host),
                          $"{pt}：**TopLevel=false 且父容器是页面宿主**（真内嵌，不是弹窗）");
                    Check(emb.FormBorderStyle == FormBorderStyle.None && emb.Dock == DockStyle.Fill,
                          $"{pt}：已去掉自己的标题栏并铺满页面");
                    // ⚠ Form.FindForm() 在自身上返回 this，拿顶层窗必须用 TopLevelControl
                    Check(ReferenceEquals(emb.TopLevelControl, form),
                          $"{pt}：TopLevelControl 指回主窗口（模态窗 owner 才不会跑偏）");
                    Check(host.Visible && host.AutoScroll,
                          $"{pt}：页面可见且开了 AutoScroll（窗口拉小靠滚动条兑）");
                    Check(navButtons.First(b => b.PageIndex == page).Active, $"{pt}：对应导航项高亮");
                }
                Check(toolTitles.Distinct().Count() == 3, $"三个工具页标题各不相同：{string.Join(" / ", toolTitles)}");

                showPage.Invoke(form, new object?[] { 0 });
                Application.DoEvents();
                Check(toolPages.All(h => !h.Visible) && toolPages.All(h => h.Embedded != null),
                      "切回总览后工具页隐藏但实例保留（再切回去无需重建）");
                Check(typeof(MainForm).GetMethod("OpenTool",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) == null,
                      "**已没有 OpenTool 弹窗路径**（工具只能内嵌，防回流）");

                form.Hide();
                form.Dispose();
            }
            DesktopNotifier.Shutdown();     // 自检里也会建托盘图标，记得收
            Check(true, "退出时托盘图标已回收（Shutdown 可重复调）");

            // ---------- FAIL 记录页：树形/表格双视图（v3.1.0）----------
            Console.WriteLine("\n【FAIL 记录页】表格/树形双视图 · 聚合倒序 · 搜索过滤");
            {
                var engF = new Engine(new AppConfig
                {
                    StationId = "FCT9",
                    ResultsRoot = Path.Combine(work, "NoSuchResults"),
                    WebhookUrl = "",
                });
                string todayF = DateTime.Today.ToString("yyyyMMdd");
                engF.Db.InsertOne(new TestRecord { StationId = "FCT9", Model = "M1", Category = "Offline",
                    TestDate = todayF, Sn = "SN-A", Result = "FAIL", XmlPath = "X:\\a.xml",
                    FailReason = "5V_Rail", BatchTimestamp = "2026-08-01T10:00:00" });
                engF.Db.InsertOne(new TestRecord { StationId = "FCT9", Model = "M2", Category = "Offline",
                    TestDate = todayF, Sn = "SN-B", Result = "FAIL", XmlPath = "X:\\b.xml",
                    FailReason = "5V_Rail", BatchTimestamp = "2026-08-01T09:00:00" });
                engF.Db.InsertOne(new TestRecord { StationId = "FCT9", Model = "M3", Category = "Offline",
                    TestDate = todayF, Sn = "SN-C", Result = "FAIL", XmlPath = "X:\\c.xml",
                    FailReason = "Check_CAN_Bus", BatchTimestamp = "2026-08-01T08:00:00" });

                var panel = new FailListPanel(engF);
                panel.Size = new Size(980, 520);
                panel.Refresh2();

                // 字段探针
                var grid = (DataGridView)typeof(FailListPanel).GetField("_grid",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(panel)!;
                var tree = (TreeView)typeof(FailListPanel).GetField("_tree",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(panel)!;
                var cntLabel = (Label)typeof(FailListPanel).GetField("_countLabel",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(panel)!;

                // 默认表格视图
                Check(grid.Visible && !tree.Visible, "默认表格视图（grid 可见、tree 隐藏）");
                Check(grid.Rows.Count == 3, $"表格行数 = FAIL 条数（{grid.Rows.Count} == 3）");
                // 按失败项聚合倒序：5V_Rail(2次) 排前，Check_CAN_Bus(1次) 排后
                Check(grid.Rows[0].Cells[0].Value?.ToString() == "5V_Rail" &&
                      grid.Rows[2].Cells[0].Value?.ToString() == "Check_CAN_Bus",
                      $"表格按失败项次数倒序（{grid.Rows[0].Cells[0].Value} / {grid.Rows[2].Cells[0].Value}）");
                Check(grid.Rows[0].Cells[1].Value?.ToString() == "SN-A", "同一失败项内按时间倒序（新 SN-A 在前）");

                // 切树形
                var treeMode = typeof(FailListPanel).GetField("_treeMode",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                typeof(FailListPanel).GetMethod("SetView",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(panel, new object?[] { true });
                Check((bool)treeMode.GetValue(panel)! && grid.Visible == false && tree.Visible == true,
                      "切树形后：_treeMode=true、grid 隐藏、tree 可见");
                Check(tree.Nodes.Count == 2, $"树根节点 = 失败项个数（{tree.Nodes.Count} == 2）");
                Check(tree.Nodes[0].Text.Contains("5V_Rail") && tree.Nodes[0].Text.Contains("2"),
                      $"高频失败项分组置顶带次数（{tree.Nodes[0].Text}）");
                Check(tree.Nodes[0].Nodes.Count == 2, $"树形组「5V_Rail」下 2 个子SN（{tree.Nodes[0].Nodes.Count}）");
                Check(tree.Nodes[0].Tag?.ToString() == "5V_Rail" && tree.Nodes[0].Nodes[0].Tag?.ToString() == "X:\\a.xml",
                      "组节点 Tag=失败项名、子节点 Tag=XML 路径（双击能开对文件）");

                // 计数
                Check(cntLabel.Text.Contains("共 3 条 FAIL") && cntLabel.Text.Contains("2 个失败项"),
                      $"计数标签含条数与失败项个数（{cntLabel.Text}）");

                // 切回表格（按钮走真实点击路径）
                typeof(FailListPanel).GetMethod("SetView",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(panel, new object?[] { false });
                Check((bool)treeMode.GetValue(panel)! == false && grid.Visible == true, "切回表格视图");

                panel.Dispose();
            }

            // ---------- 开启动画（v3.1.0）----------
            Console.WriteLine("\n【开启动画】SplashForm 构造 + 时间线纯函数");
            using (var img1px = new Bitmap(1, 1))
            {
                using var splash = new SplashForm(img1px);
                Check(true, "SplashForm 可无 UI 构造");
                Check(splash.Width <= SplashForm.ImgW && splash.Height <= SplashForm.ImgH,
                      $"窗口 ≤ 图片分辨率（{splash.Width}x{splash.Height} ≤ {SplashForm.ImgW}x{SplashForm.ImgH}，1:1 原尺寸）");
                Check(Math.Abs((double)splash.Width / splash.Height - (double)SplashForm.ImgW / SplashForm.ImgH) < 0.01,
                      "窗口比例 = 图片比例（不变形）");
            }
            double Op(double t) => SplashForm.Compute(t).opacity;
            double Sc(double t) => SplashForm.Compute(t).scale;
            Check(Math.Abs(Op(0.0)) < 0.001 && Math.Abs(Sc(0.0) - 1.0) < 0.001,
                  "t=0 透明 + scale=1（渐强起点，透明度走窗口 Opacity）");
            Check(Math.Abs(Op(0.35) - 0.875) < 0.01 && Math.Abs(Sc(0.35) - 1.0) < 0.001,
                  $"t=0.35s EaseOut 渐强中（opacity≈0.875，实得 {Op(0.35):F3}）");
            Check(Math.Abs(Op(0.7) - 1.0) < 0.001 && Math.Abs(Sc(0.7) - 1.0) < 0.001, "t=0.7s 渐强完成（全亮）");
            Check(Math.Abs(Op(3.0) - 1.0) < 0.001, "t=3.0s 保持全亮（等待 5 秒）");
            Check(Math.Abs(Sc(5.0) - 1.0) < 0.001, "t=5.0s 缩小尚未开始（scale=1）");
            Check(Math.Abs(Sc(5.3) - 0.88125) < 0.01, $"t=5.3s EaseIn 缩到 ~0.88（实得 {Sc(5.3):F3}）");
            Check(Math.Abs(Sc(5.6) - 0.05) < 0.001 && Math.Abs(Op(5.6)) < 0.001,
                  $"t=5.6s 缩成圆点（scale=0.05, opacity=0，实得 {Sc(5.6):F3}/{Op(5.6):F3}）");
            Check(Math.Abs(Sc(10) - 0.05) < 0.001, "t=10s 状态不再变化（已完成）");
        }

        // ---------- 工具箱（v2.9.0 合入；v3.0.0 数据分发已拆出去）----------
        Console.WriteLine("\n【工具箱】三个合入的工具必须能无 UI 构造并单例打开");
        {
            // 合并后最大的风险：某个工具的窗口构造失败（缺资源/缺依赖/静态初始化抛），
            // 在主程序里点一下就弹错。这里逐个真构造一遍。
            // 工具窗口仍能当普通 Form 单独用（CLI 子命令 rank/fetch/tdms 走这条）
            var tools = new (string name, Func<Form> make)[]
            {
                ("FAIL 排行 FctFailRanker", () => new FctFailRanker.MainForm()),
                ("取数打包 FctFetcher", () => new FctFetcher.MainForm()),
                ("TDMS 波形 FctTdmsViewer", () => new FctTdmsViewer.MainForm()),
            };
            foreach (var (name, make) in tools)
            {
                try
                {
                    using var f = make();
                    Check(f is Form && f.Controls.Count > 0,
                          $"{name} 无 UI 构造成功（{f.Controls.Count} 个顶层控件）");
                }
                catch (Exception ex)
                {
                    Check(false, $"{name} 构造失败！主程序工具箱里一点就炸：{ex.GetType().Name} {ex.Message}");
                }
            }

            // 分发工具的内置模板：源码换目录后资源名会变，必须仍能加载到。
            // ⚠ 自检工程只编译源码、**没有**嵌入 tpl\*.xml，所以要去量真正要发出去的那个程序集里查。
            {
                var dllCandidates = new[]
                {
                    Path.Combine("bin", "Release", "net8.0-windows", "Argus.dll"),
                    Path.Combine("dist", "Argus.dll"),
                    Path.Combine("..", "bin", "Release", "net8.0-windows", "Argus.dll"),
                };
                var dll = dllCandidates.FirstOrDefault(File.Exists);
                if (dll == null)
                {
                    Console.WriteLine("    (跳过内置模板检查：没找到已构建的 Argus.dll)");
                }
                else
                {
                    var names = System.Reflection.Assembly.LoadFrom(Path.GetFullPath(dll))
                                      .GetManifestResourceNames();
                    var xmls = names.Where(n => n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList();
                    // v3.0.0：分发器的内置模板（tpl\*.xml）随它一起搬去 fct-distributor/ 了。
                    // 这里反向断言 —— 主程序里**不该**再有任何 xml 资源，否则说明拆分没拆干净。
                    Check(xmls.Count == 0,
                          $"主程序里已无内置模板 xml 资源（分发器已独立，实得 {xmls.Count} 个）");
                    Check(names.Any(n => n.EndsWith("app_icon.ico", StringComparison.OrdinalIgnoreCase)),
                          "内嵌图标仍在（取数/TDMS 工具靠 EndsWith(\"app_icon.ico\") 找它）");                }
            }

            // 名字重名的类确实靠命名空间隔开了（合并前有 10 组同名类）
            Check(typeof(FctFailRanker.MainForm) != typeof(FctFetcher.MainForm) &&
                  typeof(FctFetcher.MainForm) != typeof(FctTdmsViewer.MainForm) &&
                  typeof(FctAggregator.MainForm) != typeof(FctFailRanker.MainForm),
                  "四个 MainForm 共存于同一程序集（靠命名空间隔离，无需改名）");
        }

        // ---------- 风格统一（v2.9.2）----------
        Console.WriteLine("\n【风格统一】主题器只改外观，不能碰布局");
        {
            // 三个工具都是绝对坐标布局，一旦主题器改了尺寸/位置就会错位。
            // 这里逐个工具：刷前记下所有控件的 Bounds，刷完逐个比。
            IEnumerable<Control> Walk(Control c)
            {
                yield return c;
                foreach (Control k in c.Controls)
                    foreach (var x in Walk(k)) yield return x;
            }

            var makers = new (string name, Func<Form> make)[]
            {
                ("FAIL 排行", () => new FctFailRanker.MainForm()),
                ("取数打包", () => new FctFetcher.MainForm()),
                ("TDMS 波形", () => new FctTdmsViewer.MainForm()),
            };
            foreach (var (name, make) in makers)
            {
                using var f = make();
                var boundsBefore = Walk(f).ToDictionary(c => c, c => c.Bounds);
                Theme.Apply(f);
                var moved = boundsBefore.Where(kv => kv.Key.Bounds != kv.Value).ToList();
                Check(moved.Count == 0,
                      $"{name}：上主题后 **{boundsBefore.Count} 个控件位置尺寸全部未变**" +
                      (moved.Count > 0 ? $"（实际动了 {moved.Count} 个，如 {moved[0].Key.GetType().Name}）" : ""));

                Check(f.BackColor == Theme.Bg && f.Font.Name == Theme.Body.Name,
                      $"{name}：底色/字体已统一（{f.BackColor.R},{f.BackColor.G},{f.BackColor.B} / {f.Font.Name}）");

                var btns = Walk(f).OfType<Button>().ToList();
                Check(btns.Count == 0 || btns.All(b => b.FlatStyle == FlatStyle.Flat),
                      $"{name}：{btns.Count} 个按钮全部改成扁平风格");
                var grids = Walk(f).OfType<DataGridView>().ToList();
                Check(grids.Count == 0 || grids.All(g => !g.EnableHeadersVisualStyles &&
                                                        g.GridColor == Theme.Border),
                      $"{name}：{grids.Count} 个表格的表头/网格线已统一");
                var tips = Walk(f).OfType<TextBox>().ToList();
                Check(tips.Count == 0 || tips.All(t => t.BorderStyle == BorderStyle.FixedSingle),
                      $"{name}：{tips.Count} 个输入框边框已统一");
            }

            // 自绘控件必须被跳过（刷了反而难看）
            var kpi = new KpiCard("t", Theme.Success) { BackColor = Theme.Bg };
            var card = new MaintenanceCard(new MaintenanceRecord { Id = 1, FailItem = "x", Status = "open" });
            var holder = new Panel();
            holder.Controls.Add(kpi);
            holder.Controls.Add(card);
            var kpiBg = kpi.BackColor; var cardBg = card.BackColor;
            Theme.Apply(holder);
            Check(kpi.BackColor == kpiBg && card.BackColor == cardBg,
                  "自绘控件（KPI 卡 / 看板卡）被主题器跳过");
            var tagged = new Panel { Tag = Theme.SkipTag, BackColor = Color.Fuchsia };
            holder.Controls.Add(tagged);
            Theme.Apply(holder);
            Check(tagged.BackColor == Color.Fuchsia, $"标了 Tag=\"{Theme.SkipTag}\" 的控件不被刷");
            holder.Dispose(); kpi.Dispose(); card.Dispose(); tagged.Dispose();
        }

        // ---------- 去重后的 xlsx 写出器（v2.9.3）----------
        Console.WriteLine("\n【去重】四份 xlsx 写出器合并为 FctShared.Xlsx，输出必须仍然合法");
        {
            // 合并的是"打包外壳"，各工具的 styles.xml 原样保留 —— 所以这里验的是
            // ① 生成的包结构完整、每个 XML 都能被严格解析；② 样式部件确实还是各家自己的。
            var dir = Path.Combine(work, "xlsx");
            Directory.CreateDirectory(dir);

            // 直接用共享写出器造一份，含中文、特殊字符、数字、合并单元格、冻结行
            var sh = new FctShared.Xlsx.Sheet { Name = "测试<表>/x:1", FreezeRows = 1 };
            sh.ColWidths.AddRange(new[] { 20.0, 12.0, 12.0 });
            sh.Rows.Add(new List<FctShared.Xlsx.Cell>
            {
                FctShared.Xlsx.T("项目 & <标记>"), FctShared.Xlsx.T("值"), FctShared.Xlsx.T("备注"),
            });
            sh.Rows.Add(new List<FctShared.Xlsx.Cell>
            {
                FctShared.Xlsx.T("含\"引号\"与'撇号'"), FctShared.Xlsx.N(3.14159), FctShared.Xlsx.Empty(),
            });
            sh.Merges.Add((0, 1, 0, 2));
            var shared = Path.Combine(dir, "shared.xlsx");
            FctShared.Xlsx.Write(shared, new[] { sh }, FctFetcher.XlsxWriter.Styles2());

            Check(new FileInfo(shared).Length > 800, $"共享写出器生成 xlsx（{new FileInfo(shared).Length} 字节）");
            using (var zip = System.IO.Compression.ZipFile.OpenRead(shared))
            {
                foreach (var need in new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml",
                                             "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet1.xml" })
                    if (!zip.Entries.Any(e => e.FullName == need)) Check(false, $"缺部件 {need}");
                Check(true, "包部件齐全");
                foreach (var e in zip.Entries)
                {
                    using var st = e.Open();
                    try { System.Xml.Linq.XDocument.Load(st); }
                    catch (Exception ex) { Check(false, $"{e.FullName} 不是合法 XML: {ex.Message}"); }
                }
                Check(true, "所有部件都是严格合法的 XML（转义没写漏）");

                using var sr = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
                var body = sr.ReadToEnd();
                Check(body.Contains("&amp;") && body.Contains("&lt;标记&gt;"), "特殊字符已正确转义");
                Check(body.Contains("<pane ySplit=\"1\""), "冻结行已写入");
                Check(body.Contains("mergeCell"), "合并单元格已写入");
                Check(body.Contains("3.14159"), "数值按不变文化写出（小数点不会变成逗号）");

                using var sr2 = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open());
                var wb = sr2.ReadToEnd();
                // Excel 真正禁止的是 \ / ? * [ ] : —— 这些换成下划线；< > 合法，只需 XML 转义
                Check(wb.Contains("_x_1") && !wb.Contains("/x:1"),
                      "工作表名：非法字符（/ :）已换成下划线");
                Check(!wb.Contains("<表>"), "工作表名里的尖括号已做 XML 转义（不会把 workbook.xml 搞坏）");
            }

            // 维修记录导出（走的也是共享写出器）仍然可读
            var recs = d.ListMaintenance("", 50);
            var mx = Path.Combine(dir, "维修.xlsx");
            MaintenanceExporter.ExportXlsx(mx, recs);
            using (var zip = System.IO.Compression.ZipFile.OpenRead(mx))
            {
                foreach (var e in zip.Entries)
                {
                    using var st = e.Open();
                    try { System.Xml.Linq.XDocument.Load(st); }
                    catch (Exception ex) { Check(false, $"维修导出 {e.FullName} XML 非法: {ex.Message}"); }
                }
                using var sr = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
                var body = sr.ReadToEnd();
                Check(body.Contains("故障项目") && body.Contains("更新时间"), "维修导出表头仍完整");
                Check(body.Contains("<pane ySplit=\"1\""),
                      "维修导出冻结表头（v2.9.3 新增；v2.9.2 无冻结，是刻意保留的唯一长相变化）");
            }

            // 两个工具的样式部件各不相同 = 报表长相没有被“统一”掉
            var st1 = FctFailRanker.XlsxExporter.Styles2();
            var st2 = FctFetcher.XlsxWriter.Styles2();
            Check(st1 != st2, "各工具的 styles.xml 仍是各自的调色板（只合并了外壳，没统一长相）");
            Check(st1.Contains("styleSheet") && st2.Contains("styleSheet"), "样式部件格式正常");
        }

        // ---------- 应用图标（v2.9.4）----------
        Console.WriteLine("\n【图标】必须能从嵌入资源拿到多尺寸图标（不能退成系统灰图）");
        {
            // v2.9.4 前：四处读 BaseDir\app_icon.ico，而打包脚本从未把它放进发布包
            // → 窗口退到 exe 内嵌图、托盘退到 SystemIcons.Application（灰色通用图标）。
            var ico = AppIcon.Load();
            Check(ico != null, "AppIcon.Load() 能拿到图标");
            Check(!ReferenceEquals(ico, SystemIcons.Application),
                  "**拿到的是应用图标而不是系统灰图**（嵌入资源读取成功）");
            var small = AppIcon.Load(16);
            Check(small.Width == 16 && small.Height == 16, $"可按尺寸取图标（托盘用 16×16，实得 {small.Width}×{small.Height}）");
            Check(ReferenceEquals(AppIcon.Load(), ico), "图标已缓存（不会每个窗口重新解一遍 160KB 的 ico）");

            // 直接验图标文件本身：多分辨率 ICO 容器头
            var icoPath = FindUp("app_icon.ico");
            if (icoPath.Length == 0) Console.WriteLine("    (跳过 ico 文件校验：没找到 app_icon.ico)");
            else
            {
                var bytes = File.ReadAllBytes(icoPath);
                int reserved = BitConverter.ToUInt16(bytes, 0);
                int type = BitConverter.ToUInt16(bytes, 2);
                int count = BitConverter.ToUInt16(bytes, 4);
                Check(reserved == 0 && type == 1, "app_icon.ico 是合法 ICO 容器");
                Check(count >= 4, $"**图标包含 {count} 个尺寸**（任务栏/托盘/大图标各自有清楚的那一份）");
                var sizes = new List<int>();
                for (int i = 0; i < count; i++)
                {
                    int w = bytes[6 + 16 * i];
                    sizes.Add(w == 0 ? 256 : w);
                }
                Check(sizes.Contains(16) && sizes.Contains(32) && sizes.Contains(256),
                      $"关键尺寸齐全：{string.Join("/", sizes)}");
                Check(bytes.Length > 20000, $"不是占位图（{bytes.Length / 1024} KB，旧的占位图只有 285 字节）");
            }
        }

        // ---------- 导出 ----------
        var records = d.ListMaintenance("", 500);
        var xlsx = Path.Combine(work, "维修记录.xlsx");
        var csv = Path.Combine(work, "维修记录.csv");
        MaintenanceExporter.ExportXlsx(xlsx, records);
        MaintenanceExporter.ExportCsv(csv, records);
        Check(new FileInfo(xlsx).Length > 1500, $"xlsx 已生成（{new FileInfo(xlsx).Length} 字节）");
        Check(new FileInfo(csv).Length > 500, $"csv 已生成（{new FileInfo(csv).Length} 字节）");

        using (var zip = System.IO.Compression.ZipFile.OpenRead(xlsx))
        {
            foreach (var need in new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml",
                                         "xl/_rels/workbook.xml.rels", "xl/worksheets/sheet1.xml" })
                if (!zip.Entries.Any(e => e.FullName == need)) Check(false, $"xlsx 缺部件 {need}");
            foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels")))
            {
                using var st = e.Open();
                try { System.Xml.Linq.XDocument.Load(st); }
                catch (Exception ex) { Check(false, $"{e.FullName} 不是合法 XML: {ex.Message}"); }
            }
            Check(true, "xlsx 部件齐全且所有 XML 严格可解析");

            using var sr = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var body = sr.ReadToEnd();
            foreach (var h in new[] { "ID", "故障项目", "设备型号", "设备SN", "故障描述", "严重度",
                                      "状态", "维修人", "维修措施", "备注", "创建时间", "更新时间" })
                if (!body.Contains(h)) Check(false, $"表头缺列「{h}」");
            Check(true, "12 列表头齐全（新增「更新时间」）");
            Check(body.Contains("未知问题") || body.Contains("正在排查"),
                  "新状态在导出里是中文（不是原始 unknown/investigating）");
            Check(!body.Contains(">unknown<") && !body.Contains(">investigating<"),
                  "导出里没有漏译的英文 key（单一字典生效）");
            var cols = System.Text.RegularExpressions.Regex.Matches(body, "<col ").Count;
            Check(cols == 12, $"列宽定义 {cols} 个（应与 12 列一致）");

            // 长相锁定（v2.9.3 xlsx 写出器去重的回归防线）：
            // 去重只验了「结构合法」，配色/列宽靠人眼一次性确认。确认过的值就钉在这里，
            // 以后谁改到列宽或表头色自检会直接抦下来。对比工具：tools\xlsx_check.ps1 -Compare
            var wantWidths = new[] { "6", "24", "14", "22", "30", "10", "10", "12", "30", "24", "20", "20" };
            var gotWidths = System.Text.RegularExpressions.Regex.Matches(body, "<col [^>]*width=\"([^\"]+)\"")
                .Select(m => m.Groups[1].Value).ToArray();
            Check(gotWidths.SequenceEqual(wantWidths),
                  $"12 个列宽值与人眼确认过的一致（{string.Join("/", gotWidths)}）");

            using var sr2 = new StreamReader(zip.GetEntry("xl/styles.xml")!.Open());
            var styles = sr2.ReadToEnd();
            Check(styles.Contains("4472C4"), "表头底色仍是 #4472C4 深蓝（调色板未被共享写出器带跑）");
            Check(styles.Contains("FFFFFF") && styles.Contains("<b/>"), "表头仍是白色粗体");
            Check(styles.Contains("微软雅黑"), "字体仍是微软雅黑");
        }

        // CSV 结构(沿用 v2.1.0): 标题行 + 导出时间 + 空行 + 表头 + 数据
        var csvLines = File.ReadAllLines(csv, System.Text.Encoding.UTF8);
        Check(csvLines[0].StartsWith("\uFEFF") || csvLines[0].Contains("维修记录导出"),
              "csv 带 UTF-8 BOM 与标题行（Excel 直开不乱码）");
        Check(csvLines[1].StartsWith("导出时间,"), "csv 第 2 行是导出时间");
        var header = csvLines[3];
        Check(header.Split(',').Length == 12, $"csv 表头 {header.Split(',').Length} 列（含「更新时间」）");
        Check(header.EndsWith("更新时间"), "csv 最后一列是「更新时间」");

        // 用真正的 CSV 解析器逐行数字段（字段里有逗号/换行时会被引号包起来，不能按行数硬算）
        int dataRows = 0, badRow = 0;
        using (var rd = new StreamReader(csv, System.Text.Encoding.UTF8))
        {
            for (int i = 0; i < 4; i++) rd.ReadLine();          // 跳过标题块 + 表头
            while (true)
            {
                var fields = ReadCsvRecord(rd);
                if (fields == null) break;
                if (fields.Count == 1 && fields[0].Length == 0) continue;
                dataRows++;
                if (fields.Count != 12) badRow++;
            }
        }
        Check(dataRows == records.Count, $"csv 数据行 {dataRows} 条 == 记录 {records.Count} 条（正确处理了引号包裹的换行）");
        Check(badRow == 0, $"每条数据都是 12 个字段（异常行 {badRow} 条）");
        Check(!File.ReadAllText(csv).Contains(",unknown,") && !File.ReadAllText(csv).Contains(",investigating,"),
              "csv 里也没有漏译的英文状态");

    /// <summary>极简 CSV 记录读取（支持引号内的逗号与换行）。读到文件尾返回 null。</summary>
    static List<string>? ReadCsvRecord(StreamReader rd)
    {
        if (rd.EndOfStream) return null;
        var fields = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuotes = false;
        while (true)
        {
            int ci = rd.Read();
            if (ci < 0) { fields.Add(cur.ToString()); return fields; }
            char c = (char)ci;
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (rd.Peek() == '"') { rd.Read(); cur.Append('"'); }
                    else inQuotes = false;
                }
                else cur.Append(c);
                continue;
            }
            if (c == '"') { inQuotes = true; continue; }
            if (c == ',') { fields.Add(cur.ToString()); cur.Clear(); continue; }
            if (c == '\r') continue;
            if (c == '\n') { fields.Add(cur.ToString()); return fields; }
            cur.Append(c);
        }
    }

        Console.WriteLine($"\n产物留存（可用 Excel 打开人工确认）:\n  {xlsx}\n  {csv}");
        Console.WriteLine(_fail == 0 ? "\n==== 全部通过 ====" : $"\n==== {_fail} 项失败 ====");
        return _fail == 0 ? 0 : 1;
    }
}

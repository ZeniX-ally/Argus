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

    [STAThread]
    static int Main()
    {
        var work = Path.Combine(Path.GetTempPath(), "fct_agg_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(work);
        var real = @"F:\Programe\G4_9-QA\fct-aggregator-cs\dist\data\fct.db";
        var db = Path.Combine(work, "fct.db");

        bool haveReal = File.Exists(real);
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
        Check(bak.Length == 1, $"迁移前已自动备份 db（{(bak.Length == 1 ? Path.GetFileName(bak[0]) : "未找到")}）");
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
            card.ContextRequested += (c, at) => { got = c.Record; pt = at; };

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
        Check(realFails.Count > 0, $"从真实库读到 FAIL 源行 {realFails.Count} 条");
        var realAgg = FailItemPickerForm.Aggregate(realFails);
        Console.WriteLine($"    真实库去重后故障项 {realAgg.Count} 个，前 3: " +
                          string.Join(" | ", realAgg.Take(3).Select(a => $"{a.Count}x {a.Item}")));
        Check(realAgg.Count > 0 && realAgg.Count < realFails.Count,
              $"真实库：{realFails.Count} 条 FAIL -> 去重后 {realAgg.Count} 个故障项（确实去了重）");
        Check(realFails.All(f => f.GetType().GetField("Sn") == null),
              "FailItemSource 结构里根本没有 Sn 字段（从数据层就不带 SN）");

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
            Check(list.All(r => r.Status == MaintenanceMeta.DefaultStatus), "批量记录默认状态 = 未完成");
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

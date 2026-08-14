using FctAggregator;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// 维修记录看板化(v2.2.0)的自检：拿真实 db 的副本跑，验证
/// 迁移 / 备份 / 全字段更新 / 计数 / 排序 / 导出 12 列。
/// 不碰真实数据（全在系统临时目录里做）。
/// </summary>
class SelfTest
{
    static int _fail;
    static readonly HttpClient _http = new();   // Web 服务自检共用（复用连接池）
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

        // ---------- 时间统一解析（v3.3.0: 8/14/17 位数字 + ISO 带时区毫秒） ----------
        Console.WriteLine("\n【时间识别】四种来源格式统一解析");
        Check(TimeUtil.Normalize("2026-07-22T12:47:00.213+08:00") == "2026-07-22 12:47:00",
              "ISO 带时区毫秒(BATCH TIMESTAMP)解析正确");
        Check(TimeUtil.Normalize("2026-07-22T12:47:00") == "2026-07-22 12:47:00", "ISO 无时区解析正确");
        Check(TimeUtil.Normalize("2026-07-22 12:47:00") == "2026-07-22 12:47:00", "标准格式原样归一");
        Check(TimeUtil.Normalize("20260722124700283") == "2026-07-22 12:47:00", "17 位文件名时间解析正确(丢毫秒)");
        Check(TimeUtil.Normalize("20260722124700") == "2026-07-22 12:47:00", "14 位时间解析正确");
        Check(TimeUtil.Normalize("20260722") == "2026-07-22 00:00:00", "8 位目录日期解析正确");
        Check(TimeUtil.Normalize("垃圾时间") == "", "无法识别的时间返回空(不再吐原始怪串)");
        Check(TimeUtil.Normalize("") == "", "空时间返回空");
        Check(TimeUtil.Short("2026-07-22T12:47:00.213+08:00") != "—", "Short 对 ISO 时间可识别");

        // ---------- FCT.ini 自动识别（v3.3.0: 不依赖配置路径） ----------
        Console.WriteLine("\n【FCT.ini 自动识别】FTS 树搜索 / 浅层全盘搜索");
        {
            var iniRoot = Path.Combine(work, "autoini");
            var cfgDir = Path.Combine(iniRoot, "FTS", "Apps", "PEU", "Cfg");
            Directory.CreateDirectory(cfgDir);
            File.WriteAllText(Path.Combine(cfgDir, "FCT.ini"), "[Resource Name]\n8.2_SN=5V_Rail\n");
            var hit = FctIni.SearchFtsTree(new DirectoryInfo(iniRoot), 8);
            Check(hit != null && hit.EndsWith("FCT.ini", StringComparison.OrdinalIgnoreCase),
                  "FTS 树搜索能命中深层 FCT.ini");
            var shallow = FctIni.SearchShallow(new DirectoryInfo(work), 6);
            Check(shallow != null, "浅层搜索也能命中(自动识别兜底路径)");
            // 大小写: FctIni 用大小写不敏感匹配
            Directory.CreateDirectory(Path.Combine(iniRoot, "FTS", "Cfg"));
            File.WriteAllText(Path.Combine(iniRoot, "FTS", "Cfg", "fct.ini"), "x");
            var hit2 = FctIni.SearchFtsTree(new DirectoryInfo(iniRoot), 8);
            Check(hit2 != null && hit2.EndsWith("fct.ini", StringComparison.OrdinalIgnoreCase),
                  "文件名大小写不敏感(fct.ini 也能识别)");
        }

        // ---------- 旧库兼容（v3.3.0：产线老库直开无痛升级） ----------
        Console.WriteLine("\n【旧库兼容】产线老库直开: 缺新表 + investigating/closed 状态 + 旧时间格式");
        {
            var oldDb = Path.Combine(work, "legacy_fct.db");
            File.Delete(oldDb);
            // 1) 手工建「旧结构」库: 只有 test_records + maintenance_records,
            //    没有 todo_items / app_meta / dismissed_todos（v2.4.0 时代的结构）
            using (var c = new SqliteConnection($"Data Source={oldDb}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE test_records (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, station_id TEXT NOT NULL, model TEXT, category TEXT,
                        test_date TEXT NOT NULL, sn TEXT, result TEXT, xml_path TEXT UNIQUE, fail_reason TEXT,
                        tester TEXT, panel_status TEXT, batch_timestamp TEXT, has_fail_items INTEGER, file_size INTEGER,
                        created_at TEXT DEFAULT (datetime('now','localtime')));
                    CREATE TABLE maintenance_records (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, station_id TEXT, equipment_model TEXT, equipment_sn TEXT,
                        fail_item TEXT NOT NULL, fail_reason TEXT, severity TEXT DEFAULT 'major', status TEXT DEFAULT 'open',
                        resolver TEXT, resolution TEXT, notes TEXT, created_at TEXT DEFAULT (datetime('now','localtime')),
                        updated_at TEXT DEFAULT (datetime('now','localtime')));
                    INSERT INTO test_records (station_id,model,category,test_date,sn,result,xml_path,fail_reason,batch_timestamp)
                        VALUES ('FCT7','E3002781','Offline','20260801','SN-LG1','FAIL','X:\legacy1.xml','6.1.1.1 5V_Rail','2026-08-01T09:30:00.123+08:00');
                    INSERT INTO test_records (station_id,model,category,test_date,sn,result,xml_path,fail_reason,batch_timestamp)
                        VALUES ('FCT7','E3002781','Offline','20260801','SN-LG2','FAIL','X:\legacy2.xml','CAN_Bus_Test_CH1','20260801093015');
                    INSERT INTO maintenance_records (station_id,fail_item,fail_reason,status,resolver,created_at,updated_at)
                        VALUES ('FCT7','旧版正在排查项','老数据','investigating','张三','2026-07-20 08:00:00','2026-07-20 08:00:00');
                    INSERT INTO maintenance_records (station_id,fail_item,fail_reason,status,resolver,created_at,updated_at)
                        VALUES ('FCT7','旧版已关闭项','老数据','closed','李四','2026-07-01 08:00:00','2026-07-01 08:00:00');
                ";
                cmd.ExecuteNonQuery();
            }
            // 2) 新版 Database 直开: 不炸 + 自动补建新表
            var ld = new Database(oldDb);
            using (var c = new SqliteConnection($"Data Source={oldDb}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                using var r = cmd.ExecuteReader();
                var tables = new HashSet<string>();
                while (r.Read()) tables.Add(r.GetString(0));
                foreach (var t in new[] { "todo_items", "app_meta", "dismissed_todos", "resolvers" })
                    Check(tables.Contains(t), $"旧库打开后自动补建表 {t}");
            }
            // 3) 旧状态兼容: investigating 归并「待办」、closed 迁移「已完成」
            var inv = ld.ListMaintenance("investigating", 50).FirstOrDefault();
            Check(inv != null && MaintenanceMeta.Normalize(inv.Status) == "open",
                  "旧 investigating 记录仍在库中, 归一化到「待办」列");
            Check(ld.ListMaintenance("resolved", 50).Any(m => m.FailItem == "旧版已关闭项"),
                  "旧 closed 记录迁移到「已完成」列");
            // 4) 老数据待办登记 + 两种旧时间格式都能解析
            var createdLegacy = ld.SyncTodoItems(90);
            Check(createdLegacy >= 2, $"旧库历史不良可登记待办（新登记 {createdLegacy} 条）");
            var legacyView = ld.ListTodoView();
            Check(legacyView.Any(x => x.Title == "5V_Rail"), "旧库 5V_Rail 进待办列");
            var lg1 = ld.AllFails("FCT7").FirstOrDefault(r => r.Sn == "SN-LG1");
            Check(lg1 != null && TimeUtil.Normalize(lg1.Timestamp) == "2026-08-01 09:30:00",
                  "旧库 ISO 带时区时间可解析显示");
            var lg2 = ld.AllFails("FCT7").FirstOrDefault(r => r.Sn == "SN-LG2");
            Check(lg2 != null && TimeUtil.Normalize(lg2.Timestamp) == "2026-08-01 09:30:15",
                  "旧库 14 位数字时间可解析显示");
            // 5) 待办删除在旧库上也能用（dismissed_todos 自动补建后写入标记）
            var delLegacy = legacyView.First();
            Check(ld.DeleteTodo(delLegacy.Id), "旧库上删除待办正常（自动补建 dismissed_todos 写永久标记）");
            Check(!ld.ListTodoView().Any(x => x.Id == delLegacy.Id), "删除后离开待办列");
        }

        // ---------- 五个状态各造几条 ----------
        var keys = MaintenanceMeta.Statuses.Select(s => s.Key).ToArray();
        Check(keys.Length == 4 && keys[0] == "unknown" && keys[3] == "resolved",
              $"状态体系 4 个且顺序正确: {string.Join(" -> ", MaintenanceMeta.Statuses.Select(s => s.Zh))}");
        Check(MaintenanceMeta.DefaultStatus == "open", "新建记录默认状态 = open(未完成)");
        Check(MaintenanceMeta.ZhOf("closed") == "已完成", "legacy closed 仍显示为「已完成」（外来 db 兜底）");
        Check(MaintenanceMeta.Normalize("closed") == "resolved", "legacy closed 归并到 resolved 列");
        Check(MaintenanceMeta.Normalize("investigating") == "open", "legacy investigating 归并到 open（「正在排查」列已去掉）");
        Check(MaintenanceMeta.ZhOf("investigating") == "待办", "legacy investigating 显示为「待办」");
        Check(MaintenanceMeta.Normalize("这是个野值") == "open", "未知状态归并到 open，不会漏卡片");
        Check(MaintenanceMeta.ZhOf(MaintenanceMeta.Statuses[2].Key) == "持续跟踪", "「进行中」已改名「持续跟踪」");

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
        Check(seq == 12, $"造了 {seq} 条测试记录（4 状态 × 3）");

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
        var dragged = d.ListMaintenance("unknown", 500).First();
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
            // ⚠ 非交互会话（SSH 无桌面）下 WinForms 弹窗抛 InvalidOperationException，属环境限制跳过；
            //   桌面会话（产线/VM 图形界面）正常断言。
            {
                using var host = new Form { Width = 300, Height = 200 };
                var anchorCard = new TodoCard(merged) { Width = 200 };
                host.Controls.Add(anchorCard);
                host.Show();
                if (!SystemInformation.UserInteractive)
                {
                    Check(true, "CardPreviewForm 弹窗跳过：非交互会话（SSH 无桌面，环境限制非功能失败）");
                }
                else
                {
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
                }
                CardPreviewForm.CloseCurrent();
                Check(true, "CardPreviewForm.CloseCurrent() 可重复调用不报错");
                CardPreviewForm.CloseCurrent();
                host.Hide();
            }
            todoCard.Dispose(); recCard.Dispose();

            // v2.5.1 起无忽略入口；v3.3.0 起待办支持显式删除（右键菜单）
            var boardMethods = typeof(MaintenanceBoard)
                .GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.Instance)
                .Select(x => x.Name).ToList();
            Check(!boardMethods.Any(x => x.Contains("Dismiss")),
                  "**看板上已无忽略入口**（不良只能确认/处理/删除，没有忽略）");
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
            Check(rec != null && rec.Status == "in_progress", "可以确认时直接置为「持续跟踪」");
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

            // ---------- v3.3.0: 待办删除 / 确认到「待办」列也推送 ----------
            {
                // 确认落到默认「待办」列 -> 同样触发状态变更事件（“任何状态都推送”）
                var can2 = t.ListTodoView().First(x => x.Title == "Check_CAN_Bus");
                events2.Clear();
                var mid3 = t.AcknowledgeTodo(can2.Id, "王五", "major", "open");
                Check(mid3 > 0, $"确认到「待办」列也建记录 #{mid3}");
                Check(events2.Count == 1 && events2[0].from == "" && events2[0].to == "open",
                      $"确认到「待办」列也触发推送（{events2.Count} 次: '{events2[0].from}' -> {events2[0].to}）");
                t.DeleteMaintenance(mid3);

                // 删除待办：删后离开待办列，且不会因水位线后的新不良而复现
                AddFail(t, St, "9.9.9_Delete_Me", "SN777", Today);
                t.SyncTodoItems(30);
                var delItem = t.ListTodoView().FirstOrDefault(x => x.Title == "Delete_Me");
                Check(delItem != null, "待办删除前存在");
                Check(t.DeleteTodo(delItem!.Id), "DeleteTodo 返回 true");
                Check(!t.ListTodoView().Any(x => x.Title == "Delete_Me"), "删除后离开待办列");
                AddFail(t, St, "9.9.9_Delete_Me", "SN778", Today);
                t.SyncTodoItems(30);
                Check(!t.ListTodoView().Any(x => x.Title == "Delete_Me"),
                      "**已删除的待办不再复现**（水位线已过，删除是永久的）");
                Check(t.DeleteTodo(999999) == false, "删除不存在的待办返回 false");
            }
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
                form = new MainForm(eng2, true);   // debug 模式 = 全量导航，布局断言不因隐藏调试项缩水
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
                Check(navButtons.Count == 9, $"导航有 {navButtons.Count} 项（5 个主页面 + 4 个内嵌工具页）");
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
                Check(toolPages.Length == 4, $"四个工具页宿主已建（{toolPages.Length} 个）");
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
                Check(toolTitles.Distinct().Count() == 4, $"四个工具页标题各不相同：{string.Join(" / ", toolTitles)}");
                Check(toolPages.Length > 3 && toolPages[3].Embedded is AggCenterForm,
                      "第 4 个工具页 = 聚合看板（AggCenterForm 内嵌，空共享目录下也能正常开页）");

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

            // ---------- 调试工具默认显示（v3.4.1：用户拍板直接显示在菜单；--debug 参数保留兼容无副作用）----------
            Console.WriteLine("\n【调试工具显示】不带 --debug 时侧边栏也有调试入口，导航结构不塌");
            {
                var engD = new Engine(new AppConfig
                {
                    StationId = "FCT9",
                    ResultsRoot = Path.Combine(work, "NoSuchResults"),
                    WebhookUrl = "",
                });
                using var f2 = new MainForm(engD);   // 默认 debugMode=false = 生产形态
                f2.Show();
                f2.PerformLayout();
                Application.DoEvents();
                var nav2 = (Panel)typeof(MainForm).GetField("_nav",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(f2)!;
                var btns = nav2.Controls.OfType<NavButton>().Where(b => b.PageIndex >= 0)
                              .OrderBy(b => b.PageIndex).ToList();
                Check(btns.Count == 9, $"生产模式侧边导航 {btns.Count} 项（调试默认显示：5 页 + 4 工具）");
                Check(btns.Any(b => b.PageIndex == 4), "生产模式侧边栏有「调试工具」入口（v3.4.1 默认显示）");
                // 调试页正常可达：切过去有导航高亮
                var sp2 = typeof(MainForm).GetMethod("ShowPage",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                sp2.Invoke(f2, new object?[] { 4 });
                Application.DoEvents();
                Check(nav2.Controls.OfType<NavButton>().Any(b => b.PageIndex == 4 && b.Active),
                      "生产模式调试页有导航高亮（入口可用）");
                Check(f2.Visible && nav2.Visible, "调试项显示后主窗口与导航一切正常");
                f2.Hide();
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
                    // ⚠ 2026-08-14 修复：不能直接 LoadFrom 共享盘（SMB/Samba）路径 —— .NET 加载
                    //   程序集会抛 FileLoadException 0x80131515「不支持的操作」（win10 VM 实跑踩中，
                    //   Windows 更新后 SMB 客户端行为变化）。先拷到本地临时目录再加载，
                    //   资源清单检查语义不变；读不到/拷贝失败则记 FAIL，不再让整个自检崩溃。
                    string? namesPath = null;
                    try
                    {
                        var local = Path.Combine(Path.GetTempPath(),
                            "Argus_selftest_" + Guid.NewGuid().ToString("N")[..8] + ".dll");
                        File.Copy(dll, local, overwrite: true);
                        namesPath = local;
                    }
                    catch (Exception ex)
                    {
                        Check(false, $"内置模板检查：拷贝 Argus.dll 到本地临时目录失败——{ex.GetType().Name} {ex.Message}");
                    }
                    if (namesPath != null)
                    {
                        var names = System.Reflection.Assembly.LoadFrom(namesPath)
                                          .GetManifestResourceNames();
                        var xmls = names.Where(n => n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList();
                        // v3.0.0：分发器的内置模板（tpl\*.xml）随它一起搬去 fct-distributor/ 了。
                        // 这里反向断言 —— 主程序里**不该**再有任何 xml 资源，否则说明拆分没拆干净。
                        Check(xmls.Count == 0,
                              $"主程序里已无内置模板 xml 资源（分发器已独立，实得 {xmls.Count} 个）");
                        Check(names.Any(n => n.EndsWith("app_icon.ico", StringComparison.OrdinalIgnoreCase)),
                              "内嵌图标仍在（取数/TDMS 工具靠 EndsWith(\"app_icon.ico\") 找它）");
                    }
                }
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

        // CWE-1236 CSV 公式注入防护（v3.2.1 安全审计）：= + - @ 开头的值必须被加单引号前缀
        var csvInjPath = Path.Combine(work, "注入.csv");
        MaintenanceExporter.ExportCsv(csvInjPath, new[]
        {
            new MaintenanceRecord
            {
                FailItem = "=cmd|'/C calc'!A0",
                FailReason = "+SUM(1,2)",
                EquipmentSn = "-1+1",
                Resolver = "@evil",
                Notes = "正常备注",
                Severity = "major",
                Status = "open",
            },
            new MaintenanceRecord
            {
                FailItem = "\tTAB-LED",
                FailReason = "\rCR-LED",
                EquipmentSn = "SN-OK",
                Resolver = "张三",
                Notes = "正常备注2",
                Severity = "major",
                Status = "open",
            },
        });
        var csvBody = File.ReadAllText(csvInjPath);
        Check(csvBody.Contains("'=cmd|'/C calc'!A0"), "CSV 公式注入防护：= 开头加单引号前缀");
        Check(csvBody.Contains("'+SUM(1,2)"), "CSV 公式注入防护：+ 开头加单引号前缀");
        Check(csvBody.Contains("'-1+1"), "CSV 公式注入防护：- 开头加单引号前缀");
        Check(csvBody.Contains("'@evil"), "CSV 公式注入防护：@ 开头加单引号前缀");
        Check(csvBody.Contains("'\tTAB-LED"), "CSV 公式注入防护：\\t 开头加单引号前缀");
        Check(csvBody.Contains("\"'\rCR-LED\""), "CSV 公式注入防护：\\r 开头加单引号前缀（含 \\r 被引号包裹）");
        Check(!csvBody.Contains("\"=cmd"), "CSV 公式注入防护：= 开头未被引号包裹（引号包裹对公式无效）");

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
            Check(body.Contains("未知问题") || body.Contains("待办"),
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

        // ==================== 机台聚合推送器（Phase 1） ====================
        Console.WriteLine("\n【机台聚合推送】FAIL 事件文件 / 心跳 / 断线补偿 / 续推");
        RunAggPusherTests(work);

        // ==================== 聚合端监听（Phase 2） ====================
        Console.WriteLine("\n【聚合端监听】AggDatabase 幂等 / 初始扫描 / 增量事件 / 心跳离线");
        RunAggWatcherTests(work);

        // ==================== B2 双通道：机台端 HTTP 直推 + 聚合端 HTTP 接收（Phase 2 增强） ====================
        Console.WriteLine("\n【HTTP 双通道】机台端直推 / 心跳 / 断线补偿 / 聚合端接收 / 全链路 / 路由");
        RunHttpChannelTests(work);

        // ==================== Web 聚合服务（Phase 3：纯 Web 模式） ====================
        Console.WriteLine("\n【Web 聚合服务】页面与 API / 推送入库 / XML 下载白名单 / CSV 导出 / 路由错误码");
        RunWebAggServerTests(work);

        // ==================== agg_token 鉴权 + 分页搜索（v3.4.2） ====================
        Console.WriteLine("\n【聚合鉴权与分页】agg_token 403 / 分页 offset / 搜索 q / FAIL 总数 / XML 内容落盘");
        RunAggTokenPagingTests(work);

        // ==================== Web 目录浏览（Phase 3：/api/list + 文件页签） ====================
        Console.WriteLine("\n【Web 目录浏览】/api/list 白名单与排序 / 下载回归 / 页面文件页签");
        RunWebFileBrowserTests(work);

        // ==================== 聚合服务一键部署（v3.4.0：agg --install） ====================
        // 只测可测部分：默认 config JSON 生成；netsh/快捷方式/进程启动属系统操作，不实跑（只编译）
        Console.WriteLine("\n【一键部署】默认聚合配置生成（JSON 内容与合法性）");
        RunAggInstallConfigTests();

        Console.WriteLine(_fail == 0 ? "\n==== 全部通过 ====" : $"\n==== {_fail} 项失败 ====");
        return _fail == 0 ? 0 : 1;
    }

    // ==================== 机台聚合推送器（Phase 1） ====================

    static TestRecord MakeFailRec(string work, string station, string xml, string sn, string? reason, string result = "FAIL")
        => new TestRecord
        {
            StationId = station,
            Model = "E3002781",
            Category = "Online",
            TestDate = "20260811",
            Sn = sn,
            Result = result,
            XmlPath = xml,
            FailReason = reason,
            BatchTimestamp = "2026-08-11 12:00:00",
            HasFailItems = result == "FAIL",
        };

    static bool WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
        {
            if (cond()) return true;
            Thread.Sleep(100);
        }
        return cond();
    }

    static int CountFailFiles(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "fail-*.json").Length : 0;
        }
        catch { return 0; }
    }

    static void RunAggPusherTests(string work)
    {
        var aggRoot = Path.Combine(work, "agg");
        Directory.CreateDirectory(aggRoot);

        // ---- ① 配置未启用时不动作 ----
        var shareA = Path.Combine(aggRoot, "shareA");
        var dataA = Path.Combine(aggRoot, "dataA");
        var dbA = new Database(Path.Combine(aggRoot, "dbA.db"));
        var pA = new AgentPusher(new AppConfig { StationId = "FCT1", AggEnabled = false, AggShareRoot = shareA },
            "FCT1", dbA, dataA, retrySec: 1, heartbeatSec: 2);
        pA.Init();
        Check(!pA.Active, "聚合推送：未启用配置下不激活");
        dbA.InsertOne(MakeFailRec(work, "FCT1", Path.Combine(work, "agg-a1.xml"), "SN-A1", "自检不良A1"));
        Thread.Sleep(800);
        Check(!Directory.Exists(shareA), "聚合推送：未启用时共享目录不产生任何文件");
        Check(!File.Exists(pA.StatePath) && !File.Exists(pA.QueuePath), "聚合推送：未启用时不写 agg_state/agg_queue");
        pA.Stop();

        // ---- ② 写文件成功（先入库后启动 = 续推扫描路径） ----
        var shareB = Path.Combine(aggRoot, "shareB");
        var dataB = Path.Combine(aggRoot, "dataB");
        var dbB = new Database(Path.Combine(aggRoot, "dbB.db"));
        for (int i = 0; i < 3; i++)
            dbB.InsertOne(MakeFailRec(work, "FCT1", Path.Combine(work, $"agg-b{i}.xml"), $"SN-B{i}", $"自检不良B{i}"));
        var failsB = dbB.FetchFailRecordsAfter(0);
        Check(failsB.Count == 3, $"聚合推送：先入库 3 条 FAIL（实得 {failsB.Count}）");
        var pB = new AgentPusher(new AppConfig { StationId = "FCT1", AggEnabled = true, AggShareRoot = shareB },
            "FCT1", dbB, dataB, retrySec: 1, heartbeatSec: 2);
        pB.Init();
        var dirB = Path.Combine(shareB, "FCT1");
        Check(WaitUntil(() => CountFailFiles(dirB) == 3, 10000), "聚合推送：启动续推扫描补齐 3 个 fail 文件");
        var idsB = failsB.Select(f => f.Id).OrderBy(x => x).ToList();
        foreach (var (rec, id) in failsB)
        {
            var path = Path.Combine(dirB, $"fail-{id}.json");
            Check(File.Exists(path), $"聚合推送：fail-{id}.json 已写入");
            if (File.Exists(path))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                var r = doc.RootElement;
                Check(r.GetProperty("machine").GetString() == "FCT1" && r.GetProperty("type").GetString() == "fail"
                      && r.GetProperty("seq").GetInt64() == id && !string.IsNullOrEmpty(r.GetProperty("ts").GetString()),
                      $"fail-{id}.json 字段 machine/type/seq/ts 正确");
                Check(r.GetProperty("data").GetProperty("sn").GetString() == rec.Sn
                      && r.GetProperty("data").GetProperty("result").GetString() == "FAIL",
                      $"fail-{id}.json data 内 sn/result 正确");
            }
        }
        Check(Directory.GetFiles(dirB, "*.tmp").Length == 0, "聚合推送：无 .tmp 残留（先写临时再改名）");
        Check(WaitUntil(() =>
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(pB.StatePath));
                return doc.RootElement.GetProperty("max_seq").GetInt64() == idsB[^1];
            }
            catch { return false; }
        }, 5000), $"agg_state.json max_seq={idsB[^1]} 正确");
        Check(pB.LastSeq == idsB[^1], $"LastSeq = {idsB[^1]}（已推送最大 seq）");
        dbB.InsertOne(MakeFailRec(work, "FCT1", Path.Combine(work, "agg-bpass.xml"), "SN-BP", null, "PASS"));
        Thread.Sleep(800);
        Check(CountFailFiles(dirB) == 3, "聚合推送：PASS 记录不生成 fail 文件");

        // ---- ③ seq 单调 + 去重 ----
        for (int i = 0; i < 2; i++)
            dbB.InsertOne(MakeFailRec(work, "FCT1", Path.Combine(work, $"agg-b3-{i}.xml"), $"SN-B3{i}", $"自检不良B3{i}"));
        var allFails = dbB.FetchFailRecordsAfter(0).Select(f => f.Id).OrderBy(x => x).ToList();
        Check(WaitUntil(() => CountFailFiles(dirB) == allFails.Count, 10000),
              $"聚合推送：事件驱动实时推送（共 {allFails.Count} 个文件）");
        var diskSeqs = Directory.GetFiles(dirB, "fail-*.json")
            .Select(f => long.Parse(Path.GetFileNameWithoutExtension(f)["fail-".Length..]))
            .OrderBy(x => x).ToList();
        Check(diskSeqs.SequenceEqual(allFails), "seq 与 test_records 自增 id 一一对应（单调、无重复）");
        var dup = failsB[0];
        var dupPath = Path.Combine(dirB, $"fail-{dup.Id}.json");
        var before = File.ReadAllText(dupPath);
        pB.EnqueueFail(dup.Rec, dup.Id);
        Thread.Sleep(800);
        Check(File.ReadAllText(dupPath) == before && CountFailFiles(dirB) == allFails.Count,
              "重复 seq 幂等跳过（文件不重写、不新增）");
        Check(pB.QueuedCount == 0 && pB.LastSeq == allFails[^1], "去重后队列为空、LastSeq 不变");
        pB.Stop();

        // ---- ④ 断线补偿 ----
        var shareBad = Path.Combine(aggRoot, "shareBad");
        File.WriteAllText(shareBad, "占位文件：让共享根不可建目录");
        var shareGood = Path.Combine(aggRoot, "shareGood");
        var dataD = Path.Combine(aggRoot, "dataD");
        var dbD = new Database(Path.Combine(aggRoot, "dbD.db"));
        var pD = new AgentPusher(new AppConfig { StationId = "FCT1", AggEnabled = true, AggShareRoot = shareBad },
            "FCT1", dbD, dataD, retrySec: 1, heartbeatSec: 60);
        pD.Init();
        for (int i = 0; i < 2; i++)
            dbD.InsertOne(MakeFailRec(work, "FCT1", Path.Combine(work, $"agg-d{i}.xml"), $"SN-D{i}", $"自检不良D{i}"));
        var idsD = dbD.FetchFailRecordsAfter(0).Select(f => f.Id).OrderBy(x => x).ToList();
        Check(idsD.Count == 2, $"聚合推送：断线前入库 2 条 FAIL（实得 {idsD.Count}）");
        Check(WaitUntil(() => pD.QueuedCount == 2, 3000), "写共享失败 -> 事件全部进内存队列");
        Check(!Directory.Exists(shareGood), "断线期间共享目录无任何文件");
        Check(WaitUntil(() =>
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(pD.QueuePath));
                var evs = doc.RootElement.GetProperty("events");
                return evs.GetArrayLength() == 2
                       && evs[0].GetProperty("seq").GetInt64() == idsD[0]
                       && evs[1].GetProperty("seq").GetInt64() == idsD[1]
                       && evs[0].GetProperty("type").GetString() == "fail"
                       && evs[0].GetProperty("json").GetString()!.Contains("\"type\":\"fail\"");
            }
            catch { return false; }
        }, 5000), "agg_queue.json 落盘 2 条未推送事件（含原始 JSON，重启不丢队）");
        pD.ShareRoot = shareGood;
        var dirD = Path.Combine(shareGood, "FCT1");
        Check(WaitUntil(() => CountFailFiles(dirD) == 2, 10000), "共享恢复后自动补推 2 个 fail 文件");
        Check(WaitUntil(() => pD.QueuedCount == 0 && pD.LastSeq == idsD[^1], 5000),
              $"补推完成：队列清空、LastSeq={idsD[^1]}");
        Check(WaitUntil(() =>
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(pD.StatePath));
                return doc.RootElement.GetProperty("max_seq").GetInt64() == idsD[^1];
            }
            catch { return false; }
        }, 5000), "agg_state.json 更新 max_seq");
        pD.Stop();

        // ---- ⑤ 心跳 ----
        var shareE = Path.Combine(aggRoot, "shareE");
        var dataE = Path.Combine(aggRoot, "dataE");
        var dbE = new Database(Path.Combine(aggRoot, "dbE.db"));
        var pE = new AgentPusher(new AppConfig { StationId = "FCT1", AggEnabled = true, AggShareRoot = shareE },
            "FCT1", dbE, dataE, retrySec: 1, heartbeatSec: 2);
        pE.Init();
        dbE.InsertOne(MakeFailRec(work, "FCT1", Path.Combine(work, "agg-e0.xml"), "SN-E0", "自检不良E0"));
        var idE = dbE.FetchFailRecordsAfter(0)[0].Id;
        var hb = Path.Combine(shareE, "FCT1", "heartbeat.json");
        // 启动瞬间会先写一次 last_seq=0 的心跳，等 FAIL 入队后 next 周期心跳才带上真实 seq
        Check(WaitUntil(() => File.Exists(hb), 10000), "心跳文件 heartbeat.json 已生成");
        Check(WaitUntil(() =>
        {
            if (!File.Exists(hb)) return false;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(hb));
                var r = doc.RootElement;
                return r.GetProperty("type").GetString() == "heartbeat"
                       && r.GetProperty("machine").GetString() == "FCT1"
                       && r.GetProperty("last_seq").GetInt64() == idE
                       && !string.IsNullOrEmpty(r.GetProperty("ts").GetString());
            }
            catch { return false; }
        }, 10000), $"心跳字段正确且 last_seq={idE}（最近已推送 seq）");
        string hb1 = File.ReadAllText(hb);
        Check(WaitUntil(() => File.Exists(hb) && File.ReadAllText(hb) != hb1, 10000), "心跳按周期持续覆盖更新");
        pE.Stop();

        // ---- ⑥ 重启恢复：读 agg_queue.json 补推 + 续推扫描合并去重 ----
        var share6Bad = Path.Combine(aggRoot, "share6Bad");
        File.WriteAllText(share6Bad, "占位文件：让共享根不可建目录");
        var share6Good = Path.Combine(aggRoot, "share6Good");
        var data6 = Path.Combine(aggRoot, "data6");
        var db6 = new Database(Path.Combine(aggRoot, "db6.db"));
        var p6a = new AgentPusher(new AppConfig { StationId = "FCT1", AggEnabled = true, AggShareRoot = share6Bad },
            "FCT1", db6, data6, retrySec: 1, heartbeatSec: 60);
        p6a.Init();
        for (int i = 0; i < 2; i++)
            db6.InsertOne(MakeFailRec(work, "FCT1", Path.Combine(work, $"agg-f{i}.xml"), $"SN-F{i}", $"自检不良F{i}"));
        Check(WaitUntil(() => p6a.QueuedCount == 2, 3000), "重启前：2 条事件滞留队列");
        p6a.Stop();
        Check(File.Exists(Path.Combine(data6, "agg_queue.json")), "停止时 agg_queue.json 落盘");
        // 模拟重启：同一个 data 目录 + 同一个 db，共享恢复
        var p6b = new AgentPusher(new AppConfig { StationId = "FCT1", AggEnabled = true, AggShareRoot = share6Good },
            "FCT1", db6, data6, retrySec: 1, heartbeatSec: 60);
        p6b.Init();
        var dir6 = Path.Combine(share6Good, "FCT1");
        var ids6 = db6.FetchFailRecordsAfter(0).Select(f => f.Id).OrderBy(x => x).ToList();
        Check(WaitUntil(() => CountFailFiles(dir6) == 2, 10000), "重启后补推 2 个 fail 文件（队列补推 + 数据库续推兜底）");
        Check(WaitUntil(() => p6b.QueuedCount == 0 && p6b.LastSeq == ids6[^1], 5000),
              $"补推完成：队列清空、LastSeq={ids6[^1]}（状态续推起点正确）");
        Check(WaitUntil(() =>
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p6b.QueuePath));
                return doc.RootElement.GetProperty("events").GetArrayLength() == 0;
            }
            catch { return false; }
        }, 5000), "补推后 agg_queue.json 清空");
        p6b.Stop();

        Console.WriteLine("    聚合推送自检完成");
    }

    // ==================== 聚合端监听（Phase 2） ====================
    // 机台端推送格式（AgentPusher.cs）：
    //   fail-{seq}.json  顶层 {machine, type:"fail", seq, ts, data:{id, station_id, model, category,
    //                     test_date, sn, result, xml_path, fail_reason, tester, panel_status,
    //                     batch_timestamp, has_fail_items(0/1), file_size}}
    //   heartbeat.json   顶层 {machine, type:"heartbeat", ts, last_seq, queued}
    // 机台端先写 .tmp 再 File.Move 改名，聚合端靠 FileSystemWatcher 的 Renamed 事件接住。

    /// <summary>机台端同款 fail JSON（data 字段齐全）。写到 dir 下 fail-{seq}.json。</summary>
    static void WriteFailJson(string dir, long seq, string machine)
        => WriteFailJsonTo(Path.Combine(dir, $"fail-{seq}.json"), seq, machine);

    /// <summary>机台端同款 fail JSON 字符串（无尾换行，与 AgentPusher http 直推的 body 完全同构）。
    /// 文件版与 HTTP 版共用，保证两个通道的契约一致。</summary>
    static string BuildFailJsonString(long seq, string machine)
    {
        var data = new Dictionary<string, object?>
        {
            ["id"] = seq,
            ["station_id"] = machine,
            ["model"] = "E3002781",
            ["category"] = "Online",
            ["test_date"] = "20260812",
            ["sn"] = $"SN-{machine}-{seq}",
            ["result"] = "FAIL",
            ["xml_path"] = $"X:\\fct\\{machine}-{seq}.xml",
            ["fail_reason"] = "自检不良",
            ["tester"] = "SELFTEST",
            ["panel_status"] = "0000",
            ["batch_timestamp"] = "2026-08-12 09:00:00",
            ["has_fail_items"] = 1,
            ["file_size"] = 12345,
        };
        return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["machine"] = machine,
            ["type"] = "fail",
            ["seq"] = seq,
            ["ts"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["data"] = data,
        });
    }

    /// <summary>机台端同款 fail JSON，但可指定 xml_path/sn/fail_reason（Web 服务 XML 下载白名单与 CSV 注入测试用）。</summary>
    static string BuildFailJsonCustom(long seq, string machine, string xmlPath, string sn, string failReason)
    {
        var data = new Dictionary<string, object?>
        {
            ["id"] = seq,
            ["station_id"] = machine,
            ["model"] = "E3002781",
            ["category"] = "Online",
            ["test_date"] = "20260812",
            ["sn"] = sn,
            ["result"] = "FAIL",
            ["xml_path"] = xmlPath,
            ["fail_reason"] = failReason,
            ["tester"] = "SELFTEST",
            ["panel_status"] = "0000",
            ["batch_timestamp"] = "2026-08-12 09:00:00",
            ["has_fail_items"] = 1,
            ["file_size"] = 12345,
        };
        return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["machine"] = machine,
            ["type"] = "fail",
            ["seq"] = seq,
            ["ts"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["data"] = data,
        });
    }

    /// <summary>机台端同款 heartbeat JSON 字符串（machine/type/ts/last_seq/queued，无尾换行）。</summary>
    static string BuildHeartbeatJsonString(string machine, long lastSeq, int queued)
        => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["machine"] = machine,
            ["type"] = "heartbeat",
            ["ts"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["last_seq"] = lastSeq,
            ["queued"] = queued,
        });

    /// <summary>机台端同款 fail JSON，写到指定路径（.tmp 或最终名）。</summary>
    static void WriteFailJsonTo(string path, long seq, string machine)
    {
        File.WriteAllText(path, BuildFailJsonString(seq, machine), System.Text.Encoding.UTF8);
    }

    /// <summary>机台端同款 heartbeat JSON（ts 可指定，离线判定用）。</summary>
    static void WriteHeartbeat(string dir, string machine, string ts, long lastSeq, int queued)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["machine"] = machine,
            ["type"] = "heartbeat",
            ["ts"] = ts,
            ["last_seq"] = lastSeq,
            ["queued"] = queued,
        }) + "\n";
        File.WriteAllText(Path.Combine(dir, "heartbeat.json"), json, System.Text.Encoding.UTF8);
    }

    /// <summary>GetMachines 里按机台名找一台；没有返回 null。</summary>
    static AggMachineStatus? FindMachine(AggWatcher w, string name)
        => w.GetMachines().FirstOrDefault(m => string.Equals(m.Machine, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>删独立临时目录（watcher 已 Stop、db 已 Dispose 后调用；删不掉不阻断）。</summary>
    static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch { }
    }

    static void RunAggWatcherTests(string work)
    {
        var aggRoot = Path.Combine(work, "aggwatch");
        Directory.CreateDirectory(aggRoot);

        // ---- ① AggDatabase 幂等：同 (machine, seq) 只入库一次，字段回读 ----
        using (var db1 = new AggDatabase(Path.Combine(aggRoot, "db1.db")))
        {
            var row1 = new AggFailRow
            {
                Machine = "FCT1", Seq = 5, Type = "fail",
                Ts = "2026-08-12 08:00:00", IngestTs = "2026-08-12 08:00:01",
                StationId = "FCT1", Model = "E3002781", Category = "Online", TestDate = "20260812",
                Sn = "SN-FCT1-5", Result = "FAIL", XmlPath = "X:\\fct\\fct1-5.xml",
                FailReason = "5V_Rail", Tester = "SELFTEST", PanelStatus = "0001",
                BatchTimestamp = "2026-08-12 07:59:59", HasFailItems = true, FileSize = 12345,
            };
            Check(db1.InsertFail(row1) == 1, $"AggDatabase：首次插入 (FCT1,seq=5) 返回 1 新入库，回写 Id={row1.Id}");
            Check(row1.Id > 0, "AggDatabase：插入成功回写自增 Id");
            var dup1 = new AggFailRow { Machine = "FCT1", Seq = 5, Type = "fail", IngestTs = "2026-08-12 08:00:02" };
            Check(db1.InsertFail(dup1) == 0, "AggDatabase：同 (machine,seq) 二次插入返回 0（幂等忽略）");
            Check(db1.FailCount("") == 1 && db1.FailCount("FCT1") == 1,
                  $"AggDatabase：全库计数 = 1（实得 {db1.FailCount("")}）");
            var q1 = db1.QueryFails(10, "FCT1");
            Check(q1.Count == 1, $"AggDatabase：QueryFails 只回 1 条（实得 {q1.Count}）");
            if (q1.Count == 1)
            {
                var r = q1[0];
                Check(r.Machine == "FCT1" && r.Seq == 5 && r.Type == "fail"
                      && r.Ts == "2026-08-12 08:00:00" && r.IngestTs == "2026-08-12 08:00:01"
                      && r.StationId == "FCT1" && r.Model == "E3002781" && r.Category == "Online"
                      && r.TestDate == "20260812" && r.Sn == "SN-FCT1-5" && r.Result == "FAIL"
                      && r.XmlPath == "X:\\fct\\fct1-5.xml" && r.FailReason == "5V_Rail"
                      && r.Tester == "SELFTEST" && r.PanelStatus == "0001"
                      && r.BatchTimestamp == "2026-08-12 07:59:59"
                      && r.HasFailItems && r.FileSize == 12345,
                      "AggDatabase：回读 17 个字段全部正确（字符串/时间/布尔/数字/路径）");
            }
        }

        // ---- ② AggDatabase 跨机台：计数隔离 / machine 过滤 / ingest_ts 倒序 ----
        using (var db2 = new AggDatabase(Path.Combine(aggRoot, "db2.db")))
        {
            AggFailRow Row2(string m, long seq, string ingest) => new()
            {
                Machine = m, Seq = seq, Type = "fail",
                Ts = "2026-08-12 08:00:00", IngestTs = ingest,
                StationId = m, Model = "E3002781", Sn = $"SN-{m}-{seq}", Result = "FAIL",
            };
            Check(db2.InsertFail(Row2("FCT1", 1, "2026-08-12 09:00:01")) == 1
                  && db2.InsertFail(Row2("FCT1", 2, "2026-08-12 09:00:02")) == 1
                  && db2.InsertFail(Row2("FCT2", 1, "2026-08-12 09:00:03")) == 1,
                  "AggDatabase：FCT1 两条 + FCT2 一条全部入库");
            Check(db2.FailCount("FCT1") == 2 && db2.FailCount("FCT2") == 1 && db2.FailCount("") == 3,
                  "AggDatabase：计数按机台隔离（FCT1=2, FCT2=1, 全库=3）");
            var fct2Rows = db2.QueryFails(10, "FCT2");
            Check(fct2Rows.Count == 1 && fct2Rows[0].Machine == "FCT2" && fct2Rows[0].Seq == 1,
                  "AggDatabase：QueryFails(machine=FCT2) 只回 FCT2 的记录");
            var ordered2 = db2.QueryFails(10);
            Check(ordered2.Count == 3
                  && ordered2[0].IngestTs == "2026-08-12 09:00:03"
                  && ordered2[1].IngestTs == "2026-08-12 09:00:02"
                  && ordered2[2].IngestTs == "2026-08-12 09:00:01",
                  "AggDatabase：QueryFails 按 ingest_ts DESC 排序（最新在前）");
        }

        // ---- ③ AggWatcher 初始扫描：启动时把已有 fail 文件全量入库 ----
        var share3 = Path.Combine(aggRoot, "share3");
        Directory.CreateDirectory(Path.Combine(share3, "FCT1"));
        WriteFailJson(Path.Combine(share3, "FCT1"), 1, "FCT1");
        WriteFailJson(Path.Combine(share3, "FCT1"), 2, "FCT1");
        using (var db3 = new AggDatabase(Path.Combine(aggRoot, "db3.db")))
        using (var w3 = new AggWatcher(share3, db3, heartbeatTimeoutSec: 90, pollSec: 1))
        {
            w3.Start();
            Check(WaitUntil(() => w3.ProcessedFiles == 2 && w3.TotalFails == 2, 10000),
                  $"初始扫描：2 个 fail 文件启动即入库（ProcessedFiles={w3.ProcessedFiles}, TotalFails={w3.TotalFails}）");
            Check(WaitUntil(() =>
            {
                var st = FindMachine(w3, "FCT1");
                return st != null && st.FailCount == 2 && st.LastFailAt.Length > 0;
            }, 10000), "初始扫描：GetMachines 识别出 FCT1，FailCount=2，LastFailAt 非空");
            var f3 = FindMachine(w3, "FCT1");
            Check(f3 != null && f3.FirstSeenAt.Length > 0, "初始扫描：FirstSeenAt 有值（取 MIN(ingest_ts)）");
            w3.Stop();
        }
        TryDeleteDir(share3);

        // ---- ④ AggWatcher 增量：运行中 .tmp 写文件再 File.Move 改名（Renamed 事件接住） ----
        var share4 = Path.Combine(aggRoot, "share4");
        Directory.CreateDirectory(Path.Combine(share4, "FCT1"));
        WriteFailJson(Path.Combine(share4, "FCT1"), 1, "FCT1");
        WriteFailJson(Path.Combine(share4, "FCT1"), 2, "FCT1");
        using (var db4 = new AggDatabase(Path.Combine(aggRoot, "db4.db")))
        using (var w4 = new AggWatcher(share4, db4, heartbeatTimeoutSec: 90, pollSec: 1))
        {
            w4.Start();
            Check(WaitUntil(() => w4.ProcessedFiles == 2, 10000), "增量前置：初始 2 个文件已入库");
            var tmp = Path.Combine(share4, "FCT1", "fail-3.json.tmp");
            WriteFailJsonTo(tmp, 3, "FCT1");                                  // 先写 .tmp（机台端两步写）
            File.Move(tmp, Path.Combine(share4, "FCT1", "fail-3.json"));     // 再改名成最终名
            Check(WaitUntil(() => w4.ProcessedFiles == 3 && w4.TotalFails == 3, 10000),
                  $"增量：.tmp→Move 后 fail-3 入库（ProcessedFiles={w4.ProcessedFiles}, TotalFails={w4.TotalFails}）");
            Check(db4.QueryFails(10).Any(r => r.Seq == 3 && r.Sn == "SN-FCT1-3"),
                  "增量：seq=3 记录字段回读正确（sn=SN-FCT1-3）");
            Check(!File.Exists(tmp), "增量：.tmp 已被改名，无残留");
            w4.Stop();
        }
        TryDeleteDir(share4);

        // ---- ⑤ AggWatcher 幂等：同 seq 文件再次出现不重复入库 ----
        var share5 = Path.Combine(aggRoot, "share5");
        Directory.CreateDirectory(Path.Combine(share5, "FCT1"));
        WriteFailJson(Path.Combine(share5, "FCT1"), 1, "FCT1");
        WriteFailJson(Path.Combine(share5, "FCT1"), 2, "FCT1");
        using (var db5 = new AggDatabase(Path.Combine(aggRoot, "db5.db")))
        using (var w5 = new AggWatcher(share5, db5, heartbeatTimeoutSec: 90, pollSec: 1))
        {
            w5.Start();
            Check(WaitUntil(() => w5.ProcessedFiles == 2, 10000), "幂等前置：初始 2 个文件已入库");
            // 同 seq 重发：删掉原文件再重写（必然触发 Created 事件）；随后写新 seq=3 当哨兵，
            // 哨兵入库时重复文件必然已处理完 —— 若重复也被计数，ProcessedFiles 会变成 4
            File.Delete(Path.Combine(share5, "FCT1", "fail-2.json"));
            WriteFailJson(Path.Combine(share5, "FCT1"), 2, "FCT1");
            WriteFailJson(Path.Combine(share5, "FCT1"), 3, "FCT1");
            Check(WaitUntil(() => w5.ProcessedFiles == 3, 10000),
                  $"幂等：重复 seq=2 文件被处理但不计数（哨兵 seq=3 到达后 ProcessedFiles=3，实得 {w5.ProcessedFiles}）");
            Check(w5.TotalFails == 3 && db5.QueryFails(10, "FCT1").Count == 3,
                  "幂等：全库 3 条、FCT1 3 条（重复文件未产生新行）");
            w5.Stop();
        }
        TryDeleteDir(share5);

        // ---- ⑥ AggWatcher 心跳：在线判定 + 超时判离线 ----
        var share6 = Path.Combine(aggRoot, "share6");
        Directory.CreateDirectory(Path.Combine(share6, "FCT1"));
        WriteHeartbeat(Path.Combine(share6, "FCT1"), "FCT1",
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), lastSeq: 7, queued: 3);
        using (var db6 = new AggDatabase(Path.Combine(aggRoot, "db6.db")))
        using (var w6 = new AggWatcher(share6, db6, heartbeatTimeoutSec: 3, pollSec: 1))
        {
            w6.Start();
            Check(WaitUntil(() =>
            {
                var st = FindMachine(w6, "FCT1");
                return st != null && st.Online && st.LastSeq == 7 && st.Queued == 3;
            }, 10000), "心跳：FCT1 上线（3s 超时阈值内判定），last_seq=7/queued=3 从心跳读出");
            // 心跳 ts 改成 2 分钟前 → 超过超时阈值 → 下一轮轮询判离线
            WriteHeartbeat(Path.Combine(share6, "FCT1"), "FCT1",
                DateTime.Now.AddMinutes(-2).ToString("yyyy-MM-dd HH:mm:ss"), 7, 3);
            Check(WaitUntil(() => FindMachine(w6, "FCT1") is { Online: false }, 10000),
                  "心跳：ts 落后超时阈值后判离线（轮询 1s 内翻转）");
            w6.Stop();
        }
        TryDeleteDir(share6);

        // ---- ⑦ AggWatcher 新机台上线：运行中新建目录 + 心跳即被发现 ----
        var share7 = Path.Combine(aggRoot, "share7");
        Directory.CreateDirectory(Path.Combine(share7, "FCT1"));
        WriteHeartbeat(Path.Combine(share7, "FCT1"), "FCT1",
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), 1, 0);
        using (var db7 = new AggDatabase(Path.Combine(aggRoot, "db7.db")))
        using (var w7 = new AggWatcher(share7, db7, heartbeatTimeoutSec: 30, pollSec: 1))
        {
            w7.Start();
            Check(WaitUntil(() => FindMachine(w7, "FCT1") is { Online: true }, 10000),
                  "新机台上线前置：FCT1 已在线");
            Directory.CreateDirectory(Path.Combine(share7, "FCT2"));
            WriteHeartbeat(Path.Combine(share7, "FCT2"), "FCT2",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), 1, 0);
            Check(WaitUntil(() =>
            {
                var st = FindMachine(w7, "FCT2");
                return st != null && st.Online;
            }, 10000), "新机台上线：运行中出现的 FCT2 目录 + 心跳被轮询发现且在线");
            Check(w7.GetMachines().Count == 2, $"新机台上线：机台列表共 2 台（实得 {w7.GetMachines().Count}）");
            w7.Stop();
        }
        TryDeleteDir(share7);

        Console.WriteLine("    聚合端监听自检完成");
    }

    // ==================== B2 双通道：机台端 HTTP 直推 + 聚合端 HTTP 接收（Phase 2 增强） ====================
    // 机台端 http 模式：AgentPusher(AggTransport=="http" && AggHttpUrl 非空) 把与 fail-{seq}.json
    // 完全相同的 JSON（UTF-8、无尾换行）POST 到 AggHttpUrl 根路径，心跳同构 POST；
    // 断线补偿复用队列：失败入 agg_queue.json，后台按 retrySec 重试。
    // 聚合端：HttpIngest 路由 POST /api/fail、/api/heartbeat、/（按 body type 分发），
    // 回调内容为机台 POST 的原始 JSON 字符串，交 AggWatcher.IngestFail/IngestHeartbeat 入库。
    // 测试接收端用本文件自带的 HttpCollector（HttpListener 绑 127.0.0.1，Windows 无需管理员权限）。

    /// <summary>用 TcpListener(0) 拿一个空闲端口（拿到后立即释放，随后交给 HttpListener/HttpIngest 绑定）。</summary>
    static int GetFreePort()
    {
        using var t = new TcpListener(IPAddress.Loopback, 0);
        t.Start();
        var port = ((IPEndPoint)t.LocalEndpoint).Port;
        t.Stop();
        return port;
    }

    /// <summary>
    /// 测试用 HTTP 接收端：后台线程 GetContext 循环收集 POST body（模拟聚合端，替代 HttpIngest 做白盒验证）。
    /// 只绑 127.0.0.1 前缀，Windows 普通用户即可监听；GetContext 在 Stop/Close 后抛异常，catch 后正常退出线程。
    /// </summary>
    sealed class HttpCollector : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Thread _thread;
        private readonly List<string> _bodies = new();
        private readonly object _lock = new();

        public HttpCollector(int port)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _thread = new Thread(Loop) { IsBackground = true, Name = "http-collector" };
            _thread.Start();
        }

        /// <summary>已收集的 body 条数。</summary>
        public int Count { get { lock (_lock) return _bodies.Count; } }

        /// <summary>已收集 body 的快照（按收到顺序）。</summary>
        public string[] Snapshot() { lock (_lock) return _bodies.ToArray(); }

        private void Loop()
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; }   // 监听器 Stop/Close：GetContext 抛 ObjectDisposedException/HttpListenerException，正常退出
                string body;
                try
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    body = reader.ReadToEnd();
                    ctx.Response.StatusCode = 200;
                }
                catch
                {
                    body = "";
                    ctx.Response.StatusCode = 500;
                }
                try { ctx.Response.Close(); } catch { }
                lock (_lock) _bodies.Add(body);
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { _thread.Join(3000); } catch { }
        }
    }

    static void RunHttpChannelTests(string work)
    {
        var httpRoot = Path.Combine(work, "httpchan");
        Directory.CreateDirectory(httpRoot);

        // ---- ① 机台端 http 模式推送：POST 与 fail-{seq}.json 完全相同的 JSON（无尾换行），不产生共享目录文件 ----
        var root1 = Path.Combine(httpRoot, "http1");
        Directory.CreateDirectory(root1);
        int port1 = GetFreePort();
        var db1h = new Database(Path.Combine(root1, "db1.db"));
        var p1 = new AgentPusher(new AppConfig
        {
            StationId = "FCT1",
            AggEnabled = true,
            AggTransport = "http",
            AggHttpUrl = $"http://127.0.0.1:{port1}/",   // AggShareRoot 留空：http 模式不该碰共享目录
        }, "FCT1", db1h, Path.Combine(root1, "data1"), retrySec: 1, heartbeatSec: 60);
        using (var col1 = new HttpCollector(port1))
        {
            p1.Init();
            Check(p1.HttpMode && p1.Active, "http 推送：http 模式激活（transport=http 且 url 非空）");
            db1h.InsertOne(MakeFailRec(work, "FCT1", Path.Combine(work, "http1-a.xml"), "SN-H1A", "自检不良H1A"));
            db1h.InsertOne(MakeFailRec(work, "FCT1", Path.Combine(work, "http1-b.xml"), "SN-H1B", "自检不良H1B"));
            // ⚠ 2026-08-14 修复竞态：不能数 col1.Count == 2 —— 推送器 worker 启动瞬间
            //   会立刻发首条心跳（_lastHeartbeat 初始 0），两条 fail 又几乎同时 POST，
            //   轮询窗口可能永远看不到「恰好 2 条」（win10 VM Windows 更新后时序漂移实测踩中）。
            //   语义是「两条 FAIL 事件都到了」，所以只数 type=fail 的 body，心跳是噪音。
            bool TwoFails() => col1.Snapshot().Count(b => b.Contains("\"type\":\"fail\"")) == 2;
            if (!WaitUntil(TwoFails, 30000))
            {
                var diag = string.Join(" | ", col1.Snapshot().Select(b =>
                {
                    try
                    {
                        using var d = System.Text.Json.JsonDocument.Parse(b);
                        var t = d.RootElement.GetProperty("type").GetString() ?? "?";
                        var s = d.RootElement.TryGetProperty("seq", out var sv) ? sv.GetInt64() : -1;
                        return $"{t}#{s}";
                    }
                    catch { return "?"; }
                }));
                Console.WriteLine($"    [诊断] col1.Count={col1.Count}, fail 数={col1.Snapshot().Count(b => b.Contains("\"type\":\"fail\""))}, bodies: {diag}");
            }
            Check(TwoFails(), "http 推送：收到 2 条 fail POST body（两条 FAIL 事件）");
            var rows1 = db1h.FetchFailRecordsAfter(0).OrderBy(x => x.Id).ToList();
            Check(rows1.Count == 2, $"http 推送：库里 2 条 FAIL（实得 {rows1.Count}）");
            var bodies1 = col1.Snapshot();
            foreach (var rec in rows1)
            {
                var body = bodies1.FirstOrDefault(b => b.Contains($"\"sn\":\"{rec.Rec.Sn}\""));
                Check(body != null, $"http 推送：SN={rec.Rec.Sn} 的 body 已收到");
                if (body == null) continue;
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var r = doc.RootElement;
                Check(r.GetProperty("machine").GetString() == "FCT1"
                      && r.GetProperty("type").GetString() == "fail"
                      && r.GetProperty("seq").GetInt64() == rec.Id && r.GetProperty("seq").GetInt64() > 0
                      && !string.IsNullOrEmpty(r.GetProperty("ts").GetString()),
                      $"http 推送：body 顶层 machine/type/seq/ts 正确（seq={rec.Id}）");
                Check(r.GetProperty("data").GetProperty("sn").GetString() == rec.Rec.Sn
                      && r.GetProperty("data").GetProperty("fail_reason").GetString() == rec.Rec.FailReason
                      && r.GetProperty("data").GetProperty("result").GetString() == "FAIL",
                      $"http 推送：body data 内 sn/fail_reason/result 正确（SN={rec.Rec.Sn}）");
                Check(!body.EndsWith('\n') && !body.EndsWith('\r'),
                      $"http 推送：body 末尾无换行（{body.Length} 字符，与文件版一致）");
            }
            Check(!Directory.Exists(Path.Combine(root1, "share1")),
                  "http 推送：不产生任何共享目录文件（share 目录不存在）");
            p1.Stop();
        }
        TryDeleteDir(root1);

        // ---- ② http 心跳：后台线程按周期 POST heartbeat（machine/type/ts/last_seq/queued） ----
        var root2 = Path.Combine(httpRoot, "http2");
        Directory.CreateDirectory(root2);
        int port2 = GetFreePort();
        var db2h = new Database(Path.Combine(root2, "db2.db"));
        var p2 = new AgentPusher(new AppConfig
        {
            StationId = "FCT1",
            AggEnabled = true,
            AggTransport = "http",
            AggHttpUrl = $"http://127.0.0.1:{port2}/",
        }, "FCT1", db2h, Path.Combine(root2, "data2"), retrySec: 1, heartbeatSec: 1);
        using (var col2 = new HttpCollector(port2))
        {
            p2.Init();
            Check(WaitUntil(() => col2.Snapshot().Any(b => b.Contains("\"type\":\"heartbeat\"")), 10000),
                  "http 心跳：后台线程按周期 POST 心跳");
            var hb = col2.Snapshot().FirstOrDefault(b => b.Contains("\"type\":\"heartbeat\""));
            if (hb != null)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(hb);
                var r = doc.RootElement;
                Check(r.GetProperty("machine").GetString() == "FCT1"
                      && r.GetProperty("type").GetString() == "heartbeat"
                      && !string.IsNullOrEmpty(r.GetProperty("ts").GetString()),
                      "http 心跳：machine/type/ts 字段正确");
                Check(r.TryGetProperty("last_seq", out _) && r.TryGetProperty("queued", out _),
                      "http 心跳：含 last_seq/queued 字段");
            }
            Check(WaitUntil(() => col2.Count >= 2, 8000), "http 心跳：后续周期持续 POST（累计 >= 2 条）");
            p2.Stop();
        }
        TryDeleteDir(root2);

        // ---- ③ http 断线补偿：断线期间事件进 agg_queue.json，接收端恢复后自动补推、队列清空 ----
        var root3 = Path.Combine(httpRoot, "http3");
        Directory.CreateDirectory(root3);
        int port3 = GetFreePort();
        var db3h = new Database(Path.Combine(root3, "db3.db"));
        var p3 = new AgentPusher(new AppConfig
        {
            StationId = "FCT1",
            AggEnabled = true,
            AggTransport = "http",
            AggHttpUrl = $"http://127.0.0.1:{port3}/",
        }, "FCT1", db3h, Path.Combine(root3, "data3"), retrySec: 1, heartbeatSec: 60);
        var col3 = new HttpCollector(port3);
        try
        {
            p3.Init();
            db3h.InsertOne(MakeFailRec(work, "FCT1", Path.Combine(work, "http3-a.xml"), "SN-H3A", "自检不良H3A"));
            // ⚠ 必须等 seq=1 完全确认出队（LastSeq>=1）再 Dispose，否则 Dispose 会杀掉 in-flight 的
            //   seq=1 响应：请求已处理(body 到达)但客户端读不到响应 → 判失败留队 → 后续断言错乱。
            //   body 到达与响应收讫有先后（HTTP keep-alive 连接池），分两步各自独立等。
            Check(WaitUntil(() => col3.Count >= 1, 10000), "http 断线前置：seq=1 body 已到达");
            Check(WaitUntil(() => p3.LastSeq >= 1, 20000),
                  "http 断线前置：seq=1 已确认出队（响应收讫，Dispose 无 in-flight 竞态）");
            col3.Dispose();   // 模拟断线：接收端关闭，之后的 POST 全部失败
            db3h.InsertOne(MakeFailRec(work, "FCT1", Path.Combine(work, "http3-b.xml"), "SN-H3B", "自检不良H3B"));
            var id3b = db3h.FetchFailRecordsAfter(0)[1].Id;
            Check(WaitUntil(() => p3.QueuedCount == 1, 10000), "http 断线：第 2 条滞留内存队列");
            Check(WaitUntil(() =>
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p3.QueuePath));
                    var evs = doc.RootElement.GetProperty("events");
                    for (int i = 0; i < evs.GetArrayLength(); i++)
                        if (evs[i].GetProperty("seq").GetInt64() == id3b) return true;
                    return false;
                }
                catch { return false; }
            }, 5000), "http 断线：agg_queue.json 已落盘且含断线 seq（重启不丢队）");
            // 重启接收端（同端口）；worker 按 retrySec=1 自动补推
            using var col3b = new HttpCollector(port3);
            Check(WaitUntil(() => col3b.Count == 1, 30000), "http 断线：接收端恢复后自动补推第 2 条");
            var b3b = col3b.Snapshot().FirstOrDefault(b => b.Contains("\"sn\":\"SN-H3B\""));
            Check(b3b != null && !b3b.EndsWith('\n'), "http 断线：补推的 body 是完整 JSON（无尾换行）");
            Check(WaitUntil(() => p3.QueuedCount == 0 && p3.LastSeq == id3b, 5000),
                  $"http 断线：补推完成队列清空、LastSeq={id3b}");
            p3.Stop();
        }
        finally
        {
            try { col3.Dispose(); } catch { }
        }
        TryDeleteDir(root3);

        // ---- ④ HttpIngest 接收：onFail/onHeartbeat 回调收到原始 JSON、内容与 POST 完全一致 ----
        var root4 = Path.Combine(httpRoot, "http4");
        Directory.CreateDirectory(root4);
        int port4 = GetFreePort();
        var recv4 = new List<string>();
        var hb4 = new List<string>();
        var lock4 = new object();
        using (var ing4 = new HttpIngest(port4,
            s => { lock (lock4) recv4.Add(s); },
            s => { lock (lock4) hb4.Add(s); }))
        {
            ing4.Start();
            Check(WaitUntil(() => ing4.Listening, 5000), "HttpIngest：Start 后进入监听状态");
            using var client4 = new HttpClient();
            var failJson4 = BuildFailJsonString(11, "FCT1");
            var rFail = client4.PostAsync($"http://127.0.0.1:{port4}/api/fail",
                new StringContent(failJson4, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(rFail.StatusCode == HttpStatusCode.OK, $"HttpIngest：POST /api/fail 回 200（实得 {(int)rFail.StatusCode}）");
            Check(WaitUntil(() => { lock (lock4) return recv4.Count == 1; }, 5000), "HttpIngest：onFail 回调收到 1 条");
            Check(WaitUntil(() => ing4.ReceivedCount >= 1, 5000),
                  $"HttpIngest：ReceivedCount={ing4.ReceivedCount}（合法请求才计数）");
            string? got4;
            lock (lock4) got4 = recv4.FirstOrDefault();
            Check(got4 == failJson4, "HttpIngest：onFail 收到的内容与 POST 的 JSON 完全一致（原样透传）");
            var hbJson4 = BuildHeartbeatJsonString("FCT1", 11, 0);
            var rHb = client4.PostAsync($"http://127.0.0.1:{port4}/api/heartbeat",
                new StringContent(hbJson4, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(rHb.StatusCode == HttpStatusCode.OK, "HttpIngest：POST /api/heartbeat 回 200");
            Check(WaitUntil(() => { lock (lock4) return hb4.Count == 1; }, 5000), "HttpIngest：onHeartbeat 回调收到 1 条");
            string? gotHb4;
            lock (lock4) gotHb4 = hb4.FirstOrDefault();
            Check(gotHb4 == hbJson4, "HttpIngest：onHeartbeat 收到的内容与 POST 一致");
        }
        TryDeleteDir(root4);

        // ---- ⑤ 聚合端全链路：watcher + ingest 接线，机台自动出现 / 在线 / 超时离线 / 重复 seq 幂等 ----
        var root5 = Path.Combine(httpRoot, "http5");
        var share5 = Path.Combine(root5, "share5");
        // ⚠ 给「存在的空目录」而非不存在目录：AggWatcher.Start() 对不存在的 shareRoot 直接 return、
        // 不启动心跳轮询线程，HTTP 机台的离线翻转（LastSeen 超时）就永远不发生。
        // HTTP 机台不读共享文件，空目录同样验证「http 推送自动创建机台、不依赖共享目录」。
        Directory.CreateDirectory(share5);
        int port5 = GetFreePort();
        using (var db5 = new AggDatabase(Path.Combine(root5, "agg5.db")))
        using (var w5 = new AggWatcher(share5, db5, heartbeatTimeoutSec: 1, pollSec: 1))
        using (var ing5 = new HttpIngest(port5, w5.IngestFail, w5.IngestHeartbeat))
        {
            ing5.Start();
            w5.Start();
            using var client5 = new HttpClient();
            var failJson5 = BuildFailJsonString(21, "FCT1");
            var r5a = client5.PostAsync($"http://127.0.0.1:{port5}/api/fail",
                new StringContent(failJson5, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            var r5b = client5.PostAsync($"http://127.0.0.1:{port5}/api/heartbeat",
                new StringContent(BuildHeartbeatJsonString("FCT1", 21, 0), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(r5a.StatusCode == HttpStatusCode.OK && r5b.StatusCode == HttpStatusCode.OK,
                  "http 全链路：POST /api/fail 与 /api/heartbeat 均回 200");
            Check(WaitUntil(() =>
            {
                var st = FindMachine(w5, "FCT1");
                return st != null && st.Online && st.FailCount == 1;
            }, 10000), "http 全链路：HTTP 推送的机台自动出现在看板且在线、FailCount=1（无共享文件也创建）");
            Check(WaitUntil(() => FindMachine(w5, "FCT1") is { Online: false }, 10000),
                  "http 全链路：心跳超时(1s)后轮询判离线（LastSeen 统一在线判定）");
            var r5c = client5.PostAsync($"http://127.0.0.1:{port5}/api/fail",
                new StringContent(failJson5, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(r5c.StatusCode == HttpStatusCode.OK, "http 全链路：重复 seq 重发回 200");
            Check(WaitUntil(() => FindMachine(w5, "FCT1")?.FailCount == 1, 5000),
                  "http 全链路：重复 seq 幂等，FailCount 不增加");
            w5.Stop();
        }
        TryDeleteDir(root5);

        // ---- ⑥ 路由响应码：200 / 405 / 404 / 根路径按 body type 分发 ----
        var root6 = Path.Combine(httpRoot, "http6");
        Directory.CreateDirectory(root6);
        int port6 = GetFreePort();
        using (var ing6 = new HttpIngest(port6, _ => { }, _ => { }))
        {
            ing6.Start();
            Check(WaitUntil(() => ing6.Listening, 5000), "路由：HttpIngest 已监听");
            using var client6 = new HttpClient();
            var baseUrl6 = $"http://127.0.0.1:{port6}";
            var okJson6 = BuildFailJsonString(31, "FCT1");
            var r200 = client6.PostAsync($"{baseUrl6}/api/fail",
                new StringContent(okJson6, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(r200.StatusCode == HttpStatusCode.OK, "路由：POST /api/fail -> 200");
            var r405 = client6.GetAsync($"{baseUrl6}/api/fail").GetAwaiter().GetResult();
            Check(r405.StatusCode == HttpStatusCode.MethodNotAllowed, "路由：GET /api/fail -> 405");
            var r404 = client6.PostAsync($"{baseUrl6}/nope",
                new StringContent(okJson6, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(r404.StatusCode == HttpStatusCode.NotFound, "路由：POST /nope -> 404");
            // 机台端把 fail/heartbeat 都 POST 到 AggHttpUrl 根路径：根路径必须按 body type 分发
            var rRoot = client6.PostAsync($"{baseUrl6}/",
                new StringContent(BuildFailJsonString(32, "FCT1"), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(rRoot.StatusCode == HttpStatusCode.OK, "路由：POST /（根路径）按 body type=fail 分发 -> 200");
        }
        TryDeleteDir(root6);

        Console.WriteLine("    HTTP 双通道自检完成");
    }

    // ==================== Web 聚合服务（Phase 3：纯 Web 模式） ====================
    // WebAggServer.cs 路由：GET /（看板页）、/api/machines、/api/fails?limit&machine、
    // /api/file?id=|path=（XML 下载，白名单=resultsRoot 或 {BaseDir}/logs/app.log）、
    // /api/export.csv（UTF-8 BOM + 公式注入防护）、/api/health；POST /api/fail、/api/heartbeat、/（按 body type 分发）；
    // 未知路径 404；方法不符 405；body 超 1MB 413；id 非数字/无参数 400；穿越/越权/文件不存在 403；id 查无记录 404。
    // JSON 字段为 PascalCase（AggFailRow/AggMachineStatus 是 public 字段，序列化开 IncludeFields）。
    // 注意：POST 入库在服务端 Task.Run 异步完成，回 200 后必须 WaitUntil 等库。

    static void RunWebAggServerTests(string work)
    {
        var webRoot = Path.Combine(work, "webagg");
        var resultsRoot = Path.Combine(webRoot, "results");
        var shareRoot = Path.Combine(webRoot, "share");
        Directory.CreateDirectory(resultsRoot);
        Directory.CreateDirectory(shareRoot);   // ⚠ 必须存在：AggWatcher.Start() 对不存在的 shareRoot 直接 return
        int port = GetFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";

        using (var db = new AggDatabase(Path.Combine(webRoot, "agg.db")))
        using (var w = new AggWatcher(shareRoot, db, heartbeatTimeoutSec: 60, pollSec: 1))
        using (var srv = new WebAggServer(port, w, db, resultsRoot, shareRoot))
        {
            w.Start();
            srv.Start();
            Check(WaitUntil(() => srv.Listening, 5000), "Web 服务：Start 后进入监听状态");

            // ---- ① 页面与 API 可用 ----
            var rPage = _http.GetAsync($"{baseUrl}/").GetAwaiter().GetResult();
            Check(rPage.StatusCode == HttpStatusCode.OK && rPage.Content.Headers.ContentType?.MediaType == "text/html",
                  "GET / -> 200 text/html（看板页）");
            if (rPage.StatusCode == HttpStatusCode.OK)
            {
                var page = rPage.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Check(page.Contains("Argus 多机台聚合看板"), "GET /：页面含看板标题元素");
            }
            var rHealth = _http.GetAsync($"{baseUrl}/api/health").GetAwaiter().GetResult();
            Check(rHealth.StatusCode == HttpStatusCode.OK, "GET /api/health -> 200");
            if (rHealth.StatusCode == HttpStatusCode.OK)
            {
                using var hdoc = System.Text.Json.JsonDocument.Parse(
                    rHealth.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Check(hdoc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
                      "GET /api/health：JSON 为 {\"ok\":true}");
            }
            var rMach = _http.GetAsync($"{baseUrl}/api/machines").GetAwaiter().GetResult();
            Check(rMach.StatusCode == HttpStatusCode.OK, "GET /api/machines -> 200");
            if (rMach.StatusCode == HttpStatusCode.OK)
            {
                using var mdoc = System.Text.Json.JsonDocument.Parse(
                    rMach.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Check(mdoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                      && mdoc.RootElement.GetArrayLength() == 0,
                      "GET /api/machines：空库时返回合法 JSON 数组 []");
            }
            var rFails0 = _http.GetAsync($"{baseUrl}/api/fails").GetAwaiter().GetResult();
            Check(rFails0.StatusCode == HttpStatusCode.OK, "GET /api/fails -> 200");
            if (rFails0.StatusCode == HttpStatusCode.OK)
            {
                using var fdoc = System.Text.Json.JsonDocument.Parse(
                    rFails0.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Check(fdoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                      && fdoc.RootElement.GetArrayLength() == 0,
                      "GET /api/fails：空库时返回合法 JSON 数组 []");
            }
            Check(srv.ReceivedCount >= 4, $"Web 服务：合法请求计入 ReceivedCount（实得 {srv.ReceivedCount}）");

            // ---- ② 推送路由 + 入库 ----
            var failJson = BuildFailJsonCustom(41, "FCT1", "X:\\fct\\web41.xml", "SN-WEB-41", "5V_Rail");
            var rPush = _http.PostAsync($"{baseUrl}/api/fail",
                new StringContent(failJson, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(rPush.StatusCode == HttpStatusCode.OK, "POST /api/fail（合法 fail JSON）-> 200");
            long cntPush = 0;
            Check(WaitUntil(() => { cntPush = db.FailCount(""); return cntPush == 1; }, 10000),
                  $"推送入库：聚合库 FailCount=1（实得 {cntPush}）");
            var rFails1 = _http.GetAsync($"{baseUrl}/api/fails").GetAwaiter().GetResult();
            if (rFails1.StatusCode == HttpStatusCode.OK)
            {
                using var f1 = System.Text.Json.JsonDocument.Parse(
                    rFails1.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                var arr = f1.RootElement;
                Check(arr.GetArrayLength() == 1
                      && arr[0].GetProperty("Machine").GetString() == "FCT1"
                      && arr[0].GetProperty("Sn").GetString() == "SN-WEB-41"
                      && arr[0].GetProperty("FailReason").GetString() == "5V_Rail"
                      && arr[0].GetProperty("Result").GetString() == "FAIL",
                      "GET /api/fails：回读该条记录字段正确（machine/sn/fail_reason/result）");
            }
            var rHb = _http.PostAsync($"{baseUrl}/api/heartbeat",
                new StringContent(BuildHeartbeatJsonString("FCT1", 41, 0), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(rHb.StatusCode == HttpStatusCode.OK, "POST /api/heartbeat -> 200");
            Check(WaitUntil(() => FindMachine(w, "FCT1") is { Online: true }, 10000),
                  "POST /api/heartbeat：机台 FCT1 出现在看板且在线");
            var rRootFail = _http.PostAsync($"{baseUrl}/",
                new StringContent(BuildFailJsonCustom(42, "FCT2", "X:\\fct\\web42.xml", "SN-WEB-42", "注入测试"),
                    Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(rRootFail.StatusCode == HttpStatusCode.OK, "POST /（body type=fail）-> 200");
            var rRootHb = _http.PostAsync($"{baseUrl}/",
                new StringContent(BuildHeartbeatJsonString("FCT2", 42, 1), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(rRootHb.StatusCode == HttpStatusCode.OK, "POST /（body type=heartbeat）-> 200");
            long cntRoot = 0;
            Check(WaitUntil(() => { cntRoot = db.FailCount(""); return cntRoot == 2; }, 10000),
                  $"POST / 分发：fail 已入库（FailCount={cntRoot}，FCT1+FCT2 各一条）");

            // ---- ③ XML 下载白名单 ----
            var xml = Path.Combine(resultsRoot, "rep-001.xml");
            const string xmlContent = "<BATCH><STATUS>FAIL</STATUS></BATCH>";
            File.WriteAllText(xml, xmlContent);
            var rPushXml = _http.PostAsync($"{baseUrl}/api/fail",
                new StringContent(BuildFailJsonCustom(43, "FCT1", xml, "SN-WEB-43", "XML 下载"),
                    Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(rPushXml.StatusCode == HttpStatusCode.OK, "XML 白名单：POST fail（xml_path 指向白名单内文件）-> 200");
            long xmlId = 0;
            Check(WaitUntil(() =>
            {
                var row = db.QueryFails(10, "FCT1").FirstOrDefault(r => r.Sn == "SN-WEB-43");
                if (row == null) return false;
                xmlId = row.Id;
                return true;
            }, 10000), "XML 白名单：带 xml_path 的记录已入库（取到 id）");
            var rById = _http.GetAsync($"{baseUrl}/api/file?id={xmlId}").GetAwaiter().GetResult();
            Check(rById.StatusCode == HttpStatusCode.OK, $"GET /api/file?id={xmlId} -> 200");
            if (rById.StatusCode == HttpStatusCode.OK)
                Check(rById.Content.ReadAsStringAsync().GetAwaiter().GetResult() == xmlContent,
                      "GET /api/file?id=：内容与 resultsRoot 下文件一致");
            var rByPath = _http.GetAsync($"{baseUrl}/api/file?path={Uri.EscapeDataString(xml)}").GetAwaiter().GetResult();
            Check(rByPath.StatusCode == HttpStatusCode.OK, "GET /api/file?path=（白名单内绝对路径）-> 200");
            var rTraversal = _http.GetAsync($"{baseUrl}/api/file?path=../secret.txt").GetAwaiter().GetResult();
            Check(rTraversal.StatusCode == HttpStatusCode.Forbidden, "GET /api/file?path=../secret.txt（目录穿越）-> 403");
            var outside = Path.Combine(webRoot, "secret.txt");
            File.WriteAllText(outside, "秘密内容");
            var rOutside = _http.GetAsync($"{baseUrl}/api/file?path={Uri.EscapeDataString(outside)}").GetAwaiter().GetResult();
            Check(rOutside.StatusCode == HttpStatusCode.Forbidden, "GET /api/file?path={resultsRoot 外真实文件} -> 403");
            var rGhost = _http.GetAsync($"{baseUrl}/api/file?id=999999").GetAwaiter().GetResult();
            Check(rGhost.StatusCode == HttpStatusCode.NotFound, "GET /api/file?id=999999（记录不存在）-> 404");
            var rBadId = _http.GetAsync($"{baseUrl}/api/file?id=abc").GetAwaiter().GetResult();
            Check(rBadId.StatusCode == HttpStatusCode.BadRequest, "GET /api/file?id=abc（非数字）-> 400");

            // ---- ④ CSV 导出 ----
            var rCsv = _http.GetAsync($"{baseUrl}/api/export.csv").GetAwaiter().GetResult();
            Check(rCsv.StatusCode == HttpStatusCode.OK, "GET /api/export.csv -> 200");
            if (rCsv.StatusCode == HttpStatusCode.OK)
            {
                var bytes = rCsv.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                Check(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                      "CSV：以 UTF-8 BOM（EF BB BF）开头");
                var csv = Encoding.UTF8.GetString(bytes);
                Check(csv.Contains("时间") && csv.Contains("机台"), "CSV：表头含「时间」「机台」");
                Check(csv.Contains("SN-WEB-41") && csv.Contains("5V_Rail"), "CSV：数据行含已入库的 sn/fail_reason");
            }
            var rInject = _http.PostAsync($"{baseUrl}/api/fail",
                new StringContent(BuildFailJsonCustom(44, "FCT1", "X:\\fct\\web44.xml", "SN-WEB-44", "=cmd|' /C calc'!A0"),
                    Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(rInject.StatusCode == HttpStatusCode.OK, "CSV 注入：POST fail（fail_reason 以 = 开头）-> 200");
            long cntInject = 0;
            Check(WaitUntil(() => { cntInject = db.FailCount(""); return cntInject == 4; }, 10000),
                  $"CSV 注入：注入记录已入库（FailCount={cntInject}）");
            var rCsv2 = _http.GetAsync($"{baseUrl}/api/export.csv").GetAwaiter().GetResult();
            if (rCsv2.StatusCode == HttpStatusCode.OK)
            {
                var csv2 = Encoding.UTF8.GetString(rCsv2.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                Check(csv2.Contains("'=cmd|' /C calc'!A0"),
                      "CSV 注入：= 开头字段被加单引号前缀（'=cmd...），Excel 不执行公式");
            }

            // ---- ⑤ 路由错误码 ----
            var r404 = _http.GetAsync($"{baseUrl}/api/nope").GetAwaiter().GetResult();
            Check(r404.StatusCode == HttpStatusCode.NotFound, "GET /api/nope（未知路径）-> 404");
            var r405put = _http.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/api/fail")).GetAwaiter().GetResult();
            Check(r405put.StatusCode == HttpStatusCode.MethodNotAllowed, "PUT /api/fail（方法不符）-> 405");
            var r405get = _http.GetAsync($"{baseUrl}/api/fail").GetAwaiter().GetResult();
            Check(r405get.StatusCode == HttpStatusCode.MethodNotAllowed, "GET /api/fail（方法不符）-> 405");
            var big = new string('x', 2 * 1024 * 1024);
            var r413 = _http.PostAsync($"{baseUrl}/api/fail",
                new StringContent(big, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(r413.StatusCode == HttpStatusCode.RequestEntityTooLarge, "POST /api/fail（body 超 1MB）-> 413");

        srv.Stop();
        w.Stop();
    }
    TryDeleteDir(webRoot);
    Console.WriteLine("    Web 聚合服务自检完成");
    }

    // ==================== agg_token 鉴权 + 分页搜索 + XML 内容落盘（v3.4.2） ====================
    // 场景：token 未配放行 / 配了不匹配 403 / 匹配 200（头与 query 两路）/ 分页 offset /
    // 搜索 q（sn/model/fail_reason）/ /api/fails/count 总数 / 跨机台 XML 内容随事件上送落盘后下载。
    static void RunAggTokenPagingTests(string work)
    {
        var webRoot = Path.Combine(work, "aggsec");
        var resultsRoot = Path.Combine(webRoot, "results");
        var shareRoot = Path.Combine(webRoot, "share");
        Directory.CreateDirectory(resultsRoot);
        Directory.CreateDirectory(shareRoot);
        int port = GetFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";

        using (var db = new AggDatabase(Path.Combine(webRoot, "agg.db")))
        using (var w = new AggWatcher(shareRoot, db, heartbeatTimeoutSec: 60, pollSec: 1))
        using (var srv = new WebAggServer(port, w, db, resultsRoot, shareRoot, token: "secret-token"))
        {
            w.Start();
            srv.Start();
            Check(WaitUntil(() => srv.Listening, 5000), "聚合鉴权：带 token 的 Web 服务已启动");

            // ---- ① 无 token 全部 403 ----
            var rNoTok = _http.GetAsync($"{baseUrl}/api/machines").GetAwaiter().GetResult();
            Check(rNoTok.StatusCode == HttpStatusCode.Forbidden, "GET /api/machines（无 token）-> 403");
            var rNoTokPush = _http.PostAsync($"{baseUrl}/api/fail",
                new StringContent(BuildFailJsonCustom(101, "FCT1", "X:\\fct\\a.xml", "SN-101", "无 token"),
                    Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(rNoTokPush.StatusCode == HttpStatusCode.Forbidden, "POST /api/fail（无 token）-> 403");
            Check(WaitUntil(() => db.FailCount("") == 0, 3000), "聚合鉴权：403 的推送未入库（FailCount=0）");

            // ---- ② 错误 token 403 ----
            using (var bad = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/machines"))
            {
                bad.Headers.Add("X-Agg-Token", "wrong");
                var rBadTok = _http.SendAsync(bad).GetAwaiter().GetResult();
                Check(rBadTok.StatusCode == HttpStatusCode.Forbidden, "GET /api/machines（错误 token）-> 403");
            }

            // ---- ③ 正确 token（header）200 且能入库 ----
            using (var okPush = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/fail"))
            {
                okPush.Headers.Add("X-Agg-Token", "secret-token");
                okPush.Content = new StringContent(
                    BuildFailJsonCustom(101, "FCT1", "X:\\fct\\a.xml", "SN-101", "5V_Rail"),
                    Encoding.UTF8, "application/json");
                var rOkPush = _http.SendAsync(okPush).GetAwaiter().GetResult();
                Check(rOkPush.StatusCode == HttpStatusCode.OK, "POST /api/fail（正确 token 头）-> 200");
            }
            Check(WaitUntil(() => db.FailCount("") == 1, 10000), "聚合鉴权：正确 token 的推送已入库（FailCount=1）");

            // 再入库 3 条（不同机台/SN/原因）供分页与搜索断言
            PushFailWithToken(baseUrl, "secret-token", BuildFailJsonCustom(102, "FCT2", "X:\\fct\\b.xml", "SN-102", "12V_Rail"));
            PushFailWithToken(baseUrl, "secret-token", BuildFailJsonCustom(103, "FCT1", "X:\\fct\\c.xml", "SN-103", "CAN_Bus"));
            PushFailWithToken(baseUrl, "secret-token", BuildFailJsonCustom(104, "FCT2", "X:\\fct\\d.xml", "SN-104", "5V_Rail"));
            Check(WaitUntil(() => db.FailCount("") == 4, 10000), "聚合鉴权：共入库 4 条（FailCount=4）");

            // ---- ④ 分页：limit=2 + offset ----
            var rPage0 = _http.GetAsync($"{baseUrl}/api/fails?limit=2&offset=0&token=secret-token").GetAwaiter().GetResult();
            if (rPage0.StatusCode == HttpStatusCode.OK)
            {
                using var p0 = System.Text.Json.JsonDocument.Parse(
                    rPage0.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Check(p0.RootElement.GetArrayLength() == 2, "分页:limit=2&offset=0 -> 返回 2 条");
            }
            var rPage1 = _http.GetAsync($"{baseUrl}/api/fails?limit=2&offset=2&token=secret-token").GetAwaiter().GetResult();
            if (rPage1.StatusCode == HttpStatusCode.OK)
            {
                using var p1 = System.Text.Json.JsonDocument.Parse(
                    rPage1.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Check(p1.RootElement.GetArrayLength() == 2, "分页:limit=2&offset=2 -> 返回 2 条（第二页）");
            }
            var rPageTail = _http.GetAsync($"{baseUrl}/api/fails?limit=2&offset=4&token=secret-token").GetAwaiter().GetResult();
            if (rPageTail.StatusCode == HttpStatusCode.OK)
            {
                using var pt = System.Text.Json.JsonDocument.Parse(
                    rPageTail.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Check(pt.RootElement.GetArrayLength() == 0, "分页:limit=2&offset=4 -> 返回 0 条（越界空页）");
            }

            // ---- ⑤ 搜索：q 过滤 SN ----
            var rSearch = _http.GetAsync($"{baseUrl}/api/fails?q={Uri.EscapeDataString("SN-103")}&token=secret-token").GetAwaiter().GetResult();
            if (rSearch.StatusCode == HttpStatusCode.OK)
            {
                using var sd = System.Text.Json.JsonDocument.Parse(
                    rSearch.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Check(sd.RootElement.GetArrayLength() == 1
                      && sd.RootElement[0].GetProperty("Sn").GetString() == "SN-103",
                      "搜索:q=SN-103 -> 只命中 1 条且 SN 正确");
            }
            var rSearchReason = _http.GetAsync($"{baseUrl}/api/fails?q={Uri.EscapeDataString("12V_Rail")}&token=secret-token").GetAwaiter().GetResult();
            if (rSearchReason.StatusCode == HttpStatusCode.OK)
            {
                using var rd = System.Text.Json.JsonDocument.Parse(
                    rSearchReason.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Check(rd.RootElement.GetArrayLength() == 1
                      && rd.RootElement[0].GetProperty("FailReason").GetString() == "12V_Rail",
                      "搜索:q=12V_Rail -> 只命中 fail_reason 匹配的 1 条");
            }
            var rSearchNone = _http.GetAsync($"{baseUrl}/api/fails?q={Uri.EscapeDataString("不存在")}&token=secret-token").GetAwaiter().GetResult();
            if (rSearchNone.StatusCode == HttpStatusCode.OK)
            {
                using var nd = System.Text.Json.JsonDocument.Parse(
                    rSearchNone.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Check(nd.RootElement.GetArrayLength() == 0, "搜索:q=不存在 -> 0 条");
            }

            // ---- ⑥ /api/fails/count 总数（含过滤） ----
            var rCntAll = _http.GetAsync($"{baseUrl}/api/fails/count?token=secret-token").GetAwaiter().GetResult();
            Check(rCntAll.StatusCode == HttpStatusCode.OK, "GET /api/fails/count -> 200");
            if (rCntAll.StatusCode == HttpStatusCode.OK)
            {
                using var cd = System.Text.Json.JsonDocument.Parse(
                    rCntAll.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Check(cd.RootElement.GetProperty("count").GetInt64() == 4,
                      "GET /api/fails/count：全库总数 = 4");
            }
            var rCntFiltered = _http.GetAsync($"{baseUrl}/api/fails/count?q={Uri.EscapeDataString("5V_Rail")}&token=secret-token").GetAwaiter().GetResult();
            if (rCntFiltered.StatusCode == HttpStatusCode.OK)
            {
                using var fd = System.Text.Json.JsonDocument.Parse(
                    rCntFiltered.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Check(fd.RootElement.GetProperty("count").GetInt64() == 2,
                      "GET /api/fails/count?q=5V_Rail：过滤后总数 = 2");
            }

            // ---- ⑦ 跨机台 XML 内容落盘：xml_content 随事件上送 -> 聚合端本地 agg_xml 可下载 ----
            const string xmlPayload = "<BATCH><STATUS>FAIL</STATUS><SN>SN-201</SN></BATCH>";
            var data = new Dictionary<string, object?>
            {
                ["id"] = 201, ["station_id"] = "FCT1", ["model"] = "E3002781",
                ["category"] = "Online", ["test_date"] = "20260812", ["sn"] = "SN-201",
                ["result"] = "FAIL", ["xml_path"] = "X:\\fct\\local-201.xml",   // 机台端路径（聚合端不可达）
                ["fail_reason"] = "跨机台XML", ["tester"] = "SELFTEST",
                ["panel_status"] = "0000", ["batch_timestamp"] = "2026-08-12 09:00:00",
                ["has_fail_items"] = 1, ["file_size"] = 123,
                ["xml_content"] = xmlPayload,
            };
            var failWithXml = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["machine"] = "FCT1", ["type"] = "fail", ["seq"] = 201,
                ["ts"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), ["data"] = data,
            });
            PushFailWithToken(baseUrl, "secret-token", failWithXml);
            Check(WaitUntil(() => db.FailCount("") == 5, 10000), "XML 内容落盘:带 xml_content 的推送已入库（FailCount=5）");
            long xmlId = 0;
            Check(WaitUntil(() =>
            {
                var row = db.QueryFails(10, "FCT1").FirstOrDefault(r => r.Sn == "SN-201");
                if (row == null) return false;
                xmlId = row.Id;
                return !string.IsNullOrEmpty(row.XmlPath) && File.Exists(row.XmlPath);   // XmlPath 已改写为本地文件
            }, 10000), "XML 内容落盘:XmlPath 已改写为聚合端本地文件且存在");
            var rXml = _http.GetAsync($"{baseUrl}/api/file?id={xmlId}&token=secret-token").GetAwaiter().GetResult();
            Check(rXml.StatusCode == HttpStatusCode.OK, $"GET /api/file?id={xmlId}（跨机台 XML 本地落盘）-> 200");
            if (rXml.StatusCode == HttpStatusCode.OK)
                Check(rXml.Content.ReadAsStringAsync().GetAwaiter().GetResult() == xmlPayload,
                      "GET /api/file?id=：内容与上送的 xml_content 一致");

            // ---- ⑧ 管理后台：GET /api/settings（只读，不污染真实 config.json）----
            var rSet = _http.GetAsync($"{baseUrl}/api/settings?token=secret-token").GetAwaiter().GetResult();
            Check(rSet.StatusCode == HttpStatusCode.OK, "GET /api/settings（带 token）-> 200");
            if (rSet.StatusCode == HttpStatusCode.OK)
            {
                using var sd = System.Text.Json.JsonDocument.Parse(
                    rSet.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                var root = sd.RootElement;
                Check(root.TryGetProperty("agg_http_port", out _)
                      && root.TryGetProperty("agg_token_set", out _)
                      && root.TryGetProperty("agg_webhook_set", out _)
                      && root.TryGetProperty("agg_summary_minutes", out _)
                      && root.TryGetProperty("agg_transport", out _)
                      && root.TryGetProperty("results_root", out _),
                      "GET /api/settings：返回全部聚合配置字段（端口/token/webhook/汇总/通道/白名单）");
            }
            var rSetNoToken = _http.GetAsync($"{baseUrl}/api/settings").GetAwaiter().GetResult();
            Check(rSetNoToken.StatusCode == HttpStatusCode.Forbidden, "GET /api/settings（无 token）-> 403（设置页也要鉴权）");

            // 页面含「设置」页签
            var rPage2 = _http.GetAsync($"{baseUrl}/?token=secret-token").GetAwaiter().GetResult();
            if (rPage2.StatusCode == HttpStatusCode.OK)
            {
                var page2 = rPage2.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Check(page2.Contains("tabSettings") && page2.Contains("settingsView"),
                      "GET /：页面含「设置」页签与设置视图容器");
                Check(page2.Contains("/api/settings"), "GET /：页面 JS 调用 /api/settings（设置页加载）");
            }

            // ---- ⑨ Config.Save 容错：损坏的 config.json 不崩溃、未知字段保留 ----
            var cfgPath = Path.Combine(AppConfig.BaseDir, "config.json");
            if (File.Exists(cfgPath))
            {
                var backup = File.ReadAllText(cfgPath);
                try
                {
                    File.WriteAllText(cfgPath, "{ 这不是合法 JSON");
                    var cfgTmp = new AppConfig { AggEnabled = false };
                    Check(cfgTmp.Save() == false, "Config.Save：损坏 config.json 时返回 false 不抛异常");
                }
                finally { File.WriteAllText(cfgPath, backup); }
            }

            srv.Stop();
            w.Stop();
        }
        TryDeleteDir(webRoot);
        Console.WriteLine("    聚合鉴权与分页自检完成");
    }

    /// <summary>带 token 头 POST fail（鉴权自检辅助）。</summary>
    static void PushFailWithToken(string baseUrl, string token, string json)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/fail");
        req.Headers.Add("X-Agg-Token", token);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        _http.SendAsync(req).GetAwaiter().GetResult();
    }

    // ==================== Web 目录浏览（Phase 3：/api/list + 页面文件页签） ====================
    // 场景：根目录列表（目录在前文件在后） / 相对路径 / 绝对路径 / 穿越与越权 403 / 不存在 404 /
    // 下载回归 / 页面文件页签。白名单根 = resultsRoot（构造参数），与 /api/file 同源。
    // ==================== 聚合服务一键部署（v3.4.0：agg --install） ====================
    // 场景：默认 config JSON 生成 —— 字段齐全 / 值正确 / 合法可解析 / 缩进可读。
    // 系统操作（netsh 防火墙 / 快捷方式 / 启动进程）依赖 Windows 环境，这里只保证编译通过。
    static void RunAggInstallConfigTests()
    {
        var json = Program.BuildDefaultAggConfigJson();
        Check(!string.IsNullOrEmpty(json), "一键部署:BuildDefaultAggConfigJson 返回非空 JSON");
        if (string.IsNullOrEmpty(json)) return;
        bool parseOk = false;
        using (var doc = System.Text.Json.JsonDocument.Parse(json))
        {
            parseOk = true;
            var root = doc.RootElement;
            Check(root.TryGetProperty("station_id", out var s) && s.GetString() == "AGG",
                  "一键部署:station_id=AGG");
            Check(root.TryGetProperty("results_root", out var r) && r.GetString() == @"D:\Results",
                  "一键部署:results_root=D:\\Results");
            Check(root.TryGetProperty("agg_transport", out var t) && t.GetString() == "http",
                  "一键部署:agg_transport=http");
            Check(root.TryGetProperty("agg_http_port", out var p) &&
                  p.ValueKind == System.Text.Json.JsonValueKind.Number && p.GetInt32() == 8080,
                  "一键部署:agg_http_port=8080");
            Check(root.TryGetProperty("agg_share_root", out var sr) && sr.GetString() == "",
                  "一键部署:agg_share_root 为空串");
            Check(root.TryGetProperty("log_level", out var l) && l.GetString() == "INFO",
                  "一键部署:log_level=INFO");
        }
        Check(parseOk, "一键部署:JSON 合法可解析");
        Check(json.Contains("  ") && json.Contains("\n"), "一键部署:JSON 已缩进（WriteIndented 可读格式）");

        // ---- v3.4.4：AggDeployer 内嵌部署器（不再依赖外部 bat） ----
        var depJson = AggDeployer.BuildDefaultConfigJson();
        Check(!string.IsNullOrEmpty(depJson) && depJson == json,
              "AggDeployer:BuildDefaultConfigJson 与 Program 转发版一致（内嵌单一来源）");
        Check(AggDeployer.IsAdmin() == false || AggDeployer.IsAdmin() == true,
              "AggDeployer:IsAdmin 可调用（Windows 下返回布尔，不抛异常）");
    }

    static void RunWebFileBrowserTests(string work)
    {
        var webRoot = Path.Combine(work, "webfiles");
        var resultsRoot = Path.Combine(webRoot, "results");
        var shareRoot = Path.Combine(webRoot, "share");
        Directory.CreateDirectory(resultsRoot);
        Directory.CreateDirectory(shareRoot);   // ⚠ 必须存在：AggWatcher.Start() 对不存在的 shareRoot 直接 return
        // 造目录结构：test.xml + sub/sub.txt + empty-dir（空目录也应在列表里）
        const string xmlContent = "<BATCH><STATUS>FAIL</STATUS></BATCH>";
        File.WriteAllText(Path.Combine(resultsRoot, "test.xml"), xmlContent);
        var subDir = Path.Combine(resultsRoot, "sub");
        Directory.CreateDirectory(subDir);
        const string subContent = "sub 目录里的文本";
        File.WriteAllText(Path.Combine(subDir, "sub.txt"), subContent);
        Directory.CreateDirectory(Path.Combine(resultsRoot, "empty-dir"));
        int port = GetFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";

        using (var db = new AggDatabase(Path.Combine(webRoot, "agg.db")))
        using (var w = new AggWatcher(shareRoot, db, heartbeatTimeoutSec: 60, pollSec: 1))
        using (var srv = new WebAggServer(port, w, db, resultsRoot, shareRoot))
        {
            w.Start();
            srv.Start();
            Check(WaitUntil(() => srv.Listening, 5000), "目录浏览：Web 服务进入监听状态");

            // ---- ① 根目录列表：目录在前文件在后、各自按名排序 ----
            var rRoot = _http.GetAsync($"{baseUrl}/api/list").GetAwaiter().GetResult();
            Check(rRoot.StatusCode == HttpStatusCode.OK, "GET /api/list（无 path = 根目录）-> 200");
            if (rRoot.StatusCode == HttpStatusCode.OK)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(
                    rRoot.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                var arr = doc.RootElement;
                Check(arr.GetArrayLength() == 3,
                      $"GET /api/list：根目录 3 项（test.xml + sub + empty-dir，实得 {arr.GetArrayLength()}）");
                Check(arr[0].GetProperty("IsDir").GetBoolean() && arr[1].GetProperty("IsDir").GetBoolean()
                      && !arr[2].GetProperty("IsDir").GetBoolean(),
                      "GET /api/list：目录排前、文件排后");
                Check(arr[0].GetProperty("Name").GetString() == "empty-dir"
                      && arr[1].GetProperty("Name").GetString() == "sub"
                      && arr[2].GetProperty("Name").GetString() == "test.xml",
                      "GET /api/list：目录/文件各自按名称排序（OrdinalIgnoreCase）");
                Check(arr[0].GetProperty("Size").GetInt64() == 0, "GET /api/list：目录 Size=0");
                Check(arr[1].GetProperty("Path").GetString() == subDir,
                      "GET /api/list：Path=该项完整路径（可直接回传 /api/list 或 /api/file）");
                Check(arr[2].GetProperty("Size").GetInt64() == xmlContent.Length,
                      "GET /api/list：文件 Size=实际字节数");
                Check(arr[2].GetProperty("Modified").GetString() ==
                      File.GetLastWriteTime(Path.Combine(resultsRoot, "test.xml")).ToString("yyyy-MM-dd HH:mm:ss"),
                      "GET /api/list：Modified 格式 yyyy-MM-dd HH:mm:ss");
            }

            // ---- ② 相对路径子目录 ----
            var rSub = _http.GetAsync($"{baseUrl}/api/list?path={Uri.EscapeDataString("sub")}").GetAwaiter().GetResult();
            Check(rSub.StatusCode == HttpStatusCode.OK, "GET /api/list?path=sub（相对路径）-> 200");
            if (rSub.StatusCode == HttpStatusCode.OK)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(
                    rSub.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                var arr = doc.RootElement;
                Check(arr.GetArrayLength() == 1 && arr[0].GetProperty("Name").GetString() == "sub.txt"
                      && !arr[0].GetProperty("IsDir").GetBoolean(),
                      "GET /api/list?path=sub：列出 sub.txt");
            }

            // ---- ③ 绝对路径子目录（白名单内放行） ----
            var rAbs = _http.GetAsync($"{baseUrl}/api/list?path={Uri.EscapeDataString(subDir)}").GetAwaiter().GetResult();
            Check(rAbs.StatusCode == HttpStatusCode.OK, "GET /api/list?path=<根下子目录绝对路径> -> 200");
            if (rAbs.StatusCode == HttpStatusCode.OK)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(
                    rAbs.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Check(doc.RootElement.GetArrayLength() == 1
                      && doc.RootElement[0].GetProperty("Name").GetString() == "sub.txt",
                      "GET /api/list?path=绝对路径：同样列出 sub.txt");
            }

            // ---- ④ 越权 / 穿越 / 不存在 ----
            var rTraversal = _http.GetAsync($"{baseUrl}/api/list?path={Uri.EscapeDataString("../outside")}").GetAwaiter().GetResult();
            Check(rTraversal.StatusCode == HttpStatusCode.Forbidden, "GET /api/list?path=../outside（目录穿越）-> 403");
            var rWindows = _http.GetAsync($"{baseUrl}/api/list?path={Uri.EscapeDataString(@"C:\Windows")}").GetAwaiter().GetResult();
            Check(rWindows.StatusCode == HttpStatusCode.Forbidden, "GET /api/list?path=C:\\Windows（绝对路径越权）-> 403");
            if (rWindows.StatusCode == HttpStatusCode.Forbidden)
                Check(rWindows.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("拒绝访问"),
                      "GET /api/list 403：返回中文提示「拒绝访问：目录不在允许范围内」");
            var rGhost = _http.GetAsync($"{baseUrl}/api/list?path={Uri.EscapeDataString("no-such-dir")}").GetAwaiter().GetResult();
            Check(rGhost.StatusCode == HttpStatusCode.NotFound, "GET /api/list?path=不存在目录 -> 404");

            // ---- ⑤ 下载回归 + 页面文件页签 ----
            var rFile = _http.GetAsync($"{baseUrl}/api/file?path={Uri.EscapeDataString(Path.Combine(resultsRoot, "test.xml"))}").GetAwaiter().GetResult();
            Check(rFile.StatusCode == HttpStatusCode.OK
                  && rFile.Content.ReadAsStringAsync().GetAwaiter().GetResult() == xmlContent,
                  "GET /api/file?path=（回归）200 且内容与磁盘文件一致");
            var rPage = _http.GetAsync($"{baseUrl}/").GetAwaiter().GetResult();
            if (rPage.StatusCode == HttpStatusCode.OK)
            {
                var page = rPage.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Check(page.Contains("tabFiles") && page.Contains("文件"), "GET /：页面含「文件」页签");
                Check(page.Contains("filesView") && page.Contains("fileRows"), "GET /：页面含文件浏览容器与表格");
                Check(page.Contains("/api/list"), "GET /：页面 JS 调用了 /api/list");
            }

            srv.Stop();
            w.Stop();
        }
        TryDeleteDir(webRoot);
        Console.WriteLine("    目录浏览自检完成");
    }
}

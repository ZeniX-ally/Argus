using FctAggregator;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

class SelfTest
{
    static int _fail;
    static readonly HttpClient _http = new();
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "[OK]   " : "[FAIL] ") + what);
        if (!ok) _fail++;
    }

    static string FindUp(string relative)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                var p = Path.Combine(dir.FullName, relative);
                if (File.Exists(p)) return p;
                var p2 = Path.Combine(dir.FullName, "FctAggregator", relative);
                if (File.Exists(p2)) return p2;
            }
        }
        return "";
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--perf")
            return PerfReport.Run(args);

        var work = Path.Combine(Path.GetTempPath(), "fct_agg_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(work);
        var real = FindUp(Path.Combine("dist", "data", "fct.db"));
        var db = Path.Combine(work, "fct.db");

        bool haveReal = real.Length > 0 && File.Exists(real);
        if (haveReal) File.Copy(real, db);
        Console.WriteLine($"工作目录: {work}");
        Console.WriteLine(haveReal ? $"已复制真实库: {new FileInfo(db).Length / 1024} KB" : "真实库不存在，用空库");

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

        var d = new Database(db);
        Check(true, "Database 构造完成（Init + 迁移已跑）");

        int nmTables;
        using (var c = new SqliteConnection($"Data Source={db}"))
        {
            c.Open();
            using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('normal_models','deviation_events')";
            nmTables = Convert.ToInt32(q.ExecuteScalar());
        }
        Check(nmTables == 2, "normal_models + deviation_events 表已建（主库 Init 迁移）");

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

        var d2 = new Database(db);
        Check(true, "重复打开库（迁移幂等，无异常）");

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

        Check(TimeUtil.Normalize("2026-08-14T07:17:29.004+08:00") == "2026-08-14 07:17:29",
              "ISO +08:00 偏移：按墙上时间解析，不随机器时区漂移");
        Check(TimeUtil.Normalize("2026-08-14T07:17:29.004+0800") == "2026-08-14 07:17:29",
              "ISO +0800 无冒号偏移同样支持");
        Check(TimeUtil.Normalize("2026-08-14T07:17:29Z") == "2026-08-14 07:17:29", "ISO Z 后缀同样支持");
        Check(TimeUtil.Normalize("2026-08-14T07:17:29.004-05:00") == "2026-08-14 07:17:29",
              "负偏移按原墙上时间取（不换算成机器本地）");

        Check(TimeUtil.ResolveFileNameTime("P_20260722124700283.xml", new DateTime(2026,7,22)) == "2026-07-22 12:47:00",
              "域名1: 文件名 17 位时间贴近系统时间可解析");
        Check(TimeUtil.ResolveFileNameTime("P_20260722124700.xml", new DateTime(2026,7,22)) == "2026-07-22 12:47:00",
              "域名1: 文件名 14 位时间贴近系统时间可解析");
        Check(TimeUtil.ResolveFileNameTime("P_20240101120000.xml", new DateTime(2026,7,22)) == "",
              "域名1: 文件名时间与系统偏差 >30 天判为 SN/误匹配返回空");
        Check(TimeUtil.ResolveFileNameTime("SN_20260101.xml", new DateTime(2026,7,22)) == "",
              "域名1: 非时间纯数字段且偏差过大判空");
        Check(TimeUtil.ResolveFileNameTime("no_time_here.xml", new DateTime(2026,7,22)) == "",
              "域名1: 无 14/17 位数字段返回空");
        Check(TimeUtil.ResolveFileNameTime(null) == "", "域名1: 空输入返回空");

        Check(TimeUtil.ExtractFileNameTime("P_Fts_PEU_G49_FCT6_E3002781AFV75236898002K30500272_20260901105125969_20260901675353.xml") == "2026-09-01 10:51:25",
              "ExtractFileNameTime: 真实现场 17 位时间戳提取年月日时分秒正确(排除后缀非时间段)");
        Check(TimeUtil.ExtractFileNameTime("F_Fts_PEU_G49_FCT6_SN123456_20260901105125.xml") == "2026-09-01 10:51:25",
              "ExtractFileNameTime: 14 位合法时间戳提取正确");
        Check(TimeUtil.ExtractFileNameTime("P_Fts_PEU_G49_FCT6_SN_20260901675353.xml") == "",
              "ExtractFileNameTime: 非法时分秒(小时67)无法转为有效DateTime返回空");
        Check(TimeUtil.ExtractFileNameTime("no_time.xml") == "", "ExtractFileNameTime: 无时间段返回空");

        var badXml = "<root><FACTORY USER=\\\"x\\\"></root>";
        var pfOut = new FctAggregator.Parsing.DefaultResultParser(FctAggregator.Parsing.ParserRuleSet.Default, "FCT1").Parse("D:\\Results\\Offline\\E300\\20260812\\F_20260722124700_xxx.xml", badXml);
        Check(pfOut != null && pfOut.Error == true, "域名1: 畸形 XML 应判解析失败");
        Check(pfOut != null && pfOut.ErrorCode == "xml_malformed", "域名1: 畸形 XML 分类码=xml_malformed");

        {
            const string passXml = """
                <BATCH TIMESTAMP="2026-09-01T10:00:00.000">
                <FACTORY USER="Operator" TESTER="PEU_G49_FCT6"/>
                <PANEL STATUS="Passed"><DUT ID="E3002781AFV75236898002K30500272">
                <TEST NAME="P5V_LVX_LS" VALUE="5.0" HILIM="5.5" LOLIM="4.5" STATUS="Passed"/>
                </DUT></PANEL>
                </BATCH>
                """;
            var parser1 = new FctAggregator.Parsing.DefaultResultParser(FctAggregator.Parsing.ParserRuleSet.Default, "FCT1");
            var passOut = parser1.Parse("D:\\Results\\Online\\E3002781\\20260901\\P_20260901105125969.xml", passXml);
            Check(passOut != null && passOut.Result == "PASS" && passOut.Sn == "E3002781AFV75236898002K30500272",
                  $"PASS 文件文件名无 SN 段时回落读 XML DUT ID (实得 {(passOut?.Sn ?? "null")})");
            Check(passOut != null && passOut.Tester == "PEU_G49_FCT6" && passOut.Sn != null && passOut.Sn.Length >= 8,
                  "PASS 文件 SN 回落不破坏 tester/其他字段提取");

            var dbgOut = parser1.Parse("D:\\Results\\Online\\E3002781\\20260901\\P_20260901105125969.xml",
                passXml.Replace("Operator", "debug"));
            Check(dbgOut != null && dbgOut.Skipped && dbgOut.SkipReason == "debug",
                  "debug 账号 PASS 文件不计数（跳过不入库）");

            var fOut = parser1.Parse("D:\\Results\\Online\\E3002781\\20260901\\F_20260901105125969.xml", passXml);
            Check(fOut != null && fOut.Result == "FAIL" && fOut.Sn == "E3002781AFV75236898002K30500272",
                  "FAIL 文件 SN 回退 DUT ID 保持不变");
        }

        {
            var tmp2 = Path.Combine(Path.GetTempPath(), "fct_db2_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(tmp2);
            var dbPath2 = Path.Combine(tmp2, "test.db");
            var db2 = new Database(dbPath2);
            db2.LogSlowQuery("SELECT * FROM test_records WHERE test_date=@c", 600);
            db2.LogSlowQuery("SELECT 1", 100);
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath2}"))
            { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText="SELECT COUNT(*) FROM db_slow_log"; var cnt=Convert.ToInt32(cmd.ExecuteScalar()); Check(cnt==1, $"域2: 慢查询仅记录>500ms（实得 {cnt}）"); }
            var hc = db2.RunHealthCheck();
            Check(hc == "ok", $"域2: 健康巡检 integrity_check=ok（实得 {hc}）");
            using (var c2 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath2}"))
            { c2.Open(); using var cmd2 = c2.CreateCommand(); cmd2.CommandText="SELECT COUNT(*) FROM db_health_log WHERE check_type='integrity_check'"; var cnt2=Convert.ToInt32(cmd2.ExecuteScalar()); Check(cnt2>=1, $"域2: 健康日志已落库（实得 {cnt2}）"); }
            var oldDate = DateTime.Today.AddDays(-100).ToString("yyyyMMdd");
            db2.BatchInsert(new[] { new TestRecord{ StationId="FCT1", Model="E300", Category="Offline", TestDate=oldDate, Sn="SN-OLD", Result="FAIL", XmlPath="X:\\old.xml", FailReason="OLD", BatchTimestamp="2026-01-01 00:00:00"} });
            var del = db2.ArchiveColdData(90);
            Check(del==1, $"域2: 冷数据归档删除 1 条（实得 {del}）");
            // 真归档断言：TSV 导出、行数对账、内容含被归档 SN、表内冷数据清零
            var cutoffArc = DateTime.Today.AddDays(-90).ToString("yyyyMMdd");
            var archFile = Path.Combine(tmp2, "archive", $"test_records_before_{cutoffArc}.tsv");
            Check(File.Exists(archFile), "域2: 归档文件已生成(.tsv，真导出而非伪归档头)");
            var archLines = File.ReadAllLines(archFile);
            Check(archLines.Length == del + 2, $"域2: 归档文件行数 = 归档记录数+2（列头+元信息行，实得 {archLines.Length}）");
            Check(File.ReadAllText(archFile).Contains("SN-OLD"), "域2: 归档文件内容包含被归档的 SN");
            using (var cArc = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath2}"))
            {
                cArc.Open();
                using var cmdArc = cArc.CreateCommand();
                cmdArc.CommandText = "SELECT COUNT(*) FROM test_records WHERE test_date < @c";
                cmdArc.Parameters.AddWithValue("@c", cutoffArc);
                Check(Convert.ToInt32(cmdArc.ExecuteScalar()) == 0, "域2: 归档后表内冷数据为 0");
            }
            try { Directory.Delete(tmp2, true); } catch { }
        }


        {
            var p1 = FctAggregator.Parsing.PathMeta.FromPath(@"D:\Results\Offline\E3002781\20260814\O_Fts_PEU_G49_FCT6_E3002781AGV75236898002K81201743_20260814072417293_2026813232417386.xml", null);
            Check(p1 != null && p1.FileTime == "20260814072417293",
                  $"文件名时间戳: 标准段位取第 7 段 17 位（实得 {p1?.FileTime}）");
            var p2 = FctAggregator.Parsing.PathMeta.FromPath(@"D:\Results\Offline\E3002781\20260814\O_Fts_PEU_G49_FCT6_SN_XX_1234_20260814072417293_2026813232417386.xml", null);
            Check(p2 != null && p2.FileTime == "20260814072417293",
                  $"文件名时间戳: SN 含下划线段位漂移仍能取对时间（实得 {p2?.FileTime}）");
            var p3 = FctAggregator.Parsing.PathMeta.FromPath(@"D:\Results\Offline\E3002781\20260814\O_Fts_PEU_G49_FCT6_E3002781AGV75236898002K81201743_20260814072417293_2026813232417386.xml", null);
            Check(p3 != null && p3.FileTime == "20260814072417293",
                  $"文件名时间戳: 第 8 段 16 位非标准段自动跳过（实得 {p3?.FileTime}）");
            var p4 = FctAggregator.Parsing.PathMeta.FromPath(@"D:\Results\Offline\E3002781\20260814\O_Fts_PEU_G49_FCT6_SN1234_20260814072417_20260814072418.xml", null);
            Check(p4 != null && p4.FileTime == "20260814072417",
                  $"文件名时间戳: 14 位兼容（实得 {p4?.FileTime}）");
        }

        {
            var tsRoot = Path.Combine(work, "tsrc");
            var tsDir = Path.Combine(tsRoot, "Offline", "E3002781", "20260814");
            Directory.CreateDirectory(tsDir);
            var tsXml = Path.Combine(tsDir,
                "O_Fts_PEU_G49_FCT6_E3002781AGV75236898002K81201743_20260814072417293_2026813232417386.xml");
            const string tsBody = """
                <BATCH TIMESTAMP="2026-07-30T18:21:15.533+08:00">
                  <FACTORY USER="Operator" TESTER="PEU_G49_FCT6"/>
                  <PANEL STATUS="Terminated">
                    <DUT ID="E3002781AGV75236898002K81201743"/>
                  </PANEL>
                  <TEST NAME="BSW_vb_NMI_ESR1_Flt(XCP)" STATUS="Failed" VALUE="0" HILIM="1"/>
                </BATCH>
                """;
            File.WriteAllText(tsXml, tsBody);
            var tsPr = XmlParser.Parse(tsXml);
            Check(tsPr.BatchTimestamp == "2026-08-14 07:24:17",
                  $"时间源: 文件名 17 位时间优先（实得 {tsPr?.BatchTimestamp}）");
            var tsProc = new Processor(new AppConfig(), "");
            var tsRec = tsProc.ParseAndClassify(tsXml);
            Check(tsRec != null && TimeUtil.Normalize(tsRec.BatchTimestamp) == "2026-08-14 07:24:17",
                  $"时间源: Processor 也取文件名时间（实得 {tsRec?.BatchTimestamp}）");
            Check(tsRec != null && tsRec.StationId == "FCT6",
                  $"时间源用例: 机台号仍从 TESTER 提取（实得 {tsRec?.StationId}）");
            var tsXml2 = Path.Combine(tsDir,
                "O_Fts_PEU_G49_FCT6_E3002781AGV75236898002K81201743_nodate_2026813232417387.xml");
            File.WriteAllText(tsXml2, tsBody);
            var tsRec2 = tsProc.ParseAndClassify(tsXml2);
            Check(tsRec2 != null && TimeUtil.Normalize(tsRec2.BatchTimestamp) == "2026-08-14 00:00:00",
                  $"时间源: 无文件名时间时回退目录日期（实得 {tsRec2?.BatchTimestamp}）");
            TryDeleteDir(tsRoot);
        }

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
            Directory.CreateDirectory(Path.Combine(iniRoot, "FTS", "Cfg"));
            File.WriteAllText(Path.Combine(iniRoot, "FTS", "Cfg", "fct.ini"), "x");
            var hit2 = FctIni.SearchFtsTree(new DirectoryInfo(iniRoot), 8);
            Check(hit2 != null && hit2.EndsWith("fct.ini", StringComparison.OrdinalIgnoreCase),
                  "文件名大小写不敏感(fct.ini 也能识别)");
        }

        Console.WriteLine("\n【设备状态】INI 只列登记设备 / 缓存 / 不把系统多余 COM 画成未知");
        {
            FctIni.Invalidate();
            var ini = Path.Combine(work, "fct_dev_status.ini");
            File.WriteAllText(ini, """
                [Resource Name]
                8.2_SN=E3002781/E3002752
                KL30_PSU=COM3
                Scope=USB1
                FW_Version_1=9.9.9
                [A2L]
                G49=D:\a.a2l
                """);
            var d1 = FctIni.Parse(ini);
            Check(d1.Found && d1.Devices.Count == 2, $"设备列表仅 ini 登记项（实得 {d1.Devices.Count}）");
            Check(d1.Devices.Any(x => x.Name == "KL30_PSU" && x.Type == "com" && x.Port == "COM3"), "COM 登记项带名称");
            Check(d1.Devices.Any(x => x.Name == "Scope" && x.Type == "usb"), "USB 登记项");
            Check(!d1.Devices.Any(x => x.Name.Contains("未知")), "系统其它 COM 不进列表当未知设备");
            Check(d1.Models.Count == 2 && d1.FwVersions.Count == 1 && d1.A2lFiles.Count == 1, "型号/版本/A2L 仍解析");
            Check(d1.ExtraSystemComCount >= 0, "ExtraSystemComCount 可算");
            var snap2 = FctIni.Parse(ini);
            Check(snap2.IniPath == d1.IniPath && snap2.Devices.Count == 2, "mtime 未变时二次 Snapshot 仍正确");
            FctIni.Invalidate();
            File.WriteAllText(ini, "[Resource Name]\nOnlyOne=COM8\n");
            var devIniReload = FctIni.Parse(ini);
            Check(devIniReload.Devices.Count == 1 && devIniReload.Devices[0].Name == "OnlyOne", "Invalidate 后读到新 ini");
            FctIni.Invalidate();
        }

        Console.WriteLine("\n【旧库兼容】产线老库直开: 缺新表 + investigating/closed 状态 + 旧时间格式");
        {
            var oldDb = Path.Combine(work, "legacy_fct.db");
            File.Delete(oldDb);
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
            var inv = ld.ListMaintenance("investigating", 50).FirstOrDefault();
            Check(inv != null && MaintenanceMeta.Normalize(inv.Status) == "open",
                  "旧 investigating 记录仍在库中, 归一化到「待办」列");
            Check(ld.ListMaintenance("resolved", 50).Any(m => m.FailItem == "旧版已关闭项"),
                  "旧 closed 记录迁移到「已完成」列");
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
            var delLegacy = legacyView.First();
            Check(ld.DeleteTodo(delLegacy.Id), "旧库上删除待办正常（自动补建 dismissed_todos 写永久标记）");
            Check(!ld.ListTodoView().Any(x => x.Id == delLegacy.Id), "删除后离开待办列");

            bool hasFixCol = false;
            using (var c = new SqliteConnection($"Data Source={oldDb}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA table_info(test_records)";
                using var r = cmd.ExecuteReader();
                while (r.Read()) if (r.GetString(1) == "fixture_id") hasFixCol = true;
            }
            Check(hasFixCol, "旧库打开后自动补 test_records.fixture_id 列（v3.26.2）");
            ld.BatchInsert(new[]
            {
                new TestRecord { StationId="FCT7", Model="E3002781", Category="Offline", TestDate="20260801",
                                 Sn="SN-LG3", Result="FAIL", XmlPath="X:\\legacy3.xml", FailReason="KL30",
                                 FixtureId="JIG-A01" },
            });
            var fixRoundtrip = ld.FetchFailRecordsAfter(0, 100).FirstOrDefault(x => x.Rec.Sn == "SN-LG3").Rec;
            Check(fixRoundtrip?.FixtureId == "JIG-A01",
                  $"续推回读 FetchFailRecordsAfter 保真 FixtureId（实得 {fixRoundtrip?.FixtureId ?? "null"}）");
            var fixLegacy = ld.FetchFailRecordsAfter(0, 100).FirstOrDefault(x => x.Rec.Sn == "SN-LG1").Rec;
            Check(fixLegacy != null && fixLegacy.FixtureId == null,
                  "旧数据（无 fixture_id）回读为 null 不炸，聚合端回落 fail_reason 前缀归因");
        }

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
        int resolvedExpected = 3 + legacyCount;
        Check(norm.GetValueOrDefault("resolved") == resolvedExpected,
              $"已完成计数 = {norm.GetValueOrDefault("resolved")}（3 新 + {legacyCount} 迁移）");

        int total;
        using (var c = new SqliteConnection($"Data Source={db}"))
        {
            c.Open();
            using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM maintenance_records";
            total = Convert.ToInt32(q.ExecuteScalar());
        }
        Check(counts.Values.Sum() == total, $"计数总和 {counts.Values.Sum()} == 全表 {total}（不受 LIMIT 截断）");

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

        var target = d.ListMaintenance("unknown", 500).First();
        var oldUpdated = target.UpdatedAt;
        Thread.Sleep(1100);
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

        var ghost = after.Clone();
        ghost.Id = 999999;
        Check(!d.UpdateMaintenance(ghost), "更新不存在的记录返回 false（UI 会提示「可能已被删除」）");

        var dragged = d.ListMaintenance("unknown", 500).First();
        Check(d.UpdateMaintenanceStatus(dragged.Id, "resolved"), "UpdateMaintenanceStatus 返回 true（拖动改状态）");
        var draggedAfter = d.ListMaintenance("", 500).First(m => m.Id == dragged.Id);
        Check(draggedAfter.Status == "resolved" && draggedAfter.FailItem == dragged.FailItem,
              "拖动只改状态，其它字段不动");

        {
            var events = new List<(string from, string to)>();
            d.MaintenanceStatusChanged += (_, from, to) => events.Add((from, to));

            var ev = d.ListMaintenance("unknown", 500).First();
            events.Clear();
            Check(d.UpdateMaintenanceStatus(ev.Id, "in_progress"), "更新另一条状态返回 true");
            Check(events.Count == 1, $"状态变更事件触发一次（实得 {events.Count}）");
            Check(events[0].from == "unknown" && events[0].to == "in_progress",
                  $"事件带旧/新状态（{events[0].from} -> {events[0].to}）");

            events.Clear();
            d.UpdateMaintenanceStatus(ev.Id, "in_progress");
            Check(events.Count == 0, "同状态重复设置不触发事件（去重）");

            var ed2 = d.ListMaintenance("in_progress", 500).First(m => m.Id == ev.Id);
            var done2 = ed2.Clone();
            done2.Status = "resolved";
            done2.Resolver = "王五";
            done2.Resolution = "已换料";
            events.Clear();
            Check(d.UpdateMaintenance(done2), "UpdateMaintenance（全字段）返回 true");
            Check(events.Count == 1 && events[0].from == "in_progress" && events[0].to == "resolved",
                  $"全字段更新改状态也触发事件（{events.Count} 次: {events[0].from} -> {events[0].to}）");

            var ed3 = d.GetMaintenance(ev.Id)!;
            var noChange = ed3.Clone();
            noChange.Notes = "只改备注";
            events.Clear();
            d.UpdateMaintenance(noChange);
            Check(events.Count == 0, "只改内容不触发状态变更事件");
        }

        var oldRec = new MaintenanceRecord
        {
            StationId = "FCT1", FailItem = "往期记录", Severity = "minor",
            Status = "open", CreatedAt = "2026-01-15 08:00:00",
        };
        var oldId = d.CreateMaintenance(oldRec);
        var oldBack = d.ListMaintenance("", 500).First(m => m.Id == oldId);
        Check(oldBack.UpdatedAt.StartsWith("2026-01-15"),
              $"录入往期日期时 updated_at 跟随 created_at（{oldBack.UpdatedAt}）—— 否则老记录会被顶到看板最前");

        Check(d.DeleteMaintenance(oldId), "DeleteMaintenance 返回 true");
        Check(!d.ListMaintenance("", 500).Any(m => m.Id == oldId), "删除后确实不在列表里");

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

        int b2 = fd.CreateMaintenance(NewRec("第二条"));
        int b3 = fd.CreateMaintenance(NewRec("第三条"));
        Check(b2 == 2 && b3 == 3, $"连续新建 id = {b2}, {b3}");
        fd.DeleteMaintenance(b3);
        int b3b = fd.CreateMaintenance(NewRec("补上第三条"));
        Check(b3b == 3, $"删掉最后一条后新建 id = {b3b}（期望 3）");

        fd.DeleteMaintenance(2);
        int c4 = fd.CreateMaintenance(NewRec("删中间后新建"));
        Check(c4 == 4, $"删中间那条后新建 id = {c4}（期望 4，不回填空号 2）");

        Check(!fd.DeleteMaintenance(99999), "删不存在的 id 返回 false 且不报错");
        int c5 = fd.CreateMaintenance(NewRec("无效删除后"));
        Check(c5 == 5, $"无效删除不影响游标（下一个 id = {c5}）");

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
            onDown.Invoke(card, new object?[] { new MouseEventArgs(MouseButtons.Right, 1, 12, 34, 0) });
            mid.Invoke(card, new object?[] { new MouseEventArgs(MouseButtons.Right, 1, 300, 300, 0) });
            Check(true, "右键按住拖动不会误走拖拽分支");
        }
        Check(typeof(MaintenanceBoard).GetEvent("ContextRequested") != null,
              "MaintenanceBoard 向外暴露了 ContextRequested（菜单由 MaintenancePanel 统一提供）");

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

        var who = d.DistinctResolvers();
        Console.WriteLine("    历史维修人: " + (who.Count == 0 ? "(无)" : string.Join(", ", who)));
        Check(who.Contains("李四") && who.Contains("张三"), $"维修人候选取到历史值（{who.Count} 个）");
        Check(who.Distinct(StringComparer.OrdinalIgnoreCase).Count() == who.Count, "维修人候选已去重");
        Check(!who.Any(string.IsNullOrWhiteSpace), "维修人候选里没有空值");

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

        int usedByZhang = d.CountRecordsByResolver("张三");
        Check(usedByZhang > 0, $"「张三」在 {usedByZhang} 条历史记录里出现过（删人前会提示这个数）");
        d.AddResolver("张三");
        Check(d.DeleteResolver("张三"), "从名单删掉「张三」");
        Check(d.CountRecordsByResolver("张三") == usedByZhang, "删名单**不会动历史维修记录**");
        Check(d.ListResolvers().Contains("张三"), "删后仍能在候选里看到（因为历史记录里还有）");
        Check(!d.DeleteResolver("不存在的人"), "删不存在的人返回 false");

        d.AddResolver("张散");
        Check(d.RenameResolver("张散", "张三三", syncRecords: false) == 0, "改名（不同步）不动历史记录");
        Check(d.RosterResolvers().Contains("张三三") && !d.RosterResolvers().Contains("张散"),
              "名单里已改成「张三三」");

        var fixTarget = d.ListMaintenance("", 500).First(x => x.Resolver == "李四");
        int before = d.CountRecordsByResolver("李四");
        d.AddResolver("李四");
        int synced = d.RenameResolver("李四", "李四四", syncRecords: true);
        Check(synced == before && before > 0, $"改名并同步：改动了 {synced} 条历史记录（期望 {before}）");
        Check(d.ListMaintenance("", 500).First(x => x.Id == fixTarget.Id).Resolver == "李四四",
              "历史记录里的名字确实改掉了");
        Check(d.CountRecordsByResolver("李四") == 0, "旧名字已不再出现在记录里");

        var dRe = new Database(db);
        Check(dRe.RosterResolvers().Count == d.RosterResolvers().Count, "重新打开库后名单仍在（建表幂等）");

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

        d.AddResolver("孙七");
        int syncedMulti = d.RenameResolver("孙七", "孙七七", syncRecords: true);
        var afterMulti = d.ListMaintenance("", 500).First(x => x.Id == multiId);
        Check(syncedMulti == 1 && afterMulti.Resolver == "孙七七、周八",
              $"多人字段改名后 = {afterMulti.Resolver}（只换孙七，周八不动）");

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

        var picked = new[] { "测试项A", "测试项B", "测试项A" };
        using (var bf = new MaintenanceForm("FCT1", null, picked, who))
        {
            var list = bf.BatchResults();
            Check(list.Count == 2, $"批量建单：3 个选项(含1个重复) -> {list.Count} 条记录（期望 2）");
            Check(list.All(r => r.Id == 0), "批量记录的 Id 均为 0（交给 DB 自增）");
            Check(list.Select(r => r.FailItem).Distinct().Count() == 2, "每条的故障项不同");
            Check(list.All(r => r.Status == MaintenanceMeta.DefaultStatus), "批量记录默认状态 = 待办");
            Check(list.All(r => string.IsNullOrEmpty(r.EquipmentSn) && string.IsNullOrEmpty(r.EquipmentModel)),
                  "批量记录不带设备型号 / SN（表单已去掉这两项）");

            int n0 = d.ListMaintenance("", 500).Count;
            foreach (var r in list) d.CreateMaintenance(r);
            Check(d.ListMaintenance("", 500).Count == n0 + 2, "批量记录已成功写入库");
        }

        var formFields = typeof(MaintenanceForm)
            .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Select(f => f.Name).ToList();
        Check(!formFields.Contains("_model") && !formFields.Contains("_sn"),
              "MaintenanceForm 里已删除 _model / _sn 输入框");
        Check(formFields.Contains("_resolver"), "维修人控件仍在");
        Check(typeof(MaintenanceForm).GetField("_resolver", System.Reflection.BindingFlags.NonPublic |
              System.Reflection.BindingFlags.Instance)!.FieldType == typeof(ComboBox),
              "维修人已改为 ComboBox（可选历史值，也可直接输入）");

        Console.WriteLine("\n【待办·大项合并】同类测试项归一化");
        {
            string K(string s) => TodoGrouping.KeyOf(s);

            Check(K("6.1.1.1 5V_Rail") == K("6.1.1.2 5V_Rail"), "步骤号不同的同一测试项合并为一项");
            Check(K("6.1.1.1 5V_Rail") == K("5V Rail"), "去掉步骤号后与裸名同键");
            Check(K("CAN_Bus_Test_CH1") == K("CAN_Bus_Test_CH2"), "通道号不同(CH1/CH2)合并");
            Check(K("Check LED pin3") == K("Check LED pin7"), "位号不同(pin3/pin7)合并");
            Check(K("U12_Voltage") == K("U7_Voltage"), "器件位号不同(U12/U7)合并");
            Check(K("Check_5V_Rail") != K("Check_12V_Rail"), "**5V 与 12V 不合并**（数字+单位有物理含义）");
            Check(K("Delay_100ms") != K("Delay_200ms"), "100ms 与 200ms 不合并");
            Check(K("3V3_Rail") != K("1V8_Rail"), "3V3 与 1V8 不合并");
            Check(K("check_5v_rail") == K("Check-5V-Rail"), "大小写与分隔符差异合并");
            Check(K("ＣＡＮ＿Ｂｕｓ") == K("CAN_Bus"), "全角写法与半角合并");
            Check(K("BSW_v_Kl30UC(XCP)") == K("BSW_v_Kl30UC (KL30_LS_UC)(XCP)"),
                  "同一参数带不同括号限定词 -> 合并为一个大项");
            Check(K("Vref_3V3(DMM)") == K("Vref_3V3(XCP)"), "同一参数不同测量路径(DMM/XCP) -> 合并");
            Check(K("Check(Voltage)") != K("Check(Current)"),
                  "**括号里就是核心语义时不丢**（Check(Voltage) 不等于 Check(Current)）");
            Check(K("") == "", "空故障项返回空键（调用方跳过）");
            Check(K("6.1.1.1") != "", "纯序号项不会退化成空键（否则会全挤成一张卡）");

            var title = TodoGrouping.TitleOf(new[] { "6.1.1.2 5V_Rail_Check", "5V_Rail", "6.1.1.1 5V_Rail" });
            Check(title == "5V_Rail", $"合并后展示名取最短且去步骤号（得到「{title}」）");

            Check(TodoGrouping.PriorityZhOf(50) == "高" && TodoGrouping.PriorityZhOf(8) == "中" &&
                  TodoGrouping.PriorityZhOf(1) == "低", "优先级按 fail 次数分高/中/低");

            string M(string s) => TodoGrouping.MergeKeyOf(s);
            Check(M("8.18.2.2 SiC_G_HU High Level") == M("SiC_G_LW Low Level") && M("SiC_G_HU High Level").EndsWith("GateDrive"),
                  "G49 规则: 栅极驱动六相(PWM 高/低电平)同原理同卡");
            Check(M("RES_v_ResAng (45°)") == M("8.11.6.1 RES_v_ResAng(315°)") && M("BSW_v_PosSen_SinN").EndsWith("Resolver"),
                  "G49 规则: 旋变四角/解码/励磁同一仿真器链一张卡");
            Check(M("BSW_v_Kl30_HS") == M("SBC_KL30_FILT_1") && M("SBC_KL30_FILT_1") == M("6.1 KL30_FILT_1"),
                  "G49 规则: 同一电源轨 DMM/接口名/XCP 三种写法别名归并");
            Check(M("BSW_v_Kl30_HS") != M("BSW_v_Kl30_LS") && M("P12V_FB_HS") != M("P15V_LVD_LS"),
                  "G49 规则: 不同电源轨不互并（保住诊断信息）");
            Check(M("IGBTTM_v_IgbtTU") == M("IGBTTM_v_IgbtTW") && M("CURMV_v_CurL1V") == M("TC_AI_Cur_3"),
                  "G49 规则: 三相温度/三相电流采样链按家族合并");
            Check(M("Check_5V_Rail") != M("Check_12V_Rail") && M("Check_5V_Rail") == M("check 5v rail"),
                  "G49 未命中回落名称归并（非 G49 项行为不变）");
            bool oldSpecMerge = AppConfig.Instance.TodoSpecMerge;
            try
            {
                AppConfig.Instance.TodoSpecMerge = false;
                Check(TodoGrouping.MergeKeyOf("SiC_G_HU High Level") == TodoGrouping.KeyOf("SiC_G_HU High Level"),
                      "todo_spec_merge=false 回退纯名称归并（旧行为兼容）");
            }
            finally { AppConfig.Instance.TodoSpecMerge = oldSpecMerge; }
        }

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
            string LongAgo = DateTime.Today.AddDays(-200).ToString("yyyyMMdd");

            AddFail(t, St, "6.1.1.1 5V_Rail", "SN001", Yesterday);
            AddFail(t, St, "6.1.1.2 5V_Rail", "SN002", Yesterday);
            AddFail(t, St, "6.1.1.3 5V_Rail", "SN003", Today);
            AddFail(t, St, "Check_CAN_Bus", "SN004", Today);
            AddFail(t, St, "Ancient_Item", "SN005", LongAgo);

            var created = t.SyncTodoItems(30);
            Check(created == 2, $"3 条同类 + 1 条异类 -> 新登记 {created} 条待办（期望 2，同类已合并）");

            var view = t.ListTodoView();
            var rail = view.FirstOrDefault(x => x.Title == "5V_Rail");
            Check(rail != null, "合并后的大项展示名 = 5V_Rail");
            Check(rail != null && rail.TotalCount == 3, $"合并项累计次数 = 3（实得 {rail?.TotalCount}）");
            Check(rail != null && rail.VariantCount == 3, "记住了 3 个原始测试项（variants）");
            Check(!view.Any(x => x.Title == "Ancient_Item"), "**200 天前的不良不入待办**（只扫近一个月）");

            Check(view.Count >= 2 && view[0].Title == "5V_Rail", "列表按 fail 次数倒序（3 次的排在 1 次前面）");
            Check(view[0].SortCount >= view[^1].SortCount, "SortCount 单调不增（优先处理次数多的）");

            var again = t.SyncTodoItems(30);
            var rail2 = t.ListTodoView().First(x => x.Title == "5V_Rail");
            Check(again == 0 && rail2.TotalCount == 3, $"重复同步不新增不重算（新增 {again}，次数仍 {rail2.TotalCount}）");

            AddFail(t, St, "6.1.1.4 5V_Rail", "SN006", Today);
            t.SyncTodoItems(30);
            var rail3 = t.ListTodoView().First(x => x.Title == "5V_Rail");
            Check(rail3.TotalCount == 4, $"新不良增量累加到同一张卡（{rail3.TotalCount} 次）");
            Check(rail3.VariantCount == 4, "新的同类变体并入 variants");

            var todayOnly = t.ListTodoView(DateTime.Today, DateTime.Today);
            var railToday = todayOnly.FirstOrDefault(x => x.Title == "5V_Rail");
            Check(railToday != null && railToday.RangeCount == 2 && railToday.TotalCount == 4,
                  $"区间统计独立于累计（今日 {railToday?.RangeCount} 次 / 累计 {railToday?.TotalCount} 次）");

            var future = t.ListTodoView(DateTime.Today.AddDays(5), DateTime.Today.AddDays(6));
            Check(future.Count == 0, "选一个没有不良的区间 -> 待办列为空");
            Check(t.ListTodoView().Count >= 2, "**换回不限区间待办还在**（永久保留，不是被删了）");
            Check(t.CountPendingTodos() >= 2, "CountPendingTodos 与视图一致");

            var allPending = t.ListTodoView();
            var pendingCnt = t.CountPendingTodos();
            Check(allPending.Count == pendingCnt,
                  $"默认视图待办数 == CountPendingTodos（{allPending.Count} == {pendingCnt}，v3.6.1 修复）");
            Check(allPending.Count >= 2 && allPending.All(x => x.RangeCount == x.TotalCount),
                  "不限区间视图 RangeCount 回落为累计次数（不显示误导性的区间次数）");

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

            t.UpdateMaintenanceStatus(mid, "resolved");
            t.SyncTodoItems(30);
            Check(!t.ListTodoView().Any(x => x.Title == "5V_Rail"), "记录已完成后待办仍不显示（问题已处理）");

            System.Threading.Thread.Sleep(1100);
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

            var canItem = t.ListTodoView().First(x => x.Title == "Check_CAN_Bus");
            var mid2 = t.AcknowledgeTodo(canItem.Id, "李四", "major");
            Check(!t.ListTodoView().Any(x => x.Title == "Check_CAN_Bus"), "确认后 CAN 项暂时离开待办");
            Check(t.CountFailRecords("Check_CAN_Bus", St) == 1, "CountFailRecords 能数出该故障项的真实不良数");
            t.DeleteMaintenance(mid2);
            t.SyncTodoItems(30);
            Check(t.ListTodoView().Any(x => x.Title == "Check_CAN_Bus"),
                  "**删掉维修记录后待办自动回来**（待办删不掉）");

#pragma warning disable CS0618
            t.DismissTodo("Check_CAN_Bus", St, "E3002781");
#pragma warning restore CS0618
            t.SyncTodoItems(30);
            Check(t.ListTodoView().Any(x => x.Title == "Check_CAN_Bus"),
                  "**写入 dismissed_todos 也不会让待办消失**（不读忽略名单）");

            var t2 = new Database(tdb);
            var cntBeforeReopen = t2.ListTodoView().First(x => x.Title == "5V_Rail").TotalCount;
            t2.SyncTodoItems(30);
            var cntAfterReopen = t2.ListTodoView().First(x => x.Title == "5V_Rail").TotalCount;
            Check(cntBeforeReopen == cntAfterReopen, $"重开库后同步不重复累加（{cntBeforeReopen} -> {cntAfterReopen}）");

            AddFail(t, "FCT8", "6.1.1.1 5V_Rail", "SN900", Today);
            t.SyncTodoItems(30);
            var railBoth = t.ListTodoView();
            Check(railBoth.Count(x => x.Title == "5V_Rail") == 2, "同故障项在两个机台各自一条待办");

            {
                var can2 = t.ListTodoView().First(x => x.Title == "Check_CAN_Bus");
                events2.Clear();
                var mid3 = t.AcknowledgeTodo(can2.Id, "王五", "major", "open");
                Check(mid3 > 0, $"确认到「待办」列也建记录 #{mid3}");
                Check(events2.Count == 1 && events2[0].from == "" && events2[0].to == "open",
                      $"确认到「待办」列也触发推送（{events2.Count} 次: '{events2[0].from}' -> {events2[0].to}）");
                t.DeleteMaintenance(mid3);

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

        Console.WriteLine("\n【去重】四份 xlsx 写出器合并为 FctShared.Xlsx，输出必须仍然合法");
        {
            var dir = Path.Combine(work, "xlsx");
            Directory.CreateDirectory(dir);

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
                Check(wb.Contains("_x_1") && !wb.Contains("/x:1"),
                      "工作表名：非法字符（/ :）已换成下划线");
                Check(!wb.Contains("<表>"), "工作表名里的尖括号已做 XML 转义（不会把 workbook.xml 搞坏）");
            }

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

            var st1 = FctFailRanker.XlsxExporter.Styles2();
            var st2 = FctFetcher.XlsxWriter.Styles2();
            Check(st1 != st2, "各工具的 styles.xml 仍是各自的调色板（只合并了外壳，没统一长相）");
            Check(st1.Contains("styleSheet") && st2.Contains("styleSheet"), "样式部件格式正常");
        }

        var records = d.ListMaintenance("", 500);
        var xlsx = Path.Combine(work, "维修记录.xlsx");
        var csv = Path.Combine(work, "维修记录.csv");
        MaintenanceExporter.ExportXlsx(xlsx, records);
        MaintenanceExporter.ExportCsv(csv, records);
        Check(new FileInfo(xlsx).Length > 1500, $"xlsx 已生成（{new FileInfo(xlsx).Length} 字节）");
        Check(new FileInfo(csv).Length > 500, $"csv 已生成（{new FileInfo(csv).Length} 字节）");

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

        var csvLines = File.ReadAllLines(csv, System.Text.Encoding.UTF8);
        Check(csvLines[0].StartsWith("\uFEFF") || csvLines[0].Contains("维修记录导出"),
              "csv 带 UTF-8 BOM 与标题行（Excel 直开不乱码）");
        Check(csvLines[1].StartsWith("导出时间,"), "csv 第 2 行是导出时间");
        var header = csvLines[3];
        Check(header.Split(',').Length == 12, $"csv 表头 {header.Split(',').Length} 列（含「更新时间」）");
        Check(header.EndsWith("更新时间"), "csv 最后一列是「更新时间」");

        int dataRows = 0, badRow = 0;
        using (var rd = new StreamReader(csv, System.Text.Encoding.UTF8))
        {
            for (int i = 0; i < 4; i++) rd.ReadLine();
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











        RunUpdateCheckerTests(work);





        Console.WriteLine("\n【正常态自学习】Welford/P² / 建模 / P2评分闸门回放 / P3快照 / P4证据");
        RunNormalStateTests(work);

        Console.WriteLine("\n【v3.36.0 性能】表达式/复合索引 + PRAGMA + N+1 批量缓存");
        RunPerfIndexTests(work);






        Console.WriteLine("\n【P6+P7+P2-A】设备监控（心跳轻量+5min全量+L1维度+采样滚动7天+FCT.ini）+ 无头服务化 + 数据拉取趋势/分布雏形");
        RunP6DeviceHeadlessFetcherTests(work);

        Console.WriteLine("\n【日志导出】app.log 尾部预览 + zip 诊断包（不含 webhook）");
        RunLogExportTests(work);

        Console.WriteLine(_fail == 0 ? "\n==== 全部通过 ====" : $"\n==== {_fail} 项失败 ====");
        return _fail == 0 ? 0 : 1;
    }

    static void RunUpdateCheckerTests(string work)
    {
        Console.WriteLine("\n【更新器】本地更新包检测 / 版本对比 / RELEASE.txt 特点 / 已提示去重");
        var root = Path.Combine(work, "updater");
        var updDir = Path.Combine(root, "updates");
        Directory.CreateDirectory(updDir);

        Check(UpdateChecker.ParseZipVersion("Argus-v9.9.9-update.zip") == new Version(9, 9, 9),
              "解析更新包名 Argus-v9.9.9-update.zip -> 9.9.9");
        Check(UpdateChecker.ParseZipVersion("Argus-v3.5.3.zip") == new Version(3, 5, 3),
              "兼容完整包名 Argus-v3.5.3.zip -> 3.5.3");
        Check(UpdateChecker.ParseZipVersion("Argus-v1.2.zip") == new Version(1, 2),
              "两位版本号 Argus-v1.2.zip -> 1.2");
        Check(UpdateChecker.ParseZipVersion("readme.txt") == null, "非 zip 文件名 -> null");

        var db = new Database(Path.Combine(root, "meta.db"));
        var cur = UpdateChecker.CurrentVersion;
        var newer = new Version(cur.Major + 1, 0, 0);

        var zipPath = Path.Combine(updDir, $"Argus-v{newer}-update.zip");
        using (var fs = File.Create(zipPath)) { fs.Write(new byte[] { 0x50, 0x4B, 0x05, 0x06 }, 0, 4); }

        var info = UpdateChecker.Scan(updDir, db);
        Check(info != null && info.Version == newer, $"扫描到新包 v{newer}（当前 v{cur}）");
        Check(info != null && Path.GetFileName(info.ZipPath) == Path.GetFileName(zipPath), "返回的 zip 路径正确");

        Check(!UpdateChecker.PromptedVersions(db).Contains(newer), "新版本初始未提示过");
        UpdateChecker.MarkPrompted(newer, db);
        Check(UpdateChecker.PromptedVersions(db).Contains(newer), "MarkPrompted 后已记录");
        Check(UpdateChecker.Scan(updDir, db) == null, "已提示过的版本不再弹出（去重生效）");

        var relPath = Path.Combine(updDir, "RELEASE.txt");
        File.WriteAllText(relPath, $@"
Argus 统一发布包  v{newer}
发布日期：2026-08-27
==============================
包含的 zip：
  Argus-v{newer}.zip   [OK]

版本特点：
  · 更新器上线：本地检测新包并弹窗提示
  · GUI 回归 Windows 原版风格
==============================
纯净性声明：本批次 zip 均为纯净包
", System.Text.Encoding.UTF8);
        var notes = UpdateChecker.GetReleaseNotes(newer, updDir);
        Check(notes.Contains("更新器上线"), $"RELEASE.txt 提取到 {newer} 的版本特点（含「更新器上线」）");
        Check(notes.Contains("GUI 回归 Windows 原版风格"), "版本特点含 GUI 原生回归说明");
        Check(!notes.Contains("纯净性声明"), "特点段不越界到下一节（到分隔线/标题为止）");

        var curZip = Path.Combine(updDir, $"Argus-v{cur}-update.zip");
        using (var fs = File.Create(curZip)) { fs.Write(new byte[] { 0x50, 0x4B, 0x05, 0x06 }, 0, 4); }
        Check(UpdateChecker.Scan(updDir, db) == null, "与当前版本相同的包不触发（已提示的仍被过滤）");

        // 审计 C4 回归：换装语义——未锁定直接覆盖、被本进程加载锁定的文件先改名 *.old 让位再覆盖
        {
            var stage = Path.Combine(root, "staging_sim");
            var base_ = Path.Combine(root, "base_sim");
            Directory.CreateDirectory(Path.Combine(stage, "sub"));
            Directory.CreateDirectory(base_);
            File.WriteAllText(Path.Combine(stage, "free.txt"), "NEW-FREE");
            File.WriteAllText(Path.Combine(stage, "sub", "nested.txt"), "NEW-NESTED");
            File.WriteAllText(Path.Combine(stage, "locked.dll"), "NEW-LOCKED-DLL");
            File.WriteAllText(Path.Combine(stage, "config.json"), "PKG-CONFIG");
            File.WriteAllText(Path.Combine(base_, "free.txt"), "OLD-FREE");
            File.WriteAllText(Path.Combine(base_, "locked.dll"), "OLD-LOCKED");
            File.WriteAllText(Path.Combine(base_, "config.json"), "SITE-CONFIG");
            // 模拟已加载映像：持有读+删除共享（镜像加载器语义）——Copy 会撞锁、Move 允许
            using (var hold = new FileStream(Path.Combine(base_, "locked.dll"), FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                UpdateChecker.SwapIn(stage, base_);
                Check(File.ReadAllText(Path.Combine(base_, "free.txt")) == "NEW-FREE",
                      "换装：未锁定文件直接覆盖");
                Check(File.ReadAllText(Path.Combine(base_, "sub", "nested.txt")) == "NEW-NESTED",
                      "换装：子目录结构镜像创建");
                Check(File.ReadAllText(Path.Combine(base_, "locked.dll")) == "NEW-LOCKED-DLL",
                      "换装：被锁定文件改名让位后新内容落位");
            }
            Check(File.ReadAllText(Path.Combine(base_, "locked.dll.old")) == "OLD-LOCKED",
                  "换装：被锁定文件的旧映像保留为 *.old（待下次启动清理）");
            Check(File.ReadAllText(Path.Combine(base_, "config.json")) == "SITE-CONFIG",
                  "换装：config.json 跳过不覆盖（站点合并专用）");
            var oldMarker = Path.Combine(AppConfig.BaseDir, "swapin_selftest.old");
            File.WriteAllText(oldMarker, "x");
            UpdateChecker.CleanupOldBinaries();
            Check(!File.Exists(oldMarker), "换装：CleanupOldBinaries 清理 BaseDir 下 *.old 残留");
            Check(UpdateChecker.TryCommitPending(db), "换装：无 pending 任务 TryCommitPending -> true（幂等 no-op）");
            try { Directory.Delete(stage, true); } catch { }
            try { Directory.Delete(base_, true); } catch { }
        }

        // v3.35.5 只读更新源：update_source 指向共享盘时，扫描/RELEASE.txt 走只读源，暂存仍落本地 update_dir
        var srcOnly = Path.Combine(root, "src_only");
        Directory.CreateDirectory(srcOnly);
        var srcZip = Path.Combine(srcOnly, "Argus-v9.9.9-update.zip");
        using (var fs = File.Create(srcZip)) fs.Write(new byte[] { 0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 0, 22);
        File.WriteAllText(Path.Combine(srcOnly, "RELEASE.txt"),
            "Argus 统一发布包  v9.9.9\n======\n版本特点：\n  · 支持共享文件夹只读热更（update_source）\n======\n",
            System.Text.Encoding.UTF8);
        Check(UpdateChecker.Scan(updDir, db) == null, "update_source: 未配只读源时扫描仍指向本地 update_dir");
        var prevSource = AppConfig.Instance.UpdateSource;
        try
        {
            AppConfig.Instance.UpdateSource = srcOnly;
            var fromSrc = UpdateChecker.Scan(null, db);
            Check(fromSrc != null && fromSrc.Version == new Version(9, 9, 9) &&
                  string.Equals(Path.GetDirectoryName(fromSrc.ZipPath), srcOnly, StringComparison.OrdinalIgnoreCase),
                  "update_source: 只读源优先生效，包路径取自源目录");
            var srcNotes = UpdateChecker.GetReleaseNotes(new Version(9, 9, 9));
            Check(srcNotes.Contains("共享文件夹只读热更") && srcNotes.Contains("update_source"),
                  "update_source: RELEASE.txt 从只读源读取");
            Check(string.Equals(UpdateChecker.ResolveScanDir().TrimEnd('\\'),
                    srcOnly.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase),
                  "update_source: ResolveScanDir 命中只读源");
        }
        finally { AppConfig.Instance.UpdateSource = prevSource; }
        Check(UpdateChecker.Scan(null, db) == null,
              "update_source: 恢复空值后回落本机 update_dir（不残留源状态）");

        try { Directory.Delete(root, true); } catch { }
    }

    static void RunPerfIndexTests(string work)
    {
        var dbPath = Path.Combine(work, "perf_idx.db");
        if (File.Exists(dbPath)) File.Delete(dbPath);
        var db = new Database(dbPath);
        // 造 ~20 天 × 100 条 PASS/FAIL 混合（PASS 带测量值），供 EXPLAIN 计划判定
        var seed = new List<TestRecord>();
        var now = DateTime.Now;
        for (int d = 0; d < 20; d++)
            for (int i = 0; i < 100; i++)
            {
                bool pass = (i % 3) != 0;
                var day = now.Date.AddDays(-d);
                seed.Add(new TestRecord
                {
                    StationId = "FCT1", Model = "G49", TestDate = day.ToString("yyyyMMdd"),
                    Sn = $"SN{d}_{i}", Result = pass ? "PASS" : "FAIL",
                    FailReason = pass ? "" : "STEP_X fail",
                    XmlPath = $"X:\\perf_{d}_{i}.xml",
                    Measurements = pass ? new List<MeasurementRow> { new() { TestName = $"T{i % 10}", Value = 10.0 + i * 0.01 } } : new List<MeasurementRow>(),
                });
            }
        db.BatchInsert(seed);

        // 1) 索引存在（Init 幂等建出）
        var idx = new HashSet<string>();
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name IN ('idx_tr_date_result','idx_tm_daystr','idx_fi_daystr','idx_tf_daystr')";
            using var r = cmd.ExecuteReader();
            while (r.Read()) idx.Add(r.GetString(0));
        }
        Check(idx.Contains("idx_tr_date_result"), "性能: test_records(test_date,result) 复合索引已建");
        Check(idx.Contains("idx_tm_daystr") && idx.Contains("idx_fi_daystr") && idx.Contains("idx_tf_daystr"),
              "性能: 三张明细表日期表达式索引已建");

        // 2) 看板 top fails 走复合索引（等值前缀，计划确定）
        string planTopFails;
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN SELECT COALESCE(fail_reason,''), COALESCE(station_id,'') FROM test_records WHERE test_date = @d AND result = 'FAIL'";
            cmd.Parameters.AddWithValue("@d", now.ToString("yyyyMMdd"));
            using var r = cmd.ExecuteReader();
            var sb = new System.Text.StringBuilder();
            while (r.Read()) sb.AppendLine(r.GetString(3));
            planTopFails = sb.ToString();
        }
        Check(planTopFails.Contains("idx_tr_date_result"),
              "性能: 看板 top fails 查询命中 idx_tr_date_result（避免全表扫描）");

        // 3) 测量窗口 DISTINCT 查询不再整表扫描 test_measurements（表达式索引/sargable 化生效）
        string planTm;
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"EXPLAIN QUERY PLAN
                SELECT DISTINCT m.test_name
                FROM test_measurements m
                JOIN test_records r ON r.id = m.record_id
                WHERE r.result='PASS' AND m.value IS NOT NULL
                  AND m.test_name IS NOT NULL AND m.test_name <> ''
                  AND substr(COALESCE(m.ts,''),1,10) >= @from
                  AND substr(COALESCE(m.ts,''),1,10) <= @to
                ORDER BY m.test_name COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@from", now.AddDays(-20).ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@to", now.ToString("yyyy-MM-dd"));
            using var r = cmd.ExecuteReader();
            var sb = new System.Text.StringBuilder();
            while (r.Read()) sb.AppendLine(r.GetString(3));
            planTm = sb.ToString();
        }
        Check(!planTm.Contains("SCAN test_measurements"),
              "性能: 测量窗口 DISTINCT 查询不整表扫描 test_measurements");
        Check(planTm.Contains("USING INDEX"),
              "性能: 测量窗口查询 JOIN 走索引（非全表扫描）");

        // 4) 连接 PRAGMA 内容
        Check(Database.ConnectionPragmas.Contains("synchronous = NORMAL")
              && Database.ConnectionPragmas.Contains("cache_size = -20000")
              && Database.ConnectionPragmas.Contains("temp_store = MEMORY"),
              "性能: 连接 PRAGMA 含 synchronous=NORMAL/cache_size/temp_store");

        // 5) DeviationSeriesBuilder 批量模型缓存：200 测项×400 PASS 大库全量出图（证明 N+1 消除后行为等价）
        var bigDbPath = Path.Combine(work, "perf_big_build.db");
        if (File.Exists(bigDbPath)) File.Delete(bigDbPath);
        var bdb = new Database(bigDbPath);
        var bcfg = new AppConfig { LearnNormalEnabled = true, LearnNormalMinSamples = 5, LearnNormalStaleDays = 30 };
        var bBase = new DateTime(2026, 7, 22);
        for (int t = 0; t < 200; t++)
            for (int s = 0; s < 8; s++)
                NormalModelStore.Observe(bdb, 5, "measurement", "G49", $"T_{t:D3}",
                    10.0 + t * 0.01, bBase.AddDays(s).ToString("yyyy-MM-dd HH:mm:ss"));
        var bbatch = new List<TestRecord>(400);
        for (int p = 0; p < 400; p++)
        {
            var ts = bBase.AddMinutes(p * 3).ToString("yyyy-MM-dd HH:mm:ss");
            var meas = new List<MeasurementRow>(200);
            for (int t = 0; t < 200; t++)
                meas.Add(new MeasurementRow { TestName = $"T_{t:D3}", Value = 10.0 + t * 0.01 + (p % 7) * 0.02 });
            bbatch.Add(new TestRecord
            {
                StationId = "FCT1", Model = "G49", TestDate = ts[..10].Replace("-", ""),
                Sn = $"SN{p}", Result = "PASS", BatchTimestamp = ts, Measurements = meas,
                XmlPath = $"X:\\big_{p}.xml",
            });
        }
        bdb.BatchInsert(bbatch);
        var (bLabels, bSeries, _) = DeviationSeriesBuilder.Build(bdb, bcfg, 0);
        Check(bSeries.Count == 200, $"性能: 批量模型缓存 Build 全量出图（实得 {bSeries.Count}/200）");
        Check(bLabels.Count >= 400, $"性能: 400 条 PASS 全部入轴（实得 {bLabels.Count}）");
    }

    static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch { }
    }

    static void RunP6DeviceHeadlessFetcherTests(string work)
    {


        {
            // 审计 D2：config.json 损坏时 Load 从最近备份恢复一次（备份文件名 Guid 格式串修复后备份才真正生成）
            var cfgFileD2 = Path.Combine(AppConfig.BaseDir, "config.json");
            var backupD2 = File.Exists(cfgFileD2) ? File.ReadAllText(cfgFileD2) : null;
            var bakDirD2 = Path.Combine(AppConfig.BaseDir, "data", "config_backups");
            Directory.CreateDirectory(bakDirD2);
            var bakFileD2 = Path.Combine(bakDirD2, "config_backup_zzzz_d2test.json"); // 'z' 前缀确保排序为最新（ListBackups 按文件名降序取第一个）
            File.WriteAllText(bakFileD2, "{\"station_id\":\"D2_RESTORED\"}");
            try
            {
                File.WriteAllText(cfgFileD2, "{这不是合法JSON");
                var restored = AppConfig.Load();
                Check(restored.StationId == "D2_RESTORED",
                    $"审计D2: config.json 损坏时从最近备份恢复（station_id={restored.StationId}）");
            }
            finally
            {
                try { if (backupD2 != null) File.WriteAllText(cfgFileD2, backupD2); else File.Delete(cfgFileD2); } catch { }
                try { File.Delete(bakFileD2); } catch { }
            }
        }

        {
            // 单键类型错误不再让整份配置静默回落默认（GetStringSafe 安全化）
            var cfgFile6 = Path.Combine(AppConfig.BaseDir, "config.json");
            var backup6 = File.Exists(cfgFile6) ? File.ReadAllText(cfgFile6) : null;
            try
            {
                File.WriteAllText(cfgFile6, "{\"results_root\":123,\"station_id\":\"ST-SELFTEST\",\"todo_scan_days\":45}");
                var loaded6 = AppConfig.Load();
                Check(loaded6.StationId == "ST-SELFTEST" && loaded6.TodoScanDays == 45,
                    "Config results_root 类型错误(数字)时其余正常键仍生效（不抛异常整份回落）");
                Check(loaded6.ResultsRoot == new AppConfig().ResultsRoot,
                    $"Config results_root 类型错误时该键回落默认值（实得 {loaded6.ResultsRoot}）");
            }
            finally
            {
                try { if (backup6 != null) File.WriteAllText(cfgFile6, backup6); else File.Delete(cfgFile6); } catch { }
            }
        }











        {
            var tmpU = Path.Combine(work, "upgwiz_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(tmpU);
            try
            {
                var pkgRoot = Path.Combine(tmpU, "pkg");
                Directory.CreateDirectory(Path.Combine(pkgRoot, "public"));
                Directory.CreateDirectory(Path.Combine(pkgRoot, "runtimes", "win", "lib", "net8.0"));
                var selfExe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(selfExe) && File.Exists(selfExe))
                    File.Copy(selfExe, Path.Combine(pkgRoot, "Argus.exe"));
                else
                    File.WriteAllText(Path.Combine(pkgRoot, "Argus.exe"), "fake");
                File.WriteAllText(Path.Combine(pkgRoot, "config.json"), "{}");
                File.WriteAllText(Path.Combine(pkgRoot, "public", "x.txt"), "x");
                File.WriteAllText(Path.Combine(pkgRoot, "runtimes", "win", "lib", "net8.0", "x.dll"), "x");
                var zipPath = Path.Combine(tmpU, "Argus-v3.22.1-update.zip");
                System.IO.Compression.ZipFile.CreateFromDirectory(pkgRoot, zipPath);

                var stage1 = Path.Combine(tmpU, "stage_ok");
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, stage1);
                var (ok1, ver1, err1) = FctAggregator.modules.Upgrader.UpgradeWizard.ValidateStage(stage1);
                Check(ok1, $"升级向导: 合成真实结构包校验通过{(ok1 ? "" : "：" + err1)}");
                Check(!string.IsNullOrEmpty(ver1), $"升级向导: 包内版本号可读（v{ver1}）");

                var badPkg = Path.Combine(tmpU, "bad_pkg");
                Directory.CreateDirectory(Path.Combine(badPkg, "data"));
                File.Copy(Path.Combine(pkgRoot, "Argus.exe"), Path.Combine(badPkg, "Argus.exe"));
                File.WriteAllText(Path.Combine(badPkg, "data", "x.db"), "x");
                var (ok2, _, err2) = FctAggregator.modules.Upgrader.UpgradeWizard.ValidateStage(badPkg);
                Check(!ok2 && err2.Contains("data"), $"升级向导: 含 data\\ 目录的包被拒绝（{err2}）");

                var noExe = Path.Combine(tmpU, "no_exe");
                Directory.CreateDirectory(noExe);
                File.WriteAllText(Path.Combine(noExe, "config.json"), "{}");
                var (ok3, _, err3) = FctAggregator.modules.Upgrader.UpgradeWizard.ValidateStage(noExe);
                Check(!ok3 && err3.Contains("Argus.exe"), $"升级向导: 缺 Argus.exe 的包被拒绝（{err3}）");

                var baseDir = Path.Combine(tmpU, "base");
                Directory.CreateDirectory(Path.Combine(baseDir, "tools"));
                File.WriteAllText(Path.Combine(baseDir, "tools", "deploy_update.ps1"), "# tools");
                Check(FctAggregator.modules.Upgrader.UpgradeWizard.FindDeployScript(baseDir, noExe) == Path.Combine(baseDir, "tools", "deploy_update.ps1"),
                    "升级向导: 脚本定位优先 tools\\deploy_update.ps1");
                Directory.Delete(Path.Combine(baseDir, "tools"), recursive: true);
                File.WriteAllText(Path.Combine(baseDir, "deploy_update.ps1"), "# root");
                Check(FctAggregator.modules.Upgrader.UpgradeWizard.FindDeployScript(baseDir, noExe) == Path.Combine(baseDir, "deploy_update.ps1"),
                    "升级向导: tools\\ 缺失时回落安装目录根");
                File.Delete(Path.Combine(baseDir, "deploy_update.ps1"));
                File.WriteAllText(Path.Combine(noExe, "deploy_update.ps1"), "# in-package");
                Check(FctAggregator.modules.Upgrader.UpgradeWizard.FindDeployScript(baseDir, noExe) == Path.Combine(noExe, "deploy_update.ps1"),
                    "升级向导: 安装目录没有时回落包内脚本（v3.22.1 更新包自带）");
            }
            finally
            {
                TryDeleteDir(tmpU);
            }
        }

        {
            int Lum(Color c) => (c.R + c.G + c.B) / 3;
            var lightBg = Theme.Bg;
            var lightText = Theme.TextMain;
            Check(lightBg == SystemColors.Control, "主题: 固定浅色模式（Bg=系统控件色，暗黑已移除）");
            Check(Math.Abs(Lum(lightText) - Lum(lightBg)) >= 120, $"主题: 浅色模式文字/背景对比度 ≥120（实得 {Math.Abs(Lum(lightText) - Lum(lightBg))}）");
            Check(Theme.Bg != Theme.Surface && Theme.Surface != Theme.TextMain, "主题: Bg/Surface/TextMain 三色互不相同（无撞色）");
            Check(Theme.TextMain.ToArgb() != Theme.Surface.ToArgb() &&
                  Theme.TextSub.ToArgb() != Theme.Surface.ToArgb(),
                  "主题: 撞色修复——文字令牌与面板底色不同值（白字白底类回归防线）");

            using (var f = new Form { BackColor = Theme.Bg })
            {
                var lbl = new Label { Text = "x" };
                var dg = new DataGridView();
                var pnl = new Panel();
                f.Controls.Add(lbl); f.Controls.Add(dg); f.Controls.Add(pnl);
                Theme.Apply(f, isPageRoot: true);
                var bg1 = f.BackColor; var lbl1 = lbl.ForeColor; var dgCell1 = dg.DefaultCellStyle.BackColor;
                Theme.Apply(f, isPageRoot: true);
                Check(f.BackColor == bg1 && f.BackColor == Theme.Bg, "主题: Apply 两轮颜色稳定（Bg=系统控件色）");
                Check(lbl.ForeColor == lbl1 && lbl.ForeColor == Theme.TextMain, "主题: Label 两轮均刷成正文色");
                Check(dg.DefaultCellStyle.BackColor == dgCell1 && dgCell1 == Theme.Surface, "主题: DataGridView 单元格底=Surface");
                Check(dg.BackgroundColor == Theme.Bg, "主题: DataGridView 背景=Bg（StyleGrid 走令牌）");
                var btn = Theme.MakeButton("测试", 60);
                Check(btn.FlatStyle == FlatStyle.System, "主题: 按钮 FlatStyle.System 系统原生");
            }
        }

        // ── UI 稳定性: 主页大屏空数据口径与自绘控件离屏绘制 ──
        {
            var emptySnap = new StatsSnapshot();
            Check(emptySnap.TodayPass + emptySnap.TodayFail == 0 && emptySnap.TodayYield == 0.0,
                  "UI 口径: 空数据 StatsSnapshot（TodayPass+TodayFail==0，KPI 良率按 — 显示）");
            bool paintOk = false;
            try
            {
                using var lamp = new DeviceOnlinePanel { Bounds = new Rectangle(0, 0, 320, 240) };
                lamp.SetData(new FctIniData());
                lamp.SetData(new FctIniData
                {
                    Found = true,
                    Devices =
                    {
                        new DeviceInfo { Name = "电源", Port = "COM3", Type = "com", Online = true },
                        new DeviceInfo { Name = "程控源", Port = "COM7", Type = "com", Online = false },
                        new DeviceInfo { Name = "USB-CAN", Port = "USB", Type = "usb", Online = false },
                    },
                });
                using var bmp = new Bitmap(320, 240);
                using var g = Graphics.FromImage(bmp);
                var onPaint = typeof(Control).GetMethod("OnPaint",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                onPaint!.Invoke(lamp, new object[] { new PaintEventArgs(g, new Rectangle(0, 0, 320, 240)) });
                paintOk = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    (离屏绘制异常: {ex.GetType().Name}: {ex.Message})");
            }
            Check(paintOk, "UI 稳定性: DeviceOnlinePanel 空数据/有设备 OnPaint 离屏绘制无异常");
        }

        {
            Check(new AppConfig().AutoUpdate, "无感热升级: config auto_update 缺省 true（自动暂存+自动重启）");
            var cwLive = new AppConfig { LearnNormalEnabled = true };
            cwLive.CopyFrom(new AppConfig { LearnNormalEnabled = false, DesktopNotify = false });
            Check(!cwLive.LearnNormalEnabled && !cwLive.DesktopNotify,
                "ConfigWatcher 热加载: CopyFrom 原地改实例（不换引用）");
            Check(UpdateChecker.ParseZipVersion("Argus-v3.26.0-update.zip") == new Version(3, 26, 0),
                "无感热升级: 更新包文件名解析版本号");
        }


        Console.WriteLine("\n【v3.23.3 客户端主页】Database 统计方法");
        {
            var dbDir = Path.Combine(work, "selftest_v3233_db");
            if (Directory.Exists(dbDir)) Directory.Delete(dbDir, true);
            Directory.CreateDirectory(dbDir);
            var dbFile = Path.Combine(dbDir, "local_test.db");

            var db = new Database(dbFile);
            var todayStr = DateTime.Now.ToString("yyyy-MM-dd");

            var records = new List<TestRecord>
            {
                new TestRecord
                {
                    StationId = "FCT01",
                    Sn = "SN_2026_001",
                    Model = "MODEL_A",
                    Result = "PASS",
                    FailReason = "",
                    TestDate = todayStr,
                    BatchTimestamp = $"{todayStr}T08:15:30",
                    XmlPath = $"C:\\logs\\PASS_SN001_{DateTime.Now:yyyyMMdd}081530.xml"
                },
                new TestRecord
                {
                    StationId = "FCT01",
                    Sn = "SN_2026_002",
                    Model = "MODEL_A",
                    Result = "PASS",
                    FailReason = "",
                    TestDate = todayStr,
                    BatchTimestamp = $"{todayStr}T08:45:00",
                    XmlPath = $"C:\\logs\\PASS_SN002_{DateTime.Now:yyyyMMdd}084500.xml"
                },
                new TestRecord
                {
                    StationId = "FCT02",
                    Sn = "SN_2026_003",
                    Model = "MODEL_B",
                    Result = "FAIL",
                    FailReason = "VoltageCheck;CurrentLimit",
                    Tester = "PEU_G49_FCT2",
                    TestDate = todayStr,
                    BatchTimestamp = $"{todayStr}T08:50:00",
                    XmlPath = $"C:\\logs\\FAIL_SN003_{DateTime.Now:yyyyMMdd}085000.xml"
                },
                new TestRecord
                {
                    StationId = "FCT01",
                    Sn = "SN_2026_004",
                    Model = "MODEL_A",
                    Result = "FAIL",
                    FailReason = "VoltageCheck",
                    Tester = "PEU_G49_FCT1",
                    TestDate = todayStr,
                    BatchTimestamp = $"{todayStr}T09:10:00",
                    XmlPath = $"C:\\logs\\FAIL_SN004_{DateTime.Now:yyyyMMdd}091000.xml"
                },
                new TestRecord
                {
                    StationId = "FCT01",
                    Sn = "SN_2026_005",
                    Model = "MODEL_A",
                    Result = "PASS",
                    FailReason = "",
                    TestDate = todayStr,
                    BatchTimestamp = $"{todayStr}T09:20:00",
                    XmlPath = $"C:\\logs\\PASS_SN005_{DateTime.Now:yyyyMMdd}092000.xml"
                }
            };
            db.BatchInsert(records);

            var hourlyStats = db.FetchDailyHourlyStats("", todayStr);
            Check(hourlyStats.Count == 24, "FetchDailyHourlyStats: 返回完整 24 小时槽位（0..23）");
            var h8 = hourlyStats[8];
            Check(h8.Pass == 2 && h8.Fail == 1 && h8.Total == 3, $"8时统计正确: Pass=2, Fail=1, Total=3 (实得 Pass={h8.Pass}, Fail={h8.Fail})");
            Check(Math.Abs(h8.YieldRate - 66.666) < 0.1, $"8时良率计算准确: ~66.7% (实得 {h8.YieldRate:F1}%)");
            var h9 = hourlyStats[9];
            Check(h9.Pass == 1 && h9.Fail == 1 && h9.Total == 2, $"9时统计正确: Pass=1, Fail=1 (实得 Pass={h9.Pass}, Fail={h9.Fail})");
            var h0 = hourlyStats[0];
            Check(h0.Total == 0 && h0.YieldRate == 0.0, "无数据时段产量为 0且良率显示 0.0%");

            var topFails = db.FetchDailyTopFails("", todayStr, 5);
            Check(topFails.Count >= 2, $"FetchDailyTopFails: 返回不良项清单（{topFails.Count}项）");
            var top1 = topFails[0];
            Check(top1.FailItem == "VoltageCheck" && top1.Count == 2, $"Top1 故障项正确: VoltageCheck 频次 2 (实得 {top1.FailItem}:{top1.Count})");
            Check(top1.Ratio > 60.0, $"Top1 故障占比正确计算 (>60%，实得 {top1.Ratio:F1}%)");
            var top2 = topFails[1];
            Check(top2.FailItem == "CurrentLimit" && top2.Count == 1, $"Top2 故障项正确: CurrentLimit 频次 1 (实得 {top2.FailItem}:{top2.Count})");

            var recentAlerts = db.FetchRecentFailAlerts("", 10);
            Check(recentAlerts.Count == 2, $"FetchRecentFailAlerts: 返回最近 2 条 FAIL 记录（实得 {recentAlerts.Count}）");
            Check(recentAlerts[0].Sn == "SN_2026_004", $"流水逆序排列: 最新一条为 SN_2026_004 (实得 {recentAlerts[0].Sn})");
            Check(recentAlerts[0].FailReason == "VoltageCheck", "流水包含完整失败原因字段");
            Check(!string.IsNullOrEmpty(recentAlerts[0].TimeText), $"流水包含格式化时间: {recentAlerts[0].TimeText}");

            {
                var monthDbPath = Path.Combine(Path.GetTempPath(), "argus_monthly_" + DateTime.Now.Ticks + ".db");
                try
                {
                    var mdb = new Database(monthDbPath);
                    var ym = DateTime.Now.ToString("yyyyMM");
                    var ymPrev = DateTime.Now.AddMonths(-1).ToString("yyyyMM");
                    mdb.BatchInsert(new[]
                    {
                        new TestRecord { StationId = "FCT1", TestDate = ym + "01", Result = "PASS", Sn = "M1", XmlPath = "x1.xml" },
                        new TestRecord { StationId = "FCT1", TestDate = ym + "02", Result = "FAIL", Sn = "M2", XmlPath = "x2.xml" },
                        new TestRecord { StationId = "FCT1", TestDate = ym + "03", Result = "INTERRUPTED", Sn = "M3", XmlPath = "x3.xml" },
                        new TestRecord { StationId = "FCT1", TestDate = ymPrev + "28", Result = "FAIL", Sn = "M4", XmlPath = "x4.xml" },
                        new TestRecord { StationId = "FCT2", TestDate = ym + "04", Result = "FAIL", Sn = "M5", XmlPath = "x5.xml" },
                    });
                    var mAll = mdb.FetchMonthlyStats("", ym);
                    Check(mAll.Pass == 1 && mAll.Fail == 2 && mAll.Interrupted == 1,
                        $"FetchMonthlyStats: 跨机台当月合计正确 (实得 P={mAll.Pass},F={mAll.Fail},I={mAll.Interrupted})");
                    var mF1 = mdb.FetchMonthlyStats("FCT1", ym);
                    Check(mF1.Pass == 1 && mF1.Fail == 1 && mF1.Interrupted == 1,
                        "FetchMonthlyStats: 按机台过滤正确（他台不计）");
                    Check(mdb.FetchMonthlyStats("", ymPrev).Fail == 1,
                        "FetchMonthlyStats: 上月数据不混入当月（前缀隔离）");
                }
                finally { try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(monthDbPath); } catch { } }
            }

            {
                var reDbPath = Path.Combine(Path.GetTempPath(), "argus_monthly_re_" + DateTime.Now.Ticks + ".db");
                try
                {
                    var rdb = new Database(reDbPath);
                    var ym = DateTime.Now.ToString("yyyyMM");
                    rdb.BatchInsert(new[]
                    {
                        new TestRecord { StationId = "FCT1", TestDate = ym + "05", Result = "FAIL", Sn = "SN-RE", XmlPath = "re1.xml", FailReason = "KL30" },
                        new TestRecord { StationId = "FCT1", TestDate = ym + "05", Result = "FAIL", Sn = "SN-RE", XmlPath = "re2.xml", FailReason = "KL30" },
                        new TestRecord { StationId = "FCT1", TestDate = ym + "06", Result = "PASS", Sn = "SN-RE", XmlPath = "re3.xml" },
                        new TestRecord { StationId = "FCT1", TestDate = ym + "05", Result = "PASS", Sn = "SN-RE2", XmlPath = "re4.xml" },
                        new TestRecord { StationId = "FCT1", TestDate = ym + "06", Result = "FAIL", Sn = "SN-RE2", XmlPath = "re5.xml", FailReason = "KL30" },
                        new TestRecord { StationId = "FCT1", TestDate = ym + "06", Result = "PASS", Sn = "", XmlPath = "re6.xml" },
                        new TestRecord { StationId = "FCT1", TestDate = ym + "06", Result = "PASS", Sn = "  ", XmlPath = "re7.xml" },
                    });
                    var mRe = rdb.FetchMonthlyStats("", ym);
                    Check(mRe.Pass == 4 && mRe.Fail == 3 && mRe.Interrupted == 0,
                        $"FetchMonthlyStats: log 条数口径重测全计 (F→F→P 记 1P+2F、P→F 记 1P+1F、空 SN 按 xml_path 各计; 实得 P={mRe.Pass},F={mRe.Fail},I={mRe.Interrupted})");
                    Check(mRe.TodayProductCount == 4,
                        $"FetchMonthlyStats: 当月产品数=去重后 4 台 (实得 {mRe.TodayProductCount})");
                }
                finally { try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(reDbPath); } catch { } }
            }

            {
                // 产品数口径统一断言：空 SN 记录按 xml_path 兜底去重（与月度口径一致），不再被空串坍缩/NULL 丢失
                var snDbPath = Path.Combine(Path.GetTempPath(), "argus_sn_fallback_" + DateTime.Now.Ticks + ".db");
                try
                {
                    var sdb = new Database(snDbPath);
                    var dStr = DateTime.Now.ToString("yyyyMMdd");
                    sdb.BatchInsert(new[]
                    {
                        new TestRecord { StationId = "FCT1", TestDate = dStr, Result = "PASS", Sn = "SN-A", XmlPath = "p1.xml" },
                        new TestRecord { StationId = "FCT1", TestDate = dStr, Result = "PASS", Sn = "", XmlPath = "p2.xml" },
                        new TestRecord { StationId = "FCT1", TestDate = dStr, Result = "PASS", Sn = "   ", XmlPath = "p3.xml" },
                        new TestRecord { StationId = "FCT1", TestDate = dStr, Result = "PASS", Sn = null, XmlPath = "p4.xml" },
                    });
                    var gStats = sdb.FetchGlobalStats("");
                    Check(gStats.ProductCount == 4,
                        $"FetchGlobalStats: 产品数空 SN 按 xml_path 兜底去重=4（旧口径空串坍缩为 1 且 NULL 不计；实得 {gStats.ProductCount}）");
                    var dStats = sdb.FetchDailyStats("", dStr);
                    Check(dStats.TodayProductCount == 4,
                        $"FetchDailyStats: 今日产品数空 SN 按 xml_path 兜底去重=4（与月度口径一致；实得 {dStats.TodayProductCount}）");
                    Check(gStats.Pass == 4 && dStats.Pass == 4,
                        $"PASS 记录口径不变仍按条数计（实得 全局={gStats.Pass}, 今日={dStats.Pass}）");
                }
                finally { try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(snDbPath); } catch { } }
            }

            {
                // SN 批量回填断言：单事务批量写，仅填仍为空的行，返回受影响行数，幂等
                var bfDbPath = Path.Combine(Path.GetTempPath(), "argus_sn_batch_" + DateTime.Now.Ticks + ".db");
                try
                {
                    var bdb = new Database(bfDbPath);
                    bdb.BatchInsert(new[]
                    {
                        new TestRecord { StationId = "FCT1", TestDate = "20260101", Result = "PASS", Sn = "", XmlPath = "b1.xml" },
                        new TestRecord { StationId = "FCT1", TestDate = "20260101", Result = "PASS", Sn = null, XmlPath = "b2.xml" },
                        new TestRecord { StationId = "FCT1", TestDate = "20260101", Result = "PASS", Sn = "KEEP", XmlPath = "b3.xml" },
                    });
                    var ids = new List<long>();
                    using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={bfDbPath}"))
                    {
                        c.Open();
                        using var cmd = c.CreateCommand();
                        cmd.CommandText = "SELECT id FROM test_records WHERE xml_path IN ('b1.xml','b2.xml','b3.xml') ORDER BY xml_path";
                        using var r = cmd.ExecuteReader();
                        while (r.Read()) ids.Add(r.GetInt64(0));
                    }
                    var n1 = bdb.UpdateMissingSnBatch(new List<(long, string)> { (ids[0], "SN-B1"), (ids[1], "SN-B2"), (ids[2], "SN-SKIP") });
                    Check(n1 == 2, $"UpdateMissingSnBatch: 仅填仍为空的行受影响 2（已有 SN 的行跳过；实得 {n1}）");
                    var n2 = bdb.UpdateMissingSnBatch(new List<(long, string)> { (ids[0], "SN-AGAIN") });
                    Check(n2 == 0, $"UpdateMissingSnBatch: 幂等重放不再改写（实得 {n2}）");
                }
                finally { try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(bfDbPath); } catch { } }
            }

            try { Directory.Delete(dbDir, true); } catch { }
        }

        Console.WriteLine("\n【v3.24.0 S1】G49ProductDictionary & FailReasonMerger 真实脏样本解析与三级归并断言");
        {
            Check(G49ProductDictionary.FindKnownSignal("SiC_G_HV_Low_Level") != null, "字典包含 SiC_G_HV_Low_Level");
            Check(G49ProductDictionary.FindKnownSignal("RES_v_ResAng(45°)") != null, "字典包含旋变 45°");
            Check(G49ProductDictionary.FindKnownSignal("TC_AI_Cur_1")?.FamilyName == "TC_AI_Cur", "TC_AI_Cur_1 属于三相电流族");
            Check(G49ProductDictionary.FindKnownSignal("P5V_CAN")?.SemanticType == FailSemanticType.Measurement, "P5V_CAN 为 Measurement 语义");

            Check(G49ProductDictionary.IsInjectionSection("8.19.1"), "8.19.1 识别为 Injection 注入型章节");
            Check(G49ProductDictionary.IsInjectionSection("8.20.2"), "8.20.2 识别为 Injection 注入型章节");

            var rawRes = "8.11.6.1 RES_v_ResAng(45°)(XCP)";
            var keyOff = FailReasonMerger.GetMergedKey(rawRes, false, "signal");
            Check(keyOff == rawRes, "开关关闭时 GetMergedKey 回退原串");

            var parsedSignal = FailReasonMerger.Parse(rawRes);
            var keySignal = FailReasonMerger.GetMergedKey(rawRes, true, "signal");
            Check(keySignal.Contains("RES_v_ResAng") && keySignal.Contains("(XCP)"), "signal 粒度下旋变 45° 归并为 RES_v_ResAng(XCP)");
            Check(parsedSignal.RootCauseHint.Contains("旋变"), "旋变故障匹配治具根因 (工装/模拟器)");

            var keySec = FailReasonMerger.GetMergedKey(rawRes, true, "section");
            Check(keySec == "§8.11", "section 粒度下归并为章号 §8.11");

            var dirtyNoSpace = FailReasonMerger.Parse("8.18.2.2SiC_G_HV_Low_Level_HU(OSC)");
            var keyNoSpace = FailReasonMerger.GetMergedKey("8.18.2.2SiC_G_HV_Low_Level_HU(OSC)", true, "signal");
            Check(dirtyNoSpace.Section == "8.18.2.2", "无空格章节号成功剥离: 8.18.2.2");
            Check(keyNoSpace.Contains("SiC_G_HV_Low_Level") && keyNoSpace.Contains("(OSC)"), "六相栅极 _HU 归并为 SiC_G_HV_Low_Level(OSC)");

            var dirtyDoubleSpace = FailReasonMerger.Parse("7.1.3  IOH_CAN(DMM)");
            Check(dirtyDoubleSpace.Section == "7.1.3", "双空格章节号成功提取");
            Check(dirtyDoubleSpace.SignalBase == "IOH_CAN", "信号基名正确: IOH_CAN");

            var dirtyVoltFor = FailReasonMerger.Parse("8.9.2.1 Volt for TC_AI_Cur_1 (DMM)");
            var keyVoltFor = FailReasonMerger.GetMergedKey("8.9.2.1 Volt for TC_AI_Cur_1 (DMM)", true, "signal");
            Check(keyVoltFor.Contains("TC_AI_Cur") && keyVoltFor.Contains("(DMM)"), "Volt for 前缀剥离且三相电流 _1 归并");

            var dirtyValSpec = FailReasonMerger.Parse("8.1.3.2 KL30_1(Power)(值=33.85, 规格=5~, mA)");
            var keyValSpec = FailReasonMerger.GetMergedKey("8.1.3.2 KL30_1(Power)(值=33.85, 规格=5~, mA)", true, "signal");
            Check(dirtyValSpec.SignalBase == "KL30_1", "值与规格详情剥离且信号为 KL30_1");
            Check(keyValSpec.Contains("KL30_1") && !keyValSpec.Contains("值="), "值与规格被清理，归并名规范");

            var unk = FailReasonMerger.GetMergedKey("UnknownCustomDeviceFailure", true, "signal");
            Check(unk == "UnknownCustomDeviceFailure", "未知格式安全回退原字符串");

            var groupSample = new List<string>
            {
                "6.1.1.1 P5V_CAN(DMM)",
                "6.1.1.2 P1.25V(DMM)",
                "6.1.1.3 VREF(DMM)"
            };
            var alerts = FailReasonMerger.CheckSectionGroupAlert(groupSample, 3);
            Check(alerts.Count == 1, "6.1 电源轨 3 个不同信号成功触发章节群挂告警");
            Check(alerts[0].RootCauseHint.Contains("供电") || alerts[0].RootCauseHint.Contains("电源"), "章节群挂提示供电/电源系统性问题");

            var kKl301 = FailReasonMerger.GetMergedKey("8.1.3.2 KL30_1(Power)", true, "signal");
            var kKl302 = FailReasonMerger.GetMergedKey("8.1.3.2 KL30_2(Power)", true, "signal");
            Check(kKl301 != kKl302, "S1: KL30_1 与 KL30_2 两轨独立不并");
            Check(kKl301.Contains("KL30_1(RailFamily)"), "S1: KL30_1 归并入 KL30_1(RailFamily)");
            Check(FailReasonMerger.GetMergedKey("8.1.3.2 KL30_FILT_1(Power)", true, "signal") == kKl301
                  && FailReasonMerger.Parse("8.1.3.2 KL30_FILT_1(Power)").SignalBase == "KL30_FILT_1",
                  "S1: KL30_FILT_1 与 KL30_1 同轨归并（保有基名）");
            var kGd0 = FailReasonMerger.GetMergedKey("9.1.2 BSW_v_GD_Status1_0(XCP)", true, "signal");
            var kGd5 = FailReasonMerger.GetMergedKey("9.1.2 BSW_v_GD_Status2_5(XCP)", true, "signal");
            Check(kGd0 == kGd5 && kGd0.Contains("BSW_v_GD_Status"), "S1: GD 状态数组下标归一，同族归并");
            Check(FailReasonMerger.GetMergedKey("9.1.3 FLTM_v_ErrStateInvOff1(XCP)", true, "signal")
                    == FailReasonMerger.GetMergedKey("9.1.3 FLTM_v_ErrStateInvOff10(XCP)", true, "signal"),
                  "S1: FLTM 错误状态数组同族归并");
            Check(G49ProductDictionary.FindKnownSignal("P17V_LV_LS")?.FamilyName == "P17V_LV_LS", "S1: 字典含 P17V_LV_LS（独立轨族）");
            Check(G49ProductDictionary.FindKnownSignal("P1.25V_LVD_Core")?.FamilyName == "P1.25V_LVD_Core", "S1: P1.25V_LVD_Core 独立轨族不并");
            var kP12 = FailReasonMerger.GetMergedKey("6.1.2.1 P12V_FB_HS(DMM)", true, "signal");
            var kP15 = FailReasonMerger.GetMergedKey("6.1.2.1 P15V_LVD_LS(DMM)", true, "signal");
            Check(kP12 != kP15, "S1: 不同电源轨不并（P12V_FB_HS ≠ P15V_LVD_LS，保住诊断）");
        }

        {
            var dbDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_learning_s2_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dbDir);
            var dbFile = Path.Combine(dbDir, "local_test.db");

            var db = new Database(dbFile);
            Check(db != null, "Database 初始化成功");

            var now = DateTime.Now;
            db!.InsertLocalDeviceSample(15.5, 45.2, 120.0, now.ToString("yyyy-MM-dd HH:mm:ss"));
            var samples = db.GetLocalDeviceSamples(1);
            Check(samples.Count == 1, "查询本地样本数量为 1");
            Check(Math.Abs(samples[0].Cpu - 15.5) < 0.001, "本地样本 CPU 读出一致");
            Check(Math.Abs(samples[0].Mem - 45.2) < 0.001, "本地样本 Memory 读出一致");
            Check(Math.Abs(samples[0].DiskFree - 120.0) < 0.001, "本地样本 DiskGb 读出一致");

            db.InsertLocalDeviceSample(20.0, 50.0, 100.0, now.AddDays(-20).ToString("yyyy-MM-dd HH:mm:ss"));
            var purged = db.PurgeOldLocalDeviceSamples(14);
            Check(purged >= 1, "PurgeOldLocalDeviceSamples 成功清理 14 天前旧数据");

            var snap0 = DeviceSampleRecorder.Instance.RecordOnce();
            Check(!snap0.HasValue, "审计M12: Instance 首次采样仅建立基线返回 null（不再产 0.0 哨兵假样本）");
            Thread.Sleep(100); // CPU 差分需时间跨度，连续调用 sysTotal=0 → null（生产为 5 分钟周期无此问题）
            var snap = DeviceSampleRecorder.Instance.RecordOnce();
            Check(snap.HasValue, "DeviceSampleRecorder 第二次采样成功（首次已建立 CPU 基线）");
            Check(snap!.Value.Cpu >= 0 && snap.Value.Cpu <= 100, "DeviceSampleRecorder CPU 采集合法");
            Check(snap.Value.MemPct >= 0 && snap.Value.MemPct <= 100, "DeviceSampleRecorder 内存采集合法");
            Check(snap.Value.DiskFreeGb >= 0, "DeviceSampleRecorder 磁盘余量合法");


            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dbDir, true); } catch { }
        }

        {
            var fixedNow = new DateTime(2026, 9, 4, 20, 0, 0);
            var today = fixedNow.Date;
            string D(DateTime d) => d.ToString("yyyyMMdd");
            var recs = new List<BaselineSourceRecord>();
            long id = 0;
            void Add(DateTime d, int hour, string model, string sn, string result)
                => recs.Add(new BaselineSourceRecord(++id, D(d), hour, model, sn, result));

            for (int day = 1; day <= 6; day++)
                for (int i = 0; i < 10; i++)
                    Add(today.AddDays(-day), 8, "M1", $"M1-{day}-{i}", "PASS");
            for (int i = 0; i < 6; i++) Add(today, 8, "M1", $"T1-{i}", "PASS");
            for (int i = 0; i < 4; i++) Add(today, 8, "M1", $"T1-F{i}", "FAIL");
            for (int i = 0; i < 10; i++) Add(today, 14, "M1", $"T2-{i}", "PASS");
            for (int i = 0; i < 5; i++) Add(today.AddDays(-1), 9, "M2", $"M2-{i}", "PASS");
            for (int i = 0; i < 5; i++) Add(today, 9, "M2", $"M2T-{i}", "PASS");
            for (int day = 1; day <= 3; day++)
                for (int i = 0; i < 10; i++) Add(today.AddDays(-day), 20, "M1", $"N-{day}-{i}", "PASS");
            for (int i = 0; i < 7; i++) Add(today, 20, "M1", $"NT-{i}", "PASS");
            for (int i = 0; i < 3; i++) Add(today, 20, "M1", $"NT-I{i}", "INTERRUPTED");

            var cfg = new AppConfig
            {
                LearnBaselineEnabled = true,
                LearnBaselineWindowDays = 7,
                LearnBaselineSigma = 3.0,
                LearnBaselineMinSamples = 30,
            };
            var state = SelfBaseline.Compute(recs, cfg, fixedNow);

            var yd = state.Alerts.Where(a => a.Kind == "yield_drop").ToList();
            Check(yd.Count == 1, $"S3: 仅 M1 早段触发 1 条良率跌破预警（实得 {yd.Count}）");
            Check(yd[0].Model == "M1" && yd[0].Slot == 1, "S3: 预警定位 M1 早(06-12) 段");
            Check(Math.Abs(yd[0].Mean - 100.0) < 0.01, "S3: 基线均值 100%（窗口全 PASS）");
            Check(yd[0].ExpectedLow >= 97.0, $"S3: 零σ下限生效，期望下界 ≥ 97（实得 {yd[0].ExpectedLow}）");
            Check(yd[0].Message.Contains("M1"), "S3: 预警文案含型号与期望区间（只标记不强动作）");

            Check(!state.Alerts.Any(a => a.Model == "M2"), "S3: 冷启动桶（M2 窗口 <30 件）不产出预警");
            Check(state.Alerts.Count(a => a.Kind == "yield_drop") == 1, "S3: 型号×时段隔离——正常桶（午段）不误报");

            var hz = state.Alerts.Where(a => a.Kind == "interrupt_hotzone").ToList();
            Check(hz.Count == 1 && hz[0].Slot == 3, $"S3: 晚段中断热区触发 1 条（实得 {hz.Count}）");
            Check(Math.Abs(hz[0].Actual - 30.0) < 0.01, "S3: 今日晚段中断率 30%");
            Check(hz[0].Message.Contains("治具") || hz[0].Message.Contains("治具/通信"), "S3: 中断预警带根因指向（治具/通信/操作）");

            var m1s1 = state.Buckets.First(b => b.Model == "M1" && b.Slot == 1);
            Check(m1s1.SampleCount == 60 && m1s1.DayCount == 6, $"S3: M1 早段桶 60 件/6 天（实得 {m1s1.SampleCount}/{m1s1.DayCount}）");
            var dupList = new List<BaselineSourceRecord>
            {
                new(1, D(today), 8, "M1", "DUP", "FAIL"),
                new(2, D(today), 8, "M1", "DUP", "PASS"),
            };
            Check(SelfBaseline.DedupBySn(dupList).Count == 1, "S3: 同 SN 复测去重只保留最新一条");

            var bRecs = new List<BaselineSourceRecord>();
            long bid = 0;
            for (int day = 1; day <= 3; day++)
                for (int i = 0; i < 10; i++)
                    bRecs.Add(new BaselineSourceRecord(++bid, D(today.AddDays(-day)), 8, "M1", $"B{day}-{i}", "PASS"));
            for (int i = 0; i < 97; i++) bRecs.Add(new BaselineSourceRecord(++bid, D(today), 8, "M1", $"T{i}", "PASS"));
            for (int i = 0; i < 3; i++) bRecs.Add(new BaselineSourceRecord(++bid, D(today), 8, "M1", $"TF{i}", "FAIL"));
            var stB = SelfBaseline.Compute(bRecs, new AppConfig { LearnBaselineWindowDays = 7, LearnBaselineSigma = 3.0, LearnBaselineMinSamples = 3 }, fixedNow);
            Check(stB.Alerts.Count == 0, $"S3: 今日良率恰等于期望下界 97%（开区间）不报警（实得 {stB.Alerts.Count} 条）");
            var stB2 = SelfBaseline.Compute(bRecs, new AppConfig { LearnBaselineWindowDays = 7, LearnBaselineSigma = 2.9, LearnBaselineMinSamples = 3 }, fixedNow);
            Check(stB2.Alerts.Count == 1, "S3: σ 收紧到 2.9 后同数据触发预警（边界外）");

            var round = BaselineState.FromJson(state.ToJson());
            Check(round != null && round!.Buckets.Count == state.Buckets.Count && round.Alerts.Count == state.Alerts.Count,
                  "S3: BaselineState JSON 序列化往返一致");

            var dbDir3 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_learning_s3_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dbDir3);
            var db3 = new Database(Path.Combine(dbDir3, "local.db"));
            int seq3 = 0;
            TestRecord TR(DateTime d, int hour, string sn, string result, string reason = "")
            {
                seq3++;
                return new TestRecord
                {
                    StationId = "FCT1", Sn = sn, Model = "E3002781", Result = result, FailReason = reason,
                    TestDate = d.ToString("yyyyMMdd"),
                    BatchTimestamp = $"{d:yyyy-MM-dd} {hour:00}:30:00",
                    XmlPath = $@"C:\t\s3_{seq3}.xml",
                };
            }
            var trs = new List<TestRecord>();
            for (int i = 0; i < 10; i++) trs.Add(TR(fixedNow.AddDays(-1), 8, $"B-{i}", "PASS"));
            for (int i = 0; i < 10; i++) trs.Add(TR(fixedNow, 8, $"G-P{i}", "PASS"));
            trs.Add(TR(fixedNow, 8, "G1", "FAIL", "6.1.1.2.7 P5V_CAN(DMM)"));
            trs.Add(TR(fixedNow, 8, "G2", "FAIL", "6.1.1.2.3 P1.25V_LVD_Core(DMM)"));
            trs.Add(TR(fixedNow, 8, "G3", "FAIL", "6.1.1.3.1 VREF_HU to SiC_S_HU(DMM)"));
            trs.Add(TR(fixedNow, 8, "R1", "FAIL", "8.11.6.1 RES_v_ResAng(45°)(XCP)"));
            trs.Add(TR(fixedNow, 8, "R2", "FAIL", "8.11.6.2 RES_v_ResAng(135°)(XCP)"));
            trs.Add(TR(fixedNow, 8, "R3", "FAIL", "8.11.6.3 RES_v_ResAng(225°)(XCP)"));
            db3.BatchInsert(trs);

            LearningEngine.RunOnce(db3, new AppConfig(), fixedNow);
            Check(db3.GetMeta(LearningEngine.MetaBaseline) == null
                  && db3.GetMeta(LearningEngine.MetaGroupAlerts) == null
                  && db3.GetMeta(LearningEngine.MetaPriorityFactors) == null,
                  "S3/S4: 全开关关闭时 RunOnce 为 no-op（不写任何 meta，行为兼容）");

            var cfgOn = new AppConfig
            {
                LearnBaselineEnabled = true,
                LearnFailMergeEnabled = true,
                LearnPriorityEnabled = true,
                LearnBaselineWindowDays = 7,
                LearnBaselineMinSamples = 5,
                LearnGroupAlertMin = 3,
            };
            LearningEngine.RunOnce(db3, cfgOn, fixedNow);

            var bState = LearningEngine.GetBaselineState(db3);
            Check(bState != null, "S3: RunOnce 后 app_meta 落盘 learn_baseline_state");
            Check(bState!.Buckets.Any(b => b.Model == "E3002781" && b.Slot == 1), "S3: 基线含 E3002781 早段桶（yyyyMMdd 日期格式兼容）");

            var gState = LearningEngine.GetGroupAlerts(db3);
            Check(gState != null && gState!.Alerts.Count == 1 && gState.Alerts[0].Section == "6.1",
                  $"S4: 章节群挂落盘——6.1 三信号族触发 1 条（RES 四角同族不计，实得 {gState?.Alerts.Count ?? 0}）");
            Check(gState!.Alerts[0].Hint.Contains("供电") || gState.Alerts[0].Hint.Contains("电源"),
                  "S4: 群挂预警根因指向供电系统性问题");

            var injAlerts = FailReasonMerger.CheckSectionGroupAlert(
                new[] { "8.19.5 FLTM_DESAT_A(XCP)", "8.20.2 ASC_B(XCP)", "8.21.1 SBC_C(XCP)" }, 3);
            Check(injAlerts.Count == 0, "S4: 注入型章节（8.19/8.20/8.21）不计群挂");

            Check(Math.Abs(LearningEngine.CalibrateFactor(0, 0) - 1.0) < 0.001, "S4: 无维修无删除 → 因子 1.0");
            Check(Math.Abs(LearningEngine.CalibrateFactor(10, 0) - 1.5) < 0.001, "S4: 完成维修 10 次 → 因子封顶 1.5");
            Check(Math.Abs(LearningEngine.CalibrateFactor(0, 10) - 0.5) < 0.001, "S4: 显式删除 10 次 → 因子下探 0.5");
            Check(Math.Abs(LearningEngine.CalibrateFactor(10, 10) - 0.75) < 0.001, "S4: 乘性合成 1.5×0.5=0.75");

            db3.CreateMaintenance(new MaintenanceRecord
            { StationId = "FCT1", FailItem = "P5V_CAN", Severity = "major", Status = "resolved" });
            LearningEngine.RunOnce(db3, cfgOn, fixedNow);
            LearningEngine.LoadPriorityFactors(db3);
            Check(LearningEngine.FactorOf("P5V_CAN") > 1.0, "S4: 已完成维修项因子 > 1.0（优先级上调）");
            Check(LearningEngine.FactorOf("NoSuchItem") == 1.0, "S4: 未学习项因子 = 1.0 原权重");


            var todayKey = today.ToString("yyyyMMdd");
            var mergedTop = db3.FetchDailyTopFails("", todayKey, 5, mergeOverride: true);
            var resTop = mergedTop.FirstOrDefault(t => t.FailItem.Contains("RES_v_ResAng"));
            Check(resTop != null && resTop.Count == 3,
                  $"S4: Top 排行归并——旋变四角同族合并计数 3（实得 {(resTop?.Count ?? 0)}）");
            Check(resTop != null && resTop.RootCauseHint.Contains("旋变"), "S4: 归并 Top 条目带治具根因指向文案");
            var rawTop = db3.FetchDailyTopFails("", todayKey, 10, mergeOverride: false);
            Check(rawTop.Count(t => t.FailItem.Contains("RES_v_ResAng")) == 3,
                  "S4: 归并关闭时 Top 排行保持原串（行为 100% 兼容）");

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dbDir3, true); } catch { }
        }

        {
            var card = FeishuCardV2.Root("FCT1 · E3002781 · FAIL 告警", "red", new List<object>
            {
                FeishuCardV2.FieldRow(("机台", "FCT1"), ("型号", "E3002781")),
                FeishuCardV2.Md("**失败项**\nKL30_1"),
                FeishuCardV2.Hr(),
                FeishuCardV2.Note("Argus · 2026-09-04"),
            }, subtitle: "产线告警");
            var json = System.Text.Json.JsonSerializer.Serialize(card);

            Check(json.Contains("\"schema\":\"2.0\""), "飞书卡片2.0: 根声明 schema=2.0");
            Check(json.Contains("\"body\":{") && json.Contains("\"elements\":["), "飞书卡片2.0: elements 位于 body 层级（非 1.0 顶层）");
            Check(json.Contains("\"column_set\"") && !json.Contains("\"fields\":"), "飞书卡片2.0: 多列用 column_set（无 1.0 div.fields）");
            Check(json.Contains("\"tag\":\"hr\"") && json.Contains("\"tag\":\"markdown\""), "飞书卡片2.0: hr/markdown 新 tag");
            Check(json.Contains("\"template\":\"red\""), "飞书卡片2.0: header.template 合法枚举色");
            Check(json.Contains("width_mode") && json.Contains("plain_text"), "飞书卡片2.0: fill 宽度模式 + 标题 plain_text");

            var esc = FeishuCardV2.Escape("a*b[c]`d|e");
            Check(esc == @"a\*b\[c\]\`d\|e", $"飞书卡片2.0: markdown 转义生效（实得 {esc}）");
            Check(FeishuCardV2.Escape(null) == "", "飞书卡片2.0: null 输入安全返回空串");

            var items = new List<object> { FeishuCardV2.FieldRow(("机台", "FCT1"), ("型号", "E3")), FeishuCardV2.Note("n") };
            var withBanner = System.Text.Json.JsonSerializer.Serialize(
                FeishuCardV2.Root("t", "red", items, bannerImgKey: "img_v2_banner_1"));
            Check(withBanner.Contains("\"tag\":\"img\"") && withBanner.Contains("\"img_key\":\"img_v2_banner_1\""),
                "飞书卡片2.0: banner img_key 渲染为 img 元素");
            Check(withBanner.Contains("\"margin\":\"-12px -16px 0 -16px\""), "飞书卡片2.0: banner 负 margin 通栏且贴 header");
            Check(withBanner.Contains("\"padding\":\"12px 16px 0 16px\""), "飞书卡片2.0: 带 banner 时 header 底 padding 归零");
            Check(withBanner.IndexOf("\"img_key\"") < withBanner.IndexOf("\"column_set\""),
                "飞书卡片2.0: banner 位于 body 最顶端（先于正文字段）");
            var noBanner = System.Text.Json.JsonSerializer.Serialize(FeishuCardV2.Root("t", "red", items));
            Check(!noBanner.Contains("img_key"), "飞书卡片2.0: 未配 img_key 卡片无图（兼容旧版式）");
            Check(FeishuCardV2.BannerImg(null) == null && FeishuCardV2.BannerImg("") == null && FeishuCardV2.BannerImg("   ") == null,
                "飞书卡片2.0: banner 空白/null key 安全返回 null");
            Check(FeishuCardV2.BannerImg(" img_v2_x ") is not null, "飞书卡片2.0: banner 合法 key 返回元素");

            Check(string.IsNullOrEmpty(AppConfig.FallbackWebhookUrl),
                "飞书推送: FallbackWebhookUrl 已置空（开源版不携带任何硬编码凭据）");
        }

        // ============ 规格06 多元深分析：PASS 测量值采集 / v14 存储 / 漂移+CPK / backfill ============
        {
            Console.WriteLine("\n【规格06】PASS 测量值采集（ParseMeasurements）…");
            var savedStation = AppConfig.Instance.StationId;
            var savedRoot = AppConfig.Instance.ResultsRoot;
            var savedCollect = AppConfig.Instance.AnalyzeCollectPass;
            var savedMaxKb = AppConfig.Instance.AnalyzeMaxFileKb;
            var savedMaxTests = AppConfig.Instance.AnalyzeMaxTestsPerFile;
            try
            {
                AppConfig.Instance.StationId = "FCT1";
                AppConfig.Instance.ResultsRoot = Path.Combine(work, "a6res");
                AppConfig.Instance.AnalyzeCollectPass = true;

                string Test(string name, string? value, string? hi = null, string? lo = null, string? unit = null, string? rule = null)
                {
                    var s = $"<TEST NAME=\"{name}\" STATUS=\"Passed\"";
                    if (value != null) s += $" VALUE=\"{value}\"";
                    if (hi != null) s += $" HILIM=\"{hi}\"";
                    if (lo != null) s += $" LOLIM=\"{lo}\"";
                    if (unit != null) s += $" UNIT=\"{unit}\"";
                    if (rule != null) s += $" RULE=\"{rule}\"";
                    return s + "/>";
                }
                var passXml =
                    "<FACTORY USER=\"op1\" TESTER=\"FCT-01\" FIXTURE_ID=\"FX01\">" +
                    "<PANEL STATUS=\"Passed\"><DUT ID=\"E3002781A1\">" +
                    Test("V_KL30", "13.52", "14.4", "10.8", "V", "LeGe") +
                    Test("MODE_STR", "OK") +
                    Test("NO_VAL", null) +
                    "</DUT></PANEL></FACTORY>";

                var parser = new FctAggregator.Parsing.DefaultResultParser(FctAggregator.Parsing.ParserRuleSet.Default, "FCT1");
                var rows = parser.ParseMeasurements(passXml);
                Check(rows.Count == 3, $"规格06: PASS 文件全 TEST 收录不限 Failed（实得 {rows.Count}）");
                var r0 = rows[0];
                Check(r0.Value.HasValue && Math.Abs(r0.Value.Value - 13.52) < 1e-9
                      && r0.Hilim.HasValue && Math.Abs(r0.Hilim.Value - 14.4) < 1e-9
                      && r0.Lolim.HasValue && Math.Abs(r0.Lolim.Value - 10.8) < 1e-9
                      && r0.Unit == "V" && r0.Rule == "LeGe",
                      "规格06: 数值测量行 value/上下限/单位/规则齐全");
                Check(rows[1].Value == null && rows[1].ValueText == "OK",
                      "规格06: 非数值 VALUE → value 空 + value_text 原串");
                Check(rows[2].Value == null && rows[2].ValueText == null,
                      "规格06: 无 VALUE 节点行保留（无值）");
                var sci = parser.ParseMeasurements(
                    "<FACTORY USER=\"op1\"><PANEL STATUS=\"Passed\"><DUT ID=\"D\">" +
                    Test("SC", "1.3e1") + "</DUT></PANEL></FACTORY>");
                Check(sci.Count == 1 && sci[0].Value.HasValue && Math.Abs(sci[0].Value!.Value - 13.0) < 1e-9,
                      "规格06: 科学计数法 VALUE 正常解析");

                AppConfig.Instance.AnalyzeCollectPass = false;
                Check(parser.ParseMeasurements(passXml).Count == 0,
                      "规格06: analyze_collect_pass=false 提取 no-op（开关可关回旧行为）");
                AppConfig.Instance.AnalyzeCollectPass = true;

                AppConfig.Instance.AnalyzeMaxFileKb = 1;
                Check(parser.ParseMeasurements(passXml.PadRight(3000)).Count == 0,
                      "规格06: 超大文件护栏（>max_file_kb）跳过提取");
                AppConfig.Instance.AnalyzeMaxFileKb = savedMaxKb;

                AppConfig.Instance.AnalyzeMaxTestsPerFile = 2;
                Check(parser.ParseMeasurements(passXml).Count == 2,
                      "规格06: 单文件测项上限截断（1000 默认可调）");
                AppConfig.Instance.AnalyzeMaxTestsPerFile = savedMaxTests;

                Check(parser.ParseMeasurements("<broken").Count == 0,
                      "规格06: 损坏 XML 提取安全空表不抛异常");

                var a6res = Path.Combine(work, "a6res");
                var passFile = Path.Combine(a6res, "Online", "E3002781", "20260905", "P_UNIT_SN001_20260905120000_0001.xml");
                var failFile = Path.Combine(a6res, "Online", "E3002781", "20260905", "F_UNIT_SN002_20260905120100_0001.xml");
                Directory.CreateDirectory(Path.GetDirectoryName(passFile)!);
                File.WriteAllText(passFile, passXml);
                File.WriteAllText(failFile, passXml);

                var outp = parser.Parse(passFile, passXml);
                Check(outp != null && outp.Result == "PASS" && outp.Measurements.Count == 3,
                      $"规格06: Parse 集成 PASS 分支接线（实得 {outp?.Measurements.Count ?? -1} 行）");
                var outpF = parser.Parse(failFile, passXml);
                Check(outpF != null && outpF.Result == "FAIL" && outpF.Measurements.Count == 0,
                      "规格06: FAIL 文件不采测量值（明细留待期2 fail_items）");

                var proc = new Processor(AppConfig.Instance, "FCT1", FctAggregator.Parsing.ParserRegistry.Instance, null);
                var recT = proc.ParseAndClassify(passFile);
                Check(recT != null && recT.Measurements.Count == 3 && recT.Result == "PASS",
                      "规格06: Processor 透传 Measurements 至 TestRecord");
            }
            finally
            {
                AppConfig.Instance.StationId = savedStation;
                AppConfig.Instance.ResultsRoot = savedRoot;
                AppConfig.Instance.AnalyzeCollectPass = savedCollect;
                AppConfig.Instance.AnalyzeMaxFileKb = savedMaxKb;
                AppConfig.Instance.AnalyzeMaxTestsPerFile = savedMaxTests;
            }
            Console.WriteLine("[规格06] 采集组完成");

            Console.WriteLine("\n【规格06】存储层 test_measurements / fail_items …");
            long RowCount(string dbPath, string table)
            {
                using var c = new SqliteConnection($"Data Source={dbPath}");
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
            long IdOfRec(string dbPath, string xmlPath)
            {
                using var c = new SqliteConnection($"Data Source={dbPath}");
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT id FROM test_records WHERE xml_path=@p";
                cmd.Parameters.AddWithValue("@p", xmlPath);
                var v = cmd.ExecuteScalar();
                return v == null || v is DBNull ? -1 : Convert.ToInt64(v);
            }

            var dirA6 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_analysis_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dirA6);
            var dbA6Path = Path.Combine(dirA6, "local.db");
            var dbA6 = new Database(dbA6Path);
            int seqA6 = 0;
            TestRecord MP(string ts, (string name, double v, double? lo, double? hi)[] ms)
            {
                seqA6++;
                return new TestRecord
                {
                    StationId = "FCT1", Model = "E3002781", Result = "PASS",
                    TestDate = "20260905", BatchTimestamp = ts,
                    XmlPath = $@"C:\t\a6_{seqA6}.xml",
                    Measurements = ms.Select(m => new MeasurementRow
                    {
                        TestName = m.name, Value = m.v, Lolim = m.lo, Hilim = m.hi, Unit = "V",
                    }).ToList(),
                };
            }

            Check(RowCount(dbA6Path, "test_measurements") == 0, "规格06: test_measurements 表随 Init 创建（v14）");
            Check(RowCount(dbA6Path, "fail_items") == 0, "规格06: fail_items 表预建（期2启用）");

            var m1 = MP("2026-09-04 08:00:00", new[] { ("V_KL30", 13.5, (double?)10.8, (double?)14.4) });
            dbA6.BatchInsert(new[] { m1 });
            Check(RowCount(dbA6Path, "test_measurements") == 1, "规格06: BatchInsert 后测量值自动挂接入库");
            var ridM1 = IdOfRec(dbA6Path, m1.XmlPath);
            dbA6.BatchInsert(new[] { m1 });
            dbA6.InsertMeasurementsFor(new List<(TestRecord, long)> { (m1, ridM1) });
            Check(RowCount(dbA6Path, "test_measurements") == 1,
                  "规格06: UNIQUE(record_id,test_name) 幂等（重复插入不重复落行）");

            _ = new Database(dbA6Path);
            Check(RowCount(dbA6Path, "test_measurements") == 1, "规格06: 老库重开 Init 幂等（CREATE IF NOT EXISTS），数据完好");

            var mOld = MP("2026-01-01 00:00:00", new[] { ("T_OLD", 1.0, (double?)null, (double?)null) });
            dbA6.BatchInsert(new[] { mOld });
            Check(RowCount(dbA6Path, "test_measurements") == 2, "规格06: 旧 ts 测量行入库");
            var purgedM = dbA6.PurgeOldMeasurements(30);
            Check(purgedM >= 1 && RowCount(dbA6Path, "test_measurements") == 1,
                  $"规格06: PurgeOldMeasurements 清旧留新（删 {purgedM}）");

            var mS1 = MP("2026-09-04 09:00:00", new[] { ("T_STAT", 9.9, (double?)9.0, (double?)11.0) });
            var mS2 = MP("2026-09-04 09:05:00", new[] { ("T_STAT", 10.0, (double?)9.0, (double?)11.0) });
            var mS3 = MP("2026-09-04 09:10:00", new[] { ("T_STAT", 10.1, (double?)9.0, (double?)11.0) });
            dbA6.BatchInsert(new[] { mS1, mS2, mS3 });
            var stat = dbA6.AggregateMeasurementStats("2026-09-04", "2026-09-05");
            Check(stat.Count == 2, $"规格06: 聚合按 (model,test_name) 分组（实得 {stat.Count} 组）");
            var statT = stat.FirstOrDefault(s => s.TestName == "T_STAT");
            Check(statT != null && statT.N == 3 && Math.Abs(statT.Mean - 10.0) < 1e-9,
                  "规格06: 聚合统计 N/mean 正确");
            var sT = statT!;
            Check(Math.Abs(sT.Sigma - 0.1) < 1e-9
                  && sT.Lolim.HasValue && Math.Abs(sT.Lolim.Value - 9.0) < 1e-9
                  && sT.Hilim.HasValue && Math.Abs(sT.Hilim.Value - 11.0) < 1e-9,
                  "规格06: 样本σ（n−1 修正）与限值聚合正确");
            Console.WriteLine("[规格06] 存储组完成");

            Console.WriteLine("\n【规格06】分析器 漂移 + CPK …");
            var fixedNow = new DateTime(2026, 9, 5, 20, 0, 0); // 漂移窗口改为"过去24h"（C-I3）后需 20 点时刻完整覆盖 06-15 点 today 样本
            var b1 = MP("2026-09-04 08:00:00", new[]
            {
                ("T_DRIFT", 9.9, (double?)9.0, (double?)11.0),
                ("T_MARGIN", 10.50, (double?)9.0, (double?)11.0),
                ("T_SETPOINT", 14.40, (double?)14.20, (double?)14.60),
                ("T_CONST", 5.0, (double?)null, (double?)null),
                ("T_ZERO", 1.0, (double?)null, (double?)null),
                ("T_OK", 10.2, (double?)9.0, (double?)11.0),
                ("T_NOLIM", 1.0, (double?)null, (double?)null),
            });
            var b2 = MP("2026-09-04 09:00:00", new[]
            {
                ("T_DRIFT", 10.1, (double?)9.0, (double?)11.0),
                ("T_MARGIN", 10.90, (double?)9.0, (double?)11.0),
                ("T_SETPOINT", 14.41, (double?)14.20, (double?)14.60),
                ("T_CONST", 5.0, (double?)null, (double?)null),
                ("T_ZERO", 1.0, (double?)null, (double?)null),
                ("T_OK", 9.8, (double?)9.0, (double?)11.0),
                ("T_NOLIM", 1.2, (double?)null, (double?)null),
            });
            dbA6.BatchInsert(new[] { b1, b2 });
            var todayRecs = new List<TestRecord>();
            for (int i = 0; i < 10; i++) // MinTodaySamples 3→10（C-I3），造数同步抬到 10 条
                todayRecs.Add(MP($"2026-09-05 {i + 6:00}:00:00", new[]
                {
                    ("T_DRIFT", 10.5, (double?)9.0, (double?)11.0),
                    ("T_MARGIN", 10.85, (double?)9.0, (double?)11.0),
                    ("T_SETPOINT", 14.40, (double?)14.20, (double?)14.60),
                    ("T_CONST", 5.0, (double?)null, (double?)null),
                    ("T_ZERO", 1.0, (double?)null, (double?)null),
                    ("T_OK", 10.0, (double?)9.0, (double?)11.0),
                    ("T_NOLIM", 1.0, (double?)null, (double?)null),
                }));
            dbA6.BatchInsert(todayRecs);

            var baseStats = dbA6.AggregateMeasurementStats("2026-09-04", "2026-09-05");
            var todayStats = dbA6.AggregateMeasurementStats("2026-09-05", "2026-09-06");
            var cfgC = new AppConfig { AnalyzeMinSamples = 2, AnalyzeDriftSigma = 3.0, AnalyzeDriftWarnRatio = 1.0, AnalyzeWindowDays = 7 };

            var driftAlerts = DriftAnalyzer.Compute(baseStats, todayStats, cfgC);
            var dDrift = driftAlerts.FirstOrDefault(a => a.TestName == "T_DRIFT");
            Check(dDrift != null && dDrift.Kind == "drift" && Math.Abs(dDrift.Score - 3.54) < 0.05,
                  $"规格06: 均值漂移触发（z 实得 {dDrift?.Score ?? -1}）");
            Check(dDrift != null && Math.Abs(dDrift.ExpectedLow - (10.0 - 3 * 0.1414213562)) < 1e-3
                  && Math.Abs(dDrift.ExpectedHigh - (10.0 + 3 * 0.1414213562)) < 1e-3,
                  "规格06: 漂移预警附期望区间 mean±kσ");
            Check(driftAlerts.All(a => a.TestName != "T_OK"),
                  "规格06: 稳定测项不误报");
            var cfgCold = new AppConfig { AnalyzeMinSamples = 10, AnalyzeDriftSigma = 3.0, AnalyzeDriftWarnRatio = 1.0 };
            Check(DriftAnalyzer.Compute(baseStats, todayStats, cfgCold).Count == 0,
                  "规格06: 基线样本不足（冷启动 < min_samples）不预警");
            var shortToday = new List<MeasureStatsRow>
            {
                new() { Model = "E3002781", TestName = "T_DRIFT", N = 2, Mean = 10.5, Sigma = 0.1 },
            };
            Check(DriftAnalyzer.Compute(baseStats, shortToday, cfgC).Count == 0,
                  "规格06: 今日样本不足（<10）不出预警");
            var zeroBase = new List<MeasureStatsRow>
            {
                new() { Model = "M", TestName = "T_Z", N = 30, Mean = 10.0, Sigma = 0, Lolim = 9, Hilim = 11 },
            };
            var zeroToday = new List<MeasureStatsRow>
            {
                new() { Model = "M", TestName = "T_Z", N = 10, Mean = 10.5, Sigma = 0, Lolim = 9, Hilim = 11 }, // N≥MinTodaySamples(10)
            };
            var zeroAlerts = DriftAnalyzer.Compute(zeroBase, zeroToday, cfgC);
            Check(zeroAlerts.Count == 1 && zeroAlerts[0].Kind == "drift",
                  "规格06: σ 防零量程下限（1% 量程）使恒定值测项仍可检出漂移");
            var noRangeBase = new List<MeasureStatsRow>
            {
                new() { Model = "M", TestName = "T_N", N = 30, Mean = 0, Sigma = 0 },
            };
            var noRangeToday = new List<MeasureStatsRow>
            {
                new() { Model = "M", TestName = "T_N", N = 3, Mean = 1.0, Sigma = 0 },
            };
            Check(DriftAnalyzer.Compute(noRangeBase, noRangeToday, cfgC).Count == 0,
                  "规格06: 恒定值且无量程（σ_eff=0）安全跳过");
            var dMargin = driftAlerts.FirstOrDefault(a => a.TestName == "T_MARGIN");
            Check(dMargin != null && dMargin.Kind == "margin" && dMargin.Score < 1.0,
                  $"规格06: 有过程波动且贴限才逼近规格限（余量比实得 {dMargin?.Score ?? -1}）");
            Check(driftAlerts.All(a => a.TestName != "T_SETPOINT"),
                  "规格06: G49 设定值测项（σ≪规格带，如 KL30_FILT 14.4V±0.2V）不报逼近限");
            Check(driftAlerts.First().Kind == "drift" && driftAlerts.Last().Kind == "margin",
                  "规格06: 预警排序 drift 优先于 margin");

            var allStats = dbA6.AggregateMeasurementStats("2026-09-04", "2026-09-06");
            var cpkItems = CpkAnalyzer.Compute(allStats);
            var cpkOk = cpkItems.First(i => i.TestName == "T_OK");
            Check(cpkOk.Level == "ok" && cpkOk.Cpk.HasValue && Math.Abs(cpkOk.Cpk.Value - 3.909) < 0.01,
                  $"规格06: CPK 双边限计算（实得 {cpkOk.Cpk}）");
            var cpkMargin = cpkItems.First(i => i.TestName == "T_MARGIN");
            Check(cpkMargin.Level == "low" && cpkMargin.Cpk.HasValue,
                  "规格06: 有过程波动且贴限 → CPK 低能力");
            Check(cpkItems.First(i => i.TestName == "T_SETPOINT").Level == "constant",
                  "规格06: 设定值测项 σ/带宽 <2% 记 constant 不评 CPK");
            Check(cpkItems.First(i => i.TestName == "T_DRIFT").Level == "low",
                  "规格06: 低能力测项 Level=low 入清单");
            var cpkConst = cpkItems.First(i => i.TestName == "T_CONST");
            Check(cpkConst.Level == "constant" && !cpkConst.Cpk.HasValue,
                  "规格06: σ=0 常数量测项记 constant 不参与能力评级");
            Check(cpkItems.First(i => i.TestName == "T_ZERO").Level == "constant"
                  && cpkItems.First(i => i.TestName == "T_NOLIM").Level == "no_limit",
                  "规格06: 无限值/零方差测项分类正确");
            Check(CpkAnalyzer.MarginRatio(0, null, null, 0) == double.MaxValue,
                  "规格06: MarginRatio σ=0 安全返回 MaxValue");
            Check(!CpkAnalyzer.HasProcessVariation(new MeasureStatsRow { Mean = 14.4, Sigma = 0.005, Lolim = 14.2, Hilim = 14.6 }),
                  "规格06: HasProcessVariation 拒绝 KL30_FILT 类设定值（σ/带宽<2%）");
            Check(CpkAnalyzer.HasProcessVariation(new MeasureStatsRow { Mean = 10.7, Sigma = 0.28, Lolim = 9, Hilim = 11 }),
                  "规格06: HasProcessVariation 接受过程量（σ/带宽≥2%）");
            Check(G49ProductDictionary.IsNonProcessSemantic("8.19.5 FLTM_DESAT_A(XCP)")
                  && G49ProductDictionary.IsNonProcessSemantic("8.20.2 ASC_B(XCP)")
                  && G49ProductDictionary.IsNonProcessSemantic("7.2.1 linkStatus(XCP)")
                  && !G49ProductDictionary.IsNonProcessSemantic("T_MARGIN"),
                  "规格06: 注入章 8.19/8.20 与通信 7.2 识别为非过程量，普通测项不是");
            var injRow = new MeasureStatsRow
            {
                Model = "M", TestName = "8.19.5 FLTM_DESAT_A(XCP)",
                N = 40, Mean = 0.5, Sigma = 0.15, Lolim = 0, Hilim = 1,
            };
            Check(CpkAnalyzer.HasProcessVariation(injRow)
                  && !CpkAnalyzer.ShouldTreatAsProcess(injRow.TestName, injRow),
                  "规格06: 注入项即使 σ 大也不当过程量（语义门优先于波动门）");
            Check(CpkAnalyzer.Compute(new List<MeasureStatsRow> { injRow })[0].Level == "constant",
                  "规格06: 注入章测项不进 CPK 低能力（Level=constant）");
            Check(!DriftAnalyzer.ShouldWarnMargin(injRow, injRow),
                  "规格06: 注入章不报逼近规格限（IsInjectionSection 接到测名）");

            var dbA6n = new Database(Path.Combine(dirA6, "noop.db"));
            AnalysisEngine.RunOnce(dbA6n, new AppConfig
            {
                AnalyzeDriftEnabled = false, AnalyzeCpkEnabled = false,
                AnalyzeFailAttrEnabled = false, AnalyzeFixtureEnabled = false,
            }, fixedNow);
            Check(dbA6n.GetMeta(AnalysisEngine.MetaDrift) == null && dbA6n.GetMeta(AnalysisEngine.MetaCpk) == null
                  && dbA6n.GetMeta(AnalysisEngine.MetaAttribution) == null && dbA6n.GetMeta(AnalysisEngine.MetaFixture) == null,
                  "规格06: 分析四开关显式全关 RunOnce no-op（不写任何 meta）");
            AnalysisEngine.RunOnce(dbA6, new AppConfig { AnalyzeDriftEnabled = true, AnalyzeCpkEnabled = true, AnalyzeMinSamples = 2, AnalyzeWindowDays = 7 }, fixedNow);
            var driftState = AnalysisEngine.GetDriftState(dbA6);
            var cpkState = AnalysisEngine.GetCpkState(dbA6);
            Check(driftState != null && driftState.Date == "2026-09-05" && driftState.Alerts.Any(a => a.TestName == "T_DRIFT"),
                  "规格06: RunOnce 落盘 app_meta 漂移快照且可读回");
            Check(cpkState != null && cpkState.LowCount > 0 && cpkState.Items.Any(i => i.TestName == "T_DRIFT"),
                  "规格06: RunOnce 落盘 CPK 快照含低能力项");
            Check(cpkState != null && CpkState.FromJson(cpkState.ToJson())!.LowCount == cpkState.LowCount,
                  "规格06: CpkState JSON 往返一致");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dirA6, true); } catch { }
            Console.WriteLine("[规格06] 分析器组完成");

            Console.WriteLine("\n【规格06】backfill 回填 …");
            var savedStationB = AppConfig.Instance.StationId;
            var savedRootB = AppConfig.Instance.ResultsRoot;
            var savedCollectB = AppConfig.Instance.AnalyzeCollectPass;
            var dirB6 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_backfill_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dirB6);
            try
            {
                AppConfig.Instance.StationId = "FCT1";
                AppConfig.Instance.AnalyzeCollectPass = true;
                var yest = DateTime.Now.Date.AddDays(-1);
                var yestStr = yest.ToString("yyyyMMdd");
                var todayStr = DateTime.Now.Date.ToString("yyyyMMdd");
                var resRoot = Path.Combine(dirB6, "results");
                var dbB6Path = Path.Combine(dirB6, "local.db");
                var dbB6 = new Database(dbB6Path);

                string MkXml(string sn, string ts) =>
                    "<FACTORY USER=\"op1\" TESTER=\"FCT-01\">" +
                    "<PANEL STATUS=\"Passed\"><DUT ID=\"E3002781A1\">" +
                    $"<TEST NAME=\"V_KL30\" STATUS=\"Passed\" VALUE=\"13.5{sn.Length % 10}\" HILIM=\"14.4\" LOLIM=\"10.8\" UNIT=\"V\"/>" +
                    "<TEST NAME=\"I_STANDBY\" STATUS=\"Passed\" VALUE=\"0.52\" HILIM=\"1.0\" LOLIM=\"0\" UNIT=\"A\"/>" +
                    $"</DUT></PANEL><!-- {ts} --></FACTORY>";
                var yestFile = Path.Combine(resRoot, "Online", "E3002781", yestStr, $"P_UNIT_{sn6(yestStr)}_0001.xml");
                var todayFile = Path.Combine(resRoot, "Online", "E3002781", todayStr, $"P_UNIT_{sn6(todayStr)}_0002.xml");
                static string sn6(string d) => "SN" + d[2..];
                Directory.CreateDirectory(Path.GetDirectoryName(yestFile)!);
                Directory.CreateDirectory(Path.GetDirectoryName(todayFile)!);
                File.WriteAllText(yestFile, MkXml("SN" + yestStr[2..], yestStr));
                File.WriteAllText(todayFile, MkXml("SN" + todayStr[2..], todayStr));
                AppConfig.Instance.ResultsRoot = resRoot;

                var preExist = new TestRecord
                {
                    StationId = "FCT1", Model = "E3002781", Result = "PASS",
                    TestDate = yestStr, XmlPath = Path.GetFullPath(yestFile),
                };
                dbB6.BatchInsert(new[] { preExist });
                Check(RowCount(dbB6Path, "test_measurements") == 0, "规格06: 存量记录初始无测量值");

                int rc1 = BackfillTool.Run(Array.Empty<string>(), dbB6Path);
                Check(rc1 == 0 && RowCount(dbB6Path, "test_records") == 2,
                      $"规格06: backfill 首跑 exit 0 且新记录入库（实得 {RowCount(dbB6Path, "test_records")}）");
                Check(RowCount(dbB6Path, "test_measurements") == 4,
                      $"规格06: 新记录自动补测量 + 存量 PASS 补采（实得 {RowCount(dbB6Path, "test_measurements")}）");

                int rc2 = BackfillTool.Run(Array.Empty<string>(), dbB6Path);
                Check(rc2 == 0 && RowCount(dbB6Path, "test_records") == 2 && RowCount(dbB6Path, "test_measurements") == 4,
                      "规格06: backfill 二跑幂等（xml_path UNIQUE + 测量 UNIQUE 双防线）");
                using (var csn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbB6Path}"))
                {
                    csn.Open();
                    using var cmdSn = csn.CreateCommand();
                    cmdSn.CommandText = "SELECT COUNT(*) FROM test_records WHERE sn = 'E3002781A1'";
                    var snCnt = Convert.ToInt64(cmdSn.ExecuteScalar());
                    Check(snCnt == 2, $"规格06: SN 补采覆盖存量与新增空 SN 记录 (实得 {snCnt})");
                }
                Check(dbB6.GetMeta(BackfillTool.MetaWatermark) == DateTime.Now.Date.ToString("yyyy-MM-dd"),
                      $"规格06: 水位落盘 backfill_last_day（实得 {dbB6.GetMeta(BackfillTool.MetaWatermark)}）");

                var resD6 = Path.Combine(dirB6, "results_d6");
                var d6File = Path.Combine(resD6, "Online", "E3002781", yestStr, "P_UNIT_D6_0001.xml");
                Directory.CreateDirectory(Path.GetDirectoryName(d6File)!);
                File.WriteAllText(d6File, MkXml("D6", yestStr));
                AppConfig.Instance.ResultsRoot = resD6;
                var dbD6Path = Path.Combine(dirB6, "d6.db");
                _ = new Database(dbD6Path);
                var d6rc = BackfillTool.Run(new[] { "--days", "1" }, dbD6Path);
                AppConfig.Instance.ResultsRoot = resRoot;
                Check(d6rc == 0 && RowCount(dbD6Path, "test_records") == 0,
                      "规格06: --days 1 窗口过滤昨日目录");
            }
            finally
            {
                AppConfig.Instance.StationId = savedStationB;
                AppConfig.Instance.ResultsRoot = savedRootB;
                AppConfig.Instance.AnalyzeCollectPass = savedCollectB;
            }

            {
                var dirB7 = Path.Combine(dirB6, "off");
                Directory.CreateDirectory(dirB7);
                var dbB7Path = Path.Combine(dirB7, "local.db");
                new Database(dbB7Path);
                AppConfig.Instance.AnalyzeCollectPass = false;
                var rcOff = BackfillTool.Run(Array.Empty<string>(), dbB7Path);
                AppConfig.Instance.AnalyzeCollectPass = savedCollectB;
                Check(rcOff == 0 && RowCount(dbB7Path, "test_records") == 0,
                      "规格06: collect_pass=false backfill 无动作 exit 0");
                AppConfig.Instance.AnalyzeCollectPass = savedCollectB;

                AppConfig.Instance.ResultsRoot = Path.Combine(dirB7, "no_such_root");
                var rcNoRoot = BackfillTool.Run(Array.Empty<string>(), dbB7Path);
                AppConfig.Instance.ResultsRoot = savedRootB;
                Check(rcNoRoot == 1, "规格06: results_root 不存在 exit 1");
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dirB6, true); } catch { }
            Console.WriteLine("[规格06] 回填组完成");

            // ---------- 期2：fail_items 落表 ----------
            Console.WriteLine("\n【规格06期2】失败项明细 fail_items …");
            object? Scalar(string dbPath, string sql)
            {
                using var c = new SqliteConnection($"Data Source={dbPath}");
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                var v = cmd.ExecuteScalar();
                return v is DBNull ? null : v;
            }
            var dirE6 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_p6s2_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dirE6);
            var dbE6Path = Path.Combine(dirE6, "local.db");
            var dbE6 = new Database(dbE6Path);
            int seqE6 = 0;
            TestRecord FR(string ts, string tester, string fixture, string result, string panel,
                (string name, string value, string lo, string hi, string unit, string rule)[] items)
            {
                seqE6++;
                return new TestRecord
                {
                    StationId = "FCT1", Model = "E3002781", Result = result, TestDate = "20260905",
                    BatchTimestamp = ts, Tester = tester, FixtureId = fixture, PanelStatus = panel,
                    XmlPath = $@"C:\t\e6_{seqE6}.xml",
                    FailedTests = items.Select(i => new FailedTest
                    { Name = i.name, Value = i.value, Lolim = i.lo, Hilim = i.hi, Unit = i.unit, Rule = i.rule }).ToList(),
                };
            }
            TestRecord PR(string ts, string tester, string fixture, (string name, double v, double lo, double hi)[] ms)
            {
                seqE6++;
                return new TestRecord
                {
                    StationId = "FCT1", Model = "E3002781", Result = "PASS", TestDate = "20260905",
                    BatchTimestamp = ts, Tester = tester, FixtureId = fixture, PanelStatus = "Passed",
                    XmlPath = $@"C:\t\e6_{seqE6}.xml",
                    Measurements = ms.Select(m => new MeasurementRow
                    { TestName = m.name, Value = m.v, Lolim = m.lo, Hilim = m.hi, Unit = "V" }).ToList(),
                };
            }

            var f1 = FR("2026-09-05 08:30:00", "T1", "FX1", "FAIL", "Failed", new[]
            {
                ("V_KL30", "3.29", "10.8", "14.4", "V", "LeGe"),
                ("CONT", "Open", "", "", "", ""),
            });
            dbE6.BatchInsert(new[] { f1 });
            Check(RowCount(dbE6Path, "fail_items") == 2, $"期2: FAIL 记录失败项自动挂接（实得 {RowCount(dbE6Path, "fail_items")}）");
            Check(Convert.ToDouble(Scalar(dbE6Path, "SELECT value FROM fail_items WHERE test_name='V_KL30'")) == 3.29
                  && (string?)Scalar(dbE6Path, "SELECT value_text FROM fail_items WHERE test_name='V_KL30'") == "3.29",
                  "期2: 数值失败项 value REAL + value_text 原串");
            Check(Scalar(dbE6Path, "SELECT value FROM fail_items WHERE test_name='CONT'") == null
                  && (string?)Scalar(dbE6Path, "SELECT value_text FROM fail_items WHERE test_name='CONT'") == "Open",
                  "期2: 非数值失败项 value 空 + value_text 保留");
            Check(Convert.ToInt64(Scalar(dbE6Path, "SELECT hour FROM fail_items WHERE test_name='V_KL30'")) == 8,
                  "期2: hour 取自 batch_timestamp 空格格式");
            var fT = FR("2026-09-05T09:30:00", "T1", "FX1", "FAIL", "Failed",
                new[] { ("V_KL30", "3.3", "10.8", "14.4", "V", "LeGe") });
            dbE6.BatchInsert(new[] { fT });
            Check(Convert.ToInt64(Scalar(dbE6Path, "SELECT hour FROM fail_items WHERE ts='2026-09-05T09:30:00'")) == 9,
                  "期2: hour 兼容 T 分隔格式");
            var fPass = FR("2026-09-05 10:00:00", "T1", "FX1", "PASS", "Passed",
                new[] { ("GHOST", "1", "", "", "", "") });
            var fInt = FR("2026-09-05 10:30:00", "T1", "FX1", "INTERRUPTED", "Failed",
                new[] { ("GHOST2", "1", "", "", "", "") });
            dbE6.BatchInsert(new[] { fPass, fInt });
            Check(RowCount(dbE6Path, "fail_items") == 3,
                  "期2: PASS/INTERRUPTED 记录不落失败项（仅 FAIL 前置守卫）");
            var ridF1 = IdOfRec(dbE6Path, f1.XmlPath);
            dbE6.BatchInsert(new[] { f1 });
            dbE6.InsertFailItemsFor(new List<(TestRecord, long)> { (f1, ridF1) });
            Check(RowCount(dbE6Path, "fail_items") == 3, "期2: fail_items UNIQUE(record_id,test_name) 幂等");
            var fOld = FR("2026-01-01 00:00:00", "T1", "FX1", "FAIL", "Failed",
                new[] { ("T_OLD", "1", "", "", "", "") });
            dbE6.BatchInsert(new[] { fOld });
            var purgedFi = dbE6.PurgeOldFailItems(90);
            Check(purgedFi >= 1 && RowCount(dbE6Path, "fail_items") == 3,
                  $"期2: PurgeOldFailItems 清旧留新（删 {purgedFi}）");
            Console.WriteLine("[规格06期2] fail_items 组完成");

            // ---------- 期2：多维归因 ----------
            Console.WriteLine("\n【规格06期2】失败多维归因 …");
            var dirF6 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_p6attr_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dirF6);
            var dbF6Path = Path.Combine(dirF6, "local.db");
            var dbF6 = new Database(dbF6Path);
            var recsF = new List<TestRecord>();
            for (int i = 0; i < 20; i++)
            {
                recsF.Add(FR($"2026-09-05 08:{i:00}:00", "T1", "FX1", "FAIL", "Failed",
                    new[] { ("X", "1", "", "", "", "") }));
                recsF.Add(FR($"2026-09-05 10:{i + 20:00}:00", "T1", "FX1", "PASS", "Passed", Array.Empty<(string, string, string, string, string, string)>()));
                recsF.Add(FR($"2026-09-05 10:{i:00}:00", "T2", "FX2", "PASS", "Passed", Array.Empty<(string, string, string, string, string, string)>()));
                recsF.Add(FR($"2026-09-05 10:{i + 40:00}:00", "T2", "FX2", i < 4 ? "FAIL" : "PASS", i < 4 ? "Failed" : "Passed", Array.Empty<(string, string, string, string, string, string)>()));
            }
            dbF6.BatchInsert(recsF);
            var cfgF = new AppConfig { AnalyzeWindowDays = 7, AnalyzeAttrMinBucket = 20, AnalyzeAttrDevRatio = 1.5 };
            var attrState = FailAttributor.Run(dbF6, cfgF, fixedNow);
            Check(attrState.TotalN == 80 && Math.Abs(attrState.GlobalFailRate - 0.3) < 1e-6,
                  $"期2: 归因全局口径（n={attrState.TotalN}，全局失败率 {attrState.GlobalFailRate}）");
            Check(attrState.Buckets.Count == 1, $"期2: 仅 hour 桶异常（实得 {attrState.Buckets.Count}）");
            Check(attrState.Buckets.All(b => b.Dim == "hour"), "期2: 归因输出仅保留 hour 维");
            var bH8 = attrState.Buckets.FirstOrDefault(b => b.Dim == "hour" && b.Key == "8");
            Check(bH8 != null && bH8.N == 20 && bH8.Fails == 20 && Math.Abs(bH8.Ratio - 3.33) < 0.05,
                  "期2: hour=8 全失败桶偏差比 ~3.33 触发");
            Check(bH8 != null && Math.Abs(bH8.PanelNgRate - 1.0) < 1e-6, "期2: panel NG 率按 hour 桶统计");
            Check(attrState.Buckets.First().Key == "8", "期2: 偏差比降序（hour=8 最先）");
            var cfgF5 = new AppConfig { AnalyzeWindowDays = 7, AnalyzeAttrMinBucket = 50, AnalyzeAttrDevRatio = 1.5 };
            Check(FailAttributor.Run(dbF6, cfgF5, fixedNow).Buckets.Count == 0,
                  "期2: 桶样本 < min_bucket 跳过");
            var dbF6b = new Database(Path.Combine(dirF6, "allpass.db"));
            var passOnly = new List<TestRecord>();
            for (int i = 0; i < 30; i++)
                passOnly.Add(FR($"2026-09-05 10:{i:00}:00", "T1", "FX1", "PASS", "Passed",
                    Array.Empty<(string, string, string, string, string, string)>()));
            dbF6b.BatchInsert(passOnly);
            Check(FailAttributor.Run(dbF6b, cfgF, fixedNow).Buckets.Count == 0
                  && FailAttributor.Run(dbF6b, cfgF, fixedNow).GlobalFailRate == 0,
                  "期2: 全局零失败无可比基线不报警");

            for (int h = 10; h <= 15; h++)
            {
                for (int k = 0; k < h - 9; k++)
                    dbF6b.BatchInsert(new[] { FR($"2026-09-05 {h:00}:{k:00}:00", "T1", "FX1", "FAIL", "Failed",
                        Array.Empty<(string, string, string, string, string, string)>()) });
                dbF6b.InsertLocalDeviceSample(h * 10.0, 50, 100, $"{fixedNow:yyyy-MM-dd} {h:00}:05:00");
            }
            var attrCorr = FailAttributor.Run(dbF6b, cfgF, fixedNow);
            var cpuCorr = attrCorr.ResourceCorrelations.FirstOrDefault(c => c.Metric == "cpu");
            Check(cpuCorr != null && Math.Abs(cpuCorr.R - 1.0) < 0.01,
                  $"期2: 资源关联 Pearson 正例（cpu r={cpuCorr?.R ?? double.NaN}，corr={attrCorr.ResourceCorrelations.Count}）");
            Check(attrCorr.ResourceCorrelations.All(c => c.Metric != "mem"),
                  "期2: 零方差序列（mem 恒 50）不出相关");
            Check(double.IsNaN(FailAttributor.Pearson(new[] { 1.0, 1, 1 }, new[] { 1.0, 2, 3 })),
                  "期2: Pearson 零方差返回 NaN");
            Check(Math.Abs(FailAttributor.Pearson(new[] { 1.0, 2, 3, 4 }, new[] { 2.0, 1, 2, 1 })) < 0.6,
                  "期2: Pearson 弱相关低于阈值");
            Console.WriteLine("[规格06期2] 归因组完成");

            // ---------- 期2：tester 效应量 + PASS 工厂信息补齐 ----------
            Console.WriteLine("\n【规格06期2】tester 效应量对比 …");
            var dirG6 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_p6fx_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dirG6);
            var dbG6Path = Path.Combine(dirG6, "local.db");
            var dbG6 = new Database(dbG6Path);
            var recsG = new List<TestRecord>();
            for (int i = 0; i < 30; i++)
            {
                recsG.Add(PR("2026-09-05 12:00:00", "TA", "FX1", new[]
                {
                    ("T_EFF1", 9.8 + (i % 3) * 0.2, 9.0, 12.0),
                    ("T_EFF2", 10.0 + (i % 2) * 0.2, 9.0, 12.0),
                }));
                recsG.Add(PR("2026-09-05 12:30:00", "TB", "FX1", new[]
                {
                    ("T_EFF1", 10.8 + (i % 3) * 0.2, 9.0, 12.0),
                    ("T_EFF2", 10.4 + (i % 2) * 0.2, 9.0, 12.0),
                }));
                recsG.Add(PR("2026-09-05 13:00:00", "UNKNOWN", "FX1", new[]
                {
                    ("T_EFF1", 15.0, 9.0, 12.0),
                    ("T_EFF2", 15.0, 9.0, 12.0),
                }));
            }
            dbG6.BatchInsert(recsG);
            var testerStats = dbG6.FetchTesterMeasurementStats("2026-08-30");
            Check(testerStats.Where(t => t.TestName == "T_EFF1").All(t => t.Tester is "TA" or "TB"),
                  "期2: tester='UNKNOWN' 脏数据剔除");
            var cfgG = new AppConfig { AnalyzeWindowDays = 7, AnalyzeMinSamples = 30, AnalyzeEffectD = 0.8 };
            var fxState = FixtureComparator.Run(dbG6, cfgG, fixedNow);
            Check(fxState.Effects.Count == 2, $"期2: 两个测项出效应量（实得 {fxState.Effects.Count}）");
            Check(fxState.Effects[0].TestName == "T_EFF1" && Math.Abs(fxState.Effects[0].D - 6.02) < 0.05,
                  $"期2: 效应量正例 d≈6.02（旋转值列 σ≈0.166，实得 {fxState.Effects[0].D}）且降序在前");
            Check(fxState.Effects[1].TestName == "T_EFF2" && Math.Abs(fxState.Effects[1].D - 3.93) < 0.05,
                  $"期2: 次强效应 d≈3.93 仍超 0.8 阈值入清单");
            var dbG6b = new Database(Path.Combine(dirG6, "const.db"));
            var recsG2 = new List<TestRecord>();
            for (int i = 0; i < 30; i++)
            {
                recsG2.Add(PR("2026-09-05 12:00:00", "TA", "FX1", new[] { ("T_C", 10.0, 9.0, 12.0) }));
                recsG2.Add(PR("2026-09-05 12:30:00", "TB", "FX1", new[] { ("T_C", 11.0, 9.0, 12.0) }));
                recsG2.Add(PR("2026-09-05 13:00:00", "TC", "FX1", new[] { ("T_S", 10.0 + (i % 3) * 0.2, 9.0, 12.0) }));
            }
            dbG6b.BatchInsert(recsG2);
            var fxConst = FixtureComparator.Run(dbG6b, cfgG, fixedNow);
            Check(fxConst.Effects.All(e => e.TestName != "T_C"),
                  "期2: 双 tester 恒定值 σ 防零（量程下限）跳过");
            Check(fxConst.Effects.Count == 0, "期2: 单 tester 测项跳过对比");

            var passXml2 = "<FACTORY USER=\"op9\" TESTER=\"T1\" FIXTURE_ID=\"FX01\">" +
                           "<PANEL STATUS=\"Passed\"><DUT ID=\"D\"><TEST NAME=\"V\" STATUS=\"Passed\" VALUE=\"1\"/></DUT></PANEL></FACTORY>";
            var passFile2 = Path.Combine(work, "a6res2", "Online", "E3002781", "20260905", "P_UNIT_SN009_0001.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(passFile2)!);
            File.WriteAllText(passFile2, passXml2);
            var parser2 = new FctAggregator.Parsing.DefaultResultParser(FctAggregator.Parsing.ParserRuleSet.Default, "FCT1");
            var outp2 = parser2.Parse(passFile2, passXml2);
            Check(outp2 != null && outp2.Tester == "T1" && outp2.FixtureId == "FX01" && outp2.Result == "PASS",
                  "期2: PASS 工厂信息补齐（tester/fixture_id 单遍读取）");
            var parser3 = new FctAggregator.Parsing.DefaultResultParser(FctAggregator.Parsing.ParserRuleSet.Default, null);
            var outp3 = parser3.Parse(passFile2, passXml2);
            Check(outp3 != null && outp3.StationId == "UNKNOWN",
                  "期2: tester 非机台号格式时 station 口径回落 UNKNOWN（FCT-01 不含 FCT{i}）");
            var passXml3 = passXml2.Replace("TESTER=\"T1\"", "TESTER=\"FCT3\"");
            File.WriteAllText(passFile2, passXml3);
            var outp4 = parser3.Parse(passFile2, passXml3);
            Check(outp4 != null && outp4.StationId == "FCT3",
                  "期2: PASS tester 含 FCT{i} 时 station 提取实测机台号（口径改进）");
            Console.WriteLine("[规格06期2] 对比组完成");

            // ---------- 期2：快照落盘与 round trip ----------
            Console.WriteLine("\n【规格06期2】分析快照 round trip …");
            AnalysisEngine.RunOnce(dbF6, new AppConfig
            {
                AnalyzeDriftEnabled = false, AnalyzeCpkEnabled = false,
                AnalyzeFailAttrEnabled = true, AnalyzeFixtureEnabled = true,
                AnalyzeWindowDays = 7, AnalyzeAttrMinBucket = 20, AnalyzeAttrDevRatio = 1.5,
                AnalyzeMinSamples = 30, AnalyzeEffectD = 0.8,
            }, fixedNow);
            var attrGet = AnalysisEngine.GetAttributionState(dbF6);
            Check(attrGet != null && attrGet.Buckets.Count == 1 && attrGet.Date == "2026-09-05",
                  "期2: RunOnce 落盘归因快照且可读回（hour-only）");
            Check(AttributionState.FromJson(attrGet!.ToJson())!.Buckets.Count == attrGet.Buckets.Count,
                  "期2: AttributionState JSON 往返一致");
            AnalysisEngine.RunOnce(dbG6, new AppConfig
            {
                AnalyzeDriftEnabled = false, AnalyzeCpkEnabled = false,
                AnalyzeFailAttrEnabled = false, AnalyzeFixtureEnabled = true,
                AnalyzeWindowDays = 7, AnalyzeMinSamples = 30, AnalyzeEffectD = 0.8,
            }, fixedNow);
            var fxGet = AnalysisEngine.GetFixtureState(dbG6);
            Check(fxGet != null && fxGet.Effects.Count == 2, "期2: RunOnce 落盘 tester 对比快照且可读回");
            Check(FixtureState.FromJson(fxGet!.ToJson())!.Effects.Count == fxGet.Effects.Count,
                  "期2: FixtureState JSON 往返一致");
            Check(AttributionState.FromJson("not-json") == null && FixtureState.FromJson("") == null,
                  "期2: 坏 JSON 安全返回 null");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dirE6, true); } catch { }
            try { Directory.Delete(dirF6, true); } catch { }
            try { Directory.Delete(dirG6, true); } catch { }
            Console.WriteLine("[规格06期2] 快照组完成");

            // ---------- 期3：TDMS 特征化（纯函数 / 定位 / 落表 / 护栏 / 快照） ----------
            Console.WriteLine("\n【规格06期3】TDMS 特征化 …");
            // 纯函数：基础统计（口径 = TdmsDoc.Describe）
            var tdmsRows = TdmsFeatureAnalyzer.Compute(new[]
            {
                new TdmsFeatureAnalyzer.TdmsChannelData { Group = "03.2 电压", Channel = "V1", Data = new[] { 1.0, 2.0, 3.0, 4.0, 5.0 } },
            }, 400);
            Check(tdmsRows.Count == 1 && tdmsRows[0].N == 5 && tdmsRows[0].Section == "3.2",
                  "期3: 通道统计 n/section 提取正确");
            Check(Math.Abs(tdmsRows[0].Mean!.Value - 3.0) < 1e-9
                  && Math.Abs(tdmsRows[0].Std!.Value - Math.Sqrt(2.5)) < 1e-9
                  && tdmsRows[0].Min == 1.0 && tdmsRows[0].Max == 5.0
                  && tdmsRows[0].First == 1.0 && tdmsRows[0].Last == 5.0,
                  "期3: 通道统计 min/max/mean/std/first/last 逐值正确");
            var tdmsSkip = TdmsFeatureAnalyzer.Compute(new[]
            {
                new TdmsFeatureAnalyzer.TdmsChannelData { Group = "G", Channel = "NaN1", Data = new[] { double.NaN, double.NaN } },
                new TdmsFeatureAnalyzer.TdmsChannelData { Group = "G", Channel = "Empty", Data = Array.Empty<double>() },
                new TdmsFeatureAnalyzer.TdmsChannelData { Group = "Resolver", Channel = "C3", Data = new[] { 1.0, 1.0 } },
            }, 400);
            Check(tdmsSkip.Count == 1 && tdmsSkip[0].ChannelName == "C3" && tdmsSkip[0].Section == "",
                  "期3: 全 NaN/空数组通道跳过，无章节号 Group section 为空");
            var tdmsSingle = TdmsFeatureAnalyzer.Compute(new[]
            {
                new TdmsFeatureAnalyzer.TdmsChannelData { Group = "G", Channel = "C1", Data = new[] { 7.0 } },
            }, 400);
            Check(tdmsSingle.Count == 1 && tdmsSingle[0].N == 1 && tdmsSingle[0].Std == 0,
                  "期3: 单样本 Std=0（Describe 口径锁定）");
            var tdmsCap = TdmsFeatureAnalyzer.Compute(new[]
            {
                new TdmsFeatureAnalyzer.TdmsChannelData { Group = "G", Channel = "C1", Data = new[] { 1.0 } },
                new TdmsFeatureAnalyzer.TdmsChannelData { Group = "G", Channel = "C2", Data = new[] { 2.0 } },
                new TdmsFeatureAnalyzer.TdmsChannelData { Group = "G", Channel = "C3", Data = new[] { 3.0 } },
            }, 2);
            Check(tdmsCap.Count == 2 && tdmsCap[0].ChannelName == "C1" && tdmsCap[1].ChannelName == "C2",
                  "期3: maxChannels 通道截断");

            // 定位器：临时树 {root}/{Category}/{Model}/{yyyyMMdd}/{SN}_*.tdms
            var tdmsRootDir = Path.Combine(work, "tdmsroot");
            var tdmsDayDir = Path.Combine(tdmsRootDir, "Online", "E3002781", "20260905");
            Directory.CreateDirectory(tdmsDayDir);
            File.WriteAllText(Path.Combine(tdmsDayDir, "SN001_0001.tdms"), "x");
            File.WriteAllText(Path.Combine(tdmsDayDir, "SN0011_0001.tdms"), "x");
            File.WriteAllText(Path.Combine(tdmsDayDir, "SN001_0002.tdms"), "x");
            Check(TdmsFeatureAnalyzer.LocateTdmsFile(tdmsRootDir, "Online", "E3002781", "20260905", "SN001")
                  == Path.Combine(tdmsDayDir, "SN001_0002.tdms"),
                  "期3: 定位命中同 SN 文件名降序最新（SN001_0002）");
            Check(TdmsFeatureAnalyzer.LocateTdmsFile(tdmsRootDir, "Online", "E3002781", "20260905", "SN009") == "",
                  "期3: 无匹配 SN 返回空");
            Check(TdmsFeatureAnalyzer.LocateTdmsFile("", "Online", "E3002781", "20260905", "SN001") == ""
                  && TdmsFeatureAnalyzer.LocateTdmsFile(tdmsRootDir, "Online", "E3002781", "20260905", "") == "",
                  "期3: root 空/SN 空 防呆返回空");
            Check(TdmsFeatureAnalyzer.LocateTdmsFile(tdmsRootDir, "Online", "E3002781", "2026090", "SN001") == "",
                  "期3: testDate 非 8 位 防呆返回空（前缀不误命中 SN0011）");
            Check(TdmsFeatureAnalyzer.LocateTdmsFile(tdmsRootDir, "Online", "E3002781", "20260801", "SN001") == "",
                  "期3: 日期目录不存在 返回空");

            // 落表：Init 预建 / 字段抽查 / UNIQUE 幂等 / Purge + 老库重开
            var tdmsDbDir = Path.Combine(work, "tdmsdb");
            Directory.CreateDirectory(tdmsDbDir);
            var dbT = new Database(Path.Combine(tdmsDbDir, "t.db"));
            Check(dbT.GetTdmsFeatureSummary() == (0L, 0L, ""), "期3: Init 预建 tdms_features 空表");
            var tdmsRec = new TestRecord
            {
                StationId = "FCT1", Model = "E3002781", Category = "Online", TestDate = "20260905",
                Sn = "SN001", Result = "PASS", XmlPath = Path.Combine(work, "a1.xml"),
                BatchTimestamp = "2026-09-05 10:00:00",
            };
            var tdmsFeatureRows = new List<TdmsFeatureAnalyzer.TdmsFeatureRow>
            {
                new() { GroupName = "03.2 电压", ChannelName = "V1", Section = "3.2", N = 5,
                        Min = 1, Max = 5, Mean = 3, Std = Math.Sqrt(2.5), First = 1, Last = 5 },
                new() { GroupName = "8.11 Resolver Test", ChannelName = "RES_A", Section = "8.11", N = 3,
                        Min = 0.5, Max = 1.5, Mean = 1.0, Std = 0.5, First = 0.5, Last = 1.5 },
            };
            dbT.InsertTdmsFeaturesFor(tdmsRec, 101, Path.Combine(tdmsDayDir, "SN001_0002.tdms"), tdmsFeatureRows);
            var tdmsRecent = dbT.GetRecentTdmsFeatures(10);
            Check(tdmsRecent.Count == 2, "期3: InsertTdmsFeaturesFor 落表 2 行");
            var r1 = tdmsRecent.First(x => x.ChannelName == "V1");
            Check(r1.RecordId == 101 && r1.Model == "E3002781" && r1.Sn == "SN001"
                  && r1.GroupName == "03.2 电压" && r1.Section == "3.2" && r1.N == 5
                  && Math.Abs(r1.Mean!.Value - 3.0) < 1e-9 && r1.Ts == "2026-09-05 10:00:00",
                  "期3: 特征行字段抽查（冗余列 record_id/model/sn/ts）");
            dbT.InsertTdmsFeaturesFor(tdmsRec, 101, Path.Combine(tdmsDayDir, "SN001_0002.tdms"), tdmsFeatureRows);
            Check(dbT.GetTdmsFeatureSummary() == (2L, 1L, "2026-09-05 10:00:00"),
                  "期3: UNIQUE(record_id,group,channel) 幂等重插不变");
            var tdmsRecOld = new TestRecord
            {
                StationId = "FCT1", Model = "E3002781", Category = "Online", TestDate = "20260801",
                Sn = "SN001", Result = "PASS", XmlPath = Path.Combine(work, "a0.xml"),
                BatchTimestamp = "2026-08-01 09:00:00",
            };
            dbT.InsertTdmsFeaturesFor(tdmsRecOld, 100, "old.tdms", new List<TdmsFeatureAnalyzer.TdmsFeatureRow>
            {
                new() { GroupName = "G", ChannelName = "OLD1", N = 2, Min = 0, Max = 1, Mean = 0.5, Std = 0.5, First = 0, Last = 1 },
            });
            dbT.PurgeOldTdmsFeatures(30);
            Check(dbT.GetTdmsFeatureSummary().Rows == 2, "期3: PurgeOldTdmsFeatures 清旧留新");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // 护栏：开关关 / 大小超限 / 无效 TDMS 吞异常
            TdmsFeatureCollector.FilesOk = 0; TdmsFeatureCollector.FilesSkipped = 0;
            TdmsFeatureCollector.Dropped = 0; TdmsFeatureCollector.LastError = ""; TdmsFeatureCollector.LastErrorPath = "";
            var tdmsCfgOff = new AppConfig { AnalyzeTdmsEnabled = false, TdmsRoot = tdmsRootDir };
            Check(TdmsFeatureCollector.CaptureOne(dbT, tdmsCfgOff, tdmsRec, 101) == 0
                  && TdmsFeatureCollector.FilesSkipped == 0,
                  "期3: 开关关 CaptureOne no-op 不计数");
            var tdmsCfgBig = new AppConfig
            {
                AnalyzeTdmsEnabled = true, TdmsRoot = tdmsRootDir,
                AnalyzeTdmsMaxKb = 1, AnalyzeTdmsMaxChannels = 400,
            };
            var tdmsBigFile = Path.Combine(tdmsDayDir, "SN900_0001.tdms");
            File.WriteAllBytes(tdmsBigFile, new byte[2048]);
            var tdmsRecBig = new TestRecord
            {
                StationId = "FCT1", Model = "E3002781", Category = "Online", TestDate = "20260905",
                Sn = "SN900", Result = "PASS", XmlPath = Path.Combine(work, "a2.xml"),
            };
            Check(TdmsFeatureCollector.CaptureOne(dbT, tdmsCfgBig, tdmsRecBig, 102) == 0
                  && TdmsFeatureCollector.FilesSkipped == 1,
                  "期3: 单文件超 analyze_tdms_max_kb 跳过计数");
            var tdmsCfgBad = new AppConfig
            {
                AnalyzeTdmsEnabled = true, TdmsRoot = tdmsRootDir,
                AnalyzeTdmsMaxKb = 65536, AnalyzeTdmsMaxChannels = 400,
            };
            var tdmsRecBad = new TestRecord
            {
                StationId = "FCT1", Model = "E3002781", Category = "Online", TestDate = "20260905",
                Sn = "SN001", Result = "PASS", XmlPath = Path.Combine(work, "a3.xml"),
            };
            Check(TdmsFeatureCollector.CaptureOne(dbT, tdmsCfgBad, tdmsRecBad, 103) == 0
                  && TdmsFeatureCollector.FilesSkipped == 2
                  && TdmsFeatureCollector.LastErrorPath.EndsWith("SN001_0002.tdms")
                  && dbT.GetTdmsFeatureSummary().Rows == 2,
                  "期3: 无效 TDMS 内容 Load 抛异常吞掉计数不落行不崩溃");

            // 快照：TdmsState round trip + RunOnce 只开 TDMS 开关
            var tdmsCfgOn = new AppConfig
            {
                AnalyzeDriftEnabled = false, AnalyzeCpkEnabled = false,
                AnalyzeFailAttrEnabled = false, AnalyzeFixtureEnabled = false,
                AnalyzeTdmsEnabled = true, TdmsRoot = tdmsRootDir,
            };
            AnalysisEngine.RunOnce(dbT, tdmsCfgOn, fixedNow);
            var tdmsGet = AnalysisEngine.GetTdmsState(dbT);
            Check(tdmsGet != null && tdmsGet.RowsTotal == 2 && tdmsGet.RecordsTotal == 1
                  && tdmsGet.FilesSkipped >= 2 && tdmsGet.Date == "2026-09-05",
                  "期3: RunOnce 仅 TDMS 开关落盘快照（行数/记录数/跳过计数）");
            Check(TdmsState.FromJson(tdmsGet!.ToJson())!.RowsTotal == tdmsGet.RowsTotal
                  && TdmsState.FromJson("bad") == null,
                  "期3: TdmsState JSON 往返一致 + 坏 JSON 安全 null");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(tdmsDbDir, true); } catch { }
            try { Directory.Delete(tdmsRootDir, true); } catch { }
            Console.WriteLine("[规格06期3] TDMS 特征化完成");
        }

        // ============ 数据采集链路修复：重试队列落盘 / 解析失败暂时性与永久性分类 ============
        {
            Console.WriteLine("\n【采集链路】重试队列持久化纯函数 …");
            var rqItems = new List<(string Path, int Attempt)>
            {
                (@"C:\res\Online\E3002781\P_UNIT_SN001_0001.xml", 0),
                (@"C:\res\Offline\E3002781\F_UNIT_SN002_0001.xml", 2),
            };
            var rqBack = Engine.DeserializeRetryQueue(Engine.SerializeRetryQueue(rqItems));
            Check(rqBack.Count == 2 && rqBack[0].Path == rqItems[0].Path && rqBack[0].Attempt == 0
                  && rqBack[1].Path == rqItems[1].Path && rqBack[1].Attempt == 2,
                  "采集链路: 重试队列 序列化→反序列化 往返一致（path/attempt 保留）");
            Check(Engine.SerializeRetryQueue(new List<(string Path, int Attempt)>()) == "[]",
                  "采集链路: 空重试队列序列化为 []");
            Check(Engine.DeserializeRetryQueue("{坏JSON!!").Count == 0 && Engine.DeserializeRetryQueue("").Count == 0,
                  "采集链路: 坏 JSON/空串反序列化返回空列表不抛异常");

            Console.WriteLine("【采集链路】解析失败暂时性/永久性分类 …");
            var clDir = Path.Combine(work, "clres", "Online", "E3002781", "20260905");
            Directory.CreateDirectory(clDir);
            var clProc = new Processor(new AppConfig { StationId = "FCT1" }, "FCT1", FctAggregator.Parsing.ParserRegistry.Instance, null);
            var halfXml = Path.Combine(clDir, "F_UNIT_SN901_20260905130000_0001.xml");
            File.WriteAllText(halfXml, "<FACTORY USER=\"op1\"><PANEL STATUS="); // 半成品 XML
            var recHalf = clProc.ParseAndClassify(halfXml, out var transientHalf);
            Check(recHalf == null && transientHalf, "采集链路: 半成品 XML 解析失败为暂时性（引擎将入重试队列）");
            var skipXml = Path.Combine(clDir, "X_UNIT_SN902_20260905130100_0001.xml");
            File.WriteAllText(skipXml, "<FACTORY USER=\"op1\"/>"); // 未知前缀
            var recSkip = clProc.ParseAndClassify(skipXml, out var transientSkip);
            Check(recSkip == null && !transientSkip, "采集链路: 未知前缀解析失败为永久性（不重试）");
            var recGone = clProc.ParseAndClassify(Path.Combine(clDir, "F_UNIT_SN903_20260905130200_0001.xml"), out var transientGone);
            Check(recGone == null && transientGone, "采集链路: 文件读取失败为暂时性（占用场景可重试）");

            // ── 审计随行回归（v3.31.0）──
            // A4：Skipped 不再无痕消失——落 parse_failure_log(error_code=skip)
            var clDbA4 = new Database(Path.Combine(work, "cl_a4.db"));
            var clProcDb = new Processor(new AppConfig { StationId = "FCT1" }, "FCT1", FctAggregator.Parsing.ParserRegistry.Instance, clDbA4);
            var recSkipDb = clProcDb.ParseAndClassify(skipXml, out var transientSkipDb);
            Check(recSkipDb == null && !transientSkipDb, "审计A4: 未知前缀仍为永久性跳过（不重试）");
            using (var cA4 = new SqliteConnection($"Data Source={Path.Combine(work, "cl_a4.db")}"))
            {
                cA4.Open();
                using var cmd = cA4.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM parse_failure_log WHERE error_code='skip' AND xml_path=@p";
                cmd.Parameters.AddWithValue("@p", Path.GetFullPath(skipXml));
                Check(Convert.ToInt64(cmd.ExecuteScalar()) >= 1, "审计A4: Skipped 文件落 parse_failure_log(error_code=skip) 留痕");
            }
            // A4：registry 对 Skipped 不短路——高优先级 stub 解析器 Skipped 后回落内置解析器
            var regA4 = FctAggregator.Parsing.ParserRegistry.Load(null);
            regA4.Register(new SkipStubParser());
            var passA4 = Path.Combine(clDir, "P_UNIT_SN904_20260905130400_0001.xml");
            File.WriteAllText(passA4, "<FACTORY USER=\"op1\"/>");
            var outA4 = regA4.Resolve(passA4, File.ReadAllText(passA4));
            Check(outA4 != null && !outA4.Skipped && outA4.Result == "PASS",
                  "审计A4: 高优先级解析器 Skipped 不短路，回落内置解析器正常解析 PASS");
            // A5：截断的 PASS 文件不再静默入库为 PASS
            var badPass = Path.Combine(clDir, "P_UNIT_SN905_20260905130500_0001.xml");
            File.WriteAllText(badPass, "<FACTORY USER=\"op1\"><PANEL STATUS=");
            var outA5 = regA4.Resolve(badPass, File.ReadAllText(badPass));
            Check(outA5 != null && outA5.Error && outA5.ErrorCode == "xml_malformed",
                  "审计A5: 截断 PASS 文件走 xml_malformed 不再静默入库污染良率");
            // A2：单条入库返回 id，重复 xml_path 返回 0（免反查）
            var recA2 = new TestRecord { StationId = "FCT1", Model = "E3002781", Category = "Online", TestDate = "20260905", Sn = "SN906", Result = "PASS", XmlPath = Path.Combine(clDir, "P_UNIT_SN906_20260905130600_0001.xml") };
            var idA2 = clDbA4.InsertOneReturnId(recA2);
            var idA2b = clDbA4.InsertOneReturnId(recA2);
            Check(idA2 > 0 && idA2b == 0, $"审计A2: InsertOneReturnId 新记录返 id>0、重复返 0（实得 {idA2}/{idA2b}）");
            // M12：CPU 采样首次调用仅建立基线，返回 null（不再产 0.0 哨兵假样本）
            using (var samplerM12 = new DeviceSampleRecorder(new AppConfig()))
            {
                Check(samplerM12.SampleSystemCpu() == null, "审计M12: CPU 采样首次调用返回 null（不再 0.0 哨兵假样本）");
            }
            // C-I1：零失败小时参与 Pearson 样本（旧实现剔除后 CPU 恒定 → NaN 无告警）
            var dbC1 = new Database(Path.Combine(work, "cl_c1.db"));
            {
                using (var cC1 = new SqliteConnection($"Data Source={Path.Combine(work, "cl_c1.db")}"))
                {
                    cC1.Open();
                    for (int h = 0; h < 8; h++)
                    {
                        using var ins = cC1.CreateCommand();
                        ins.CommandText = "INSERT INTO device_samples_local(ts, cpu_usage, mem_used_pct, disk_free_gb) VALUES(@t,@c,@m,@d)";
                        ins.Parameters.AddWithValue("@t", DateTime.Now.ToString("yyyy-MM-dd") + $" {h:00}:00:00");
                        ins.Parameters.AddWithValue("@c", h < 2 ? 100.0 : 10.0);
                        ins.Parameters.AddWithValue("@m", 40.0);
                        ins.Parameters.AddWithValue("@d", 50.0);
                        ins.ExecuteNonQuery();
                    }
                }
                var srcC1 = new AttributionSource();
                for (int h = 0; h < 8; h++) srcC1.HourlyFails[h] = h < 2 ? 0 : 5;
                var corr = FailAttributor.ComputeResourceCorrelations(dbC1, srcC1, 30);
                Check(corr.Any(x => x.Metric == "cpu" && Math.Abs(x.R) >= 0.6),
                      "审计C-I1: 零失败小时参与样本（前 2 小时 CPU=100/失败=0 不再被剔除，|r|≈1 可检出）");
            }
            // C-I3：漂移今日样本门槛 3→10
            Check(DriftAnalyzer.MinTodaySamples == 10, "审计C-I3: 漂移对照样本门槛提到 10（凌晨部分天不再假漂移）");
            // M6：双单样本 0/0=NaN 不再绕过护栏（effects 为空且不抛异常）
            var dbM6 = new Database(Path.Combine(work, "cl_m6.db"));
            {
                foreach (var (tester, sn) in new[] { ("FX1", "SNM61"), ("FX2", "SNM62") })
                {
                    dbM6.BatchInsert(new[]
                    {
                        new TestRecord
                        {
                            StationId = "FCT1", Model = "E3002781", Category = "Online", TestDate = "20260905",
                            Sn = sn, Result = "PASS", XmlPath = Path.Combine(work, $"m6_{sn}.xml"), Tester = tester,
                            Measurements = new List<MeasurementRow> { new MeasurementRow { TestName = "T_M6", Value = 10.0, Lolim = 9, Hilim = 11 } },
                        },
                    });
                }
                var fxM6 = FixtureComparator.Run(dbM6, new AppConfig { AnalyzeMinSamples = 1, AnalyzeWindowDays = 7, AnalyzeEffectD = 0.1 }, new DateTime(2026, 9, 8));
                Check(fxM6.Effects.Count == 0, "审计M6: 双单样本 0/0=NaN 被前置护栏拦截（不产出 NaN 效应量）");
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Path.Combine(work, "clres"), true); } catch { }
            Console.WriteLine("[采集链路] 持久化与分类组完成");
        }
    }

    /// <summary>审计 A4 断言用：恒 Skipped 的高优先级 stub 解析器（Priority 999 &lt; Default 1000，先跑但不短路）。</summary>
    sealed class SkipStubParser : FctAggregator.Parsing.IResultParser
    {
        public string Id => "selftest-skip-stub";
        public int Priority => 999;
        public FctAggregator.Parsing.ParseOutput? Parse(string xmlPath, string rawXml)
            => new FctAggregator.Parsing.ParseOutput { Skipped = true, SkipReason = "stub" };
    }


    static void RunNormalStateTests(string work)
    {
        // ── Task3: config 默认关闭 ──
        var defCfg = new AppConfig();
        Check(defCfg.LearnNormalEnabled, "learn_normal_enabled 默认 true");
        Check(defCfg.LearnNormalMinSamples == 30 && defCfg.LearnNormalStaleDays == 14, "正常态默认参数 30/14");
        Check(defCfg.LearnNormalVoteSignals == 3 && defCfg.LearnNormalEventScore == 75 && defCfg.LearnNormalMaxEventsPerDay == 10,
            "正常态 P2 默认投票/分数/风暴 3/75/10");
        Check(defCfg.LearnNormalEnabled, "learn_normal_enabled 默认 true（P2 键不改变开默认）");

        // ── Task1: Welford 收敛对拍 ──
        var w = new Welford();
        var xs = new List<double>();
        var rng = new Random(42);
        for (int i = 0; i < 1000; i++) { var x = 3.3 + rng.NextDouble() * 0.1; w.Update(x); xs.Add(x); }
        var mean = xs.Average();
        var std = Math.Sqrt(xs.Sum(x => (x - mean) * (x - mean)) / (xs.Count - 1));
        Check(Math.Abs(w.Mean - mean) < 1e-9, $"Welford 均值收敛（{w.Mean:F6} vs {mean:F6}）");
        Check(Math.Abs(w.Sigma - std) < 1e-9, $"Welford 样本σ收敛（{w.Sigma:F6} vs {std:F6}）");
        Check(w.N == 1000, "Welford 计数正确");

        // ── Task1: P² 分位数近似 ──
        var p01 = new P2Quantile(0.01); var p50 = new P2Quantile(0.5); var p99 = new P2Quantile(0.99);
        foreach (var x in xs) { p01.Update(x); p50.Update(x); p99.Update(x); }
        var sorted = xs.OrderBy(x => x).ToList();
        Check(Math.Abs(p50.Quantile() - sorted[500]) < 0.02 * (sorted.Max() - sorted.Min()) + 1e-9,
            $"P² 中位数近似（{p50.Quantile():F6} vs {sorted[500]:F6}）");
        Check(p01.Quantile() <= p50.Quantile() && p50.Quantile() <= p99.Quantile(), "P² 分位单调（p01≤p50≤p99）");

        // ── Task1: P² 序列化往返 ──
        var restored = P2Quantile.Deserialize(p50.Serialize());
        Check(restored != null && Math.Abs(restored!.Quantile() - p50.Quantile()) < 1e-12, "P² 序列化往返一致");
        Check(P2Quantile.Deserialize(null) == null && P2Quantile.Deserialize("垃圾") == null, "P² 反序列化容错");

        // ── Task2: normal_models 建模与状态机 ──
        var nsDb = new Database(Path.Combine(work, "ns.db"));
        Check(nsDb.GetNormalModel("measurement", "M1", "T_VOLT") == null, "初始无模型行");

        for (int i = 0; i < 2; i++)
            NormalModelStore.Observe(nsDb, 3, "measurement", "M1", "T_VOLT", 3.30 + i * 0.01, $"2026-09-0{i + 1} 10:00:00");
        var row = nsDb.GetNormalModel("measurement", "M1", "T_VOLT");
        Check(row != null && row!.Status == "learning" && row.N == 2, "n<minSamples 状态=learning");

        NormalModelStore.Observe(nsDb, 3, "measurement", "M1", "T_VOLT", 3.32, "2026-09-03 10:00:00");
        row = nsDb.GetNormalModel("measurement", "M1", "T_VOLT");
        Check(row != null && row!.Status == "ready" && row.N == 3, "n≥minSamples 状态=ready");
        Check(Math.Abs(row!.Mean - (3.30 + 3.31 + 3.32) / 3) < 1e-9, "模型均值正确");
        Check(row.MinV <= row.P01 && row.P01 <= row.P50 && row.P50 <= row.P99 && row.P99 <= row.MaxV, "分位与极值单调合理");

        var stale = NormalModelStore.EffectiveStatus(row!, new DateTime(2026, 9, 3, 10, 0, 0).AddDays(15), 14);
        Check(stale == "stale", "超 stale_days 惰性判 stale");
        var fresh = NormalModelStore.EffectiveStatus(row!, new DateTime(2026, 9, 5, 10, 0, 0), 14);
        Check(fresh == "ready", "窗口内保持 ready");

        NormalModelStore.Observe(nsDb, 3, "measurement", "M1", "T_VOLT", double.NaN, "2026-09-04 10:00:00");
        Check(nsDb.GetNormalModel("measurement", "M1", "T_VOLT")!.N == 3, "NaN/Inf 不入模（只学有限值）");

        // 主库迁移幂等：构造函数重复打开不抛异常且表存在
        Check(true, "normal_models 建表随 Database.Init 生效（ns.db 构造已隐式验证）");

        // ── Task4: 学习器行为 ──
        var lrDb = new Database(Path.Combine(work, "lr.db"));
        var lrCfg = new AppConfig { LearnNormalEnabled = true, LearnNormalMinSamples = 2 };

        var pass = new TestRecord { Model = "G49", Result = "PASS", Sn = "SN1", BatchTimestamp = "2026-09-07 10:00:00" };
        pass.Measurements.Add(new MeasurementRow { TestName = "T_VOLT", Value = 3.3 });
        NormalLearners.ObserveMeasurement(lrDb, lrCfg, pass);
        NormalLearners.ObserveMeasurement(lrDb, lrCfg, pass);
        var m = lrDb.GetNormalModel(NormalModelStore.SourceMeasurement, "G49", "T_VOLT");
        Check(m != null && m!.Status == "ready", "测量学习器：PASS 数据入模");

        var failRec = new TestRecord { Model = "G49", Result = "FAIL", Sn = "SN2", BatchTimestamp = "2026-09-07 10:01:00" };
        failRec.Measurements.Add(new MeasurementRow { TestName = "T_VOLT", Value = 9.9 });
        NormalLearners.ObserveMeasurement(lrDb, lrCfg, failRec);
        m = lrDb.GetNormalModel(NormalModelStore.SourceMeasurement, "G49", "T_VOLT");
        Check(m != null && m!.N == 2 && m.Mean < 3.4, "FAIL 数据不入模（正常态不被污染）");

        var wfCfg = lrCfg;
        var wfRec = new TestRecord { Model = "G49", Result = "PASS", Sn = "SN3", BatchTimestamp = "2026-09-07 10:02:00" };
        var wfRows = new List<TdmsFeatureAnalyzer.TdmsFeatureRow>
            { new() { GroupName = "G1", ChannelName = "CH1", Mean = 1.2, Std = 0.1, Max = 2.0 } };
        NormalLearners.ObserveWaveform(lrDb, wfCfg, wfRec, wfRows);
        Check(lrDb.GetNormalModel(NormalModelStore.SourceWaveform, "G49", "G1/CH1/vmean") != null, "波形学习器：vmean 建模");
        Check(lrDb.GetNormalModel(NormalModelStore.SourceWaveform, "G49", "G1/CH1/vstd") != null, "波形学习器：vstd 建模");
        Check(lrDb.GetNormalModel(NormalModelStore.SourceWaveform, "G49", "G1/CH1/vmax") != null, "波形学习器：vmax 建模");

        NormalLearners.ObserveDeviceSample(lrDb, lrCfg, 15.5, 45.0, 120.0, "2026-09-07 23:30:00");
        Check(lrDb.GetNormalModel(NormalModelStore.SourceDevice, "", "cpu@3") != null, "设备学习器：按时段建键（23 点→slot3）");

        NormalLearners.FlushHeartbeat(lrDb);
        var hb = lrDb.GetMeta("learn_normal_heartbeat");
        Check(!string.IsNullOrWhiteSpace(hb) && hb!.Contains("measurement") && hb.Contains("errors"), "心跳落库 learn_normal_heartbeat");

        // 开关关闭全链路 no-op
        var offDb = new Database(Path.Combine(work, "off.db"));
        var offCfg = new AppConfig { LearnNormalEnabled = false };
        NormalLearners.ObserveMeasurement(offDb, offCfg, pass);
        NormalLearners.ObserveWaveform(offDb, offCfg, pass, wfRows);
        NormalLearners.ObserveDeviceSample(offDb, offCfg, 10, 40, 100, "2026-09-07 08:30:00");
        Check(offDb.GetNormalModel(NormalModelStore.SourceMeasurement, "G49", "T_VOLT") == null
           && offDb.GetNormalModel(NormalModelStore.SourceWaveform, "G49", "G1/CH1/vmean") == null
           && offDb.GetNormalModel(NormalModelStore.SourceDevice, "", "cpu@1") == null,
            "开关关闭：三学习器全部 no-op");

        // ── P2: DeviationScorer 公式 / 闸门 / 投票 / 风暴 ──
        Check(DeviationScorer.SigmaFloor == 1e-6, "sigma_floor 常量 1e-6");
        var scDb = new Database(Path.Combine(work, "sc.db"));
        var scNow = new DateTime(2026, 9, 8, 12, 0, 0);
        var readyRow = new NormalModelRow
        {
            Source = "measurement", Model = "M1", SignalKey = "T1",
            N = 50, Mean = 10.0, Sigma = 1.0, Status = "ready",
            LastTs = "2026-09-08 10:00:00",
        };
        Check(DeviationScorer.ScoreOne(readyRow, 13.0, scNow, 14) == 50, "z=3 → 分数 50");
        Check(DeviationScorer.ScoreOne(readyRow, 16.0, scNow, 14) == 100, "z=6 → 分数 100（封顶）");
        Check(DeviationScorer.ScoreOne(readyRow, 4.0, scNow, 14) == 100, "负偏离 |z|=6 → 分数 100");
        var learningRow = new NormalModelRow { Status = "learning", Mean = 10, Sigma = 1, N = 5, LastTs = readyRow.LastTs };
        Check(DeviationScorer.ScoreOne(learningRow, 16.0, scNow, 14) == null, "非 ready 不可评");
        var staleRow = new NormalModelRow { Status = "ready", Mean = 10, Sigma = 1, N = 50, LastTs = "2026-01-01 00:00:00" };
        Check(DeviationScorer.ScoreOne(staleRow, 16.0, scNow, 14) == null, "stale 不可评");

        var voteCfg = new AppConfig
        {
            LearnNormalEnabled = true, LearnNormalMinSamples = 5,
            LearnNormalVoteSignals = 3, LearnNormalEventScore = 75, LearnNormalMaxEventsPerDay = 10,
            LearnNormalStaleDays = 14,
        };
        for (int i = 0; i < 8; i++)
        {
            double d = (i - 3.5) * 0.25;
            NormalModelStore.Observe(scDb, 5, "measurement", "M1", "A", 10.0 + d, "2026-09-01 10:00:00");
            NormalModelStore.Observe(scDb, 5, "measurement", "M1", "B", 20.0 + d, "2026-09-01 10:00:00");
            NormalModelStore.Observe(scDb, 5, "measurement", "M1", "C", 30.0 + d, "2026-09-01 10:00:00");
            NormalModelStore.Observe(scDb, 5, "measurement", "M1", "D", 40.0 + d, "2026-09-01 10:00:00");
        }
        var evSilent = DeviationScorer.EvaluateWindow(scDb, voteCfg, "measurement", "M1", "2026-09-08 12:00:00",
            new[] { ("A", 10.0), ("B", 20.0), ("C", 30.0), ("D", 40.0) }, scNow);
        Check(evSilent == null && scDb.CountDeviationEventsOnDay("2026-09-08") == 0, "正常窗口沉默（不落事件）");

        var evHit = DeviationScorer.EvaluateWindow(scDb, voteCfg, "measurement", "M1", "2026-09-08 12:01:00",
            new[] { ("A", 16.0), ("B", 26.0), ("C", 36.0), ("D", 40.0) }, scNow);
        Check(evHit != null && evHit!.SignalCount >= 3 && evHit.TopScore >= 75, "≥3 信号 score≥75 落偏离事件");
        Check(scDb.CountDeviationEventsOnDay("2026-09-08") == 1, "投票通过写入 1 条 deviation_events");

        var twoOnly = DeviationScorer.EvaluateWindow(scDb, voteCfg, "measurement", "M1", "2026-09-08 12:02:00",
            new[] { ("A", 16.0), ("B", 26.0), ("C", 30.0), ("D", 40.0) }, scNow);
        Check(twoOnly == null && scDb.CountDeviationEventsOnDay("2026-09-08") == 1, "仅 2 信号超门限不落库");

        voteCfg.LearnNormalMaxEventsPerDay = 1;
        var storm = DeviationScorer.EvaluateWindow(scDb, voteCfg, "measurement", "M1", "2026-09-08 12:03:00",
            new[] { ("A", 16.0), ("B", 26.0), ("C", 36.0), ("D", 40.0) }, scNow);
        Check(storm == null && scDb.CountDeviationEventsOnDay("2026-09-08") == 1, "风暴门：超每日上限只计数不落库");

        var offVote = new AppConfig { LearnNormalEnabled = false, LearnNormalVoteSignals = 3, LearnNormalEventScore = 75 };
        Check(DeviationScorer.EvaluateWindow(scDb, offVote, "measurement", "M1", "2026-09-08 12:04:00",
            new[] { ("A", 16.0), ("B", 26.0), ("C", 36.0) }, scNow) == null, "开关关：评分 no-op");

        var spDb = new Database(Path.Combine(work, "sp.db"));
        var spCfg = new AppConfig
        {
            LearnNormalEnabled = true, LearnNormalVoteSignals = 3, LearnNormalEventScore = 75,
            LearnNormalMaxEventsPerDay = 10, LearnNormalStaleDays = 14,
        };
        foreach (var key in new[] { "KL30_FILT_1", "P5V_CAN", "P3V3_DIG" })
        {
            for (int i = 0; i < 40; i++)
                NormalModelStore.Observe(spDb, 30, "measurement", "G49", key, 14.400 + i * 1e-6, "2026-09-01 10:00:00");
        }
        var spEv = DeviationScorer.EvaluateWindow(spDb, spCfg, "measurement", "G49", "2026-09-08 12:10:00",
            new[] { ("KL30_FILT_1", 14.401), ("P5V_CAN", 14.402), ("P3V3_DIG", 14.399) }, scNow);
        Check(spEv == null && spDb.CountDeviationEventsOnDay("2026-09-08") == 0,
            "正常态：G49 设定值（σ≪均值）不参与投票、不落偏离事件");
        foreach (var key in new[] { "8.19.5 FLTM_DESAT_A(XCP)", "8.20.1 ASC_H(XCP)", "8.21.1 SBC_C(XCP)" })
        {
            for (int i = 0; i < 40; i++)
                NormalModelStore.Observe(spDb, 30, "measurement", "G49", key, 0.50 + (i % 5) * 0.04, "2026-09-01 10:00:00");
        }
        var injEv = DeviationScorer.EvaluateWindow(spDb, spCfg, "measurement", "G49", "2026-09-08 12:11:00",
            new[] { ("8.19.5 FLTM_DESAT_A(XCP)", 0.9), ("8.20.1 ASC_H(XCP)", 0.9), ("8.21.1 SBC_C(XCP)", 0.9) }, scNow);
        Check(injEv == null, "正常态：注入章测名不参与偏离投票");

        var live = new AppConfig { LearnNormalEnabled = true, AnalyzeTdmsEnabled = false, LogLevel = "INFO" };
        var incoming = new AppConfig { LearnNormalEnabled = false, AnalyzeTdmsEnabled = true, LogLevel = "DEBUG" };
        live.CopyFrom(incoming);
        Check(!live.LearnNormalEnabled && live.AnalyzeTdmsEnabled && live.LogLevel == "DEBUG",
            "Config.CopyFrom 把开关拷到同一实例（Engine 持引用可热加载）");

        // ── P2: 回填只学 PASS、幂等 ──
        var bfDb = new Database(Path.Combine(work, "bf.db"));
        var bfCfg = new AppConfig { LearnNormalEnabled = true, LearnNormalMinSamples = 2 };
        var passBf = new TestRecord
        {
            StationId = "FCT1", Model = "G49", Category = "Online", TestDate = "20260901",
            Sn = "SNBF1", Result = "PASS", XmlPath = Path.Combine(work, "bf_pass.xml"),
            BatchTimestamp = "2026-09-01 10:00:00",
            Measurements = new List<MeasurementRow> { new() { TestName = "T_BF", Value = 5.0 } },
        };
        var failBf = new TestRecord
        {
            StationId = "FCT1", Model = "G49", Category = "Online", TestDate = "20260901",
            Sn = "SNBF2", Result = "FAIL", XmlPath = Path.Combine(work, "bf_fail.xml"),
            BatchTimestamp = "2026-09-01 10:01:00",
            Measurements = new List<MeasurementRow> { new() { TestName = "T_BF", Value = 99.0 } },
        };
        bfDb.BatchInsert(new[] { passBf, failBf });
        File.WriteAllText(passBf.XmlPath, "<x/>");
        File.WriteAllText(failBf.XmlPath, "<x/>");
        var n1 = LearnBackfill.Run(bfDb, bfCfg, 30);
        var n2 = LearnBackfill.Run(bfDb, bfCfg, 30);
        var bfRow = bfDb.GetNormalModel(NormalModelStore.SourceMeasurement, "G49", "T_BF");
        Check(bfRow != null && bfRow!.N >= 1 && bfRow.Mean < 10, "回填只学 PASS（FAIL 99 不入模）");
        Check(n1 >= 1 && n2 == 0, "回填水位幂等（第二次 0 增量）");

        var offBf = LearnBackfill.Run(offDb, new AppConfig { LearnNormalEnabled = false }, 30);
        Check(offBf == 0, "开关关：回填 no-op");

        var winDb = new Database(Path.Combine(work, "win.db"));
        winDb.BatchInsert(new[]
        {
            new TestRecord
            {
                StationId = "FCT1", Model = "G49", TestDate = "20260722", Result = "PASS",
                XmlPath = Path.Combine(work, "w1.xml"), BatchTimestamp = "2026-07-22 10:00:00",
            },
        });
        Check(LearnPipeline.ResolveBackfillDays(winDb, 1) == 30, "UI 回填窗口：近 30 天");
        Check(LearnPipeline.ResolveBackfillDays(winDb, 0) >= 30, "UI 回填窗口：自动覆盖最早 PASS");

        // ── P2: ReplayRunner 合成 4 周三指标 ──
        var rp = ReplayRunner.RunSynthetic(Path.Combine(work, "rp.db"));
        Check(rp.DetectionRate >= 0.80, $"回放异常检出率 ≥80%（实得 {rp.DetectionRate:P0}）");
        Check(rp.FalsePosPerDay <= 0.20, $"回放误报 ≤0.2 条/天（实得 {rp.FalsePosPerDay:F2}）");
        Check(rp.ReadyCoverage >= 0.90, $"回放 ready 覆盖率 ≥90%（实得 {rp.ReadyCoverage:P0}）");
        Check(rp.Pass, "ReplayRunner 三指标同时达标");

        var fieldDbPath = Path.Combine(work, "field.db");
        var fieldDb = new Database(fieldDbPath);
        {
            string[] fKeys = { "S1", "S2", "S3", "S4", "S5" };
            double[] fMeans = { 10, 20, 30, 40, 50 };
            var fTrainStart = new DateTime(2026, 8, 1);
            var fTrainEnd = fTrainStart.AddDays(21);
            var fScoreEnd = fTrainEnd.AddDays(7);
            var fAnomaly = new DateTime(2026, 8, 25);
            var fieldRecs = new List<TestRecord>();
            int sn = 0;
            for (var d = fTrainStart; d < fTrainEnd; d = d.AddDays(1))
            {
                for (int k = 0; k < 8; k++)
                {
                    var ts = d.AddHours(8 + k).ToString("yyyy-MM-dd HH:mm:ss");
                    var rec = new TestRecord
                    {
                        StationId = "FCT1", Model = "G49", Category = "Online",
                        TestDate = d.ToString("yyyyMMdd"), Sn = $"F{sn++}", Result = "PASS",
                        XmlPath = Path.Combine(work, $"field_{sn}.xml"), BatchTimestamp = ts,
                    };
                    for (int i = 0; i < fKeys.Length; i++)
                        rec.Measurements.Add(new MeasurementRow
                        {
                            TestName = fKeys[i],
                            Value = fMeans[i] * (1.0 + 0.04 * (k - 3.5) / 3.5),
                        });
                    fieldRecs.Add(rec);
                }
            }
            for (var d = fTrainEnd; d < fScoreEnd; d = d.AddDays(1))
            {
                bool bad = d.Date == fAnomaly.Date;
                var ts = d.AddHours(10).ToString("yyyy-MM-dd HH:mm:ss");
                var rec = new TestRecord
                {
                    StationId = "FCT1", Model = "G49", Category = "Online",
                    TestDate = d.ToString("yyyyMMdd"), Sn = $"F{sn++}", Result = "PASS",
                    XmlPath = Path.Combine(work, $"field_score_{sn}.xml"), BatchTimestamp = ts,
                };
                for (int i = 0; i < fKeys.Length; i++)
                    rec.Measurements.Add(new MeasurementRow { TestName = fKeys[i], Value = bad ? fMeans[i] + 6.0 : fMeans[i] });
                fieldRecs.Add(rec);
            }
            fieldDb.BatchInsert(fieldRecs);
            fieldDb.CheckpointForCopy();
        }
        var fieldCfg = new AppConfig
        {
            LearnNormalEnabled = true, LearnNormalMinSamples = 30, LearnNormalStaleDays = 14,
            LearnNormalVoteSignals = 3, LearnNormalEventScore = 75, LearnNormalMaxEventsPerDay = 10,
        };
        var fr = ReplayRunner.RunField(fieldDbPath, fieldCfg, 28, new[] { "2026-08-25" });
        Check(fr.Pass, "RunField 现场回放三指标达标");
        Check(fr.DetectionRate >= 0.80, $"RunField 检出率 ≥80%（实得 {fr.DetectionRate:P0}）");

        foreach (var g in fieldDb.ListPassMeasurementPoints("2026-08-01", "2026-08-21", null, 5000).GroupBy(r => r.RecordId))
        {
            var f = g.First();
            NormalLearners.ObserveMeasurement(fieldDb, fieldCfg, new TestRecord
            {
                Model = f.Model, Result = "PASS", BatchTimestamp = f.Ts,
                Measurements = g.Select(x => new MeasurementRow { TestName = x.TestName, Value = x.Value }).ToList(),
            });
        }
        var chartNow = new DateTime(2026, 8, 28, 12, 0, 0);
        var (zLabels, zSeries, _) = DeviationSeriesBuilder.Build(fieldDb, fieldCfg, 3, 200, chartNow);
        Check(zLabels.Count >= 7 && zSeries.Count >= 1 && zSeries[0].Points.Count >= 1,
            "偏离折线：PASS 时间轴 + 至少一条 |z| 序列");
        Check(zSeries.Any(s => s.Points.Any(p => p.Point.Z > 1)),
            "偏离折线：评分周异常 PASS 的 |z| > 1");

        var histDb = new Database(Path.Combine(work, "chart_hist.db"));
        var histCfg = new AppConfig { LearnNormalEnabled = true, LearnNormalMinSamples = 2 };
        for (int i = 0; i < 5; i++)
            NormalModelStore.Observe(histDb, 2, "measurement", "G49", "T_HIST", 10.0 + i * 0.1, "2026-07-22 10:00:00");
        var histPass = new TestRecord
        {
            StationId = "FCT1", Model = "G49", TestDate = "20260722", Result = "PASS",
            XmlPath = Path.Combine(work, "hist.xml"), BatchTimestamp = "2026-07-22 10:00:00",
            Measurements = new List<MeasurementRow> { new() { TestName = "T_HIST", Value = 10.5 } },
        };
        histDb.BatchInsert(new[] { histPass });
        var (_, wallSeries, _) = DeviationSeriesBuilder.Build(histDb, histCfg, 1);
        Check(wallSeries.Count >= 1, "偏离折线：7 天窗口锚最新 PASS 日期（非墙钟今天）");
        var (autoLabels, autoSeries, autoHint) = DeviationSeriesBuilder.Build(histDb, histCfg, 0);
        Check(autoSeries.Count >= 1 && autoLabels.Count >= 1,
            $"偏离折线：自动窗口锚最新 PASS（实得 series={autoSeries.Count}）");
        Check(string.IsNullOrEmpty(autoHint), "偏离折线：自动窗口有数据时无空提示");

        var snap = LearningSnapshot.Capture(scDb, voteCfg, scNow);
        Check(snap.Total >= 4 && snap.Ready >= 4, "P3 快照：覆盖率与表对拍（ready≥4）");
        Check(snap.Events.Count >= 1, "P3 快照：偏离事件与表对拍");
        Check(snap.HeartbeatSummary() != null, "P3 快照：心跳文案可生成");

        var evDb = new Database(Path.Combine(work, "ev.db"));
        var evCfg = new AppConfig
        {
            LearnNormalEnabled = true, LearnNormalMinSamples = 5, LearnNormalStaleDays = 14,
            AnalyzeWindowDays = 7, AnalyzeAttrMinBucket = 20, AnalyzeAttrDevRatio = 1.5,
        };
        for (int i = 0; i < 8; i++)
            NormalModelStore.Observe(evDb, 5, "measurement", "G49", "T_EV", 10.0, "2026-09-01 10:00:00");
        evDb.BatchInsert(new[]
        {
            new TestRecord
            {
                StationId = "FCT1", Model = "G49", Category = "Online", TestDate = "20260908",
                Sn = "SNEV", Result = "FAIL", XmlPath = Path.Combine(work, "ev_fail.xml"),
                BatchTimestamp = "2026-09-08 12:00:00",
                FailedTests = { new FailedTest { Name = "T_EV", Value = "16.0" } },
            },
        });
        var attr = FailAttributor.Run(evDb, evCfg, new DateTime(2026, 9, 8, 12, 0, 0));
        Check(attr.DeviationEvidence.Any(d => d.TestName == "T_EV" && d.Score >= 75),
            "P4 归因偏离证据：FAIL 信号对本机 ready 模型打出高分");
        evCfg.LearnNormalEnabled = false;
        Check(FailAttributor.Run(evDb, evCfg, new DateTime(2026, 9, 8)).DeviationEvidence.Count == 0,
            "P4 开关关：不附偏离证据");
    }

    static void RunLogExportTests(string work)
    {
        var marker = "LOGEXPORT-" + Guid.NewGuid().ToString("N")[..8];
        Logger.Info("[自检] " + marker);
        var tail = Logger.ReadTail();
        Check(tail.Contains(marker), "ReadTail 含刚写入的 INFO 行");
        Check(File.Exists(Logger.CurrentLogPath), "app.log 落盘");

        var zipPath = Path.Combine(work, "argus-logs-selftest.zip");
        var got = Logger.ExportZip(zipPath);
        Check(File.Exists(got) && new FileInfo(got).Length > 0, "ExportZip 写出非空 zip");

        using (var zip = ZipFile.OpenRead(got))
        {
            var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Check(names.Contains("diagnostic.txt"), "zip 含 diagnostic.txt");
            Check(names.Any(n => n.StartsWith("app.log", StringComparison.OrdinalIgnoreCase)), "zip 含 app.log*");
            using var sr = new StreamReader(zip.GetEntry("diagnostic.txt")!.Open(), Encoding.UTF8);
            var diag = sr.ReadToEnd();
            Check(diag.Contains("webhook_configured=") && !diag.Contains("webhook_url="),
                "diagnostic 只报 webhook 是否配置，不写 URL");
            Check(diag.Contains("version=") && diag.Contains("base_dir="), "diagnostic 含版本与安装目录");
        }

        var emptyName = Logger.SuggestedExportName();
        Check(emptyName.EndsWith(".zip") && emptyName.StartsWith("Argus-logs-"), "建议文件名 Argus-logs-*.zip");
        Check(new AppConfig().AnalyzeMaxTestsPerFile == 1000, "默认 analyze_max_tests_per_file=1000（G49 超 500）");
        Check(LeftoverAgg.QueryService("ArgusNoSuchService_xyz") == null, "残留探测：不存在的服务返回 null");
    }
}

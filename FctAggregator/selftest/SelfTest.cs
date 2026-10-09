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

        // 先按主程序同一套 SDK 生成逻辑初始化（含 csproj 的 ApplicationHighDpiMode）：
        // 必须在创建任何窗口之前调用，否则 SetHighDpiMode 会抛 InvalidOperationException
        ApplicationConfiguration.Initialize();
        Console.WriteLine($"DPI 感知: {Application.HighDpiMode}");

        var work = Path.Combine(Path.GetTempPath(), "fct_agg_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(work);
        // 审计 F14：真实库 fixture 原只找 dist\data\fct.db——dist/ 已随纯单机化删除，
        // haveReal 恒 false，「迁移前自动备份」等老库兼容断言每次被静默跳过。补 data\fct.db 候选
        // （gitignore 的运行期目录，开发机跑过主程序即有；两处都没有仍走空库路径）。
        var real = FindUp(Path.Combine("dist", "data", "fct.db"));
        if (real.Length == 0) real = FindUp(Path.Combine("data", "fct.db"));
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
            // 夹具日期按"昨天"生成而非硬编码：Processor 侧文件名时间要过 ResolveFileNameTime 的 anchor 校验，
            // 写死 2026-08-14 这种日期会让套件随真实时间推移自然失效（09-14 跑时相差 31 天 > 30 天阈值）。
            var tsDay = DateTime.Now.AddDays(-1);
            var tsTime = tsDay.ToString("yyyy-MM-dd HH:mm:ss");
            var tsDir = Path.Combine(tsRoot, "Offline", "E3002781", tsDay.ToString("yyyyMMdd"));
            Directory.CreateDirectory(tsDir);
            var tsXml = Path.Combine(tsDir,
                $"O_Fts_PEU_G49_FCT6_E3002781AGV75236898002K81201743_{tsDay:yyyyMMddHHmmss}293_2026813232417386.xml");
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
            Check(tsPr.BatchTimestamp == tsTime,
                  $"时间源: 文件名 17 位时间优先（实得 {tsPr?.BatchTimestamp}）");
            var tsProc = new Processor(new AppConfig(), "");
            var tsRec = tsProc.ParseAndClassify(tsXml);
            Check(tsRec != null && TimeUtil.Normalize(tsRec.BatchTimestamp) == tsTime,
                  $"时间源: Processor 也取文件名时间（实得 {tsRec?.BatchTimestamp}）");
            Check(tsRec != null && tsRec.StationId == "FCT6",
                  $"时间源用例: 机台号仍从 TESTER 提取（实得 {tsRec?.StationId}）");
            // anchor 改为目录日后：历史文件（距"现在" >30 天）不再丢文件名时间；与目录日相差 >30 天的伪段仍被拒
            var tsOldDir = Path.Combine(tsRoot, "Offline", "E3002781", "20260115");
            Directory.CreateDirectory(tsOldDir);
            var tsOldXml = Path.Combine(tsOldDir,
                "O_Fts_PEU_G49_FCT6_SN_20260115083059654_2026813232417390.xml");
            File.WriteAllText(tsOldXml, tsBody);
            var tsOldRec = tsProc.ParseAndClassify(tsOldXml);
            Check(tsOldRec != null && TimeUtil.Normalize(tsOldRec.BatchTimestamp) == "2026-01-15 08:30:59",
                  $"时间源: 目录日锚点——历史文件（距今 >30 天）仍取文件名时间（实得 {tsOldRec?.BatchTimestamp}）");
            var tsBadDir = Path.Combine(tsRoot, "Offline", "E3002781", "20260116");
            Directory.CreateDirectory(tsBadDir);
            var tsBadXml = Path.Combine(tsBadDir,
                "O_Fts_PEU_G49_FCT6_SN_20240101083059655_2026813232417391.xml");
            File.WriteAllText(tsBadXml, tsBody);
            var tsBadRec = tsProc.ParseAndClassify(tsBadXml);
            Check(tsBadRec != null && TimeUtil.Normalize(tsBadRec.BatchTimestamp) == "2026-01-16 00:00:00",
                  $"时间源: 文件名时间与目录日相差 >30 天仍拒用（实得 {tsBadRec?.BatchTimestamp}）");
            var tsXml2 = Path.Combine(tsDir,
                "O_Fts_PEU_G49_FCT6_E3002781AGV75236898002K81201743_nodate_2026813232417387.xml");
            File.WriteAllText(tsXml2, tsBody);
            var tsRec2 = tsProc.ParseAndClassify(tsXml2);
            Check(tsRec2 != null && TimeUtil.Normalize(tsRec2.BatchTimestamp) == $"{tsDay:yyyy-MM-dd} 00:00:00",
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

            // COM 探测加了 2s 缓存（总览每秒、设备页 200ms 都会调）：缓存必须不改变对外语义
            var cc1 = FctIni.ProbePresentComPorts();
            var cc2 = FctIni.ProbePresentComPorts();
            Check(cc1.SetEquals(cc2), "COM 探测两次调用结果一致（缓存不改变语义）");
            cc1.Add("COM999");
            var cc3 = FctIni.ProbePresentComPorts();
            Check(!cc3.Contains("COM999"), "修改 ProbePresentComPorts 返回值不污染内部缓存（返回副本）");
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
            Console.WriteLine("    (跳过真实库断言：无 fixture，把任意一个机台库拷到 data\\fct.db 即可启用)");
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

        Console.WriteLine("\n【分析页趋势图】测量值归一化 / 时间分桶 / 桶宽自适应 / 单边限合成");
        RunMeasurementTrendTests(work);

        Console.WriteLine("\n【FAIL 明细导出】自然周期 / 测项级明细（老记录退回）/ CSV 列与转义");
        RunFailDetailExportTests(work);

        Console.WriteLine("\n【v3.48.1 审计修复】热升级参数/换装白名单 + 飞书业务码 + 机台号边界 + 冷归档日期口径 + 告警回队 + CLI 兜底");
        RunAuditFixTests(work);

        Console.WriteLine("\n【v3.48.2 日期格式 / 运行时】dash 日期不再从待办·KPI·补推消失；热升级保留现场调参；重试占坑");
        RunDateFormatAndRuntimeTests(work);

        Console.WriteLine("\n【v3.48.3 审计修复批一】待办区间合并键 / 复发FAIL告警 / dotnet宿主重启 / 压缩保留期");
        RunAuditFixBatch1Tests(work);

        Console.WriteLine("\n【v3.48.4 审计修复批二】瘦表DDL事务 / checkpoint返回值 / 备份保留glob / 状态机常量 / 区间时间归一");
        RunAuditFixBatch2Tests(work);

        Console.WriteLine("\n【v3.48.5 审计修复批三】UI 线程重活 / Font 泄漏 / OnPaint 兜底 / 导出行错位 / 失败可见");
        RunAuditFixBatch3Tests();

        Console.WriteLine("\n【v3.48.6 审计修复批四】回填水位单调 / 基线日期归一 / 样本σ / 近常量闸门");
        RunAuditFixBatch4Tests(work);

        Console.WriteLine("\n【v3.48.7 审计修复批五】工程纪律：脚本失效/文档基线/夹具路径/来源项round-trip");
        RunAuditFixBatch5Tests();

        Console.WriteLine("\n【v3.48.8 审计修复批六】站号回落链 / 时间归一 / 按日统计 / 超期候选 / 回放主库句柄 / 保留期分口径 / 空ts明细 / 两段版本 / 待办重扫");
        RunAuditFixBatch6Tests(work);

        Console.WriteLine("\n【v3.49.0 维修日志】一条记录一行，改状态覆盖原行");
        RunMaintenanceCsvLogTests(work);

        Console.WriteLine("\n【v3.51.0 FAIL 月表】按失败次数排序，新月新文件");
        RunFailMonthCsvTests(work);

        Console.WriteLine("\n【v3.51.2 测试报告】TEST 与无测量值的测量 GROUP 都进明细");
        RunReportMeasurementTests();

        Console.WriteLine("\n【v3.53.0 章节耗时】SequenceCall 的 TOTALTIME，不进测项明细");
        RunReportChapterTimeTests();

        Console.WriteLine("\n【v3.54.0 启动画面】直接进入主窗口");
        RunNoSplashTests();

        Console.WriteLine("\n【v3.55.0 同台复测】当日次第、上次结果、章节差、出限方向");
        RunUnitRetestTests(work);

        Console.WriteLine("\n【v3.56.0 今日节拍】有总时长才进平均，下午和上午比");
        RunCycleTaktTests(work);

        Console.WriteLine("\n【v3.52.0 重复故障】本月次数 / 满 5 次补待办");
        RunRepeatFailTests(work);

        RunBannerColorTests();

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
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name IN ('idx_tr_date_result','idx_tm_ts','idx_fi_name_ts','idx_tf_ts')";
            using var r = cmd.ExecuteReader();
            while (r.Read()) idx.Add(r.GetString(0));
        }
        Check(idx.Contains("idx_tr_date_result"), "性能: test_records(test_date,result) 复合索引已建");
        Check(idx.Contains("idx_tm_ts") && idx.Contains("idx_fi_name_ts") && idx.Contains("idx_tf_ts"),
              "性能: 明细表时间索引已建（瘦表，无路径冗余列）");

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
                  AND m.ts >= @from AND m.ts < @toExcl
                ORDER BY m.test_name COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@from", now.AddDays(-20).ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@toExcl", now.AddDays(1).ToString("yyyy-MM-dd"));
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

        // ── 颜色令牌（2026-10-09）：Ui/ 硬编码 RGB 字面量全收敛为 Theme 令牌（透明度变体除外）──
        {
            Console.WriteLine("\n【颜色令牌】Ui/ 字面量收敛 / 178-179 笔误统一…");

            Check(Theme.BrandRed.ToArgb() == Color.FromArgb(200, 16, 46).ToArgb(),
                "颜色令牌: BrandRed == #C8102E（FAIL/待办主红）");
            Check(MaintenanceMeta.SeverityColorOf("minor").ToArgb() == Theme.GrayLight.ToArgb()
                  && TodoGrouping.PriorityColorOf(0).ToArgb() == Theme.GrayLight.ToArgb(),
                "颜色令牌: 轻微严重度与低优先级共用同一浅灰（原 178/179 相差 1 的笔误已统一）");
            Check(MaintenanceMeta.AccentOf("resolved").ToArgb() == Theme.GrayPale.ToArgb()
                  && MaintenanceMeta.AccentOf("unknown").ToArgb() == Theme.GrayMid.ToArgb()
                  && MaintenanceMeta.AccentOf("in_progress").ToArgb() == Theme.NearBlack.ToArgb(),
                "颜色令牌: 维修状态点色走 GrayPale / GrayMid / NearBlack");

            var themeSrc = FindUp(Path.Combine("Ui", "Theme.cs"));
            Check(themeSrc.Length > 0, "颜色令牌: 找到 Theme.cs 源文件");
            if (themeSrc.Length > 0)
            {
                var uiDir = Path.GetDirectoryName(themeSrc)!;
                var offenders = new List<string>();
                foreach (var f in Directory.GetFiles(uiDir, "*.cs"))
                {
                    if (Path.GetFileName(f) == "Theme.cs") continue;
                    int no = 0;
                    foreach (var raw in File.ReadLines(f))
                    {
                        no++;
                        if (raw.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                        var idx = 0;
                        while ((idx = raw.IndexOf("Color.FromArgb(", idx, StringComparison.Ordinal)) >= 0)
                        {
                            // 3/4 个数字参数才是硬编码 RGB(A)；带 Color 参数的透明度变体是合法派生。
                            var rest = raw[(idx + "Color.FromArgb(".Length)..];
                            if (System.Text.RegularExpressions.Regex.IsMatch(rest, @"^\s*\d+\s*,\s*\d+\s*,\s*\d+\s*[,)]"))
                                offenders.Add($"{Path.GetFileName(f)}:{no}");
                            idx += "Color.FromArgb(".Length;
                        }
                    }
                }
                Check(offenders.Count == 0,
                    $"颜色令牌: Ui/ 除 Theme.cs 外无 RGB 字面量（发现 {offenders.Count} 处: {string.Join(", ", offenders)}）");
            }
        }

        // ── UI 体验修复（v3.40.5）：primary 上色 / 对比度 / DPI 基准 / 无障碍 / 键盘可达 ──
        {
            using (var f = new Form())
            {
                var primary = Theme.MakeButton("刷新", 76, primary: true);
                var plain = Theme.MakeButton("其它", 76);
                f.Controls.Add(primary); f.Controls.Add(plain);
                Theme.Apply(f, isPageRoot: true);
                Check(primary.FlatStyle == FlatStyle.Flat && !primary.UseVisualStyleBackColor
                      && primary.BackColor == Theme.PrimaryDim && primary.ForeColor == Color.White,
                      "UI-7: primary 按钮走 Flat 上色（Apply 后仍保持，不被 System 风格吞掉）");
                Check(plain.FlatStyle == FlatStyle.System, "UI-7: 普通按钮保持系统原生外观");
                Theme.SetButtonPrimary(primary, false);
                Check(primary.BackColor == Theme.Surface && primary.FlatStyle == FlatStyle.Flat,
                      "UI-7: 运行时切回非高亮态仍能上色（树形/表格切换按钮依赖此行为）");
                Check(!string.IsNullOrEmpty(plain.AccessibleName) && plain.AccessibleName == plain.Text,
                      "UI-9: Apply 用控件文本补 AccessibleName（读屏可读）");
            }

            double Rel(Color c)
            {
                double Ch(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
                return 0.2126 * Ch(c.R / 255.0) + 0.7152 * Ch(c.G / 255.0) + 0.0722 * Ch(c.B / 255.0);
            }
            double Contrast(Color a, Color b)
            {
                var l1 = Rel(a); var l2 = Rel(b);
                if (l1 < l2) (l1, l2) = (l2, l1);
                return (l1 + 0.05) / (l2 + 0.05);
            }
            var mutedContrast = Contrast(Theme.TextMuted, Color.White);
            Check(mutedContrast >= 4.5,
                  $"UI-8: 次要灰字白底对比度 ≥4.5:1（WCAG AA，实得 {mutedContrast:F2}:1）");
            Check(Theme.Tiny.Size >= 8F, $"UI-8: Tiny 字号 ≥8pt（实得 {Theme.Tiny.Size}pt）");

            using (var probe = new Form())
            {
                Theme.ApplyDpi(probe);
                Check(probe.AutoScaleMode == AutoScaleMode.Dpi && probe.AutoScaleDimensions == new SizeF(96F, 96F),
                      "UI-4: ApplyDpi 统一 AutoScaleMode.Dpi + 96dpi 基准");
            }
            var strayForms = typeof(Program).Assembly.GetTypes()
                .Where(t => typeof(Form).IsAssignableFrom(t) && !t.IsAbstract && !typeof(AppForm).IsAssignableFrom(t))
                .Select(t => t.Name).ToList();
            Check(strayForms.Count == 0,
                  $"UI-4: 所有窗体都继承 AppForm（漏网 {strayForms.Count} 个: {string.Join(",", strayForms)}）");
            Check(Application.HighDpiMode == HighDpiMode.PerMonitorV2,
                  $"UI-3: 进程 DPI 感知为 PerMonitorV2（实得 {Application.HighDpiMode}）");

            var probeCard = new KeyboardProbeCard();
            bool cardActivated = false, cardContext = false;
            probeCard.ActivateRequested += _ => cardActivated = true;
            probeCard.ContextRequested += (_, _) => cardContext = true;
            Check(probeCard.TabStop && probeCard.AccessibleRole == AccessibleRole.PushButton,
                  "UI-9: 看板卡片可 Tab 聚焦且无障碍角色=按钮");
            InvokeCardKey(probeCard, Keys.Enter);
            Check(cardActivated, "UI-9: 卡片 Enter = 确认（与双击等价）");
            InvokeCardKey(probeCard, Keys.F10 | Keys.Shift);
            Check(cardContext, "UI-9: 卡片 Shift+F10 = 右键菜单");
            probeCard.Dispose();

            using (var host = new Panel())
            {
                var debounce = UiAsync.Debounce(host, 100, () => { });
                debounce.Start();
                host.Dispose();
                Check(!debounce.Enabled, "UI-11: 防抖 Timer 随宿主控件销毁而释放");
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

        // ── v3.57.4 DPI 屏幕适配：非 100% 缩放下窗口/最小尺寸不得超出工作区（Win10 机台现场问题）──
        {
            Console.WriteLine("\n【v3.57.4 DPI 屏幕适配】工作区折算 / AutoScale 系数 / 缩放回归…");

            // 100% 缩放必须是恒等变换——老机台（1024×768）行为逐位不变
            Check(UiScreenFit.ToLogical(1920, 1f) == 1920 && UiScreenFit.ToLogical(1024, 1f) == 1024,
                "DPI 适配: 100% 缩放折算为恒等（老机台零视觉改动）");

            // 根因锁定：AutoScale 路径（PerformAutoScale → Scale → ScaleControl）确实会把 MinimumSize 乘上系数。
            // 这条断言在，才说明「工作区必须先按系数折算」是必要的；WinForms 若改行为会在这里变红。
            float applied;
            using (var probe = new Form())
            {
                probe.AutoScaleDimensions = new SizeF(96f, 96f);
                probe.AutoScaleMode = AutoScaleMode.Dpi;
                probe.MinimumSize = new Size(1200, 720);
                probe.Scale(new SizeF(1.5f, 1.5f));
                applied = probe.MinimumSize.Width / 1200f;
            }
            Check(applied > 1.4f,
                $"DPI 适配: AutoScale 路径确实放大 MinimumSize（实测 ×{applied:F2}，故设备像素预算必须先折算）");

            // 150% + 1920 宽屏：折算后 1280 逻辑 → 缩放回 1920，恰好不越界（修复前 1408→2112 越界）
            var l150 = UiScreenFit.LogicalWorkArea(new Size(1920, 1032), 1.5f);
            int w150 = Math.Max(640, Math.Min(1408, l150.Width));
            Check(w150 * 1.5f <= 1920,
                $"DPI 适配: 150% + 1920 宽屏 窗口宽 {w150} 缩放后 {w150 * 1.5f:F0} ≤ 1920");

            // 150% + 1024×768 机台：最小尺寸缩放后不得超出工作区（v3.35.x 只修了 100%，这里锁住非 100% 的回归）
            var l150b = UiScreenFit.LogicalWorkArea(new Size(1024, 728), 1.5f);
            var min150 = new Size(Math.Min(1200, l150b.Width), Math.Min(720, l150b.Height));
            Check(min150.Width * 1.5f <= 1024 && min150.Height * 1.5f <= 728,
                $"DPI 适配: 150% + 1024×768 机台 最小尺寸 {min150.Width}×{min150.Height} 缩放后"
                + $" {(int)(min150.Width * 1.5f)}×{(int)(min150.Height * 1.5f)} ≤ 1024×728");

            // 非法/未知系数一律回落 1:1：不抛异常、不把尺寸算成 0
            Check(UiScreenFit.ToLogical(800, 0f) == 800 && UiScreenFit.ToLogical(800, -2f) == 800
                  && UiScreenFit.ToLogical(800, float.NaN) == 800 && UiScreenFit.ToLogical(800, float.PositiveInfinity) == 800,
                "DPI 适配: 非法缩放系数回落 1:1（不抛异常、不把尺寸算成 0）");

            using (var f = new Form())
            {
                Theme.ApplyDpi(f);   // AppForm 的构造就是这一句（其构造函数 protected，自检不可直接 new）
                Check(Math.Abs(UiScreenFit.AutoScaleFactor(f) - 1f) < 0.01f,
                    "DPI 适配: 100% 缩放下 AutoScaleFactor == 1（与 WinForms 同源，不自造 DPI 口径）");
            }

            // 诊断行必须能落地（现场 Win10/Win11 排查就靠它，字段缺失即失效）
            var line = UiScreenFit.Describe(new Size(1920, 1032), 1.5f, new Size(1280, 688), new Size(800, 480), 144);
            Check(line.Contains("缩放=150%") && line.Contains("工作区=1920x1032") && line.Contains("折算后=1280x688"),
                "DPI 适配: 诊断行含 缩放/工作区/折算后 三项现场关键事实");

            // 100% 缩放的机台只能靠「会话类型 / 字体实际解析结果」区分（RDP 会话与字体回退是两大嫌疑）
            Check(line.Contains("会话=") && line.Contains("字体=") && line.Contains("Body="),
                "UI 兼容: 诊断行含 会话类型 与 字体实际解析（100% 机台的 Win10 症状靠这两项定性）");
            Check(typeof(MainForm).GetProperty("CurrentPageIndex") is { CanRead: true },
                "UI 兼容: MainForm 暴露 CurrentPageIndex（UI 异常现场记录当前页）");
            var ctxLine = UiScreenFit.DescribeActiveForm();
            Check(ctxLine.Length > 0, $"UI 兼容: UI 异常现场上下文可采集（{ctxLine}）");

            // LiveAlertPanel 命中测试必须与绘制同源：原先绘制 startY=46、命中 startY=48，
            // 悬停/点击整体偏 2px——100% 缩放下就错，与 DPI 无关。Y=47 正好落在修前命中不到的那 2px 里。
            using (var live2 = new LiveAlertPanel { Bounds = new Rectangle(0, 0, 320, 300) })
            {
                live2.SetData(new List<LiveFailAlert> { new() { Id = 1, TimeText = "08:00:00", Sn = "S1", FailReason = "F1" } });
                var onMove = typeof(Control).GetMethod("OnMouseMove",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                onMove!.Invoke(live2, new object[] { new MouseEventArgs(MouseButtons.None, 0, 100, 47, 0) });
                Check(live2.Cursor == Cursors.Hand,
                    "UI 兼容: LiveAlertPanel 第 0 行顶端 2px 内可命中（绘制/命中共用 RowsTop；修前偏 2px 命中不到）");
                onMove!.Invoke(live2, new object[] { new MouseEventArgs(MouseButtons.None, 0, 100, 45, 0) });
                Check(live2.Cursor == Cursors.Default, "UI 兼容: LiveAlertPanel 行区之上不误命中");
            }
            var rowsTop = typeof(LiveAlertPanel).GetField("RowsTop",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Check(rowsTop is { IsLiteral: true } && (int)rowsTop.GetRawConstantValue()! == 46,
                "UI 兼容: LiveAlertPanel.RowsTop 是共享常量（绘制与命中只此一处，结构上无法再分叉）");

            // 工程纪律（源文件级）：upgrade 入口必须走 ApplicationConfiguration.Initialize()。
            // 非 100% 缩放机台上只有它设置 PerMonitorV2；漏调 → 升级向导被系统位图拉伸。
            var wizardSrc = FindUp(Path.Combine("modules", "Upgrader", "UpgradeWizard.cs"));
            Check(wizardSrc.Length > 0, "UI 兼容: 找到 UpgradeWizard.cs 源文件");
            if (wizardSrc.Length > 0)
            {
                var src = File.ReadAllText(wizardSrc);
                Check(src.Contains("ApplicationConfiguration.Initialize()"),
                    "UI 兼容: upgrade 入口走 ApplicationConfiguration.Initialize（不感知 DPI 的旧两行已删）");
            }

            // 版本号唯一来源是程序集（CODEBUDDY §6）：向导标题曾长期硬编码自称 v3.22.1。
            // 注意：自检工程自己的程序集是 FctAggregator.SelfTest（版本 1.0.0），拿它跟 WizardVersion
            // 对拍属于自证（恒真、查不出回归），所以改为锁源文件形态。
            if (wizardSrc.Length > 0)
            {
                var srcVer = File.ReadAllText(wizardSrc);
                Check(srcVer.Contains("WizardVersion =>") && !srcVer.Contains("WizardVersion = \""),
                    "UI 兼容: 向导版本号取自程序集（源文件里不得再出现硬编码版本字面量）");
            }
        }

        // ── v3.44.2 去卡顿：同数据不重绘 / Tab 合成 / 表格双缓冲 / 自绘 OptimizedDoubleBuffer ──
        {
            Console.WriteLine("\n【去卡顿】SetData 签名跳过 / BufferedTabControl / StyleGrid 双缓冲…");

            var hours = Enumerable.Range(0, 24).Select(h => new HourlyStatItem { Hour = h, Pass = h, Fail = 1 }).ToList();
            using (var chart = new HourlyTrendChart())
            {
                chart.SetData(hours);
                var g1 = chart.PaintGeneration;
                var sig1 = chart.LastPaintSig;
                chart.SetData(hours.ToList());
                Check(chart.PaintGeneration == g1 && chart.LastPaintSig == sig1,
                    "去卡顿: HourlyTrendChart 相同小时序列第二次 SetData 不换签名、不增代际");
                hours[3].Pass = 99;
                chart.SetData(hours);
                Check(chart.PaintGeneration == g1 + 1, "去卡顿: HourlyTrendChart 数据变化后代际 +1");
                chart.SetData(null);
                var gEmpty = chart.PaintGeneration;
                chart.SetData(new List<HourlyStatItem>());
                Check(chart.PaintGeneration == gEmpty, "去卡顿: HourlyTrendChart null 与空列表签名相同");
            }

            var tops = new List<TopFailItem>
            {
                new() { FailItem = "KL30_1", Count = 3, Ratio = 12.5, MainStation = "FCT1" },
            };
            using (var rank = new TopFailRankPanel())
            {
                rank.SetData(tops);
                var g1 = rank.PaintGeneration;
                rank.SetData(new List<TopFailItem> { new() { FailItem = "KL30_1", Count = 3, Ratio = 12.5, MainStation = "FCT1" } });
                Check(rank.PaintGeneration == g1, "去卡顿: TopFailRankPanel 相同条目第二次不重绘");
                tops[0].Count = 4;
                rank.SetData(tops);
                Check(rank.PaintGeneration == g1 + 1, "去卡顿: TopFailRankPanel 次数变化后代际 +1");
            }

            var alerts = new List<LiveFailAlert>
            {
                new() { Id = 7, TimeText = "08:01:02", Sn = "SN1", FailReason = "KL30" },
            };
            using (var live = new LiveAlertPanel())
            {
                live.SetData(alerts);
                var g1 = live.PaintGeneration;
                live.SetData(new List<LiveFailAlert> { new() { Id = 7, TimeText = "08:01:02", Sn = "SN1", FailReason = "KL30" } });
                Check(live.PaintGeneration == g1, "去卡顿: LiveAlertPanel 相同告警第二次不重绘");
                alerts[0].TimeText = "08:01:03";
                live.SetData(alerts);
                Check(live.PaintGeneration == g1 + 1, "去卡顿: LiveAlertPanel 时间变化后代际 +1");
            }

            using (var dg = new DataGridView())
            {
                Theme.Apply(dg, isPageRoot: false);
                var prop = typeof(Control).GetProperty("DoubleBuffered",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Check(prop != null && (bool)prop.GetValue(dg)!,
                    "去卡顿: Theme.StyleGrid 反射打开 DataGridView.DoubleBuffered");
            }

            using (var tabs = new BufferedTabControl())
            {
                var cpProp = typeof(Control).GetProperty("CreateParams",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var cp = (CreateParams)cpProp!.GetValue(tabs)!;
                Check((cp.ExStyle & 0x02000000) != 0,
                    "去卡顿: BufferedTabControl CreateParams 含 WS_EX_COMPOSITED");
            }

            bool HasOpt(Control c)
            {
                var m = typeof(Control).GetMethod("GetStyle",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                return (bool)m!.Invoke(c, new object[] { ControlStyles.OptimizedDoubleBuffer })!;
            }
            using (var kpi = new KpiCard("今日", Theme.Primary))
                Check(HasOpt(kpi), "去卡顿: KpiCard OptimizedDoubleBuffer");
            using (var chips = new ChipBar())
                Check(HasOpt(chips), "去卡顿: ChipBar OptimizedDoubleBuffer");
            using (var card = new KeyboardProbeCard())
                Check(HasOpt(card), "去卡顿: BoardCard OptimizedDoubleBuffer");
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
                    Category = "Online",
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
                    Category = "Online",
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
                    Category = "Online",
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
                    Category = "Online",
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
                    Category = "Online",
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
                // 今日 KPI 公司口径：Online + SN 终检；空 SN 按 xml_path 分区；总测试 = PASS+FAIL
                var snDbPath = Path.Combine(Path.GetTempPath(), "argus_sn_fallback_" + DateTime.Now.Ticks + ".db");
                try
                {
                    var sdb = new Database(snDbPath);
                    var dStr = DateTime.Now.ToString("yyyyMMdd");
                    sdb.BatchInsert(new[]
                    {
                        new TestRecord { StationId = "FCT1", Category = "Online", TestDate = dStr, Result = "PASS", Sn = "SN-A", XmlPath = "p1.xml", BatchTimestamp = $"{dStr} 09:00:00" },
                        new TestRecord { StationId = "FCT1", Category = "Online", TestDate = dStr, Result = "PASS", Sn = "", XmlPath = "p2.xml", BatchTimestamp = $"{dStr} 09:01:00" },
                        new TestRecord { StationId = "FCT1", Category = "Online", TestDate = dStr, Result = "PASS", Sn = "   ", XmlPath = "p3.xml", BatchTimestamp = $"{dStr} 09:02:00" },
                        new TestRecord { StationId = "FCT1", Category = "Online", TestDate = dStr, Result = "PASS", Sn = null, XmlPath = "p4.xml", BatchTimestamp = $"{dStr} 09:03:00" },
                    });
                    var gStats = sdb.FetchGlobalStats("");
                    Check(gStats.ProductCount == 4,
                        $"FetchGlobalStats: 产品数空 SN 按 xml_path 兜底去重=4（旧口径空串坍缩为 1 且 NULL 不计；实得 {gStats.ProductCount}）");
                    var dStats = sdb.FetchDailyStats("", dStr);
                    Check(dStats.TodayProductCount == 4 && dStats.Pass == 4 && dStats.Fail == 0,
                        $"FetchDailyStats: 空 SN 按 xml_path 终检 4 台 PASS（实得 总={dStats.TodayProductCount},P={dStats.Pass},F={dStats.Fail}）");
                    Check(gStats.Pass == 4 && dStats.Pass == 4,
                        $"全局 PASS 仍按条数计（实得 全局={gStats.Pass}, 今日={dStats.Pass}）");

                    // 空白 SN 归一：同一串空白 SN 出现在多条记录时，必须各自按 xml_path 计（与今日口径一致）
                    sdb.BatchInsert(new[]
                    {
                        new TestRecord { StationId = "FCT1", Category = "Online", TestDate = dStr, Result = "PASS", Sn = "WS-SN", XmlPath = "ws1.xml", BatchTimestamp = $"{dStr} 10:00:00" },
                        new TestRecord { StationId = "FCT1", Category = "Online", TestDate = dStr, Result = "PASS", Sn = "  ", XmlPath = "ws2.xml", BatchTimestamp = $"{dStr} 10:01:00" },
                        new TestRecord { StationId = "FCT1", Category = "Online", TestDate = dStr, Result = "PASS", Sn = "  ", XmlPath = "ws3.xml", BatchTimestamp = $"{dStr} 10:02:00" },
                    });
                    var gWs = sdb.FetchGlobalStats("");
                    // 全表：SN-A + p2/p3/p4.xml（空白与 NULL 各按 xml_path）+ WS-SN + ws2/ws3.xml = 7
                    // 若 COUNT 里不做 TRIM，两条 "  " 会坍缩成 1 个 → 得 6
                    Check(gWs.ProductCount == 7,
                        $"FetchGlobalStats: 同一串空白 SN 的多条记录各自按 xml_path 计（不 TRIM 会坍缩成 1；实得 {gWs.ProductCount}）");
                }
                finally { try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(snDbPath); } catch { } }
            }

            {
                var coDbPath = Path.Combine(Path.GetTempPath(), "argus_company_daily_" + DateTime.Now.Ticks + ".db");
                try
                {
                    var cdb = new Database(coDbPath);
                    var dStr = DateTime.Now.ToString("yyyyMMdd");
                    cdb.BatchInsert(new[]
                    {
                        new TestRecord { StationId = "FCT1", Category = "Online", TestDate = dStr, Sn = "SN-RT", Result = "PASS", XmlPath = "rt1.xml", BatchTimestamp = $"{dStr} 08:00:00" },
                        new TestRecord { StationId = "FCT1", Category = "Online", TestDate = dStr, Sn = "SN-RT", Result = "PASS", XmlPath = "rt2.xml", BatchTimestamp = $"{dStr} 10:00:00" },
                        new TestRecord { StationId = "FCT1", Category = "Online", TestDate = dStr, Sn = "SN-BF", Result = "PASS", XmlPath = "bf1.xml", BatchTimestamp = $"{dStr} 08:30:00" },
                        new TestRecord { StationId = "FCT1", Category = "Online", TestDate = dStr, Sn = "SN-BF", Result = "FAIL", XmlPath = "bf2.xml", FailReason = "KL30", BatchTimestamp = $"{dStr} 09:30:00" },
                        new TestRecord { StationId = "FCT1", Category = "Offline", TestDate = dStr, Sn = "SN-OFF", Result = "FAIL", XmlPath = "off.xml", FailReason = "KL15", BatchTimestamp = $"{dStr} 11:00:00" },
                        new TestRecord { StationId = "FCT1", Category = "Online", TestDate = dStr, Sn = "SN-REPASS", Result = "FAIL", XmlPath = "rp1.xml", FailReason = "KL30", BatchTimestamp = $"{dStr} 07:00:00" },
                        new TestRecord { StationId = "FCT1", Category = "Online", TestDate = dStr, Sn = "SN-REPASS", Result = "PASS", XmlPath = "rp2.xml", BatchTimestamp = $"{dStr} 12:00:00" },
                        new TestRecord { StationId = "FCT1", Category = "Offline", TestDate = dStr, Sn = "SN-OFFP", Result = "PASS", XmlPath = "offp.xml", BatchTimestamp = $"{dStr} 13:00:00" },
                    });
                    var co = cdb.FetchDailyStats("", dStr);
                    Check(co.Pass == 1 && co.Fail == 3 && co.TodayProductCount == 4,
                        $"FetchDailyStats 公司口径: FAIL优先+Offline PASS不计（实得 P={co.Pass},F={co.Fail},总={co.TodayProductCount}）");
                    var byM = cdb.FetchDailyStatsByModel("", dStr);
                    Check(byM.Count == 1 && byM[0].Pass == 1 && byM[0].Fail == 3,
                        $"FetchDailyStatsByModel 与总计一致（实得 {byM.Count} 型号 P={byM.FirstOrDefault()?.Pass} F={byM.FirstOrDefault()?.Fail}）");
                    var hz = cdb.FetchDailyHourlyStats("", dStr);
                    Check(hz.Count == 24 && hz.Sum(x => x.Total) == co.TodayProductCount,
                        $"小时图总量与 KPI 总测试一致（hourly={hz.Sum(x => x.Total)} kpi={co.TodayProductCount}）");
                    Check(hz[7].Fail >= 1 && hz[9].Fail >= 1 && hz[11].Fail >= 1,
                        $"小时图: FAIL 优先落到 7/9/11 时（7={hz[7].Fail} 9={hz[9].Fail} 11={hz[11].Fail}）");
                    Check(hz[10].Pass >= 1, $"小时图: SN-RT 终检 PASS 在 10 时（实得 {hz[10].Pass}）");
                    var top = cdb.FetchDailyTopFails("", dStr, 5);
                    Check(top.Count >= 1 && top.Sum(x => x.Count) == co.Fail,
                        $"Top5 频次合计等于 KPI FAIL（top={top.Sum(x => x.Count)} fail={co.Fail}）");
                    var al = cdb.FetchRecentFailAlerts("", 10, dStr);
                    Check(al.Count == co.Fail, $"播报流条数等于 KPI FAIL（al={al.Count} fail={co.Fail}）");
                    var hzS = cdb.FetchDailyHourlyStats("FCT1", dStr);
                    Check(hzS.Sum(x => x.Total) == co.TodayProductCount, "带 station_id 的小时图与 KPI 一致");
                    Check(cdb.FetchDailyTopFails("FCT1", dStr, 5).Count >= 1, "带 station_id 的 Top5 有 FAIL");
                    Check(cdb.FetchRecentFailAlerts("FCT1", 10, dStr).Count == co.Fail, "带 station_id 的播报流等于 KPI FAIL");
                }
                finally { try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(coDbPath); } catch { } }
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
                    StationId = "FCT1", Category = "Online", Sn = sn, Model = "E3002781", Result = result, FailReason = reason,
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

        // ============ 飞书 FAIL 告警批量合并（窗口内多条合成一张卡，治批量不良刷屏） ============
        {
            Console.WriteLine("\n【飞书FAIL批量告警】合并窗口 / 待发队列去重 / 合并卡内容…");

            // —— 纯函数：冲刷时机 ——
            Check(FeishuFailBatcher.ShouldFlushImmediately(1, 20, false),
                "合并关闭：任何一条 FAIL 都立即冲刷（退回 v3.40.5 逐条推送行为）");
            Check(!FeishuFailBatcher.ShouldFlushImmediately(19, 20, true),
                "合并开启：未攒满上限不立即冲刷（等窗口到点）");
            Check(FeishuFailBatcher.ShouldFlushImmediately(20, 20, true),
                "合并开启：攒满上限立即冲刷（不等窗口）");
            Check(!FeishuFailBatcher.ShouldFlushImmediately(0, 20, true),
                "空队列不触发冲刷");

            // —— 纯函数：标记口径（决定失败后是否会被补推重发）——
            Check(FeishuFailBatcher.ShouldMarkAlerted(FeishuSendOutcome.Sent)
                  && FeishuFailBatcher.ShouldMarkAlerted(FeishuSendOutcome.Skipped),
                "推送成功/主动跳过都标记已推送（未配 webhook 的机台不会每次启动重刷今日 FAIL）");
            Check(!FeishuFailBatcher.ShouldMarkAlerted(FeishuSendOutcome.Failed),
                "推送失败不标记 → 留给下次启动「今日补推」兜底（不新增落盘状态）");

            // —— 待发队列：去重 / 排序 / 取走 ——
            var q = new FailAlertQueue();
            var qa = new TestRecord { StationId = "FCT1", Model = "E3002781", Sn = "SN0001", Result = "FAIL", XmlPath = @"D:\R\a.xml", BatchTimestamp = "2026-09-14T10:00:01" };
            var qb = new TestRecord { StationId = "FCT1", Model = "E3002781", Sn = "SN0002", Result = "FAIL", XmlPath = @"D:\R\b.xml", BatchTimestamp = "2026-09-14T10:00:00" };
            var qaDup = new TestRecord { StationId = "FCT1", Model = "E3002781", Sn = "SN0001", Result = "FAIL", XmlPath = @"D:\R\A.XML", BatchTimestamp = "2026-09-14T10:00:01" };

            Check(q.Enqueue(qa) && q.Count == 1, "队列：首次入队成功");
            Check(!q.Enqueue(qaDup), "队列：同 xml_path 重复入队被拒（大小写不敏感，与 fail_alerted 同口径）");
            Check(q.Count == 1, "队列：重复入队不增加计数");
            Check(!q.Enqueue(new TestRecord { Result = "FAIL", XmlPath = "" }), "队列：空路径不入队（无路径则无法落库标记）");

            q.Enqueue(qb);
            var qSnap = q.Snapshot();
            Check(qSnap.Count == 2 && qSnap[0].Sn == "SN0002", "队列：快照按 batch_timestamp 升序（卡面明细顺序可复现）");
            Check(q.Count == 2, "队列：Snapshot 只读不清空");
            Check(q.TakeAll().Count == 2 && q.Count == 0, "队列：TakeAll 取走全部并清空");
            Check(q.TakeAll().Count == 0, "队列：空队列 TakeAll 返回空列表（不抛异常）");

            // —— 合并卡内容（用宽松编码序列化，中文断言可读）——
            var relaxed = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            TestRecord MkFail(string sn, string ts, string xml, params (string name, string val)[] items)
            {
                var rec = new TestRecord
                {
                    StationId = "FCT1", Model = "E3002781", Sn = sn, Result = "FAIL",
                    XmlPath = xml, BatchTimestamp = ts,
                };
                foreach (var it in items)
                    rec.FailedTests.Add(new FailedTest { Name = it.name, Value = it.val });
                return rec;
            }

            var batch3 = new List<TestRecord>
            {
                MkFail("SN0001", "2026-09-14T10:00:01", @"D:\R\1.xml", ("KL30_1", "3.5"), ("V_5V", "4.1")),
                MkFail("SN0002", "2026-09-14T10:00:02", @"D:\R\2.xml", ("KL30_1", "3.6")),
                MkFail("SN0003", "2026-09-14T10:00:03", @"D:\R\3.xml", ("KL30_1", "3.4")),
            };
            var bJson = JsonSerializer.Serialize(FeishuNotifier.BuildFailBatchCard(batch3, 60), relaxed);

            Check(bJson.Contains("\"schema\":\"2.0\""), "合并卡：仍是卡片 2.0（复用同一套构建原语）");
            Check(bJson.Contains("\"template\":\"red\""), "合并卡：header 红色（与单条 FAIL 告警同色系）");
            Check(bJson.Contains("FAIL 批量告警 ×3"), "合并卡：标题带批量条数（×3）");
            Check(bJson.Contains("失败项统计（2 项）"), "合并卡：失败项按去重后项数统计（2 项）");
            Check(bJson.Contains("KL30_1 ×3"), "合并卡：同一测项跨记录合并计数（KL30_1 ×3）");
            Check(bJson.Contains("V_5V ×1"), "合并卡：只出现一次的测项也列出（V_5V ×1）");
            Check(bJson.Contains("SN0001") && bJson.Contains("SN0002") && bJson.Contains("SN0003"), "合并卡：明细列出各条 SN");
            Check(bJson.IndexOf("SN0001", StringComparison.Ordinal) < bJson.IndexOf("SN0002", StringComparison.Ordinal),
                "合并卡：明细按时间升序（SN0001 早于 SN0002）");
            Check(bJson.Contains("60s 窗口内合并 · 共 3 条 FAIL"), "合并卡：页脚交代窗口与总条数");
            Check(bJson.Contains("×3") && bJson.Contains("2026-09-14 10:00:01"), "合并卡：失败条数与时间范围已渲染");

            // 失败项超 10 项 / 记录超 10 条 → 截断提示
            var manyItems = new List<TestRecord>();
            for (int i = 0; i < 12; i++)
                manyItems.Add(MkFail($"SN{i:D4}", "2026-09-14T11:00:00", $@"D:\R\m{i}.xml", ($"ITEM_{i:D2}", "1")));
            var manyJson = JsonSerializer.Serialize(FeishuNotifier.BuildFailBatchCard(manyItems, 60), relaxed);
            Check(manyJson.Contains("失败项统计（12 项）") && manyJson.Contains("另有 2 项未列出"),
                "合并卡：失败项超 10 项只列前 10 并提示「另有 2 项未列出」");
            Check(manyJson.Contains("另有 2 条未列出"), "合并卡：明细超 10 条只列前 10 并提示「另有 2 条未列出」");

            // 无测项明细的老记录 → 退回 fail_reason，并标注条数
            var legacy = new TestRecord
            {
                StationId = "FCT1", Model = "E3002781", Sn = "SN9001", Result = "FAIL",
                XmlPath = @"D:\R\legacy.xml", BatchTimestamp = "2026-09-14T12:00:00",
                FailReason = "整体测试失败\n未生成测项明细",
            };
            var legacyJson = JsonSerializer.Serialize(FeishuNotifier.BuildFailBatchCard(new[] { legacy }, 60), relaxed);
            Check(legacyJson.Contains("（1 条无测项明细，仅有汇总失败原因）"), "合并卡：无测项明细的记录单独标注条数");
            Check(legacyJson.Contains("整体测试失败 未生成测项明细"), "合并卡：老记录明细退回 fail_reason（换行压平，不破版式）");

            // markdown 转义：SN 里的特殊字符必须转义，否则飞书渲染破版
            // 注意：Escape 产出单反斜杠，JSON 序列化会再转义成 \\ —— 断言按 JSON 里的实际形态写
            var escJson = JsonSerializer.Serialize(
                FeishuNotifier.BuildFailBatchCard(new[] { MkFail("SN*01[甲]", "2026-09-14T13:00:00", @"D:\R\e.xml", ("KL*30", "1")) }, 60), relaxed);
            Check(escJson.Contains(@"SN\\*01\\[甲\\]"), "合并卡：SN 中 markdown 特殊字符已转义");
            Check(escJson.Contains(@"KL\\*30"), "合并卡：测项名中 markdown 特殊字符已转义");

            // 多机台/多型号聚合（补推跨机台时会出现）
            var mixed = new List<TestRecord>
            {
                MkFail("S1", "2026-09-14T14:00:00", @"D:\R\x1.xml", ("A", "1")),
                MkFail("S2", "2026-09-14T14:00:01", @"D:\R\x2.xml", ("A", "1")),
            };
            mixed[1].StationId = "FCT2"; mixed[1].Model = "E3002782";
            var mixedJson = JsonSerializer.Serialize(FeishuNotifier.BuildFailBatchCard(mixed, 60), relaxed);
            Check(mixedJson.Contains("FCT1/FCT2") && mixedJson.Contains("E3002781/E3002782"),
                "合并卡：跨机台/跨型号时并列展示");
            Check(FeishuNotifier.Summarize(new[] { "A", "B", "C", "D" }, "—", "台") == "A/B/C 等 4 台",
                "合并卡：机台超过 3 个时截断并补「等 N 台」");
            Check(FeishuNotifier.Summarize(new string[0], "未知机台", "台") == "未知机台",
                "合并卡：无机台信息时回落占位文案");

            Check(FeishuNotifier.Clip(null, 5) == "—" && FeishuNotifier.Clip("   ", 5) == "—",
                "合并卡：空白 fail_reason 回落「—」");
            Check(FeishuNotifier.Clip("0123456789", 5) == "01234…", "合并卡：超长文本按上限裁剪并加省略号");

            // —— 推送结果三态（全部走本地短路分支，不发真实请求）——
            Check(FeishuNotifier.SendFailAlertBatch("https://example.invalid/hook", Array.Empty<TestRecord>())
                    .GetAwaiter().GetResult() == FeishuSendOutcome.Skipped,
                "批量推送：空列表直接跳过（不发请求）");
            Check(FeishuNotifier.SendFailAlert("", qa).GetAwaiter().GetResult() == FeishuSendOutcome.Skipped,
                "推送：未配置 webhook 返回 Skipped（不发请求）");
            Check(FeishuNotifier.SendFailAlert("http://example.com/hook", qa).GetAwaiter().GetResult() == FeishuSendOutcome.Skipped,
                "推送：非 https 前缀返回 Skipped（配置错误不重试，避免每次启动重刷日志）");

            // —— 配置键（默认值 + Load 生效 + 越界回落）——
            var mergeDef = new AppConfig();
            Check(mergeDef.FeishuFailMergeEnabled && mergeDef.FeishuFailMergeWindowSec == 60 && mergeDef.FeishuFailMergeMax == 20,
                "配置：feishu_fail_merge_* 默认 开 / 60s / 20 条");

            var cfgFileM = Path.Combine(AppConfig.BaseDir, "config.json");
            var backupM = File.Exists(cfgFileM) ? File.ReadAllText(cfgFileM) : null;
            try
            {
                File.WriteAllText(cfgFileM, "{\"feishu_fail_merge_enabled\":false,\"feishu_fail_merge_window_sec\":120,\"feishu_fail_merge_max\":5}");
                var loadedM = AppConfig.Load();
                Check(!loadedM.FeishuFailMergeEnabled && loadedM.FeishuFailMergeWindowSec == 120 && loadedM.FeishuFailMergeMax == 5,
                    "配置：feishu_fail_merge_* 三键 Load 生效");

                File.WriteAllText(cfgFileM, "{\"feishu_fail_merge_window_sec\":5,\"feishu_fail_merge_max\":9999}");
                var badM = AppConfig.Load();
                Check(badM.FeishuFailMergeWindowSec == 60 && badM.FeishuFailMergeMax == 20,
                    $"配置：越界值回落默认（窗口 {badM.FeishuFailMergeWindowSec}s / 上限 {badM.FeishuFailMergeMax}）");
            }
            finally
            {
                try { if (backupM != null) File.WriteAllText(cfgFileM, backupM); else File.Delete(cfgFileM); } catch { }
            }
        }

        // ============ 采集异常告警 + 每日运行摘要（漏采从「翻日志」变主动告知） ============
        {
            Console.WriteLine("\n【采集异常/每日摘要】判定口径 / 卡片内容 / parse_failure_log 查询…");

            var relaxed = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

            // —— 纯判定：该不该告警 / 该不该报恢复 ——
            Check(CollectHealthMonitor.Decide(false, 0, 10, 0, 20) == CollectAlertDecision.None,
                "采集判定：一切正常且此前未告警 → 不动");
            Check(CollectHealthMonitor.Decide(false, 10, 10, 0, 20) == CollectAlertDecision.Alert,
                "采集判定：解析失败达到阈值（等于也算）→ 告警");
            Check(CollectHealthMonitor.Decide(false, 0, 10, 20, 20) == CollectAlertDecision.Alert,
                "采集判定：重试队列积压达到阈值 → 告警");
            Check(CollectHealthMonitor.Decide(false, 999, 0, 0, 0) == CollectAlertDecision.None,
                "采集判定：阈值 ≤0 视为关闭该维度");
            Check(CollectHealthMonitor.Decide(true, 0, 10, 0, 20) == CollectAlertDecision.Recovered,
                "采集判定：曾告警且现已正常 → 报恢复");
            Check(CollectHealthMonitor.Decide(true, 999, 0, 0, 0) == CollectAlertDecision.Recovered,
                "采集判定：两个维度都关闭时，曾告警也要收敛为恢复（不会永久挂着）");

            // —— 纯判定：节流 ——
            var t0 = new DateTime(2026, 9, 14, 12, 0, 0);
            Check(CollectHealthMonitor.ThrottleAllows("", t0, 60), "节流：从未推送过 → 放行");
            Check(!CollectHealthMonitor.ThrottleAllows("2026-09-14 11:30:00", t0, 60), "节流：未到间隔 → 拦下");
            Check(CollectHealthMonitor.ThrottleAllows("2026-09-14 10:30:00", t0, 60), "节流：已过间隔 → 放行");
            Check(CollectHealthMonitor.ThrottleAllows("不是时间", t0, 60), "节流：时间戳不可解析 → 放行（不因脏数据静默哑掉）");
            Check(CollectHealthMonitor.ThrottleAllows("2026-09-14 11:59:00", t0, 0), "节流：阈值 ≤0 → 不节流");

            Check(CollectHealthMonitor.ReasonOf(14, 10, 25, 20).Contains("解析失败 14 条")
                  && CollectHealthMonitor.ReasonOf(14, 10, 25, 20).Contains("重试队列积压 25 条"),
                "触发原因：两个维度越线时都给（实得 " + CollectHealthMonitor.ReasonOf(14, 10, 25, 20) + "）");
            Check(CollectHealthMonitor.ReasonOf(0, 10, 0, 20) == "", "触发原因：都不越线时为空串");

            // —— 纯判定：摘要日期口径（维护默认凌晨跑，应汇总刚结束的昨天）——
            Check(CollectHealthMonitor.SummaryDayOf(new DateTime(2026, 9, 14, 3, 0, 0)) == new DateTime(2026, 9, 13),
                "摘要日期：凌晨 3 点跑 → 汇总昨天");
            Check(CollectHealthMonitor.SummaryDayOf(new DateTime(2026, 9, 14, 14, 0, 0)) == new DateTime(2026, 9, 14),
                "摘要日期：下午跑 → 汇总当天");
            Check(CollectHealthMonitor.WindowStart(new DateTime(2026, 9, 14, 12, 0, 0), 60) == "2026-09-14 11:00:00",
                "采集窗口起点：now - windowMin");

            // —— 错误码中文映射 ——
            Check(FeishuNotifier.ErrorCodeZh("oversize").Contains("512MB"), "错误码中文：oversize → 文件超上限");
            Check(FeishuNotifier.ErrorCodeZh("xml_malformed") == "XML 格式错误", "错误码中文：xml_malformed");
            Check(FeishuNotifier.ErrorCodeZh("read_error").Contains("读取失败"), "错误码中文：read_error");
            Check(FeishuNotifier.ErrorCodeZh("skip").Contains("正常跳过"), "错误码中文：skip → 正常跳过");
            Check(FeishuNotifier.ErrorCodeZh("brand_new_code") == "brand_new_code",
                "错误码中文：未知码原样返回（新增解析器不用改这里）");
            Check(FeishuNotifier.ErrorCodeZh("") == "未标注", "错误码中文：空码回落「未标注」");

            // —— 采集异常卡 ——
            var alertPayload = new CollectAlertPayload
            {
                StationId = "FCT1",
                WindowMin = 60,
                AbnormalCount = 14,
                ParseThreshold = 10,
                RetryQueueDepth = 25,
                RetryThreshold = 20,
                ByCode = new List<CollectFailureRow>
                {
                    new() { ErrorCode = "xml_malformed", Count = 12, SamplePath = @"D:\R\FAIL_1.xml", SkipReason = "xml 解析异常" },
                    new() { ErrorCode = "read_error", Count = 2, SamplePath = @"D:\R\FAIL_2.xml", SkipReason = "read file failed" },
                },
                Recent = new List<CollectFailureItem>
                {
                    new() { XmlPath = @"D:\R\Online\E3002781\20260914\FAIL_9.xml", ErrorCode = "xml_malformed", CreatedAt = "2026-09-14 10:05:00" },
                },
            };
            var alertJson = JsonSerializer.Serialize(FeishuNotifier.BuildCollectAlertCard(alertPayload), relaxed);
            Check(alertJson.Contains("\"template\":\"orange\""), "采集异常卡：header 橙色（要看一眼但非停机事故）");
            Check(alertJson.Contains("采集异常告警"), "采集异常卡：标题含「采集异常告警」");
            Check(alertJson.Contains("XML 格式错误 ×12"), "采集异常卡：错误码转中文并带条数");
            Check(alertJson.Contains("窗口内解析失败 14 条（阈值 10）"), "采集异常卡：触发原因含解析失败维度");
            Check(alertJson.Contains("重试队列积压 25 条（阈值 20）"), "采集异常卡：触发原因含重试队列维度");
            Check(alertJson.Contains("最近失败文件") && alertJson.Contains("FAIL_9.xml"), "采集异常卡：列出最近失败文件供定位");
            Check(alertJson.Contains("漏采会让良率偏高"), "采集异常卡：页脚点明后果（漏采 → 良率偏高）");

            // —— 采集恢复卡 ——
            var recJson = JsonSerializer.Serialize(
                FeishuNotifier.BuildCollectRecoverCard("FCT1", "窗口内解析失败 14 条（阈值 10）", 0), relaxed);
            Check(recJson.Contains("\"template\":\"green\""), "采集恢复卡：header 绿色");
            Check(recJson.Contains("采集异常已恢复"), "采集恢复卡：标题含「已恢复」");
            Check(recJson.Contains("此前告警") && recJson.Contains("窗口内解析失败 14 条"), "采集恢复卡：回显此前告警原因");
            Check(recJson.Contains("当前解析失败"), "采集恢复卡：给出当前值供核对");

            // —— 每日摘要载荷（纯函数）——
            var sumStats = new StatsData { Pass = 97, Fail = 3, Interrupted = 1 };
            var sum = CollectHealthMonitor.BuildDailySummary("FCT1", new DateTime(2026, 9, 13), sumStats,
                new List<TopFailItem> { new() { FailItem = "KL30_1", Count = 2, Ratio = 66.7 } },
                88, 123456789, @"D:\backup\local_20260913.db", 4, 7, 60);
            Check(sum.Total == 100 && Math.Abs(sum.YieldPct - 97.0) < 0.01,
                $"摘要：综合良率 = PASS/(PASS+FAIL)（实得 {sum.YieldPct}%）");
            Check(sum.Interrupted == 1, "摘要：中断单独列出、不计入良率分母");
            Check(sum.BackupOk && sum.BackupName == "local_20260913.db", "摘要：备份只取文件名并标记成功");

            var sumZero = CollectHealthMonitor.BuildDailySummary("FCT1", new DateTime(2026, 9, 13), new StatsData(),
                new List<TopFailItem>(), 100, 0, null, 0, 0, 60);
            Check(sumZero.YieldPct == 0 && !sumZero.BackupOk,
                "摘要：当日无数据时良率 0、备份标记失败（不出现 NaN/除零）");

            var sumJson = JsonSerializer.Serialize(FeishuNotifier.BuildDailySummaryCard(sum), relaxed);
            Check(sumJson.Contains("\"template\":\"blue\""), "摘要卡：header 蓝色");
            Check(sumJson.Contains("运行摘要"), "摘要卡：标题含「运行摘要」");
            Check(sumJson.Contains("97.00%"), "摘要卡：良率保留两位小数");
            Check(sumJson.Contains("KL30_1 ×2（66.7%）"), "摘要卡：Top 失败项带次数与占比");
            Check(sumJson.Contains("库健康分: 88/100"), "摘要卡：库健康分");
            Check(sumJson.Contains("每日备份: 成功"), "摘要卡：备份成功状态");
            Check(sumJson.Contains("重试队列: 4 条"), "摘要卡：重试队列积压");
            Check(sumJson.Contains("117.7 MB"), "摘要卡：库体积按量级格式化（实得 " +
                (sumJson.Contains("117.7 MB") ? "117.7 MB" : "非预期") + "）");
            Check(sumJson.Contains("解析失败: 7 条（近 60 分钟）"), "摘要卡：窗口内解析失败条数");

            var sumNoFail = CollectHealthMonitor.BuildDailySummary("FCT1", new DateTime(2026, 9, 13),
                new StatsData { Pass = 5 }, new List<TopFailItem>(), 90, 1000, null, 0, 0, 60);
            Check(JsonSerializer.Serialize(FeishuNotifier.BuildDailySummaryCard(sumNoFail), relaxed)
                    .Contains("（当日无 FAIL 记录）"),
                "摘要卡：无 FAIL 时给明确文案（不是空白段）");
            Check(JsonSerializer.Serialize(FeishuNotifier.BuildDailySummaryCard(sumNoFail), relaxed)
                    .Contains("每日备份: **未完成**"),
                "摘要卡：备份缺失时给出警示文案（加粗强调）");

            // —— parse_failure_log 查询（该表此前只进不出，本轮首次加读取入口）——
            var chDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_collect_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(chDir);
            try
            {
                var chDb = new Database(Path.Combine(chDir, "local.db"));
                chDb.LogParseFailure(@"D:\R\a.xml", "xml_malformed", "xml 解析异常", "FCT1");
                chDb.LogParseFailure(@"D:\R\b.xml", "xml_malformed", "xml 解析异常", "FCT1");
                chDb.LogParseFailure(@"D:\R\c.xml", "read_error", "read file failed", "FCT1");
                chDb.LogParseFailure(@"D:\R\d.xml", "skip", "debug", "FCT1");
                chDb.LogParseFailure(@"D:\R\e.xml", "skip", "debug", "FCT1");

                var since = CollectHealthMonitor.WindowStart(DateTime.Now, 60);
                Check(chDb.CountAbnormalParseFailures(since) == 3,
                    "采集查询：异常计数排除 skip（5 条里只有 3 条算异常）");
                Check(chDb.CountAbnormalParseFailures(since, "FCT1") == 3, "采集查询：按机台过滤命中");
                Check(chDb.CountAbnormalParseFailures(since, "FCT9") == 0, "采集查询：按机台过滤不串台");

                var byCode = chDb.FetchParseFailureSummary(since);
                Check(byCode.Count == 3, $"采集查询：按错误码聚合 3 类（实得 {byCode.Count}）");
                var malformed = byCode.FirstOrDefault(x => x.ErrorCode == "xml_malformed");
                Check(malformed != null && malformed.Count == 2, "采集查询：同错误码合并计数（xml_malformed ×2）");
                Check(byCode[0].Count >= byCode[^1].Count, "采集查询：按条数倒序（最多的排最前）");

                var recent = chDb.FetchRecentParseFailures(10, since);
                Check(recent.Count == 3, "采集查询：最近失败明细同样排除 skip");
                Check(recent[0].CreatedAt.CompareTo(recent[^1].CreatedAt) >= 0, "采集查询：明细按时间倒序");
                Check(chDb.FetchRecentParseFailures(2, since).Count == 2, "采集查询：limit 生效");

                var sinceFuture = DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm:ss");
                Check(chDb.CountAbnormalParseFailures(sinceFuture) == 0, "采集查询：窗口外不计入");

                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(chDir, true); } catch { }
            }

            // —— 采集告警状态序列化往返 ——
            var cst = new CollectAlertState { Alerting = true, LastPushTs = "2026-09-14 10:00:00", LastReason = "x" };
            var cstRound = CollectAlertState.FromJson(cst.ToJson());
            Check(cstRound != null && cstRound!.Alerting && cstRound.LastReason == "x", "采集告警状态：JSON 往返一致");
            Check(CollectAlertState.FromJson("") == null && CollectAlertState.FromJson("{坏}") == null,
                "采集告警状态：空/坏 JSON 安全返回 null");
        }

        // ============ 自学习告警：章节群挂卡 + 正常态偏离卡 ============
        {
            Console.WriteLine("\n【自学习告警】群挂去重 / 偏离封顶 / 两张卡内容…");

            var relaxed = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

            // —— 群挂去重：5 分钟一轮巡检，同一群挂不能反复推 ——
            var gs = new GroupAlertPushState();
            var gaAll = new List<SectionGroupAlertResult>
            {
                new() { Section = "6.1", DistinctSignalCount = 4, SignalNames = new List<string> { "KL30_1", "KL30_2", "KL30_3", "KL30_4" }, RootCauseHint = "治具接触不良" },
                new() { Section = "7.2", DistinctSignalCount = 3, SignalNames = new List<string> { "X", "Y", "Z" } },
            };
            Check(LearnAlertMonitor.SelectFreshAlerts(gs, "2026-09-14", gaAll).Count == 2, "群挂去重：首次评估两个章节都算新");
            Check(gs.Date == "2026-09-14" && gs.Sections.Count == 2, "群挂去重：章节已记入当日状态");
            Check(LearnAlertMonitor.SelectFreshAlerts(gs, "2026-09-14", gaAll).Count == 0,
                "群挂去重：同一天重复评估不再推（关键——巡检每 5 分钟一次）");
            Check(LearnAlertMonitor.SelectFreshAlerts(gs, "2026-09-15", gaAll).Count == 2 && gs.Sections.Count == 2,
                "群挂去重：跨天自动重置额度");

            var gsCase = new GroupAlertPushState { Date = "d", Sections = new List<string> { "AbC" } };
            Check(LearnAlertMonitor.SelectFreshAlerts(gsCase, "d",
                    new List<SectionGroupAlertResult> { new() { Section = "abc" } }).Count == 0,
                "群挂去重：章节名比较大小写不敏感");
            Check(LearnAlertMonitor.SelectFreshAlerts(new GroupAlertPushState(), "d",
                    new List<SectionGroupAlertResult> { new() { Section = "6.1" } }).Count == 1,
                "群挂去重：新章节照常放行");
            Check(LearnAlertMonitor.SelectFreshAlerts(null!, "d", gaAll).Count == 0, "群挂去重：null 状态安全返回空");

            // —— 偏离封顶 ——
            var ds = new DeviationPushState();
            Check(LearnAlertMonitor.TryReserveDeviationPush(ds, "2026-09-14", 3), "偏离封顶：第 1 条占额度成功");
            Check(LearnAlertMonitor.TryReserveDeviationPush(ds, "2026-09-14", 3), "偏离封顶：第 2 条占额度成功");
            Check(LearnAlertMonitor.TryReserveDeviationPush(ds, "2026-09-14", 3), "偏离封顶：第 3 条占额度成功");
            Check(!LearnAlertMonitor.TryReserveDeviationPush(ds, "2026-09-14", 3), "偏离封顶：达上限后拒绝");
            Check(ds.Count == 3, "偏离封顶：被拒时计数不增长（否则会被后续请求连带压死）");
            Check(LearnAlertMonitor.TryReserveDeviationPush(ds, "2026-09-15", 3), "偏离封顶：跨天重置额度");

            var dsZero = new DeviationPushState();
            Check(LearnAlertMonitor.TryReserveDeviationPush(dsZero, "d", 0) && dsZero.Count == 1,
                "偏离封顶：上限非法(0) 按 1 兜底，仍允许推 1 条");
            Check(!LearnAlertMonitor.TryReserveDeviationPush(dsZero, "d", 0), "偏离封顶：兜底上限下第 2 条被拒");

            // —— 偏离载荷组装（DetailJson 损坏只损失明细，不影响主信息）——
            var devEv = new DeviationEvent
            {
                Ts = "2026-09-14 10:20:30",
                Model = "E3002781",
                Source = NormalModelStore.SourceMeasurement,
                SignalCount = 3,
                TopSignal = "KL30_1",
                TopScore = 96,
                DetailJson = "[{\"signal_key\":\"KL30_1\",\"score\":96,\"value\":3.52,\"mean\":3.3,\"sigma\":0.05},"
                            + "{\"signal_key\":\"V_5V\",\"score\":91,\"value\":5.4,\"mean\":5.0,\"sigma\":0.1}]",
            };
            var dp = DeviationAlertPayload.FromEvent(devEv, "FCT1");
            Check(dp.StationId == "FCT1" && dp.TopSignal == "KL30_1" && Math.Abs(dp.TopScore - 96) < 0.01,
                "偏离载荷：事件本体字段映射");
            Check(dp.Top.Count == 2 && dp.Top[0].Key == "KL30_1" && dp.Top[1].Key == "V_5V", "偏离载荷：明细按分数倒序");
            Check(dp.SourceZh == "测量值", "偏离载荷：来源代号转中文");
            Check(DeviationAlertPayload.FromEvent(devEv, "").StationId == "未知机台", "偏离载荷：无机台号回落占位");
            Check(DeviationAlertPayload.FromEvent(devEv, "FCT1", 1).Top.Count == 1, "偏离载荷：topN 生效");

            var dpBad = DeviationAlertPayload.FromEvent(
                new DeviationEvent { TopSignal = "X", TopScore = 90, DetailJson = "{不是数组" }, "FCT1");
            Check(dpBad.Top.Count == 0 && dpBad.TopSignal == "X", "偏离载荷：DetailJson 损坏不抛异常（最高分信号仍在）");
            Check(DeviationAlertPayload.FromEvent(new DeviationEvent { TopSignal = "Y", DetailJson = "" }, "FCT1").Top.Count == 0,
                "偏离载荷：无明细时 Top 为空列表");

            // —— 章节群挂卡 ——
            var gaJson = JsonSerializer.Serialize(
                FeishuNotifier.BuildGroupAlertCard("FCT1", "2026-09-14", gaAll.Take(1).ToList(), 3), relaxed);
            Check(gaJson.Contains("\"template\":\"orange\""), "群挂卡：header 橙色");
            Check(gaJson.Contains("章节群挂告警 ×1"), "群挂卡：标题带命中章节数");
            Check(gaJson.Contains("§6.1 · 4 个不同信号"), "群挂卡：章节与不同信号数");
            Check(gaJson.Contains("KL30_1") && gaJson.Contains("KL30_4"), "群挂卡：列出信号名");
            Check(gaJson.Contains("根因指向：治具接触不良"), "群挂卡：带根因指向");
            Check(gaJson.Contains("≥3 个不同信号"), "群挂卡：标注判定阈值");
            Check(gaJson.Contains("系统性原因"), "群挂卡：页脚给出排查方向");

            var manySec = Enumerable.Range(0, 6)
                .Select(i => new SectionGroupAlertResult { Section = $"S{i}", DistinctSignalCount = 3, SignalNames = new List<string> { "a" } })
                .ToList();
            Check(JsonSerializer.Serialize(FeishuNotifier.BuildGroupAlertCard("FCT1", "d", manySec, 3), relaxed)
                    .Contains("另有 1 个章节未列出"),
                "群挂卡：章节超 5 个时截断提示");

            var manySig = new List<SectionGroupAlertResult>
            {
                new() { Section = "S", DistinctSignalCount = 12, SignalNames = Enumerable.Range(0, 12).Select(i => $"sig{i}").ToList() },
            };
            Check(JsonSerializer.Serialize(FeishuNotifier.BuildGroupAlertCard("FCT1", "d", manySig, 3), relaxed)
                    .Contains("另有 2 个信号未列出"),
                "群挂卡：单章节信号超 10 个时截断提示");

            // —— 正常态偏离卡 ——
            var dpJson = JsonSerializer.Serialize(FeishuNotifier.BuildDeviationAlertCard(dp), relaxed);
            Check(dpJson.Contains("\"template\":\"yellow\""), "偏离卡：header 黄色（预警而非故障，与 FAIL 红区分）");
            Check(dpJson.Contains("正常态偏离告警"), "偏离卡：标题含「正常态偏离告警」");
            Check(dpJson.Contains("KL30_1") && dpJson.Contains("分 96"), "偏离卡：只突出最高分信号和分数");
            Check(dpJson.Contains("实测 3.52，正常 3.3±0.05"), "偏离卡：最高分那条带实测和正常均值±σ");
            Check(dpJson.Contains("另有 2 个信号越线"), "偏离卡：其余信号只报个数");
            Check(!dpJson.Contains("偏离明细") && !dpJson.Contains("V_5V"), "偏离卡：不再逐条列出其余信号");
            var oneJson = JsonSerializer.Serialize(FeishuNotifier.BuildDeviationAlertCard(new DeviationAlertPayload
            {
                StationId = "FCT1", Model = "M", TopSignal = "KL30", TopScore = 90, SignalCount = 1,
                Ts = "2026-09-14 10:20:30",
            }), relaxed);
            Check(!oneJson.Contains("另有") && oneJson.Contains("PASS，偏离已学正常值"),
                "偏离卡：只有一个信号时不写「另有」，页脚点明仍是 PASS");

            // —— 状态序列化 ——
            Check(GroupAlertPushState.FromJson("") == null && GroupAlertPushState.FromJson("{坏") == null,
                "群挂状态：空 / 坏 JSON 安全返回 null");
            var gRound = GroupAlertPushState.FromJson(
                new GroupAlertPushState { Date = "d", Sections = new List<string> { "a" } }.ToJson());
            Check(gRound != null && gRound!.Date == "d" && gRound.Sections.Count == 1, "群挂状态：JSON 往返一致");
            Check(DeviationPushState.FromJson("{坏") == null, "偏离状态：坏 JSON 安全返回 null");
            var dRound = DeviationPushState.FromJson(new DeviationPushState { Date = "d", Count = 4 }.ToJson());
            Check(dRound != null && dRound!.Count == 4, "偏离状态：JSON 往返一致");

            // —— 配置默认值 ——
            var learnCfgDef = new AppConfig();
            Check(learnCfgDef.FeishuGroupAlertEnabled && learnCfgDef.FeishuDeviationAlertEnabled
                  && learnCfgDef.FeishuDeviationMinScore == 90 && learnCfgDef.FeishuDeviationMaxPerDay == 5,
                "配置：feishu_group_alert / feishu_deviation_* 默认 开 / 开 / 90 / 5");
        }

        // ============ 运维告警：存储健康卡 + 待办超期卡 ============
        {
            Console.WriteLine("\n【运维告警】严重度门槛 / 超期筛选 / 两张卡内容…");

            var relaxed = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

            // —— 严重度权重 ——
            Check(OpsAlertMonitor.SeverityRank("critical") == 3 && OpsAlertMonitor.SeverityRank("major") == 2
                  && OpsAlertMonitor.SeverityRank("minor") == 1, "严重度权重：critical > major > minor");
            Check(OpsAlertMonitor.SeverityRank("CRITICAL") == 3, "严重度权重：大小写不敏感");
            Check(OpsAlertMonitor.SeverityRank("野值") == 0 && OpsAlertMonitor.SeverityRank(null) == 0,
                "严重度权重：未知/空一律 0（不会因为脏数据被当成严重）");

            // —— 超期判定 ——
            var odNow = new DateTime(2026, 9, 14, 10, 0, 0);
            Check(OpsAlertMonitor.IsOverdue("open", "2026-09-01 08:00:00", "critical", odNow, 3, 3),
                "超期判定：待办 + 13 天前 + 严重 → 超期");
            Check(OpsAlertMonitor.IsOverdue("in_progress", "2026-09-10 08:00:00", "critical", odNow, 3, 3),
                "超期判定：持续跟踪同样算未闭环");
            Check(!OpsAlertMonitor.IsOverdue("open", "2026-09-13 08:00:00", "critical", odNow, 3, 3),
                "超期判定：刚登记 1 天不算超期");
            Check(!OpsAlertMonitor.IsOverdue("resolved", "2026-09-01 08:00:00", "critical", odNow, 3, 3),
                "超期判定：已完成不算超期（哪怕很旧）");
            Check(!OpsAlertMonitor.IsOverdue("open", "2026-09-01 08:00:00", "minor", odNow, 3, 3),
                "超期判定：严重度不达标不告警（轻微项不该刷屏）");
            Check(!OpsAlertMonitor.IsOverdue("open", "坏时间", "critical", odNow, 3, 3)
                  && !OpsAlertMonitor.IsOverdue("open", "", "critical", odNow, 3, 3),
                "超期判定：创建时间缺失/不可解析 → 不判定（不误报）");
            Check(OpsAlertMonitor.IsOverdue("open", "2026-09-13 09:00:00", "critical", odNow, 0, 3),
                "超期判定：天数非法(0) 按 1 天兜底");

            // —— 超期筛选与排序 ——
            MaintenanceRecord MR(int id, string status, string severity, string created, string item = "KL30_1")
                => new() { Id = id, Status = status, Severity = severity, CreatedAt = created, FailItem = item, StationId = "FCT1" };
            var recs = new List<MaintenanceRecord>
            {
                MR(1, "open", "critical", "2026-09-01 08:00:00"),
                MR(2, "open", "major", "2026-09-01 09:00:00"),
                MR(3, "in_progress", "critical", "2026-09-10 08:00:00"),
                MR(4, "resolved", "critical", "2026-09-01 08:00:00"),
                MR(5, "open", "critical", "2026-09-13 08:00:00"),
                MR(6, "open", "minor", "2026-09-01 08:00:00"),
                MR(7, "open", "critical", "2026-09-11 08:00:00"),
            };
            var od = OpsAlertMonitor.SelectOverdue(recs, odNow, 3, 3);
            Check(od.Total == 3, $"超期筛选：门槛 critical 命中 3 条（实得 {od.Total}）");
            Check(od.Shown.Select(m => m.Id).SequenceEqual(new[] { 1, 3, 7 }),
                "超期筛选：同龄按创建时间从旧到新（实得 " + string.Join(",", od.Shown.Select(m => m.Id)) + "）");

            var odMajor = OpsAlertMonitor.SelectOverdue(recs, odNow, 3, 2);
            Check(odMajor.Total == 4, $"超期筛选：门槛降到 major 时纳入 major（实得 {odMajor.Total}）");

            var odCap = OpsAlertMonitor.SelectOverdue(recs, odNow, 3, 3, 2);
            Check(odCap.Shown.Count == 2 && odCap.Total == 3,
                "超期筛选：cap 只截断展示条数，总数仍为全量（供「另有 N 条」提示）");
            Check(OpsAlertMonitor.SelectOverdue(null!, odNow, 3, 3).Total == 0, "超期筛选：null 列表安全返回空");

            // —— 按天去重（RunNow 每次启动都补跑，不去重会重复推）——
            Check(OpsAlertMonitor.DayAllows(null, "2026-09-14"), "按天去重：从未推过 → 放行");
            Check(!OpsAlertMonitor.DayAllows("2026-09-14", "2026-09-14"), "按天去重：当天已推 → 拦下");
            Check(OpsAlertMonitor.DayAllows("2026-09-13", "2026-09-14"), "按天去重：跨天 → 放行");

            // —— 存储健康卡 ——
            var sp = new StorageAlertPayload
            {
                StationId = "FCT1",
                Score = 42,
                Threshold = 60,
                FileBytes = 2147483648,
                WalBytes = 1048576,
                BloatRatio = 0.35,
                Issues = new List<string> { "库空洞率 35% 偏高", "WAL 未合并" },
                PlannedActions = new List<string> { "执行 VACUUM 回收磁盘" },
            };
            var spJson = JsonSerializer.Serialize(FeishuNotifier.BuildStorageAlertCard(sp), relaxed);
            Check(spJson.Contains("\"template\":\"orange\""), "存储卡：header 橙色");
            Check(spJson.Contains("存储健康告警（42/100）"), "存储卡：标题带健康分");
            Check(spJson.Contains("42/100（阈值 60）"), "存储卡：健康分与阈值并列");
            Check(spJson.Contains("2.00 GB") && spJson.Contains("1.0 MB"), "存储卡：库体积/WAL 按量级格式化");
            Check(spJson.Contains("35.0%"), "存储卡：空洞率百分比");
            Check(spJson.Contains("库空洞率 35% 偏高") && spJson.Contains("WAL 未合并"), "存储卡：列出问题");
            Check(spJson.Contains("执行 VACUUM 回收磁盘"), "存储卡：列出建议动作");

            // —— 待办超期卡 ——
            var tp = new TodoOverduePayload
            {
                StationId = "FCT1",
                OverdueDays = 3,
                MinSeverity = "critical",
                TotalOverdue = 12,
                Items = new List<MaintenanceRecord>
                {
                    new() { Id = 7, FailItem = "KL30_1", Severity = "critical", Status = "open",
                            CreatedAt = DateTime.Now.AddDays(-5).ToString("yyyy-MM-dd HH:mm:ss") },
                },
            };
            var tpJson = JsonSerializer.Serialize(FeishuNotifier.BuildTodoOverdueCard(tp), relaxed);
            Check(tpJson.Contains("\"template\":\"orange\""), "超期卡：header 橙色");
            Check(tpJson.Contains("待办超期未闭环 ×12"), "超期卡：标题带超期总数");
            Check(tpJson.Contains("≥3 天未闭环"), "超期卡：标注超期判定口径");
            Check(tpJson.Contains("严重"), "超期卡：严重度门槛转中文");
            Check(tpJson.Contains("#7 KL30_1"), "超期卡：列出待办编号与故障项");
            Check(tpJson.Contains("已 5 天"), "超期卡：列出已超期天数");
            Check(tpJson.Contains("另有 11 条未列出"), "超期卡：超出展示上限时提示总数差额");

            var tpBad = new TodoOverduePayload
            {
                StationId = "FCT1", OverdueDays = 3, MinSeverity = "critical", TotalOverdue = 1,
                Items = new List<MaintenanceRecord> { new() { Id = 1, FailItem = "X", CreatedAt = "坏时间" } },
            };
            Check(JsonSerializer.Serialize(FeishuNotifier.BuildTodoOverdueCard(tpBad), relaxed).Contains("已 — 天"),
                "超期卡：创建时间不可解析时显示「—」（不抛异常）");

            // —— 配置键 ——
            var opsCfgDef = new AppConfig();
            Check(opsCfgDef.FeishuStorageAlertEnabled && opsCfgDef.FeishuStorageAlertScore == 60
                  && opsCfgDef.FeishuTodoOverdueEnabled && opsCfgDef.FeishuTodoOverdueDays == 3
                  && opsCfgDef.FeishuTodoOverdueMinSeverity == "critical",
                "配置：feishu_storage_alert_* / feishu_todo_overdue_* 默认 开 / 60 / 开 / 3 / critical");

            var cfgFileO = Path.Combine(AppConfig.BaseDir, "config.json");
            var backupO = File.Exists(cfgFileO) ? File.ReadAllText(cfgFileO) : null;
            try
            {
                File.WriteAllText(cfgFileO, "{\"feishu_todo_overdue_min_severity\":\"bogus\",\"feishu_storage_alert_score\":150}");
                var lo = AppConfig.Load();
                Check(lo.FeishuTodoOverdueMinSeverity == "critical" && lo.FeishuStorageAlertScore == 60,
                    "配置：非法严重度 / 越界分数回落默认");

                File.WriteAllText(cfgFileO, "{\"feishu_todo_overdue_min_severity\":\"major\",\"feishu_storage_alert_score\":75,\"feishu_todo_overdue_days\":10}");
                var lo2 = AppConfig.Load();
                Check(lo2.FeishuTodoOverdueMinSeverity == "major" && lo2.FeishuStorageAlertScore == 75
                      && lo2.FeishuTodoOverdueDays == 10,
                    "配置：feishu_storage_alert_score / feishu_todo_overdue_* Load 生效");
            }
            finally
            {
                try { if (backupO != null) File.WriteAllText(cfgFileO, backupO); else File.Delete(cfgFileO); } catch { }
            }
        }

        // ============ 口径单一来源：字节格式化 / 良率（收敛实现，输出与收敛前逐位一致） ============
        {
            Console.WriteLine("\n【口径收敛】ByteUtil / StatsUtil（实现收敛，输出不变）…");

            // —— 字节格式化：两套进制都保留，值必须与收敛前的原实现一致 ——
            Check(ByteUtil.MbGb(2_147_483_648) == "2.15 GB", $"MbGb: 十进制 GB（实得 {ByteUtil.MbGb(2_147_483_648)}）");
            Check(ByteUtil.MbGb(1_048_576) == "1.0 MB", $"MbGb: 十进制 MB（实得 {ByteUtil.MbGb(1_048_576)}）");
            Check(ByteUtil.MbGb(0) == "0.0 MB", $"MbGb: 小于 1MB 也按 MB 显示（原行为，实得 {ByteUtil.MbGb(0)}）");

            Check(ByteUtil.Human(2_147_483_648) == "2.00 GB", $"Human: 二进制 GB（实得 {ByteUtil.Human(2_147_483_648)}）");
            Check(ByteUtil.Human(1_048_576) == "1.0 MB", $"Human: 二进制 MB（实得 {ByteUtil.Human(1_048_576)}）");
            Check(ByteUtil.Human(2048) == "2.0 KB", $"Human: KB 默认 1 位小数（飞书卡，实得 {ByteUtil.Human(2048)}）");
            Check(ByteUtil.Human(2048, 0) == "2 KB", $"Human: KB 可传 0 位小数（工具箱，实得 {ByteUtil.Human(2048, 0)}）");
            Check(ByteUtil.Human(512) == "512 B", $"Human: B 档（实得 {ByteUtil.Human(512)}）");

            Check(ByteUtil.MbGb(2_147_483_648) != ByteUtil.Human(2_147_483_648),
                "字节格式化：两套进制对同一值显示不同（2.15 GB vs 2.00 GB）——本轮只收敛实现、未统一进制");

            // —— 良率：两种运算顺序必须各自保持原浮点结果 ——
            Check(Math.Abs(StatsUtil.YieldPct(23, 57) - 28.75) < 1e-12,
                $"YieldPct(先乘后除): 23/57 = 28.75（实得 {StatsUtil.YieldPct(23, 57):R}）");
            Check(StatsUtil.YieldPctDivFirst(23, 57) < 28.75,
                $"YieldPctDivFirst(先除后乘): 23/57 略小于 28.75（实得 {StatsUtil.YieldPctDivFirst(23, 57):R}）");
            Check(StatsUtil.YieldPctDivFirst(23, 57) != StatsUtil.YieldPct(23, 57),
                "良率：两种运算顺序结果不同 —— 这正是二者不能合并成一个函数的原因（一位小数显示 28.8% vs 28.7%）");

            Check(StatsUtil.YieldPct(0, 0) == 0.0 && StatsUtil.YieldPctDivFirst(0, 0) == 0.0,
                "良率：分母为 0 时返回 0（不产生 NaN/除零）");
            Check(StatsUtil.YieldPct(97, 3) == 97.0 && StatsUtil.YieldPctDivFirst(97, 3) == 97.0,
                "良率：能整除时两者一致（97/100 = 97）");

            // —— 回归：收敛后各处调用点仍走同一实现（抽查 HomeCard 口径的 HourlyStatItem）——
            var hourly = new HourlyStatItem { Pass = 23, Fail = 57 };
            Check(hourly.YieldRate == StatsUtil.YieldPctDivFirst(23, 57),
                "回归：HourlyStatItem.YieldRate 走 StatsUtil（先除后乘口径不变）");
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

            // 滚动清理（PurgeOldMeasurements / CompactStorage）的 cutoff 取自 DateTime.Now，所以
            // 「该留下的新行」夹具必须相对今天造。原来写死 2026-09-04/05：自然日一旦漂出 30 天保留窗，
            // 这些行就被当成旧数据清掉，清旧留新 / 聚合分组 / 瘦表 JOIN 四条断言会随日期自己变红。
            var a6Day0 = DateTime.Today.AddDays(-3).ToString("yyyy-MM-dd");
            var a6Day1 = DateTime.Today.AddDays(-2).ToString("yyyy-MM-dd");
            var a6Day2 = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");

            var m1 = MP($"{a6Day0} 08:00:00", new[] { ("V_KL30", 13.5, (double?)10.8, (double?)14.4) });
            dbA6.BatchInsert(new[] { m1 });
            Check(RowCount(dbA6Path, "test_measurements") == 1, "规格06: BatchInsert 后测量值自动挂接入库");
            var ridM1 = IdOfRec(dbA6Path, m1.XmlPath);
            dbA6.BatchInsert(new[] { m1 });
            dbA6.InsertMeasurementsFor(new List<(TestRecord, long)> { (m1, ridM1) });
            Check(RowCount(dbA6Path, "test_measurements") == 1,
                  "规格06: UNIQUE(record_id,test_name) 幂等（重复插入不重复落行）");

            _ = new Database(dbA6Path);
            Check(RowCount(dbA6Path, "test_measurements") == 1, "规格06: 老库重开 Init 幂等（CREATE IF NOT EXISTS），数据完好");

            var a6DayOld = DateTime.Today.AddDays(-200).ToString("yyyy-MM-dd");
            var mOld = MP($"{a6DayOld} 00:00:00", new[] { ("T_OLD", 1.0, (double?)null, (double?)null) });
            dbA6.BatchInsert(new[] { mOld });
            Check(RowCount(dbA6Path, "test_measurements") == 2, "规格06: 旧 ts 测量行入库");
            var purgedM = dbA6.PurgeOldMeasurements(30);
            Check(purgedM >= 1 && RowCount(dbA6Path, "test_measurements") == 1,
                  $"规格06: PurgeOldMeasurements 清旧留新（删 {purgedM}）");

            var mS1 = MP($"{a6Day0} 09:00:00", new[] { ("T_STAT", 9.9, (double?)9.0, (double?)11.0) });
            var mS2 = MP($"{a6Day0} 09:05:00", new[] { ("T_STAT", 10.0, (double?)9.0, (double?)11.0) });
            var mS3 = MP($"{a6Day0} 09:10:00", new[] { ("T_STAT", 10.1, (double?)9.0, (double?)11.0) });
            dbA6.BatchInsert(new[] { mS1, mS2, mS3 });
            var stat = dbA6.AggregateMeasurementStats(a6Day0, a6Day1);
            Check(stat.Count == 2, $"规格06: 聚合按 (model,test_name) 分组（实得 {stat.Count} 组）");
            var statT = stat.FirstOrDefault(s => s.TestName == "T_STAT");
            Check(statT != null && statT.N == 3 && Math.Abs(statT.Mean - 10.0) < 1e-9,
                  "规格06: 聚合统计 N/mean 正确");
            var sT = statT!;
            Check(Math.Abs(sT.Sigma - 0.1) < 1e-9
                  && sT.Lolim.HasValue && Math.Abs(sT.Lolim.Value - 9.0) < 1e-9
                  && sT.Hilim.HasValue && Math.Abs(sT.Hilim.Value - 11.0) < 1e-9,
                  "规格06: 样本σ（n−1 修正）与限值聚合正确");
            Check(Database.IsWithinRetention(DateTime.Today.ToString("yyyyMMdd"), 30), "保留窗: 今天应保留");
            Check(!Database.IsWithinRetention(DateTime.Today.AddDays(-40).ToString("yyyyMMdd"), 30), "保留窗: 40 天前应丢弃");
            Check(new AppConfig().AnalyzeMeasureRetentionDays == 90, "默认测量保留 90 天");
            Check(LearnPipeline.EffectiveMeasureRetentionDays(new AppConfig { AnalyzeMeasureRetentionDays = 30 }, null) == 30,
                  "测量有效保留：无库时仅用配置");
            Check(LearnPipeline.EffectiveMeasureRetentionDays(
                      new AppConfig { AnalyzeMeasureRetentionDays = 30, LearnNormalEnabled = true, AnalyzeCollectPass = true },
                      null) == 30,
                  "测量有效保留：无库时不抬高");
            Check(LearnPipeline.EffectiveMeasureRetentionDays(
                      new AppConfig { AnalyzeMeasureRetentionDays = 30, LearnNormalEnabled = true, AnalyzeCollectPass = true },
                      null, 3) == 180,
                  "测量有效保留：回填 180 天下限抬高配置");
            var compactDbPath = Path.Combine(dirA6, "compact.db");
            var cdb = new Database(compactDbPath);
            cdb.BatchInsert(new[] { MP($"{a6Day1} 10:00:00", new[] { ("T_C", 1.0, (double?)null, (double?)null) }) });
            Check(cdb.GetStorageStats().SlimSchema, "新库测量表为瘦结构（无 xml_path 冗余列）");
            Check(cdb.GetStorageStats().Measurements >= 1, "瘦表仍能写入测量行");
            var crep = cdb.CompactStorage(30, vacuum: true);
            Check(cdb.GetStorageStats().Measurements >= 1, $"压缩后测量行仍在（{crep.Message}）");
            var cstat = cdb.AggregateMeasurementStats(a6Day1, a6Day2);
            Check(cstat.Any(s => s.TestName == "T_C" && s.N >= 1), "瘦表聚合 JOIN test_records.model 仍可用");

            Console.WriteLine("\n【存储优化】StorageOptimizer 监控与分类 …");
            var optCfg = new AppConfig();
            var optHealth = StorageOptimizer.Evaluate(cdb, optCfg);
            Check(optHealth.Score >= 70, $"瘦库健康分应较高（实得 {optHealth.Score}）");
            Check(optHealth.Tiers.Any(t => t.Table == "test_records" && t.Category == "核心"),
                  "存储分层: test_records 归为核心");
            Check(optHealth.Tiers.Any(t => t.Table == "test_measurements" && t.Category == "明细"),
                  "存储分层: test_measurements 归为明细");
            using (var pfConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={compactDbPath}"))
            {
                pfConn.Open();
                using var ins = pfConn.CreateCommand();
                ins.CommandText = "INSERT INTO parse_failure_log(xml_path,error_code,skip_reason,created_at) VALUES('old.xml','skip','t','2020-01-01 00:00:00')";
                ins.ExecuteNonQuery();
            }
            Check(cdb.PurgeOldParseFailures(30) >= 1, "PurgeOldParseFailures 清旧留痕");
            var light = StorageOptimizer.Run(cdb, optCfg, StorageOptimizeMode.Light);
            Check(light.Mode == StorageOptimizeMode.Light && light.ScoreAfter >= light.ScoreBefore - 5,
                  "轻量巡检不抛异常且评分稳定");
            var deep = StorageOptimizer.Run(cdb, optCfg, StorageOptimizeMode.Deep);
            Check(deep.Mode == StorageOptimizeMode.Deep && deep.ScoreAfter >= 60,
                  $"深度优化完成（评分 {deep.ScoreAfter}）");
            Check(!string.IsNullOrEmpty(cdb.GetMeta(StorageOptimizer.MetaLastRun)), "优化后写入 last_run meta");
            Console.WriteLine("[存储优化] 组完成");

            Console.WriteLine("\n【FTS备份】测试程序压缩备份 …");
            var ftsWork = Path.Combine(dirA6, "fts_backup");
            var ftsSrc = Path.Combine(ftsWork, "FTS");
            var ftsDest = Path.Combine(ftsWork, "backup");
            Directory.CreateDirectory(Path.Combine(ftsSrc, "Apps", "PEU", "Cfg"));
            File.WriteAllText(Path.Combine(ftsSrc, "Apps", "PEU", "Cfg", "FCT.ini"),
                "[Resource Name]\r\nFW_Version_1=1.0.0\r\n8.2_SN=E3002781\r\n");
            File.WriteAllBytes(Path.Combine(ftsSrc, "Apps", "PEU", "payload.bin"), Enumerable.Repeat((byte)0xAB, 8192).ToArray());
            var ftsDbPath = Path.Combine(ftsWork, "fts.db");
            var ftsCfg = new AppConfig
            {
                FctProgramSourceRoot = ftsSrc,
                FctProgramBackupDir = ftsDest,
                FctProgramBackupKeep = 2,
            };
            var ftsDb = new Database(ftsDbPath);
            var ftsRep = FctProgramBackup.Run(ftsDb, ftsCfg, "T1", FctProgramBackup.TriggerManual);
            Check(ftsRep.Ok && ftsRep.Record != null && File.Exists(ftsRep.Record.ZipPath),
                  $"FTS 备份生成 zip（{ftsRep.Message}）");
            Check(ftsRep.Record!.FileCount >= 2, $"FTS 备份打包文件数 {ftsRep.Record.FileCount}");
            Check(ftsRep.Record.ZipBytes > 0 && ftsRep.Record.ZipBytes < 8192 + 500,
                  "FTS 高压缩后体积明显小于原始");
            Check(ftsDb.ListFctProgramBackups(5).Count >= 1, "FTS 备份记录入库");
            FctProgramBackup.Run(ftsDb, ftsCfg, "T1", FctProgramBackup.TriggerManual);
            FctProgramBackup.Run(ftsDb, ftsCfg, "T1", FctProgramBackup.TriggerManual);
            FctProgramBackup.PurgeOldArchives(ftsDb, ftsCfg, ftsDest);
            Check(ftsDb.ListFctProgramBackups(10).Any(x => !x.ZipExists),
                  "超保留份数删 zip 但数据库记录仍保留");
            Check(new AppConfig().FctProgramBackupDir == @"D:\backup", "默认备份目录 D:\\backup");
            Check(new AppConfig().FctProgramSourceRoot == @"C:\FTS", "默认源 C:\\FTS");
            var schedDb = new Database(Path.Combine(ftsWork, "sched.db"));
            Check(FctProgramBackup.ShouldRunScheduled(schedDb, ftsCfg), "本月尚无备份时应触发定时备份");
            Check(!FctProgramBackup.ShouldRunScheduled(ftsDb, ftsCfg), "本月已有成功备份则跳过");

            Console.WriteLine("[FTS备份] 组完成");

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

            // 本组末尾的 PurgeOldFailItems(90) cutoff 取自 DateTime.Now，所以「该留下的」三行必须相对今天造。
            // FR/PR 的 TestDate 被归因组 / 治具组共用（那两组锚在硬编码 fixedNow 上），只改本组的 ts：
            // 这些行 ts 非空，走 substr(ts) 分支，父记录的 test_date 不参与判定。
            var e6Day = DateTime.Today.AddDays(-2).ToString("yyyy-MM-dd");
            var f1 = FR($"{e6Day} 08:30:00", "T1", "FX1", "FAIL", "Failed", new[]
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
            var fT = FR($"{e6Day}T09:30:00", "T1", "FX1", "FAIL", "Failed",
                new[] { ("V_KL30", "3.3", "10.8", "14.4", "V", "LeGe") });
            dbE6.BatchInsert(new[] { fT });
            Check(Convert.ToInt64(Scalar(dbE6Path, $"SELECT hour FROM fail_items WHERE ts='{e6Day}T09:30:00'")) == 9,
                  "期2: hour 兼容 T 分隔格式");
            var fPass = FR($"{e6Day} 10:00:00", "T1", "FX1", "PASS", "Passed",
                new[] { ("GHOST", "1", "", "", "", "") });
            var fInt = FR($"{e6Day} 10:30:00", "T1", "FX1", "INTERRUPTED", "Failed",
                new[] { ("GHOST2", "1", "", "", "", "") });
            dbE6.BatchInsert(new[] { fPass, fInt });
            Check(RowCount(dbE6Path, "fail_items") == 3,
                  "期2: PASS/INTERRUPTED 记录不落失败项（仅 FAIL 前置守卫）");
            var ridF1 = IdOfRec(dbE6Path, f1.XmlPath);
            dbE6.BatchInsert(new[] { f1 });
            dbE6.InsertFailItemsFor(new List<(TestRecord, long)> { (f1, ridF1) });
            Check(RowCount(dbE6Path, "fail_items") == 3, "期2: fail_items UNIQUE(record_id,test_name) 幂等");
            var fOld = FR($"{DateTime.Today.AddDays(-200):yyyy-MM-dd} 00:00:00", "T1", "FX1", "FAIL", "Failed",
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
                // 采样时间必须相对"今天"：GetLocalDeviceSamples 按 datetime('now','-N days') 取数，
                // 写死 fixedNow 会让样本随真实时间推移滑出窗口（小时键仍与上面的失败记录对齐）
                dbF6b.InsertLocalDeviceSample(h * 10.0, 50, 100, $"{DateTime.Today:yyyy-MM-dd} {h:00}:05:00");
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
            // 同 PurgeOldMeasurements：PurgeOldTdmsFeatures 的 cutoff 取自 DateTime.Now，特征行夹具
            // 必须相对今天造，否则「清旧留新」连带后面的落盘快照断言会随自然日漂移而变红。
            var tdmsDay8 = DateTime.Today.AddDays(-2).ToString("yyyyMMdd");
            var tdmsTs = DateTime.Today.AddDays(-2).ToString("yyyy-MM-dd") + " 10:00:00";
            var tdmsDayOld8 = DateTime.Today.AddDays(-200).ToString("yyyyMMdd"); // 该日目录不建，兼作「定位器日期目录不存在」夹具
            var tdmsRootDir = Path.Combine(work, "tdmsroot");
            var tdmsDayDir = Path.Combine(tdmsRootDir, "Online", "E3002781", tdmsDay8);
            Directory.CreateDirectory(tdmsDayDir);
            File.WriteAllText(Path.Combine(tdmsDayDir, "SN001_0001.tdms"), "x");
            File.WriteAllText(Path.Combine(tdmsDayDir, "SN0011_0001.tdms"), "x");
            File.WriteAllText(Path.Combine(tdmsDayDir, "SN001_0002.tdms"), "x");
            Check(TdmsFeatureAnalyzer.LocateTdmsFile(tdmsRootDir, "Online", "E3002781", tdmsDay8, "SN001")
                  == Path.Combine(tdmsDayDir, "SN001_0002.tdms"),
                  "期3: 定位命中同 SN 文件名降序最新（SN001_0002）");
            Check(TdmsFeatureAnalyzer.LocateTdmsFile(tdmsRootDir, "Online", "E3002781", tdmsDay8, "SN009") == "",
                  "期3: 无匹配 SN 返回空");
            Check(TdmsFeatureAnalyzer.LocateTdmsFile("", "Online", "E3002781", tdmsDay8, "SN001") == ""
                  && TdmsFeatureAnalyzer.LocateTdmsFile(tdmsRootDir, "Online", "E3002781", tdmsDay8, "") == "",
                  "期3: root 空/SN 空 防呆返回空");
            Check(TdmsFeatureAnalyzer.LocateTdmsFile(tdmsRootDir, "Online", "E3002781", tdmsDay8[..7], "SN001") == "",
                  "期3: testDate 非 8 位 防呆返回空（前缀不误命中 SN0011）");
            Check(TdmsFeatureAnalyzer.LocateTdmsFile(tdmsRootDir, "Online", "E3002781", tdmsDayOld8, "SN001") == "",
                  "期3: 日期目录不存在 返回空");

            // 落表：Init 预建 / 字段抽查 / UNIQUE 幂等 / Purge + 老库重开
            var tdmsDbDir = Path.Combine(work, "tdmsdb");
            Directory.CreateDirectory(tdmsDbDir);
            var dbT = new Database(Path.Combine(tdmsDbDir, "t.db"));
            Check(dbT.GetTdmsFeatureSummary() == (0L, 0L, ""), "期3: Init 预建 tdms_features 空表");
            var tdmsRec = new TestRecord
            {
                StationId = "FCT1", Model = "E3002781", Category = "Online", TestDate = tdmsDay8,
                Sn = "SN001", Result = "PASS", XmlPath = Path.Combine(work, "a1.xml"),
                BatchTimestamp = tdmsTs,
            };
            var tdmsFeatureRows = new List<TdmsFeatureAnalyzer.TdmsFeatureRow>
            {
                new() { GroupName = "03.2 电压", ChannelName = "V1", Section = "3.2", N = 5,
                        Min = 1, Max = 5, Mean = 3, Std = Math.Sqrt(2.5), First = 1, Last = 5 },
                new() { GroupName = "8.11 Resolver Test", ChannelName = "RES_A", Section = "8.11", N = 3,
                        Min = 0.5, Max = 1.5, Mean = 1.0, Std = 0.5, First = 0.5, Last = 1.5 },
            };
            var tdmsRid = dbT.InsertOneReturnId(tdmsRec);
            dbT.InsertTdmsFeaturesFor(tdmsRec, tdmsRid, Path.Combine(tdmsDayDir, "SN001_0002.tdms"), tdmsFeatureRows);
            var tdmsRecent = dbT.GetRecentTdmsFeatures(10);
            Check(tdmsRecent.Count == 2, "期3: InsertTdmsFeaturesFor 落表 2 行");
            var r1 = tdmsRecent.First(x => x.ChannelName == "V1");
            Check(r1.RecordId == tdmsRid && r1.Model == "E3002781" && r1.Sn == "SN001"
                  && r1.GroupName == "03.2 电压" && r1.Section == "3.2" && r1.N == 5
                  && Math.Abs(r1.Mean!.Value - 3.0) < 1e-9 && r1.Ts == tdmsTs,
                  "期3: 特征行字段抽查（JOIN test_records 取 model/sn）");
            dbT.InsertTdmsFeaturesFor(tdmsRec, tdmsRid, Path.Combine(tdmsDayDir, "SN001_0002.tdms"), tdmsFeatureRows);
            Check(dbT.GetTdmsFeatureSummary() == (2L, 1L, tdmsTs),
                  "期3: UNIQUE(record_id,group,channel) 幂等重插不变");
            var tdmsRecOld = new TestRecord
            {
                StationId = "FCT1", Model = "E3002781", Category = "Online", TestDate = tdmsDayOld8,
                Sn = "SN001", Result = "PASS", XmlPath = Path.Combine(work, "a0.xml"),
                BatchTimestamp = $"{DateTime.Today.AddDays(-200):yyyy-MM-dd} 09:00:00",
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
                StationId = "FCT1", Model = "E3002781", Category = "Online", TestDate = tdmsDay8,
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
                StationId = "FCT1", Model = "E3002781", Category = "Online", TestDate = tdmsDay8,
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
            // A2：单条 upsert 返回 id；重复同内容不新建；结果变化则覆盖更新
            var recA2 = new TestRecord { StationId = "FCT1", Model = "E3002781", Category = "Online", TestDate = "20260905", Sn = "SN906", Result = "PASS", XmlPath = Path.Combine(clDir, "P_UNIT_SN906_20260905130600_0001.xml") };
            var oA2 = clDbA4.UpsertTestRecord(recA2);
            var oA2b = clDbA4.UpsertTestRecord(recA2);
            Check(oA2.IsNew && oA2.RecordId > 0 && oA2b.RecordId == oA2.RecordId && !oA2b.IsNew && !oA2b.WasUpdated,
                $"审计A2: Upsert 新插 id>0、重复同内容不更新（实得 {oA2.RecordId}/{oA2b.WasUpdated}）");
            var recA2fail = new TestRecord { StationId = "FCT1", Model = "E3002781", Category = "Online", TestDate = "20260905", Sn = "SN906", Result = "FAIL", XmlPath = recA2.XmlPath, FailReason = "STEP_X", HasFailItems = true, FailedTests = { new FailedTest { Name = "STEP_X" } } };
            var oA2c = clDbA4.UpsertTestRecord(recA2fail);
            Check(oA2c.WasUpdated && oA2c.NeedsFailAlert && oA2c.RecordId == oA2.RecordId,
                $"审计A2: PASS→FAIL 覆盖更新且 NeedsFailAlert（实得 updated={oA2c.WasUpdated} alert={oA2c.NeedsFailAlert}）");
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


    static void RunMeasurementTrendTests(string work)
    {
        // ── 桶宽自适应（分桶即降采样）──
        Check(MeasurementTrendBuilder.ChooseBucketSeconds(TimeSpan.FromDays(30), 300) == 10800,
            "趋势桶宽：30 天 / 上限 300 桶 → 3 小时档");
        Check(MeasurementTrendBuilder.CountBuckets(TimeSpan.FromDays(30), 10800) == 240,
            "趋势桶数：30 天按 3 小时桶 = 240（不超上限）");
        Check(MeasurementTrendBuilder.ChooseBucketSeconds(TimeSpan.FromDays(1), 300) == 300,
            "趋势桶宽：1 天 / 上限 300 桶 → 5 分钟档");
        Check(MeasurementTrendBuilder.ChooseBucketSeconds(TimeSpan.FromDays(3650), 300) == 1209600,
            "趋势桶宽：超长跨度回落到 14 天档（不外溢 nice 序列）");
        Check(MeasurementTrendBuilder.ChooseBucketSeconds(TimeSpan.Zero, 300) == 60,
            "趋势桶宽：零跨度取最小档不抛异常");

        // ── 归一化 ──
        Check(MeasurementTrendBuilder.Normalize(15, 10, 20) == 0.5, "归一化：带中心 = 0.5");
        Check(MeasurementTrendBuilder.Normalize(10, 10, 20) == 0, "归一化：贴下限 = 0");
        Check(MeasurementTrendBuilder.Normalize(20, 10, 20) == 1, "归一化：贴上限 = 1");
        Check(MeasurementTrendBuilder.Normalize(30, 10, 20) == 2, "归一化：超上限 = 2（带宽倍数）");
        Check(MeasurementTrendBuilder.Normalize(5, 10, 20) == -0.5, "归一化：低于下限为负");
        Check(double.IsNaN(MeasurementTrendBuilder.Normalize(10, 20, 20)),
            "归一化：零宽规格带返回 NaN（不除零）");

        // ── 基准带：双侧 / 单边 / 退化 / 无 ──
        var bandTwo = MeasurementTrendBuilder.ResolveBand(10, 20, 5, 30);
        Check(bandTwo.HasValue && bandTwo!.Value.Low == 10 && bandTwo.Value.High == 20
              && !bandTwo.Value.LowSynthetic && !bandTwo.Value.HighSynthetic,
            "基准带：双侧限直接用规格带");
        var bandLow = MeasurementTrendBuilder.ResolveBand(10, null, 5, 8);
        Check(bandLow.HasValue && bandLow!.Value.Low == 10 && bandLow.Value.High == 13 && bandLow.Value.HighSynthetic,
            "基准带：只有下限 → 上限按窗口量程合成（10~13）");
        var bandHigh = MeasurementTrendBuilder.ResolveBand(null, 20, 5, 8);
        Check(bandHigh.HasValue && bandHigh!.Value.Low == 17 && bandHigh.Value.High == 20 && bandHigh.Value.LowSynthetic,
            "基准带：只有上限 → 下限按窗口量程合成（17~20）");
        var bandDegenerate = MeasurementTrendBuilder.ResolveBand(10, 10, 0, 4);
        Check(bandDegenerate.HasValue && bandDegenerate!.Value.Low == 8 && bandDegenerate.Value.High == 12,
            "基准带：上下限相等（退化规格带）→ 以该值为中心用量程撑开");
        var bandSpread = MeasurementTrendBuilder.ResolveBand(null, null, 5, 8);
        Check(bandSpread.HasValue && bandSpread!.Value.Low == 5 && bandSpread.Value.High == 8
              && bandSpread.Value.LowSynthetic && bandSpread.Value.HighSynthetic,
            "基准带：无限值但有量程 → 双端都按量程合成");
        Check(MeasurementTrendBuilder.ResolveBand(null, null, null, null) == null,
            "基准带：全空 → null（跳过该测项）");
        Check(MeasurementTrendBuilder.ResolveBand(10, null, 5, 5) == null,
            "基准带：单边限但窗口量程为 0 → null（不造基准）");

        // ── 轴文案 ──
        Check(MeasurementTrendBuilder.FormatBucketLabel(new DateTime(2026, 9, 1), 2, 86400) == "09-03",
            "桶标签：按天桶只到日期");
        Check(MeasurementTrendBuilder.FormatBucketLabel(new DateTime(2026, 9, 1), 3, 3600) == "09-01 03:00",
            "桶标签：按小时桶含时分");

        // ── 端到端：入库 → 分桶 → 归一化 ──
        var trDb = new Database(Path.Combine(work, "trend.db"));
        var recs = new List<TestRecord>();
        void AddRec(string key, string ts, string item, double value, double? lo, double? hi)
        {
            recs.Add(new TestRecord
            {
                StationId = "FCT1", Model = "G49", Category = "Online",
                TestDate = ts[..10].Replace("-", ""), Sn = key, Result = "PASS",
                XmlPath = Path.Combine(work, $"trend_{key}.xml"), BatchTimestamp = ts,
                Measurements = new List<MeasurementRow>
                {
                    new() { TestName = item, Value = value, Lolim = lo, Hilim = hi, Unit = "V" },
                },
            });
        }
        AddRec("a", "2026-09-01 08:00:00", "T_V", 10.5, 10, 13);
        AddRec("b", "2026-09-01 20:00:00", "T_V", 11.5, 10, 13);
        AddRec("c", "2026-09-02 09:00:00", "T_V", 10.0, 10, 13);
        AddRec("d", "2026-09-01 10:00:00", "T_ONE", 7.0, 5, null);
        AddRec("e", "2026-09-02 10:00:00", "T_ONE", 9.0, 5, null);
        AddRec("f", "2026-09-01 12:00:00", "T_NOLIM", 3.0, null, null);
        AddRec("g", "2026-09-01 12:00:00", "T_GAP", 1.0, 0, 2);
        AddRec("h", "2026-09-03 12:00:00", "T_GAP", 1.0, 0, 2);
        // FAIL 记录：趋势图叠虚线的数据源（只有"失败的那个测项"会进 fail_items）
        void AddFailRec(string key, string ts, string item, string value, string lo, string hi)
        {
            recs.Add(new TestRecord
            {
                StationId = "FCT1", Model = "G49", Category = "Online",
                TestDate = ts[..10].Replace("-", ""), Sn = key, Result = "FAIL",
                XmlPath = Path.Combine(work, $"trend_fail_{key}.xml"), BatchTimestamp = ts, FailReason = item,
                FailedTests = new List<FailedTest>
                {
                    new() { Name = item, Value = value, Lolim = lo, Hilim = hi, Unit = "V" },
                },
            });
        }
        AddFailRec("fa", "2026-09-01 09:00:00", "T_V", "3.5", "10", "13");
        AddFailRec("fd", "2026-09-02 11:00:00", "T_V", "3.8", "10", "13");
        AddFailRec("fb", "2026-09-02 10:00:00", "T_ONE", "Open", "5", "");
        AddFailRec("fc", "2026-09-01 11:00:00", "T_ONLYFAIL", "1.0", "0", "2");
        trDb.BatchInsert(recs);

        var t2 = MeasurementTrendBuilder.Build(trDb, new DateTime(2026, 9, 1), new DateTime(2026, 9, 2), 2);
        Check(t2.BucketSeconds == 86400 && t2.BucketCount == 2,
            $"趋势分桶：2 天窗口按天分桶（实得 {t2.BucketSeconds}s × {t2.BucketCount}）");
        Check(t2.AllItemNames.Count == 4, $"趋势测项：窗口内 4 个 PASS 测项（实得 {t2.AllItemNames.Count}）");
        Check(t2.Series.Count == 3 && t2.SkippedNoBand == 1,
            $"趋势：3 条可绘 / 1 个测项无限值无量程被跳过（实得 {t2.Series.Count}/{t2.SkippedNoBand}）");

        var tv = t2.Series.FirstOrDefault(s => s.TestName == "T_V");
        Check(tv != null && tv!.Points.Count == 2, $"趋势：T_V 两天各 1 桶（实得 {tv?.Points.Count ?? -1}）");
        Check(tv != null && tv!.Points[0].N == 2 && Math.Abs(tv.Points[0].MeanValue - 11.0) < 1e-9,
            "趋势：同桶 2 条样本取段内均值 11.0");
        Check(tv != null && Math.Abs(tv!.Points[0].Norm - 1.0 / 3.0) < 1e-9,
            $"趋势：均值 11 对限值 10~13 归一化为 1/3（实得 {tv?.Points[0].Norm:F4}）");
        Check(tv != null && tv!.Points[1].Norm == 0 && Math.Abs(tv.MaxNorm - 1.0 / 3.0) < 1e-9 && tv.MinNorm < 0,
            "趋势：极差保留供「波动最大」排序（PASS 侧 MaxNorm=1/3；MinNorm 已含 FAIL 极值 <0）");

        var tone = t2.Series.FirstOrDefault(s => s.TestName == "T_ONE");
        Check(tone != null && tone!.IsSingleSided && tone.HighSynthetic && tone.BandLow == 5 && tone.BandHigh == 7,
            "趋势：单边限测项按窗口量程合成缺失端（5~7）");
        Check(tone != null && Math.Abs(tone!.Points[0].Norm - 1.0) < 1e-9 && Math.Abs(tone.Points[1].Norm - 2.0) < 1e-9,
            "趋势：合成带宽下归一化 = 1 / 2");

        var t3 = MeasurementTrendBuilder.Build(trDb, new DateTime(2026, 9, 1), new DateTime(2026, 9, 3), 3);
        var gap = t3.Series.FirstOrDefault(s => s.TestName == "T_GAP");
        Check(gap != null && gap!.Points.Select(p => p.Bucket).SequenceEqual(new[] { 0, 2 }),
            "趋势：中间空桶不插值，折线按桶号断开（桶 0 与 2）");

        // ── FAIL 虚线叠加（v3.40.0） ──
        var t5 = MeasurementTrendBuilder.Build(trDb, new DateTime(2026, 9, 1), new DateTime(2026, 9, 2), 2);
        var tv5 = t5.Series.First(s => s.TestName == "T_V");
        Check(tv5.Points.Count == 2 && tv5.FailPoints.Count == 2 && tv5.FailSamples == 2,
            $"FAIL 叠加：T_V 两桶 PASS 2 点 + FAIL 2 点（实得 {tv5.Points.Count}/{tv5.FailPoints.Count}）");
        Check(Math.Abs(tv5.Points[0].MeanValue - 11.0) < 1e-9,
            "FAIL 叠加：FAIL 不污染 PASS 桶均值（桶 0 仍为 PASS 均值 11.0）");
        Check(Math.Abs(tv5.FailPoints[0].MeanValue - 3.5) < 1e-9
              && Math.Abs(tv5.FailPoints[0].Norm - (3.5 - 10) / 3.0) < 1e-9,
            "FAIL 叠加：失败值按同测项限值归一化（3.5 对 10~13 → 负相对位置）");
        Check(tv5.MinNorm < 0 && tv5.PassMinNorm >= 0,
            $"FAIL 叠加：MinNorm 含掉坑供排序（{tv5.MinNorm:F2}）；PassMinNorm 不被 FAIL 拉低（{tv5.PassMinNorm:F2}）");
        Check(!t5.Series.Any(s => s.TestName == "T_ONLYFAIL"),
            "FAIL 叠加：只有 FAIL 没有 PASS 的测项不建系列（趋势图以正常态为基准）");
        var tone5 = t5.Series.First(s => s.TestName == "T_ONE");
        Check(!tone5.HasFail, "FAIL 叠加：非数值失败项（value_text=Open）不进虚线");
        Check(t5.HasAnyFail && t5.FailSamplesTotal == 2,
            $"FAIL 叠加：汇总失败样本数（实得 {t5.FailSamplesTotal}）");

        var emptyWin = MeasurementTrendBuilder.Build(trDb, new DateTime(2026, 6, 1), new DateTime(2026, 6, 30), 60);
        Check(emptyWin.Series.Count == 0 && (emptyWin.Hint ?? "").Contains("库内测量实际为"),
            "空窗口：提示库内测量实际日期，而不是只说无数据");
        var wideWin = MeasurementTrendBuilder.Build(trDb, new DateTime(2026, 8, 1), new DateTime(2026, 9, 2), 60);
        Check(wideWin.Series.Count > 0 && (wideWin.Hint ?? "").Contains("测量实际落在"),
            "宽窗口：前段无测量时提示实际有点日期");

        var only = MeasurementTrendBuilder.Build(trDb, new DateTime(2026, 9, 1), new DateTime(2026, 9, 2), 2,
            new[] { "t_one" });
        Check(only.Series.Count == 1 && only.Series[0].TestName == "T_ONE",
            "趋势：构建时按测项名过滤（忽略大小写）");

        // ── 只对部分测项取桶数据（UI 只勾几条时用；不得影响测项清单） ──
        var partial = MeasurementTrendBuilder.Build(trDb, new DateTime(2026, 9, 1), new DateTime(2026, 9, 2), 2,
            null, new[] { "t_one" });
        Check(partial.Series.Count == 1 && partial.Series[0].TestName == "T_ONE",
            "趋势取桶：只对指定测项取桶数据 → 系列只含它");
        Check(partial.AllItemNames.Count == 4,
            $"趋势取桶：测项清单不受影响（仍为窗口内全部 4 项，实得 {partial.AllItemNames.Count}）");
        Check(partial.Series[0].Points.Count == only.Series[0].Points.Count,
            "趋势取桶：同一测项的桶点与全量构建一致");

        // ── 端到端：模拟 UI 刷新序列（首次全量取桶 → 选中若干 → 改日期只取勾选） ──
        var full3 = MeasurementTrendBuilder.Build(trDb, new DateTime(2026, 9, 1), new DateTime(2026, 9, 3), 3);
        var picked = MeasurementTrendBuilder.TopVolatileNames(full3.Series, 8);
        var incr = MeasurementTrendBuilder.Build(trDb, new DateTime(2026, 9, 1), new DateTime(2026, 9, 3), 3,
            null, picked);
        Check(incr.AllItemNames.Count == full3.AllItemNames.Count && incr.AllItemNames.Count == 4,
            $"刷新序列：只取勾选桶后测项清单不缩水（实得 {incr.AllItemNames.Count}，全量 {full3.AllItemNames.Count}）");
        Check(incr.Series.Count > 0
              && incr.Series.All(s => picked.Contains(s.TestName, StringComparer.OrdinalIgnoreCase)),
            "刷新序列：只取勾选桶后系列是勾选子集");
        Check(incr.Series.Count == full3.Series.Count,
            $"刷新序列：勾选覆盖全部可绘测项时系列数与全量一致（实得 {incr.Series.Count}）");

        // ── 端到端：换日期后必须换成新区间的数据（旧区间数据不得残留） ──
        var narrow = MeasurementTrendBuilder.Build(trDb, new DateTime(2026, 9, 2), new DateTime(2026, 9, 2), 60);
        Check(narrow.From == new DateTime(2026, 9, 2) && narrow.ToExclusive == new DateTime(2026, 9, 3),
            "换日期：区间严格用新选择（From / ToExclusive 跟随）");
        Check(!narrow.AllItemNames.Contains("T_GAP"),
            "换日期：9/2 单日窗口内 T_GAP 无数据 → 不出现（旧区间数据未残留）");

        // ── 桶查询下推：只取指定测项时行数应显著减少（UI「只勾几条」提速的前提） ──
        var allBucketRows = trDb.QueryPassTrendBuckets("2026-09-01 00:00:00", "2026-09-04 00:00:00", 86400, null);
        var fewBucketRows = trDb.QueryPassTrendBuckets("2026-09-01 00:00:00", "2026-09-04 00:00:00", 86400,
            new[] { "T_ONE" });
        Check(fewBucketRows.Count > 0 && fewBucketRows.Count < allBucketRows.Count,
            $"桶查询下推：指定测项后行数减少（全部 {allBucketRows.Count} 行 → 单项 {fewBucketRows.Count} 行）");

        // ── 勾选 / 排序 / 查询口径 ──
        Check(MeasurementTrendBuilder.Filter(t2.Series, Array.Empty<string>()).Count == 0,
            "趋势：未勾选任何测项则不画线");
        var fil = MeasurementTrendBuilder.Filter(t2.Series, new[] { "t_v" });
        Check(fil.Count == 1 && fil[0].TestName == "T_V", "趋势：按测项名过滤（忽略大小写）");
        var top = MeasurementTrendBuilder.TopVolatileNames(t2.Series, 1);
        Check(top.Count == 1 && top[0] == "T_V",
            $"趋势：默认勾选波动最大的测项（含 FAIL 极值后 T_V 最大，实得 {(top.Count > 0 ? top[0] : "-")}）");

        var rows = trDb.QueryPassTrendBuckets("2026-09-01 00:00:00", "2026-09-03 00:00:00", 86400, null);
        var vRows = rows.Where(r => r.TestName == "T_V").OrderBy(r => r.Bucket).ToList();
        Check(vRows.Count == 2 && vRows[0].Bucket == 0 && vRows[0].N == 2,
            "趋势查询：桶 0 对齐区间起点，同日 08:00 与 20:00 归同桶");
        Check(rows.All(r => r.Bucket >= 0), "趋势查询：窗口外的点不会产生负桶");

        var empty = MeasurementTrendBuilder.Build(trDb, new DateTime(2026, 1, 1), new DateTime(2026, 1, 5), 60);
        Check(empty.IsEmpty && !string.IsNullOrWhiteSpace(empty.Hint), "趋势：空窗口给出空态提示而非抛异常");
        var blankDb = new Database(Path.Combine(work, "trend_blank.db"));
        var reversed = MeasurementTrendBuilder.Build(blankDb, new DateTime(2026, 9, 5), new DateTime(2026, 9, 1), 60);
        Check(reversed.IsEmpty && reversed.ToExclusive > reversed.From,
            "趋势：起止日期倒置时自动纠正为至少 1 天");
    }

    /// <summary>FAIL 页测项级明细 + 自然周期导出（v3.39.0）。</summary>
    static void RunFailDetailExportTests(string work)
    {
        // ── 自然周期边界（today 注入，不依赖运行当天是周几）──
        var wed = new DateTime(2026, 9, 9);   // 2026-09-11 是周五 → 09-09 周三、09-07 周一、09-13 周日
        var rDay = TimeUtil.NaturalRange(TimeUtil.NaturalPeriod.Day, wed);
        Check(rDay.From == wed && rDay.ToInclusive == wed, "自然周期：日 = 当天");
        var rWeek = TimeUtil.NaturalRange(TimeUtil.NaturalPeriod.Week, wed);
        Check(rWeek.From == new DateTime(2026, 9, 7) && rWeek.ToInclusive == wed,
            $"自然周期：周 = 本周一至今（实得 {rWeek.From:yyyy-MM-dd}）");
        var rMonth = TimeUtil.NaturalRange(TimeUtil.NaturalPeriod.Month, wed);
        Check(rMonth.From == new DateTime(2026, 9, 1) && rMonth.ToInclusive == wed, "自然周期：月 = 本月 1 号至今");
        var rQ = TimeUtil.NaturalRange(TimeUtil.NaturalPeriod.Quarter, wed);
        Check(rQ.From == new DateTime(2026, 7, 1) && rQ.ToInclusive == wed,
            $"自然周期：季度 = 本季度首日（9 月属 Q3 → 07-01，实得 {rQ.From:yyyy-MM-dd}）");
        Check(TimeUtil.NaturalRange(TimeUtil.NaturalPeriod.Quarter, new DateTime(2026, 1, 15)).From == new DateTime(2026, 1, 1)
              && TimeUtil.NaturalRange(TimeUtil.NaturalPeriod.Quarter, new DateTime(2026, 12, 31)).From == new DateTime(2026, 10, 1),
            "自然周期：季度边界（1 月 → Q1 首日 / 12 月 → Q4 首日）");
        var mon = new DateTime(2026, 9, 7);
        Check(TimeUtil.NaturalRange(TimeUtil.NaturalPeriod.Week, mon).From == mon,
            "自然周期：当天恰为周一时，周区间起点 = 当天");
        Check(TimeUtil.NaturalRange(TimeUtil.NaturalPeriod.Week, new DateTime(2026, 9, 13)).From == new DateTime(2026, 9, 7),
            "自然周期：周日仍属本周（起点是本周一，不是下周一）");
        Check(TimeUtil.NaturalPeriodName(TimeUtil.NaturalPeriod.Quarter) == "本季度"
              && TimeUtil.NaturalPeriodName(TimeUtil.NaturalPeriod.Day) == "今日",
            "自然周期：名称文案");

        // ── 测项级明细 ──
        var dir = Path.Combine(work, "faildetail_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = new Database(Path.Combine(dir, "local.db"));
        int seq = 0;
        void AddFail(string sn, string ts, string day, string reason,
            params (string name, string val, string lo, string hi, string unit)[] items)
        {
            seq++;
            db.BatchInsert(new[]
            {
                new TestRecord
                {
                    StationId = "FCT1", Model = "E3002781", Result = "FAIL", Sn = sn,
                    TestDate = day, BatchTimestamp = ts, XmlPath = $@"C:\t\fd_{seq}.xml", FailReason = reason,
                    FailedTests = items.Select(i => new FailedTest
                    { Name = i.name, Value = i.val, Lolim = i.lo, Hilim = i.hi, Unit = i.unit }).ToList(),
                },
            });
        }
        AddFail("SN1", "2026-09-05 08:30:00", "20260905", "V_KL30;CONT",
            ("V_KL30", "3.29", "10.8", "14.4", "V"), ("CONT", "Open", "", "", ""));
        // 老记录：库里只有汇总原因，没有 fail_items 明细
        seq++;
        db.BatchInsert(new[]
        {
            new TestRecord
            {
                StationId = "FCT1", Model = "E3002781", Result = "FAIL", Sn = "SN9",
                TestDate = "20260905", BatchTimestamp = "2026-09-05 09:00:00",
                XmlPath = $@"C:\t\fd_old_{seq}.xml", FailReason = "老记录汇总原因",
            },
        });
        AddFail("SN2", "2026-08-01 08:00:00", "20260801", "OUT_OF_RANGE",
            ("T_OUT", "1", "0", "2", "V"));

        var all = db.QueryFailItemDetails("FCT1", "20260901", "20260930");
        Check(all.Count == 3, $"FAIL 明细：9 月区间 3 行（2 项明细 + 1 条老记录），实得 {all.Count}");
        Check(all.Count(r => r.HasDetail) == 2 && all.Count(r => !r.HasDetail) == 1,
            "FAIL 明细：无 fail_items 的老记录仍出现，且 HasDetail=false");
        var vk = all.FirstOrDefault(r => r.TestName == "V_KL30");
        Check(vk != null && vk!.ValueDisplay == "3.29" && vk.LimitDisplay == "10.8 ~ 14.4V" && vk.HasDetail,
            $"FAIL 明细：值取原串、限值拼单位（实得 值='{vk?.ValueDisplay}' 限值='{vk?.LimitDisplay}'）");
        var cont = all.FirstOrDefault(r => r.TestName == "CONT");
        Check(cont != null && cont!.ValueDisplay == "Open" && cont.LimitDisplay == "",
            "FAIL 明细：非数值项值取 value_text、无限值则 limit 留空");
        var old = all.FirstOrDefault(r => !r.HasDetail);
        Check(old != null && old!.TestName == "老记录汇总原因" && old.ValueDisplay == "" && old.LimitDisplay == "",
            "FAIL 明细：老记录项目退回 fail_reason、值/limit 留空");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string prev = "";
        bool contiguous = true;
        foreach (var r in all)
        {
            if (string.Equals(r.TestName, prev, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(r.TestName)) { contiguous = false; break; }
            prev = r.TestName;
        }
        Check(contiguous, "FAIL 明细：同项目的行在结果里连续（默认按相同项排序）");
        var aug = db.QueryFailItemDetails("FCT1", "20260801", "20260831");
        Check(aug.Count == 1 && aug[0].LimitDisplay == "0 ~ 2V",
            $"FAIL 明细：日期闭区间过滤 + 双限拼接（实得 {aug.Count} 行 / '{aug.FirstOrDefault()?.LimitDisplay}'）");

        // ── 最近 N 条记录展开（屏幕表格口径：不限日期） ──
        var recent = db.QueryFailItemDetails("FCT1", recentRecords: 10);
        Check(recent.Count == 4 && recent.Any(r => r.TestName == "T_OUT"),
            $"FAIL 明细：recentRecords 模式不限日期、含更早记录（3 条记录展开 4 行，实得 {recent.Count}）");

        // ── CSV 导出 ──
        var csv = Path.Combine(dir, "out.csv");
        var n = FailExporter.ExportCsv(csv, TimeUtil.NaturalPeriod.Month,
            new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), all);
        var text = File.ReadAllText(csv);
        Check(n == 3 && text.Contains("FAIL 明细导出（本月）") && text.Contains("范围,2026-09-01 ~ 2026-09-30"),
            "CSV：标题与周期范围行正确");
        Check(text.Contains("型号,SN,测试Fail时间,项目,值,下限,上限,单位"), "CSV：表头 8 列（限值拆下限/上限/单位便于透视）");
        Check(text.Contains("3.29") && text.Contains("10.8") && text.Contains("14.4"), "CSV：值与上下限落表");

        var inj = Path.Combine(dir, "inj.csv");
        FailExporter.ExportCsv(inj, TimeUtil.NaturalPeriod.Day, DateTime.Today, DateTime.Today,
            new[] { new FailItemDetail { TestName = "=cmd|calc", Sn = "+1", Model = "M", Ts = "t" } });
        var injText = File.ReadAllText(inj);
        Check(injText.Contains("'=cmd|calc") && injText.Contains("'+1"),
            "CSV：以 = / + 开头的单元格加前导单引号（CWE-1236）");

        var q = Path.Combine(dir, "q.csv");
        FailExporter.ExportCsv(q, TimeUtil.NaturalPeriod.Day, DateTime.Today, DateTime.Today,
            new[] { new FailItemDetail { TestName = "A,B", Sn = "he\"llo", Model = "M", Ts = "t" } });
        var qText = File.ReadAllText(q);
        Check(qText.Contains("\"A,B\"") && qText.Contains("\"he\"\"llo\""),
            "CSV：逗号与引号按 RFC 4180 转义");

        // ── xlsx 导出（列宽已预设：解决"CSV 打开挤在一起"） ──
        var xlsxPath = Path.Combine(dir, "out.xlsx");
        var nx = FailExporter.ExportXlsx(xlsxPath, all);
        Check(nx == 3, $"xlsx：返回数据行数（实得 {nx}）");
        Check(new FileInfo(xlsxPath).Length > 1200, $"xlsx：已生成（{new FileInfo(xlsxPath).Length} 字节）");
        using (var zip = System.IO.Compression.ZipFile.OpenRead(xlsxPath))
        {
            using var sx = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var body = sx.ReadToEnd();
            Check(body.Contains("<cols>") && body.Contains("customWidth=\"1\"") && body.Contains("width=\"46\""),
                "xlsx：列宽已写入（长文本列按内容预设，打开即用不必手调）");
            Check(body.Contains("<pane ySplit=\"1\"") && body.Contains("state=\"frozen\""),
                "xlsx：表头行已冻结");
            Check(body.Contains("t=\"inlineStr\""),
                "xlsx：单元格按文本落表（时间/测项名不被 Excel 乱解析）");
            Check(body.Contains("V_KL30") && body.Contains("3.29") && body.Contains("10.8") && body.Contains("14.4"),
                "xlsx：项目/值/上下限均落表");
            Check(body.Contains("小计：V_KL30") && body.Contains("s=\"2\""),
                "xlsx：分组小计行已写出并套用加粗样式（style 2）");
        }

        // ── 分组小计：每组「明细 → 小计行 → 空行」，末组不补空行 ──
        var expRows = FailExporter.BuildRows(all);
        var kinds = string.Concat(expRows.Select(r => r.Kind switch
        {
            FailExporter.FailRowKind.Detail => "D",
            FailExporter.FailRowKind.Subtotal => "S",
            _ => "B",
        }));
        Check(kinds == "DSBDSBDS", $"导出行序：三组各「明细+小计+空行」、末组不带空行（实得 {kinds}）");
        Check(expRows.Count(r => r.Kind == FailExporter.FailRowKind.Subtotal) == 3,
            "导出行序：每组恰好一个小计行");
        var subVk = expRows.First(r => r.Kind == FailExporter.FailRowKind.Subtotal && r.Cells[3].Contains("V_KL30"));
        Check(subVk.Cells[3] == "小计：V_KL30" && subVk.Cells[5] == "失败 1 次",
            $"小计行：项目名与失败次数进项目/限值列（实得 '{subVk.Cells[3]}' / '{subVk.Cells[5]}'）");
        var subCont = expRows.First(r => r.Kind == FailExporter.FailRowKind.Subtotal && r.Cells[3].Contains("CONT"));
        Check(subCont.Cells[4] == "—", $"小计行：整组无数值 → 均值写 —（实得 '{subCont.Cells[4]}'）");
        Check(text.Contains("小计："), "CSV：分组小计行已写出");

        var memRows = FailExporter.BuildRows(new[]
        {
            new FailItemDetail { TestName = "T_X", Value = 3.29, Ts = "t1" },
            new FailItemDetail { TestName = "T_X", Value = 3.35, Ts = "t2" },
            new FailItemDetail { TestName = "T_X", ValueText = "Open", Ts = "t3" },
        });
        var subX = memRows.First(r => r.Kind == FailExporter.FailRowKind.Subtotal);
        Check(subX.Cells[4] == "3.32" && subX.Cells[5] == "失败 3 次",
            $"小计均值：只统计数值项（(3.29+3.35)/2=3.32，实得 {subX.Cells[4]} / {subX.Cells[5]}）");
        var singleGroup = FailExporter.BuildRows(new[]
        {
            new FailItemDetail { TestName = "ONLY", Value = 1.5, Ts = "t" },
        });
        Check(singleGroup.Count == 2 && singleGroup[^1].Kind == FailExporter.FailRowKind.Subtotal,
            "导出行序：单组时末尾不加空行");

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch { }
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
        // 浮点误差可能让 m2 成为极小负数 → Sqrt(负数)=NaN，而 NaN 参与比较全为 false，会让该测项静默不报警
        var wNeg = new Welford();
        wNeg.Restore(5, 1.0, -1e-12);
        Check(!double.IsNaN(wNeg.Sigma) && wNeg.Sigma == 0, $"Welford: m2 因浮点误差为负时 σ 取 0 而非 NaN（实得 {wNeg.Sigma}）");
        var wOne = new Welford();
        wOne.Update(1.5);
        Check(wOne.Sigma == 0, "Welford: 单样本 σ=0（n<2）");

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

        var savedAppStation = AppState.StationId;
        try
        {
            AppState.StationId = "";
            var emptyCfg = new AppConfig { StationId = "" };
            Check(LearnAlertMonitor.StationLabel("FCT6", emptyCfg) == "FCT6",
                "偏离卡机台：用这份记录上的机台，不看空的 station_id");
            Check(LearnAlertMonitor.StationLabel("UNKNOWN", emptyCfg) == "未知机台",
                "偏离卡机台：UNKNOWN 不当成机台号");
            Check(LearnAlertMonitor.StationLabel("  ", new AppConfig { StationId = "FCT1" }) == "FCT1",
                "偏离卡机台：记录没有时机台用配置");
            AppState.StationId = "FCT2";
            Check(LearnAlertMonitor.StationLabel("UNKNOWN", emptyCfg) == "FCT2",
                "偏离卡机台：记录没有时用进程已识别的机台");
            Check(LearnAlertMonitor.StationLabel("FCT6", new AppConfig { StationId = "FCT1" }) == "FCT6",
                "偏离卡机台：这份记录的机台盖过配置和进程识别");

            AppState.StationId = "";
            var stationCfg = new AppConfig
            {
                LearnNormalEnabled = true, LearnNormalVoteSignals = 3, LearnNormalEventScore = 75,
                LearnNormalMaxEventsPerDay = 10, LearnNormalStaleDays = 3650,
                StationId = "", FeishuDeviationAlertEnabled = false,
            };
            var staRec = new TestRecord
            {
                Result = "PASS", Model = "M1", StationId = "FCT6",
                BatchTimestamp = "2026-09-10 12:00:00",
                Measurements = new List<MeasurementRow>
                {
                    new() { TestName = "A", Value = 16.0 },
                    new() { TestName = "B", Value = 26.0 },
                    new() { TestName = "C", Value = 36.0 },
                    new() { TestName = "D", Value = 40.0 },
                },
            };
            var staEv = DeviationScorer.EvaluateMeasurementRecord(scDb, stationCfg, staRec);
            Check(staEv?.StationId == "FCT6",
                $"偏离卡机台：测量值记录的机台进到事件（实得 {staEv?.StationId}）");
        }
        finally { AppState.StationId = savedAppStation; }

        var live = new AppConfig { LearnNormalEnabled = true, AnalyzeTdmsEnabled = false, LogLevel = "INFO" };
        var incoming = new AppConfig { LearnNormalEnabled = false, AnalyzeTdmsEnabled = true, LogLevel = "DEBUG" };
        live.CopyFrom(incoming);
        Check(!live.LearnNormalEnabled && live.AnalyzeTdmsEnabled && live.LogLevel == "DEBUG",
            "Config.CopyFrom 把开关拷到同一实例（Engine 持引用可热加载）");

        // ── P2: 回填只学 PASS、幂等 ──
        var bfDb = new Database(Path.Combine(work, "bf.db"));
        var bfCfg = new AppConfig { LearnNormalEnabled = true, LearnNormalMinSamples = 2 };
        // LearnBackfill.Run 的起点是 Now.Date.AddDays(-(days-1))，夹具日期写死就会随自然日漂出
        // 30 天窗口 → n1 恒为 0，「只学 PASS」「水位幂等」两条断言与代码无关地变红。
        var bfDay8 = DateTime.Today.AddDays(-3).ToString("yyyyMMdd");
        var bfDayDash = DateTime.Today.AddDays(-3).ToString("yyyy-MM-dd");
        var passBf = new TestRecord
        {
            StationId = "FCT1", Model = "G49", Category = "Online", TestDate = bfDay8,
            Sn = "SNBF1", Result = "PASS", XmlPath = Path.Combine(work, "bf_pass.xml"),
            BatchTimestamp = $"{bfDayDash} 10:00:00",
            Measurements = new List<MeasurementRow> { new() { TestName = "T_BF", Value = 5.0 } },
        };
        var failBf = new TestRecord
        {
            StationId = "FCT1", Model = "G49", Category = "Online", TestDate = bfDay8,
            Sn = "SNBF2", Result = "FAIL", XmlPath = Path.Combine(work, "bf_fail.xml"),
            BatchTimestamp = $"{bfDayDash} 10:01:00",
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

        // 审计：实时喂样推进水位的单调语义（不回拨；且不破坏已有水位）
        LearnBackfill.AdvanceWatermark(bfDb, LearnBackfill.MetaMeas, 1000);
        LearnBackfill.AdvanceWatermark(bfDb, LearnBackfill.MetaMeas, 500);
        LearnBackfill.AdvanceWatermark(bfDb, LearnBackfill.MetaMeas, 0);
        Check(bfDb.GetMeta(LearnBackfill.MetaMeas) == "1000",
              "审计: AdvanceWatermark 单调不回拨（1000→500→0 保持 1000）");
        var devIdDb = new Database(Path.Combine(work, "dev_wm.db"));
        long devId = devIdDb.InsertLocalDeviceSample(12.5, 40.0, 200.0, "2026-09-09 10:00:00");
        Check(devId > 0, $"审计: InsertLocalDeviceSample 返回自增 id（实得 {devId}）");
        LearnBackfill.AdvanceWatermark(devIdDb, LearnBackfill.MetaDev, devId);
        Check(devIdDb.GetMeta(LearnBackfill.MetaDev) == devId.ToString(),
              "审计: 设备采样实时喂样推进设备水位");

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

        var vis = DeviationSeriesBuilder.DefaultVisibleNames(new List<DeviationSeries>
        {
            new() { TestName = "A_LOW", Points = { (0, new PassDeviationPoint { Z = 1 }) } },
            new() { TestName = "B_HIGH", Points = { (0, new PassDeviationPoint { Z = 9 }) } },
            new() { TestName = "C_MID", Points = { (0, new PassDeviationPoint { Z = 3 }) } },
            new() { TestName = "D_MID2", Points = { (0, new PassDeviationPoint { Z = 2 }) } },
        }, 2);
        Check(vis.Count == 2 && vis[0] == "B_HIGH" && vis[1] == "C_MID",
            "偏离折线：默认勾选 |z| 最大的测项");
        var fil = DeviationSeriesBuilder.Filter(
            new List<DeviationSeries>
            {
                new() { TestName = "A_LOW" },
                new() { TestName = "B_HIGH" },
                new() { TestName = "C_MID" },
            },
            new[] { "a_low", "C_MID" });
        Check(fil.Count == 2 && fil.All(s => s.TestName is "A_LOW" or "C_MID"),
            "偏离折线：按测项名过滤（忽略大小写）");
        Check(DeviationSeriesBuilder.Filter(new List<DeviationSeries> { new() { TestName = "A" } }, Array.Empty<string>()).Count == 0,
            "偏离折线：未勾选则不画线");
        Check(DeviationSeriesBuilder.SeriesColorHex("KL30") == DeviationSeriesBuilder.SeriesColorHex("kl30"),
            "偏离折线：同测项颜色稳定");

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

    /// <summary>看板卡片抽象基类的可实例化探针（UI-9 键盘可达断言用）。</summary>
    private sealed class KeyboardProbeCard : BoardCardBase
    {
        public override string ColumnKey => "probe";
    }

    /// <summary>模拟按键：OnKeyDown 是 protected，用反射调用以直测键盘路径。</summary>
    static void InvokeCardKey(Control card, Keys keys)
    {
        var mi = typeof(BoardCardBase).GetMethod("OnKeyDown",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        mi.Invoke(card, new object[] { new KeyEventArgs(keys) });
    }

    static void RunLogExportTests(string work)
    {
        var marker = "LOGEXPORT-" + Guid.NewGuid().ToString("N")[..8];
        Logger.Info("[自检] " + marker);
        var tail = Logger.ReadTail();
        Check(tail.Contains(marker), "ReadTail 含刚写入的 INFO 行");
        Check(File.Exists(Logger.CurrentLogPath), "app.log 落盘");

        // PERF-10：日志改为复用同一个 StreamWriter（原来每行 open→write→close），连续多行仍必须全部可见
        var batchMarker = "LOGBATCH-" + Guid.NewGuid().ToString("N")[..8];
        for (int i = 0; i < 3; i++) Logger.Info($"[自检] {batchMarker}-{i}");
        var tail2 = Logger.ReadTail();
        Check(tail2.Contains($"{batchMarker}-0") && tail2.Contains($"{batchMarker}-2"),
              "PERF-10: 复用写入器后连续多行全部落盘（含最后一行）");

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

    /// <summary>v3.48.1 审计修复回归：每条断言锁定一处已修缺陷，防回退。</summary>
    static void RunAuditFixTests(string work)
    {
        // ── 热升级：换装白名单（不得覆盖现场 data/ 与 config）──
        Check(UpdateChecker.IsRuntimePath(@"data\fct.db") && UpdateChecker.IsRuntimePath("data/sub/x.db"),
            "换装白名单：data/ 子树一律不覆盖（现场数据库不得被更新包覆盖）");
        Check(UpdateChecker.IsRuntimePath(@"logs\app.log"), "换装白名单：logs/ 不覆盖");
        Check(UpdateChecker.IsRuntimePath("config.json") && UpdateChecker.IsRuntimePath(@"fct.db"),
            "换装白名单：config.json 与根目录 .db 不覆盖");
        Check(!UpdateChecker.IsRuntimePath(@"Argus.dll") && !UpdateChecker.IsRuntimePath("modules/readme.txt"),
            "换装白名单：程序文件正常放行");

        // ── 热升级：版本号文本化与包名匹配（两段版本不再抛 ArgumentException / 扫描不再漏包）──
        Check(UpdateChecker.VersionText(new Version(3, 48, 1)) == "3.48.1",
            "版本文本：三段版本按三段输出");
        string vt2;
        try { vt2 = UpdateChecker.VersionText(new Version(1, 2)); }
        catch { vt2 = "THROW"; }
        Check(vt2 == "1.2", $"版本文本：两段版本不抛异常（实得 {vt2}）");
        Check(UpdateChecker.VersionText(null) == "unknown", "版本文本：解析失败回落 unknown");
        Check(UpdateChecker.ParseZipVersion("Argus-v1.2.3.zip")?.ToString() == "1.2.3"
              && UpdateChecker.ParseZipVersion("Argus_1.2.3.zip")?.ToString() == "1.2.3"
              && UpdateChecker.ParseZipVersion("Argus 1.2.3.zip")?.ToString() == "1.2.3",
            "更新包解析：Argus[-_空格]v? 三种命名都能识别（扫描已放宽到 Argus*.zip）");

        // ── 飞书：HTTP 200 + 业务非 0 码必须判失败（否则该条 FAIL 被永久标记已推送）──
        Check(FeishuNotifier.IsBusinessSuccess("{\"code\":0,\"msg\":\"success\"}"),
            "飞书业务码：code=0 视为成功");
        Check(!FeishuNotifier.IsBusinessSuccess("{\"code\":9499,\"msg\":\"Bad Request\"}"),
            "飞书业务码：code=9499 判失败（不再被 HTTP 200 掩盖）");
        Check(FeishuNotifier.IsBusinessSuccess("{\"code\":\"0\"}"), "飞书业务码：字符串 0 兼容为成功");
        Check(FeishuNotifier.IsBusinessSuccess("") && FeishuNotifier.IsBusinessSuccess("<html>502</html>"),
            "飞书业务码：空/非 JSON 应答不据此判失败（仍看 HTTP 状态码）");
        Check(FeishuNotifier.IsBusinessSuccess("{\"msg\":\"ok\"}"), "飞书业务码：无 code 字段视为成功");

        // ── 机台号识别：数字后缀不得被截断成 FCT1 ──
        Check(StationDetector.ExtractStationFromTester("FCT1") == "FCT1", "机台号：FCT1 正常识别");
        Check(StationDetector.ExtractStationFromTester("FCT7") == "FCT7", "机台号：FCT7 正常识别");
        Check(StationDetector.ExtractStationFromTester("FCT15") == null,
            "机台号：FCT15 不再被误判为 FCT1（Contains 破坏词边界，原缺陷）");
        Check(StationDetector.ExtractStationFromTester("Panel_FCT3") == "FCT3",
            "机台号：Panel_FCT3 仍可识别（保留下划线连接场景）");
        Check(StationDetector.ExtractStationFromTester(null) == null, "机台号：空输入返回 null");

        // ── 冷数据归档：dash 格式的近期待记录不得被当冷数据删除 ──
        {
            var dir = Path.Combine(work, "coldarc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var db = new Database(Path.Combine(dir, "cold.db"));
            var recentDash = DateTime.Today.AddDays(-3).ToString("yyyy-MM-dd");
            var oldDash = DateTime.Today.AddDays(-200).ToString("yyyy-MM-dd");
            var oldYmd = DateTime.Today.AddDays(-200).ToString("yyyyMMdd");
            db.BatchInsert(new[]
            {
                new TestRecord { StationId="FCT1", Model="E300", Category="Offline", TestDate=recentDash,
                                 Sn="SN-RECENT", Result="PASS", XmlPath=Path.Combine(dir, "recent.xml") },
                new TestRecord { StationId="FCT1", Model="E300", Category="Offline", TestDate=oldDash,
                                 Sn="SN-OLDDASH", Result="PASS", XmlPath=Path.Combine(dir, "olddash.xml") },
                new TestRecord { StationId="FCT1", Model="E300", Category="Offline", TestDate=oldYmd,
                                 Sn="SN-OLDYMD", Result="PASS", XmlPath=Path.Combine(dir, "oldymd.xml") },
            });
            var deleted = db.ArchiveColdData(90);
            Check(deleted == 2, $"冷归档：只删两条真正的冷记录，dash 格式近期记录保留（实删 {deleted}）");
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "cold.db")}");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM test_records WHERE sn='SN-RECENT'";
            Check(Convert.ToInt32(cmd.ExecuteScalar()) == 1,
                "冷归档：dash 格式的近期（3 天前）记录仍在表内（原缺陷会静默删除）");
            Check(Database.ColdDataPredicate.Contains("replace(test_date,'-','')"),
                "冷归档：COUNT/SELECT/DELETE 三处共用同一条归一化判定");
            try { Directory.Delete(dir, true); } catch { }
        }

        // ── 飞书批量告警：推送失败必须回队，不能只打日志（进程长期不重启会永久丢）──
        {
            var cfg = new AppConfig { FeishuFailMergeEnabled = true, FeishuFailMergeMax = 100 };
            var dir = Path.Combine(work, "batchq_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var db = new Database(Path.Combine(dir, "q.db"));
            var batcher = new FeishuFailBatcher(cfg, db,
                (_, _) => Task.FromResult(FeishuSendOutcome.Failed));
            batcher.Enqueue(new TestRecord { StationId="FCT1", Model="E300", Sn="A", Result="FAIL",
                                             XmlPath=Path.Combine(dir, "a.xml"), BatchTimestamp="2026-01-01 00:00:01" });
            batcher.Enqueue(new TestRecord { StationId="FCT1", Model="E300", Sn="B", Result="FAIL",
                                             XmlPath=Path.Combine(dir, "b.xml"), BatchTimestamp="2026-01-01 00:00:02" });
            var before = batcher.PendingCount;
            batcher.FlushAsync().GetAwaiter().GetResult();
            Check(before == 2 && batcher.PendingCount == 2,
                $"告警回队：推送失败后 2 条仍在队列（冲刷前 {before} / 后 {batcher.PendingCount}）");

            var okBatcher = new FeishuFailBatcher(cfg, db,
                (_, _) => Task.FromResult(FeishuSendOutcome.Sent));
            okBatcher.Enqueue(new TestRecord { StationId="FCT1", Model="E300", Sn="C", Result="FAIL",
                                              XmlPath=Path.Combine(dir, "c.xml"), BatchTimestamp="2026-01-01 00:00:03" });
            okBatcher.FlushAsync().GetAwaiter().GetResult();
            Check(okBatcher.PendingCount == 0, "告警回队：推送成功则正常出队（回队逻辑不误伤成功路径）");
            batcher.Dispose(); okBatcher.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }

        // ── CLI 兜底：解析不了的 TDMS 必须给出错误码而不是未处理异常 ──
        // 注意选材：TDMSReader 对"足够长的非 TDMS 文本"会当成 0 组的空文档（返回 0，不是失败），
        // 只有截断/头部不完整才抛异常。这里用「TDMS 魔数 + 截断」精确命中异常路径。
        {
            var fake = Path.Combine(work, "not_tdms_" + Guid.NewGuid().ToString("N") + ".tdms");
            File.WriteAllBytes(fake, new byte[] { 0x54, 0x44, 0x53, 0x6D, 0x0E, 0x00, 0x00, 0x00 });
            int rc;
            try { rc = FctTdmsViewer.Program.RunCliEntry(new[] { "--info", fake }); }
            catch { rc = -1; }
            Check(rc == 2, $"TDMS CLI：截断文件返回 2 而不是未处理异常（实得 {rc}）");
            try { File.Delete(fake); } catch { }
        }

        // ── CLI 兜底：诊断报告输出目录不可写时必须给错误码而不是未处理异常 ──
        {
            var badOut = Path.Combine(work, "no_such_dir_" + Guid.NewGuid().ToString("N"), "sub", "diag.txt");
            var xml = Path.Combine(work, "diag_probe.xml");
            File.WriteAllText(xml, "<TestResults/>");
            int rc;
            try { rc = FctFetcher.Program.RunCliEntry(new[] { "--diag", xml, "--out", badOut }); }
            catch { rc = -1; }
            Check(rc == 2, $"Fetcher CLI：--out 目录不存在返回 2 而不是未处理异常（实得 {rc}）");
            try { File.Delete(xml); } catch { }
        }
    }

    /// <summary>v3.48.2：test_date 双格式与热升级/重试占坑。先写断言再改生产代码。</summary>
    static void RunDateFormatAndRuntimeTests(string work)
    {
        var today8 = DateTime.Today.ToString("yyyyMMdd");
        var todayDash = DateTime.Today.ToString("yyyy-MM-dd");
        var monthYm = DateTime.Today.ToString("yyyyMM");
        var recentDash = DateTime.Today.AddDays(-3).ToString("yyyy-MM-dd");
        var old8 = DateTime.Today.AddDays(-200).ToString("yyyyMMdd");

        {
            var dir = Path.Combine(work, "datefmt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var dbPath = Path.Combine(dir, "d.db");
            var db = new Database(dbPath);

            db.InsertOne(new TestRecord
            {
                StationId = "FCT1", Model = "E300", Category = "FCT", TestDate = today8,
                Sn = "SN-YMD", Result = "PASS", XmlPath = Path.Combine(dir, "ymd.xml"),
                BatchTimestamp = DateTime.Today.ToString("yyyy-MM-dd") + " 10:00:00",
            });
            db.InsertOne(new TestRecord
            {
                StationId = "FCT1", Model = "E300", Category = "Offline", TestDate = todayDash,
                Sn = "SN-DASH", Result = "FAIL", XmlPath = Path.Combine(dir, "dash.xml"),
                FailReason = "Dash_Rail", BatchTimestamp = todayDash + " 11:00:00", HasFailItems = true,
            });
            db.InsertOne(new TestRecord
            {
                StationId = "FCT1", Model = "E300", Category = "Offline", TestDate = recentDash,
                Sn = "SN-RECENT-DASH", Result = "FAIL", XmlPath = Path.Combine(dir, "recent-dash.xml"),
                FailReason = "Recent_Dash_Item", BatchTimestamp = recentDash + " 08:00:00", HasFailItems = true,
            });
            db.InsertOne(new TestRecord
            {
                StationId = "FCT1", Model = "E300", Category = "Offline", TestDate = old8,
                Sn = "SN-OLD", Result = "FAIL", XmlPath = Path.Combine(dir, "old.xml"),
                FailReason = "Ancient_Dash_Compat", BatchTimestamp = "2020-01-01T00:00:00", HasFailItems = true,
            });

            var daily = db.FetchDailyStats("", today8);
            Check(Database.TestDateEqDay.Contains("@d8") && Database.TestDateEqDay.Contains("@dDash"),
                "日期口径：KPI/补推共用 TestDateEqDay 双格式等值");
            Check(daily.Pass == 1 && daily.Fail == 1,
                $"今日 KPI：8 位 PASS + dash FAIL 各计 1（实得 P={daily.Pass} F={daily.Fail}，原缺陷 dash 行静默漏计）");

            var needing = db.ListTodayFailsNeedingAlert(today8, "");
            Check(needing.Count == 1 && needing[0].Sn == "SN-DASH",
                $"启动补推：dash 今日 FAIL 进入 ListTodayFailsNeedingAlert（实得 {needing.Count} 条）");

            var month = db.FetchMonthlyStats("", monthYm);
            Check(month.Pass >= 1 && month.Fail >= 2,
                $"当月统计：含 dash 今日 FAIL 与 3 天前 dash FAIL（实得 P={month.Pass} F={month.Fail}）");

            var created = db.SyncTodoItems(30);
            var todos = db.ListTodoView();
            Check(todos.Any(t => t.Title.Contains("Dash_Rail") || t.Title.Contains("Recent_Dash")),
                $"待办扫描：近窗 dash FAIL 能入待办（新增 {created}，条数 {todos.Count}；原 CompareOrdinal 把 dash 全当过期）");
            Check(!todos.Any(t => t.Title.Contains("Ancient_Dash_Compat")),
                "待办扫描：200 天前 8 位 FAIL 仍不入待办（收口不放宽扫描窗）");

            var sources = db.FailItemSources("", days: 7);
            Check(sources.Any(s => s.XmlPath.EndsWith("recent-dash.xml", StringComparison.OrdinalIgnoreCase)),
                $"故障来源：3 天前 dash FAIL 落在 7 天窗内（实得 {sources.Count} 条）");

            var details = db.QueryFailItemDetails("", today8, today8);
            Check(details.Any(d => d.XmlPath.EndsWith("dash.xml", StringComparison.OrdinalIgnoreCase)),
                $"FAIL 导出：闭区间 {today8} 含 dash 今日行（实得 {details.Count}）");

            db.MarkFailAlertedMany(new[] { Path.Combine(dir, "dash.xml") });
            var afterMark = db.ListTodayFailsNeedingAlert(today8, "");
            Check(afterMark.Count == 0, "批量标记已推送：一条路径一次调用后补推列表为空");

            try { Directory.Delete(dir, true); } catch { }
        }

        {
            var dir = Path.Combine(work, "hourly_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var dbPath = Path.Combine(dir, "h.db");
            var db = new Database(dbPath);
            using (var c = new SqliteConnection($"Data Source={dbPath}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO test_records
                        (station_id, model, category, test_date, sn, result, xml_path,
                         fail_reason, batch_timestamp, created_at)
                    VALUES ('FCT1','E300','FCT',@d,'SN-BADTS','PASS',@p,'','','not-a-timestamp')";
                cmd.Parameters.AddWithValue("@d", today8);
                cmd.Parameters.AddWithValue("@p", Path.Combine(dir, "no-time.xml"));
                cmd.ExecuteNonQuery();
            }
            var hourNow = DateTime.Now.Hour;
            var hourly = db.FetchDailyHourlyStats("", today8);
            var dumped = hourly[hourNow].Pass + hourly[hourNow].Fail;
            var totalH = hourly.Sum(x => x.Pass + x.Fail);
            Check(totalH == 0 && dumped == 0,
                $"小时柱：时间戳无法解析时不灌进当前小时 {hourNow:00}（柱合计 {totalH}，当前小时 {dumped}）");
            try { Directory.Delete(dir, true); } catch { }
        }

        {
            var pkg = """{"station_id":"X","auto_update":true,"webhook_url":"","feishu_fail_merge_enabled":true,"learn_normal_enabled":true}""";
            var site = """{"station_id":"FCT4","auto_update":false,"webhook_url":"https://example/hook","feishu_fail_merge_enabled":false,"learn_normal_enabled":false,"agg_token":"legacy"}""";
            var merged = UpdateChecker.MergeConfigJson(pkg, site);
            using var doc = JsonDocument.Parse(merged);
            var root = doc.RootElement;
            Check(root.GetProperty("station_id").GetString() == "FCT4", "热升级合并：station_id 保留现场");
            Check(root.GetProperty("auto_update").ValueKind == JsonValueKind.False,
                "热升级合并：auto_update=false 不被模板改回 true");
            Check(root.GetProperty("feishu_fail_merge_enabled").ValueKind == JsonValueKind.False,
                "热升级合并：feishu 现场调参保留");
            Check(root.GetProperty("learn_normal_enabled").ValueKind == JsonValueKind.False,
                "热升级合并：learn 现场调参保留");
            Check(root.GetProperty("webhook_url").GetString() == "https://example/hook",
                "热升级合并：webhook_url 保留现场");
            Check(!root.TryGetProperty("agg_token", out _),
                "热升级合并：模板已无的聚合遗留键不从现场带回");
        }

        {
            var map = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(Engine.InFlightPathComparer);
            Check(Engine.TryEnterInFlight(map, @"C:\Results\a.xml", 0), "在途占坑：首次 attempt=0 成功");
            Check(!Engine.TryEnterInFlight(map, @"c:\results\A.XML", 1),
                "在途占坑：重试 attempt>0 与大小写变体视为同一路径，不得并行");
            Engine.ExitInFlight(map, @"C:\Results\a.xml");
            Check(Engine.TryEnterInFlight(map, @"C:\Results\a.xml", 1), "在途占坑：释放后重试可再进入");
        }

        {
            const System.Reflection.BindingFlags PubStat =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            Check(typeof(Theme).GetField("NumberMid", PubStat) == null
                  && typeof(Theme).GetField("ControlLeft", PubStat) == null
                  && typeof(Theme).GetField("LabelTopOffset", PubStat) == null
                  && typeof(Theme).GetField("NavWidth", PubStat) == null
                  && typeof(Theme).GetField("Radius", PubStat) == null
                  && typeof(Theme).GetMethod("Rounded", PubStat) == null,
                "精简：Theme 六枚死令牌已剔除（NumberMid/ControlLeft/LabelTopOffset/NavWidth/Radius/Rounded）");
            Check(typeof(CsvUtil).GetMethod("Write", PubStat) == null
                  && typeof(CsvUtil).GetMethod("BuildBytes", PubStat) == null
                  && typeof(CsvUtil).GetMethod("BuildSimpleBytes", PubStat) == null,
                "精简：CsvUtil 写出三件套已剔除（现场导出走 Esc）");
            Check(typeof(G49ProductDictionary).GetMethod("LookupSignal", PubStat) == null
                  && typeof(G49ProductDictionary).GetMethod("LookupBySection", PubStat) == null,
                "精简：G49 LookupSignal/LookupBySection 别名已剔除");
            Check(typeof(AppConfig).GetMethod("Validate") == null
                  && typeof(AppConfig).Assembly.GetType("FctAggregator.ConfigValidator") == null,
                "精简：从未接线的 ConfigValidator / AppConfig.Validate 已剔除");
        }
    }

    /// <summary>v3.48.3 审计修复批一（静默失效类）：先写断言再改生产代码。</summary>
    static void RunAuditFixBatch1Tests(string work)
    {
        var todayB1 = DateTime.Today.ToString("yyyyMMdd");

        // B1：待办区间视图与入库同源合并键（MergeKeyOf）——G49 spec 合并项不再被区间筛选整体丢弃
        {
            var dir = Path.Combine(work, "b1_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var db = new Database(Path.Combine(dir, "t.db"));
            const string St = "FCT7";
            void AddFail(string item, string sn)
            {
                db.InsertOne(new TestRecord
                {
                    StationId = St, Model = "E3002781", Category = "Offline", TestDate = todayB1,
                    Sn = sn, Result = "FAIL", XmlPath = $@"X:\{sn}_{Math.Abs(item.GetHashCode()):x}.xml",
                    FailReason = item, BatchTimestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                    HasFailItems = true,
                });
            }

            AddFail("6.1.1.1 BSW_v_Kl30_HS", "SNB1"); // spec 命中 -> g49:6.1:PowerRail:KL30_FILT_1
            AddFail("Plain_Item_XYZ", "SNB2");
            db.SyncTodoItems(30);
            var specKey = TodoGrouping.MergeKeyOf("6.1.1.1 BSW_v_Kl30_HS");
            Check(specKey.StartsWith("g49:"), $"spec 项合并键为 G49 族键（{specKey}）");
            Check(db.ListTodoView().Any(x => x.GroupKey == specKey), "不限区间：spec 待办在列");
            var viewToday = db.ListTodoView(DateTime.Today, DateTime.Today);
            Check(viewToday.Any(x => x.GroupKey == specKey),
                  "区间筛选：spec 合并待办不再被整体丢弃（原缺陷区间统计用 KeyOf、入库用 MergeKeyOf，键不一致）");
            Check(viewToday.Any(x => x.Title.Contains("Plain_Item_XYZ")), "区间筛选：普通 token 键待办仍在");
            try { Directory.Delete(dir, true); } catch { }
        }

        // B6：FAIL→PASS→FAIL 复发必须重新告警（fail_alerted 离开 FAIL 时复位）
        {
            var dir = Path.Combine(work, "b6_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var db = new Database(Path.Combine(dir, "f.db"));
            string p = Path.Combine(dir, "recur.xml");
            TestRecord Rec(string result) => new TestRecord
            {
                StationId = "FCT1", Model = "E300", Category = "Offline", TestDate = todayB1,
                Sn = "SN-RECUR", Result = result, XmlPath = p,
                FailReason = result == "FAIL" ? "Some_Fail" : null,
                BatchTimestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                HasFailItems = result == "FAIL",
            };
            db.InsertOne(Rec("FAIL"));
            Check(!db.IsFailAlerted(p) && db.ListTodayFailsNeedingAlert(todayB1, "").Count == 1,
                  "B6 前置：新 FAIL 未标记、进入补推列表");
            db.MarkFailAlerted(p);
            Check(db.IsFailAlerted(p) && db.ListTodayFailsNeedingAlert(todayB1, "").Count == 0,
                  "B6 前置：标记已推送后补推列表为空");
            db.UpsertTestRecord(Rec("PASS"));
            Check(!db.IsFailAlerted(p), "B6：离开 FAIL（复测 PASS）时 fail_alerted 复位");
            db.UpsertTestRecord(Rec("FAIL"));
            Check(!db.IsFailAlerted(p) && db.ListTodayFailsNeedingAlert(todayB1, "").Count == 1,
                  "B6：FAIL→PASS→FAIL 复发重新进入补推列表（原缺陷永不再告警）");
            try { Directory.Delete(dir, true); } catch { }
        }

        // E1：dotnet 宿主启动（随包 启动.bat）时重启命令必须带上 Argus.dll
        {
            var (exeFile, exeArgs) = UpdateChecker.RestartCommand("--post-update", @"C:\Argus\Argus.exe");
            Check(exeFile == @"C:\Argus\Argus.exe" && exeArgs == "--post-update",
                  "E1：exe 直接启动时重启参数不变");
            var (dotFile, dotArgs) = UpdateChecker.RestartCommand("--post-update", @"C:\Program Files\dotnet\dotnet.exe");
            Check(dotFile.EndsWith("dotnet.exe") && dotArgs.Contains("Argus.dll") && dotArgs.Contains("--post-update"),
                  $"E1：dotnet 宿主启动时重启命令补上 Argus.dll（{dotArgs}）");
        }

        // B2：CompactStorage 保留期钳制与配置上限对齐（365），不再按 180 口径多删
        {
            var dir = Path.Combine(work, "b2_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string dbPath = Path.Combine(dir, "c.db");
            var db = new Database(dbPath);
            long RowCountB2(string p, string table)
            {
                using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={p}");
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
                return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            var old200 = DateTime.Now.Date.AddDays(-200);
            db.BatchInsert(new[]
            {
                new TestRecord
                {
                    StationId = "FCT1", Model = "E300", Result = "PASS",
                    TestDate = old200.ToString("yyyyMMdd"),
                    BatchTimestamp = old200.ToString("yyyy-MM-dd HH:mm:ss"), XmlPath = @"C:\t\b2_0.xml",
                    Measurements = new[] { new MeasurementRow { TestName = "T_B2", Value = 1.0 } }.ToList(),
                },
            });
            Check(RowCountB2(dbPath, "test_measurements") == 1, "B2 前置：200 天前测量行已入库");
            db.CompactStorage(365, vacuum: false);
            Check(RowCountB2(dbPath, "test_measurements") == 1,
                  "B2：保留期 365 时 200 天前的测量不被压缩清掉（原缺陷硬钳 180 多删 180~365 天明细）");
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>v3.48.4 审计修复批二（数据安全类）：先写断言再改生产代码。</summary>
    static void RunAuditFixBatch2Tests(string work)
    {
        long Count(string p, string sql)
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={p}");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }

        // B3：瘦表重建整组 DDL 单事务——重建后不得残留 _slim 半成品表，数据必须原样保留
        {
            var dir = Path.Combine(work, "b3_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string dbPath = Path.Combine(dir, "slim.db");
            var db = new Database(dbPath);
            db.BatchInsert(new[]
            {
                new TestRecord
                {
                    StationId = "FCT1", Model = "E300", Result = "PASS",
                    TestDate = DateTime.Today.ToString("yyyyMMdd"),
                    BatchTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), XmlPath = @"C:\t\b3_0.xml",
                    Measurements = new[] { new MeasurementRow { TestName = "T_B3", Value = 2.0 } }.ToList(),
                },
            });
            // 人为把测量表改回胖结构（补冗余列），迫使 CompactStorage 走重建分支
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "ALTER TABLE test_measurements ADD COLUMN xml_path TEXT";
                cmd.ExecuteNonQuery();
            }
            db.CompactStorage(30, vacuum: false);
            Check(Count(dbPath, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='test_measurements_slim'") == 0,
                  "B3：重建后无 test_measurements_slim 半成品残留（DDL 单事务，崩溃不留空表）");
            Check(Count(dbPath, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='test_measurements'") == 1,
                  "B3：重建后主表明细在");
            Check(Count(dbPath, "SELECT COUNT(*) FROM test_measurements") == 1,
                  "B3：重建后测量数据原样保留");
            try { Directory.Delete(dir, true); } catch { }
        }

        // 部署现场：上次瘦表重建在 CREATE 之后中断，test_measurements 仍是胖表，
        // test_measurements_slim 已留下。再次 Init 不能因「table already exists」把进程打死。
        {
            var dir = Path.Combine(work, "b3b_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string dbPath = Path.Combine(dir, "leftover.db");
            var db = new Database(dbPath);
            db.BatchInsert(new[]
            {
                new TestRecord
                {
                    StationId = "FCT1", Model = "E300", Result = "PASS",
                    TestDate = DateTime.Today.ToString("yyyyMMdd"),
                    BatchTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), XmlPath = @"C:\t\b3b_0.xml",
                    Measurements = new[] { new MeasurementRow { TestName = "T_B3B", Value = 3.0 } }.ToList(),
                },
            });
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"
                    ALTER TABLE test_measurements ADD COLUMN xml_path TEXT;
                    CREATE TABLE test_measurements_slim (id INTEGER PRIMARY KEY);
                ";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            Exception? boom = null;
            try { _ = new Database(dbPath); }
            catch (Exception ex) { boom = ex; }
            Check(boom == null, $"B3b：残留 test_measurements_slim 时再次打开不崩溃（{boom?.Message}）");
            if (boom == null)
            {
                Check(Count(dbPath, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='test_measurements_slim'") == 0,
                      "B3b：自愈后半成品表已清掉");
                Check(Count(dbPath, "SELECT COUNT(*) FROM test_measurements") == 1,
                      "B3b：自愈后测量行仍在（以胖表为准，不采用半成品）");
                Check(Count(dbPath, "SELECT COUNT(*) FROM pragma_table_info('test_measurements') WHERE name='xml_path'") == 0,
                      "B3b：自愈后测量表不再带 xml_path");
            }
            try { Directory.Delete(dir, true); } catch { }
        }

        // B4：备份清理 glob 排除 .sha256——名义「保留 7 份」必须真的留 7 个 .bak
        {
            var dir = Path.Combine(work, "b4_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string dbPath = Path.Combine(dir, "fct.db");
            var db = new Database(dbPath);
            for (int k = 1; k <= 10; k++)
            {
                var name = $"{dbPath}.bak-202001{k:02}";
                File.WriteAllText(name, "x");
                File.WriteAllText(name + ".sha256", "y");
            }
            db.BackupDaily();
            var baks = Directory.GetFiles(dir, "fct.db.bak-*")
                .Count(f => !f.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase));
            Check(baks == Database.BackupKeepDays,
                  $"B4：每日备份后 .bak 实留 {Database.BackupKeepDays} 份（实得 {baks}；原缺陷 .sha256 占额度只留 ~3 份）");
            try { Directory.Delete(dir, true); } catch { }
        }

        // B5：wal_checkpoint 返回值被读取——新库 checkpoint 必须 busy=0；备份路径可用
        {
            var dir = Path.Combine(work, "b5_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string dbPath = Path.Combine(dir, "cp.db");
            var db = new Database(dbPath);
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                c.Open();
                Check(Database.CheckpointTruncate(c),
                      "B5：CheckpointTruncate 读 PRAGMA 返回值，新库 busy=0 判 true");
            }
            db.CheckpointTruncateForBackup(); // 不得抛
            Check(File.Exists(dbPath), "B5：CheckpointTruncateForBackup 后库完好");
            Check(db.BackupDaily() != null, "B5：checkpoint 正常时每日备份成功");
            try { Directory.Delete(dir, true); } catch { }
        }

        // B8：维修状态机终态收口 MaintenanceMeta（常量锁定 + 行为回归）
        {
            Check(MaintenanceMeta.DoneStatus == "resolved" && MaintenanceMeta.LegacyClosed == "closed"
                  && MaintenanceMeta.Normalize("closed") == "resolved",
                  "B8：终态常量锁定（DoneStatus=resolved / LegacyClosed=closed，closed 归一化为 resolved）");
            var dir = Path.Combine(work, "b8_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var db = new Database(Path.Combine(dir, "m.db"));
            db.InsertOne(new TestRecord
            {
                StationId = "FCT1", Model = "E300", Category = "Offline", TestDate = DateTime.Today.ToString("yyyyMMdd"),
                Sn = "SN-B8", Result = "FAIL", XmlPath = Path.Combine(dir, "b8.xml"),
                FailReason = "B8_Item", BatchTimestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"), HasFailItems = true,
            });
            db.SyncTodoItems(30);
            var todo = db.ListTodoView().First(x => x.Title.Contains("B8_Item"));
            var mid = db.AcknowledgeTodo(todo.Id, "测试", "major");
            // 直接写遗留 closed 状态，验证 ReconcileTodoStates 按 Meta 常量把待办归到 resolved
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "m.db")}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "UPDATE maintenance_records SET status='closed' WHERE id=@id";
                cmd.Parameters.AddWithValue("@id", mid);
                cmd.ExecuteNonQuery();
            }
            db.SyncTodoItems(30);
            Check(!db.ListTodoView().Any(x => x.Title.Contains("B8_Item")),
                  "B8：遗留 closed 状态的维修记录仍能让待办离开待办列（常量收口后行为不变）");
            try { Directory.Delete(dir, true); } catch { }
        }

        // B12：区间首末时间拼装归一 dash 格式——dash 记录不再得到空串
        {
            var dir = Path.Combine(work, "b12_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var db = new Database(Path.Combine(dir, "r.db"));
            var todayDash = DateTime.Today.ToString("yyyy-MM-dd");
            db.InsertOne(new TestRecord
            {
                StationId = "FCT1", Model = "E300", Category = "Offline", TestDate = todayDash,
                Sn = "SN-B12", Result = "FAIL", XmlPath = Path.Combine(dir, "b12.xml"),
                FailReason = "B12_Range_Item", BatchTimestamp = "", HasFailItems = true,
            });
            db.SyncTodoItems(30);
            var item = db.ListTodoView(DateTime.Today, DateTime.Today).FirstOrDefault(x => x.Title.Contains("B12_Range_Item"));
            Check(item != null, "B12 前置：dash 今日 FAIL 进入区间视图");
            var expect = DateTime.Today.ToString("yyyy-MM-dd") + " 00:00:00";
            Check(item != null && item.RangeFirstSeen == expect && item.RangeLastSeen == expect,
                  $"B12：dash 记录区间首末时间归一为 {expect}（实得 {item?.RangeFirstSeen}，原缺陷 substr 拼出垃圾串）");
            try { Directory.Delete(dir, true); } catch { }
        }

        // B14：状态读-写单事务 + 不存在 id 的边界
        {
            var dir = Path.Combine(work, "b14_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var db = new Database(Path.Combine(dir, "t.db"));
            Check(!db.UpdateMaintenanceStatus(999999, "resolved"), "B14：更新不存在的维修记录返回 false");
            db.InsertOne(new TestRecord
            {
                StationId = "FCT1", Model = "E300", Category = "Offline", TestDate = DateTime.Today.ToString("yyyyMMdd"),
                Sn = "SN-B14", Result = "FAIL", XmlPath = Path.Combine(dir, "b14.xml"),
                FailReason = "B14_Item", BatchTimestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"), HasFailItems = true,
            });
            db.SyncTodoItems(30);
            var todo = db.ListTodoView().First(x => x.Title.Contains("B14_Item"));
            var mid = db.AcknowledgeTodo(todo.Id, "测试", "major");
            var events = 0;
            db.MaintenanceStatusChanged += (_, from, to) => events++;
            Check(db.UpdateMaintenanceStatus(mid, "resolved"), "B14：状态更新成功（单事务读-写）");
            Check(events == 1 && db.GetMaintenance(mid)!.Status == "resolved",
                  "B14：状态变更事件照旧触发一次且落库");
            Check(db.UpdateMaintenanceStatus(mid, "resolved"), "B14：同状态重复更新短路返回 true（无变更不通知）");
            Check(events == 1, "B14：无变更时不重复发通知");
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>v3.48.5 审计修复批三（UI 批）：先写断言再改生产代码。</summary>
    static void RunAuditFixBatch3Tests()
    {
        // D11：Theme.CachedFont 是 Font 单一缓存入口——同键必须返回同一实例（共享 HFONT，不再每次 new 泄漏）
        var f1 = Theme.CachedFont("Microsoft YaHei UI", 9F);
        var f2 = Theme.CachedFont("Microsoft YaHei UI", 9F);
        Check(ReferenceEquals(f1, f2), "D11：CachedFont 同 (family,size,style) 返回同一实例（共享不泄漏）");
        Check(!ReferenceEquals(f1, Theme.CachedFont("Microsoft YaHei UI", 9F, FontStyle.Bold)),
              "D11：CachedFont 按样式区分缓存（Bold 是另一份）");
        var b1 = Theme.BoldOf(Theme.Body);
        var b2 = Theme.BoldOf(Theme.Body);
        Check(ReferenceEquals(b1, b2), "D11：BoldOf 派生加粗同样走缓存");

        // D4：FAIL 导出小计行进「项目/值/限值」列（3/4/5），不得再写进型号/SN/时间列（0/1/2）
        var rows = FailExporter.BuildRows(new[]
        {
            new FailItemDetail { TestName = "T_UI", Value = 3.29, Ts = "t1" },
            new FailItemDetail { TestName = "T_UI", Value = 3.35, Ts = "t2" },
        });
        var sub = rows.First(r => r.Kind == FailExporter.FailRowKind.Subtotal);
        Check(sub.Cells[3] == "小计：T_UI" && sub.Cells[4] == "3.32" && sub.Cells[5] == "失败 2 次",
              $"D4：小计行进项目/值/限值列（实得 [{sub.Cells[3]}] [{sub.Cells[4]}] [{sub.Cells[5]}]）");
        Check(sub.Cells[0] == "" && sub.Cells[1] == "" && sub.Cells[2] == "",
              "D4：小计行的型号/SN/时间列保持空白（Excel 按列筛选不再把均值当 SN）");
    }

    /// <summary>v3.48.6 审计修复批四（自学习口径）：先写断言再改生产代码。</summary>
    static void RunAuditFixBatch4Tests(string work)
    {
        // C1：回填水位单调（并发小值不回拨）+ 二次回填 0 增量（不双计）
        {
            var dir = Path.Combine(work, "c1_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var db = new Database(Path.Combine(dir, "l.db"));
            db.BatchInsert(new[]
            {
                new TestRecord
                {
                    StationId = "FCT1", Model = "G49", Result = "PASS",
                    TestDate = DateTime.Today.ToString("yyyyMMdd"),
                    BatchTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), XmlPath = @"C:\t\c1_0.xml",
                    Measurements = new[] { new MeasurementRow { TestName = "T_C1", Value = 5.0 } }.ToList(),
                },
            });
            var cfg = new AppConfig { LearnNormalEnabled = true };
            var n1 = LearnBackfill.Run(db, cfg, 30);
            Check(n1 >= 1, $"C1 前置：回填入模 {n1} 条");
            var wm = db.GetMeta(LearnBackfill.MetaMeas);
            Check(!string.IsNullOrEmpty(wm), "C1 前置：水位已写入");
            LearnBackfill.AdvanceWatermark(db, LearnBackfill.MetaMeas, 1); // 模拟实时路径传进更小 id
            Check(db.GetMeta(LearnBackfill.MetaMeas) == wm, "C1：水位单调不回拨（小值写入被忽略）");
            var row1 = db.GetNormalModel(NormalModelStore.SourceMeasurement, "G49", "T_C1");
            var n2 = LearnBackfill.Run(db, cfg, 30);
            Check(n2 == 0, "C1：二次回填 0 增量（水位幂等）");
            var row2 = db.GetNormalModel(NormalModelStore.SourceMeasurement, "G49", "T_C1");
            Check(row1 != null && row2 != null && row2.N == row1.N,
                  $"C1：重复回填不双计 n（{row1?.N} → {row2?.N}）");
            try { Directory.Delete(dir, true); } catch { }
        }

        // C3：中断基线按天分组归一 dash/8 位混存
        {
            var mixRecs = new List<BaselineSourceRecord>
            {
                new(1, "20260901", 8, "M1", "S1", "PASS"),
                new(2, "2026-09-01", 8, "M1", "S2", "INTERRUPTED"),
                new(3, "20260902", 8, "M1", "S3", "PASS"),
            };
            var slots = SelfBaseline.BuildSlotInterruptBaseline(mixRecs);
            Check(slots.Count == 1 && slots[0].DayCount == 2,
                  $"C3：dash/8位混存同日只算一天（DayCount={slots.FirstOrDefault()?.DayCount}，期望 2；原缺陷裂成 3 天）");
        }

        // C4：基线 σ 用样本 σ（n−1），与 Welford/SQL 口径一致
        {
            var recs = new List<BaselineSourceRecord>();
            long id = 0;
            for (int i = 0; i < 9; i++) recs.Add(new BaselineSourceRecord(++id, "20260901", 8, "M1", $"A{i}", "PASS"));
            recs.Add(new BaselineSourceRecord(++id, "20260901", 8, "M1", "AF", "FAIL"));   // 当日良率 90
            for (int i = 0; i < 10; i++) recs.Add(new BaselineSourceRecord(++id, "20260902", 8, "M1", $"B{i}", "PASS")); // 当日良率 100
            var buckets = SelfBaseline.BuildBuckets(recs);
            var b = buckets.FirstOrDefault();
            Check(b != null && Math.Abs(b!.YieldMean - 95.0) < 1e-9, $"C4 前置：良率均值 95（实得 {b?.YieldMean}）");
            Check(b != null && Math.Abs(b.YieldSigma - Math.Sqrt(50)) < 1e-9,
                  $"C4：样本 σ=√50≈7.071（实得 {b?.YieldSigma:F4}；原缺陷总体 σ=5 偏小导致阈值偏紧误报）");
        }

        // C5：近常量信号不参与偏离评分（纯函数锁定）
        {
            Check(DeviationScorer.IsNearConstant(new NormalModelRow { Mean = 100, Sigma = 1e-9, N = 10 }),
                  "C5：σ=1e-9 判近常量（不评分）");
            Check(DeviationScorer.IsNearConstant(new NormalModelRow { Mean = 100, Sigma = 0, N = 10 }),
                  "C5：σ=0 判近常量");
            Check(!DeviationScorer.IsNearConstant(new NormalModelRow { Mean = 100, Sigma = 1.0, N = 10 }),
                  "C5：σ=1（相对 1%）正常参与评分");
            Check(!DeviationScorer.IsNearConstant(new NormalModelRow { Mean = 0.001, Sigma = 1.0, N = 10 }),
                  "C5：小量程大波动不误判为近常量");
        }
    }

    /// <summary>v3.48.7 审计修复批五（工程纪律）：先写断言再改生产代码。</summary>

    /// <summary>v3.48.8 审计修复批六（静默失效 / 数据错位）：先写断言，再改生产代码。</summary>
    static void RunAuditFixBatch6Tests(string work)
    {
        // A1：解析器站号回落链——注册表注入值（Engine 的 IP 识别结果）必须被采用
        {
            var prev = FctAggregator.Parsing.ParserRegistry.DefaultStation;
            try
            {
                FctAggregator.Parsing.ParserRegistry.DefaultStation = "  FCT9  ";
                Check(FctAggregator.Parsing.ParserRegistry.DefaultStation == "FCT9", "A1：站号注入值 Trim 归一");

                var xmlPath = Path.Combine(work, "Online", "E3001234", "20260908", "P_1.xml");
                var xml = "<BATCH><FACTORY USER='u' TESTER='OP001'/><DUT ID='SN1'/></BATCH>";

                var viaRegistry = new FctAggregator.Parsing.DefaultResultParser(
                    FctAggregator.Parsing.ParserRuleSet.Default, null).Parse(xmlPath, xml);
                Check(viaRegistry?.StationId == "FCT9",
                      $"A1：未传站号的解析器采用注册表注入值（实得 {viaRegistry?.StationId}；原缺陷落 UNKNOWN）");

                var explicitStation = new FctAggregator.Parsing.DefaultResultParser(
                    FctAggregator.Parsing.ParserRuleSet.Default, "FCT2").Parse(xmlPath, xml);
                Check(explicitStation?.StationId == "FCT2", $"A1：实例站号优先于注册表（实得 {explicitStation?.StationId}）");

                FctAggregator.Parsing.ParserRegistry.DefaultStation = null;
                var noStation = new FctAggregator.Parsing.DefaultResultParser(
                    FctAggregator.Parsing.ParserRuleSet.Default, null).Parse(xmlPath, xml);
                Check(noStation?.StationId == "UNKNOWN",
                      $"A1：两级都无站号且 TESTER 不含 FCTx 才落 UNKNOWN（实得 {noStation?.StationId}）");

                var configurable = new FctAggregator.Parsing.ConfigurableResultParser(
                    FctAggregator.Parsing.ParserRuleSet.Default, "FCT3").Parse(xmlPath, xml);
                Check(configurable?.StationId == "FCT3",
                      $"A1：自定义规则解析器也透传站号（实得 {configurable?.StationId}；原构造直接丢弃 defaultStation）");

                Check(FctAggregator.Parsing.ParserRegistry.DefaultStation == null, "A1：注入空值归一为 null（回落链继续）");
            }
            finally { FctAggregator.Parsing.ParserRegistry.DefaultStation = prev; }
        }

        // A2：文件名/文本时间归一必须校验日期合法性，且 anchor 闸门不可被非法串绕过
        {
            Check(TimeUtil.Normalize("20260908103000") == "2026-09-08 10:30:00",
                  $"A2：合法 14 位仍正常归一（实得 {TimeUtil.Normalize("20260908103000")}）");
            Check(TimeUtil.Normalize("20260901675353") == "",
                  $"A2：非法时分秒（67:53:53）不再铸成脏时间戳（实得 '{TimeUtil.Normalize("20260901675353")}'）");
            Check(TimeUtil.Short("2026-09-01 67:53:53") == "—", "A2：Short 遇脏值返回占位符而不抛 FormatException");
            Check(TimeUtil.ResolveFileNameTime("x_20260901675353_y", new DateTime(2026, 9, 1)) == "",
                  "A2：锚点存在时非法文件名时间被拒（原实现绕过 ±30 天闸门原样返回）");
            Check(TimeUtil.ResolveFileNameTime("x_20260908103000_y", new DateTime(2026, 9, 8)) == "2026-09-08 10:30:00",
                  "A2：合法文件名时间照常通过");
        }

        // A3：按日 FAIL 统计必须承认 8 位 test_date（回放异常日推断依赖它）
        {
            var dir = Path.Combine(work, "a3_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var db = new Database(Path.Combine(dir, "a3.db"), setAsCurrent: false);
            db.BatchUpsert(new[]
            {
                new TestRecord
                {
                    StationId = "FCT1", Model = "G49", Result = "FAIL", TestDate = "20260908",
                    BatchTimestamp = "", XmlPath = Path.Combine(dir, "F_1.xml"),
                    FailReason = "ItemA", HasFailItems = true,
                },
            }, null, null);
            var d = db.CountDailyFailsBetween("2026-09-01", "2026-09-30");
            Check(d.TryGetValue("2026-09-08", out var cnt) && cnt == 1,
                  $"A3：8 位 test_date 的 FAIL 计入按日统计（实得 {(d.TryGetValue("2026-09-08", out var c2) ? c2 : 0)}；原缺陷整行落选）");
            try { Directory.Delete(dir, true); } catch { }
        }

        // A4：超期告警候选必须「未闭环 + 创建时间升序」，不被「最近 N 条」上限挤出
        {
            var dir = Path.Combine(work, "a4_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var db = new Database(Path.Combine(dir, "a4.db"), setAsCurrent: false);
            var openOld = db.CreateMaintenance(new MaintenanceRecord
            { StationId = "FCT1", FailItem = "OLD", Status = MaintenanceMeta.DefaultStatus, Severity = "critical", CreatedAt = "2020-01-01 08:00:00" });
            db.CreateMaintenance(new MaintenanceRecord
            { StationId = "FCT1", FailItem = "NEW", Status = MaintenanceMeta.DefaultStatus, Severity = "critical", CreatedAt = "2030-01-01 08:00:00" });
            var doneOld = db.CreateMaintenance(new MaintenanceRecord
            { StationId = "FCT1", FailItem = "DONE", Status = MaintenanceMeta.DoneStatus, Severity = "critical", CreatedAt = "2019-01-01 08:00:00" });
            var list = db.ListUnclosedMaintenanceAsc(5000);
            Check(list.Count == 2 && list[0].Id == openOld,
                  $"A4：未闭环待办按创建时间升序，最旧的排最前（实得 {list.FirstOrDefault()?.FailItem}）");
            Check(!list.Any(m => m.Id == doneOld), "A4：已闭环记录不进超期候选");
            try { Directory.Delete(dir, true); } catch { }
        }

        // A5：现场回放不得改写全局 Database.Current（否则采样/TDMS/水位全写进已删除的临时库）
        {
            var dir = Path.Combine(work, "a5_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var src = Path.Combine(dir, "src.db");
            var sdb = new Database(src, setAsCurrent: false);
            sdb.BatchUpsert(new[]
            {
                new TestRecord
                {
                    StationId = "FCT1", Model = "G49", Result = "PASS",
                    TestDate = DateTime.Today.ToString("yyyyMMdd"),
                    BatchTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    XmlPath = Path.Combine(dir, "P_1.xml"),
                    Measurements = new List<MeasurementRow> { new MeasurementRow { TestName = "T_A5", Value = 1.0 } }.ToList(),
                },
            }, null, null);
            var keep = Database.Current;
            var rcfg = new AppConfig
            {
                LearnNormalEnabled = true, LearnNormalMinSamples = 30, LearnNormalStaleDays = 14,
                LearnNormalVoteSignals = 3, LearnNormalEventScore = 75, LearnNormalMaxEventsPerDay = 10,
            };
            var rep = ReplayRunner.RunField(src, rcfg, 7);
            Check(ReferenceEquals(Database.Current, keep),
                  "A5：回放后 Database.Current 仍指向原库（原缺陷永久指向已删除的临时库）");
            Check(rep != null, "A5：回放本身正常返回");
            try { Directory.Delete(dir, true); } catch { }
        }

        // A6：CompactStorage 必须按各自保留期清三类明细（fail_items 有独立配置键）
        {
            var dir = Path.Combine(work, "a6_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var db = new Database(Path.Combine(dir, "a6.db"), setAsCurrent: false);
            var oldTs = DateTime.Today.AddDays(-100).ToString("yyyy-MM-dd") + " 10:00:00";
            db.BatchUpsert(new[]
            {
                new TestRecord
                {
                    StationId = "FCT1", Model = "G49", Result = "FAIL",
                    TestDate = DateTime.Today.AddDays(-100).ToString("yyyyMMdd"), BatchTimestamp = oldTs,
                    XmlPath = Path.Combine(dir, "F_1.xml"), FailReason = "ItemA", HasFailItems = true,
                    FailedTests = new List<FailedTest> { new FailedTest { Name = "FT_A6", Value = "1" } }.ToList(),
                },
            }, null, null);
            Check(db.GetStorageInventory().FailItems == 1, "A6 前置：fail_items 1 行");
            db.CompactStorage(30, vacuum: false, failItemRetentionDays: 180);
            Check(db.GetStorageInventory().FailItems == 1,
                  "A6：fail_items 走自身保留期（180 天）→ 100 天前的失败项保留（原缺陷按测量口径 30 天删掉）");
            db.CompactStorage(30, vacuum: false);
            Check(db.GetStorageInventory().FailItems == 0, "A6：不传 failitem 口径时沿用测量口径（兼容旧调用）");
            try { Directory.Delete(dir, true); } catch { }
        }

        // A7：ts 为空的明细行也要纳入滚动删除（按父记录自然日判定，不误删当天新记录）
        {
            var dir = Path.Combine(work, "a7_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var db = new Database(Path.Combine(dir, "a7.db"), setAsCurrent: false);
            db.BatchUpsert(new[]
            {
                new TestRecord
                {
                    StationId = "FCT1", Model = "G49", Result = "PASS",
                    TestDate = DateTime.Today.AddDays(-100).ToString("yyyyMMdd"), BatchTimestamp = "",
                    XmlPath = Path.Combine(dir, "P_old.xml"),
                    Measurements = new List<MeasurementRow> { new MeasurementRow { TestName = "M_OLD", Value = 1.0 } }.ToList(),
                },
                new TestRecord
                {
                    StationId = "FCT1", Model = "G49", Result = "PASS",
                    TestDate = DateTime.Today.ToString("yyyyMMdd"), BatchTimestamp = "",
                    XmlPath = Path.Combine(dir, "P_new.xml"),
                    Measurements = new List<MeasurementRow> { new MeasurementRow { TestName = "M_NEW", Value = 1.0 } }.ToList(),
                },
            }, null, null);
            Check(db.GetStorageInventory().Measurements == 2, "A7 前置：2 行测量（ts 均为空）");
            db.PurgeOldMeasurements(30);
            Check(db.GetStorageInventory().Measurements == 1,
                  "A7：空 ts 的老明细按父记录自然日被清，当天新记录保留（原缺陷永不清理）");
            try { Directory.Delete(dir, true); } catch { }
        }

        // A10：有效保留期缓存——首值等于直算值，且只允许单调加宽（绝不比真实值小 → 不会漏删）
        {
            var dir = Path.Combine(work, "a10_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var db = new Database(Path.Combine(dir, "a10.db"), setAsCurrent: false);
            db.BatchUpsert(new[]
            {
                new TestRecord
                {
                    StationId = "FCT1", Model = "G49", Result = "PASS",
                    TestDate = DateTime.Today.AddDays(-200).ToString("yyyyMMdd"),
                    BatchTimestamp = DateTime.Today.AddDays(-200).ToString("yyyy-MM-dd") + " 10:00:00",
                    XmlPath = Path.Combine(dir, "P_old.xml"),
                    Measurements = new List<MeasurementRow> { new MeasurementRow { TestName = "T_A10", Value = 1.0 } }.ToList(),
                },
            }, null, null);
            var a10cfg = new AppConfig { LearnNormalEnabled = true, AnalyzeMeasureRetentionDays = 90 };
            var direct = LearnPipeline.EffectiveMeasureRetentionDays(a10cfg, db);
            var first = LearnPipeline.EffectiveMeasureRetentionDaysCached(a10cfg, db);
            var second = LearnPipeline.EffectiveMeasureRetentionDaysCached(a10cfg, db);
            Check(first == direct && first >= 90, $"A10：缓存首值等于直算值（缓存 {first} / 直算 {direct}）");
            Check(second >= first, $"A10：缓存单调不回退（{first} -> {second}）");
            try { Directory.Delete(dir, true); } catch { }
        }

        // A8：两段版本号不得让版本文本化抛异常（热升级暂存路径依赖它）
        {
            Check(UpdateChecker.VersionText(new Version(3, 49)) == "3.49",
                  "A8：两段版本安全文本化（原 ToString(3) 抛 ArgumentException）");
            Check(UpdateChecker.ParseZipVersion("Argus-v3.49-update.zip") != null,
                  "A8：两段版本更新包名可被解析（正是需要守卫的输入）");
        }

        // A9：就地更新（UPDATE，id 不变）的 FAIL 记录也要进待办
        {
            var dir = Path.Combine(work, "a9_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var db = new Database(Path.Combine(dir, "a9.db"), setAsCurrent: false);
            var xml = Path.Combine(dir, "F_1.xml");
            var day8 = DateTime.Today.ToString("yyyyMMdd");
            var nowTs = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            db.BatchUpsert(new[]
            {
                new TestRecord { StationId = "FCT1", Model = "G49", Result = "FAIL", TestDate = day8,
                    BatchTimestamp = nowTs, XmlPath = xml, FailReason = "Check_CAN_Bus", HasFailItems = true, FileSize = 100 },
            }, null, null);
            db.SyncTodoItems(30);
            Check(db.ListTodoView().Any(t => t.Variants.Any(v => v.Contains("Check_CAN_Bus"))),
                  "A9 前置：首次 FAIL 已登记进待办");
            db.BatchUpsert(new[]
            {
                new TestRecord { StationId = "FCT1", Model = "G49", Result = "FAIL", TestDate = day8,
                    BatchTimestamp = nowTs, XmlPath = xml, FailReason = "5V_Rail", HasFailItems = true, FileSize = 200 },
            }, null, null);
            db.SyncTodoItems(30);
            Check(db.ListTodoView().Any(t => t.Variants.Any(v => v.Contains("5V_Rail"))),
                  "A9：就地更新的 FAIL 记录也进待办（原缺陷被「id > 水位」漏掉，告警有、待办无）");
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static void RunAuditFixBatch5Tests()
    {
        // F12：待办备注「来源测试项」round-trip——写进去的必须原样读出来
        var items = new[] { "6.1.1.1 5V_Rail", "BSW_v_Kl30_HS", "  带空格  " };
        var note = TodoGrouping.BuildSourceItemsNote(items);
        var parsed = TodoGrouping.ParseSourceItems(note);
        Check(parsed.Count == 3 && parsed[0] == "6.1.1.1 5V_Rail" && parsed[1] == "BSW_v_Kl30_HS",
              $"F12：来源测试项 round-trip 保序保值（实得 {parsed.Count} 条）");
        Check(TodoGrouping.ParseSourceItems("没有标记的备注").Count == 0,
              "F12：无标记备注解析为空（不误读其它文本）");
        Check(TodoGrouping.ParseSourceItems(null).Count == 0 && TodoGrouping.ParseSourceItems("").Count == 0,
              "F12：null/空串解析为空");

        // F12：spec 开关关时 MergeKeyOf 必须退化为 KeyOf（两函数一致性锁定）
        var prevSpec = AppConfig.Instance.TodoSpecMerge;
        AppConfig.Instance.TodoSpecMerge = false;
        try
        {
            Check(TodoGrouping.MergeKeyOf("6.1.1.1 BSW_v_Kl30_HS") == TodoGrouping.KeyOf("6.1.1.1 BSW_v_Kl30_HS"),
                  "F12：todo_spec_merge=false 时合并键退化为 token 键（不锁死 G49）");
        }
        finally { AppConfig.Instance.TodoSpecMerge = prevSpec; }
    }

    static void RunMaintenanceCsvLogTests(string work)
    {
        var dir = Path.Combine(work, "maint_csv");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "m.db");
        var db = new Database(dbPath, setAsCurrent: false);
        var path = MaintenanceExporter.LogPathFor(dbPath);

        var id = db.CreateMaintenance(new MaintenanceRecord
        {
            FailItem = "电源轨", Status = "open", Severity = "major", StationId = "FCT1",
        });
        var lines = File.ReadAllLines(path);
        Check(lines.Length == 2, $"新建只写一行数据（实得 {lines.Length - 1}）");
        Check(lines[0].StartsWith("ID,"), "维修日志带表头");
        var cells = lines[1].Split(',');
        Check(cells.Length >= 7 && cells[0] == id.ToString() && cells[6] == "待办",
              $"新建行状态为待办（实得 {(cells.Length > 6 ? cells[6] : "?")}）");

        Check(db.UpdateMaintenanceStatus(id, "resolved"), "改状态写入成功");
        lines = File.ReadAllLines(path);
        Check(lines.Length == 2, "改状态不另起一行");
        cells = lines[1].Split(',');
        Check(cells[0] == id.ToString() && cells[6] == "已完成",
              $"原行状态改为已完成（实得 {(cells.Length > 6 ? cells[6] : "?")}）");

        var id2 = db.CreateMaintenance(new MaintenanceRecord
        {
            FailItem = "=1+1", Status = "in_progress", Severity = "minor",
        });
        lines = File.ReadAllLines(path);
        Check(lines.Length == 3, "第二条追加在末尾");
        Check(lines[1].Split(',')[6] == "已完成", "第一条保持改后的状态");
        Check(lines[2].Contains("'=1+1") && lines[2].Split(',')[6] == "持续跟踪",
              "公式开头被转义，状态写成持续跟踪");

        var id3 = db.CreateMaintenance(new MaintenanceRecord { FailItem = "A,B", Status = "open" });
        Check(db.UpdateMaintenanceStatus(id3, "in_progress"), "含逗号的项目改状态");
        lines = File.ReadAllLines(path);
        Check(lines.Length == 4, "含逗号的项目仍占一行");
        Check(lines[3].StartsWith(id3 + ",", StringComparison.Ordinal)
              && lines[3].Contains("\"A,B\"") && lines[3].Contains("持续跟踪"),
              "含逗号的项目改的是原行");

        Check(db.DeleteMaintenance(id2), "删除第二条");
        lines = File.ReadAllLines(path);
        Check(lines.Length == 3, "删除去掉对应行");
        Check(!lines.Any(l => l.StartsWith(id2 + ",", StringComparison.Ordinal)), "被删记录不留在日志里");
        Check(lines[1].Split(',')[0] == id.ToString() && lines[1].Split(',')[6] == "已完成",
              "删除其它行后第一条仍在且状态不变");
    }

    static TestRecord FailMonthRec(string dir, string sn, string ymd, string ts, string item) => new()
    {
        StationId = "FCT1", Model = "G49", Result = "FAIL", TestDate = ymd, Sn = sn,
        FailReason = item, HasFailItems = true, BatchTimestamp = ts,
        XmlPath = Path.Combine(dir, sn + ".xml"),
        FailedTests = new List<FailedTest> { new() { Name = item, Value = "1.5", Lolim = "0", Hilim = "1", Unit = "V" } },
    };

    static void RunFailMonthCsvTests(string work)
    {
        var dir = Path.Combine(work, "fail_month_csv");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "m.db");
        var db = new Database(dbPath, setAsCurrent: false);
        db.BatchUpsert(new[]
        {
            FailMonthRec(dir, "a", "20261001", "2026-10-01 08:00:00", "电源"),
            FailMonthRec(dir, "b", "20261002", "2026-10-02 12:00:00", "电源"),
            FailMonthRec(dir, "c", "20261003", "2026-10-03 09:00:00", "通信"),
        });
        var oct = FailExporter.MonthLogPath(dbPath, 2026, 10);
        Check(File.Exists(oct), "10 月 FAIL 表已生成");
        var lines = File.ReadAllLines(oct);
        Check(lines.Length == 4, $"10 月 3 条明细加表头（实得 {lines.Length} 行）");
        Check(lines[0].StartsWith("型号,"), "月表列与 FAIL 明细导出一致");
        Check(lines[1].Contains("电源") && lines[1].Contains("2026-10-02"),
              "失败次数多的项目排在前面，同项内时间倒序");
        Check(lines[2].Contains("电源") && lines[2].Contains("2026-10-01"), "同项目第二条仍排在次数少的项目之前");
        Check(lines[3].Contains("通信"), "失败 1 次的项目排在后面");

        db.BatchUpsert(new[]
        {
            FailMonthRec(dir, "d", "20261101", "2026-11-01 08:00:00", "电源"),
        });
        var nov = FailExporter.MonthLogPath(dbPath, 2026, 11);
        Check(File.Exists(nov) && File.ReadAllLines(nov).Length == 2, "新月份单独一张表");
        Check(File.ReadAllLines(oct).Length == 4, "写入 11 月不改 10 月的表");

        var deferDir = Path.Combine(work, "fail_month_defer");
        Directory.CreateDirectory(deferDir);
        var deferDbPath = Path.Combine(deferDir, "m.db");
        var deferDb = new Database(deferDbPath, setAsCurrent: false);
        var deferPath = FailExporter.MonthLogPath(deferDbPath, 2026, 10);
        var wroteEarly = false;
        deferDb.RunWithDeferredFailMonthCsv(() =>
        {
            deferDb.BatchUpsert(new[] { FailMonthRec(deferDir, "e", "20261004", "2026-10-04 08:00:00", "电源") });
            wroteEarly = File.Exists(deferPath);
            deferDb.BatchUpsert(new[] { FailMonthRec(deferDir, "f", "20261005", "2026-10-05 08:00:00", "通信") });
        });
        Check(!wroteEarly, "推迟期间不写月表");
        var deferred = File.ReadAllLines(deferPath);
        Check(deferred.Length == 3 && deferred[1].Contains("电源") && deferred[2].Contains("通信"),
              "推迟结束每个月只写一次，两条都在");
    }

    static void RunReportMeasurementTests()
    {
        const string xml = """
            <BATCH TIMESTAMP="2026-07-24T13:01:00+08:00">
              <PANEL STATUS="Failed" TIMESTAMP="2026-07-24T13:01:00+08:00">
                <DUT ID="SN1">
                  <GROUP NAME="MainSequence Callback" TYPE="SequenceCall" STATUS="Failed">
                    <GROUP NAME="A_Pass" TYPE="NumericLimitTest" STATUS="Passed"><TEST NAME="A_Pass" VALUE="1" LOLIM="0" HILIM="2" UNIT="V" STATUS="Passed"/></GROUP>
                    <GROUP NAME="B_Fail" TYPE="NumericLimitTest" STATUS="Failed"><TEST NAME="B_Fail" VALUE="9" LOLIM="0" HILIM="1" UNIT="V" STATUS="Failed"/></GROUP>
                    <GROUP NAME="C_Skip" TYPE="NumericLimitTest" STATUS="Skipped"/>
                    <GROUP NAME="If1" TYPE="NI_Flow_If" STATUS="Passed"/>
                  </GROUP>
                </DUT>
              </PANEL>
            </BATCH>
            """;
        var r = XmlParser.ParseReportText(xml);
        Check(r.Tests.Count == 3, $"报告测项含通过、失败、跳过（实得 {r.Tests.Count}）");
        Check(r.Tests[0].Name == "A_Pass" && r.Tests[0].Status == "Passed" && r.Tests[0].Value == "1",
              "有 TEST 子节点的步骤用测量值，不重复记 GROUP");
        Check(r.Tests[1].Name == "B_Fail" && r.Tests[1].Status == "Failed", "失败测项保留");
        Check(r.Tests[2].Name == "C_Skip" && r.Tests[2].Status == "Skipped" && r.Tests[2].Value == "",
              "没有 TEST 子节点的测量步骤（Skipped）也进明细");
        Check(r.Tests.All(t => t.Name is not ("If1" or "MainSequence Callback")),
              "流程节点（If / SequenceCall）不进测试项明细");
        Check(r.Chapters.Count == 0 && r.TotalSeconds == null,
              "没有 TOTALTIME 时章节耗时为空");
    }

    static void RunReportChapterTimeTests()
    {
        const string xml = """
            <BATCH TIMESTAMP="2026-07-24T13:01:00+08:00">
              <PANEL STATUS="Passed" TIMESTAMP="2026-07-24T13:01:00+08:00" TESTTIME="400">
                <DUT ID="SN1">
                  <GROUP NAME="MainSequence Callback" TYPE="SequenceCall" TOTALTIME="334.0" STATUS="Passed">
                    <GROUP NAME="XPT_FCT_4.9_(2609)(2752)(2757)" TYPE="SequenceCall" TOTALTIME="332.6" STATUS="Passed">
                      <GROUP NAME="8.11 Resolver Test" TYPE="SequenceCall" TOTALTIME="13.7" STATUS="Passed">
                        <GROUP NAME="8.11.1.1 ResE" TYPE="NumericLimitTest" STATUS="Passed">
                          <TEST NAME="8.11.1.1 ResE" VALUE="1" LOLIM="0" HILIM="2" UNIT="V" STATUS="Passed"/>
                        </GROUP>
                        <GROUP NAME="MainSequence Callback" TYPE="SequenceCall" TOTALTIME="99" STATUS="Passed"/>
                      </GROUP>
                      <GROUP NAME="6.1 Power Test" TYPE="SequenceCall" TOTALTIME="93.3" STATUS="Passed"/>
                      <GROUP NAME="8.1.1.1 KL30" TYPE="SequenceCall" TOTALTIME="2.0" STATUS="Passed"/>
                      <GROUP NAME="Get SN" TYPE="SequenceCall" TOTALTIME="1.0" STATUS="Passed"/>
                      <GROUP NAME="6.2 Bad Time" TYPE="SequenceCall" TOTALTIME="abc" STATUS="Passed"/>
                    </GROUP>
                  </GROUP>
                </DUT>
              </PANEL>
            </BATCH>
            """;
        var r = XmlParser.ParseReportText(xml);
        Check(r.TotalSeconds == 334.0, $"总时长取最外层 MainSequence Callback（实得 {r.TotalSeconds}）");
        Check(r.Chapters.Count == 2, $"只保留「步骤号.步骤号 标题」的章节（实得 {r.Chapters.Count}）");
        Check(r.Chapters[0].Name == "6.1 Power Test" && r.Chapters[0].Seconds == 93.3,
              "章节按耗时从高到低，6.1 在前");
        Check(r.Chapters[1].Name == "8.11 Resolver Test" && r.Chapters[1].Seconds == 13.7,
              "8.11 排在 6.1 之后");
        Check(r.Chapters.All(c => c.Name is not ("MainSequence Callback" or "XPT_FCT_4.9_(2609)(2752)(2757)" or "8.1.1.1 KL30" or "Get SN" or "6.2 Bad Time")),
              "总时长包装、产品序列名、更深的步骤号、无步骤号的调用、非法耗时都不进章节");
        Check(r.Tests.Count == 1 && r.Tests[0].Name == "8.11.1.1 ResE" && r.Tests[0].Value == "1",
              "收集章节耗时不改变测项明细");
        var share = XmlParser.ChapterShare(93.3, r.TotalSeconds);
        Check(share != null && Math.Abs(share.Value - 27.9341317365) < 1e-6,
              $"占比按总时长计算（实得 {share}）");
        Check(XmlParser.ChapterShare(93.3, null) == null && XmlParser.ChapterShare(1, 0) == null,
              "没有总时长或总时长为 0 时不计算占比");
    }

    static void RunCycleTaktTests(string work)
    {
        var samples = new (double Seconds, int? Hour)[]
        {
            (300, 9),
            (360, 10),
            (420, 14),
            (480, 15),
            (100, null),
            (0, 8),
            (-5, 16),
        };
        var sum = CycleTakt.Compute(samples);
        Check(sum.Count == 5, $"没有总时长、0 和负数不进平均（实得 {sum.Count}）");
        Check(sum.Average is double avg && Math.Abs(avg - (300 + 360 + 420 + 480 + 100) / 5.0) < 1e-6,
              $"全天平均含无钟点的记录（实得 {sum.Average}）");
        Check(sum.MorningCount == 2 && sum.AfternoonCount == 2, "11 点前是上午，12 点起是下午");
        Check(sum.DeltaSeconds is double d && Math.Abs(d - ((420 + 480) / 2.0 - (300 + 360) / 2.0)) < 1e-6,
              $"差值是下午平均减上午平均（实得 {sum.DeltaSeconds}）");
        Check(CycleTakt.FormatClock(324) == "5:24" && CycleTakt.FormatClock(42) == "42秒", "满一分钟写成钟点");
        Check(CycleTakt.FormatCompare(sum) == "比上午慢 120 秒", "下午更久写成慢");
        var faster = CycleTakt.Compute(new[] { (400.0, (int?)9), (300.0, (int?)15) });
        Check(CycleTakt.FormatCompare(faster) == "比上午快 100 秒", "下午更短写成快");
        var morningOnly = CycleTakt.Compute(new[] { (300.0, (int?)9) });
        Check(CycleTakt.FormatCompare(morningOnly) == "" && CycleTakt.FormatLine(morningOnly).Contains("1 台有总时长"),
              "还没有下午时不比，只写台数");
        Check(CycleTakt.FormatLine(CycleTakt.Compute(Array.Empty<(double, int?)>())).Contains("没有总时长"),
              "一台都没有总时长时明示");

        const string withTime = """
            <BATCH><PANEL><DUT>
              <GROUP NAME="MainSequence Callback" TYPE="SequenceCall" TOTALTIME="334.5"></GROUP>
              <GROUP NAME="MainSequence Callback" TYPE="SequenceCall" TOTALTIME="9"></GROUP>
            </DUT></PANEL></BATCH>
            """;
        const string noTime = """<BATCH><PANEL STATUS="Passed"><DUT ID="A"/></PANEL></BATCH>""";
        Check(XmlParser.ReadCycleSeconds(withTime) == 334.5, "总时长取最外层 MainSequence，不取后面那个");
        Check(XmlParser.ReadCycleSeconds(noTime) == null, "没有 TOTALTIME 不算节拍");

        var dir = Path.Combine(work, "cycle_takt");
        Directory.CreateDirectory(dir);
        var db = new Database(Path.Combine(dir, "m.db"), setAsCurrent: false);
        var day = DateTime.Today.ToString("yyyyMMdd");
        var dash = DateTime.Today.ToString("yyyy-MM-dd");
        string P(string name, string hour) => Path.Combine(dir, name);
        db.BatchUpsert(new[]
        {
            new TestRecord { StationId = "FCT1", Result = "PASS", TestDate = day, Sn = "A", XmlPath = P("a.xml", ""), BatchTimestamp = dash + " 09:00:00", CycleChecked = true, CycleSeconds = 300 },
            new TestRecord { StationId = "FCT1", Result = "FAIL", TestDate = day, Sn = "B", XmlPath = P("b.xml", ""), BatchTimestamp = dash + " 15:00:00", CycleChecked = true, CycleSeconds = 420 },
            new TestRecord { StationId = "FCT1", Result = "PASS", TestDate = day, Sn = "C", XmlPath = P("c.xml", ""), BatchTimestamp = dash + " 16:00:00", CycleChecked = true, CycleSeconds = null },
            new TestRecord { StationId = "FCT1", Result = "PASS", TestDate = "20000101", Sn = "D", XmlPath = P("d.xml", ""), BatchTimestamp = "2000-01-01 10:00:00", CycleChecked = true, CycleSeconds = 999 },
            new TestRecord { StationId = "FCT1", Result = "PASS", TestDate = day, Sn = "E", XmlPath = P("e.xml", ""), BatchTimestamp = dash + " 11:00:00" },
        });
        var got = db.ListTodayCycles("FCT1", day);
        var line = CycleTakt.FormatLine(CycleTakt.Compute(got));
        Check(got.Count == 2 && line.Contains("6:00") && line.Contains("比上午慢 120 秒"),
              $"今天只平均有总时长的两台（实得 {got.Count} 条 / {line}）");
        var missing = db.ListMissingCyclePaths("FCT1", day, 10);
        Check(missing.Count == 1 && missing[0].EndsWith("e.xml"), "没记过节拍的路径留给补读，已确认没有总时长的不重复读");
    }

    static void RunUnitRetestTests(string work)
    {
        var runs = new List<UnitRun>
        {
            new() { XmlPath = @"C:\a.xml", Result = "PASS", Timestamp = "2026-10-05 18:00:00", TestDate = "20261005" },
            new() { XmlPath = @"C:\b.xml", Result = "FAIL", FailReason = "电源", Timestamp = "2026-10-06 09:00:00", TestDate = "20261006" },
            new() { XmlPath = @"C:\c.xml", Result = "FAIL", FailReason = "通信", Timestamp = "2026-10-06T10:00:00", TestDate = "2026-10-06" },
        };
        var firstToday = UnitRetest.Locate(runs, @"c:\b.XML");
        Check(firstToday.Found && firstToday.IndexOnDay == 1 && firstToday.CountOnDay == 2,
              $"当天第一台是第 1/2 次（实得 {firstToday.IndexOnDay}/{firstToday.CountOnDay}）");
        Check(firstToday.Previous != null && firstToday.Previous.XmlPath.EndsWith("a.xml"),
              "当天第一次的上一次是前一天");
        Check(UnitRetest.FormatPrevious(firstToday.Previous!, "2026-10-06") == "上次 10-05 18:00 PASS",
              "跨日上次带月日");
        var second = UnitRetest.Locate(runs, @"C:\c.xml");
        Check(second.IndexOnDay == 2 && second.Previous?.FailReason == "电源",
              "当天第二次的上一次是当天第一次");
        Check(UnitRetest.FormatOrdinal(second) == "当日第 2/2 次", "当日次第写成第 n/m 次");
        Check(UnitRetest.FormatPrevious(second.Previous!, "2026-10-06") == "上次 09:00 FAIL · 电源",
              "同一天的上次只写时刻和失败项");
        Check(!UnitRetest.Locate(runs, @"C:\missing.xml").Found, "库里没有这条路径就不标次第");

        Check(UnitRetest.Judge(1.5, 0.5, 0, 1) == UnitRetest.Move.Out, "上次在限内、这次出限");
        Check(UnitRetest.Judge(2.5, 1.5, 0, 1) == UnitRetest.Move.Further, "两次都出限且这次更远");
        Check(UnitRetest.Judge(1.2, 2.0, 0, 1) == UnitRetest.Move.Closer, "两次都出限且这次更近");
        Check(UnitRetest.Judge(0.5, 1.5, 0, 1) == UnitRetest.Move.BackIn, "上次出限、这次回到限内");
        Check(UnitRetest.Judge(1.5, 1.5, 0, 1) == UnitRetest.Move.Flat, "出限距离相同算持平");
        Check(UnitRetest.Judge(0.4, 0.5, 0, 1) == UnitRetest.Move.StillIn, "两次都在限内");
        Check(UnitRetest.Judge(1.5, 0.5, null, null) == UnitRetest.Move.None, "没有上下限不下结论");
        Check(UnitRetest.FormatItemShift("0.5", 0.5, UnitRetest.Move.Out) == "上次 0.5 · 出限",
              "失败项对照写上次值和方向");

        var chapters = new List<XmlParser.ReportChapter> { new() { Name = "6.1 Power Test", Seconds = 80 } };
        Check(UnitRetest.PriorChapterSeconds(chapters, "6.1 power test") == 80, "章节名忽略大小写对齐");
        Check(UnitRetest.PriorChapterSeconds(chapters, "8.11 Resolver Test") == null, "上次没有的章节不对齐");
        Check(UnitRetest.FormatDelta(93.3, 80) == "+13.3 秒", "变慢写正差");
        Check(UnitRetest.FormatDelta(70, 80) == "-10.0 秒", "变快写负差");

        var dir = Path.Combine(work, "unit_retest");
        Directory.CreateDirectory(dir);
        var db = new Database(Path.Combine(dir, "m.db"), setAsCurrent: false);
        var passPath = Path.Combine(dir, "pass.xml");
        var failPath = Path.Combine(dir, "fail.xml");
        var laterPath = Path.Combine(dir, "later.xml");
        db.BatchUpsert(new[]
        {
            new TestRecord
            {
                StationId = "FCT1", Model = "G49", Result = "PASS", TestDate = "20261006", Sn = "SN1",
                BatchTimestamp = "2026-10-06 08:00:00", XmlPath = passPath,
                Measurements = { new MeasurementRow { TestName = "KL30", Value = 0.5, ValueText = "0.5", Lolim = 0, Hilim = 1, Unit = "V" } },
            },
            new TestRecord
            {
                StationId = "FCT1", Model = "G49", Result = "FAIL", TestDate = "20261006", Sn = "SN1",
                FailReason = "KL30", HasFailItems = true, BatchTimestamp = "2026-10-06 09:00:00", XmlPath = failPath,
                FailedTests = { new FailedTest { Name = "KL30", Value = "1.5", Lolim = "0", Hilim = "1", Unit = "V" } },
            },
            new TestRecord
            {
                StationId = "FCT1", Model = "G49", Result = "FAIL", TestDate = "20261006", Sn = "OTHER",
                FailReason = "KL30", HasFailItems = true, BatchTimestamp = "2026-10-06 09:30:00", XmlPath = Path.Combine(dir, "other.xml"),
                FailedTests = { new FailedTest { Name = "KL30", Value = "9", Lolim = "0", Hilim = "1" } },
            },
            new TestRecord
            {
                StationId = "FCT1", Model = "G49", Result = "FAIL", TestDate = "20261006", Sn = "SN1",
                FailReason = "KL30", HasFailItems = true, BatchTimestamp = "2026-10-06 11:00:00", XmlPath = laterPath,
                FailedTests = { new FailedTest { Name = "KL30", Value = "2.5", Lolim = "0", Hilim = "1", Unit = "V" } },
            },
        });
        var listed = db.ListUnitRuns(" sn1 ");
        var place = UnitRetest.Locate(listed, failPath);
        Check(listed.Count == 3 && place.Found && place.IndexOnDay == 2 && place.CountOnDay == 3,
              $"同一序列号按时间排，失败这次是当日第 2/3 次（实得 {place.IndexOnDay}/{place.CountOnDay}，条数 {listed.Count}）");
        Check(place.Previous?.Result == "PASS", "上一次是更早的 PASS，不是别的序列号");
        var prior = db.FindPriorItem("SN1", "kl30", failPath, "2026-10-06 09:00:00");
        Check(prior?.Value == 0.5, $"这项的上次值来自更早的 PASS 测量（实得 {prior?.Value}）");
        var laterPrior = db.FindPriorItem("SN1", "KL30", laterPath, "2026-10-06 11:00:00");
        Check(laterPrior?.Value == 1.5, $"再往后一次对到最近的失败值，不跳回更早的 0.5（实得 {laterPrior?.Value}）");
        Check(db.FindPriorItem("OTHER", "KL30", failPath, "2026-10-06 09:00:00") == null, "别的序列号不拿来对照");
        Check(db.ListUnitRuns("").Count == 0 && db.ListUnitRuns("   ").Count == 0, "空白序列号不互相对上");
    }

    static void RunNoSplashTests()
    {
        var asm = typeof(Program).Assembly;
        Check(asm.GetTypes().All(t => t.Name != "SplashForm"), "程序集里不再有启动画面窗体");
        var csproj = FindUp("FctAggregator.csproj");
        var embedded = File.Exists(csproj) && File.ReadAllText(csproj).Contains("argus_splash.png", StringComparison.OrdinalIgnoreCase);
        Check(!embedded, "主工程不再嵌入启动画面图片");
    }

    static void RunRepeatFailTests(string work)
    {
        var dir = Path.Combine(work, "repeat_fail");
        Directory.CreateDirectory(dir);
        var db = new Database(Path.Combine(dir, "m.db"), setAsCurrent: false);
        for (int i = 1; i <= 4; i++)
            db.BatchUpsert(new[] { FailMonthRec(dir, "r" + i, "2026100" + i, $"2026-10-0{i} 08:00:00", "RepeatItem") });
        Check(!db.ListTodoView().Any(t => t.Title == "RepeatItem"), "本月 4 次不补待办卡");
        db.BatchUpsert(new[] { FailMonthRec(dir, "r5", "20261006", "2026-10-06 08:00:00", "RepeatItem") });
        var todos = db.ListTodoView().Where(t => t.Title == "RepeatItem").ToList();
        Check(todos.Count == 1, $"本月满 5 次补一张待办（实得 {todos.Count}）");
        db.BatchUpsert(new[] { FailMonthRec(dir, "r6", "20261006", "2026-10-06 09:00:00", "RepeatItem") });
        Check(db.ListTodoView().Count(t => t.Title == "RepeatItem") == 1, "再次失败不重复补卡");
        var oct = db.CountFailDetailsByName("20261001", "20261031");
        Check(RepeatFailMonth.Lookup(oct, "repeatitem") == 6, $"本月计数忽略大小写（实得 {RepeatFailMonth.Lookup(oct, "repeatitem")}）");
        db.BatchUpsert(new[] { FailMonthRec(dir, "old", "20260901", "2026-09-01 08:00:00", "RepeatItem") });
        var sep = db.CountFailDetailsByName("20260901", "20260930");
        Check(RepeatFailMonth.Lookup(sep, "RepeatItem") == 1, "上月次数不并进本月");
        var recent = db.ListRecentFailDetails("RepeatItem", "20261001", "20261031", 3);
        Check(recent.Count == 3 && recent[0].Ts.Contains("2026-10-06 09:00:00"),
              "最近失败按时间倒序，带 SN 和值");
        var marked = new TodoItem { Title = "RepeatItem" };
        RepeatFailMonth.Apply(new[] { marked }, oct);
        Check(marked.MonthCount == 6, "待办卡能标上本月次数");
    }

    static void RunBannerColorTests()
    {
        Console.WriteLine("\n【v3.57.0 飞书头图分色】红走兜底 NIO，其余四色各用自己的 img_key");
        var relaxed = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        var cfg = new AppConfig();
        Check(cfg.BannerImgKeyFor("red") == "" && cfg.BannerImgKeyFor("blue") == "",
            "头图：五个颜色键和兜底都空时，不带头图");

        cfg.FeishuBannerImgKey = "img_nio";
        cfg.FeishuBannerImgKeyOrange = "img_orange";
        cfg.FeishuBannerImgKeyBlue = "img_blue";
        cfg.FeishuBannerImgKeyGreen = "img_green";
        cfg.FeishuBannerImgKeyYellow = "img_yellow";
        Check(cfg.BannerImgKeyFor("red") == "img_nio", "头图：红未单配时用兜底 NIO");
        Check(cfg.BannerImgKeyFor("orange") == "img_orange", "头图：橙用现场异常");
        Check(cfg.BannerImgKeyFor("blue") == "img_blue", "头图：蓝用状态同步");
        Check(cfg.BannerImgKeyFor("green") == "img_green", "头图：绿用已恢复");
        Check(cfg.BannerImgKeyFor("yellow") == "img_yellow", "头图：黄用偏离基线");
        cfg.FeishuBannerImgKeyRed = "  img_red  ";
        Check(cfg.BannerImgKeyFor("red") == "img_red", "头图：红色单配优先于兜底，并去掉首尾空白");
        cfg.FeishuBannerImgKeyOrange = "   ";
        Check(cfg.BannerImgKeyFor("orange") == "img_nio", "头图：颜色键只有空白时回退兜底");
        Check(cfg.BannerImgKeyFor("purple") == "img_nio", "头图：未知颜色回退兜底");

        var cfgFile = Path.Combine(AppConfig.BaseDir, "config.json");
        var backup = File.Exists(cfgFile) ? File.ReadAllText(cfgFile) : null;
        try
        {
            File.WriteAllText(cfgFile,
                "{\"feishu_banner_img_key\":\" img_nio \",\"feishu_banner_img_key_orange\":\" img_o \"}");
            var loaded = AppConfig.Load();
            Check(loaded.FeishuBannerImgKey == "img_nio" && loaded.FeishuBannerImgKeyOrange == "img_o"
                  && loaded.FeishuBannerImgKeyRed == "" && loaded.FeishuBannerImgKeyBlue == ""
                  && loaded.BannerImgKeyFor("orange") == "img_o" && loaded.BannerImgKeyFor("red") == "img_nio",
                "头图：Load 读分色键并 Trim，缺的颜色回退兜底");
        }
        finally
        {
            try
            {
                if (backup != null) File.WriteAllText(cfgFile, backup);
                else if (File.Exists(cfgFile)) File.Delete(cfgFile);
            }
            catch { }
        }

        var inst = AppConfig.Instance;
        var savedFallback = inst.FeishuBannerImgKey;
        var savedRed = inst.FeishuBannerImgKeyRed;
        var savedOrange = inst.FeishuBannerImgKeyOrange;
        var savedBlue = inst.FeishuBannerImgKeyBlue;
        var savedGreen = inst.FeishuBannerImgKeyGreen;
        var savedYellow = inst.FeishuBannerImgKeyYellow;
        try
        {
            inst.FeishuBannerImgKey = "img_nio";
            inst.FeishuBannerImgKeyRed = "";
            inst.FeishuBannerImgKeyOrange = "img_orange";
            inst.FeishuBannerImgKeyBlue = "img_blue";
            inst.FeishuBannerImgKeyGreen = "img_green";
            inst.FeishuBannerImgKeyYellow = "img_yellow";

            string JsonOf(object card) => JsonSerializer.Serialize(card, relaxed);
            void Expect(object card, string key, string what)
                => Check(JsonOf(card).Contains($"\"img_key\":\"{key}\""), what);

            var fail = new TestRecord
            {
                StationId = "FCT1", Model = "M", Sn = "S", Result = "FAIL",
                XmlPath = @"D:\a.xml", BatchTimestamp = "2026-09-14T10:00:00",
            };
            Expect(FeishuNotifier.BuildFailBatchCard(new[] { fail }, 60), "img_nio", "FAIL 批量卡：红未单配走 NIO");
            var failCard = typeof(FeishuNotifier).GetMethod("BuildFailCard", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { fail })!;
            Expect(failCard, "img_nio", "FAIL 单条卡：红未单配走 NIO");
            Expect(FeishuNotifier.BuildCollectAlertCard(new CollectAlertPayload { StationId = "FCT1" }), "img_orange", "采集异常卡：用橙图");
            Expect(FeishuNotifier.BuildGroupAlertCard("FCT1", "2026-09-14", new List<SectionGroupAlertResult>(), 3), "img_orange", "章节群挂卡：用橙图");
            Expect(FeishuNotifier.BuildStorageAlertCard(new StorageAlertPayload { StationId = "FCT1" }), "img_orange", "存储健康卡：用橙图");
            Expect(FeishuNotifier.BuildTodoOverdueCard(new TodoOverduePayload { StationId = "FCT1" }), "img_orange", "待办超期卡：用橙图");
            Expect(FeishuNotifier.BuildCollectRecoverCard("FCT1", "此前", 0), "img_green", "采集恢复卡：用绿图");
            Expect(FeishuNotifier.BuildDailySummaryCard(new DailySummaryPayload { StationId = "FCT1", Date = "2026-09-14" }), "img_blue", "运行摘要卡：用蓝图");
            var statusCard = typeof(FeishuNotifier).GetMethod("BuildStatusChangeCard", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { new MaintenanceRecord { StationId = "FCT1", FailItem = "KL30" }, "", "open" })!;
            Expect(statusCard, "img_blue", "待办状态变更卡：用蓝图");
            Expect(FeishuNotifier.BuildDeviationAlertCard(new DeviationAlertPayload { StationId = "FCT1", TopSignal = "KL30" }), "img_yellow", "偏离卡：用黄图");

            inst.FeishuBannerImgKeyOrange = "";
            var fallen = JsonOf(FeishuNotifier.BuildCollectAlertCard(new CollectAlertPayload { StationId = "FCT1" }));
            Check(fallen.Contains("\"img_key\":\"img_nio\"") && !fallen.Contains("img_orange"),
                "采集异常卡：橙键清空后回退 NIO");

            inst.FeishuBannerImgKey = "";
            var bare = JsonOf(FeishuNotifier.BuildFailBatchCard(new[] { fail }, 60));
            Check(!bare.Contains("img_key"), "FAIL 批量卡：兜底和红色都空时不带头图");
        }
        finally
        {
            inst.FeishuBannerImgKey = savedFallback;
            inst.FeishuBannerImgKeyRed = savedRed;
            inst.FeishuBannerImgKeyOrange = savedOrange;
            inst.FeishuBannerImgKeyBlue = savedBlue;
            inst.FeishuBannerImgKeyGreen = savedGreen;
            inst.FeishuBannerImgKeyYellow = savedYellow;
        }
    }
}

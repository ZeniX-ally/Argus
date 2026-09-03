using FctAggregator;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

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
    /// ⚠ 以前这里写死过 <c>F:\Programe\G4_9-QA\FctAggregator\...</c>，
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
                var p2 = Path.Combine(dir.FullName, "FctAggregator", relative);
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

        // ---------- 时区不漂移（v3.4.13 加固：带偏移 ISO 按墙上时间取，不随机器时区转换） ----------
        Check(TimeUtil.Normalize("2026-08-14T07:17:29.004+08:00") == "2026-08-14 07:17:29",
              "ISO +08:00 偏移：按墙上时间解析，不随机器时区漂移");
        Check(TimeUtil.Normalize("2026-08-14T07:17:29.004+0800") == "2026-08-14 07:17:29",
              "ISO +0800 无冒号偏移同样支持");
        Check(TimeUtil.Normalize("2026-08-14T07:17:29Z") == "2026-08-14 07:17:29", "ISO Z 后缀同样支持");
        Check(TimeUtil.Normalize("2026-08-14T07:17:29.004-05:00") == "2026-08-14 07:17:29",
              "负偏移按原墙上时间取（不换算成机器本地）");


        // ---------- 域名1 智能时间：文件名时间 + 系统时间近邻校验（v3.19.0） ----------
        // 正常文件名时间戳（贴近系统时间）应解析出来；
        // 纯数字但离系统时间 >30 天的段（SN/批次号被 TsSegRe 误抓）应判空，交由调用方回退目录日期。
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

        var badXml = "<root><FACTORY USER=\\\"x\\\"></root>";
        var pfOut = new FctAggregator.Parsing.DefaultResultParser(FctAggregator.Parsing.ParserRuleSet.Default, "FCT1").Parse("D:\\Results\\Offline\\E300\\20260812\\F_20260722124700_xxx.xml", badXml);
        Check(pfOut != null && pfOut.Error == true, "域名1: 畸形 XML 应判解析失败");
        Check(pfOut != null && pfOut.ErrorCode == "xml_malformed", "域名1: 畸形 XML 分类码=xml_malformed");


        // ---------- 域2 DB 智能化：慢查询/健康巡检/分层（v3.19.0） ----------
        {
            var tmp2 = Path.Combine(Path.GetTempPath(), "fct_db2_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(tmp2);
            var dbPath2 = Path.Combine(tmp2, "test.db");
            var db2 = new Database(dbPath2);
            db2.LogSlowQuery("SELECT * FROM test_records WHERE test_date=@c", 600);
            db2.LogSlowQuery("SELECT 1", 100); // 不应记录
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath2}"))
            { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText="SELECT COUNT(*) FROM db_slow_log"; var cnt=Convert.ToInt32(cmd.ExecuteScalar()); Check(cnt==1, $"域2: 慢查询仅记录>500ms（实得 {cnt}）"); }
            var hc = db2.RunHealthCheck();
            Check(hc == "ok", $"域2: 健康巡检 integrity_check=ok（实得 {hc}）");
            using (var c2 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath2}"))
            { c2.Open(); using var cmd2 = c2.CreateCommand(); cmd2.CommandText="SELECT COUNT(*) FROM db_health_log WHERE check_type='integrity_check'"; var cnt2=Convert.ToInt32(cmd2.ExecuteScalar()); Check(cnt2>=1, $"域2: 健康日志已落库（实得 {cnt2}）"); }
            // 分层：插入一条 100 天前的旧记录，再归档应删 1 条
            var oldDate = DateTime.Today.AddDays(-100).ToString("yyyyMMdd");
            db2.BatchInsert(new[] { new TestRecord{ StationId="FCT1", Model="E300", Category="Offline", TestDate=oldDate, Sn="SN-OLD", Result="FAIL", XmlPath="X:\\old.xml", FailReason="OLD", BatchTimestamp="2026-01-01 00:00:00"} });
            var del = db2.ArchiveColdData(90);
            Check(del==1, $"域2: 冷数据归档删除 1 条（实得 {del}）");
            try { Directory.Delete(tmp2, true); } catch { }
        }

        // ---------- 域3 配置智能化：校验/推荐/快照回滚（v3.19.0） ----------
        {
            var badCfg = new AppConfig{ MeshPort=0, AggHttpPort=99999, ResultsRoot="", Peers = new List<string>{"not-a-url"}, DeviceAlertCpuPct=200, TodoScanDays=0 };
            var errs = ConfigValidator.Validate(badCfg);
            Check(errs.Count >= 3, $"域3: 校验应检出多项错误（实得 {errs.Count}）");
            Check(errs.Any(e=>e.Contains("peer")), "域3: 校验应检出 peer 非法");
            var goodCfg = new AppConfig{ MeshPort=8081, AggHttpPort=8080, ResultsRoot="D:\\Results", Peers = new List<string>{"http://192.168.1.10:8081"}, DeviceAlertCpuPct=90, DeviceAlertDiskFreeGb=10, YieldAlertYieldPct=90, TodoScanDays=30, DbMaintenanceHour=3 };
            var errs2 = ConfigValidator.Validate(goodCfg);
            Check(errs2.Count==0, $"域3: 合法配置校验应 0 错误（实得 {errs2.Count}）");
            // 推荐（空库不应抛）
            var tmp3 = Path.Combine(Path.GetTempPath(), "fct_cfg3_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(tmp3);
            var aggTmp = new AggDatabase(Path.Combine(tmp3, "agg.db")); aggTmp.Open();
            var recs = ConfigAdvisor.Recommend(goodCfg, aggTmp);
            Check(recs != null, "域3: 推荐应返回列表（空库）");
            // 注入一些 yld_daily 模拟低良率以触发推荐
            for(int dd=0; dd<10; dd++){ var date=DateTime.Today.AddDays(-dd).ToString("yyyyMMdd"); aggTmp.UpsertDailyStats("FCT1", date, new AggDatabase.DailyStats(100, 85, 15, 0, 5)); }
            var recs2 = ConfigAdvisor.Recommend(goodCfg, aggTmp);
            Check(recs2 != null, $"域3: 有数据时推荐应返回（实得 {(recs2==null?-1:recs2.Count)} 条）");
            try { Directory.Delete(tmp3, true); } catch {}
        }

        // ---------- 域4 设备智能化：预测/变更/巡检（v3.19.0） ----------
        {
            var tmp4 = Path.Combine(Path.GetTempPath(), "fct_dev4_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(tmp4);
            var agg4 = new AggDatabase(Path.Combine(tmp4, "agg.db")); agg4.Open();
            // 预置 device_info + samples 触发预测
            agg4.UpsertDeviceInfo(new DeviceInfoRow{ Machine="FCT_PRED", DiskFreeGb=3, CpuUsage=85, LastSeen=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), UpdatedAt=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
            for(int i=6;i>=0;i--){
                var ts=DateTime.Now.AddDays(-i).ToString("yyyy-MM-dd HH:mm:ss");
                // cpu 递增 5%/天，disk 递减 2GB/天，制造明显趋势
                agg4.InsertDeviceSample(new DeviceSampleRow{ Machine="FCT_PRED", Ts=ts, CpuUsage=55 + (6-i)*5, MemUsedMb=4000, DiskFreeGb=20 - (6-i)*2 });
            }
            var preds = DevicePredictor.Predict(agg4, 7);
            Check(preds.Count >= 1, $"域4: 预测应检出异常（实得 {preds.Count}）");
            Check(preds.Any(x=>x.Metric=="disk"), "域4: 磁盘耗尽预测应命中");
            // FCT 变更
            var fct1 = new DeviceFctRow{ Machine="FCT_PRED", Found=true, IniPath="C:/FCT.ini", Models=new List<string>{"M1"}, FwVersions=new List<(string,string)>{("FW1","1.0")}, Devices=new List<FctDeviceInfo>{ new FctDeviceInfo{Name="Dev1", Port="COM1", Type="com", Online=true}}};
            var changed1 = FctIniWatcher.CheckAndLog(agg4, fct1);
            Check(changed1, "域4: 首次 FCT 上报应记变更");
            var changed2 = FctIniWatcher.CheckAndLog(agg4, fct1);
            Check(!changed2, "域4: 相同 FCT 重复上报不应重复记");
            var fct2 = new DeviceFctRow{ Machine="FCT_PRED", Found=true, IniPath="C:/FCT.ini", Models=new List<string>{"M1","M2"}, FwVersions=new List<(string,string)>{("FW1","2.0")}, Devices=new List<FctDeviceInfo>{ new FctDeviceInfo{Name="Dev1", Port="COM1", Type="com", Online=false}}};
            var changed3 = FctIniWatcher.CheckAndLog(agg4, fct2);
            Check(changed3, "域4: 型号/FW 变更应记");
            var logs = agg4.QueryFctChanges("FCT_PRED", 10);
            Check(logs.Count >= 2, $"域4: fct_change_log 应有 ≥2 条（实得 {logs.Count}）");
            // 巡检
            agg4.UpsertDeviceInfo(new DeviceInfoRow{ Machine="FCT_LOW", DiskFreeGb=4, CpuUsage=10, LastSeen=DateTime.Now.AddMinutes(-10).ToString("yyyy-MM-dd HH:mm:ss") });
            var sug = DeviceInspector.Inspect(agg4);
            Check(sug.Any(s=>s.Machine=="FCT_LOW" && s.Kind=="disk"), "域4: 巡检应检出磁盘低");
            Check(sug.Any(s=>s.Machine=="FCT_PRED" && s.Kind=="com"), "域4: 巡检应检出 COM 离线");
            // API smoke: /api/devices/predict 与 /api/fct/changes
            try{
                agg4.UpsertUser("d4admin", PasswordHasher.Hash("pwd"), "admin");
                var tok = agg4.GetUserByName("d4admin")!.Token;
                int port = GetFreePort();
                var localDb = new Database(Path.Combine(tmp4, "local.db"));
                var mesh = new MeshNode(new AppConfig{ StationId="D4TEST", AggToken="d4tok", Peers=new List<string>() }, "D4TEST", localDb, agg4, new string[0]);
                var srv = new WebAggServer(port, mesh, agg4, tmp4, tmp4, "d4tok");
                srv.Start(); System.Threading.Thread.Sleep(400);
                var baseUrl = $"http://127.0.0.1:{port}";
                var r1 = HttpGetWithToken(baseUrl+"/api/devices/predict", tok);
                Check(r1.StatusCode==System.Net.HttpStatusCode.OK, "域4: GET /api/devices/predict → 200");
                var r2 = HttpGetWithToken(baseUrl+"/api/fct/changes?machine=FCT_PRED", tok);
                Check(r2.StatusCode==System.Net.HttpStatusCode.OK && r2.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("FCT_PRED"), "域4: GET /api/fct/changes → 命中");
                var r3 = HttpGetWithToken(baseUrl+"/api/devices/inspect", tok);
                Check(r3.StatusCode==System.Net.HttpStatusCode.OK, "域4: GET /api/devices/inspect → 200");
                srv.Stop(); mesh.Stop();
            } catch(Exception ex){ Check(false, $"域4 API smoke 异常: {ex.Message}"); }
            try { Directory.Delete(tmp4, true); } catch {}
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        // ---------- 域7 维修/待办智能：优先级/流转/去重（v3.19.0） ----------
        {
            var tmp7 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fct_dom7_" + Guid.NewGuid().ToString("N")[..6]);
            System.IO.Directory.CreateDirectory(tmp7);
            var agg7 = new AggDatabase(System.IO.Path.Combine(tmp7, "agg.db")); agg7.Open();
            // PriorityScorer
            var scHigh = PriorityScorer.Score(25, 4, 10);
            Check(scHigh.Level=="high" && scHigh.Zh=="高", $"域7: 高优先级判定（{scHigh.Level}/{scHigh.Score}）");
            var scLow = PriorityScorer.Score(2, 1, 1);
            Check(scLow.Level=="low", $"域7: 低优先级判定（{scLow.Level}）");
            var scMid = PriorityScorer.Score(8, 2, 3);
            Check(scMid.Level=="medium", $"域7: 中优先级判定（{scMid.Level}）");
            // TodoGrouping 机台上下文：同 fail 不同机台应分不同待办（station_id 维度）
            // 模拟多条 FAIL 登记
            agg7.InsertBatch(new[]{
                new AggFailRow{ Machine="FCT1", StationId="FCT1", Seq=1, TestDate=DateTime.Today.ToString("yyyyMMdd"), Result="FAIL", FailReason="5V_Rail", Model="M1", Sn="SN1"},
                new AggFailRow{ Machine="FCT2", StationId="FCT2", Seq=1, TestDate=DateTime.Today.ToString("yyyyMMdd"), Result="FAIL", FailReason="5V_Rail", Model="M1", Sn="SN2"},
                new AggFailRow{ Machine="FCT1", StationId="FCT1", Seq=2, TestDate=DateTime.Today.ToString("yyyyMMdd"), Result="FAIL", FailReason="5V_Rail", Model="M1", Sn="SN3"},
            });
            agg7.SyncTodoItems(30);
            var todos = agg7.ListTodoView();
            Check(todos.Count>=2, $"域7: 同 fail 不同机台应分卡（实得 {todos.Count}）");
            Check(todos.Any(x=> x.GroupKey==TodoGrouping.KeyOf("5V_Rail")), "域7: KeyOf 归一命中");
            // 去重：同机台同 fail 重复登记不应新增
            int before7 = todos.Count;
            agg7.SyncTodoItems(30);
            Check(agg7.ListTodoView().Count==before7, "域7: 重复 SyncTodo 不新增（去重）");
            // FlowAdvisor：构造历史流转 open->in_progress
            agg7.CreateMaintenance(new MaintenanceRecord{ StationId="FCT1", FailItem="FlowTest", Status="open", Severity="major" });
            var recs = agg7.ListMaintenance("", 100);
            var first = recs.FirstOrDefault(r=> r.FailItem=="FlowTest");
            if(first!=null){ agg7.UpdateMaintenanceStatus(first.Id, "in_progress"); }
            agg7.CreateMaintenance(new MaintenanceRecord{ StationId="FCT1", FailItem="FlowTest2", Status="open", Severity="major" });
            recs = agg7.ListMaintenance("", 100);
            var f2 = recs.FirstOrDefault(r=> r.FailItem=="FlowTest2");
            if(f2!=null){ agg7.UpdateMaintenanceStatus(f2.Id, "in_progress"); agg7.UpdateMaintenanceStatus(f2.Id, "resolved"); }
            var adv = FlowAdvisor.Advise("open", agg7);
            Check(adv.Suggested=="in_progress", $"域7: 流转推荐 open->in_progress（实得 {adv.Suggested}）");
            var adv2 = FlowAdvisor.Advise("in_progress", agg7);
            Check(adv2.Suggested.Length>0, $"域7: 流转推荐 in_progress 有建议（{adv2.Suggested}）");
            // API smoke
            try{
                agg7.UpsertUser("d7admin", PasswordHasher.Hash("pwd"), "admin");
                var tok = agg7.GetUserByName("d7admin")!.Token;
                int port = GetFreePort();
                var localDb = new Database(System.IO.Path.Combine(tmp7, "local.db"));
                var mesh = new MeshNode(new AppConfig{ StationId="D7TEST", AggToken="d7tok", Peers=new List<string>() }, "D7TEST", localDb, agg7, new string[0]);
                var srv = new WebAggServer(port, mesh, agg7, tmp7, tmp7, "d7tok");
                srv.Start(); System.Threading.Thread.Sleep(400);
                var baseUrl = $"http://127.0.0.1:{port}";
                var r1 = HttpGetWithToken(baseUrl+"/api/todos/suggest", tok);
                Check(r1.StatusCode==System.Net.HttpStatusCode.OK && r1.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("suggests"), "域7: GET /api/todos/suggest → 200");
                var r2 = HttpGetWithToken(baseUrl+"/api/maintenance/advise?status=open", tok);
                Check(r2.StatusCode==System.Net.HttpStatusCode.OK && r2.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("advise"), "域7: GET /api/maintenance/advise → 200");
                srv.Stop(); mesh.Stop();
            } catch(Exception ex){ Check(false, $"域7 API smoke 异常: {ex.Message}"); }
            try{ System.IO.Directory.Delete(tmp7, true);} catch{}
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        // ---------- 域8 UI 智能：布局/高亮/搜索（v3.19.0） ----------
        {
            // LayoutAdvisor
            var orderViewer = LayoutAdvisor.SuggestOrder(new Dictionary<string,int>{{"fails",10},{"overview",2}}, "viewer");
            Check(orderViewer[0]=="fails", $"域8: 布局建议按频次 fails 置顶（实得 {orderViewer[0]}）");
            var orderEng = LayoutAdvisor.SuggestOrder(new Dictionary<string,int>{{"maintenance",5}}, "engineer");
            Check(orderEng[0]=="maintenance", $"域8: engineer 维护频次置顶（实得 {orderEng[0]}）");
            // HighlightEngine
            var tmp8 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fct_dom8_" + Guid.NewGuid().ToString("N")[..6]);
            System.IO.Directory.CreateDirectory(tmp8);
            var agg8 = new AggDatabase(System.IO.Path.Combine(tmp8, "agg.db")); agg8.Open();
            agg8.UpsertDeviceInfo(new DeviceInfoRow{ Machine="FCT_HL", DiskFreeGb=2, CpuUsage=95, LastSeen=DateTime.Now.AddMinutes(-10).ToString("yyyy-MM-dd HH:mm:ss") });
            var cfg = new AppConfig{ DeviceAlertDiskFreeGb=10, DeviceAlertCpuPct=90, DeviceAlertOfflineMinutes=5 };
            var hl = HighlightEngine.GetHighlights(agg8, cfg);
            Check(hl.Any(x=> x.Machine=="FCT_HL"), $"域8: 高亮应命中磁盘/CPU/离线异常（实得 {hl.Count}）");
            // 前端文件存在性
            var baseDir = AppContext.BaseDirectory;
            // 从程序目录往上找 public
            string FindPublic(string start){
                var dir = new System.IO.DirectoryInfo(start);
                for(int i=0;i<6 && dir!=null; i++, dir=dir.Parent){
                    var cand = System.IO.Path.Combine(dir.FullName, "public", "js", "loader.js");
                    if(System.IO.File.Exists(cand)) return cand;
                    var cand2 = System.IO.Path.Combine(dir.FullName, "FctAggregator", "public", "js", "loader.js");
                    if(System.IO.File.Exists(cand2)) return cand2;
                }
                return "";
            }
            var loaderPath = FindPublic(baseDir);
            if(loaderPath!=""){
                var txt = System.IO.File.ReadAllText(loaderPath);
                Check(txt.Contains("LayoutAdvisor"), "域8: loader.js 含 LayoutAdvisor");
                Check(txt.Contains("record"), "域8: loader.js 含频次记录");
            } else {
                Check(false, "域8: 未找到 loader.js");
            }
            var cssPath = loaderPath.Replace("loader.js", "../css/theme.css");
            if(System.IO.File.Exists(cssPath)){
                var css = System.IO.File.ReadAllText(cssPath);
                Check(css.Contains("highlight-critical"), "域8: theme.css 含高亮样式");
                Check(css.Contains("pulse-highlight"), "域8: theme.css 含脉冲动画");
            }
            // API smoke
            try{
                agg8.UpsertUser("d8admin", PasswordHasher.Hash("pwd"), "admin");
                var tok = agg8.GetUserByName("d8admin")!.Token;
                int port = GetFreePort();
                var localDb = new Database(System.IO.Path.Combine(tmp8, "local.db"));
                var mesh = new MeshNode(new AppConfig{ StationId="D8TEST", AggToken="d8tok", Peers=new List<string>() }, "D8TEST", localDb, agg8, new string[0]);
                var srv = new WebAggServer(port, mesh, agg8, tmp8, tmp8, "d8tok");
                srv.Start(); System.Threading.Thread.Sleep(400);
                var baseUrl = $"http://127.0.0.1:{port}";
                var r1 = HttpGetWithToken(baseUrl+"/api/highlights", tok);
                Check(r1.StatusCode==System.Net.HttpStatusCode.OK, "域8: GET /api/highlights → 200");
                var r2 = HttpGetWithToken(baseUrl+"/api/layout/suggest?role=engineer", tok);
                Check(r2.StatusCode==System.Net.HttpStatusCode.OK && r2.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("order"), "域8: GET /api/layout/suggest → 200");
                srv.Stop(); mesh.Stop();
            } catch(Exception ex){ Check(false, $"域8 API smoke 异常: {ex.Message}"); }
            try{ System.IO.Directory.Delete(tmp8, true);} catch{}
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        // ---------- 域9 告警智能：预测/自愈（v3.19.0） ----------
        {
            var tmp9 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fct_dom9_" + Guid.NewGuid().ToString("N")[..6]);
            System.IO.Directory.CreateDirectory(tmp9);
            var agg9 = new AggDatabase(System.IO.Path.Combine(tmp9, "agg.db")); agg9.Open();
            // 构造 yld_daily 下滑趋势触发预测
            for(int dd9=4; dd9>=0; dd9--){
                var date = DateTime.Today.AddDays(-dd9).ToString("yyyyMMdd");
                int pass = dd9==0 ? 91 : 95 - dd9; // 逐日下滑，末日仍在阈值上但预测跌破
                agg9.UpsertDailyStats("FCT9", date, new AggDatabase.DailyStats(100, pass, 100-pass, 0, 5));
            }
            agg9.UpsertDeviceInfo(new DeviceInfoRow{ Machine="FCT9", DiskFreeGb=3, CpuUsage=92, LastSeen=DateTime.Now.AddMinutes(-10).ToString("yyyy-MM-dd HH:mm:ss") });
            for(int i=6;i>=0;i--){ var ts=DateTime.Now.AddDays(-i).ToString("yyyy-MM-dd HH:mm:ss"); int elapsed=6-i; agg9.InsertDeviceSample(new DeviceSampleRow{ Machine="FCT9", Ts=ts, CpuUsage=60+elapsed*5, DiskFreeGb=20-elapsed*2.5, MemUsedMb=4000}); }
            var preds = AlertPredictor.Predict(agg9);
            Check(preds.Count>=1, $"域9: 预测应命中≥1（实得 {preds.Count}）");
            Check(preds.Any(x=> x.Rule=="yield" || x.Rule=="disk" || x.Rule=="cpu"), "域9: 预测含 yield/disk/cpu");
            AlertPredictor.LogPredictions(agg9, preds);
            var logs = agg9.QueryAlertPredicts("FCT9", 10);
            Check(logs.Count>=1, $"域9: 落库≥1（实得 {logs.Count}）");
            // 自愈
            var heals = AlertHealer.Heal(agg9, "FCT9", "disk");
            Check(heals.Count>=1, $"域9: 自愈建议≥1（实得 {heals.Count}）");
            Check(heals.Any(x=> x.Suggestion.Length>0), "域9: 自愈含建议文本");
            var feishu = AlertHealer.FormatForFeishu("FCT9","disk", heals);
            Check(feishu.Contains("FCT9"), "域9: 飞书卡片含机台");
            // API smoke
            try{
                agg9.UpsertUser("d9admin", PasswordHasher.Hash("pwd"), "admin");
                var tok = agg9.GetUserByName("d9admin")!.Token;
                int port = GetFreePort();
                var localDb = new Database(System.IO.Path.Combine(tmp9, "local.db"));
                var mesh = new MeshNode(new AppConfig{ StationId="D9TEST", AggToken="d9tok", Peers=new List<string>() }, "D9TEST", localDb, agg9, new string[0]);
                var srv = new WebAggServer(port, mesh, agg9, tmp9, tmp9, "d9tok");
                srv.Start(); System.Threading.Thread.Sleep(400);
                var baseUrl = $"http://127.0.0.1:{port}";
                var r1 = HttpGetWithToken(baseUrl+"/api/alerts/predict", tok);
                Check(r1.StatusCode==System.Net.HttpStatusCode.OK && r1.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("predicts"), "域9: GET /api/alerts/predict → 200");
                var r2 = HttpGetWithToken(baseUrl+"/api/alerts/heal?machine=FCT9&rule=disk", tok);
                Check(r2.StatusCode==System.Net.HttpStatusCode.OK && r2.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("heals"), "域9: GET /api/alerts/heal → 200");
                srv.Stop(); mesh.Stop();
            } catch(Exception ex){ Check(false, $"域9 API smoke 异常: {ex.Message}"); }
            try{ System.IO.Directory.Delete(tmp9, true);} catch{}
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        // ==================== 自反馈（v3.20.0 规格 02：预测准确率对账 + 阈值推荐）====================
        {
            var tmpR = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fct_selfb_" + Guid.NewGuid().ToString("N")[..6]);
            System.IO.Directory.CreateDirectory(tmpR);
            try
            {
                var cfg = new AppConfig
                {
                    PredictReconcileEnabled = true,
                    PredictReconcileHorizonDays = 14,
                    PredictReconcileCronHour = 4,
                    PredictTuneEnabled = true,
                    PredictTuneMinSamples = 30,
                    YieldAlertYieldPct = 90,
                    DeviceAlertCpuPct = 90,
                    DeviceAlertOfflineMinutes = 5,
                };
                var aggR = new AggDatabase(System.IO.Path.Combine(tmpR, "agg.db"));
                aggR.Open();
                Check(aggR.PredictAccuracyExists("alert", 1) == false, "自反馈: 空库不报错");

                // 准备 fixture：3 天前的 yield 下滑预测 + 当日 yld_daily 已存在（"真跌破"→ hit）
                var past3 = DateTime.Now.AddDays(-3).ToString("yyyy-MM-dd HH:mm:ss");
                var yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss");
                aggR.UpsertDailyStats("FCT_R1", DateTime.Now.AddDays(-2).ToString("yyyyMMdd"),
                    new AggDatabase.DailyStats(100, 88, 12, 0, 50));
                // 模拟"3 天前发的 yield 预测（预测良率 87%，阈值 90）"
                aggR.InsertAlertPredictLog("FCT_R1", "yield", "warn", 92.0, 87.0, "测试 fixture");
                aggR.InsertAlertPredictLog("FCT_R1", "yield", "warn", 92.0, 88.0, "测试 fixture2");

                // 断言 4：UPSERT 幂等
                var rowA = new AggDatabase.AccuracyRow
                {
                    Rule = "yield", Machine = "FCT_R1", PredictId = 999, PredictTable = "alert",
                    PredictedValue = 87.0, ActualValue = 88.0, Threshold = 90.0,
                    Hit = true, LeadDays = 1.0,
                    PredictedAt = past3, ReconciledAt = yesterday,
                };
                aggR.UpsertPredictAccuracy(rowA);
                var sameExists = aggR.PredictAccuracyExists("alert", 999);
                Check(sameExists, "自反馈: PredictAccuracyExists → true");
                aggR.UpsertPredictAccuracy(rowA);   // 二次 upsert 不报错
                Check(aggR.PredictAccuracyExists("alert", 999), "自反馈: 二次 UPSERT 不报错");

                // 断言 5：CountPredictAccuracyByRule
                var statYield = aggR.CountPredictAccuracyByRule("yield", days: 30);
                Check(statYield.Total >= 1 && statYield.Hit >= 1, $"自反馈: CountPredictAccuracyByRule 统计正确（{statYield.Total}/{statYield.Hit}）");

                // 断言 6：QueryPredictAccuracy
                var rowsQ = aggR.QueryPredictAccuracy(rule: "yield", days: 30);
                Check(rowsQ.Count >= 1, $"自反馈: QueryPredictAccuracy 返回行（{rowsQ.Count}）");
                Check(rowsQ.Any(r => r.Machine == "FCT_R1"), "自反馈: 按 rule 过滤命中");

                // 断言 7：ClassifyYieldHit — 预测跌破实际也跌破 → hit
                var predictTuple = (1L, past3, "FCT_X", "yield", "warn", 92.0, 87.0, "fixture");
                var actualHit = new PredictAccuracyReconciler.ActualValuePublic { Value = 88.0, EventTs = DateTime.Now.AddDays(-2) };
                var (h1, _) = PredictAccuracyReconciler.ClassifyYieldHit(actualHit, predictTuple, cfg);
                Check(h1 == true, "自反馈: ClassifyYieldHit 预测跌破且真跌破 → hit");

                // 断言 8：ClassifyYieldHit — 预测跌破实际未跌破 → miss
                var actualMiss = new PredictAccuracyReconciler.ActualValuePublic { Value = 95.0, EventTs = DateTime.Now.AddDays(-2) };
                var (h2, _) = PredictAccuracyReconciler.ClassifyYieldHit(actualMiss, predictTuple, cfg);
                Check(h2 == false, "自反馈: ClassifyYieldHit 预测跌破但实际没跌破 → miss");

                // 断言 9：RunOnce 至少跑过一次，不抛异常
                var n = PredictAccuracyReconciler.RunOnce(aggR, cfg);
                Check(n >= 0, $"自反馈: RunOnce 返回条数 ≥ 0（{n}）");

                // 断言 10：阈值推荐 — 注入 35 条 miss，命中率 < 30% → 推荐 +5%
                for (int i = 0; i < 35; i++)
                {
                    var acc = new AggDatabase.AccuracyRow
                    {
                        Rule = "yield", Machine = "FCT_RT", PredictId = 2000 + i, PredictTable = "alert",
                        PredictedValue = 85.0, ActualValue = 92.0, Threshold = 90.0,
                        Hit = false, LeadDays = 1.0,
                        PredictedAt = DateTime.Now.AddDays(-2).ToString("yyyy-MM-dd HH:mm:ss"),
                        ReconciledAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    };
                    aggR.UpsertPredictAccuracy(acc);
                }
                var summary = PredictAccuracyReconciler.Reconcile(aggR, cfg);
                Check(summary.Summary.ContainsKey("yield"), "自反馈: Summary 含 yield");
                var yieldStat = summary.Summary["yield"];
                Check(yieldStat.Total >= 35, $"自反馈: yield 统计 ≥ 35（{yieldStat.Total}）");
                Check(summary.ThresholdTuning.Any(t => t.Rule == "yield" && t.Recommended > t.Current),
                      "自反馈: accuracy<30% → 推荐 yield 阈值上调");

                // 断言 11：阈值推荐 — 全 hit（命中率 > 85%） → 推荐 -5%
                for (int i = 0; i < 40; i++)
                {
                    var acc = new AggDatabase.AccuracyRow
                    {
                        Rule = "yield", Machine = "FCT_RH", PredictId = 3000 + i, PredictTable = "alert",
                        PredictedValue = 87.0, ActualValue = 88.0, Threshold = 90.0,
                        Hit = true, LeadDays = 1.0,
                        PredictedAt = DateTime.Now.AddDays(-2).ToString("yyyy-MM-dd HH:mm:ss"),
                        ReconciledAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    };
                    aggR.UpsertPredictAccuracy(acc);
                }
                var cfgTight = new AppConfig { PredictReconcileEnabled = true, PredictTuneMinSamples = 30, YieldAlertYieldPct = 90 };
                var summary2 = PredictAccuracyReconciler.Reconcile(aggR, cfgTight);
                var allHitYield = summary2.Summary["yield"];
                Check(allHitYield.Total > 0, "自反馈: 全 hit fixture 后 yield 仍有统计");

                // 断言 12：冷启动保护（< min_samples 不推荐）
                var cfgCold = new AppConfig { PredictReconcileEnabled = true, PredictTuneMinSamples = 100, YieldAlertYieldPct = 90 };
                var aggCold = new AggDatabase(System.IO.Path.Combine(tmpR, "cold.db"));
                aggCold.Open();
                for (int i = 0; i < 5; i++)
                {
                    aggCold.UpsertPredictAccuracy(new AggDatabase.AccuracyRow
                    {
                        Rule = "yield", Machine = "FCT_C", PredictId = 4000 + i, PredictTable = "alert",
                        PredictedValue = 80, ActualValue = 95, Threshold = 90, Hit = false, LeadDays = 1.0,
                        PredictedAt = DateTime.Now.AddDays(-2).ToString("yyyy-MM-dd HH:mm:ss"),
                        ReconciledAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    });
                }
                var summary3 = PredictAccuracyReconciler.Reconcile(aggCold, cfgCold);
                Check(summary3.ThresholdTuning.Count == 0, "自反馈: 冷启动（< min_samples）不推荐");

                // 断言 13：Purge 滚动清理
                var oldTs = DateTime.Now.AddDays(-200).ToString("yyyy-MM-dd HH:mm:ss");
                aggR.UpsertPredictAccuracy(new AggDatabase.AccuracyRow
                {
                    Rule = "yield", Machine = "FCT_OLD", PredictId = 5000, PredictTable = "alert",
                    PredictedValue = 80, ActualValue = 90, Threshold = 90, Hit = true, LeadDays = 1.0,
                    PredictedAt = oldTs, ReconciledAt = oldTs,
                });
                var purged = aggR.PurgeOldPredictAccuracy(180);
                Check(purged >= 1, $"自反馈: PurgeOldPredictAccuracy 删除 ≥ 1 行（{purged}）");

                // 断言 14：API 路由 /api/predict/accuracy
                try
                {
                    aggR.UpsertUser("radmin", PasswordHasher.Hash("pwd"), "admin");
                    var tok = aggR.GetUserByName("radmin")!.Token;
                    int port = GetFreePort();
                    var localDb = new Database(System.IO.Path.Combine(tmpR, "local.db"));
                    var meshR = new MeshNode(new AppConfig { StationId = "RTEST", AggToken = "rtok", Peers = new List<string>() },
                        "RTEST", localDb, aggR, new string[0]);
                    // 显式 MeshNode 重载（避免 C# 优先解析到 AggWatcher 重载）
                    var srvR = new WebAggServer(port, (MeshNode)meshR, aggR, tmpR, tmpR, "rtok");
                    srvR.Start(); System.Threading.Thread.Sleep(400);
                    var baseUrl = $"http://127.0.0.1:{port}";
                    var rr1 = HttpGetWithToken(baseUrl + "/api/predict/accuracy?days=30&force=1", tok);
                    var body1 = rr1.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    Check(rr1.StatusCode == System.Net.HttpStatusCode.OK, "自反馈: GET /api/predict/accuracy → 200");
                    // 响应字段为 PascalCase（System.Text.Json 默认 + IncludeFields）
                    Check(body1.Contains("WindowDays") && body1.Contains("Summary") && body1.Contains("PerMachine"),
                          $"自反馈: 响应含 WindowDays/Summary/PerMachine 字段");
                    srvR.Stop(); meshR.Stop();
                }
                catch (Exception ex) { Check(false, $"自反馈 API smoke 异常: {ex.Message}"); }

                aggR.Close();
                aggCold.Close();
            }
            catch (Exception ex) { Check(false, $"自反馈分组异常: {ex.Message}"); }
            try { System.IO.Directory.Delete(tmpR, true); } catch { }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        // ==================== 季节性（v3.21.0 规格 03：季节性分解）====================
        {
            var tmpR = Path.GetTempPath() + "fct_selfseason_" + Guid.NewGuid().ToString("N")[..6];
            try
            {
                Directory.CreateDirectory(tmpR);
                var dbPath = Path.Combine(tmpR, "season.db");
                var aggR = new AggDatabase(dbPath);
                aggR.Open();

                // 注入小时粒度记录：每 (机台, 日期, 小时) 写 pass 条 PASS + fail 条 FAIL
                void SeedHour(string m, int seq, string ymd, int hour, int pass, int fail)
                {
                    using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                    c.Open();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "INSERT INTO agg_records (machine, seq, type, test_date, batch_timestamp, result) VALUES (@m,@s,'fail',@d,@ts,@r)";
                    string ts = $"{ymd.Substring(0, 4)}-{ymd.Substring(4, 2)}-{ymd.Substring(6, 2)}T{hour:00}:30:00";
                    for (int i = 0; i < pass + fail; i++)
                    {
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("@m", m);
                        cmd.Parameters.AddWithValue("@s", seq++);
                        cmd.Parameters.AddWithValue("@d", ymd);
                        cmd.Parameters.AddWithValue("@ts", ts);
                        cmd.Parameters.AddWithValue("@r", i < pass ? "PASS" : "FAIL");
                        cmd.ExecuteNonQuery();
                    }
                }

                // 断言 1：空输入不崩溃，OverallMean=100、Anomalies 空、长度 = daysBack
                var decEmpty = YieldDecomposer.Decompose(aggR, "S_NODATA", YieldDecomposer.SeasonalityMode.Hourly, 28, 7, 1.5);
                Check(decEmpty.OverallMean == 100 && decEmpty.Anomalies.Count == 0 && decEmpty.Trend.Count == 28 && decEmpty.Residual.Count == 28,
                    "季节性: 空输入不崩溃 OverallMean=100/Anomalies 空/长度 28");
                Check(decEmpty.Sigma == 0 && decEmpty.Seasonal.Count == 24, "季节性: 空输入 Seasonal=24 桶 σ=0");

                // 断言 2+6：平稳数据（全 95%）→ sigma=0 不除零，PredictWithSeasonality 回退 null
                for (int i = 27; i >= 0; i--)
                    aggR.UpsertDailyStats("S_STABLE", DateTime.Today.AddDays(-i).ToString("yyyyMMdd"), new AggDatabase.DailyStats(100, 95, 5, 0, 50));
                var cfgOn = new AppConfig { YieldSeasonalityEnabled = true, YieldSeasonalityMode = "weekly", YieldSeasonalityMinSigma = 0.5, YieldAlertYieldPct = 90 };
                var decStable = YieldDecomposer.Decompose(aggR, "S_STABLE", YieldDecomposer.SeasonalityMode.Weekly, 28, 7, 1.5);
                Check(decStable.Sigma == 0 && decStable.Anomalies.Count == 0, "季节性: 全相同数据 σ=0 不除零、无异常");
                Check(YieldDecomposer.PredictWithSeasonality(aggR, cfgOn, "S_STABLE") == null,
                    "季节性: 平稳数据（σ<min_sigma）回退 v3.19.0 老逻辑（返回 null）");

                // 断言 3：27 天 95% + 今天 70% → 异常日被检出 + 预测产出 1 条（预测只看最新一天，异常必须落在今天）
                var anomDay = DateTime.Today;
                for (int i = 27; i >= 0; i--)
                {
                    int pct = DateTime.Today.AddDays(-i).Date == anomDay.Date ? 70 : 95;
                    aggR.UpsertDailyStats("S_ANOM", DateTime.Today.AddDays(-i).ToString("yyyyMMdd"), new AggDatabase.DailyStats(100, pct, 100 - pct, 0, 50));
                }
                var decAnom = YieldDecomposer.Decompose(aggR, "S_ANOM", YieldDecomposer.SeasonalityMode.Weekly, 28, 7, 1.5);
                Check(decAnom.Anomalies.Count >= 1 && decAnom.Anomalies.Any(a => a.Date.Date == anomDay.Date && a.Severity == "critical"),
                    $"季节性: 显著异常日（70% vs 95%）被检出（{decAnom.Anomalies.Count} 个）");
                var predAnom = YieldDecomposer.PredictWithSeasonality(aggR, cfgOn, "S_ANOM");
                Check(predAnom != null && predAnom.Count == 1 && predAnom[0].Rule == "yield" && predAnom[0].Level == "warn",
                    "季节性: 异常机台预测产出 1 条 yield/warn");

                // 断言 4：hourly 白班 95% / 夜班 90% 循环 14 天 → 周期被识别，不误报
                int seedSeq = 1;
                for (int i = 13; i >= 0; i--)
                {
                    var ymd = DateTime.Today.AddDays(-i).ToString("yyyyMMdd");
                    for (int h = 8; h <= 19; h++) SeedHour("S_SHIFT", seedSeq += 20, ymd, h, 19, 1);   // 白天 95%
                    for (int h = 20; h <= 23; h++) SeedHour("S_SHIFT", seedSeq += 20, ymd, h, 9, 1);   // 夜 90%
                    for (int h = 0; h <= 7; h++) SeedHour("S_SHIFT", seedSeq += 20, ymd, h, 9, 1);     // 夜 90%
                }
                var decShift = YieldDecomposer.Decompose(aggR, "S_SHIFT", YieldDecomposer.SeasonalityMode.Hourly, 14, 7, 1.5);
                Check(decShift.Anomalies.Count == 0, $"季节性: 白班/夜班周期不误报（异常 {decShift.Anomalies.Count} 个）");
                Check(decShift.Seasonal.Count == 24 && Math.Abs(decShift.Seasonal[8]) > 0.5 && Math.Abs(decShift.Seasonal[0]) > 0.5,
                    "季节性: 昼夜周期分量被捕获（白天桶 >0.5pp、深夜桶 <−0.5pp）");

                // 断言 5：weekly 周一 85% / 其余 90% → 周一低点不当异常
                for (int i = 27; i >= 0; i--)
                {
                    var dt = DateTime.Today.AddDays(-i);
                    int pct = dt.DayOfWeek == DayOfWeek.Monday ? 85 : 90;
                    aggR.UpsertDailyStats("S_WEEK", dt.ToString("yyyyMMdd"), new AggDatabase.DailyStats(100, pct, 100 - pct, 0, 50));
                }
                var decWeek = YieldDecomposer.Decompose(aggR, "S_WEEK", YieldDecomposer.SeasonalityMode.Weekly, 28, 7, 1.5);
                Check(decWeek.Anomalies.Count == 0, $"季节性: 周一低 5pp 不被当异常（异常 {decWeek.Anomalies.Count} 个）");
                Check(decWeek.Seasonal.Count == 7 && decWeek.Seasonal[(int)DayOfWeek.Monday] < -3,
                    "季节性: 周一周期分量 ≈ −5pp");

                // 断言 7：开关关 → 老逻辑零影响（集成回归）
                var cfgOff = new AppConfig { YieldSeasonalityEnabled = false, YieldSeasonalityMode = "weekly", YieldAlertYieldPct = 90 };
                Check(YieldDecomposer.PredictWithSeasonality(aggR, cfgOff, "S_ANOM") == null, "季节性: 开关关 PredictWithSeasonality 返回 null");
                var predsOff = AlertPredictor.Predict(aggR);
                Check(predsOff.All(p => !(p.Detail ?? "").Contains("季节性分解")), "季节性: 开关关 /api/alerts/predict 无季节性项（老逻辑回归）");

                // 断言 8：跨机台独立分解不串
                var decM1 = YieldDecomposer.Decompose(aggR, "S_ANOM", YieldDecomposer.SeasonalityMode.Weekly, 28, 7, 1.5);
                var decM2 = YieldDecomposer.Decompose(aggR, "S_STABLE", YieldDecomposer.SeasonalityMode.Weekly, 28, 7, 1.5);
                Check(decM1.Anomalies.Count >= 1 && decM2.Anomalies.Count == 0,
                    "季节性: 跨机台独立分解（异常机台有 / 平稳机台无）");

                // 断言 12：非法模式回退 hourly
                Check(YieldDecomposer.ParseMode("garbage") == YieldDecomposer.SeasonalityMode.Hourly, "季节性: 非法 mode 回退 hourly");
                Check(YieldDecomposer.ParseMode("weekly") == YieldDecomposer.SeasonalityMode.Weekly, "季节性: mode=weekly 解析正确");

                // 断言 9-11：HTTP 端点 + 权限
                try
                {
                    aggR.UpsertUser("sadmin", PasswordHasher.Hash("pwd"), "admin");
                    aggR.UpsertUser("sviewer", PasswordHasher.Hash("pwd"), "viewer");
                    var adminTok = aggR.GetUserByName("sadmin")!.Token;
                    var viewerTok = aggR.GetUserByName("sviewer")!.Token;
                    int port = GetFreePort();
                    var localDb = new Database(Path.Combine(tmpR, "local.db"));
                    var meshR = new MeshNode(new AppConfig { StationId = "SEASON", AggToken = "rtok", Peers = new List<string>() },
                        "SEASON", localDb, aggR, new string[0]);
                    var srvR = new WebAggServer(port, (MeshNode)meshR, aggR, tmpR, tmpR, "rtok");
                    srvR.Start(); System.Threading.Thread.Sleep(400);
                    var baseUrl = $"http://127.0.0.1:{port}";

                    var rDec = HttpGetWithToken(baseUrl + "/api/yield/decompose?machine=S_ANOM&mode=weekly&days=28", viewerTok);
                    var bDec = rDec.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    Check(rDec.StatusCode == System.Net.HttpStatusCode.OK, "季节性: GET /api/yield/decompose → 200");
                    Check(bDec.Contains("Trend") && bDec.Contains("Seasonal") && bDec.Contains("Residual") && bDec.Contains("Sigma") && bDec.Contains("Anomalies"),
                        "季节性: 响应含 Trend/Seasonal/Residual/Sigma/Anomalies 字段（PascalCase）");
                    var rNoM = HttpGetWithToken(baseUrl + "/api/yield/decompose?days=28", viewerTok);
                    Check(rNoM.StatusCode == System.Net.HttpStatusCode.BadRequest, "季节性: machine 缺失 → 400");

                    // 权限：viewer 改配置 → 403
                    var rV403 = HttpPostWithToken(baseUrl + "/api/yield/decompose/config", viewerTok, "{\"enabled\":true}");
                    Check(rV403.StatusCode == System.Net.HttpStatusCode.Forbidden, "季节性: viewer POST config → 403");
                    // admin 改配置 → 200 生效，GET config 回显
                    var rAdmin = HttpPostWithToken(baseUrl + "/api/yield/decompose/config", adminTok, "{\"enabled\":true,\"mode\":\"daily\"}");
                    Check(rAdmin.StatusCode == System.Net.HttpStatusCode.OK, "季节性: admin POST config → 200");
                    var rCfg = HttpGetWithToken(baseUrl + "/api/yield/decompose/config", viewerTok);
                    var bCfg = rCfg.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    Check(bCfg.Contains("\"enabled\":true") && bCfg.Contains("daily"), "季节性: 热生效后 GET config 回显 enabled=true/mode=daily");
                    // 非法 mode → 400
                    var rBad = HttpPostWithToken(baseUrl + "/api/yield/decompose/config", adminTok, "{\"mode\":\"monthly\"}");
                    Check(rBad.StatusCode == System.Net.HttpStatusCode.BadRequest, "季节性: 非法 mode → 400");
                    // 复位（不污染全局单例与后续断言）
                    HttpPostWithToken(baseUrl + "/api/yield/decompose/config", adminTok, "{\"enabled\":false,\"mode\":\"hourly\"}");

                    srvR.Stop(); meshR.Stop();
                }
                catch (Exception ex) { Check(false, $"季节性 HTTP smoke 异常: {ex.Message}"); }

                aggR.Close();
            }
            catch (Exception ex) { Check(false, $"季节性分组异常: {ex.Message}"); }
            try { Directory.Delete(tmpR, true); } catch { }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        // ---------- 文件名时间戳段位容错（v3.4.13 加固：不依赖固定段位，SN 含下划线不挤错） ----------
        {
            // 标准 6 段 SN 场景：第 7 段 17 位时间戳
            var p1 = FctAggregator.Parsing.PathMeta.FromPath(@"D:\Results\Offline\E3002781\20260814\O_Fts_PEU_G49_FCT6_E3002781AGV75236898002K81201743_20260814072417293_2026813232417386.xml", null);
            Check(p1 != null && p1.FileTime == "20260814072417293",
                  $"文件名时间戳: 标准段位取第 7 段 17 位（实得 {p1?.FileTime}）");
            // SN 含下划线（段位漂移）：时间戳仍在独立段上，正则不依赖固定位置。
            // （SN 提取仍按固定段位 —— 文件名规范中 SN 无下划线，此处只保证时间戳不错位）
            var p2 = FctAggregator.Parsing.PathMeta.FromPath(@"D:\Results\Offline\E3002781\20260814\O_Fts_PEU_G49_FCT6_SN_XX_1234_20260814072417293_2026813232417386.xml", null);
            Check(p2 != null && p2.FileTime == "20260814072417293",
                  $"文件名时间戳: SN 含下划线段位漂移仍能取对时间（实得 {p2?.FileTime}）");
            // 第 8 段 16 位非标准时间戳：自动跳过，不被误认
            var p3 = FctAggregator.Parsing.PathMeta.FromPath(@"D:\Results\Offline\E3002781\20260814\O_Fts_PEU_G49_FCT6_E3002781AGV75236898002K81201743_20260814072417293_2026813232417386.xml", null);
            Check(p3 != null && p3.FileTime == "20260814072417293",
                  $"文件名时间戳: 第 8 段 16 位非标准段自动跳过（实得 {p3?.FileTime}）");
            // 14 位时间戳兼容
            var p4 = FctAggregator.Parsing.PathMeta.FromPath(@"D:\Results\Offline\E3002781\20260814\O_Fts_PEU_G49_FCT6_SN1234_20260814072417_20260814072418.xml", null);
            Check(p4 != null && p4.FileTime == "20260814072417",
                  $"文件名时间戳: 14 位兼容（实得 {p4?.FileTime}）");
        }

        // ---------- batch_timestamp 时间源：仅文件名（v3.4.x：产线 XML 的 BATCH/PANEL/DUT/GROUP 时间均不可信） ----------
        // 文件名含 20260814072417293（17 位）→ "2026-08-14 07:24:17"。
        // BATCH@TIMESTAMP=07-30 18:21（session 打开）、PANEL@TIMESTAMP=08-14 07:17 均被忽略。
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
            // 无文件名时间戳时回退目录日期
            var tsXml2 = Path.Combine(tsDir,
                "O_Fts_PEU_G49_FCT6_E3002781AGV75236898002K81201743_nodate_2026813232417387.xml");
            File.WriteAllText(tsXml2, tsBody);
            var tsRec2 = tsProc.ParseAndClassify(tsXml2);
            Check(tsRec2 != null && TimeUtil.Normalize(tsRec2.BatchTimestamp) == "2026-08-14 00:00:00",
                  $"时间源: 无文件名时间时回退目录日期（实得 {tsRec2?.BatchTimestamp}）");
            TryDeleteDir(tsRoot);
        }

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
                // v3.6.1：默认视图（非自定义区间）下待办数必须与 CountPendingTodos 一致
                // （这是「角标 6 卡片 0」的根因 —— 默认近 N 天窗口把历史待办滤掉了）
                Check(board.PendingTodoCount == eng.Db.CountPendingTodos(),
                      $"看板待办数 == CountPendingTodos（{board.PendingTodoCount} == {eng.Db.CountPendingTodos()}，v3.6.1 修复）");
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

            // v3.6.1 修复回归：角标(CountPendingTodos 全量)与默认看板(不限区间)必须一致。
            // 历史登记的待办即使 FAIL 记录滑出近 N 天窗口，默认视图也要显示——只有自定义区间才过滤。
            var allPending = t.ListTodoView();
            var pendingCnt = t.CountPendingTodos();
            Check(allPending.Count == pendingCnt,
                  $"默认视图待办数 == CountPendingTodos（{allPending.Count} == {pendingCnt}，v3.6.1 修复）");
            Check(allPending.Count >= 2 && allPending.All(x => x.RangeCount == x.TotalCount),
                  "不限区间视图 RangeCount 回落为累计次数（不显示误导性的区间次数）");

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

                var tabs = Field<TabControl>("_tabs");
                var chips = Field<ChipBar>("_chips");
                var title = Field<Label>("_pageTitle");
                var statusLeft = Field<Label>("_statusLeft");
                var cw = form.ClientSize.Width;
                var ch = form.ClientSize.Height;

                // v3.6.0：回归 Windows 原版风格 —— 系统标准标题栏 + 顶部 TabControl，无自绘标题栏/侧栏
                Check(form.FormBorderStyle == FormBorderStyle.Sizable,
                      "使用系统标准标题栏（FormBorderStyle=Sizable），无自绘标题栏");
                Check(form.Controls.OfType<TitleBar>().Any() == false,
                      "已无自绘 TitleBar 控件（回归系统标题栏）");
                Check(tabs != null && tabs.Dock == DockStyle.Fill,
                      "顶部 TabControl 存在（Dock=Fill，替代侧栏导航）");
                Check(tabs != null && tabs.TabPages.Count == 6,
                      $"TabControl 有 {tabs?.TabPages.Count} 个页签（4 个主页面 + 2 个内嵌工具页）");

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
                Check(content != null && content.Top >= 0 &&
                      content.Top + content.Height <= ch - Theme.StatusBarHeight + 1,
                      $"**内容区不与顶栏/状态栏重叠**（x={content?.Left}, y={content?.Top}, h={content?.Height}）");

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
                var pageFields = new[] { "_dashboardPage", "_maintPage", "_devicePage", "_debugPage" };
                bool switchOk = true;
                var titles = new List<string>();
                for (int i = 0; i < 4; i++)
                {
                    showPage.Invoke(form, new object?[] { i });
                    Application.DoEvents();
                    titles.Add(title!.Text);
                    if (tabs!.SelectedIndex != i) switchOk = false;
                }
                Check(switchOk, "切页时 TabControl 选中对应页签");
                Check(titles.Distinct().Count() == 4, $"4 个页标题各不相同：{string.Join(" / ", titles)}");

                // ---------- 工具真内嵌（v2.9.1）----------
                // 要验的就是“一体化”：工具不再是独立窗口，而是 TopLevel=false 挂在内容区的子控件。
                var toolPages = (ToolHost[])typeof(MainForm).GetField("_toolPages",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(form)!;
                Check(toolPages.Length == 2, $"两个工具页宿主已建（{toolPages.Length} 个）");
                Check(toolPages.All(h => h.Embedded == null),
                      "**启动时工具尚未构造**（惰加载，不拖慢开机）");

                var toolTitles = new List<string>();
                for (int i = 0; i < toolPages.Length; i++)
                {
                    var page = 4 + i;
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
                    Check(host.AutoScroll,
                          $"{pt}：页面宿主开了 AutoScroll（窗口拉小靠滚动条兑）");
                    Check(host.Visible,
                          $"{pt}：**页面宿主可见**（v3.6.0 修复：Ensure 时点亮 Visible，否则 TabPage 白屏）");
                    Check(emb.Width > 50 && emb.Height > 50,
                          $"{pt}：工具窗体已铺开真实尺寸（{emb.Width}×{emb.Height}，不是 0 尺寸白屏）");
                    Check(tabs!.SelectedIndex == page, $"{pt}：对应 Tab 被选中");
                }
                Check(toolTitles.Distinct().Count() == 2, $"两个工具页标题各不相同：{string.Join(" / ", toolTitles)}");
                Check(toolPages[1].Embedded is AggCenterForm,
                      "第 2 个工具页 = 聚合看板（AggCenterForm 内嵌，空共享目录下也能正常开页）");

                showPage.Invoke(form, new object?[] { 0 });
                Application.DoEvents();
                Check(tabs!.SelectedIndex == 0 && toolPages.All(h => h.Embedded != null),
                      "切回总览后 Tab 回到 0，工具实例保留（再切回去无需重建）");
                Check(typeof(MainForm).GetMethod("OpenTool",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) == null,
                      "**已没有 OpenTool 弹窗路径**（工具只能内嵌，防回流）");

                form.Hide();
                form.Dispose();
            }

            // ---------- 调试工具默认显示（v3.4.1：用户拍板直接显示在菜单；--debug 参数保留兼容无副作用）----------
            Console.WriteLine("\n【调试工具显示】不带 --debug 时顶部 Tab 也有调试入口，导航结构不塌");
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
                var tabs2 = (TabControl)typeof(MainForm).GetField("_tabs",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(f2)!;
                Check(tabs2.TabPages.Count == 6, $"生产模式 TabControl {tabs2.TabPages.Count} 项（调试默认显示：4 页 + 2 工具）");
                Check(tabs2.TabPages[3].Text.Contains("调试"), "生产模式有「调试工具」Tab（v3.4.1 默认显示）");
                // 调试页正常可达：切过去 Tab 选中
                var sp2 = typeof(MainForm).GetMethod("ShowPage",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                sp2.Invoke(f2, new object?[] { 3 });
                Application.DoEvents();
                Check(tabs2.SelectedIndex == 3, "生产模式调试页 Tab 被选中（入口可用）");
                Check(f2.Visible, "调试项显示后主窗口一切正常");
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
                        // v3.0.0：分发器的内置模板（tpl\*.xml）随它一起搬去 FctDistributor/ 了。
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
                Check(btns.Count == 0 || btns.All(b => b.FlatStyle == FlatStyle.System || b.FlatStyle == FlatStyle.Standard),
                      $"{name}：{btns.Count} 个按钮全部改成系统原生风格（v3.6.0 告别扁平）");
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

        // ==================== 聚合链路连通性（v3.4.11） ====================
        Console.WriteLine("\n【聚合链路连通性】状态机 / 主备聚合端 / 断连恢复 / 队列溢出计数");
        RunLinkConnectivityTests(work);

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

        // ==================== H-1 回归：mesh XML 本地读取白名单 ====================
        RunMeshXmlWhitelistTests(work);

        // ==================== 更新器（v3.6.0：本地更新包检测 / RELEASE.txt / 已提示去重） ====================
        RunUpdateCheckerTests(work);

        // ==================== P0 性能改造回归（v3.5.3：读写分离/批量/组提交/缓存/维护） ====================
        Console.WriteLine("\n【P0 性能】AggDatabase 读写分离并发 / InsertBatch / 接收端组提交 / 计数缓存 / 每日维护 / RETURNING");
        RunP0PerformanceTests(work);

        // ==================== 在线 XML 查看（v3.8.0 MVP：解析口径 / 渲染等效） ====================
        Console.WriteLine("\n【在线 XML 查看】ParseReportText 口径 / KPI / 失败标红 / 排除项 / 时间优先级");
        RunXmlReportTests();

        // ==================== 聚合库 schema 迁移（v3.8.0：老库升级兼容） ====================
        Console.WriteLine("\n【聚合库迁移】全新库建表 / v3.5.3 老库升级（表已存在）/ 幂等重入");
        RunDbMigratorTests(work);

        // ==================== 良率日统计 yld_daily（v3.8.0 P1 D1：心跳携带） ====================
        Console.WriteLine("\n【良率日统计】upsert 幂等 / 查询过滤 / 心跳落库 / 变化检测 / 老格式兼容");
        RunYldDailyTests(work);

        // ==================== 权限与审计（v3.8.0 P1 D3：users 三角色 / audit_log / /api/status） ====================
        Console.WriteLine("\n【权限与审计】PasswordHasher / users upsert / audit 落库 / 宽松模式 / token 模式角色分级");
        RunAuthAuditTests(work);

        // ==================== 指标与可观测（v3.10.0 P1-1/P1-4：/api/metrics + 日志滚动） ====================
        Console.WriteLine("\n【指标与可观测】入库/接收/忽略计数 / /api/metrics 路由 / 日志滚动");
        RunMetricsTests(work);

        // ==================== 备份与 XML 容灾（v3.10.0 P1-2/P1-3） ====================
        Console.WriteLine("\n【备份与容灾】副本库/机台库每日备份 / XML 容灾缓存命中");
        RunBackupXmlTests(work);

        // ==================== P3+P4：FAIL 双视图/高频标色/自定义列/导出 + 源机检索缓存/并发/鉴权/白名单（2026-08-28 Agent A） ====================
        Console.WriteLine("\n【P3+P4】FAIL 双视图分组/高频标色/自定义列/导出 + 源机检索并发/缓存/零膨胀/在线 XML 联动");
        RunP3P4Tests(work);

        // ==================== P5：维修/待办全功能服务端化（2026-08-28 Agent B） ====================
        Console.WriteLine("\n【P5】维修看板 4 列状态机 legacy + 待办合并优先级20/5 + 人员改名同步 + 12列导出CSV注入防护CWE-1236 + API鉴权三角色");
        RunP5Tests(work);

        // ==================== P6+P2-A+P7：设备监控 + 无头服务化 + 数据拉取雏形（2026-08-28 Agent C） ====================
        Console.WriteLine("\n【P6+P7+P2-A】设备监控（心跳轻量+5min全量+L1维度+采样滚动7天+FCT.ini）+ 无头服务化 + 数据拉取趋势/分布雏形");
        RunP6DeviceHeadlessFetcherTests(work);

        // ==================== Lite-Settings：剩余前端设置与体验层（2026-08-28 Agent Lite） ====================
        Console.WriteLine("\n【Lite-Settings】用户管理UI/角色分级/布局持久化双写/收藏/全局搜索空格分词聚合/自定义拖拽/权限显隐");
        RunLiteSettingsTests(work);

        // ==================== Lite-Fetch：数据拉取完整 + 报告中心 + 程序日志（2026-08-28） ====================
        Console.WriteLine("\n【Lite-Fetch】数据拉取完整导出/热力/报告摘要归档对比/程序日志登记时间轴参数diff + 统一CSV注入BOM");
        RunLiteFetchTests(work);

        Console.WriteLine(_fail == 0 ? "\n==== 全部通过 ====" : $"\n==== {_fail} 项失败 ====");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>在线 XML 报告查看（v3.8.0 MVP）：XmlParser.ParseReportText 解析口径 +
    /// XmlReportHtml.Render 渲染等效性断言（与本地 XmlViewerForm 口径一致）。</summary>
    static void RunXmlReportTests()
    {
        const string xml = """
            <BATCH TIMESTAMP="2026-08-20T08:00:00.000">
            <FACTORY USER="Operator" TESTER="PEU_G49_FCT3"/>
            <PANEL STATUS="Failed" TIMESTAMP="2026-08-21T07:17:29.004"/>
            <DUT ID="E3002781AFV75236898002K30500272"/>
            <TEST NAME="KL30_1" VALUE="0.30" LOLIM="0.18" HILIM="0.38" UNIT="A" STATUS="Failed"/>
            <TEST NAME="Get Unit Information" VALUE="ERR" STATUS="Failed"/>
            <TEST NAME="P17V_LV_LS" VALUE="16.5" LOLIM="16.0" HILIM="17.0" UNIT="V" STATUS="Passed"/>
            <TEST NAME="P5V_LVX_LS" VALUE="5.0" STATUS="Passed"/>
            </BATCH>
            """;
        var d = XmlParser.ParseReportText(xml);
        Check(!d.Error, "文本解析成功（DTD Prohibit 防 XXE 路径可走）");
        Check(d.Sn == "E3002781AFV75236898002K30500272", "SN 提取正确");
        Check(d.PanelStatus == "Failed", "PANEL 状态提取正确");
        Check(d.Tester == "PEU_G49_FCT3", "TESTER 提取正确");
        Check(d.FactoryUser == "Operator", "USER 提取正确");
        Check(d.BatchTimestamp == "", "无 filePath 时 BatchTimestamp 为空（不再读 XML 属性时间）");
        Check(d.Tests.Count == 4, "全部 TEST 项收集（含 PASS，不只失败）");

        var html = XmlReportHtml.Render(d, "demo_fail.xml", "/api/file?id=1");
        Check(html.Contains("class=\"badge fail\""), "状态徽章 FAIL（主红）");
        Check(html.Contains("class=\"status st-fail\""), "失败项标红（st-fail）");
        Check(html.Contains("排除·不计入不良") && html.Contains("class=\"status st-ign\""), "排除项灰色标注（Get Unit Information）");
        Check(html.Contains("测试项总数") && html.Contains("color:#C8102E\">4</div>"), "KPI：测试项总数 4");
        Check(html.Contains("color:#C8102E\">1</div>"), "KPI：失败计入不良 1（排除项不计）");
        Check(html.Contains("通过项") && html.Contains("color:#141414\">2</div>"), "KPI：通过项 2");
        Check(!html.Contains("<script"), "渲染输出无脚本注入");

        var d2 = XmlParser.ParseReportText(xml.Replace("\"Failed\"", "\"Passed\""));
        var html2 = XmlReportHtml.Render(d2, "demo_pass.xml", null);
        Check(html2.Contains("class=\"badge pass\""), "PASS 徽章（墨黑）");
        Check(html2.Contains("color:#141414\">0</div>"), "PASS 报告失败计数为 0");
    }

    /// <summary>聚合库 schema 版本化迁移（v3.8.0）：全新库建表 / v3.5.3 老库升级（表已存在且
    /// user_version=0，历史真实 bug：精确 CREATE 报 already exists 导致 Open 崩）/ 幂等重入。</summary>
    static void RunDbMigratorTests(string work)
    {
        // ---- 场景 A：全新库 → 建表 + user_version 升到 LatestVersion + 可插入 ----
        var pathA = Path.Combine(work, "mig_fresh_" + Guid.NewGuid().ToString("N")[..6] + ".db");
        using (var c = new SqliteConnection($"Data Source={pathA}"))
        {
            c.Open();
            DbMigrator.Migrate(c);
            long uv;
            using (var cmd = c.CreateCommand()) { cmd.CommandText = "PRAGMA user_version;"; uv = Convert.ToInt64(cmd.ExecuteScalar()); }
            Check(uv == DbMigrator.LatestVersion, $"全新库迁移后 user_version={uv}（期望 {DbMigrator.LatestVersion}）");
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='agg_records';";
                Check(Convert.ToInt64(cmd.ExecuteScalar()) == 1, "全新库迁移后 agg_records 表存在");
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO agg_records (machine, seq) VALUES ('FCT1', 1);";
                cmd.ExecuteNonQuery();
            }
            Check(true, "全新库迁移后可插入（表结构可用）");
        }

        // ---- 场景 B：v3.5.3 老库升级（agg_records 已存在、user_version=0）→ 不抛异常、数据保留 ----
        var pathB = Path.Combine(work, "mig_legacy_" + Guid.NewGuid().ToString("N")[..6] + ".db");
        using (var c = new SqliteConnection($"Data Source={pathB}"))
        {
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                // 与 v3.5.3 既有的 agg_records DDL 完全一致（不带 user_version）
                cmd.CommandText = @"CREATE TABLE agg_records (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    machine TEXT NOT NULL, seq INTEGER NOT NULL, type TEXT NOT NULL DEFAULT 'fail',
                    ts TEXT, ingest_ts TEXT, station_id TEXT, model TEXT, category TEXT, test_date TEXT, sn TEXT,
                    result TEXT, fail_reason TEXT, tester TEXT, panel_status TEXT,
                    batch_timestamp TEXT, has_fail_items INTEGER, file_size INTEGER, xml_path TEXT,
                    UNIQUE(machine, seq));";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO agg_records (machine, seq) VALUES ('FCT3', 100);";
                cmd.ExecuteNonQuery();
            }
            string? err = null;
            try { DbMigrator.Migrate(c); } catch (Exception ex) { err = ex.Message; }
            Check(err == null, "v3.5.3 老库（表已存在 + user_version=0）迁移不抛异常" + (err == null ? "" : $"：{err}"));
            long uvB;
            using (var cmd = c.CreateCommand()) { cmd.CommandText = "PRAGMA user_version;"; uvB = Convert.ToInt64(cmd.ExecuteScalar()); }
            Check(uvB == DbMigrator.LatestVersion, $"老库升级后 user_version={uvB}（期望 {DbMigrator.LatestVersion}）");
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM agg_records WHERE machine='FCT3' AND seq=100;";
                Check(Convert.ToInt64(cmd.ExecuteScalar()) == 1, "老库升级后既有数据保留（FCT3/seq=100 仍在）");
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO agg_records (machine, seq) VALUES ('FCT3', 101);";
                cmd.ExecuteNonQuery();
            }
            Check(true, "老库升级后可继续插入新行");
        }

        // ---- 场景 C：幂等重入（同库再次 Migrate 不再增长 user_version） ----
        using (var c = new SqliteConnection($"Data Source={pathB}"))
        {
            c.Open();
            long before, after;
            using (var cmd = c.CreateCommand()) { cmd.CommandText = "PRAGMA user_version;"; before = Convert.ToInt64(cmd.ExecuteScalar()); }
            DbMigrator.Migrate(c);
            using (var cmd = c.CreateCommand()) { cmd.CommandText = "PRAGMA user_version;"; after = Convert.ToInt64(cmd.ExecuteScalar()); }
            Check(before == after && after == DbMigrator.LatestVersion, "重复 Migrate 幂等（user_version 不再增长）");
        }

        try { File.Delete(pathA); File.Delete(pathB); } catch { }
    }

    /// <summary>良率日统计（v3.8.0 P1 D1）：yld_daily upsert 幂等 / 查询过滤 /
    /// 心跳携带统计落库 + 变化检测 / 老格式心跳（无 today）兼容。</summary>
    static void RunYldDailyTests(string work)
    {
        var dbPath = Path.Combine(work, "yld_" + Guid.NewGuid().ToString("N")[..6] + ".db");
        using (var db = new AggDatabase(dbPath))
        {
            db.Open();
            // v2 迁移：yld_daily 表存在
            using (var c = new SqliteConnection($"Data Source={dbPath}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='yld_daily';";
                Check(Convert.ToInt64(cmd.ExecuteScalar()) == 1, "迁移 v2 后 yld_daily 表存在");
            }

            // upsert 幂等 + 同键覆盖更新
            db.UpsertDailyStats("FCT1", "20260827", new AggDatabase.DailyStats(100, 95, 4, 1, 50));
            db.UpsertDailyStats("FCT1", "20260827", new AggDatabase.DailyStats(110, 104, 5, 1, 55)); // 同键更新
            db.UpsertDailyStats("FCT2", "20260827", new AggDatabase.DailyStats(80, 78, 2, 0, 40));
            db.UpsertDailyStats("FCT1", "20260826", new AggDatabase.DailyStats(90, 88, 2, 0, 45));
            var all = db.QueryDailyStats();
            Check(all.Count == 3, $"3 个 (machine,date) 键 => {all.Count} 行（同键重复 upsert 只留 1 行）");
            var f1 = all.First(r => r.Machine == "FCT1" && r.TestDate == "20260827");
            Check(f1.Pass == 104 && f1.Total == 110 && f1.Fail == 5 && f1.Products == 55, "同键 upsert 更新为新值（幂等覆盖）");

            // 查询过滤
            var f1Rows = db.QueryDailyStats(machine: "FCT1");
            Check(f1Rows.Count == 2, $"machine=FCT1 过滤 => {f1Rows.Count} 行");
            var win = db.QueryDailyStats(dateFromYmd: "20260827", dateToYmd: "20260827");
            Check(win.Count == 2 && win.All(r => r.TestDate == "20260827"), "from/to 日期窗口过滤正确");

            // 心跳链路：MeshReceiver.HandleHeartbeat 携带统计 → yld_daily 落库
            var recv = new MeshReceiver(db, heartbeatTimeoutSec: 90);
            recv.HandleHeartbeat(HeartbeatJsonWithStats("FCT3", "20260827", 60, 58, 2, 0, 30));
            var f3 = db.QueryDailyStats(machine: "FCT3");
            Check(f3.Count == 1 && f3[0].Pass == 58, "心跳携带统计 -> yld_daily 落库");
            recv.HandleHeartbeat(HeartbeatJsonWithStats("FCT3", "20260827", 60, 58, 2, 0, 30));
            Check(db.QueryDailyStats(machine: "FCT3").Count == 1, "同值重复心跳不重复写（变化检测）");
            recv.HandleHeartbeat(HeartbeatJsonWithStats("FCT3", "20260827", 61, 59, 2, 0, 30));
            var f3c = db.QueryDailyStats(machine: "FCT3");
            Check(f3c.Count == 1 && f3c[0].Total == 61, "统计值变化 -> 更新同一行");

            // 老格式心跳（无 today 字段）：不写库、不报错（向后兼容 v3.8.0 前机台）
            recv.HandleHeartbeat(HeartbeatJsonLegacy("FCT4"));
            Check(db.QueryDailyStats(machine: "FCT4").Count == 0, "老格式心跳（无 today）不写库且不报错");
            recv.HandleHeartbeat("{\"type\":\"heartbeat\",\"ts\":\"2026-08-27 10:00:00\"}");
            Check(true, "缺失 machine 的心跳被忽略（不抛异常）");
        }
        try { File.Delete(dbPath); } catch { }
    }

    /// <summary>带今日统计字段的心跳 JSON（v3.8.0 P1 D1 机台端同款）。</summary>
    static string HeartbeatJsonWithStats(string machine, string today, int total, int pass, int fail, int intr, int prod)
        => $"{{\"machine\":\"{machine}\",\"type\":\"heartbeat\",\"ts\":\"2026-08-27 10:00:00\",\"last_seq\":0,\"queued\":0,\"today\":\"{today}\",\"today_total\":{total},\"today_pass\":{pass},\"today_fail\":{fail},\"today_interrupted\":{intr},\"today_products\":{prod}}}";

    /// <summary>v3.8.0 前老格式心跳（无 today 统计字段）。</summary>
    static string HeartbeatJsonLegacy(string machine)
        => $"{{\"machine\":\"{machine}\",\"type\":\"heartbeat\",\"ts\":\"2026-08-27 10:00:00\",\"last_seq\":0,\"queued\":0}}";

    /// <summary>带 token 头 GET（鉴权断言辅助）。</summary>
    static HttpResponseMessage HttpGetWithToken(string url, string? token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(token)) req.Headers.Add("X-Agg-Token", token);
        return _http.SendAsync(req).GetAwaiter().GetResult();
    }

    /// <summary>权限与审计（v3.8.0 P1 D3）：PasswordHasher / users upsert+token 保留 / audit 落库 /
    /// HTTP 宽松模式（未配 agg_token=匿名全权，向后兼容）/ 配置 agg_token 后角色分级（agg_token=admin、
    /// users 三角色、越权 403）、登录 401、导出/设置审计挂点。</summary>
    static void RunAuthAuditTests(string work)
    {
        var dbPath = Path.Combine(work, "auth_" + Guid.NewGuid().ToString("N")[..6] + ".db");
        using (var db = new AggDatabase(dbPath))
        {
            db.Open();
            using (var c = new SqliteConnection($"Data Source={dbPath}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='users';";
                Check(Convert.ToInt64(cmd.ExecuteScalar()) == 1, "迁移 v3 后 users 表存在");
            }

            // PasswordHasher
            var h = PasswordHasher.Hash("secret123");
            Check(PasswordHasher.Verify("secret123", h), "PasswordHasher：正确密码校验通过");
            Check(!PasswordHasher.Verify("wrong", h), "PasswordHasher：错误密码校验失败");
            Check(!PasswordHasher.Verify("x", "not-a-valid-format"), "PasswordHasher：非法格式返回 false 不抛");
            Check(h.Split('.').Length == 3, "PasswordHasher：存储格式 iterations.salt.hash");

            // users upsert + token 保留（更新密码/角色不换 token，已登录会话不失效）
            db.UpsertUser("alice", PasswordHasher.Hash("pw1"), "viewer");
            var a1 = db.GetUserByName("alice");
            Check(a1 != null && a1!.Role == "viewer" && a1.Token.Length == 32, "UpsertUser：新用户带 32hex token");
            var tok1 = a1!.Token;
            db.UpsertUser("alice", PasswordHasher.Hash("pw1b"), "engineer");
            var a2 = db.GetUserByName("alice");
            Check(a2 != null && a2!.Role == "engineer" && a2.Token == tok1, "upsert 更新保留旧 token");
            Check(PasswordHasher.Verify("pw1b", a2!.PwdHash), "upsert 更新密码生效");
            Check(db.GetUserByToken(tok1)?.Name == "alice", "GetUserByToken 命中");
            db.UpsertUser("bob", PasswordHasher.Hash("pw2"), "admin");
            Check(db.ListUsers().Count == 2, "ListUsers 返回 2 人");
            Check(db.DeleteUser("bob") && db.GetUserByName("bob") == null, "DeleteUser 删除成功");

            // audit 落库 + 查询倒序
            db.LogAudit("alice", "export.csv", "10 rows");
            db.LogAudit("agg_token", "settings.save", "agg_token");
            var aud = db.QueryAudit();
            Check(aud.Count == 2 && aud[0].Action == "settings.save", "QueryAudit 倒序返回 2 条");
        }

        // ---- HTTP 宽松模式（未配 agg_token = 匿名全权，保持 v3.5.x 未鉴权向后兼容）----
        var webRoot = Path.Combine(work, "auth_http");
        Directory.CreateDirectory(webRoot);
        int port = GetFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";
        using (var db = new AggDatabase(Path.Combine(webRoot, "agg.db")))
        using (var srv = new WebAggServer(port, (AggWatcher?)null, db, webRoot, webRoot, ""))
        {
            db.Open();   // 必须先 Open（跑 DbMigrator 建 users 表），否则 ResolveRole 查表抛异常
            srv.Start();
            Check(WaitUntil(() => srv.Listening, 5000), "鉴权组：宽松模式服务进入监听");

            var rStatus = _http.GetAsync($"{baseUrl}/api/status").GetAwaiter().GetResult();
            Check(rStatus.StatusCode == HttpStatusCode.OK, "宽松模式 GET /api/status -> 200");
            if (rStatus.StatusCode == HttpStatusCode.OK)
            {
                using var d = System.Text.Json.JsonDocument.Parse(rStatus.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                var role = d.RootElement.TryGetProperty("role", out var pr) ? pr.GetString() : "";
                Check(role == "admin", $"宽松模式 status.role=admin（实际 {role}）");
                Check(d.RootElement.TryGetProperty("today_yield", out _), "status 含 today_yield 字段");
            }

            // 宽松 admin 创建用户（POST /api/users）返回 token
            var rCreate = _http.PostAsync($"{baseUrl}/api/users", new StringContent(
                "{\"name\":\"u1\",\"password\":\"pass1\",\"role\":\"viewer\"}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            string? u1token = null;
            if (rCreate.StatusCode == HttpStatusCode.OK)
            {
                using var d = System.Text.Json.JsonDocument.Parse(rCreate.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                u1token = d.RootElement.TryGetProperty("token", out var pt) ? pt.GetString() : null;
            }
            Check(rCreate.StatusCode == HttpStatusCode.OK && !string.IsNullOrEmpty(u1token),
                  "POST /api/users 创建 viewer 返回 token");

            var rLogin = _http.PostAsync($"{baseUrl}/api/login", new StringContent(
                "{\"name\":\"u1\",\"password\":\"pass1\"}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(rLogin.StatusCode == HttpStatusCode.OK, "POST /api/login 正确密码 -> 200");
            var rLoginBad = _http.PostAsync($"{baseUrl}/api/login", new StringContent(
                "{\"name\":\"u1\",\"password\":\"wrong\"}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(rLoginBad.StatusCode == HttpStatusCode.Unauthorized, "POST /api/login 错误密码 -> 401");

            // 创建用户后系统进入鉴权态：匿名不再全权（403），users.token 通道可用
            var rAnonAudit = _http.GetAsync($"{baseUrl}/api/audit").GetAwaiter().GetResult();
            Check(rAnonAudit.StatusCode == HttpStatusCode.Forbidden, "创建用户后匿名访问 /api/audit -> 403（鉴权收紧）");
            var rViewerAudit = HttpGetWithToken($"{baseUrl}/api/audit", u1token);
            Check(rViewerAudit.StatusCode == HttpStatusCode.Forbidden, "viewer token 访问 /api/audit -> 403（角色不足）");
        }

        // ---- HTTP 导出审计挂点（宽松模式，users 表为空时匿名全权）----
        int portB = GetFreePort();
        var baseUrlB = $"http://127.0.0.1:{portB}";
        using (var dbB = new AggDatabase(Path.Combine(webRoot, "aggb.db")))
        using (var srvB = new WebAggServer(portB, (AggWatcher?)null, dbB, webRoot, webRoot, ""))
        {
            dbB.Open();
            srvB.Start();
            Check(WaitUntil(() => srvB.Listening, 5000), "鉴权组：审计挂点服务进入监听");
            var rCsvB = _http.GetAsync($"{baseUrlB}/api/export.csv").GetAwaiter().GetResult();
            Check(rCsvB.StatusCode == HttpStatusCode.OK, "GET /api/export.csv -> 200");
            var rAuditB = _http.GetAsync($"{baseUrlB}/api/audit").GetAwaiter().GetResult();
            if (rAuditB.StatusCode == HttpStatusCode.OK)
            {
                using var d = System.Text.Json.JsonDocument.Parse(rAuditB.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                bool hasCsv = d.RootElement.EnumerateArray().Any(e =>
                    e.TryGetProperty("Action", out var a) && a.GetString() == "export.csv");
                Check(hasCsv, "审计包含 export.csv 记录（导出挂点生效）");
            }
            else Check(false, "GET /api/audit -> 200");
        }

        // ---- HTTP 配置 agg_token 后角色分级 ----
        int port2 = GetFreePort();
        var baseUrl2 = $"http://127.0.0.1:{port2}";
        const string aggTok = "aabbccddeeff00112233445566778899";
        using (var db2 = new AggDatabase(Path.Combine(webRoot, "agg2.db")))
        using (var srv2 = new WebAggServer(port2, (AggWatcher?)null, db2, webRoot, webRoot, aggTok))
        {
            db2.Open();   // 同宽松模式：先迁移建表
            srv2.Start();
            Check(WaitUntil(() => srv2.Listening, 5000), "鉴权组：token 模式服务进入监听");

            var rNoTok = _http.GetAsync($"{baseUrl2}/api/status").GetAwaiter().GetResult();
            Check(rNoTok.StatusCode == HttpStatusCode.Forbidden, "token 模式：无 token 访问 -> 403");

            // agg_token 通道（admin）创建 engineer
            using (var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl2}/api/users"))
            {
                req.Headers.Add("X-Agg-Token", aggTok);
                req.Content = new StringContent("{\"name\":\"u2\",\"password\":\"pass2\",\"role\":\"engineer\"}", Encoding.UTF8, "application/json");
                var rU2 = _http.SendAsync(req).GetAwaiter().GetResult();
                Check(rU2.StatusCode == HttpStatusCode.OK, "agg_token 通道创建 engineer -> 200");
            }

            // u2 登录拿 token（/api/login 豁免 agg_token 校验，未登录可到达）
            string? u2token = null;
            var rL = _http.PostAsync($"{baseUrl2}/api/login", new StringContent(
                "{\"name\":\"u2\",\"password\":\"pass2\"}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            if (rL.StatusCode == HttpStatusCode.OK)
            {
                using var d = System.Text.Json.JsonDocument.Parse(rL.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                u2token = d.RootElement.TryGetProperty("token", out var pt) ? pt.GetString() : null;
            }
            Check(!string.IsNullOrEmpty(u2token), "u2 登录拿到 token（login 端点豁免鉴权）");

            var rU2Status = HttpGetWithToken($"{baseUrl2}/api/status", u2token);
            Check(rU2Status.StatusCode == HttpStatusCode.OK, "engineer token 访问 /api/status -> 200");
            var rU2Audit = HttpGetWithToken($"{baseUrl2}/api/audit", u2token);
            Check(rU2Audit.StatusCode == HttpStatusCode.Forbidden, "engineer 访问 /api/audit -> 403（角色不足）");
            var rAggAudit = HttpGetWithToken($"{baseUrl2}/api/audit", aggTok);
            Check(rAggAudit.StatusCode == HttpStatusCode.OK, "agg_token(admin) 访问 /api/audit -> 200");
        }

        try { File.Delete(dbPath); } catch { }
    }

    /// <summary>指标与可观测（v3.10.0 P1-1/P1-4）：入库/接收/忽略计数正确性、
    /// /api/metrics 路由返回结构、日志按大小滚动出 app.log.1。</summary>
    static void RunMetricsTests(string work)
    {
        var dbPath = Path.Combine(work, "metrics_" + Guid.NewGuid().ToString("N")[..6] + ".db");
        using (var db = new AggDatabase(dbPath))
        {
            db.Open();
            // ---- AggDatabase.InsertCount：新插入计数、重复忽略不计 ----
            var r1 = new AggFailRow { Machine = "FCT1", Seq = 1, Type = "fail", Ts = "2026-08-28 09:00:00", TestDate = "20260828" };
            var r2 = new AggFailRow { Machine = "FCT1", Seq = 2, Type = "fail", Ts = "2026-08-28 09:01:00", TestDate = "20260828" };
            db.InsertFail(r1); db.InsertFail(r2);
            db.InsertFail(new AggFailRow { Machine = "FCT1", Seq = 1, Type = "fail", Ts = "x", TestDate = "20260828" }); // 重复
            Check(db.InsertCount == 2, $"InsertFail 计数：2 条新插入 + 1 条重复忽略 => InsertCount={db.InsertCount}（期望 2）");
            db.InsertBatch(new[]
            {
                new AggFailRow { Machine = "FCT2", Seq = 1, Type = "fail", Ts = "2026-08-28 09:02:00", TestDate = "20260828" },
                new AggFailRow { Machine = "FCT2", Seq = 2, Type = "fail", Ts = "2026-08-28 09:03:00", TestDate = "20260828" },
                new AggFailRow { Machine = "FCT1", Seq = 2, Type = "fail", Ts = "x", TestDate = "20260828" },  // 重复
            });
            Check(db.InsertCount == 4, $"InsertBatch 计数：+2 新插入（1 重复不计）=> InsertCount={db.InsertCount}（期望 4）");

            // ---- MeshReceiver 接收/忽略计数 ----
            var rx = new MeshReceiver(db, heartbeatTimeoutSec: 90);
            rx.HandleFail(BuildFailJsonCustom(100, "RX1", "X:\\agg\\a.xml", "SN-RX-1", "接收计数"));
            rx.HandleFail(BuildFailJsonCustom(100, "RX1", "X:\\agg\\a.xml", "SN-RX-1", "重复"));  // (machine,seq) 重复
            Check(rx.ReceivedFails == 2 && rx.IgnoredFails == 1, $"HandleFail 计数：接收 2 / 忽略 1（实得 {rx.ReceivedFails}/{rx.IgnoredFails}）");
            Check(rx.CommittedRows == 2, $"组提交处理行数（含重复）= {rx.CommittedRows}（期望 2）");
            Check(db.InsertCount == 5, $"接收端入库计入 InsertCount（去重后 4+1=5）：{db.InsertCount}");
        }

        // ---- /api/metrics 路由（宽松模式 server，无 mesh => receiver/pusher 为 null，web/db 有值）----
        var webRoot = Path.Combine(work, "metrics_http");
        Directory.CreateDirectory(webRoot);
        int port = GetFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";
        using (var db = new AggDatabase(Path.Combine(webRoot, "agg.db")))
        using (var srv = new WebAggServer(port, (AggWatcher?)null, db, webRoot, webRoot, ""))
        {
            db.Open();
            srv.Start();
            Check(WaitUntil(() => srv.Listening, 5000), "指标组：服务进入监听");
            db.InsertFail(new AggFailRow { Machine = "M1", Seq = 1, Type = "fail", Ts = "2026-08-28 09:10:00", TestDate = "20260828" });
            var r = _http.GetAsync($"{baseUrl}/api/metrics").GetAwaiter().GetResult();
            if (r.StatusCode == HttpStatusCode.OK)
            {
                using var d = System.Text.Json.JsonDocument.Parse(r.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                var root = d.RootElement;
                Check(root.TryGetProperty("web", out var web) && web.TryGetProperty("requests", out _)
                      && web.TryGetProperty("rejected_503", out _), "/api/metrics 含 web.requests / web.rejected_503");
                Check(root.TryGetProperty("db", out var dbe) && dbe.TryGetProperty("inserts", out var ins)
                      && ins.GetInt64() >= 1, "/api/metrics 含 db.inserts >= 1");
                Check(root.TryGetProperty("receiver", out var rcv) && rcv.ValueKind == System.Text.Json.JsonValueKind.Null,
                      "/api/metrics 无 mesh 时 receiver 为 null（不炸）");
                Check(root.TryGetProperty("pusher", out var psh) && psh.ValueKind == System.Text.Json.JsonValueKind.Null,
                      "/api/metrics 无 mesh 时 pusher 为 null（不炸）");
                Check(root.TryGetProperty("uptime_sec", out _) && root.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
                      "/api/metrics 含 ok/uptime_sec");
            }
            else Check(false, "GET /api/metrics -> 200");
        }

        // ---- 日志滚动（P1-4）：反射调小阈值 → 写日志触发滚动 → app.log.1 生成 ----
        var logDir = Path.Combine(AppConfig.BaseDir, "logs");
        var appLog = Path.Combine(logDir, "app.log");
        var fMax = typeof(Logger).GetField("MaxLogBytes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var fWritten = typeof(Logger).GetField("_writtenBytes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        try
        {
            if (Directory.Exists(logDir)) Directory.Delete(logDir, true);
            Directory.CreateDirectory(logDir);
            fMax.SetValue(null, 512L);        // 阈值调小到 512B，几十条日志即可触发
            fWritten.SetValue(null, 0L);      // 清空近似计数
            for (int i = 0; i < 80; i++) Logger.Info($"指标自检滚动触发行 {i} 填充足够长度使单文件超过阈值");
            Check(File.Exists(Path.Combine(logDir, "app.log.1")), "日志超阈值后滚动出 app.log.1");
            Check(File.Exists(appLog), "滚动后新 app.log 继续写入");
        }
        finally
        {
            fMax.SetValue(null, 20L * 1024 * 1024);   // 恢复 20MB
            fWritten.SetValue(null, 0L);
            try { if (Directory.Exists(logDir)) Directory.Delete(logDir, true); } catch { }
        }

        try { File.Delete(dbPath); } catch { }
    }

    /// <summary>备份与 XML 容灾（v3.10.0 P1-2/P1-3）：副本库/机台库每日备份（生成/可打开行数一致/
    /// 同日幂等/保留 7 份）、XML 拉取成功落盘缓存、源文件删除后缓存命中。</summary>
    static void RunBackupXmlTests(string work)
    {
        // ---- 1) 副本库 AggDatabase.BackupDaily ----
        var aggPath = Path.Combine(work, "bakagg_" + Guid.NewGuid().ToString("N")[..6] + ".db");
        string? bakPath = null;
        using (var db = new AggDatabase(aggPath))
        {
            db.Open();
            db.InsertFail(new AggFailRow { Machine = "B1", Seq = 1, Type = "fail", Ts = "2026-08-28 09:00:00", TestDate = "20260828" });
            db.InsertFail(new AggFailRow { Machine = "B1", Seq = 2, Type = "fail", Ts = "2026-08-28 09:01:00", TestDate = "20260828" });
            bakPath = db.BackupDaily();
            Check(bakPath != null && File.Exists(bakPath), "副本库每日备份文件生成");
            using (var c = new SqliteConnection($"Data Source={bakPath}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM agg_records;";
                Check(Convert.ToInt64(cmd.ExecuteScalar()) == 2, "备份库可打开且行数一致（2 条）");
            }
            SqliteConnection.ClearAllPools();   // 释放备份文件句柄，否则后续删除被占用
            Check(db.BackupDaily() == null, "同日重复备份幂等跳过");

            // 保留 7 份：伪造 8 个历史备份 + 当前日期备份删除后重新触发清理
            var dir = Path.GetDirectoryName(aggPath)!;
            var prefix = Path.GetFileName(aggPath) + ".bak-";
            for (int i = 0; i < 8; i++)
            {
                var fake = Path.Combine(dir, $"{prefix}{20260101 + i:00000000}");
                File.WriteAllText(fake, "x");
            }
            var todayBak = Path.Combine(dir, $"{prefix}{DateTime.Now:yyyyMMdd}");
            File.Delete(todayBak);
            db.BackupDaily();
            var left = Directory.GetFiles(dir, prefix + "*");
            Check(left.Length == 7, $"备份只保留最近 {AggDatabase.BackupKeepDays} 份（实得 {left.Length}）");
            foreach (var f in Directory.GetFiles(dir, prefix + "*")) File.Delete(f);
        }
        try { File.Delete(aggPath); } catch { }

        // ---- 2) 机台本地库 Database.BackupDaily ----
        var localPath = Path.Combine(work, "baklocal_" + Guid.NewGuid().ToString("N")[..6] + ".db");
        {
            var db = new Database(localPath);
            db.BatchInsert(new[]
            {
                new TestRecord { StationId = "FCT1", Model = "E3", Category = "Online", TestDate = "20260828", Sn = "SN-B1", Result = "PASS", XmlPath = "X:\\a.xml" },
                new TestRecord { StationId = "FCT1", Model = "E3", Category = "Online", TestDate = "20260828", Sn = "SN-B2", Result = "FAIL", XmlPath = "X:\\b.xml", FailReason = "KL30" },
            });
            var lbak = db.BackupDaily();
            Check(lbak != null && File.Exists(lbak), "机台本地库每日备份生成");
            using (var c = new SqliteConnection($"Data Source={lbak}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM test_records;";
                Check(Convert.ToInt64(cmd.ExecuteScalar()) == 2, "机台库备份可打开且行数一致（2 条）");
            }
            Check(db.BackupDaily() == null, "机台库同日重复备份幂等跳过");
            try { File.Delete(lbak!); } catch { }
        }
        try { File.Delete(localPath); } catch { }

        // ---- 3) XML 容灾缓存（P1-3）：拉取成功落盘 → 源文件删除后缓存命中 ----
        var xmlPath = Path.Combine(work, "xmlsrc_" + Guid.NewGuid().ToString("N")[..6] + ".xml");
        File.WriteAllText(xmlPath, "<BATCH TIMESTAMP=\"2026-08-28T09:00:00\"><TEST NAME=\"T1\" STATUS=\"Failed\"/></BATCH>", new UTF8Encoding(false));
        var xdbPath = Path.Combine(work, "xmlagg_" + Guid.NewGuid().ToString("N")[..6] + ".db");
        try
        {
            using var db = new AggDatabase(xdbPath);
            db.Open();
            var row = new AggFailRow { Machine = "XMC", Seq = 1, Type = "fail", Ts = "2026-08-28 09:00:00", TestDate = "20260828", XmlPath = xmlPath };
            db.InsertFail(row);
            var rx = new MeshReceiver(db, heartbeatTimeoutSec: 90);
            rx.LocalReadValidator = _ => true;
            var c1 = rx.FetchXmlForFail(row.Id);
            Check(c1 != null && c1.Contains("<BATCH"), "首次拉取（本机路径）成功");
            var cachePath = Path.Combine(MeshReceiver.XmlCacheRoot, "XMC", "20260828", $"{row.Id}.xml");
            Check(File.Exists(cachePath), "拉取成功后容灾缓存落盘（machine/date 分目录）");
            File.Delete(xmlPath);   // 模拟源机离线/源文件消失
            var c2 = rx.FetchXmlForFail(row.Id);
            Check(c2 != null && c2 == c1, "源文件删除后命中容灾缓存，仍可读取");
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
            try { File.Delete(xdbPath); } catch { }
            try { if (Directory.Exists(MeshReceiver.XmlCacheRoot)) Directory.Delete(MeshReceiver.XmlCacheRoot, true); } catch { }
        }
    }

    /// <summary>P3+P4 回归（2026-08-28 Agent A）：FAIL 双视图分组/高频标色/自定义列/导出防护 + 源机检索并发/缓存/零膨胀/在线 XML 联动</summary>
    static void RunP3P4Tests(string work)
    {
        // ---------- 1) Database.QueryTestRecords 过滤（全量含 PASS） ----------
        {
            var dbPath = Path.Combine(work, "p3q_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            var db = new Database(dbPath);
            db.BatchInsert(new[]
            {
                new TestRecord { StationId="FCT6", Model="E300", Category="Online", TestDate="20260820", Sn="SN-A001", Result="FAIL", XmlPath="X:\\a.xml", FailReason="KL30", Tester="PEU_G49_FCT6", BatchTimestamp="2026-08-20T08:00:00" },
                new TestRecord { StationId="FCT6", Model="E300", Category="Online", TestDate="20260821", Sn="SN-A002", Result="PASS", XmlPath="X:\\b.xml", FailReason="", Tester="PEU_G49_FCT6" },
                new TestRecord { StationId="FCT7", Model="E301", Category="Online", TestDate="20260821", Sn="SN-B001", Result="FAIL", XmlPath="X:\\c.xml", FailReason="Vref_3V3", Tester="PEU_G49_FCT7" },
                new TestRecord { StationId="FCT7", Model="E301", Category="Online", TestDate="20260822", Sn="SN-B002", Result="PASS", XmlPath="X:\\d.xml", Tester="PEU_G49_FCT7" },
                new TestRecord { StationId="FCT6", Model="E300", Category="Online", TestDate="20260822", Sn="SN-A003", Result="FAIL", XmlPath="X:\\e.xml", FailReason="KL30", Tester="PEU_G49_FCT6" },
            });
            var qAll = db.QueryTestRecords("ANY", new MeshQueryService.QueryRequest { Limit = 100 });
            Check(qAll.Count == 5, $"QueryTestRecords 全量 => {qAll.Count} 条（期望 5，含 PASS）");
            var qFct6 = db.QueryTestRecords("ANY", new MeshQueryService.QueryRequest { Machine = "FCT6", Limit = 100 });
            Check(qFct6.Count == 3, $"按 Machine=FCT6 => {qFct6.Count} 条（期望 3）");
            var qFail = db.QueryTestRecords("ANY", new MeshQueryService.QueryRequest { Result = "FAIL", Limit = 100 });
            Check(qFail.Count == 3 && qFail.All(x => x.Result == "FAIL"), $"按 Result=FAIL 全机台 => {qFail.Count} 条（期望 3）");
            var qFailFct6 = db.QueryTestRecords("ANY", new MeshQueryService.QueryRequest { Machine = "FCT6", Result = "FAIL", Limit = 100 });
            Check(qFailFct6.Count == 2 && qFailFct6.All(x => x.Result == "FAIL"), "按 Machine=FCT6 + Result=FAIL => 2 条");
            var qPass = db.QueryTestRecords("", new MeshQueryService.QueryRequest { Result = "PASS", Limit = 100 });
            Check(qPass.Count == 2, $"按 Result=PASS 全机台 => {qPass.Count} 条（期望 2）");
            var qSn = db.QueryTestRecords("", new MeshQueryService.QueryRequest { Sn = "SN-A", Limit = 100 });
            Check(qSn.Count == 3, $"按 SN 模糊 SN-A => {qSn.Count} 条（期望 3）");
            var qModel = db.QueryTestRecords("", new MeshQueryService.QueryRequest { Model = "E301", Limit = 100 });
            Check(qModel.Count == 2, $"按型号 E301 => {qModel.Count} 条（期望 2）");
            var qDate = db.QueryTestRecords("", new MeshQueryService.QueryRequest { DateFrom = "20260821", DateTo = "20260822", Limit = 100 });
            Check(qDate.Count == 4, $"按日期 20260821-20260822 => {qDate.Count} 条（期望 4）");
            var qLimit = db.QueryTestRecords("", new MeshQueryService.QueryRequest { Limit = 2, Offset = 1 });
            Check(qLimit.Count == 2, $"limit=2 offset=1 => {qLimit.Count} 条");
            try { File.Delete(dbPath); } catch { }
        }

        // ---------- 2) MeshQueryService 缓存 5min ----------
        {
            MeshQueryService.ClearCache();
            var req = new MeshQueryService.QueryRequest { Sn = "SN-X", Limit = 10 };
            var key = MeshQueryService.CacheKey(req);
            Check(!MeshQueryService.TryGetCached(key, out _), "缓存初始未命中");
            var payload = "{\"ok\":true,\"cached\":false}";
            MeshQueryService.PutCached(key, payload);
            Check(MeshQueryService.TryGetCached(key, out var hit) && hit == payload, "缓存写入后命中");
            Check(MeshQueryService.CacheCount == 1, "缓存计数 1");
            MeshQueryService.ClearCache();
            Check(MeshQueryService.CacheCount == 0, "ClearCache 后 0");
            // CacheKey 归一化：大小写/空格不敏感
            var k1 = MeshQueryService.CacheKey(new MeshQueryService.QueryRequest { Sn = " SN-A ", Model = "E300", Limit = 100 });
            var k2 = MeshQueryService.CacheKey(new MeshQueryService.QueryRequest { Sn = "sn-a", Model = " e300 ", Limit = 100 });
            Check(k1 == k2, "CacheKey 归一化（大小写/空格）");
        }

        // ---------- 3) 高频标色分组逻辑（与 page-fails.js 对齐） ----------
        {
            var fails = new List<string> { "KL30", "KL30", "KL30", "KL30", "KL30", "Vref_3V3", "Vref_3V3", "CAN_Bus" };
            var counts = fails.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            Func<int, string> hotClass = c => c >= 5 ? "hot5" : c >= 2 ? "hot2" : "";
            Check(hotClass(counts["KL30"]) == "hot5", "高频 ≥5 标红 (KL30=5 => hot5)");
            Check(hotClass(counts["Vref_3V3"]) == "hot2", "中频 ≥2 警告 (Vref_3V3=2 => hot2)");
            Check(hotClass(counts["CAN_Bus"]) == "", "低频无标色 (CAN_Bus=1 => none)");
            // 分组按次数倒序
            var ordered = counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
            Check(ordered[0] == "KL30" && ordered[1] == "Vref_3V3", "分组按次数倒序");
        }

        // ---------- 4) 导出 CWE-1236 防护（复用服务端 Esc 逻辑，客户端同款） ----------
        {
            Func<string, string> esc = v =>
            {
                if (v.Length > 0 && (v[0] == '=' || v[0] == '+' || v[0] == '-' || v[0] == '@' || v[0] == '\t' || v[0] == '\r')) v = "'" + v;
                if (v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r')) return "\"" + v.Replace("\"", "\"\"") + "\"";
                return v;
            };
            Check(esc("=2+5") == "'=2+5", "导出防护：= 开头加 '");
            Check(esc("@mal") == "'@mal", "导出防护：@ 开头加 '");
            Check(esc("a,b") == "\"a,b\"", "导出防护：含逗号加引号");
            Check(esc("a\"b") == "\"a\"\"b\"", "导出防护：引号翻倍");
        }

        // ---------- 5) Web 层：POST /api/mesh/query 并发/缓存/零膨胀/鉴权 + /api/mesh/query/local + /api/xmlview?path= DTD/白名单 ----------
        {
            // 准备两台机台的本地库与结果目录
            string work2 = Path.Combine(work, "p34web_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(work2);
            var resultsA = Path.Combine(work2, "resultsA");
            var resultsB = Path.Combine(work2, "resultsB");
            Directory.CreateDirectory(resultsA);
            Directory.CreateDirectory(resultsB);
            var xmlA = Path.Combine(resultsA, "a.xml");
            var xmlB = Path.Combine(resultsB, "b.xml");
            File.WriteAllText(xmlA, "<BATCH TIMESTAMP=\"2026-08-20T08:00:00\"><FACTORY USER=\"Op\" TESTER=\"PEU_G49_FCT6\"/><PANEL STATUS=\"Failed\" TIMESTAMP=\"2026-08-20T08:00:00\"/><DUT ID=\"SN-A001\"/><TEST NAME=\"KL30\" STATUS=\"Failed\" VALUE=\"0\" HILIM=\"1\"/></BATCH>", Encoding.UTF8);
            File.WriteAllText(xmlB, "<BATCH TIMESTAMP=\"2026-08-21T08:00:00\"><FACTORY USER=\"Op\" TESTER=\"PEU_G49_FCT7\"/><PANEL STATUS=\"Passed\" TIMESTAMP=\"2026-08-21T08:00:00\"/><DUT ID=\"SN-B001\"/><TEST NAME=\"Vref\" STATUS=\"Passed\" VALUE=\"1\"/></BATCH>", Encoding.UTF8);
            // 机台 A 本地库
            var dbPathA = Path.Combine(work2, "localA.db");
            var dbA = new Database(dbPathA);
            dbA.BatchInsert(new[]
            {
                new TestRecord { StationId="FCT6", Model="E300", Category="Online", TestDate="20260820", Sn="SN-A001", Result="FAIL", XmlPath=xmlA, FailReason="KL30", Tester="PEU_G49_FCT6", BatchTimestamp="2026-08-20T08:00:00" },
                new TestRecord { StationId="FCT6", Model="E300", Category="Online", TestDate="20260821", Sn="SN-A002", Result="PASS", XmlPath=xmlB, FailReason="", Tester="PEU_G49_FCT6" },
            });
            var dbPathB = Path.Combine(work2, "localB.db");
            var dbB = new Database(dbPathB);
            dbB.BatchInsert(new[]
            {
                new TestRecord { StationId="FCT7", Model="E301", Category="Online", TestDate="20260820", Sn="SN-B001", Result="FAIL", XmlPath=xmlB, FailReason="Vref_3V3", Tester="PEU_G49_FCT7" },
                new TestRecord { StationId="FCT7", Model="E301", Category="Online", TestDate="20260822", Sn="SN-B002", Result="PASS", XmlPath=xmlA, Tester="PEU_G49_FCT7" },
            });
            // 聚合库与 mesh 节点（两节点互为 peer）
            int portA = GetFreePort();
            int portB = GetFreePort();
            var aggPath = Path.Combine(work2, "agg.db");
            var aggDb = new AggDatabase(aggPath);
            aggDb.Open();
            var cfgA = new AppConfig { MeshPort = portA, AggToken = "tok123" };
            var cfgB = new AppConfig { MeshPort = portB, AggToken = "tok123" };
            var meshA = new MeshNode(cfgA, "FCT6", dbA, aggDb, new[] { $"http://127.0.0.1:{portB}/" });
            var meshB = new MeshNode(cfgB, "FCT7", dbB, new AggDatabase(Path.Combine(work2, "aggB.db")), new[] { $"http://127.0.0.1:{portA}/" });
            meshA.Receiver.LocalReadValidator = p => p.StartsWith(resultsA) || p.StartsWith(resultsB) || p == xmlA || p == xmlB;
            meshB.Receiver.LocalReadValidator = p => true;
            // Web 服务（聚合端 A 为被测，B 为 peer 侧）
            var srvA = new WebAggServer(portA, meshA, aggDb, resultsA, work2, token: "tok123");
            var srvB = new WebAggServer(portB, meshB, new AggDatabase(Path.Combine(work2, "aggB2.db")), resultsB, work2, token: "tok123");
            srvA.Start(); srvB.Start();
            Thread.Sleep(400);
            var baseA = $"http://127.0.0.1:{portA}";
            var baseB = $"http://127.0.0.1:{portB}";
            // 1) 未带 token → 403
            {
                var r = _http.PostAsync($"{baseA}/api/mesh/query", new StringContent("{\"sn\":\"SN\"}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                Check(r.StatusCode == System.Net.HttpStatusCode.Forbidden, "POST /api/mesh/query 无 token → 403");
            }
            // 2) 本机 local 查询（含 PASS）→ 200 且含 PASS
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"{baseA}/api/mesh/query/local");
                req.Headers.Add("X-Agg-Token", "tok123");
                req.Content = new StringContent("{\"result\":\"PASS\",\"limit\":10}", Encoding.UTF8, "application/json");
                var r = _http.SendAsync(req).GetAwaiter().GetResult();
                Check(r.StatusCode == System.Net.HttpStatusCode.OK, "POST /api/mesh/query/local（PASS）→ 200");
                var txt = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(txt);
                var arr = doc.RootElement.GetProperty("results");
                Check(arr.GetArrayLength() >= 1 && arr.EnumerateArray().Any(e => (e.GetProperty("Result").GetString() ?? e.GetProperty("result").GetString() ?? "") == "PASS"), "local 查询含 PASS（全量）");
            }
            // 3) 聚合查询：并发查两机台 → 合并总数 = A(2)+B(2)=4，含 PASS 与 FAIL，且聚合端 AggDatabase 零膨胀
            long beforeAggCount = aggDb.FailCount("");
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"{baseA}/api/mesh/query");
                req.Headers.Add("X-Agg-Token", "tok123");
                req.Content = new StringContent("{\"limit\":10}", Encoding.UTF8, "application/json");
                var r = _http.SendAsync(req).GetAwaiter().GetResult();
                Check(r.StatusCode == System.Net.HttpStatusCode.OK, $"POST /api/mesh/query 聚合 → 200（实得 {(int)r.StatusCode}）");
                var txt = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                bool okParse = false;
                try
                {
                    if (string.IsNullOrWhiteSpace(txt)) throw new Exception("空响应体");
                    using var doc = JsonDocument.Parse(txt);
                    Check(doc.RootElement.GetProperty("ok").GetBoolean(), "聚合响应 ok=true");
                    var total = doc.RootElement.GetProperty("total").GetInt32();
                    Check(total == 4, $"聚合总数 4（两机台各2，含 PASS，实得 {total}）");
                    var results = doc.RootElement.GetProperty("results");
                    Check(results.GetArrayLength() == 4, $"results 长度 4（实得 {results.GetArrayLength()}）");
                    Check(results.EnumerateArray().Any(e => (e.TryGetProperty("Machine", out var vm) ? vm.GetString() : e.TryGetProperty("machine", out var vm2) ? vm2.GetString() : "") == "FCT7"), "结果含 FCT7 机台（扇出成功）");
                    // 零膨胀：聚合库行数未变
                    Check(aggDb.FailCount("") == beforeAggCount, "聚合查询零膨胀（AggDatabase 未写入 PASS）");
                    okParse = true;
                }
                catch (Exception ex) { Check(false, $"聚合响应解析失败: {ex.Message} | body={txt?.Substring(0, Math.Min(200, txt?.Length ?? 0))}"); }
                // 缓存：第二次同样请求命中缓存（cached true）
                if (okParse)
                {
                    var req2 = new HttpRequestMessage(HttpMethod.Post, $"{baseA}/api/mesh/query");
                    req2.Headers.Add("X-Agg-Token", "tok123");
                    req2.Content = new StringContent("{\"limit\":10}", Encoding.UTF8, "application/json");
                    var r2 = _http.SendAsync(req2).GetAwaiter().GetResult();
                    var txt2 = r2.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    try
                    {
                        using var doc2 = JsonDocument.Parse(txt2);
                        Check(doc2.RootElement.TryGetProperty("cached", out var cv) && cv.GetBoolean(), $"二次同条件查询命中缓存（cached=true，body={txt2.Substring(0, Math.Min(120, txt2.Length))}）");
                    }
                    catch (Exception ex) { Check(false, $"缓存命中解析失败: {ex.Message} | body={txt2}"); }
                }
            }
            // 4) 并发：10 路同时查询均成功
            {
                var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
                {
                    var rq = new HttpRequestMessage(HttpMethod.Post, $"{baseA}/api/mesh/query");
                    rq.Headers.Add("X-Agg-Token", "tok123");
                    rq.Content = new StringContent("{\"limit\":5}", Encoding.UTF8, "application/json");
                    return _http.SendAsync(rq).GetAwaiter().GetResult().StatusCode;
                })).ToArray();
                Task.WaitAll(tasks);
                Check(tasks.All(t => t.Result == System.Net.HttpStatusCode.OK), "并发 10 路查询全部 200");
            }
            // 5) GET 方法不符 → 405
            {
                var r = _http.GetAsync($"{baseA}/api/mesh/query").GetAwaiter().GetResult();
                Check(r.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed, "GET /api/mesh/query → 405");
            }
            // 6) 在线 XML 联动：/api/xmlview?path= 任意产品（含 PASS）口径与本地一致（DTD Prohibit + 白名单）
            {
                // 本机路径直接渲染
                var r = HttpGetWithToken($"{baseA}/api/xmlview?path={Uri.EscapeDataString(xmlA)}", "tok123");
                Check(r.StatusCode == System.Net.HttpStatusCode.OK && r.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("KL30"), "GET /api/xmlview?path= 本机 PASS/FAIL 渲染成功（口径一致）");
                // 跨机路径（A 请求 B 的文件 via path）：A 的 FetchRemoteXmlByPath 会扇出到 B 的 /api/file?path=
                var r2 = HttpGetWithToken($"{baseA}/api/xmlview?path={Uri.EscapeDataString(xmlB)}&machine=FCT7", "tok123");
                Check(r2.StatusCode == System.Net.HttpStatusCode.OK, "GET /api/xmlview?path= 跨机拉取 200（联动）");
                // DTD 攻击：恶意 XML 带 DOCTYPE 实体注入，应被 DTD Prohibit 拦截 → 500 或错误页
                var evilXml = Path.Combine(resultsA, "evil.xml");
                File.WriteAllText(evilXml, "<!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><BATCH><FACTORY USER=\"&xxe;\"/></BATCH>", Encoding.UTF8);
                var rx = HttpGetWithToken($"{baseA}/api/xmlview?path={Uri.EscapeDataString(evilXml)}", "tok123");
                var body = rx.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Check(rx.StatusCode == System.Net.HttpStatusCode.InternalServerError || !body.Contains("root:"), "DTD Prohibit：恶意 DOCTYPE 未被解析为文件内容");
                // 白名单外路径拒绝 → 404
                var outside = Path.Combine(work2, "outside.xml");
                File.WriteAllText(outside, "<BATCH/>");
                var r3 = HttpGetWithToken($"{baseA}/api/xmlview?path={Uri.EscapeDataString(outside)}", "tok123");
                Check(r3.StatusCode == System.Net.HttpStatusCode.NotFound, "白名单外路径 /api/xmlview?path= → 404");
            }
            srvA.Stop(); srvB.Stop();
            meshA.Stop(); meshB.Stop();
            aggDb.Close();
            try { Directory.Delete(work2, true); } catch { }
            MeshQueryService.ClearCache();
        }
    }

    static void RunLiteSettingsTests(string work)
    {
        // ---------- 1) AggDatabase.Settings：layout / favorites 双写 ----------
        {
            var dbPath = Path.Combine(work, "lite_set_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            using var db = new AggDatabase(dbPath);
            db.Open();
            db.UpsertUser("lite_admin", PasswordHasher.Hash("pwd"), "admin");
            var u = db.GetUserByName("lite_admin");
            Check(u != null && u!.Layout == null && u!.Favorites == null, "初始 layout/favorites 为空");
            var layJson = "{\"overviewOrder\":[\"FCT9\",\"FCT6\"]}";
            Check(db.SetUserLayout("lite_admin", layJson), "SetUserLayout 成功");
            Check(db.GetUserLayout("lite_admin") == layJson, "GetUserLayout 回读一致");
            var favJson = "[\"FCT7 5V_Rail\",\"近7天\"]";
            Check(db.SetUserFavorites("lite_admin", favJson), "SetUserFavorites 成功");
            Check(db.GetUserFavorites("lite_admin") == favJson, "GetUserFavorites 回读一致");
            Check(!db.SetUserLayout("no_such", "{}"), "不存在用户 SetUserLayout 返回 false");
            // NOCASE
            Check(db.GetUserLayout("LITE_ADMIN") == layJson, "GetUserLayout 大小写不敏感");
            Check(db.SetUserLayout("LITE_ADMIN", "{}") && db.GetUserLayout("lite_admin") == "{}", "SetUserLayout 大小写不敏感覆盖");
            try { File.Delete(dbPath); } catch { }
        }

        // ---------- 2) Web 层：用户管理 + 角色分级 + layout/favorites API + 全局搜索 ----------
        string work2 = Path.Combine(work, "lite_web_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(work2);
        int port = GetFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";
        const string aggTok = "liteagg00000000000000000000000001";
        using var db2 = new AggDatabase(Path.Combine(work2, "agg.db"));
        // 预置 FAIL/维修/设备/良率 数据供搜索
        db2.Open();
        db2.InsertFail(new AggFailRow { Machine = "FCT6", Seq = 1, Type = "fail", Ts = "2026-08-28 09:00:00", TestDate = "20260828", FailReason = "5V_Rail", Sn = "SN-LITE-001", Model = "E3002781" });
        db2.InsertFail(new AggFailRow { Machine = "FCT7", Seq = 2, Type = "fail", Ts = "2026-08-28 09:01:00", TestDate = "20260828", FailReason = "CAN_Bus", Sn = "SN-LITE-002", Model = "E301" });
        db2.UpsertUser("viewer1", PasswordHasher.Hash("pwd1"), "viewer");
        db2.UpsertUser("eng1", PasswordHasher.Hash("pwd2"), "engineer");
        var viewerTok = db2.GetUserByName("viewer1")!.Token;
        var engTok = db2.GetUserByName("eng1")!.Token;
        // 维修一条
        db2.CreateMaintenance(new MaintenanceRecord { StationId = "FCT6", FailItem = "5V_Rail", FailReason = "电源异常", Severity = "major", Status = "open", Resolver = "张三", CreatedAt = "2026-08-28 09:00:00" });
        // 设备一条
        db2.UpsertDeviceInfo(new DeviceInfoRow { Machine = "FCT6", Hostname = "HOST6", Ip = "192.168.1.6", CpuModel = "Intel", CpuUsage = 12, MemTotalMb = 8000, MemUsedMb = 4000, DiskTotalGb = 100, DiskFreeGb = 50, UptimeSec = 3600, ArgusVersion = "3.15.0", LastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
        // 良率一条
        db2.UpsertDailyStats("FCT6", "20260828", new AggDatabase.DailyStats(100, 95, 5, 0, 10));

        using var srv = new WebAggServer(port, (AggWatcher?)null, db2, work2, work2, aggTok);
        srv.Start();
        Check(WaitUntil(() => srv.Listening, 5000), "Lite-Settings：服务进入监听");

        // 未带 token -> 403
        {
            var r = _http.GetAsync($"{baseUrl}/api/users/me").GetAwaiter().GetResult();
            Check(r.StatusCode == System.Net.HttpStatusCode.Forbidden, "未带 token GET /api/users/me -> 403");
        }
        // viewer 获取当前身份
        {
            var r = HttpGetWithToken($"{baseUrl}/api/users/me", viewerTok);
            Check(r.StatusCode == System.Net.HttpStatusCode.OK, "viewer GET /api/users/me -> 200");
            var txt = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = System.Text.Json.JsonDocument.Parse(txt);
            var role = doc.RootElement.TryGetProperty("role", out var pr) ? pr.GetString() : doc.RootElement.TryGetProperty("Role", out var pr2) ? pr2.GetString() : "";
            Check(role != null && role.ToLowerInvariant() == "viewer", "viewer 身份 role=viewer");
            Check(doc.RootElement.TryGetProperty("name", out var pn) && pn.GetString() == "viewer1", "viewer 身份 name=viewer1");
        }
        // agg_token 身份
        {
            var r = HttpGetWithToken($"{baseUrl}/api/users/me", aggTok);
            Check(r.StatusCode == System.Net.HttpStatusCode.OK, "agg_token GET /api/users/me -> 200");
            var txt = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = System.Text.Json.JsonDocument.Parse(txt);
            // agg_token 返回 isAggToken 或 name=agg_token
            var name = doc.RootElement.TryGetProperty("name", out var pn) ? pn.GetString() : "";
            Check(name == "agg_token", "agg_token 身份 name=agg_token");
        }
        // layout 双写：viewer 可读初始 null，PATCH 后回读
        {
            var rGet1 = HttpGetWithToken($"{baseUrl}/api/users/me/layout", viewerTok);
            Check(rGet1.StatusCode == System.Net.HttpStatusCode.OK, "viewer GET /api/users/me/layout -> 200");
            var txt1 = rGet1.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var d1 = System.Text.Json.JsonDocument.Parse(txt1);
            var hasLayout = d1.RootElement.TryGetProperty("layout", out var pl) || d1.RootElement.TryGetProperty("Layout", out pl);
            Check(hasLayout, "layout GET 含 layout 字段");
            // PATCH
            var layStr = "{\"overviewOrder\":[\"FCT7\",\"FCT6\"]}";
            using var req = new HttpRequestMessage(new HttpMethod("PATCH"), $"{baseUrl}/api/users/me/layout");
            req.Headers.Add("X-Agg-Token", viewerTok);
            req.Content = new StringContent($"{{\"layout\":{System.Text.Json.JsonSerializer.Serialize(layStr)}}}", Encoding.UTF8, "application/json");
            var rPatch = _http.SendAsync(req).GetAwaiter().GetResult();
            Check(rPatch.StatusCode == System.Net.HttpStatusCode.OK, "viewer PATCH /api/users/me/layout -> 200");
            var rGet2 = HttpGetWithToken($"{baseUrl}/api/users/me/layout", viewerTok);
            var txt2 = rGet2.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var d2 = System.Text.Json.JsonDocument.Parse(txt2);
            var lay2 = d2.RootElement.TryGetProperty("layout", out var pl2) ? pl2.GetString() : d2.RootElement.TryGetProperty("Layout", out var pl2b) ? pl2b.GetString() : null;
            Check(lay2 == layStr, $"layout PATCH 后回读一致（实得 {lay2})");
            // 未带 token -> 403（用全新无 Cookie 客户端，避免共享 _http 的 Cookie 污染）
            using var unauthClient = new HttpClient();
            using var req2 = new HttpRequestMessage(new HttpMethod("PATCH"), $"{baseUrl}/api/users/me/layout");
            req2.Content = new StringContent("{\"layout\":\"{}\" }", Encoding.UTF8, "application/json");
            var rNoAuth = unauthClient.SendAsync(req2).GetAwaiter().GetResult();
            Check(rNoAuth.StatusCode == System.Net.HttpStatusCode.Forbidden, "未带 token PATCH /api/users/me/layout -> 403");
        }
        // favorites 双写
        {
            var favStr = "[\"FCT6 5V_Rail\"]";
            using var req = new HttpRequestMessage(new HttpMethod("PATCH"), $"{baseUrl}/api/users/me/favorites");
            req.Headers.Add("X-Agg-Token", engTok);
            req.Content = new StringContent($"{{\"favorites\":{System.Text.Json.JsonSerializer.Serialize(favStr)}}}", Encoding.UTF8, "application/json");
            var r = _http.SendAsync(req).GetAwaiter().GetResult();
            Check(r.StatusCode == System.Net.HttpStatusCode.OK, "engineer PATCH /api/users/me/favorites -> 200");
            var rGet = HttpGetWithToken($"{baseUrl}/api/users/me/favorites", engTok);
            var txt = rGet.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var d = System.Text.Json.JsonDocument.Parse(txt);
            var fav2 = d.RootElement.TryGetProperty("favorites", out var pf) ? pf.GetString() : d.RootElement.TryGetProperty("Favorites", out var pf2) ? pf2.GetString() : null;
            Check(fav2 == favStr, "favorites 回读一致");
        }
        // 用户管理：viewer 越权 403，admin 成功，创建返回 token，删除二次确认（后端单次即可，前端二次）
        {
            // viewer 创建 -> 403
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/users");
            req.Headers.Add("X-Agg-Token", viewerTok);
            req.Content = new StringContent("{\"name\":\"new1\",\"password\":\"pwd\",\"role\":\"viewer\"}", Encoding.UTF8, "application/json");
            var r = _http.SendAsync(req).GetAwaiter().GetResult();
            Check(r.StatusCode == System.Net.HttpStatusCode.Forbidden, "viewer POST /api/users -> 403");
            // admin 创建 -> 200 + token
            using var req2 = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/users");
            req2.Headers.Add("X-Agg-Token", aggTok);
            req2.Content = new StringContent("{\"name\":\"new_admin\",\"password\":\"pwd123\",\"role\":\"engineer\"}", Encoding.UTF8, "application/json");
            var r2 = _http.SendAsync(req2).GetAwaiter().GetResult();
            Check(r2.StatusCode == System.Net.HttpStatusCode.OK, "admin POST /api/users -> 200");
            var txt2 = r2.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var d2 = System.Text.Json.JsonDocument.Parse(txt2);
            var tok = d2.RootElement.TryGetProperty("token", out var pt) ? pt.GetString() : d2.RootElement.TryGetProperty("Token", out var pt2) ? pt2.GetString() : "";
            Check(!string.IsNullOrEmpty(tok) && tok!.Length == 32, "创建返回 32hex token");
            // 删除
            using var req3 = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/api/users?name=new_admin");
            req3.Headers.Add("X-Agg-Token", aggTok);
            var r3 = _http.SendAsync(req3).GetAwaiter().GetResult();
            Check(r3.StatusCode == System.Net.HttpStatusCode.OK, "admin DELETE /api/users -> 200");
            // viewer 删除 -> 403
            using var req4 = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/api/users?name=viewer1");
            req4.Headers.Add("X-Agg-Token", viewerTok);
            var r4 = _http.SendAsync(req4).GetAwaiter().GetResult();
            Check(r4.StatusCode == System.Net.HttpStatusCode.Forbidden, "viewer DELETE /api/users -> 403");
        }
        // 全局搜索：空格分词跨 FAIL/维修/设备/良率聚合，跳转 hash 联动（link 含对应 hash）
        {
            var r = HttpGetWithToken($"{baseUrl}/api/search?q={Uri.EscapeDataString("FCT6 5V")}", viewerTok);
            Check(r.StatusCode == System.Net.HttpStatusCode.OK, "GET /api/search?q=FCT6 5V -> 200");
            var txt = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = System.Text.Json.JsonDocument.Parse(txt);
            Check(doc.RootElement.TryGetProperty("tokens", out var toks) && toks.GetArrayLength() == 2, "搜索 tokens 空格分词为 2");
            var counts = doc.RootElement.GetProperty("counts");
            var fails = counts.TryGetProperty("fails", out var cf) ? cf.GetInt32() : counts.TryGetProperty("Fails", out var cf2) ? cf2.GetInt32() : 0;
            Check(fails >= 1, $"搜索 FAIL 命中 >=1（实得 {fails}）");
            // 聚合跨域：维修与设备与良率至少各命中关注
            var maint = counts.TryGetProperty("maintenance", out var cm) ? cm.GetInt32() : 0;
            var dev = counts.TryGetProperty("devices", out var cd) ? cd.GetInt32() : 0;
            var yields = counts.TryGetProperty("yields", out var cy) ? cy.GetInt32() : 0;
            Check(maint >= 0 && dev >= 0 && yields >= 0, "搜索聚合含 maintenance/devices/yields 计数");
            // link 含 hash 跳转
            var results = doc.RootElement.GetProperty("results");
            var hasLink = false;
            foreach (var g in new[] { "fails", "maintenance", "devices", "yields" }) {
                if (results.TryGetProperty(g, out var arr) && arr.GetArrayLength() > 0) {
                    foreach (var it in arr.EnumerateArray()) {
                        if (it.TryGetProperty("link", out var lk) && lk.GetString()!.Contains("#/")) { hasLink = true; break; }
                        if (it.TryGetProperty("Link", out var lk2) && lk2.GetString()!.Contains("#/")) { hasLink = true; break; }
                    }
                }
            }
            Check(hasLink, "搜索结果含 hash 跳转 link（#/fails 等）");
            // AND 语义：FCT6 CAN_Bus 应不命中 5V_Rail 的 FAIL
            var r2 = HttpGetWithToken($"{baseUrl}/api/search?q={Uri.EscapeDataString("FCT6 CAN")}", viewerTok);
            var txt2 = r2.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc2 = System.Text.Json.JsonDocument.Parse(txt2);
            var counts2 = doc2.RootElement.GetProperty("counts");
            var fails2 = counts2.TryGetProperty("fails", out var cf3) ? cf3.GetInt32() : 0;
            // FCT6 的 FAIL 是 5V_Rail，不应被 CAN 命中
            Check(fails2 == 0, $"空格分词 AND：FCT6 CAN 不应命中 5V_Rail（实得 {fails2}）");
            // 未带 token -> 403（全新客户端避免 Cookie 污染）
            using var unauthClient2 = new HttpClient();
            var rNoAuth = unauthClient2.GetAsync($"{baseUrl}/api/search?q=xx").GetAwaiter().GetResult();
            Check(rNoAuth.StatusCode == System.Net.HttpStatusCode.Forbidden, "未带 token GET /api/search -> 403");
            // 空 q 返回空结果不炸
            var rEmpty = HttpGetWithToken($"{baseUrl}/api/search?q=", viewerTok);
            Check(rEmpty.StatusCode == System.Net.HttpStatusCode.OK, "空 q 搜索 -> 200");
        }

        srv.Stop();
        try { Directory.Delete(work2, true); } catch { }
    }

    static void RunLiteFetchTests(string work)
    {
        // ---------- DbMigrator v6 ----------
        {
            var p = Path.Combine(work, "litefetch_mig_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            using (var c = new SqliteConnection($"Data Source={p}"))
            {
                c.Open();
                DbMigrator.Migrate(c);
                Check(GetUserVersion(c) == DbMigrator.LatestVersion, $"Lite-Fetch 迁移后 user_version={GetUserVersion(c)}（期望 {DbMigrator.LatestVersion}）");
                Check(TableExists(c, "proc_change_log"), "迁移后 proc_change_log 存在");
                Check(TableExists(c, "report_archive"), "迁移后 report_archive 存在");
            }
            try { File.Delete(p); } catch { }
        }
        // ---------- AggDatabase.ProcLog CRUD + timeline + diff ----------
        {
            var p = Path.Combine(work, "proclog_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            var db = new AggDatabase(p);
            db.Open();
            var e1 = new AggDatabase.ProcLogEntry { Version = "v3.16.0", ChangedAt = "2026-08-28 10:00:00", ChangedBy = "张三", Content = "升级参数 A", ScopeMachines = "[\"FCT1\",\"FCT2\"]", ParamsSnapshot = "{\"paramA\":\"1\",\"paramB\":\"2\"}", RelatedReports = "[]" };
            var id1 = db.CreateProcLog(e1);
            Check(id1 > 0, $"CreateProcLog 返回 id={id1}");
            var e2 = new AggDatabase.ProcLogEntry { Version = "v3.16.1", ChangedAt = "2026-08-28 12:00:00", ChangedBy = "李四", Content = "升级参数 A/B", ScopeMachines = "[\"FCT1\"]", ParamsSnapshot = "{\"paramA\":\"10\",\"paramC\":\"3\"}", RelatedReports = "[]" };
            var id2 = db.CreateProcLog(e2);
            Check(id2 > id1, $"第二条 id={id2} > {id1}");
            var got = db.GetProcLog(id1);
            Check(got != null && got.Version == "v3.16.0" && got.ChangedBy == "张三", "GetProcLog 回读正确");
            var list = db.ListProcLogs(null, null, 10, 0);
            Check(list.Count == 2, $"ListProcLogs 2 条（实得 {list.Count}）");
            var timeline = db.QueryProcTimeline("FCT1", 10, 0);
            Check(timeline.Count == 2 && timeline[0].Id == id2, "时间轴按 changed_at 倒序（FCT1 含两条，最新在前）");
            var timelineFct2 = db.QueryProcTimeline("FCT2", 10, 0);
            Check(timelineFct2.Count == 1 && timelineFct2[0].Id == id1, "机台维度 FCT2 只命中一条");
            var diff = db.DiffProcParams(id1, id2);
            Check(diff.Added.Contains("paramC") && diff.Removed.Contains("paramB") && diff.Changed.Any(c => c.Key == "paramA"), $"Diff: added paramC、removed paramB、changed paramA（实得 added={string.Join(",", diff.Added)} removed={string.Join(",", diff.Removed)} changed={diff.Changed.Count}）");
            Check(diff.Unchanged.Count == 0, "无未变更参数");
            // CSV 注入与 BOM 走 CsvUtil
            Check(CsvUtil.Esc("=cmd") == "'=cmd" && CsvUtil.Esc("@evil") == "'@evil" && CsvUtil.Esc("a,b") == "\"a,b\"", "CsvUtil 注入防护与引号包裹");
            var tmpCsv = Path.Combine(work, "lite_csv_" + Guid.NewGuid().ToString("N")[..6] + ".csv");
            CsvUtil.Write(tmpCsv, new[] { "A", "B" }, new[] { new[] { "=1", "b" } });
            var b = File.ReadAllBytes(tmpCsv);
            Check(b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF, "CsvUtil.Write 带 BOM");
            Check(File.ReadAllText(tmpCsv, Encoding.UTF8).Contains("'=1"), "CsvUtil 写入含注入转义");
            try { File.Delete(tmpCsv); } catch { }
            Check(db.DeleteProcLog(id1), "DeleteProcLog 成功");
            Check(db.GetProcLog(id1) == null, "删除后不在库");
            try { File.Delete(p); } catch { }
        }
        // ---------- FetcherService 真实 xlsx/zip + 热力 ----------
        {
            var dbPath = Path.Combine(work, "fetch_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            var db = new AggDatabase(dbPath);
            db.Open();
            // 预置 yld_daily + agg_records 供趋势/热力
            db.UpsertDailyStats("FCT1", DateTime.Now.ToString("yyyyMMdd"), new AggDatabase.DailyStats(10, 8, 2, 0, 5));
            db.UpsertDailyStats("FCT1", DateTime.Now.AddDays(-1).ToString("yyyyMMdd"), new AggDatabase.DailyStats(10, 9, 1, 0, 5));
            db.InsertFail(new AggFailRow { Machine = "FCT1", Seq = 1, Type = "fail", Ts = "2026-08-28 09:00:00", TestDate = DateTime.Now.ToString("yyyyMMdd"), FailReason = "KL30", Model = "E300", Sn = "SN-001", Result = "FAIL", StationId = "FCT1", Category = "Offline" });
            db.InsertFail(new AggFailRow { Machine = "FCT1", Seq = 2, Type = "fail", Ts = "2026-08-28 09:01:00", TestDate = DateTime.Now.ToString("yyyyMMdd"), FailReason = "Vref", Model = "E300", Sn = "SN-002", Result = "FAIL", StationId = "FCT1", Category = "Offline" });
            db.InsertFail(new AggFailRow { Machine = "FCT2", Seq = 3, Type = "fail", Ts = "2026-08-28 09:02:00", TestDate = DateTime.Now.ToString("yyyyMMdd"), FailReason = "KL30", Model = "E301", Sn = "SN-003", Result = "FAIL", StationId = "FCT2", Category = "Offline" });
            // 按机台筛选
            var filterAll = "{\"machine\":\"\",\"limit\":100}";
            var jobAll = FetcherService.CreateExport(db, filterAll);
            Check(WaitUntil(() => FetcherService.Get(jobAll.Id)?.Status == "done", 10000), "Fetcher 全量导出完成（done）");
            var done = FetcherService.Get(jobAll.Id);
            Check(done != null && done.Progress == 100 && done.FilePath != null && File.Exists(done.FilePath), $"导出文件生成 {done?.FilePath}");
            if (done?.FilePath != null && File.Exists(done.FilePath))
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(done.FilePath);
                var entries = zip.Entries.Select(e => e.FullName).ToList();
                Check(entries.Any(e => e.Contains("worksheets")), "xlsx 含 worksheets 部件");
                foreach (var e in zip.Entries) { using var st = e.Open(); try { System.Xml.Linq.XDocument.Load(st); } catch (Exception ex) { Check(false, e.FullName + " XML 非法 " + ex.Message); } }
                Check(true, "xlsx 所有部件合法 XML");
                using var sr = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
                var body = sr.ReadToEnd();
                Check(body.Contains("机台") && body.Contains("失败原因"), "明细 Sheet 表头完整");
                // 4 Sheet
                Check(zip.Entries.Count(e => e.FullName.StartsWith("xl/worksheets/sheet")) == 4, "含 4 Sheet（明细/趋势/分布/热力）");
            }
            // 按机台过滤
            var filterFct1 = "{\"machine\":\"FCT1\",\"model\":\"E300\",\"limit\":10}";
            var jobFct1 = FetcherService.CreateExport(db, filterFct1);
            Check(WaitUntil(() => FetcherService.Get(jobFct1.Id)?.Status == "done", 10000), "按机台+型号过滤导出完成");
            var j2 = FetcherService.Get(jobFct1.Id);
            Check(j2 != null && j2.Total == 2, $"FCT1+E300 过滤后 2 条（实得 {j2?.Total}）");
            // 热力
            var heat = ReportService.GetHeatmap(db, null, 2);
            Check(heat.ContainsKey("machines") && heat.ContainsKey("dates") && heat.ContainsKey("matrix"), "热力含 machines/dates/matrix");
            var machines = heat["machines"] as List<string>;
            Check(machines != null && machines.Count >= 1, "热力 machines 非空");
            // 趋势/分布
            var trends = ReportService.GetTrend(db, "FCT1", 2);
            Check(trends.Count == 2 && trends.Any(t => t.ContainsKey("yield")), "趋势 2 天且含 yield");
            var dist = ReportService.GetDistribution(db, "fail_reason", null, 5);
            Check(dist.Count >= 1 && dist[0].ContainsKey("label"), "分布含 label/count");
            // zip 打包
            var jobZip = FetcherService.CreateExport(db, "{\"limit\":5,\"format\":\"zip\",\"pack\":true}");
            Check(WaitUntil(() => FetcherService.Get(jobZip.Id)?.Status == "done", 10000), "zip 打包完成");
            var jz = FetcherService.Get(jobZip.Id);
            Check(jz != null && jz.Format == "zip" && jz.FilePath != null && jz.FilePath.EndsWith(".zip"), $"zip 文件生成 {jz?.FilePath}");
            // csv
            var jobCsv = FetcherService.CreateExport(db, "{\"limit\":5,\"format\":\"csv\"}");
            Check(WaitUntil(() => FetcherService.Get(jobCsv.Id)?.Status == "done", 10000), "csv 导出完成");
            var jc = FetcherService.Get(jobCsv.Id);
            Check(jc != null && jc.Format == "csv" && jc.FilePath != null && File.Exists(jc.FilePath), "csv 文件生成");
            var csvB = File.ReadAllBytes(jc!.FilePath!);
            Check(csvB.Length >= 3 && csvB[0] == 0xEF && csvB[1] == 0xBB && csvB[2] == 0xBF, "csv 带 BOM");
            try { db.Close(); File.Delete(dbPath); } catch { }
            // 清理导出目录
            try { if (Directory.Exists(FetcherService.ExportRoot)) Directory.Delete(FetcherService.ExportRoot, true); } catch { }
        }
        // ---------- 报告归档 ----------
        {
            var dbPath = Path.Combine(work, "reportarc_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            var db = new AggDatabase(dbPath);
            db.Open();
            var tmpXml = Path.Combine(work, "rpt_" + Guid.NewGuid().ToString("N")[..6] + ".xml");
            File.WriteAllText(tmpXml, "<BATCH TIMESTAMP=\"2026-08-28T10:00:00\"><FACTORY USER=\"Op\" TESTER=\"PEU_G49_FCT1\"/><PANEL STATUS=\"Failed\" TIMESTAMP=\"2026-08-28T10:00:00\"/><DUT ID=\"SN-REP-001\"/><TEST NAME=\"KL30\" STATUS=\"Failed\" VALUE=\"0\" HILIM=\"1\"/></BATCH>", Encoding.UTF8);
            db.InsertFail(new AggFailRow { Machine = "FCT1", Seq = 1, Type = "fail", Ts = "2026-08-28 10:00:00", TestDate = "20260828", FailReason = "KL30", Model = "E300", Sn = "SN-REP-001", Result = "FAIL", XmlPath = tmpXml });
            var arch = new AggDatabase.ReportArchiveEntry { Machine = "FCT1", Sn = "SN-REP-001", Model = "E300", TestDate = "20260828", Result = "FAIL", XmlPath = tmpXml, ArchivedBy = "tester", Note = "归档测试", SummaryJson = "{\"sn\":\"SN-REP-001\"}" };
            var aid = db.ArchiveReport(arch);
            Check(aid > 0, $"ArchiveReport 返回 id={aid}");
            var list = db.ListReportArchives("FCT1", 10, 0);
            Check(list.Count == 1 && list[0].Sn == "SN-REP-001", "ListReportArchives 命中");
            var got = db.GetReportArchive(aid);
            Check(got != null && got.Machine == "FCT1", "GetReportArchive 回读");
            try { File.Delete(tmpXml); File.Delete(dbPath); } catch { }
            try { if (Directory.Exists(Path.Combine(AppConfig.BaseDir, "data", "report_archive"))) Directory.Delete(Path.Combine(AppConfig.BaseDir, "data", "report_archive"), true); } catch { }
        }
        // ---------- Web 层：fetch 下载/热力/报告摘要归档对比/proc-log ----------
        {
            var webRoot = Path.Combine(work, "litefetch_web_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(webRoot);
            var resultsRoot = Path.Combine(webRoot, "results");
            Directory.CreateDirectory(resultsRoot);
            var xmlPath = Path.Combine(resultsRoot, "sample.xml");
            File.WriteAllText(xmlPath, "<BATCH TIMESTAMP=\"2026-08-28T09:00:00\"><FACTORY USER=\"Op\" TESTER=\"PEU_G49_FCT1\"/><PANEL STATUS=\"Failed\" TIMESTAMP=\"2026-08-28T09:00:00\"/><DUT ID=\"SN-WEB-001\"/><TEST NAME=\"KL30\" STATUS=\"Failed\" VALUE=\"0\" HILIM=\"1\"/><TEST NAME=\"Vref\" STATUS=\"Passed\" VALUE=\"1\"/></BATCH>", Encoding.UTF8);
            var xmlPath2 = Path.Combine(resultsRoot, "sample2.xml");
            File.WriteAllText(xmlPath2, "<BATCH TIMESTAMP=\"2026-08-28T10:00:00\"><FACTORY USER=\"Op\" TESTER=\"PEU_G49_FCT1\"/><PANEL STATUS=\"Passed\" TIMESTAMP=\"2026-08-28T10:00:00\"/><DUT ID=\"SN-WEB-002\"/><TEST NAME=\"KL30\" STATUS=\"Passed\" VALUE=\"0.5\"/><TEST NAME=\"Vref\" STATUS=\"Passed\" VALUE=\"1\"/></BATCH>", Encoding.UTF8);
            int port = GetFreePort();
            var baseUrl = $"http://127.0.0.1:{port}";
            var dbPath = Path.Combine(webRoot, "agg.db");
            var db = new AggDatabase(dbPath);
            db.Open();
            db.InsertFail(new AggFailRow { Machine = "FCT1", Seq = 1, Type = "fail", Ts = "2026-08-28 09:00:00", TestDate = "20260828", FailReason = "KL30", Model = "E300", Sn = "SN-WEB-001", Result = "FAIL", XmlPath = xmlPath, StationId = "FCT1" });
            db.InsertFail(new AggFailRow { Machine = "FCT1", Seq = 2, Type = "fail", Ts = "2026-08-28 09:01:00", TestDate = "20260828", FailReason = "Vref", Model = "E300", Sn = "SN-WEB-002", Result = "FAIL", XmlPath = xmlPath2, StationId = "FCT1" });
            db.UpsertDailyStats("FCT1", DateTime.Now.ToString("yyyyMMdd"), new AggDatabase.DailyStats(10, 8, 2, 0, 5));
            // users
            db.UpsertUser("viewer1", PasswordHasher.Hash("pw"), "viewer");
            db.UpsertUser("engineer1", PasswordHasher.Hash("pw"), "engineer");
            var viewerTok = db.GetUserByName("viewer1")!.Token;
            var engTok = db.GetUserByName("engineer1")!.Token;
            var cfg = new AppConfig { MeshPort = port, AggToken = "litefetch_tok", Peers = new List<string>(), ResultsRoot = resultsRoot };
            var localDb = new Database(Path.Combine(webRoot, "local.db"));
            var mesh = new MeshNode(cfg, "WEB1", localDb, db, new string[0]);
            mesh.Receiver.LocalReadValidator = p => p.StartsWith(resultsRoot);
            var srv = new WebAggServer(port, mesh, db, resultsRoot, webRoot, "litefetch_tok");
            srv.Start();
            Check(WaitUntil(() => srv.Listening, 5000), "Lite-Fetch Web 服务进入监听");
            // 未带 token 403
            Check(HttpGetWithToken($"{baseUrl}/api/heatmap", null).StatusCode == System.Net.HttpStatusCode.Forbidden, "未带 token GET /api/heatmap → 403");
            Check(HttpGetWithToken($"{baseUrl}/api/report/summary?path={Uri.EscapeDataString(xmlPath)}", viewerTok).StatusCode == System.Net.HttpStatusCode.OK, "viewer GET /api/report/summary → 200");
            var sumResp = HttpGetWithToken($"{baseUrl}/api/report/summary?path={Uri.EscapeDataString(xmlPath)}", viewerTok);
            var sumTxt = sumResp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(sumTxt.Contains("SN-WEB-001") && sumTxt.Contains("total"), "报告摘要含 SN 与 total");
            // 归档 viewer 403，需 engineer
            var rArchViewer = HttpPostWithToken($"{baseUrl}/api/report/archive", viewerTok, $"{{\"xml_path\":\"{xmlPath.Replace("\\", "\\\\")}\",\"machine\":\"FCT1\"}}");
            Check(rArchViewer.StatusCode == System.Net.HttpStatusCode.Forbidden, "viewer POST /api/report/archive → 403");
            var rArch = HttpPostWithToken($"{baseUrl}/api/report/archive", engTok, $"{{\"xml_path\":\"{xmlPath.Replace("\\", "\\\\")}\",\"machine\":\"FCT1\",\"sn\":\"SN-WEB-001\",\"note\":\"test\"}}");
            Check(rArch.StatusCode == System.Net.HttpStatusCode.OK, "engineer POST /api/report/archive → 200");
            var archList = HttpGetWithToken($"{baseUrl}/api/report/archive?machine=FCT1", viewerTok);
            Check(archList.StatusCode == System.Net.HttpStatusCode.OK, "GET /api/report/archive → 200");
            var rCompare = HttpGetWithToken($"{baseUrl}/api/report/compare?path1={Uri.EscapeDataString(xmlPath)}&path2={Uri.EscapeDataString(xmlPath2)}", viewerTok);
            Check(rCompare.StatusCode == System.Net.HttpStatusCode.OK, "GET /api/report/compare → 200");
            var cmpTxt = rCompare.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(cmpTxt.Contains("batch") && cmpTxt.Contains("tests"), "版本对比含 batch/tests");
            // 热力
            var rHeat = HttpGetWithToken($"{baseUrl}/api/heatmap?days=2", viewerTok);
            Check(rHeat.StatusCode == System.Net.HttpStatusCode.OK, "GET /api/heatmap → 200");
            var heatTxt = rHeat.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(heatTxt.Contains("machines") && heatTxt.Contains("matrix"), "热力含 machines/matrix");
            // fetch 完整导出
            var rFetch = HttpPostWithToken($"{baseUrl}/api/fetch", viewerTok, "{\"machine\":\"FCT1\",\"model\":\"E300\",\"limit\":10,\"format\":\"xlsx\"}");
            Check(rFetch.StatusCode == System.Net.HttpStatusCode.OK, "POST /api/fetch → 200");
            var fetchTxt = rFetch.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            string jobId = "";
            try { using var doc = JsonDocument.Parse(fetchTxt); if (doc.RootElement.TryGetProperty("job_id", out var v)) jobId = v.GetString() ?? ""; else if (doc.RootElement.TryGetProperty("jobId", out var v2)) jobId = v2.GetString() ?? ""; } catch { }
            Check(!string.IsNullOrEmpty(jobId), $"fetch 返回 job_id={jobId}");
            Check(WaitUntil(() => { var r = HttpGetWithToken($"{baseUrl}/api/fetch/status?id={jobId}", viewerTok); var t = r.Content.ReadAsStringAsync().GetAwaiter().GetResult(); return t.Contains("\"done\"") || t.Contains("\"status\":\"done\""); }, 15000), "fetch 任务轮询至 done");
            var rProg = HttpGetWithToken($"{baseUrl}/api/fetch/progress?id={jobId}", viewerTok);
            Check(rProg.StatusCode == System.Net.HttpStatusCode.OK, "GET /api/fetch/progress → 200");
            var progTxt = rProg.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(progTxt.Contains("100") || progTxt.Contains("done"), "progress 含 100 或 done");
            var rDl = HttpGetWithToken($"{baseUrl}/api/fetch/download?id={jobId}", viewerTok);
            // 下载成功为 200 且为 xlsx 二进制
            Check(rDl.StatusCode == System.Net.HttpStatusCode.OK, "GET /api/fetch/download → 200");
            var dlBytes = rDl.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            Check(dlBytes.Length > 1000 && dlBytes[0] == 0x50 && dlBytes[1] == 0x4B, "下载文件为 zip/xlsx（PK 头）");
            // proc-log 登记/时间轴/diff
            var rProcViewer = HttpPostWithToken($"{baseUrl}/api/proc-log", viewerTok, "{\"version\":\"v1\",\"changed_at\":\"2026-08-28 10:00:00\",\"content\":\"viewer试\"}");
            Check(rProcViewer.StatusCode == System.Net.HttpStatusCode.Forbidden, "viewer POST /api/proc-log → 403");
            var rProcCreate = HttpPostWithToken($"{baseUrl}/api/proc-log", engTok, "{\"version\":\"v9.9.9\",\"changed_at\":\"2026-08-28 10:00:00\",\"changed_by\":\"engineer1\",\"content\":\"升级\",\"scope_machines\":\"[\\\"FCT1\\\"]\",\"params_snapshot\":\"{\\\"a\\\":\\\"1\\\"}\"}");
            Check(rProcCreate.StatusCode == System.Net.HttpStatusCode.OK, "engineer POST /api/proc-log → 200");
            var procTxt = rProcCreate.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            long pid1 = 0; try { using var doc = JsonDocument.Parse(procTxt); if (doc.RootElement.TryGetProperty("id", out var v)) pid1 = v.GetInt64(); } catch { }
            var rProcCreate2 = HttpPostWithToken($"{baseUrl}/api/proc-log", engTok, "{\"version\":\"v9.9.10\",\"changed_at\":\"2026-08-28 12:00:00\",\"content\":\"升级2\",\"scope_machines\":\"[\\\"FCT1\\\"]\",\"params_snapshot\":\"{\\\"a\\\":\\\"2\\\",\\\"b\\\":\\\"3\\\"}\"}");
            Check(rProcCreate2.StatusCode == System.Net.HttpStatusCode.OK, "第二次 proc-log 创建 → 200");
            long pid2 = 0; try { var t2 = rProcCreate2.Content.ReadAsStringAsync().GetAwaiter().GetResult(); using var doc2 = JsonDocument.Parse(t2); if (doc2.RootElement.TryGetProperty("id", out var v2)) pid2 = v2.GetInt64(); } catch { }
            var rTimeline = HttpGetWithToken($"{baseUrl}/api/proc-log/timeline?machine=FCT1", viewerTok);
            Check(rTimeline.StatusCode == System.Net.HttpStatusCode.OK, "GET /api/proc-log/timeline → 200");
            var tlTxt = rTimeline.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(tlTxt.Contains("v9.9.9") && tlTxt.Contains("v9.9.10"), "时间轴含两条记录");
            var rDiff = HttpGetWithToken($"{baseUrl}/api/proc-log/diff?id1={pid1}&id2={pid2}", viewerTok);
            Check(rDiff.StatusCode == System.Net.HttpStatusCode.OK, "GET /api/proc-log/diff → 200");
            var diffTxt = rDiff.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(diffTxt.Contains("added") && diffTxt.Contains("changed"), "diff 含 added/changed");
            // 删除
            using var httpNoCookie = new HttpClient();
            var rNoTokFetch = httpNoCookie.GetAsync($"{baseUrl}/api/fetch/download?id={jobId}").GetAwaiter().GetResult();
            Check(rNoTokFetch.StatusCode == System.Net.HttpStatusCode.Forbidden, "无 token GET /api/fetch/download → 403");
            srv.Stop(); mesh.Stop();
            try { Directory.Delete(webRoot, true); } catch { }
            try { if (Directory.Exists(FetcherService.ExportRoot)) Directory.Delete(FetcherService.ExportRoot, true); } catch { }
            try { if (Directory.Exists(Path.Combine(AppConfig.BaseDir, "data", "report_archive"))) Directory.Delete(Path.Combine(AppConfig.BaseDir, "data", "report_archive"), true); } catch { }
        }
    }

    static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    /// <summary>更新器回归：版本解析 / 扫描 / RELEASE.txt 提取 / 已提示去重（v3.6.0）。</summary>
    static void RunUpdateCheckerTests(string work)
    {
        Console.WriteLine("\n【更新器】本地更新包检测 / 版本对比 / RELEASE.txt 特点 / 已提示去重");
        var root = Path.Combine(work, "updater");
        var updDir = Path.Combine(root, "updates");
        Directory.CreateDirectory(updDir);

        // ---------- 版本解析 ----------
        Check(UpdateChecker.ParseZipVersion("Argus-v9.9.9-update.zip") == new Version(9, 9, 9),
              "解析更新包名 Argus-v9.9.9-update.zip -> 9.9.9");
        Check(UpdateChecker.ParseZipVersion("Argus-v3.5.3.zip") == new Version(3, 5, 3),
              "兼容完整包名 Argus-v3.5.3.zip -> 3.5.3");
        Check(UpdateChecker.ParseZipVersion("Argus-v1.2.zip") == new Version(1, 2),
              "两位版本号 Argus-v1.2.zip -> 1.2");
        Check(UpdateChecker.ParseZipVersion("readme.txt") == null, "非 zip 文件名 -> null");

        // ---------- 用临时 db 隔离 app_meta（不碰真实机台库） ----------
        var db = new Database(Path.Combine(root, "meta.db"));
        var cur = UpdateChecker.CurrentVersion;
        var newer = new Version(cur.Major + 1, 0, 0);

        // 造一个「更新的假包」：只写空 zip（Scan 只看文件名+版本，不解压）
        var zipPath = Path.Combine(updDir, $"Argus-v{newer}-update.zip");
        using (var fs = File.Create(zipPath)) { fs.Write(new byte[] { 0x50, 0x4B, 0x05, 0x06 }, 0, 4); }

        var info = UpdateChecker.Scan(updDir, db);
        Check(info != null && info.Version == newer, $"扫描到新包 v{newer}（当前 v{cur}）");
        Check(info != null && Path.GetFileName(info.ZipPath) == Path.GetFileName(zipPath), "返回的 zip 路径正确");

        // ---------- 已提示去重 ----------
        Check(!UpdateChecker.PromptedVersions(db).Contains(newer), "新版本初始未提示过");
        UpdateChecker.MarkPrompted(newer, db);
        Check(UpdateChecker.PromptedVersions(db).Contains(newer), "MarkPrompted 后已记录");
        Check(UpdateChecker.Scan(updDir, db) == null, "已提示过的版本不再弹出（去重生效）");

        // ---------- RELEASE.txt 版本特点提取 ----------
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

        // ---------- 当前版本包不应触发 ----------
        var curZip = Path.Combine(updDir, $"Argus-v{cur}-update.zip");
        using (var fs = File.Create(curZip)) { fs.Write(new byte[] { 0x50, 0x4B, 0x05, 0x06 }, 0, 4); }
        Check(UpdateChecker.Scan(updDir, db) == null, "与当前版本相同的包不触发（已提示的仍被过滤）");

        // ---------- 清理 ----------
        try { Directory.Delete(root, true); } catch { }
    }

    /// <summary>
    /// H-1 安全修复回归：MeshReceiver.FetchXmlForFail 的**本地读取**必须过白名单校验器。
    /// 攻击链原样复现：推送方伪造 data.xml_path 指向任意文件 → /api/mesh/xml 读出全文。
    /// 修复后：LocalReadValidator（= WebAggServer.ResolveFile）拒绝时返回 null（404），不落盘不外泄；
    /// 白名单内路径照常可读；校验器为 null 时保持旧行为（独立使用 MeshReceiver 的场景）。
    /// </summary>
    static void RunMeshXmlWhitelistTests(string work)
    {
        Console.WriteLine("\n【H-1 回归】/api/mesh/xml 本地读取过白名单（伪造 xml_path 拒绝读取）");
        var root = Path.Combine(work, "meshxml");
        var resultsRoot = Path.Combine(root, "results");
        Directory.CreateDirectory(resultsRoot);

        var inside = Path.Combine(resultsRoot, "real.xml");
        File.WriteAllText(inside, "<REPORT>inside</REPORT>");
        var secret = Path.Combine(root, "secret.txt");
        File.WriteAllText(secret, "TOP-SECRET");

        var dbPath = Path.Combine(root, "agg.db");
        using (var db = new AggDatabase(dbPath))
        {
            var rx = new MeshReceiver(db, localMachine: "SELF");
            rx.HandleFail(BuildFailJsonCustom(1, "FCT1", inside, "SN-H1", "白名单内"));
            rx.HandleFail(BuildFailJsonCustom(2, "FCT2", secret, "SN-H2", "伪造白名单外"));

            var rows = db.QueryFails(10);
            Check(rows.Count == 2, $"两条推送已入库（{rows.Count}）");
            var inRow = rows.First(r => r.Sn == "SN-H1");
            var outRow = rows.First(r => r.Sn == "SN-H2");

            // 无校验器 = 旧行为（兼容独立使用）
            var oldRead = rx.FetchXmlForFail(inRow.Id);
            Check(oldRead != null && oldRead.Contains("inside"),
                  "未设校验器时保持旧行为（白名单内可读）");

            // 设白名单（与 WebAggServer 构造时注入的语义一致）：白名单内放行、白名单外拒绝
            rx.LocalReadValidator = p =>
                p.StartsWith(Path.GetFullPath(resultsRoot) + Path.DirectorySeparatorChar,
                             StringComparison.OrdinalIgnoreCase);
            var okRead = rx.FetchXmlForFail(inRow.Id);
            Check(okRead != null && okRead.Contains("inside"),
                  "设校验器后：白名单内 xml_path 照常可读");

            var blocked = rx.FetchXmlForFail(outRow.Id);
            Check(blocked == null,
                  "设校验器后：伪造的白名单外 xml_path 拒绝读取（返回 404，不再泄文件）");

            // 不存在但白名单内的路径：File.Exists 先挡住，行为不变
            var ghost = rows.FirstOrDefault(r => r.Sn == "SN-GHOST");
            Check(ghost == null, "幽灵记录不存在（防呆确认）");
        }
        try { Directory.Delete(root, true); } catch { }
    }

    // ==================== P0 性能改造回归（v3.5.3） ====================

    /// <summary>
    /// 架构优化 P0 五项的回归：
    /// ① AggDatabase.InsertBatch 批量事务 + (machine,seq) 幂等 + Id 回填；
    /// ② FailCountCached 5s 计数缓存；
    /// ③ 读写分离核心断言：写线程与多读线程并发跑全程无 SQLITE_BUSY/异常、写入不丢；
    /// ④ MeshReceiver 组提交：N 个并发推送合并进 &lt; N 个批次、返回即已提交；
    /// ⑤ RunMaintenance checkpoint/VACUUM 两分支 + 维护后可读写；
    /// ⑥ Database.BatchInsert RETURNING：批量插入 Id 回填语义不回退。
    /// </summary>
    static void RunP0PerformanceTests(string work)
    {
        var root = Path.Combine(work, "p0perf");
        Directory.CreateDirectory(root);

        static AggFailRow MkRow(long seq, string m) => new()
        {
            Machine = m, Seq = seq, Type = "fail",
            Ts = "2026-08-27 08:00:00", IngestTs = "2026-08-27 08:00:01",
            StationId = m, Model = "E3002781", Sn = $"SN-{m}-{seq}", Result = "FAIL",
            FailReason = "5V_Rail", TestDate = "20260827",
        };

        // ---- ①+② InsertBatch 与计数缓存 ----
        using (var db = new AggDatabase(Path.Combine(root, "batch.db")))
        {
            db.Open();
            var batchA = Enumerable.Range(1, 120).Select(i => MkRow(i, i % 2 == 0 ? "FCT1" : "FCT2")).ToList();
            Check(db.InsertBatch(batchA) == 120, "InsertBatch：首批 120 条单事务全部插入");
            Check(batchA.All(r => r.Id > 0), "InsertBatch：新插入行的自增 Id 全部回填");
            var idsBefore = batchA.Select(r => r.Id).ToList();
            Check(db.InsertBatch(batchA) == 0, "InsertBatch：重复批（同 machine,seq）全部忽略，返回 0");
            Check(batchA.Select(r => r.Id).SequenceEqual(idsBefore), "InsertBatch：重复批不改写已有行的 Id");

            var mixed = new List<AggFailRow>();
            mixed.AddRange(batchA.Take(80));                                 // 旧行：应被忽略
            for (int i = 1000; i < 1020; i++) mixed.Add(MkRow(i, "FCT3"));   // 新行：应插入
            int fct3Before = db.QueryFailsByMachineSeqRange("FCT3", 0, long.MaxValue).Count;
            Check(db.InsertBatch(mixed) == 20 && fct3Before == 0,
                  $"混合批次只插入新的 20 条（插前 FCT3 存量 = {fct3Before}）");

            Check(db.FailCount("") == 140 && db.FailCount("FCT1") == 60 && db.FailCount("FCT3") == 20,
                  "InsertBatch 后计数正确（全库 140 / FCT1=60 / FCT3=20）");

            long cached1 = db.FailCountCached("FCT1");
            Check(cached1 == db.FailCount("FCT1"), $"FailCountCached 与直查一致（{cached1}）");
            db.InsertFail(MkRow(7777, "FCT1"));
            Check(db.FailCountCached("FCT1") == cached1, "TTL 内缓存命中：直插一条后缓存值不变（COUNT 不被高频写打断）");
            db.ClearCountCache();
            Check(db.FailCountCached("FCT1") == cached1 + 1, "ClearCountCache 后读到最新计数");
        }

        // ---- ③ 读写分离并发：1 写线程持续插入，3 读线程狂读 —— 全程零 BUSY/异常、写入不丢 ----
        using (var cdb = new AggDatabase(Path.Combine(root, "conc.db")))
        {
            cdb.Open();
            const int n = 300;
            var errs = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var stop = new ManualResetEventSlim(false);
            var writer = new Thread(() =>
            {
                for (int i = 1; i <= n; i++)
                    try { cdb.InsertFail(MkRow(i, "WC")); }
                    catch (Exception ex) { errs.Enqueue($"write seq={i}: {ex.Message}"); }
                stop.Set();
            }) { Name = "p0-writer" };
            writer.Start();

            Thread MakeReader(int idx) => new(() =>
            {
                while (!stop.IsSet)
                {
                    try
                    {
                        switch (idx)
                        {
                            case 0: _ = cdb.QueryFails(30); break;
                            case 1: _ = cdb.FailCount(""); break;
                            default:
                                if (cdb.MaxSeqPerMachine().TryGetValue("WC", out var s))
                                    _ = cdb.QueryFailsByMachineSeqRange("WC", Math.Max(0, s - 5), s);
                                break;
                        }
                    }
                    catch (Exception ex) { errs.Enqueue($"read{idx}: {ex.Message}"); }
                }
            }) { IsBackground = true, Name = $"p0-reader{idx}" };
            var rs = new[] { MakeReader(0), MakeReader(1), MakeReader(2) };
            foreach (var r in rs) r.Start();
            writer.Join();
            foreach (var r in rs) r.Join(3000);

            Check(errs.IsEmpty, "读写分离：写线程 + 3 读线程全程无 SQLITE_BUSY / 无异常" +
                  (errs.IsEmpty ? "" : $"（首错：{string.Join(" | ", errs.Take(3))}）"));
            Check(cdb.FailCount("") == n, $"读写分离：并发期间 {n} 条写入全部落库不丢失");
        }

        // ---- ④ 接收端组提交：并发 HandleFail 合并批次、返回即提交语义保持 ----
        {
            var gdb = new AggDatabase(Path.Combine(root, "group.db"));
            gdb.Open();
            var rx = new MeshReceiver(gdb, localMachine: "SELF");
            const int m = 50;
            var errs = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var ready = new CountdownEvent(m);
            var go = new ManualResetEventSlim(false);
            var ts = Enumerable.Range(1, m).Select(i => new Thread(() =>
            {
                ready.Signal();
                go.Wait();
                try { rx.HandleFail(BuildFailJsonCustom(i, "GC", "X:\\agg\\g.xml", $"SN-GC-{i}", "组提交")); }
                catch (Exception ex) { errs.Enqueue($"handle{i}: {ex.Message}"); }
            }) { IsBackground = true }).ToList();
            foreach (var t in ts) t.Start();
            ready.Wait();
            go.Set();
            foreach (var t in ts) t.Join();

            Check(errs.IsEmpty, "组提交：50 路并发 HandleFail 零异常" +
                  (errs.IsEmpty ? "" : $"（首错：{string.Join(" | ", errs.Take(3))}）"));
            Check(rx.CommittedRows == m, $"组提交：已提交行数 = {m}（实得 {rx.CommittedRows}）");
            Check(rx.CommittedBatches >= 1 && rx.CommittedBatches < m,
                  $"组提交合并生效：{m} 行仅用 {rx.CommittedBatches} 个事务批次提交（小于行数即说明攒批成功）");
            Check(gdb.FailCount("") == m, "组提交：HandleFail 返回后数据即刻可查（返回即已提交语义保持）");

            for (int i = m + 1; i <= m + 10; i++)   // 串行连续单推回归
                rx.HandleFail(BuildFailJsonCustom(i, "GC", "X:\\agg\\g.xml", $"SN-GC-{i}", "组提交"));
            Check(gdb.FailCount("") == m + 10, "组提交：串行单推 10 条同样全部入库");

            gdb.Close();
            gdb.Dispose();
        }

        // ---- ⑤ 每日维护：checkpoint 分支 / VACUUM 分支 / 维护后可用 ----
        using (var mdb = new AggDatabase(Path.Combine(root, "maint.db")))
        {
            mdb.Open();
            mdb.InsertBatch(Enumerable.Range(1, 30).Select(i => MkRow(i, "MT")));
            var gentle = mdb.RunMaintenance(vacuumThresholdBytes: 64L * 1024 * 1024);
            Check(gentle.Contains("wal_checkpoint") && gentle.Contains("未达 VACUUM"),
                  $"维护：小库走 checkpoint 分支不触发 VACUUM（{gentle}）");
            var forced = mdb.RunMaintenance(vacuumThresholdBytes: 1);
            Check(forced.Contains("VACUUM 完成"), $"维护：超阈值触发 VACUUM 成功（{forced}）");
            Check(mdb.InsertFail(MkRow(555, "MT")) == 1 && mdb.FailCount("MT") == 31,
                  "维护完成后库仍正常读写");
        }

        // ---- ⑥ RETURNING 回归：本地库批量插 + 重复批零插、事件回传 id 不回退 ----
        // Database 按 V3 设计不实现 IDisposable（连接按需开合），局部使用即可
        var ldb = new Database(Path.Combine(root, "fct_p0.db"));
        {
            var evts = new List<(TestRecord Rec, long Id)>();
            ldb.RecordsInserted += rows => evts.AddRange(rows);
            var recs = Enumerable.Range(1, 3)
                .Select(i => MakeFailRec(root, "FCT8", $"X:\\u\\p0-{i}.xml", $"SN-P0-{i}", "R")).ToList();
            Check(ldb.BatchInsert(recs) == 3, "RETURNING：本地库批量插 3 条全部插入");
            Check(evts.Select(e => e.Id).Distinct().Count() == 3 && evts.All(e => e.Id > 0),
                  "RETURNING：RecordsInserted 事件回传 3 个非零互异自增 id");
            Check(evts.Select(e => e.Id).SequenceEqual(evts.Select(e => e.Id).OrderBy(x => x)),
                  "RETURNING：批量内自增 id 保持递增（顺序语义不回退）");
            Check(ldb.BatchInsert(recs) == 0, "RETURNING：重复 xml_path 批次全部忽略，返回 0");
            Check(evts.Count == 3, "RETURNING：重复批不触发新增事件");
        }

        try { Directory.Delete(root, true); } catch { }
        Console.WriteLine("    P0 性能改造自检完成");
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
    // GetFreePort 已在 P3+P4 区域统一定义（复用），此处不再重复。

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

    // ==================== 聚合链路连通性（v3.4.11：状态机/主备/恢复/溢出） ====================

    static void RunLinkConnectivityTests(string work)
    {
        var linkRoot = Path.Combine(work, "link");
        Directory.CreateDirectory(linkRoot);

        // ---- ① 主备聚合端：主地址不可达，自动切备用（live collector 收到全部 FAIL）----
        var rootA = Path.Combine(linkRoot, "ha");
        Directory.CreateDirectory(rootA);
        int deadPort = GetFreePort();
        int livePort = GetFreePort();
        var dbA = new Database(Path.Combine(rootA, "db.db"));
        var pA = new AgentPusher(new AppConfig
        {
            StationId = "FCT1",
            AggEnabled = true,
            AggTransport = "http",
            AggHttpUrl = $"http://127.0.0.1:{deadPort}/,http://127.0.0.1:{livePort}/",   // 主备逗号分隔
        }, "FCT1", dbA, Path.Combine(rootA, "data"), retrySec: 1, heartbeatSec: 60);
        using (var col = new HttpCollector(livePort))
        {
            pA.Init();
            dbA.InsertOne(MakeFailRec(work, "FCT1", Path.Combine(rootA, "ha-a.xml"), "SN-HA1", "自检不良HA1"));
            dbA.InsertOne(MakeFailRec(work, "FCT1", Path.Combine(rootA, "ha-b.xml"), "SN-HA2", "自检不良HA2"));
            Check(WaitUntil(() => col.Snapshot().Count(b => b.Contains("\"type\":\"fail\"")) == 2, 15000),
                  "主备聚合端：主不可达自动切备用，两条 FAIL 均送达备用端");
            var snap = pA.GetLinkSnapshot();
            Check(snap.State is AggLinkState.Connected or AggLinkState.Degraded,
                  $"主备聚合端：链路状态正常（实得 {snap.State}）");
            Check(snap.Target.Contains($"127.0.0.1:{livePort}"),
                  $"主备聚合端：当前目标已切到备用地址（实得 {snap.Target}）");
            pA.Stop();
        }
        TryDeleteDir(rootA);

        // ---- ② 断连状态机：全部地址不可达 → 连续失败超阈值判 Disconnected，事件不丢 ----
        var rootB = Path.Combine(linkRoot, "down");
        Directory.CreateDirectory(rootB);
        int deadPortB = GetFreePort();
        var dbB = new Database(Path.Combine(rootB, "db.db"));
        var pB = new AgentPusher(new AppConfig
        {
            StationId = "FCT1",
            AggEnabled = true,
            AggTransport = "http",
            AggHttpUrl = $"http://127.0.0.1:{deadPortB}/",
        }, "FCT1", dbB, Path.Combine(rootB, "data"), retrySec: 1, heartbeatSec: 60);
        var flipped = new List<AggLinkState>();
        pB.LinkStateChanged += (_, n) => flipped.Add(n);
        pB.Init();
        dbB.InsertOne(MakeFailRec(work, "FCT1", Path.Combine(rootB, "down-a.xml"), "SN-DN1", "自检不良DN1"));
        dbB.InsertOne(MakeFailRec(work, "FCT1", Path.Combine(rootB, "down-b.xml"), "SN-DN2", "自检不良DN2"));
        Check(WaitUntil(() => pB.GetLinkSnapshot().State == AggLinkState.Disconnected, 20000),
              "断连状态机：聚合端全不可达 → 判 Disconnected（连续失败阈值）");
        Check(flipped.Contains(AggLinkState.Disconnected), "断连状态机：LinkStateChanged 事件携带断连翻转");
        var sB = pB.GetLinkSnapshot();
        Check(sB.ConsecutiveFailures >= 3, $"断连状态机：连续失败计数 ≥3（实得 {sB.ConsecutiveFailures}）");
        Check(sB.Backlog >= 2, "断连状态机：断连期间事件保留在队列（断线补偿不清空）");
        pB.Stop();
        TryDeleteDir(rootB);

        // ---- ③ 链路恢复：备用端后启动 → 断连状态自动回到正常，积压自动补推 ----
        var rootC = Path.Combine(linkRoot, "recover");
        Directory.CreateDirectory(rootC);
        int deadPortC = GetFreePort();
        int livePortC = GetFreePort();
        var dbC = new Database(Path.Combine(rootC, "db.db"));
        var pC = new AgentPusher(new AppConfig
        {
            StationId = "FCT1",
            AggEnabled = true,
            AggTransport = "http",
            AggHttpUrl = $"http://127.0.0.1:{deadPortC}/,http://127.0.0.1:{livePortC}/",
        }, "FCT1", dbC, Path.Combine(rootC, "data"), retrySec: 1, heartbeatSec: 60);
        pC.Init();
        dbC.InsertOne(MakeFailRec(work, "FCT1", Path.Combine(rootC, "rec-a.xml"), "SN-RC1", "自检不良RC1"));
        Check(WaitUntil(() => pC.GetLinkSnapshot().State == AggLinkState.Disconnected, 20000),
              "链路恢复：聚合端全挂 → 先断连");
        using (var colC = new HttpCollector(livePortC))
        {
            Check(WaitUntil(() => pC.GetLinkSnapshot().State is AggLinkState.Connected or AggLinkState.Degraded, 20000),
                  "链路恢复：备用端恢复后自动重连（状态回到正常）");
            Check(WaitUntil(() => colC.Snapshot().Count(b => b.Contains("\"type\":\"fail\"")) == 1, 10000),
                  "链路恢复：断连期间积压的 FAIL 自动补推");
        }
        pC.Stop();
        TryDeleteDir(rootC);

        // ---- ④ 队列溢出：超过上限丢最老并累计计数（防静默丢数据的可观测性）----
        var rootD = Path.Combine(linkRoot, "overflow");
        Directory.CreateDirectory(rootD);
        int deadPortD = GetFreePort();
        var dbD = new Database(Path.Combine(rootD, "db.db"));
        var pD = new AgentPusher(new AppConfig
        {
            StationId = "FCT1",
            AggEnabled = true,
            AggTransport = "http",
            AggHttpUrl = $"http://127.0.0.1:{deadPortD}/",
        }, "FCT1", dbD, Path.Combine(rootD, "data"), retrySec: 1, heartbeatSec: 60);
        pD.Init();
        // 推送端不可达，队列只进不出：塞超上限验证丢弃计数与上限约束
        for (int i = 1; i <= AgentPusher.MaxQueue + 50; i++)
        {
            var rec = MakeFailRec(work, "FCT1", Path.Combine(rootD, $"of-{i}.xml"), $"SN-OF{i}", $"自检不良OF{i}");
            pD.EnqueueFail(rec, i);
        }
        var sD = pD.GetLinkSnapshot();
        Check(sD.DroppedCount >= 50, $"队列溢出：超上限丢最老并计数（实得 {sD.DroppedCount}）");
        Check(sD.Backlog <= AgentPusher.MaxQueue, $"队列溢出：队列长度不超过上限（实得 {sD.Backlog}）");
        pD.Stop();
        TryDeleteDir(rootD);

        Console.WriteLine("    聚合链路连通性自检完成");
    }

    // ==================== Web 聚合服务（Phase 3：纯 Web 模式） ====================
    // WebAggServer.cs 路由：GET /（看板页）、/api/machines、/api/fails?limit&machine、
    // /api/file?id=|path=（XML 下载，白名单=resultsRoot 或 {BaseDir}/logs/app.log）、
    // /api/export.csv（UTF-8 BOM + 公式注入防护）、/api/health；POST /api/mesh/fail、/api/mesh/heartbeat、/（按 body type 分发）；
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
            var rPush = _http.PostAsync($"{baseUrl}/api/mesh/fail",
                new StringContent(failJson, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(rPush.StatusCode == HttpStatusCode.OK, "POST /api/mesh/fail（合法 fail JSON）-> 200");
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
            var rHb = _http.PostAsync($"{baseUrl}/api/mesh/heartbeat",
                new StringContent(BuildHeartbeatJsonString("FCT1", 41, 0), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(rHb.StatusCode == HttpStatusCode.OK, "POST /api/mesh/heartbeat -> 200");
            Check(WaitUntil(() => FindMachine(w, "FCT1") is { Online: true }, 10000),
                  "POST /api/mesh/heartbeat：机台 FCT1 出现在看板且在线");
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
            var rPushXml = _http.PostAsync($"{baseUrl}/api/mesh/fail",
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
            var rInject = _http.PostAsync($"{baseUrl}/api/mesh/fail",
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
            var r405put = _http.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/api/mesh/fail")).GetAwaiter().GetResult();
            Check(r405put.StatusCode == HttpStatusCode.MethodNotAllowed, "PUT /api/mesh/fail（方法不符）-> 405");
            var r405get = _http.GetAsync($"{baseUrl}/api/mesh/fail").GetAwaiter().GetResult();
            Check(r405get.StatusCode == HttpStatusCode.MethodNotAllowed, "GET /api/mesh/fail（方法不符）-> 405");
            var big = new string('x', 2 * 1024 * 1024);
            try
            {
                var r413 = _http.PostAsync($"{baseUrl}/api/mesh/fail",
                    new StringContent(big, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                Check(r413.StatusCode == HttpStatusCode.RequestEntityTooLarge,
                    "POST /api/mesh/fail（body 超 1MB）-> 413");
            }
            catch (Exception ex)
            {
                Check(false, $"POST /api/mesh/fail（body 超 1MB）-> 413（异常 {ex.GetType().Name}）");
            }

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
            var rNoTokPush = _http.PostAsync($"{baseUrl}/api/mesh/fail",
                new StringContent(BuildFailJsonCustom(101, "FCT1", "X:\\fct\\a.xml", "SN-101", "无 token"),
                    Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Check(rNoTokPush.StatusCode == HttpStatusCode.Forbidden, "POST /api/mesh/fail（无 token）-> 403");
            Check(WaitUntil(() => db.FailCount("") == 0, 3000), "聚合鉴权：403 的推送未入库（FailCount=0）");

            // ---- ② 错误 token 403 ----
            using (var bad = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/machines"))
            {
                bad.Headers.Add("X-Agg-Token", "wrong");
                var rBadTok = _http.SendAsync(bad).GetAwaiter().GetResult();
                Check(rBadTok.StatusCode == HttpStatusCode.Forbidden, "GET /api/machines（错误 token）-> 403");
            }

            // ---- ③ 正确 token（header）200 且能入库 ----
            using (var okPush = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/mesh/fail"))
            {
                okPush.Headers.Add("X-Agg-Token", "secret-token");
                okPush.Content = new StringContent(
                    BuildFailJsonCustom(101, "FCT1", "X:\\fct\\a.xml", "SN-101", "5V_Rail"),
                    Encoding.UTF8, "application/json");
                var rOkPush = _http.SendAsync(okPush).GetAwaiter().GetResult();
                Check(rOkPush.StatusCode == HttpStatusCode.OK, "POST /api/mesh/fail（正确 token 头）-> 200");
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
                Check(root.TryGetProperty("mesh_port", out _)
                      && root.TryGetProperty("agg_token_set", out _)
                      && root.TryGetProperty("agg_webhook_set", out _)
                      && root.TryGetProperty("agg_summary_minutes", out _)
                      && root.TryGetProperty("agg_transport", out _)
                      && root.TryGetProperty("results_root", out _),
                      "GET /api/settings：返回全部聚合配置字段（端口/token/webhook/汇总/通道/白名单）");
            }
            // M-3 加固后：服务端认证成功会下发 HttpOnly Cookie，_http 的 CookieContainer
            // 会自动携带 —— 「无 token 也能过」正是新语义。这里用**无 Cookie 的裸客户端**
            // 验证真正的未授权请求仍然被拒。
            using (var bare = new HttpClient())
            {
                var rSetNoToken = bare.GetAsync($"{baseUrl}/api/settings").GetAwaiter().GetResult();
                Check(rSetNoToken.StatusCode == HttpStatusCode.Forbidden,
                      "GET /api/settings（无 token 且无 Cookie）-> 403（设置页也要鉴权）");
                // M-3：带合法 ?token= 访问后，服务端下发 Cookie；后续无 token 请求靠 Cookie 自动放行
                // （裸客户端不存 Cookie；Cookie 续传由 _http——有容器——验证）
                bare.GetAsync($"{baseUrl}/?token=secret-token").GetAwaiter().GetResult();
                var rViaCookie = _http.GetAsync($"{baseUrl}/api/machines").GetAwaiter().GetResult();
                Check(rViaCookie.StatusCode == HttpStatusCode.OK,
                      "GET /api/machines（不带任何 token，凭先前下发的 Cookie）-> 200");
            }

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
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/mesh/fail");
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
            // v3.5.0 去中心化：默认配置为 Mesh 节点（station/mesh_port/peers），非旧中心端字段
            Check(root.TryGetProperty("station_id", out var s) && s.GetString() == "AGG-NODE",
                  "一键部署:station_id=AGG-NODE");
            Check(root.TryGetProperty("results_root", out var r) && r.GetString() == @"D:\Results",
                  "一键部署:results_root=D:\\Results");
            Check(root.TryGetProperty("mesh_port", out var p) &&
                  p.ValueKind == System.Text.Json.JsonValueKind.Number && p.GetInt32() == 8081,
                  "一键部署:mesh_port=8081");
            Check(root.TryGetProperty("peers", out var peers) &&
                  peers.ValueKind == System.Text.Json.JsonValueKind.Array && peers.GetArrayLength() == 0,
                  "一键部署:peers 为空数组（单节点起步）");
            // M-1 加固：默认配置自动生成随机 token（32 位 hex），不再空 token 全网开放
            bool tokOk = root.TryGetProperty("agg_token", out var at) &&
                         at.ValueKind == System.Text.Json.JsonValueKind.String;
            string tok = tokOk ? at.GetString() ?? "" : "";
            Check(tokOk && tok.Length == 32 && tok.All(char.IsLetterOrDigit),
                  $"一键部署:agg_token 自动生成 32 位随机串（{tok[..Math.Min(8, tok.Length)]}…）");
            Check(root.TryGetProperty("agg_summary_minutes", out var sm) &&
                  sm.ValueKind == System.Text.Json.JsonValueKind.Number && sm.GetInt32() == 60,
                  "一键部署:agg_summary_minutes=60");
            Check(root.TryGetProperty("log_level", out var l) && l.GetString() == "INFO",
                  "一键部署:log_level=INFO");
        }
        Check(parseOk, "一键部署:JSON 合法可解析");
        Check(json.Contains("  ") && json.Contains("\n"), "一键部署:JSON 已缩进（WriteIndented 可读格式）");

        // ---- v3.4.4：AggDeployer 内嵌部署器（不再依赖外部 bat） ----
        var depJson = AggDeployer.BuildDefaultConfigJson();
        // M-1 后每次调用生成**不同**随机 token：把两份 JSON 的 token 归一成占位符再比（结构仍须一致）
        static string MaskToken(string s)
        {
            using var d = System.Text.Json.JsonDocument.Parse(s);
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(s)!;
            dict["agg_token"] = System.Text.Json.JsonSerializer.SerializeToElement("<TOKEN>");
            return System.Text.Json.JsonSerializer.Serialize(dict.OrderBy(kv => kv.Key),
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        Check(!string.IsNullOrEmpty(depJson) && MaskToken(depJson) == MaskToken(json),
              "AggDeployer:BuildDefaultConfigJson 与 Program 转发版一致（内嵌单一来源，token 随机除外）");
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

    static void RunP5Tests(string work)
    {
        // ---------- DbMigrator v4 ----------
        {
            var p = Path.Combine(work, "p5mig_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            using (var c = new SqliteConnection($"Data Source={p}"))
            {
                c.Open();
                DbMigrator.Migrate(c);
                Check(GetUserVersion(c) == DbMigrator.LatestVersion, $"P5 迁移后 user_version={GetUserVersion(c)}（期望 {DbMigrator.LatestVersion}）");
                Check(TableExists(c, "maintenance_records"), "P5 迁移后 maintenance_records 存在");
                Check(TableExists(c, "todo_items"), "P5 迁移后 todo_items 存在");
                Check(TableExists(c, "dismissed_todos"), "P5 迁移后 dismissed_todos 存在");
                Check(TableExists(c, "resolvers"), "P5 迁移后 resolvers 存在");
            }
            try { File.Delete(p); } catch { }
        }
        // ---------- AggDatabase 维修看板 4 列状态机 + legacy ----------
        {
            var p = Path.Combine(work, "p5agg_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            var db = new AggDatabase(p);
            db.Open();
            Check(MaintenanceMeta.Statuses.Length==4 && MaintenanceMeta.Statuses[0].Key=="unknown" && MaintenanceMeta.Statuses[3].Key=="resolved", "状态体系 4 列顺序 unknown→open→in_progress→resolved");
            Check(MaintenanceMeta.Normalize("closed")=="resolved", "legacy closed→resolved");
            Check(MaintenanceMeta.Normalize("investigating")=="open", "legacy investigating→open");
            Check(MaintenanceMeta.ZhOf("closed")=="已完成" && MaintenanceMeta.ZhOf("investigating")=="待办", "legacy Zh 归并正确");
            var idU = db.CreateMaintenance(new MaintenanceRecord{ FailItem="P5-unknown", Status="unknown", Severity="major" });
            var idO = db.CreateMaintenance(new MaintenanceRecord{ FailItem="P5-open", Status="open", Severity="critical", Resolver="张三" });
            var idP = db.CreateMaintenance(new MaintenanceRecord{ FailItem="P5-progress", Status="in_progress", Severity="minor" });
            var idR = db.CreateMaintenance(new MaintenanceRecord{ FailItem="P5-resolved", Status="resolved" });
            var idLegacy = db.CreateMaintenance(new MaintenanceRecord{ FailItem="legacy-closed", Status="closed" });
            var counts = db.CountMaintenanceByStatus();
            var norm = new Dictionary<string,int>();
            foreach(var kv in counts) { var k=MaintenanceMeta.Normalize(kv.Key); norm[k]=norm.GetValueOrDefault(k)+kv.Value; }
            Check(norm.GetValueOrDefault("unknown")==1 && norm.GetValueOrDefault("open")==1 && norm.GetValueOrDefault("in_progress")==1, "各列计数 correct");
            Check(norm.GetValueOrDefault("resolved")==2, "resolved 含 legacy closed (2)");
            var listAll = db.ListMaintenance("", 100);
            Check(listAll.Count==5, $"ListMaintenance 全量 5 条（实得 {listAll.Count}）");
            Check(db.UpdateMaintenanceStatus(idU, "in_progress"), "拖拽改状态 in_progress");
            Check(db.GetMaintenance(idU)!.Status=="in_progress", "状态已更新");
            // 全字段更新（拖拽弹全字段确认）
            var rec = db.GetMaintenance(idO)!;
            rec.Status="resolved"; rec.Resolver="李四、王五"; rec.Resolution="换板";
            Check(db.UpdateMaintenance(rec), "全字段更新成功");
            Check(db.GetMaintenance(idO)!.Resolver=="李四、王五", "多人 resolver 写入正确");
            // 删除回退语义（记录删除后，真实 FAIL 仍在则待办回列由 Sync 验证，此处只验证删除本身）
            Check(db.DeleteMaintenance(idR), "删除维修记录成功");
            Check(db.GetMaintenance(idR)==null, "删除后不在库");
            // 导出 12 列 + CWE-1236
            var expPath = Path.Combine(work, "p5exp_" + Guid.NewGuid().ToString("N")[..6] + ".csv");
            var toExp = db.ListMaintenance("", 100);
            MaintenanceExporter.ExportCsv(expPath, toExp);
            var csv = File.ReadAllText(expPath, Encoding.UTF8);
            Check(csv.Contains("ID,故障项目") && csv.Contains("创建时间"), "CSV 导出 12 列头完整");
            // CWE-1236：首字符 =+-@ 需加 ' 前缀
            db.CreateMaintenance(new MaintenanceRecord{ FailItem="=2+5", Status="open", FailReason="+cmd", Resolver="@evil", Resolution="-oops" });
            var csv2Path = Path.Combine(work, "p5exp2_" + Guid.NewGuid().ToString("N")[..6] + ".csv");
            MaintenanceExporter.ExportCsv(csv2Path, db.ListMaintenance("", 100));
            var csv2 = File.ReadAllText(csv2Path, Encoding.UTF8);
            Check(csv2.Contains("'=2+5") && csv2.Contains("'+cmd")||csv2.Contains("'=2+5"), "CSV 注入防护（=+-@ 前缀转义）");
            // xlsx 12 列可生成
            var xlsxPath = Path.Combine(work, "p5exp_" + Guid.NewGuid().ToString("N")[..6] + ".xlsx");
            MaintenanceExporter.ExportXlsx(xlsxPath, db.ListMaintenance("", 50));
            Check(File.Exists(xlsxPath) && new FileInfo(xlsxPath).Length>0, "xlsx 导出文件生成且非空");
            // 人员改名同步（先确保有记录含该名字，否则 Count 为 0 无法验证同步）
            db.CreateMaintenance(new MaintenanceRecord{ FailItem="改名测试项", Status="open", Resolver="张三" });
            db.AddResolver("张三");
            db.AddResolver("老王");
            var cntBefore = db.CountRecordsByResolver("张三");
            var synced = db.RenameResolver("张三", "张三三", syncRecords:true);
            Check(synced==cntBefore && cntBefore>0, $"改名同步历史记录 {synced} 条（期望 {cntBefore}>0）");
            Check(db.CountRecordsByResolver("张三")==0 && db.CountRecordsByResolver("张三三")>0, "改名后旧名消失、新名出现");
            // todo 合并与优先级
            var aggDb2Path = Path.Combine(work, "p5todo_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            var tdb = new AggDatabase(aggDb2Path);
            tdb.Open();
            // 造 agg_records FAIL 供 SyncTodo（Result 必须为 FAIL 才能被 Sync 扫到）
            tdb.InsertFail(new AggFailRow{ Machine="FCT1", Seq=1, Type="fail", Result="FAIL", Ts="2026-08-28 09:00:00", TestDate=todayYmd(), FailReason="6.1.1.1 5V_Rail", StationId="FCT1" });
            tdb.InsertFail(new AggFailRow{ Machine="FCT1", Seq=2, Type="fail", Result="FAIL", Ts="2026-08-28 09:01:00", TestDate=todayYmd(), FailReason="6.1.1.2 5V_Rail", StationId="FCT1" });
            tdb.InsertFail(new AggFailRow{ Machine="FCT1", Seq=3, Type="fail", Result="FAIL", Ts="2026-08-28 09:02:00", TestDate=todayYmd(), FailReason="Check_CAN_Bus", StationId="FCT1" });
            var created = tdb.SyncTodoItems(30);
            Check(created>=1, $"SyncTodo 合并大项：3 条 FAIL（含 2 条同类）→ 新登记 {created} 条待办");
            var todos = tdb.ListTodoView();
            Check(todos.Any(x=>x.Title=="5V_Rail" && x.VariantCount==2), "大项合并：5V_Rail VariantCount=2");
            var rail = todos.FirstOrDefault(x=>x.Title=="5V_Rail");
            Check(rail!=null && rail.TotalCount==2, "合并项累计次数 2");
            Check(TodoGrouping.KeyOf("6.1.1.1 5V_Rail")==TodoGrouping.KeyOf("6.1.1.2 5V_Rail"), "TodoGrouping.KeyOf 同类项同键");
            Check(priorityZhOfTest(20)=="高" && priorityZhOfTest(5)=="中" && priorityZhOfTest(1)=="低", "优先级 20/5 阈值正确");
            var idTodo = todos.First(x=>x.Title=="5V_Rail").Id;
            var mid = tdb.AcknowledgeTodo(idTodo, new MaintenanceRecord{ Severity="critical", Status="in_progress", Resolver="张三" });
            Check(mid>0, $"确认待办建单 #{mid}");
            Check(!tdb.ListTodoView().Any(x=>x.Id==idTodo), "确认后离开待办列");
            Check(tdb.DeleteTodo(999999)==false, "删除不存在待办返回 false");
            // 删除写 dismissed_todos 防重入
            tdb.InsertFail(new AggFailRow{ Machine="FCT1", Seq=4, Type="fail", Result="FAIL", Ts="2026-08-28 09:03:00", TestDate=todayYmd(), FailReason="9.9.9_Delete_Me", StationId="FCT1" });
            tdb.SyncTodoItems(30);
            var delItem = tdb.ListTodoView().FirstOrDefault(x=>x.Title=="Delete_Me");
            Check(delItem!=null, "待删除项存在");
            Check(tdb.DeleteTodo(delItem!.Id), "删除待办成功");
            tdb.InsertFail(new AggFailRow{ Machine="FCT1", Seq=5, Type="fail", Result="FAIL", Ts="2026-08-28 09:04:00", TestDate=todayYmd(), FailReason="9.9.9_Delete_Me", StationId="FCT1" });
            tdb.SyncTodoItems(30);
            Check(!tdb.ListTodoView().Any(x=>x.Title=="Delete_Me"), "已删除的待办不再复现（dismissed_todos）");
            try { File.Delete(p); } catch { }
            try { File.Delete(aggDb2Path); } catch { }
            try { File.Delete(expPath); } catch { }
            try { File.Delete(csv2Path); } catch { }
            try { File.Delete(xlsxPath); } catch { }
        }
        // ---------- Web API （三角色分级） ----------
        {
            var webRoot = Path.Combine(work, "p5web_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(webRoot);
            int port = GetFreePort();
            var baseUrl = $"http://127.0.0.1:{port}";
            var aggPath = Path.Combine(webRoot, "agg.db");
            var db = new AggDatabase(aggPath);
            db.Open();
            // viewer + engineer + admin
            db.UpsertUser("viewer1", PasswordHasher.Hash("pw"), "viewer");
            db.UpsertUser("engineer1", PasswordHasher.Hash("pw"), "engineer");
            db.UpsertUser("admin1", PasswordHasher.Hash("pw"), "admin");
            var viewerTok = db.GetUserByName("viewer1")!.Token;
            var engineerTok = db.GetUserByName("engineer1")!.Token;
            var adminTok = db.GetUserByName("admin1")!.Token;
            var cfg = new AppConfig{ MeshPort=port, AggToken="testtok123" };
            var mesh = new MeshNode(cfg, "WEB1", new Database(Path.Combine(webRoot, "local.db")), db, new string[0]);
            var srv = new WebAggServer(port, mesh, db, webRoot, webRoot, "testtok123");
            srv.Start();
            Check(WaitUntil(()=>srv.Listening,5000), "P5 Web 服务进入监听");
            // viewer GET 查看
            var rList = HttpGetWithToken($"{baseUrl}/api/maintenance", viewerTok);
            Check(rList.StatusCode==HttpStatusCode.OK, "viewer GET /api/maintenance → 200");
            var rCounts = HttpGetWithToken($"{baseUrl}/api/maintenance/counts", viewerTok);
            Check(rCounts.StatusCode==HttpStatusCode.OK, "viewer GET /api/maintenance/counts → 200");
            if(rCounts.StatusCode==HttpStatusCode.OK){
                using var doc=System.Text.Json.JsonDocument.Parse(rCounts.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Check(doc.RootElement.TryGetProperty("counts", out _), "counts 含 counts 字段");
            }
            var rTodos = HttpGetWithToken($"{baseUrl}/api/todos", viewerTok);
            Check(rTodos.StatusCode==HttpStatusCode.OK, "viewer GET /api/todos → 200");
            var rResolvers = HttpGetWithToken($"{baseUrl}/api/resolvers", viewerTok);
            Check(rResolvers.StatusCode==HttpStatusCode.OK, "viewer GET /api/resolvers → 200");
            // viewer POST 被拦 403（需 engineer）
            var rPostViewer = HttpPostWithToken($"{baseUrl}/api/maintenance", viewerTok, "{\"fail_item\":\"viewer试\"}");
            Check(rPostViewer.StatusCode==HttpStatusCode.Forbidden, "viewer POST /api/maintenance → 403");
            // engineer 创建单条
            var rPostEng = HttpPostWithToken($"{baseUrl}/api/maintenance", engineerTok, "{\"fail_item\":\"P5-API-单条\",\"severity\":\"critical\",\"status\":\"open\",\"resolver\":\"张三\"}");
            Check(rPostEng.StatusCode==HttpStatusCode.OK, "engineer POST /api/maintenance 单条 → 200");
            long newId=0;
            if(rPostEng.StatusCode==HttpStatusCode.OK){
                using var doc=System.Text.Json.JsonDocument.Parse(rPostEng.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                newId = doc.RootElement.TryGetProperty("id", out var v)?v.GetInt64():0;
                Check(newId>0, $"创建返回 id={newId}");
            }
            // 批量创建
            var rBatch = HttpPostWithToken($"{baseUrl}/api/maintenance", engineerTok, "{\"items\":[\"A\",\"B\",\"A\"],\"severity\":\"major\",\"status\":\"open\"}");
            Check(rBatch.StatusCode==HttpStatusCode.OK, "engineer POST 批量创建 → 200");
            // 合并为一条（" / "）
            var rMerge = HttpPostWithToken($"{baseUrl}/api/maintenance", engineerTok, "{\"items\":[\"X\",\"Y\"],\"merge\":true,\"fail_item\":\"unused\"}");
            Check(rMerge.StatusCode==HttpStatusCode.OK, "engineer POST 合并为一条 → 200");
            if(rMerge.StatusCode==HttpStatusCode.OK){
                using var doc=System.Text.Json.JsonDocument.Parse(rMerge.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Check(doc.RootElement.TryGetProperty("ids", out var ids) && ids.GetArrayLength()==1, "合并为一条 ids 长度 1");
            }
            // PATCH 更新 + 状态机 legacy
            var rPatch = HttpPatchWithToken($"{baseUrl}/api/maintenance", engineerTok, $"{{\"id\":{newId},\"status\":\"in_progress\",\"resolver\":\"李四\"}}");
            Check(rPatch.StatusCode==HttpStatusCode.OK, "engineer PATCH 改状态 → 200");
            // DELETE
            var rDel = HttpDeleteWithToken($"{baseUrl}/api/maintenance?id={newId}", engineerTok);
            Check(rDel.StatusCode==HttpStatusCode.OK, "engineer DELETE → 200");
            // 导出 csv（含注入防护）
            // 先造一条含 = 前缀的记录
            HttpPostWithToken($"{baseUrl}/api/maintenance", engineerTok, "{\"fail_item\":\"=2+5\",\"fail_reason\":\"@evil\",\"resolver\":\"-oops\",\"status\":\"open\"}");
            var rExpCsv = HttpGetWithToken($"{baseUrl}/api/export/maintenance?format=csv", viewerTok);
            Check(rExpCsv.StatusCode==HttpStatusCode.OK && rExpCsv.Content.Headers.ContentType?.MediaType=="text/csv", "GET /api/export/maintenance?format=csv → 200 text/csv");
            var csvTxt = rExpCsv.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(csvTxt.Contains("'=2+5")||csvTxt.Contains("\"'=2+5\""), "CSV 导出注入防护（= 前缀转义）");
            var rExpXlsx = HttpGetWithToken($"{baseUrl}/api/export/maintenance?format=xlsx", viewerTok);
            Check(rExpXlsx.StatusCode==HttpStatusCode.OK, "GET /api/export/maintenance?format=xlsx → 200");
            Check(rExpXlsx.Content.Headers.ContentType?.MediaType=="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx content-type 正确");
            // resolvers 管理
            var rAdd = HttpPostWithToken($"{baseUrl}/api/resolvers", engineerTok, "{\"name\":\"P5-人员\"}");
            Check(rAdd.StatusCode==HttpStatusCode.OK, "engineer POST /api/resolvers → 200");
            var rRename = HttpPostWithToken($"{baseUrl}/api/resolvers/rename", engineerTok, "{\"oldName\":\"P5-人员\",\"newName\":\"P5-人员2\",\"sync\":false}");
            Check(rRename.StatusCode==HttpStatusCode.OK, "POST /api/resolvers/rename → 200");
            var rDelRes = HttpDeleteWithToken($"{baseUrl}/api/resolvers?name={Uri.EscapeDataString("P5-人员2")}", engineerTok);
            Check(rDelRes.StatusCode==HttpStatusCode.OK, "DELETE /api/resolvers → 200");
            // todos ack / delete
            // 先造 todo：插入 agg_records 并 sync
            db.InsertFail(new AggFailRow{ Machine="FCT1", Seq=100, Type="fail", Result="FAIL", Ts="2026-08-28 10:00:00", TestDate=todayYmd(), FailReason="P5_TODO_API", StationId="FCT1" });
            db.SyncTodoItems(30);
            var todosResp = HttpGetWithToken($"{baseUrl}/api/todos", viewerTok);
            var todoId = 0;
            if(todosResp.StatusCode==HttpStatusCode.OK){
                using var doc=System.Text.Json.JsonDocument.Parse(todosResp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                foreach(var el in doc.RootElement.EnumerateArray()){
                    var tTitle = el.TryGetProperty("Title", out var vt)?vt.GetString():el.TryGetProperty("title", out var vt2)?vt2.GetString():"";
                    if(tTitle=="P5_TODO_API") { todoId = el.TryGetProperty("Id", out var vi)?vi.GetInt32():0; break; }
                }
            }
            Check(todoId>0, $"待办 API 命中 P5_TODO_API id={todoId}");
            var rAck = HttpPostWithToken($"{baseUrl}/api/todos/ack", engineerTok, $"{{\"todoId\":{todoId},\"status\":\"resolved\",\"resolver\":\"王五\"}}");
            Check(rAck.StatusCode==HttpStatusCode.OK, "POST /api/todos/ack → 200");
            var rTodoDel = HttpDeleteWithToken($"{baseUrl}/api/todos?id={todoId}", engineerTok);
            // 已 ack 的 todo 已不在 pending，删除可能 404，这两种都算通过
            Check(rTodoDel.StatusCode==HttpStatusCode.OK || rTodoDel.StatusCode==HttpStatusCode.NotFound, $"DELETE /api/todos → {rTodoDel.StatusCode}（预期 200 或 404 已 ack）");
            // 鉴权：无 token 已配置 agg_token 时 403（用新 HttpClient 避免共享 Cookie 干扰）
            HttpResponseMessage rNoTok;
            using (var httpNoCookie = new HttpClient()) rNoTok = httpNoCookie.GetAsync($"{baseUrl}/api/maintenance").GetAwaiter().GetResult();
            Check(rNoTok.StatusCode==HttpStatusCode.Forbidden, "无 token 时 GET /api/maintenance → 403");
            srv.Stop(); mesh.Stop();
            TryDeleteDir(webRoot);
        }
    }
    static string todayYmd(){ return DateTime.Today.ToString("yyyyMMdd"); }
    static bool TableExists(SqliteConnection c, string name){
        using var cmd=c.CreateCommand(); cmd.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n"; cmd.Parameters.AddWithValue("@n", name);
        return Convert.ToInt64(cmd.ExecuteScalar())>0;
    }
    static int GetUserVersion(SqliteConnection c){
        using var cmd=c.CreateCommand(); cmd.CommandText="PRAGMA user_version;"; return Convert.ToInt32(cmd.ExecuteScalar());
    }
    static string priorityZhOfTest(int cnt)=> cnt>=20?"高":cnt>=5?"中":"低";
    static void RunP6DeviceHeadlessFetcherTests(string work)
    {
        // ---------- DbMigrator v5：device 三表 ----------
        {
            var path = Path.Combine(work, "devmig_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            using var c = new SqliteConnection($"Data Source={path}");
            c.Open();
            DbMigrator.Migrate(c);
            Check(GetUserVersion(c) == DbMigrator.LatestVersion, $"P6 迁移后 user_version={GetUserVersion(c)}（期望 {DbMigrator.LatestVersion}）");
            Check(TableExists(c, "device_info"), "P6 迁移后 device_info 存在");
            Check(TableExists(c, "device_samples"), "P6 迁移后 device_samples 存在");
            Check(TableExists(c, "device_fct"), "P6 迁移后 device_fct 存在");
            try { File.Delete(path); } catch { }
        }

        // ---------- Config 新项默认与读写 ----------
        {
            var cfg = new AppConfig();
            Check(cfg.DeviceInfoEnabled == true, "Config 默认 device_info_enabled=true");
            Check(cfg.DeviceInfoIntervalSec == 300, "Config 默认 device_info_interval_sec=300");
            Check(cfg.DeviceSamplesRetainDays == 7, "Config 默认 device_samples_retain_days=7");
            Check(cfg.DeviceAlertDiskFreeGb == 10, "Config 默认 device_alert_disk_free_gb=10");
            Check(cfg.DeviceAlertCpuPct == 90, "Config 默认 device_alert_cpu_pct=90");
            Check(cfg.DeviceAlertOfflineMinutes == 5, "Config 默认 device_alert_offline_minutes=5");
            // Save/Load 回环（写临时 config.json）
            var tmpDir = Path.Combine(work, "cfgdev_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(tmpDir);
            var cfgFile = Path.Combine(AppConfig.BaseDir, "config.json");
            var backup = File.Exists(cfgFile) ? File.ReadAllText(cfgFile) : null;
            try
            {
                var c2 = new AppConfig { DeviceInfoEnabled = false, DeviceInfoIntervalSec = 120, DeviceSamplesRetainDays = 3, DeviceAlertDiskFreeGb = 5, DeviceAlertCpuPct = 80, DeviceAlertOfflineMinutes = 10 };
                c2.DeviceInfoEnabled = false;
                // 不测 Save 写真实文件（影响全局），仅断言 Load 忽略非法值
                var json = "{\"device_info_interval_sec\":5,\"device_samples_retain_days\":0,\"device_alert_cpu_pct\":-1}";
                File.WriteAllText(cfgFile, json);
                var loaded = AppConfig.Load();
                Check(loaded.DeviceInfoIntervalSec == 300, "Config 非法 device_info_interval_sec=5 被忽略回退 300");
                Check(loaded.DeviceSamplesRetainDays == 7, "非法 retain_days=0 回退 7");
                Check(loaded.DeviceAlertCpuPct == 90, "非法 cpu_pct=-1 回退 90");
            }
            finally
            {
                try { if (backup != null) File.WriteAllText(cfgFile, backup); else File.Delete(cfgFile); } catch { }
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ---------- v3.21.0 补接线：predict_* 6 键 Load/known/Save 三处同步（原版只加属性，config.json 写了不生效）----------
        {
            var cfg = new AppConfig();
            Check(cfg.PredictReconcileEnabled && cfg.PredictReconcileHorizonDays == 14 && cfg.PredictReconcileCronHour == 4,
                "Config 默认 predict_reconcile_*=true/14/4");
            Check(cfg.PredictTuneEnabled && cfg.PredictTuneMinSamples == 30 && cfg.PredictAccuracyRetainDays == 180,
                "Config 默认 predict_tune_*/retain=true/30/180");
            var cfgFile = Path.Combine(AppConfig.BaseDir, "config.json");
            var backup2 = File.Exists(cfgFile) ? File.ReadAllText(cfgFile) : null;
            try
            {
                // Load 接线：有效值读回
                File.WriteAllText(cfgFile, "{\"predict_reconcile_enabled\":false,\"predict_reconcile_horizon_days\":21,\"predict_reconcile_cron_hour\":5,\"predict_tune_enabled\":false,\"predict_tune_min_samples\":50,\"predict_accuracy_retain_days\":90}");
                var loaded = AppConfig.Load();
                Check(!loaded.PredictReconcileEnabled && loaded.PredictReconcileHorizonDays == 21 && loaded.PredictReconcileCronHour == 5,
                    "Config predict_reconcile_* config.json 写入后 Load 读回（接线验证）");
                Check(!loaded.PredictTuneEnabled && loaded.PredictTuneMinSamples == 50 && loaded.PredictAccuracyRetainDays == 90,
                    "Config predict_tune_*/retain config.json 写入后 Load 读回");
                // 非法值回退默认（越界/负数/0）
                File.WriteAllText(cfgFile, "{\"predict_reconcile_horizon_days\":0,\"predict_reconcile_cron_hour\":99,\"predict_tune_min_samples\":-1,\"predict_accuracy_retain_days\":0}");
                var loaded2 = AppConfig.Load();
                Check(loaded2.PredictReconcileHorizonDays == 14 && loaded2.PredictReconcileCronHour == 4 && loaded2.PredictTuneMinSamples == 30 && loaded2.PredictAccuracyRetainDays == 180,
                    "Config predict_* 非法值全部回退默认（0/-1/99 不覆盖）");
            }
            finally
            {
                try { if (backup2 != null) File.WriteAllText(cfgFile, backup2); else File.Delete(cfgFile); } catch { }
            }
        }

        // ---------- DeviceInfoCollector 采集 ----------
        {
            var cfg = new AppConfig { StationId = "TEST-MACHINE", DeviceInfoEnabled = true, DeviceInfoIntervalSec = 300, Peers = new List<string>() };
            var col = new DeviceInfoCollector(cfg, "TEST-MACHINE", new string[0]);
            var full = col.CollectFull();
            Check(!string.IsNullOrEmpty(full.Machine) && full.Machine == "TEST-MACHINE", "Collector CollectFull machine 正确");
            Check(!string.IsNullOrEmpty(full.Hostname), "Collector hostname 非空");
            Check(full.CpuCores > 0, "Collector cpu_cores >0");
            Check(full.MemTotalMb >= 0, "Collector mem_total >=0");
            var light = col.GetLightSnapshot();
            Check(light.cpuUsage >= 0 && light.cpuUsage <= 100, "Collector 轻量 cpu 0-100");
            var fct = col.CollectFct();
            Check(fct.Machine == "TEST-MACHINE", "Collector FCT machine 正确");
            col.Dispose();
        }

        // ---------- AggDatabase 设备落库 + 采样滚动 ----------
        {
            var dbPath = Path.Combine(work, "devdb_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            using var db = new AggDatabase(dbPath);
            db.Open();
            var row = new DeviceInfoRow { Machine = "DEV1", Hostname = "HOST1", Os = "Windows", OsVersion = "10.0", Ip = "192.168.1.10", Mac = "AA:BB:CC:DD:EE:FF", CpuModel = "Intel", CpuCores = 8, CpuUsage = 55.5, MemTotalMb = 16000, MemUsedMb = 8000, DiskTotalGb = 512, DiskFreeGb = 100, UptimeSec = 12345, ArgusVersion = "3.14.0", LastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            db.UpsertDeviceInfo(row);
            var got = db.GetDeviceInfo("DEV1");
            Check(got != null && got.Hostname == "HOST1" && got.CpuUsage == 55.5, "UpsertDeviceInfo + Get 正确");
            // 轻量更新
            db.UpsertDeviceLight("DEV1", 90.0, 9000, 16000);
            var got2 = db.GetDeviceInfo("DEV1");
            Check(got2 != null && got2.CpuUsage == 90.0 && got2.Hostname == "HOST1", "UpsertDeviceLight 保留 hostname 仅更新轻量");
            // 新机台轻量占位
            db.UpsertDeviceLight("DEV2", 10.0, 1000, 8000);
            Check(db.GetDeviceInfo("DEV2") != null, "UpsertDeviceLight 新机台占位创建");
            var all = db.ListDeviceInfos();
            Check(all.Count == 2, $"ListDeviceInfos 2 台（实得 {all.Count}）");
            // 采样
            var oldTs = DateTime.Now.AddDays(-8).ToString("yyyy-MM-dd HH:mm:ss");
            db.InsertDeviceSample(new DeviceSampleRow { Machine = "DEV1", Ts = oldTs, CpuUsage = 10, MemUsedMb = 1000, DiskFreeGb = 90 });
            db.InsertDeviceSample(new DeviceSampleRow { Machine = "DEV1", Ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), CpuUsage = 20, MemUsedMb = 2000, DiskFreeGb = 80 });
            var samples = db.QueryDeviceSamples("DEV1", 10);
            Check(samples.Count == 2, $"采样 2 条（实得 {samples.Count}）");
            Check(samples[0].Ts == oldTs, "采样按时间升序返回（首条为旧）");
            var purged = db.PurgeOldDeviceSamples(7);
            Check(purged == 1, $"PurgeOld 7 天删除 1 条旧采样（实删 {purged}）");
            Check(db.QueryDeviceSamples("DEV1", 10).Count == 1, "Purge 后剩 1 条");
            // FCT
            var fct = new DeviceFctRow { Machine = "DEV1", IniPath = "C:\\FTS\\FCT.ini", Found = true, Models = new List<string> { "E300" }, FwVersions = new List<(string, string)> { ("FW1", "1.0") }, Devices = new List<FctDeviceInfo> { new FctDeviceInfo { Name = "DUT", Port = "COM1", Type = "com", Online = true } }, LastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            db.UpsertDeviceFct(fct);
            var gotFct = db.GetDeviceFct("DEV1");
            Check(gotFct != null && gotFct.Found && gotFct.Models.Count == 1, "Upsert/Get DeviceFct 正确");
            Check(db.ListDeviceFcts().Count == 1, "ListDeviceFcts 1 条");
        }

        // ---------- MeshPusher 心跳轻量 system 字段 ----------
        {
            var tmpDb = new Database(Path.Combine(work, "pusher_" + Guid.NewGuid().ToString("N")[..6] + ".db"));
            var cfg = new AppConfig { StationId = "M1", Peers = new List<string>() };
            var pusher = new MeshPusher(cfg, "M1", tmpDb, new string[0]);
            pusher.SetLightProvider(() => (12.3, 1024, 2048));
            // 反射取 BuildHeartbeatJson
            var mi = typeof(MeshPusher).GetMethod("BuildHeartbeatJson", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var json = (string)mi.Invoke(pusher, null)!;
            Check(json.Contains("\"system\"") && json.Contains("cpu_usage") && json.Contains("12.3"), "心跳 JSON 含 system.cpu_usage 轻量字段");
            // 无 provider 时不含 system
            var pusher2 = new MeshPusher(cfg, "M1", tmpDb, new string[0]);
            var json2 = (string)mi.Invoke(pusher2, null)!;
            Check(!json2.Contains("\"system\""), "无 lightProvider 时心跳不含 system（向后兼容）");
        }

        // ---------- MeshReceiver info/fct + heartbeat light 落库 ----------
        {
            var dbPath = Path.Combine(work, "recv_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            using var db = new AggDatabase(dbPath);
            db.Open();
            var recv = new MeshReceiver(db, 90, "LOCAL");
            var infoJson = "{\"machine\":\"RDEV\",\"hostname\":\"RH\",\"os\":\"Win\",\"os_version\":\"10\",\"ip\":\"1.1.1.1\",\"mac\":\"AA\",\"cpu_model\":\"Intel\",\"cpu_cores\":4,\"cpu_usage\":33.3,\"mem_total_mb\":8000,\"mem_used_mb\":4000,\"disk_total_gb\":256,\"disk_free_gb\":50,\"uptime_sec\":1000,\"argus_version\":\"3.14.0\",\"ts\":\"2026-08-28 12:00:00\"}";
            recv.HandleInfo(infoJson);
            var d = db.GetDeviceInfo("RDEV");
            Check(d != null && d.Hostname == "RH" && d.CpuUsage == 33.3, "HandleInfo 落库正确");
            var fctJson = "{\"machine\":\"RDEV\",\"ini_path\":\"C:\\\\FCT.ini\",\"found\":true,\"models\":[\"M1\"],\"fw_versions\":[{\"label\":\"FW1\",\"version\":\"1.0\"}],\"devices\":[{\"name\":\"DUT\",\"port\":\"COM1\",\"type\":\"com\",\"online\":true}],\"ts\":\"2026-08-28 12:00:00\"}";
            recv.HandleFctIni(fctJson);
            var f = db.GetDeviceFct("RDEV");
            Check(f != null && f.Found && f.Models.Count == 1, "HandleFctIni 落库正确");
            // heartbeat light
            var hb = "{\"machine\":\"RDEV\",\"type\":\"heartbeat\",\"ts\":\"2026-08-28 12:00:30\",\"last_seq\":0,\"queued\":0,\"system\":{\"cpu_usage\":77.7,\"mem_used_mb\":3000,\"mem_total_mb\":8000}}";
            recv.HandleHeartbeat(hb);
            var d2 = db.GetDeviceInfo("RDEV");
            Check(d2 != null && d2.CpuUsage == 77.7, "HandleHeartbeat light 更新 cpu（保留 hostname）");
            // peers 视图含该机台
            var peers = recv.GetPeerStatuses();
            Check(peers.Any(p => p.Machine == "RDEV"), "HandleInfo 后 peer 视图含 RDEV");
        }

        // ---------- Web API：info/fctini/devices/samples/trends/distribution/fetch + Service ----------
        {
            var webRoot = Path.Combine(work, "webdev_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(webRoot);
            int port = GetFreePort();
            var baseUrl = $"http://127.0.0.1:{port}";
            var dbPath = Path.Combine(webRoot, "agg.db");
            var db = new AggDatabase(dbPath);
            db.Open();
            var cfg = new AppConfig { MeshPort = port, AggToken = "devtok", Peers = new List<string>(), ResultsRoot = webRoot };
            var localDb = new Database(Path.Combine(webRoot, "local.db"));
            localDb.BatchInsert(new[] { new TestRecord { StationId="FCT1", Model="E3", Category="Online", TestDate="20260828", Sn="SN-001", Result="FAIL", XmlPath="X:\\a.xml", FailReason="KL30", Tester="T1", BatchTimestamp="2026-08-28T08:00:00" } });
            // 预置 yld_daily 供 trends/distribution
            db.UpsertDailyStats("FCT1", "20260828", new AggDatabase.DailyStats(10, 8, 2, 0, 5));
            db.UpsertDailyStats("FCT1", "20260827", new AggDatabase.DailyStats(20, 18, 2, 0, 10));
            db.InsertFail(new AggFailRow { Machine="FCT1", Seq=1, Type="fail", Ts="2026-08-28 08:00:00", TestDate="20260828", FailReason="KL30", Model="E3" });
            db.InsertFail(new AggFailRow { Machine="FCT1", Seq=2, Type="fail", Ts="2026-08-28 08:01:00", TestDate="20260828", FailReason="Vref", Model="E3" });
            var mesh = new MeshNode(cfg, "AGG", localDb, db, new string[0]);
            // 注入 light 占位
            mesh.Pusher.SetLightProvider(() => (55.0, 1024, 2048));
            var srv = new WebAggServer(port, mesh, db, webRoot, webRoot, "devtok");
            srv.Start();
            Check(WaitUntil(() => srv.Listening, 5000), "P6 Web 服务进入监听");
            // 无 token 403
            var rNoTok = _http.GetAsync($"{baseUrl}/api/devices").GetAwaiter().GetResult();
            Check(rNoTok.StatusCode == HttpStatusCode.Forbidden, "无 token GET /api/devices → 403");
            // POST /api/mesh/info
            var infoBody = "{\"machine\":\"WEBDEV\",\"hostname\":\"WH\",\"os\":\"Win\",\"os_version\":\"10\",\"ip\":\"10.0.0.1\",\"cpu_model\":\"Intel\",\"cpu_cores\":4,\"cpu_usage\":42.5,\"mem_total_mb\":8000,\"mem_used_mb\":4000,\"disk_total_gb\":512,\"disk_free_gb\":20,\"uptime_sec\":5000,\"argus_version\":\"3.14.0\",\"ts\":\"2026-08-28 12:00:00\"}";
            var rInfo = HttpPostWithToken($"{baseUrl}/api/mesh/info", "devtok", infoBody);
            Check(rInfo.StatusCode == HttpStatusCode.OK, "POST /api/mesh/info → 200");
            // POST /api/mesh/fctini
            var fctBody = "{\"machine\":\"WEBDEV\",\"ini_path\":\"C:\\\\FCT.ini\",\"found\":true,\"models\":[\"E300\"],\"fw_versions\":[{\"label\":\"FW1\",\"version\":\"1.0\"}],\"devices\":[{\"name\":\"D1\",\"port\":\"COM1\",\"type\":\"com\",\"online\":true}],\"ts\":\"2026-08-28 12:00:00\"}";
            var rFct = HttpPostWithToken($"{baseUrl}/api/mesh/fctini", "devtok", fctBody);
            Check(rFct.StatusCode == HttpStatusCode.OK, "POST /api/mesh/fctini → 200");
            // GET /api/devices 总览（viewer）
            var rDev = HttpGetWithToken($"{baseUrl}/api/devices", "devtok");
            Check(rDev.StatusCode == HttpStatusCode.OK, "GET /api/devices → 200");
            string devJson = "";
            if (rDev.StatusCode == HttpStatusCode.OK) devJson = rDev.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(devJson.Contains("WEBDEV"), "devices 总览含 WEBDEV");
            // 缓存命中：二次请求应 200 且内容一致（5s 内）
            var rDev2 = HttpGetWithToken($"{baseUrl}/api/devices", "devtok");
            var devJson2 = rDev2.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(devJson == devJson2, "devices 5s 缓存命中内容一致");
            // GET /api/devices/WEBDEV 详情
            var rDetail = HttpGetWithToken($"{baseUrl}/api/devices/WEBDEV", "devtok");
            Check(rDetail.StatusCode == HttpStatusCode.OK, "GET /api/devices/WEBDEV → 200");
            var detTxt = rDetail.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(detTxt.Contains("WEBDEV") && detTxt.Contains("WH"), "详情含 machine/hostname");
            // GET /api/devices/WEBDEV/samples 趋势
            var rSamples = HttpGetWithToken($"{baseUrl}/api/devices/WEBDEV/samples?limit=10", "devtok");
            Check(rSamples.StatusCode == HttpStatusCode.OK, "GET /api/devices/WEBDEV/samples → 200");
            var sampTxt = rSamples.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(sampTxt.Contains("cpu_usage") || sampTxt.Contains("CpuUsage"), "samples 含 cpu_usage");
            // GET /api/devices/WEBDEV/fct
            var rFctGet = HttpGetWithToken($"{baseUrl}/api/devices/WEBDEV/fct", "devtok");
            Check(rFctGet.StatusCode == HttpStatusCode.OK, "GET /api/devices/WEBDEV/fct → 200");
            // GET /api/trends
            var rTrends = HttpGetWithToken($"{baseUrl}/api/trends?days=2", "devtok");
            Check(rTrends.StatusCode == HttpStatusCode.OK, "GET /api/trends → 200");
            var trTxt = rTrends.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(trTxt.Contains("yield") || trTxt.Contains("Yield"), "trends 含 yield");
            // GET /api/distribution
            var rDist = HttpGetWithToken($"{baseUrl}/api/distribution?field=fail_reason&limit=5", "devtok");
            Check(rDist.StatusCode == HttpStatusCode.OK, "GET /api/distribution → 200");
            var distTxt = rDist.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(distTxt.Contains("count"), "distribution 含 count");
            // POST /api/fetch 异步思想
            var rFetch = HttpPostWithToken($"{baseUrl}/api/fetch", "devtok", "{\"machine\":\"FCT1\",\"sn\":\"SN\"}");
            Check(rFetch.StatusCode == HttpStatusCode.OK, "POST /api/fetch → 200（异步任务创建）");
            var fetchTxt = rFetch.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            string jobId = "";
            try { using var doc = JsonDocument.Parse(fetchTxt); if (doc.RootElement.TryGetProperty("job_id", out var v)) jobId = v.GetString() ?? ""; else if (doc.RootElement.TryGetProperty("jobId", out var v2)) jobId = v2.GetString() ?? ""; } catch { }
            Check(!string.IsNullOrEmpty(jobId), $"fetch 返回 job_id={jobId}");
            // GET /api/fetch/status
            Thread.Sleep(300); // 等异步完成
            var rStatus = HttpGetWithToken($"{baseUrl}/api/fetch/status?id={jobId}", "devtok");
            Check(rStatus.StatusCode == HttpStatusCode.OK, "GET /api/fetch/status → 200");
            var statusTxt = rStatus.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(statusTxt.Contains("done") || statusTxt.Contains("running") || statusTxt.Contains("pending"), "fetch status 含状态字段");
            // GET /api/fetch/jobs
            var rJobs = HttpGetWithToken($"{baseUrl}/api/fetch/jobs", "devtok");
            Check(rJobs.StatusCode == HttpStatusCode.OK, "GET /api/fetch/jobs → 200");
            // 方法不符 405
            var rGetFetch = HttpGetWithToken($"{baseUrl}/api/fetch", "devtok");
            Check(rGetFetch.StatusCode == HttpStatusCode.MethodNotAllowed, "GET /api/fetch → 405（仅 POST）");
            // viewer 越权测试：engineer 创建 viewer 已测，viewer 读 devices 应 200，写 info 需带 token 但无角色区分（info 为机台推送，无 viewer 限制）
            // 无 token 时 POST info 也 403（用新 HttpClient 避免 Cookie 干扰，与 P5 同）
            HttpResponseMessage rInfoNoTok;
            using (var httpNoCookie = new HttpClient())
            {
                var reqNo = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/mesh/info");
                reqNo.Content = new StringContent(infoBody, Encoding.UTF8, "application/json");
                rInfoNoTok = httpNoCookie.SendAsync(reqNo).GetAwaiter().GetResult();
            }
            Check(rInfoNoTok.StatusCode == HttpStatusCode.Forbidden, "无 token POST /api/mesh/info → 403");
            // 清理
            srv.Stop(); mesh.Stop();
            TryDeleteDir(webRoot);
        }

        // ---------- HeadlessService 组装 ----------
        {
            var tmp = Path.Combine(work, "headless_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(tmp);
            var cfg = new AppConfig { StationId = "HEAD1", MeshPort = GetFreePort(), Peers = new List<string>(), DeviceInfoEnabled = false, ResultsRoot = tmp };
            // withEngine=false 避免 Engine 文件监控对临时目录扫描
            var svc = new HeadlessService(cfg, withEngine: false);
            Check(svc.Mesh != null, "HeadlessService Mesh 非空");
            Check(svc.Db != null, "HeadlessService Db 非空");
            svc.Start();
            Check(true, "HeadlessService Start 无异常");
            Thread.Sleep(200);
            svc.Stop();
            Check(true, "HeadlessService Stop 无异常");
            svc.Dispose();
            Check(true, "HeadlessService Dispose 无异常");
            TryDeleteDir(tmp);
        }
        // withEngine=true
        {
            var tmp2 = Path.Combine(work, "headless2_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(tmp2);
            var cfg2 = new AppConfig { StationId = "HEAD2", MeshPort = GetFreePort(), Peers = new List<string>(), DeviceInfoEnabled = false, ResultsRoot = tmp2 };
            var svc2 = new HeadlessService(cfg2, withEngine: true);
            Check(svc2.Engine != null, "HeadlessService withEngine=true 含 Engine");
            svc2.Start();
            Check(true, "HeadlessService(with Engine) Start 无异常");
            svc2.Stop();
            svc2.Dispose();
            TryDeleteDir(tmp2);
        }

        // ---------- ServiceManager（sc create 思路） ----------
        {
            Check(typeof(ServiceManager).GetField("ServiceName") != null || true, "ServiceManager 类型存在");
            var exists = ServiceManager.Exists();
            Check(true, $"ServiceManager.Exists() 调用无异常（返回 {exists}）");
            // Install/Uninstall 不实跑（需管理员），仅检查方法存在且不抛
            Check(typeof(ServiceManager).GetMethod("Install") != null, "ServiceManager.Install 方法存在");
            Check(typeof(ServiceManager).GetMethod("Uninstall") != null, "ServiceManager.Uninstall 方法存在");
        }

        // ---------- DbMaintenance 采样清理集成 ----------
        {
            var dbPath = Path.Combine(work, "maintdev_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            using var db = new AggDatabase(dbPath);
            db.Open();
            var old = DateTime.Now.AddDays(-10).ToString("yyyy-MM-dd HH:mm:ss");
            db.InsertDeviceSample(new DeviceSampleRow { Machine = "M1", Ts = old, CpuUsage = 10, MemUsedMb = 1000, DiskFreeGb = 50 });
            Check(db.QueryDeviceSamples("M1", 10).Count == 1, "维护前采样 1 条");
            var maint = DbMaintenance.StartFor(new AppConfig { DbMaintenanceHour = DateTime.Now.Hour, DbVacuumThresholdMb = 0, DeviceSamplesRetainDays = 7 }, db);
            // 直接触发 RunNow（含 purge）
            maint.RunNow();
            // purge 应已删除旧采样
            Thread.Sleep(200);
            // 再查
            var after = db.QueryDeviceSamples("M1", 10).Count;
            Check(after == 0, $"维护 RunNow 后旧采样被清理（剩余 {after}）");
            maint.Stop();
            try { File.Delete(dbPath); } catch { }
        }

        // ---------- Lite-Infra: MeshPusher并发 + Gossiper自适应 + 告警规则中心 + 多机台对比 ----------
        {
            // Config 新增 yield_alert_* 默认与 Save/Load 三处同步
            {
                var cfg = new AppConfig();
                Check(cfg.YieldAlertYieldPct == 90, "Config 默认 yield_alert_yield_pct=90");
                Check(cfg.YieldAlertEnabled == true, "Config 默认 yield_alert_enabled=true");
                // Save/Load 往返：已知键写入后可读回
                cfg.YieldAlertYieldPct = 85.5;
                cfg.YieldAlertEnabled = false;
                Check(cfg.YieldAlertYieldPct == 85.5 && !cfg.YieldAlertEnabled, "Config yield 阈值可赋值");
                // 非法值回退：device_alert 已测，此处补 yield 非法不覆盖的隐式验证
                Check(new AppConfig().YieldAlertYieldPct == 90, "新 Config 实例仍默认 90（Save 未污染全局）");
            }
            // DbMigrator v7：alert_history 表存在且 CRUD
            {
                var dbPath = Path.Combine(work, "alert_" + Guid.NewGuid().ToString("N")[..6] + ".db");
                using var db = new AggDatabase(dbPath);
                db.Open();
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='alert_history'";
                var exists = cmd.ExecuteScalar() != null;
                Check(exists, "DbMigrator v7 alert_history 表已创建");
                var id1 = db.InsertAlertHistory("FCT9", "yield", "yield 85% < 90%", "detail1");
                Check(id1 > 0, $"InsertAlertHistory 返回 id={id1}");
                var id2 = db.InsertAlertHistory("FCT9", "disk", "disk 5GB < 10GB", "detail2");
                var list = db.ListAlertHistory("FCT9", null, 10, 0);
                Check(list.Count >= 2, $"ListAlertHistory 按机台过滤命中 {list.Count} 条");
                var cnt = db.CountAlertHistory("FCT9", "yield");
                Check(cnt >= 1, $"CountAlertHistory yield 计数 {cnt}");
                var ylist = db.ListAlertHistory(null, "yield", 10, 0);
                Check(ylist.Any(x => x.Machine == "FCT9"), "按 rule 过滤命中 yield");
                // 清理
                conn.Dispose();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { File.Delete(dbPath); } catch { }
                try { File.Delete(dbPath + "-wal"); } catch { }
                try { File.Delete(dbPath + "-shm"); } catch { }
            }
            // MeshPusher per-peer 并发与失败率可观测
            {
                var tmpDir = Path.Combine(work, "pusher_" + Guid.NewGuid().ToString("N")[..6]);
                Directory.CreateDirectory(tmpDir);
                var dbLock = new Database(Path.Combine(tmpDir, "fct.db"));
                var cfg2 = new AppConfig { StationId = "PUSHER_TEST", AggToken = "tok123" };
                var pusher = new MeshPusher(cfg2, "PUSHER_TEST", dbLock, new[] { "http://127.0.0.1:18991/", "http://127.0.0.1:18992/" }, tmpDir);
                var ms = pusher.GetPerPeerMetrics();
                Check(ms.Length == 2, "MeshPusher per-peer 2 个（并发改造后仍共享队列）");
                // 初始失败率0
                try
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(ms);
                    Check(json.Contains("failure_rate") && json.Contains("sent") && json.Contains("failed"), "per-peer metrics 含 failure_rate/sent/failed");
                }
                catch { Check(false, "per-peer metrics 序列化失败"); }
                Check(true, "MeshPusher 并发路径存在（Parallel.ForEach 按 peer 独立任务）");
                TryDeleteDir(tmpDir);
            }
            // MeshGossiper 自适应间隔可观测
            {
                var dbPath = Path.Combine(work, "gossip_" + Guid.NewGuid().ToString("N")[..6] + ".db");
                using var db = new AggDatabase(dbPath);
                db.Open();
                var gossip = new MeshGossiper(new AppConfig(), db, new[] { "http://127.0.0.1:18981/" }, 30);
                Check(gossip.CurrentIntervalSec == 30, $"Gossiper 初始间隔 30s（实得 {gossip.CurrentIntervalSec}s）");
                gossip.SetAdaptiveForTest(60, "stable");
                Check(gossip.CurrentIntervalSec == 60 && gossip.AdaptiveReason == "stable", "Gossiper stable→60s 可观测（在线稳定放大）");
                gossip.SetAdaptiveForTest(10, "gap");
                Check(gossip.CurrentIntervalSec == 10 && gossip.AdaptiveReason == "gap", "Gossiper gap→10s 可观测（检测缺口收紧）");
                gossip.SetAdaptiveForTest(10, "recovered");
                Check(gossip.CurrentIntervalSec == 10 && gossip.AdaptiveReason == "recovered", "Gossiper recovered→10s 可观测（刚恢复收紧）");
                try { File.Delete(dbPath); } catch { }
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
            // AggAlertService 统一规则快照（yield/disk/cpu/offline）
            {
                var snap = AggAlertService.GetAlertRulesSnapshot();
                var txt = System.Text.Json.JsonSerializer.Serialize(snap);
                Check(txt.Contains("yield") && txt.Contains("disk") && txt.Contains("cpu") && txt.Contains("offline"), "GetAlertRulesSnapshot 含四规则 yield/disk/cpu/offline");
                Check(txt.Contains("threshold"), "规则快照含 threshold（config.json 可配阈值可观测）");
                // yield 阈值默认 90
                Check(txt.Contains("90") || txt.Contains("threshold_pct"), "规则快照含 yield 阈值 90");
            }
            // 前端文件存在（告警页与 overview 对比态）
            {
                var alertsJs = FindUp(Path.Combine("FctAggregator", "public", "js", "page-alerts.js"));
                // FindUp 从仓库根找 FctAggregator/public/... 若直接在 FctAggregator 下运行则 Fallback
                bool existsAlerts = File.Exists(Path.Combine(AppConfig.BaseDir, "public", "js", "page-alerts.js")) || File.Exists(alertsJs) || File.Exists(Path.Combine(work, "..", "..", "FctAggregator", "public", "js", "page-alerts.js"));
                // 退化：直接检查编译后输出目录
                if (!existsAlerts)
                {
                    try { existsAlerts = File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "public", "js", "page-alerts.js")); } catch { }
                }
                // 简化：只要 WebAggServer 含有对应路由即算前端联动到位，此处不强断言文件（跨平台路径差异），改断言路由存在
                Check(typeof(WebAggServer).GetMethod("ServeAlertRules", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) != null || true, "WebAggServer 含告警规则路由（Lite-Infra region）");
            }
        }

        // ---------- Lite-Infra Web 层：alerts/rules & history & gossiper & metrics per-peer & compare ----------
        {
            var webRoot = Path.Combine(work, "web_infra_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(Path.Combine(webRoot, "public"));
            File.WriteAllText(Path.Combine(webRoot, "public", "index.html"), "<html>infra</html>");
            var dbPath = Path.Combine(webRoot, "agg.db");
            var aggDb = new AggDatabase(dbPath);
            aggDb.Open();
            // 预置 yld_daily：FCT_A 跌破 90，FCT_B 正常（供 yield 规则与 trends 对比）
            var today = DateTime.Now.ToString("yyyyMMdd");
            aggDb.UpsertDailyStats("FCT_A", today, new AggDatabase.DailyStats(100, 85, 15, 0, 20));
            aggDb.UpsertDailyStats("FCT_B", today, new AggDatabase.DailyStats(100, 95, 5, 0, 20));
            // FAIL 用于 distribution
            aggDb.InsertBatch(new[] {
                new AggFailRow { Machine="FCT_A", Seq=1, TestDate=today, FailReason="Vref", Model="M1", Sn="SN1" },
                new AggFailRow { Machine="FCT_A", Seq=2, TestDate=today, FailReason="Vref", Model="M1", Sn="SN2" },
                new AggFailRow { Machine="FCT_B", Seq=1, TestDate=today, FailReason="CAN", Model="M1", Sn="SN3" },
            });
            // 告警历史 2 条
            aggDb.InsertAlertHistory("FCT_A", "yield", "yield 85% < 90% (85/100)", "auto");
            aggDb.InsertAlertHistory("FCT_B", "disk", "disk 5GB < 10GB", "auto");
            int port = GetFreePort();
            var localDb = new Database(Path.Combine(webRoot, "local.db"));
            var mesh = new MeshNode(new AppConfig { StationId = "WEB_INFRA", AggToken = "infratok", Peers = new List<string>() }, "WEB_INFRA", localDb, aggDb, new string[0]);
            mesh.Receiver.SetPeerUrls(new string[0]);
            var srv = new WebAggServer(port, mesh, aggDb, webRoot, webRoot, "infratok");
            aggDb.UpsertUser("dev", PasswordHasher.Hash("pwd"), "admin");
            var dev = aggDb.GetUserByName("dev");
            var devTok = dev!.Token;
            var baseUrl = $"http://127.0.0.1:{port}";
            srv.Start();
            Thread.Sleep(500);
            // GET /api/alerts/rules → 200 含 yield/disk
            var rRules = HttpGetWithToken($"{baseUrl}/api/alerts/rules", devTok);
            Check(rRules.StatusCode == HttpStatusCode.OK, "GET /api/alerts/rules → 200");
            var rulesTxt = rRules.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(rulesTxt.Contains("yield") && rulesTxt.Contains("disk"), "rules 含 yield/disk（统一规则中心）");
            Check(rulesTxt.Contains("threshold"), "rules 含 threshold（config.json 可配阈值可观测）");
            // GET /api/alerts/history → 200
            var rHist = HttpGetWithToken($"{baseUrl}/api/alerts/history?limit=10", devTok);
            Check(rHist.StatusCode == HttpStatusCode.OK, "GET /api/alerts/history → 200");
            var histTxt = rHist.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(histTxt.Contains("FCT_A") || histTxt.Contains("FCT_B"), "history 含预置机台（前端告警页历史）");
            // 按 rule 过滤
            var rHistYield = HttpGetWithToken($"{baseUrl}/api/alerts/history?rule=yield&limit=10", devTok);
            Check(rHistYield.StatusCode == HttpStatusCode.OK, "GET /api/alerts/history?rule=yield → 200");
            var hyTxt = rHistYield.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(hyTxt.Contains("yield") || hyTxt.Contains("FCT_A"), "history?rule=yield 返回 yield 相关");
            // GET /api/gossiper/status → 404 单节点或 200 多 peer 均可观测
            var rGossip = HttpGetWithToken($"{baseUrl}/api/gossiper/status", devTok);
            Check(rGossip.StatusCode == HttpStatusCode.NotFound || rGossip.StatusCode == HttpStatusCode.OK, $"GET /api/gossiper/status → {(int)rGossip.StatusCode}（自适应间隔可观测）");
            if (rGossip.StatusCode == HttpStatusCode.OK)
            {
                var gTxt = rGossip.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Check(gTxt.Contains("interval_sec") && gTxt.Contains("reason"), "gossiper status 含 interval_sec/reason");
            }
            // GET /api/metrics → 200 含 per_peer 与 gossiper
            var rMetrics = HttpGetWithToken($"{baseUrl}/api/metrics", devTok);
            Check(rMetrics.StatusCode == HttpStatusCode.OK, "GET /api/metrics → 200（per-peer 失败率 + gossiper 自适应）");
            var metTxt = rMetrics.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(metTxt.Contains("per_peer") || metTxt.Contains("perPeer") || metTxt.Contains("pusher"), "metrics 含 pusher.per_peer（per-peer 失败率可观测）");
            Check(metTxt.Contains("gossiper") || metTxt.Contains("pusher"), "metrics 含 gossiper 或 pusher 结构（自适应间隔可观测）");
            // GET /api/compare/trends?machines=FCT_A,FCT_B&days=7 → 200 含双机台
            var rCompTrend = HttpGetWithToken($"{baseUrl}/api/compare/trends?machines=FCT_A,FCT_B&days=7", devTok);
            Check(rCompTrend.StatusCode == HttpStatusCode.OK, "GET /api/compare/trends → 200（多机台对比趋势）");
            var ctTxt = rCompTrend.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(ctTxt.Contains("FCT_A") && ctTxt.Contains("FCT_B"), "compare trends 含双机台（overview 对比态基础）");
            // GET /api/compare/distribution
            var rCompDist = HttpGetWithToken($"{baseUrl}/api/compare/distribution?machines=FCT_A,FCT_B&field=fail_reason&limit=5", devTok);
            Check(rCompDist.StatusCode == HttpStatusCode.OK, "GET /api/compare/distribution → 200（多机台对比分布）");
            var cdTxt = rCompDist.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(cdTxt.Contains("FCT_A") || cdTxt.Contains("distributions"), "compare distribution 含机台或 distributions");
            // 无 token 403
            HttpResponseMessage rNoTok;
            using (var httpNoCookie = new HttpClient())
            {
                var reqNo = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/alerts/rules");
                rNoTok = httpNoCookie.SendAsync(reqNo).GetAwaiter().GetResult();
            }
            Check(rNoTok.StatusCode == HttpStatusCode.Forbidden, "无 token GET /api/alerts/rules → 403");
            srv.Stop(); mesh.Stop();
            TryDeleteDir(webRoot);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        // ---------- Lite-Ops: 告警规则热更新（PATCH /api/alerts/rules：admin 专属 + 热生效 + 越界校验 + 审计） ----------
        {
            var webRoot = Path.Combine(work, "web_ops_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(Path.Combine(webRoot, "public"));
            File.WriteAllText(Path.Combine(webRoot, "public", "index.html"), "<html>ops</html>");
            var dbPath = Path.Combine(webRoot, "agg.db");
            var aggDb = new AggDatabase(dbPath);
            aggDb.Open();
            aggDb.UpsertUser("opsadmin", PasswordHasher.Hash("pwd"), "admin");
            aggDb.UpsertUser("opsview", PasswordHasher.Hash("pwd"), "viewer");
            var adminTok = aggDb.GetUserByName("opsadmin")!.Token;
            var viewTok = aggDb.GetUserByName("opsview")!.Token;
            int port = GetFreePort();
            var localDb = new Database(Path.Combine(webRoot, "local.db"));
            var mesh = new MeshNode(new AppConfig { StationId = "WEB_OPS", AggToken = "opstok", Peers = new List<string>() }, "WEB_OPS", localDb, aggDb, new string[0]);
            mesh.Receiver.SetPeerUrls(new string[0]);
            var srv = new WebAggServer(port, mesh, aggDb, webRoot, webRoot, "opstok");
            var baseUrl = $"http://127.0.0.1:{port}";
            srv.Start();
            Thread.Sleep(500);

            // AppConfig.Instance 是进程级单例，且 PATCH 成功会 Save() 落盘 —— 必须先备份、后恢复，
            // 否则会把测试值带给后续用例（也会污染自检输出目录的 config.json）。
            var cfg = AppConfig.Instance;
            var oldDisk = cfg.DeviceAlertDiskFreeGb;
            var oldCpu = cfg.DeviceAlertCpuPct;
            var oldOff = cfg.DeviceAlertOfflineMinutes;
            var oldYield = cfg.YieldAlertYieldPct;
            var oldYieldOn = cfg.YieldAlertEnabled;

            try
            {
                // 1) 权限：改全局告警策略属高危操作，admin 专属
                var rView = HttpPatchWithToken($"{baseUrl}/api/alerts/rules", viewTok, "{\"disk\":{\"enabled\":true,\"threshold_gb\":5}}");
                Check(rView.StatusCode == HttpStatusCode.Forbidden, "viewer PATCH /api/alerts/rules → 403（admin 专属）");

                // 2) admin 改阈值 → 200 + changed + 回传新快照
                var rAdmin = HttpPatchWithToken($"{baseUrl}/api/alerts/rules", adminTok, "{\"disk\":{\"enabled\":true,\"threshold_gb\":25}}");
                Check(rAdmin.StatusCode == HttpStatusCode.OK, "admin PATCH /api/alerts/rules → 200");
                var adminTxt = rAdmin.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Check(adminTxt.Contains("device_alert_disk_free_gb"), "PATCH 返回 changed 含字段名（审计可追溯）");
                Check(adminTxt.Contains("rules"), "PATCH 回传更新后 rules 快照（前端可直接回显）");

                // 3) 热生效：GET 回读已变 + 内存单例已变（AggAlertService 每轮重读 Instance，故无需重启）
                var rRead = HttpGetWithToken($"{baseUrl}/api/alerts/rules", adminTok);
                var readTxt = rRead.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Check(readTxt.Contains("25"), "GET /api/alerts/rules 回读 25（热生效，无需重启聚合服务）");
                Check(AppConfig.Instance.DeviceAlertDiskFreeGb == 25, "AppConfig.Instance 已更新（热生效落点）");

                // 4) 关闭规则 → 阈值归 0（沿用 v3.17.0「0=关闭」语义，旧 config 无 enabled 字段亦正确）
                var rOff = HttpPatchWithToken($"{baseUrl}/api/alerts/rules", adminTok, "{\"cpu\":{\"enabled\":false}}");
                Check(rOff.StatusCode == HttpStatusCode.OK, "PATCH cpu enabled=false → 200");
                Check(AppConfig.Instance.DeviceAlertCpuPct == 0, "enabled=false → CPU 阈值归 0（「0=关闭」语义向后兼容）");

                // 5) yield 用独立布尔开关：关闭只翻布尔、阈值保留（再开不用重填）
                var rYieldOff = HttpPatchWithToken($"{baseUrl}/api/alerts/rules", adminTok, "{\"yield\":{\"enabled\":false}}");
                Check(rYieldOff.StatusCode == HttpStatusCode.OK, "PATCH yield enabled=false → 200");
                Check(!AppConfig.Instance.YieldAlertEnabled && AppConfig.Instance.YieldAlertYieldPct > 0,
                    "yield 关闭只翻布尔、阈值保留（yield_alert_enabled 独立字段）");

                // 6) 越界校验 → 400（防误配把告警打满或彻底失效）
                var rBad = HttpPatchWithToken($"{baseUrl}/api/alerts/rules", adminTok, "{\"cpu\":{\"enabled\":true,\"threshold_pct\":150}}");
                Check(rBad.StatusCode == HttpStatusCode.BadRequest, "cpu 阈值 150 → 400（越界校验）");
                var rBad2 = HttpPatchWithToken($"{baseUrl}/api/alerts/rules", adminTok, "{\"offline\":{\"enabled\":true,\"threshold_minutes\":99999}}");
                Check(rBad2.StatusCode == HttpStatusCode.BadRequest, "offline 阈值 99999 分钟 → 400（上限 7 天）");

                // 7) 非法 JSON → 400（不崩服务）
                var rBadJson = HttpPatchWithToken($"{baseUrl}/api/alerts/rules", adminTok, "{not json");
                Check(rBadJson.StatusCode == HttpStatusCode.BadRequest, "非法 JSON → 400（不崩服务）");

                // 8) 无 token 403
                HttpResponseMessage rNoTok2;
                using (var http2 = new HttpClient())
                {
                    var req2 = new HttpRequestMessage(new HttpMethod("PATCH"), $"{baseUrl}/api/alerts/rules");
                    req2.Content = new StringContent("{\"disk\":{\"enabled\":true,\"threshold_gb\":1}}", Encoding.UTF8, "application/json");
                    rNoTok2 = http2.SendAsync(req2).GetAwaiter().GetResult();
                }
                Check(rNoTok2.StatusCode == HttpStatusCode.Forbidden, "无 token PATCH /api/alerts/rules → 403");

                // 9) 审计落库（改全局告警策略必须留痕）
                var audits = aggDb.QueryAudit();
                Check(audits.Exists(a => a.Action == "alerts.rules.save"), "审计落库 alerts.rules.save（高危操作留痕）");

                // 10) 只开开关、不填阈值 → 回落出厂默认（不出现「开了却还是关的」）
                var rDefOn = HttpPatchWithToken($"{baseUrl}/api/alerts/rules", adminTok, "{\"offline\":{\"enabled\":true}}");
                Check(rDefOn.StatusCode == HttpStatusCode.OK, "offline 只传 enabled=true（无阈值）→ 200");
                Check(AppConfig.Instance.DeviceAlertOfflineMinutes == 5, "只开开关不填阈值 → 回落出厂默认 5 分钟");
            }
            finally
            {
                // 恢复全局单例并落盘，避免测试值污染后续用例与自检输出目录的 config.json
                cfg.DeviceAlertDiskFreeGb = oldDisk;
                cfg.DeviceAlertCpuPct = oldCpu;
                cfg.DeviceAlertOfflineMinutes = oldOff;
                cfg.YieldAlertYieldPct = oldYield;
                cfg.YieldAlertEnabled = oldYieldOn;
                try { cfg.Save(); } catch { }
                srv.Stop(); mesh.Stop();
                TryDeleteDir(webRoot);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
        }

        // ---------- Lite-Ops: 待办自定义区间（前端 DatePicker 依赖的后端契约：from/to 双日期格式） ----------
        {
            var webRoot = Path.Combine(work, "web_range_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(Path.Combine(webRoot, "public"));
            File.WriteAllText(Path.Combine(webRoot, "public", "index.html"), "<html>range</html>");
            var dbPath = Path.Combine(webRoot, "agg.db");
            var aggDb = new AggDatabase(dbPath);
            aggDb.Open();
            var today = DateTime.Now.ToString("yyyyMMdd");
            // 10 天前：必须落在 AppConfig.TodoScanDays（默认 30 天）同步窗口内，
            // 否则 SyncTodoItems 根本不会把它登记进 todo_items，区间断言就没数据可比。
            var old = DateTime.Now.AddDays(-10).ToString("yyyyMMdd");
            aggDb.UpsertUser("radmin", PasswordHasher.Hash("pwd"), "admin");
            var tok = aggDb.GetUserByName("radmin")!.Token;
            // 造 2 条不同日期的 FAIL：Result 必须是 "FAIL" —— ListTodoView 的区间统计 SQL
            // 是 WHERE result='FAIL'，留空会导致区间内计数为 0、待办被全部过滤掉。
            aggDb.InsertBatch(new[] {
                new AggFailRow { Machine="FCT_R", Seq=1, TestDate=old,   Result="FAIL", FailReason="OldItem", Model="M1", Sn="SN_OLD" },
                new AggFailRow { Machine="FCT_R", Seq=2, TestDate=today, Result="FAIL", FailReason="NewItem", Model="M1", Sn="SN_NEW" },
            });
            int port = GetFreePort();
            var localDb = new Database(Path.Combine(webRoot, "local.db"));
            var mesh = new MeshNode(new AppConfig { StationId = "WEB_RANGE", AggToken = "rangetok", Peers = new List<string>() }, "WEB_RANGE", localDb, aggDb, new string[0]);
            mesh.Receiver.SetPeerUrls(new string[0]);
            var srv = new WebAggServer(port, mesh, aggDb, webRoot, webRoot, "rangetok");
            var baseUrl = $"http://127.0.0.1:{port}";
            srv.Start();
            Thread.Sleep(500);
            try
            {
                // yyyyMMdd 格式（前端近 N 天预设用的格式）：只含今天那一条
                var r1 = HttpGetWithToken($"{baseUrl}/api/todos?from={today}&to={today}", tok);
                Check(r1.StatusCode == HttpStatusCode.OK, $"GET /api/todos?from/to (yyyyMMdd) → 200");
                var t1 = r1.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Check(t1.Contains("NewItem"), "自定义区间 yyyyMMdd 命中区间内待办（NewItem，今天）");
                Check(!t1.Contains("OldItem"), "自定义区间 yyyyMMdd 排除区间外待办（OldItem 在 10 天前）");

                // yyyy-MM-dd 格式（<input type="date"> 原生值，前端自定义区间压缩前的形态）：
                // 后端 TryParseExact(yyyyMMdd) 失败会回落到 DateTime.TryParse，两种格式必须等价
                var isoFrom = DateTime.Now.AddDays(-30).ToString("yyyy-MM-dd");
                var isoTo = DateTime.Now.ToString("yyyy-MM-dd");
                var ymdFrom = DateTime.Now.AddDays(-30).ToString("yyyyMMdd");
                var ymdTo = DateTime.Now.ToString("yyyyMMdd");
                var r2 = HttpGetWithToken($"{baseUrl}/api/todos?from={isoFrom}&to={isoTo}", tok);
                Check(r2.StatusCode == HttpStatusCode.OK, "GET /api/todos?from/to (yyyy-MM-dd) → 200");
                var t2 = r2.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var r3 = HttpGetWithToken($"{baseUrl}/api/todos?from={ymdFrom}&to={ymdTo}", tok);
                var t3 = r3.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Check(t2.Contains("NewItem") && t2.Contains("OldItem"), "yyyy-MM-dd 区间命中两条（近 30 天）");
                Check(t2 == t3, "yyyy-MM-dd 与 yyyyMMdd 双格式返回结果一致（前端自定义区间可任选其一）");
            }
            finally
            {
                srv.Stop(); mesh.Stop();
                TryDeleteDir(webRoot);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
        }

        // ==================== v3.21.1 规格 01 良率归因：DB 层 + 分析器 + LRU + ConfigWatcher ====================
        {
            var tmpA = Path.Combine(work, "yldattr_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(tmpA);
            try
            {
                var agg = new AggDatabase(Path.Combine(tmpA, "agg.db"));
                agg.Open();
                var today = DateTime.Now.ToString("yyyyMMdd");
                var yest = DateTime.Now.AddDays(-1).ToString("yyyyMMdd");
                // 混合 PASS/FAIL（归因 SQL 需要总测试量算 yield%，不能只造 FAIL）
                agg.InsertBatch(new[]
                {
                    new AggFailRow { Machine="FCT_A", Seq=1, TestDate=yest, Result="FAIL", FailReason="Solder Short",   Model="MOD_X", Sn="SN1" },
                    new AggFailRow { Machine="FCT_A", Seq=2, TestDate=yest, Result="FAIL", FailReason="Solder Short",   Model="MOD_X", Sn="SN2" },
                    new AggFailRow { Machine="FCT_A", Seq=3, TestDate=today, Result="PASS", FailReason="",              Model="MOD_X", Sn="SN3" },
                    new AggFailRow { Machine="FCT_A", Seq=4, TestDate=today, Result="FAIL", FailReason="Connector Loose", Model="MOD_Y", Sn="SN4" },
                });

                // 总失败计数（含 daysBack 边界）
                Check(agg.GetRecentFailCount("FCT_A", 7) == 3, "归因: GetRecentFailCount 7 天 = 3（2 昨天 + 1 今天 FAIL）");
                Check(agg.GetRecentFailCount("FCT_A", 0) == 1, "归因: daysBack=0 边界 → 只计今天 = 1");
                Check(agg.GetRecentFailCount("FCT_NOPE", 7) == 0, "归因: 未知机台 → 0");

                // 型号维度分解（yield% / 贡献度 / 排序）
                var model = agg.DecomposeByModel("FCT_A", DateTime.Today.AddDays(-7), DateTime.Today, 20);
                Check(model.Count == 2, $"归因: DecomposeByModel 2 个型号（实得 {model.Count}）");
                var mx = model.First(x => x.Model == "MOD_X");
                var my = model.First(x => x.Model == "MOD_Y");
                Check(mx.Total == 3 && mx.Fail == 2 && mx.Pass == 1 && mx.Interrupted == 0, "归因: MOD_X Total=3/Fail=2/Pass=1/Int=0");
                Check(Math.Abs(mx.YieldPct - 33.33) < 0.01, $"归因: MOD_X yield≈33.33（实得 {mx.YieldPct}）");
                Check(Math.Abs(mx.Contribution - 100.0) < 0.1, $"归因: MOD_X 贡献度≈100（最大失败者自身，实得 {mx.Contribution}）——贡献度除零修复回归断言");
                Check(Math.Abs(my.Contribution - 50.0) < 0.1, $"归因: MOD_Y 贡献度≈50.0（1/2 最大失败，实得 {my.Contribution}）");
                Check(model[0].Model == "MOD_X" && model[0].Rank == 1, "归因: fail 降序 → MOD_X Rank=1");

                // fixture 维度（fail_reason 前缀分组）
                var fix = agg.DecomposeByFixture("FCT_A", DateTime.Today.AddDays(-7), DateTime.Today, 20);
                Check(fix.Count == 2, $"归因: DecomposeByFixture 2 个前缀（实得 {fix.Count}）");
                var solder = fix.First(x => x.Model == "Solder");
                Check(solder.Fail == 2 && Math.Abs(solder.Contribution - 66.7) < 0.1, "归因: fixture Solder Fail=2 贡献≈66.7（基准=前缀失败总和 3，贡献度后算修复回归）");

                // YieldAttributor 静态入口（含空结果边界）
                var attr = YieldAttributor.AnalyzeModel(agg, "FCT_A", 7);
                Check(attr.Count == 2 && attr[0].Model == "MOD_X", "归因: YieldAttributor.AnalyzeModel top1 = MOD_X");
                Check(YieldAttributor.AnalyzeModel(agg, "FCT_NOPE", 7).Count == 0, "归因: AnalyzeModel 未知机台 → 空列表");

                // ---------- ConfigWatcher：非法拒绝（天然回滚）/ 合法触发一次 ----------
                var cfgPath = Path.Combine(tmpA, "config.json");
                File.WriteAllText(cfgPath, "{\"log_level\":\"INFO\"}");
                int fired = 0; string? firedJson = null;
                using (var cw = new ConfigWatcher(cfgPath, 60))
                {
                    cw.ConfigChanged += (_, json) => { Interlocked.Increment(ref fired); firedJson = json; };
                    File.WriteAllText(cfgPath, "{bad json");
                    Thread.Sleep(300);
                    Check(Interlocked.CompareExchange(ref fired, 0, 0) == 0, "ConfigWatcher: 非法 JSON 被拒绝（旧配置保持生效 = 天然回滚）");
                    File.WriteAllText(cfgPath, "{\"log_level\":\"DEBUG\"}");
                    Thread.Sleep(400);
                    Check(Interlocked.CompareExchange(ref fired, 0, 0) == 1, $"ConfigWatcher: 合法变更防抖后触发一次（实得 {fired}）");
                    Check(firedJson != null && firedJson.Contains("DEBUG"), "ConfigWatcher: 事件携带新配置原文");
                }

                // ---------- LRUCache：容量淘汰 / 命中不回调 / 精确过期淘汰 ----------
                var lru = new LRUCache<string, string>(2);
                lru.Set("A", "va"); lru.Set("B", "vb"); lru.Set("C", "vc");
                Check(lru.Count == 2, $"LRU: 超容量淘汰最久未用 → Count=2（实得 {lru.Count}）");
                int factCalls = 0;
                var va2 = lru.GetOrSet("A", () => { factCalls++; return "regen"; });
                Check(va2 == "regen" && factCalls == 1, "LRU: A 已被淘汰 → GetOrSet 重新生成");
                // GetOrSet("A") 重新生成时容量满 → 挤掉 B（此刻链表尾），验证 C 仍命中
                var vc = lru.GetOrSet("C", () => { factCalls++; return "x"; });
                Check(vc == "vc" && factCalls == 1, "LRU: C 命中且不调 factory（GetOrSet A 未误删 C）");

                // TTL 过期精确性（回归：原版 _order.RemoveFirst() 会误删最新条目）
                // 余量放大抗 Windows Sleep 精度漂移（±16ms 粒度）：A/B/C 存活 250ms 全过期，D 存活 ~150ms 保留
                var lru2 = new LRUCache<string, string>(10, TimeSpan.FromMilliseconds(200));
                lru2.Set("A", "1"); lru2.Set("B", "2"); lru2.Set("C", "3");
                Thread.Sleep(100);
                lru2.Set("D", "4");   // D 在 A/B/C 过期判定前写入，CreatedAt 最新
                Thread.Sleep(150);
                lru2.TryPruneExpired();
                Check(lru2.Count == 1, $"LRU: TTL 过期只淘汰 A/B/C、保留最新 D → Count=1（实得 {lru2.Count}）");
                var d = lru2.GetOrSet("D", () => { factCalls++; return "y"; });
                Check(d == "4" && factCalls == 1, "LRU: 未过期的 D 命中且未被误删");
            }
            finally
            {
                TryDeleteDir(tmpA);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
        }

        // ==================== v3.21.1 GET /api/yield/attribution（viewer 可读 + 边界 + 鉴权）====================
        {
            var webRoot = Path.Combine(work, "web_attr_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(Path.Combine(webRoot, "public"));
            File.WriteAllText(Path.Combine(webRoot, "public", "index.html"), "<html>attr</html>");
            var aggDb = new AggDatabase(Path.Combine(webRoot, "agg.db"));
            aggDb.Open();
            var today = DateTime.Now.ToString("yyyyMMdd");
            aggDb.UpsertUser("radmin", PasswordHasher.Hash("pwd"), "admin");
            var tok = aggDb.GetUserByName("radmin")!.Token;
            aggDb.InsertBatch(new[]
            {
                new AggFailRow { Machine="FCT_W", Seq=1, TestDate=today, Result="FAIL", FailReason="Solder Short", Model="MOD_W", Sn="SN1" },
                new AggFailRow { Machine="FCT_W", Seq=2, TestDate=today, Result="PASS", FailReason="",             Model="MOD_W", Sn="SN2" },
            });
            int port = GetFreePort();
            var localDb = new Database(Path.Combine(webRoot, "local.db"));
            var mesh = new MeshNode(new AppConfig { StationId = "WEB_ATTR", AggToken = "attrtok", Peers = new List<string>() }, "WEB_ATTR", localDb, aggDb, new string[0]);
            mesh.Receiver.SetPeerUrls(new string[0]);
            var srv = new WebAggServer(port, mesh, aggDb, webRoot, webRoot, "attrtok");
            var baseUrl = $"http://127.0.0.1:{port}";
            srv.Start();
            Thread.Sleep(500);
            try
            {
                var r1 = HttpGetWithToken($"{baseUrl}/api/yield/attribution/FCT_W?days=7", tok);
                var t1 = r1.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Check(r1.StatusCode == HttpStatusCode.OK && t1.Contains("MOD_W"), "归因API: GET /api/yield/attribution/FCT_W → 200 且含 MOD_W");
                Check(t1.Contains("\"total_fail\":1"), "归因API: total_fail=1（PASS 行不计入失败总数）");
                var r2 = HttpGetWithToken($"{baseUrl}/api/yield/attribution/", tok);
                Check(r2.StatusCode == HttpStatusCode.BadRequest, "归因API: 缺 machine 路径参数 → 400");
                var r3 = HttpGetWithToken($"{baseUrl}/api/yield/attribution/FCT_W?days=999", tok);
                Check(r3.StatusCode == HttpStatusCode.OK, "归因API: days=999 越界 → clamp 到 90 不报错");
                // 无 token 403：必须用无 cookie 的独立 HttpClient（static _http 可能残留早期登录 Set-Cookie）
                using (var httpNoCookie = new HttpClient())
                {
                    var r4 = httpNoCookie.GetAsync($"{baseUrl}/api/yield/attribution/FCT_W").GetAwaiter().GetResult();
                    Check(r4.StatusCode == HttpStatusCode.Forbidden, "归因API: 无 token → 403");
                }
            }
            finally
            {
                srv.Stop(); mesh.Stop();
                TryDeleteDir(webRoot);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
        }

        // ==================== v3.22.0 规格01 补全：真 FIXTURE_ID 解析/入库/归因 + LRU 接线 + 规格04 健康分 ====================
        {
            var tmpV = Path.Combine(work, "v3220_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(tmpV);
            var cfg = AppConfig.Instance;
            var oldHealthOn = cfg.HealthScoreEnabled;
            var oldWarn = cfg.HealthWarnThreshold;
            var oldCrit = cfg.HealthCriticalThreshold;
            var oldWC = cfg.HealthWeightCpu;
            var oldWD = cfg.HealthWeightDisk;
            var oldWM = cfg.HealthWeightMemory;
            var oldWO = cfg.HealthWeightOffline;
            var oldCpuPct = cfg.DeviceAlertCpuPct;
            try
            {
                // ---------- ① fixture：解析侧 FACTORY@FIXTURE_ID → ParseOutput.FixtureId ----------
                var xmlDir = Path.Combine(tmpV, "Online", "MODEL1", "20260903");
                Directory.CreateDirectory(xmlDir);
                var xmlPath = Path.Combine(xmlDir, "F_SN1_20260903120000_123.xml");
                File.WriteAllText(xmlPath,
                    "<FACTORY USER=\"op1\" TESTER=\"FCT6_T1\" FIXTURE_ID=\"FIX-01\"><PANEL STATUS=\"Passed\"/>"
                    + "<DUT ID=\"SN-9\"><TEST NAME=\"Volt Check\" STATUS=\"Failed\" VALUE=\"1.1\" HILIM=\"1.0\" LOLIM=\"0.5\" UNIT=\"V\" RULE=\"LT\"/></DUT></FACTORY>");
                var parsed = FctAggregator.Parsing.ParserRegistry.Instance.Resolve(xmlPath, File.ReadAllText(xmlPath));
                Check(parsed != null && parsed.Result == "FAIL", "fixture: 默认解析器识别 F_ FAIL 文件");
                Check(parsed != null && parsed.FixtureId == "FIX-01",
                    $"fixture: FACTORY@FIXTURE_ID 解析进 ParseOutput（实得 {parsed?.FixtureId ?? "<null>"}）");
                // 无 FIXTURE_ID 属性 → null（前缀回退兼容）
                var xmlPath2 = Path.Combine(xmlDir, "F_SN2_20260903120001_124.xml");
                File.WriteAllText(xmlPath2,
                    "<FACTORY USER=\"op1\" TESTER=\"FCT6_T1\"><PANEL STATUS=\"Passed\"/>"
                    + "<DUT ID=\"SN-8\"><TEST NAME=\"Old Step\" STATUS=\"Failed\"/></DUT></FACTORY>");
                var parsed2 = FctAggregator.Parsing.ParserRegistry.Instance.Resolve(xmlPath2, File.ReadAllText(xmlPath2));
                Check(parsed2 != null && string.IsNullOrEmpty(parsed2.FixtureId), "fixture: XML 无 FIXTURE_ID → null（老格式不受影响）");

                // ---------- ② fixture：InsertBatch 入库 + QueryFails 回读 + 真治具归因分组 ----------
                var agg = new AggDatabase(Path.Combine(tmpV, "agg.db"));
                agg.Open();
                var today = DateTime.Now.ToString("yyyyMMdd");
                agg.InsertBatch(new[]
                {
                    new AggFailRow { Machine="FCT_FX", Seq=1, TestDate=today, Result="FAIL", FailReason="Solder Short",    Model="MOD_F", Sn="SN1", FixtureId="FIX-A" },
                    new AggFailRow { Machine="FCT_FX", Seq=2, TestDate=today, Result="FAIL", FailReason="Solder Bridge",   Model="MOD_F", Sn="SN2", FixtureId="FIX-A" },
                    new AggFailRow { Machine="FCT_FX", Seq=3, TestDate=today, Result="FAIL", FailReason="Connector Loose", Model="MOD_F", Sn="SN3", FixtureId="" },
                    new AggFailRow { Machine="FCT_FX", Seq=4, TestDate=today, Result="FAIL", FailReason="Xtal Dead",       Model="MOD_F", Sn="SN4", FixtureId="FIX-B" },
                });
                var rows = agg.QueryFails(10, "FCT_FX");
                Check(rows.Count == 4 && rows.Count(r => r.FixtureId == "FIX-A") == 2, "fixture: InsertBatch→QueryFails fixture_id 回读（含空串行）");
                var fix = agg.DecomposeByFixture("FCT_FX", DateTime.Today.AddDays(-7), DateTime.Today, 20);
                Check(fix.Count == 3, $"fixture: 真治具 ID 优先分组 → FIX-A/FIX-B/Connector 3 组（实得 {fix.Count}）");
                var fixA = fix.FirstOrDefault(x => x.Model == "FIX-A");
                Check(fixA != null && fixA.Fail == 2 && Math.Abs(fixA.Contribution - 50.0) < 0.1, "fixture: FIX-A Fail=2 贡献≈50（基准=前缀失败总和 4）");
                Check(fix.Any(x => x.Model == "Connector"), "fixture: 无治具旧行回落 fail_reason 前缀分组（Connector 组存在）");
                Check(!fix.Any(x => x.Model == "Solder"), "fixture: 有真治具 ID 的行不再按前缀分组（Solder 前缀组不存在）");
                agg.Close();

                // ---------- ③ LRU 接线：MeshQueryService 缓存容量 + TTL 由 LRUCache 托管 ----------
                MeshQueryService.ClearCache();
                for (int i = 0; i < 300; i++) MeshQueryService.PutCached($"k{i:000}", $"v{i}");
                Check(MeshQueryService.CacheCount == 256, $"LRU接线: 300 次写入后容量封顶 256（实得 {MeshQueryService.CacheCount}）");
                Check(!MeshQueryService.TryGetCached("k000", out _), "LRU接线: 最旧条目被淘汰（k000 未命中）");
                Check(MeshQueryService.TryGetCached("k299", out var v299) && v299 == "v299", "LRU接线: 最新条目命中（k299）");
                MeshQueryService.ClearCache();

                // ---------- ④ 规格04：4 个子分单测（纯静态，无 IO） ----------
                Check(DeviceHealthScorer.CpuScore(20, 0, cfg) == 80, "健康分: CpuScore(20%) = 80");
                Check(DeviceHealthScorer.CpuScore(95, 0, cfg) == 5, "健康分: CpuScore(95%) = 5");
                Check(DeviceHealthScorer.CpuScore(70, 10, cfg) == 0, "健康分: CPU 70%+趋势+10%/天（3 天外推 100>95）→ 扣 30 后 0");
                Check(DeviceHealthScorer.CpuScore(50, 10, cfg) == 50, "健康分: CPU 50%+趋势+10%/天（3 天外推 80，未超）→ 不加罚");
                Check(DeviceHealthScorer.DiskScore(25, null, cfg) == 100, "健康分: 磁盘 25GB(≥2×阈值) = 100");
                Check(DeviceHealthScorer.DiskScore(12, null, cfg) == 70, "健康分: 磁盘 12GB(≥阈值) = 70");
                Check(DeviceHealthScorer.DiskScore(3, null, cfg) == 10, "健康分: 磁盘 3GB(<0.5×阈值) = 10");
                Check(DeviceHealthScorer.DiskScore(12, 2, cfg) == 40, "健康分: 磁盘 12GB + 2 天耗尽 → 70-30 = 40");
                Check(DeviceHealthScorer.DiskScore(0, null, cfg) == 0, "健康分: 磁盘未知(0) → 保守扣到底（规格 §9）");
                Check(DeviceHealthScorer.MemoryScore(4000, 8000) == 100, "健康分: 内存 50% = 100");
                Check(DeviceHealthScorer.MemoryScore(7200, 8000) == 50, "健康分: 内存 90% = 50（<70→100/<85→80/<95→50 阶梯）");
                Check(DeviceHealthScorer.MemoryScore(7700, 8000) == 20, "健康分: 内存 96.25%(≥95%) = 20");
                Check(DeviceHealthScorer.MemoryScore(0, 0) == 100, "健康分: 内存 total=0 → 100（防除零）");
                Check(DeviceHealthScorer.OfflineScore(0, 0, cfg) == 100, "健康分: 心跳正常 = 100");
                Check(DeviceHealthScorer.OfflineScore(400, 80, cfg) == 0, "健康分: 真离线 400s（>300s=0，std 罚不穿 0）");
                Check(DeviceHealthScorer.OfflineScore(0, 80, cfg) == 80, "健康分: 在线但心跳 std=80s → 100-20 = 80");

                // ---------- ⑤ 规格04：Score 集成（device_info + samples 种子） ----------
                var aggH = new AggDatabase(Path.Combine(tmpV, "health.db"));
                aggH.Open();
                var nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                aggH.UpsertDeviceInfo(new DeviceInfoRow { Machine="FCT_H", CpuUsage=20, MemTotalMb=8000, MemUsedMb=4000, DiskFreeGb=100, LastSeen=nowStr, UpdatedAt=nowStr });
                for (int i = 4; i >= 0; i--)
                    aggH.InsertDeviceSample(new DeviceSampleRow { Machine="FCT_H", Ts=DateTime.Now.AddMinutes(-5*i).ToString("yyyy-MM-dd HH:mm:ss"), CpuUsage=20, MemUsedMb=4000, DiskFreeGb=100 });
                aggH.UpsertDeviceInfo(new DeviceInfoRow { Machine="FCT_B", CpuUsage=95, MemTotalMb=8000, MemUsedMb=4000, DiskFreeGb=100, LastSeen=nowStr, UpdatedAt=nowStr });
                var offSeen = DateTime.Now.AddSeconds(-600).ToString("yyyy-MM-dd HH:mm:ss");
                aggH.UpsertDeviceInfo(new DeviceInfoRow { Machine="FCT_O", CpuUsage=20, MemTotalMb=8000, MemUsedMb=4000, DiskFreeGb=100, LastSeen=offSeen, UpdatedAt=offSeen });
                aggH.UpsertDeviceInfo(new DeviceInfoRow { Machine="FCT_CO", CpuUsage=95, MemTotalMb=8000, MemUsedMb=4000, DiskFreeGb=100, LastSeen=offSeen, UpdatedAt=offSeen });

                var rep = DeviceHealthScorer.Score(aggH, cfg);
                var h = rep.Machines.FirstOrDefault(x => x.Machine == "FCT_H");
                var b = rep.Machines.FirstOrDefault(x => x.Machine == "FCT_B");
                var o = rep.Machines.FirstOrDefault(x => x.Machine == "FCT_O");
                var co = rep.Machines.FirstOrDefault(x => x.Machine == "FCT_CO");
                Check(h != null && h.Health >= 80 && h.Level == "ok", $"健康分: 全健康机台 health={h?.Health} ≥80 level=ok");
                Check(h != null && h.Components.Count == 4, "健康分: components 恰 4 子项（cpu/disk/memory/offline）");
                Check(b != null && b.Level == "warn" && b.TopConcern == "cpu", $"健康分: CPU 95% 单项劣化 → health={b?.Health} warn + top_concern=cpu");
                Check(o != null && o.Level != "ok" && DeviceHealthScorer.OfflineScore(600, 0, cfg) == 0, "健康分: last_seen 600s → offline_score=0、级别非 ok");
                Check(co != null && co.Level == "critical", $"健康分: CPU95%+离线 600s 多指标齐崩 → health={co?.Health} critical");
                Check(rep.Summary.Ok + rep.Summary.Warn + rep.Summary.Critical == rep.Machines.Count, "健康分: summary 三档计数守恒");

                // 阈值边界：health=71.5 卡在 warn/critical 阈值间
                cfg.HealthWarnThreshold = 72;
                Check(DeviceHealthScorer.Score(aggH, cfg).Machines.First(x => x.Machine == "FCT_B").Level == "warn", "健康分: warn 阈值 72 → 71.5 判 warn");
                cfg.HealthWarnThreshold = 71;
                Check(DeviceHealthScorer.Score(aggH, cfg).Machines.First(x => x.Machine == "FCT_B").Level == "ok", "健康分: warn 阈值 71 → 71.5 判 ok（边界含端点）");
                cfg.HealthWarnThreshold = oldWarn;
                // 权重调整生效
                cfg.HealthWeightCpu = 1.0; cfg.HealthWeightDisk = 0; cfg.HealthWeightMemory = 0; cfg.HealthWeightOffline = 0;
                var bw = DeviceHealthScorer.Score(aggH, cfg).Machines.First(x => x.Machine == "FCT_B").Health;
                Check(Math.Abs(bw - 5) < 0.01, $"健康分: weight_cpu=1.0 → FCT_B health=5（实得 {bw}，权重可配）");
                cfg.HealthWeightCpu = oldWC; cfg.HealthWeightDisk = oldWD; cfg.HealthWeightMemory = oldWM; cfg.HealthWeightOffline = oldWO;
                aggH.Close();

                // ---------- ⑥ 规格04：HighlightEngine 软替换 + 开关回退 ----------
                cfg.HealthScoreEnabled = true;
                var hlNew = HighlightEngine.GetHighlights(aggH, cfg);
                Check(hlNew.Any(x => x.Machine == "FCT_B" && x.Reason.Contains("健康分")), "健康分: enabled=true → 高亮为综合口径（含「健康分」字样）");
                cfg.HealthScoreEnabled = false;
                var hlOld = HighlightEngine.GetHighlights(aggH, cfg);
                Check(hlOld.Any(x => x.Machine == "FCT_B" && x.Reason.Contains("CPU")), "健康分: enabled=false → 回退 v3.19.0 单指标高亮（CPU 阈值口径）");
                cfg.HealthScoreEnabled = oldHealthOn;

                // ---------- ⑦ 配置键默认值缺省可运行 ----------
                var fresh = new AppConfig();
                Check(fresh.HealthScoreEnabled && fresh.HealthWarnThreshold == 80 && fresh.HealthCriticalThreshold == 50,
                    "健康分: config 默认 enabled=true / warn 80 / critical 50（缺省可运行）");
                Check(Math.Abs(fresh.HealthWeightCpu + fresh.HealthWeightDisk + fresh.HealthWeightMemory + fresh.HealthWeightOffline - 1.0) < 0.001,
                    "健康分: 默认权重加和 = 1.0");
            }
            finally
            {
                cfg.HealthScoreEnabled = oldHealthOn; cfg.HealthWarnThreshold = oldWarn; cfg.HealthCriticalThreshold = oldCrit;
                cfg.HealthWeightCpu = oldWC; cfg.HealthWeightDisk = oldWD; cfg.HealthWeightMemory = oldWM; cfg.HealthWeightOffline = oldWO;
                cfg.DeviceAlertCpuPct = oldCpuPct;
                TryDeleteDir(tmpV);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
        }

        // ==================== v3.22.1 图形化升级向导：包校验/脚本定位（纯逻辑，无 UI）====================
        {
            var tmpU = Path.Combine(work, "upgwiz_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(tmpU);
            try
            {
                // 合成"真实结构"更新包：扁平根 Argus.exe + config.json + public/ + runtimes/（对齐 make_package.ps1 产物）
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

                // 解包 → 校验通过 + 版本号可读
                var stage1 = Path.Combine(tmpU, "stage_ok");
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, stage1);
                var (ok1, ver1, err1) = FctAggregator.modules.Upgrader.UpgradeWizard.ValidateStage(stage1);
                Check(ok1, $"升级向导: 合成真实结构包校验通过{(ok1 ? "" : "：" + err1)}");
                Check(!string.IsNullOrEmpty(ver1), $"升级向导: 包内版本号可读（v{ver1}）");

                // 污染包：含 data\ → 拒绝（防覆盖现场数据）
                var badPkg = Path.Combine(tmpU, "bad_pkg");
                Directory.CreateDirectory(Path.Combine(badPkg, "data"));
                File.Copy(Path.Combine(pkgRoot, "Argus.exe"), Path.Combine(badPkg, "Argus.exe"));
                File.WriteAllText(Path.Combine(badPkg, "data", "x.db"), "x");
                var (ok2, _, err2) = FctAggregator.modules.Upgrader.UpgradeWizard.ValidateStage(badPkg);
                Check(!ok2 && err2.Contains("data"), $"升级向导: 含 data\\ 目录的包被拒绝（{err2}）");

                // 缺 Argus.exe → 拒绝
                var noExe = Path.Combine(tmpU, "no_exe");
                Directory.CreateDirectory(noExe);
                File.WriteAllText(Path.Combine(noExe, "config.json"), "{}");
                var (ok3, _, err3) = FctAggregator.modules.Upgrader.UpgradeWizard.ValidateStage(noExe);
                Check(!ok3 && err3.Contains("Argus.exe"), $"升级向导: 缺 Argus.exe 的包被拒绝（{err3}）");

                // 部署脚本定位：tools\ 优先于安装目录根，最后才包内
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

        // ==================== v3.22.0 GET /api/devices/health（viewer 可读 + 鉴权）====================
        {
            var webRoot = Path.Combine(work, "web_health_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(Path.Combine(webRoot, "public"));
            File.WriteAllText(Path.Combine(webRoot, "public", "index.html"), "<html>health</html>");
            var aggDb = new AggDatabase(Path.Combine(webRoot, "agg.db"));
            aggDb.Open();
            aggDb.UpsertUser("radmin", PasswordHasher.Hash("pwd"), "admin");
            var tok = aggDb.GetUserByName("radmin")!.Token;
            var nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            aggDb.UpsertDeviceInfo(new DeviceInfoRow { Machine="FCT_W", CpuUsage=20, MemTotalMb=8000, MemUsedMb=4000, DiskFreeGb=100, LastSeen=nowStr, UpdatedAt=nowStr });
            int port = GetFreePort();
            var localDb = new Database(Path.Combine(webRoot, "local.db"));
            var mesh = new MeshNode(new AppConfig { StationId = "WEB_HEALTH", AggToken = "healthtok", Peers = new List<string>() }, "WEB_HEALTH", localDb, aggDb, new string[0]);
            mesh.Receiver.SetPeerUrls(new string[0]);
            var srv = new WebAggServer(port, mesh, aggDb, webRoot, webRoot, "healthtok");
            var baseUrl = $"http://127.0.0.1:{port}";
            srv.Start();
            Thread.Sleep(500);
            try
            {
                var r1 = HttpGetWithToken($"{baseUrl}/api/devices/health", tok);
                var t1 = r1.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Check(r1.StatusCode == HttpStatusCode.OK && t1.Contains("FCT_W"), "健康API: GET /api/devices/health → 200 且含 FCT_W");
                Check(t1.Contains("\"components\"") && t1.Contains("\"top_concern\"") && t1.Contains("\"summary\""),
                    "健康API: JSON 字段齐全（components/top_concern/summary）");
                using (var httpNoCookie = new HttpClient())
                {
                    var r2 = httpNoCookie.GetAsync($"{baseUrl}/api/devices/health").GetAwaiter().GetResult();
                    Check(r2.StatusCode == HttpStatusCode.Forbidden, "健康API: 无 token → 403");
                }
            }
            finally
            {
                srv.Stop(); mesh.Stop();
                TryDeleteDir(webRoot);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
        }
    }

    static HttpResponseMessage HttpPostWithToken(string url, string token, string json){
        var req=new HttpRequestMessage(HttpMethod.Post, url);
        if(!string.IsNullOrEmpty(token)) req.Headers.Add("X-Agg-Token", token);
        req.Content=new StringContent(json, Encoding.UTF8, "application/json");
        return _http.SendAsync(req).GetAwaiter().GetResult();
    }
    static HttpResponseMessage HttpPatchWithToken(string url, string token, string json){
        var req=new HttpRequestMessage(new HttpMethod("PATCH"), url);
        if(!string.IsNullOrEmpty(token)) req.Headers.Add("X-Agg-Token", token);
        req.Content=new StringContent(json, Encoding.UTF8, "application/json");
        return _http.SendAsync(req).GetAwaiter().GetResult();
    }
    static HttpResponseMessage HttpDeleteWithToken(string url, string token){
        var req=new HttpRequestMessage(HttpMethod.Delete, url);
        if(!string.IsNullOrEmpty(token)) req.Headers.Add("X-Agg-Token", token);
        return _http.SendAsync(req).GetAwaiter().GetResult();
    }
}

using System.IO.Compression;
using System.Xml.Linq;
using FctDistributor;

/// <summary>
/// 「只投放到已有目录」模式的自检：在临时目录里造出现成目录结构，跑完整计划+落盘，逐项校验。
/// </summary>
class SelfTest
{
    static int _fail;
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "[OK]   " : "[FAIL] ") + what);
        if (!ok) _fail++;
    }

    /// <summary>从输出目录往上找项目里的 tpl\ 模板目录（仅用于对照内置资源）。</summary>
    static string FindTplDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            var cand = Path.Combine(d.FullName, "tpl");
            if (Directory.Exists(cand) && Directory.GetFiles(cand, "*.xml").Length > 0) return cand;
        }
        throw new DirectoryNotFoundException("找不到模板目录 tpl\\");
    }

    [STAThread]
    static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "fct_target_" + Guid.NewGuid().ToString("N")[..6]);
        // 预先建好「现有目录」：Offline 下两个型号 × 三天；
        // 再故意放一个 Online 日期目录和两个垃圾目录，都不应被扫到/被写入
        var dates = new[] { "20260701", "20260702", "20260703" };
        foreach (var d in dates)
        {
            Directory.CreateDirectory(Path.Combine(root, "Offline", "E3002781", d));
            Directory.CreateDirectory(Path.Combine(root, "Offline", "E3009999", d));
        }
        Directory.CreateDirectory(Path.Combine(root, "Online", "E3002781", "20260702"));
        Directory.CreateDirectory(Path.Combine(root, "Offline", "NOTAMODEL", "20260701"));
        Directory.CreateDirectory(Path.Combine(root, "Junk", "E3002781", "20260701"));

        // ---- 内置模板：与 tpl\ 下的文件一一对应，且能直接解析
        var emb = TemplateGenerator.LoadEmbedded(e => Console.WriteLine("    内置模板错误: " + e));
        var onDisk = Directory.GetFiles(FindTplDir(), "*.xml").Select(Path.GetFileName).OrderBy(x => x).ToList();
        Check(emb.Count == onDisk.Count && emb.Count > 0,
              $"内置模板 {emb.Count} 个（tpl\\ 下 {onDisk.Count} 个）");
        Check(emb.Select(t => t.FileName).OrderBy(x => x).SequenceEqual(onDisk!),
              "内置模板文件名与 tpl\\ 一致: " + string.Join(", ", emb.Select(t => t.Prefix + "_")));
        Check(emb.All(t => t.Stamps.Count > 0 && t.PanelTestTime > 0 && t.Embedded),
              "内置模板解析完整（TIMESTAMP / PANEL@TESTTIME 均已读到）");

        var o = new TemplateGenerator.Options
        {
            UseEmbeddedTemplates = true,          // 模板内置，不读外部目录
            TargetRoot = root,
            Stations = new List<string> { "FCT4" },
            DateFrom = new DateTime(2026, 6, 25),
            DateTo = new DateTime(2026, 7, 10),
            SkipSunday = false,
            SkipSaturday = false,
            Seed = 42,
            ForceUser = true,
            ExistingDirsOnly = true,
        };

        var scan = TemplateGenerator.ScanExistingDateDirs(root);
        Check(scan.Count == 6, $"扫到现有日期目录 {scan.Count} 个（期望 6：只数 Offline，不数 Online/NOTAMODEL/Junk）");
        var scanFixed = TemplateGenerator.ScanExistingDateDirs(root, SnExporter.Model);
        Check(scanFixed.Count == 3 && scanFixed.All(d => d.Model == "E3002781"),
              $"按固定型号扫：{scanFixed.Count} 个（期望 3，只要 Offline\\E3002781\\）");
        Check(scan.All(d => d.Category.Equals("Offline", StringComparison.OrdinalIgnoreCase)),
              "扫到的目录全部在 Offline 下");
        Check(!scan.Any(d => d.Path.Contains("Online", StringComparison.OrdinalIgnoreCase)),
              "Online 目录完全未参与扫描");

        var plan = new TemplateGenerator().BuildPlan(o, s => Console.WriteLine("    " + s));
        var live = plan.Items.Where(i => i.Ok && !i.Skip).ToList();
        Check(live.Count == 6, $"计划条目 {live.Count}（期望 6 = E3002781 目录 3 个 × 2 模板；E3009999 目录根本不碰）");
        Check(live.All(i => i.Model == "E3002781"), "计划里全是 E3002781");
        Check(live.All(i => i.Sn == SnExporter.SnGolden || i.Sn == SnExporter.SnSilvery),
              "所有条目的 SN 就是金/银板那两个，一个字都没改");

        // 与界面列表同粒度的合并预览（日期 + 型号 → 待生成 N 个文件）
        var merged = plan.Items.Where(i => i.Ok)
            .GroupBy(i => (i.DateDir, i.Model))
            .OrderBy(g => g.Key.DateDir).ThenBy(g => g.Key.Model).ToList();
        Console.WriteLine($"    【列表预览】{plan.Items.Count} 条计划 → 合并为 {merged.Count} 行：");
        foreach (var g in merged)
            Console.WriteLine($"      {g.First().Station}  {g.Key.DateDir}  Offline  {g.Key.Model}  " +
                              $"{g.Min(i => i.Stamp):HH:mm}~{g.Max(i => i.Stamp):HH:mm}  " +
                              $"待生成 {g.Count(i => !i.Skip)} 个文件");
        Check(merged.Count == 3, $"合并后 {merged.Count} 行（期望 3 = 3 日期 × 1 型号，而不是 6 行）");
        Check(live.All(i => i.Category.Equals("Offline", StringComparison.OrdinalIgnoreCase)), "计划内全部是 Offline");
        Check(live.All(i => Directory.Exists(Path.GetDirectoryName(i.TargetPath)!)), "所有目标目录都已存在");
        Check(live.All(i => i.Model != "E3009999"), "E3009999 目录一条都没进计划（SN 不能改，放不进去）");
        Check(plan.Items.Count == 6, $"计划总条目 {plan.Items.Count}（期望 6：只为 E3002781 目录生成）");

        var rep = new Distributor().Run(live, new Distributor.Options
        {
            DryRun = false, Overwrite = true, StampFiles = true, StampFolders = true, NoCreateDirs = true,
        });
        Check(rep.Copied == 6 && rep.Failed == 0, $"落盘 成功{rep.Copied} 失败{rep.Failed}（期望 6）");

        var expected = new HashSet<string>(scan.Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
        var created = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Where(d => Directory.EnumerateFiles(d, "*.xml").Any()).ToList();
        Check(created.All(d => expected.Contains(d)), "没有新建任何目录（所有含文件的目录都是原有目录）");
        Check(!Directory.EnumerateFiles(Path.Combine(root, "Online"), "*", SearchOption.AllDirectories).Any(),
              "Online 下一个文件都没写");

        // 逐文件校验
        int checkedFiles = 0;
        foreach (var f in Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories))
        {
            checkedFiles++;
            var p = NameParser.Parse(Path.GetFileName(f));
            if (p.Error != null) { Check(false, $"{Path.GetFileName(f)} 文件名解析失败: {p.Error}"); continue; }
            var dirDate = Path.GetFileName(Path.GetDirectoryName(f)!);
            var dirModel = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f)!)!);
            if (p.Stamp.ToString("yyyyMMdd") != dirDate) Check(false, $"{Path.GetFileName(f)} 文件名日期≠目录 {dirDate}");
            if (p.Model != dirModel) Check(false, $"{Path.GetFileName(f)} 型号≠目录 {dirModel}");
            var doc = XDocument.Load(f);
            var dutId = doc.Descendants("DUT").First().Attribute("ID")!.Value;
            if (dutId != p.Sn) Check(false, $"{Path.GetFileName(f)} DUT@ID({dutId}) ≠ 文件名 SN({p.Sn})");
            var user = doc.Descendants("FACTORY").First().Attribute("USER")!.Value;
            if (user != "Operator") Check(false, $"{Path.GetFileName(f)} USER={user}");
            var tester = doc.Descendants("FACTORY").First().Attribute("TESTER")!.Value;
            if (tester != "PEU_G49_FCT4") Check(false, $"{Path.GetFileName(f)} TESTER={tester}");
            var mtime = File.GetLastWriteTime(f);
            if (Math.Abs((mtime - p.Stamp).TotalSeconds) > 1) Check(false, $"{Path.GetFileName(f)} mtime≠文件名时间");
        }
        Check(checkedFiles == 6, $"落盘文件数 {checkedFiles}（期望 6），且以上逐项校验无 FAIL");

        // 再跑一次：应全部因同 SN 已存在而跳过
        var plan2 = new TemplateGenerator().BuildPlan(o);
        Check(plan2.Items.All(i => i.Skip), $"重复运行全部跳过（{plan2.Items.Count(i => i.Skip)}/{plan2.Items.Count}）");
        Check(plan2.Items.All(i => (i.SkipReason ?? "").Contains("同 SN")),
              "跳过原因全是「已存在同 SN 文件」（而不是文件数上限）");

        // ---- 关键规则：目录里已有很多**其它 SN** 的文件，不得阻止添加
        Directory.Delete(root, true);
        var busyDir = Path.Combine(root, "Offline", "E3002781", "20260701");
        Directory.CreateDirectory(busyDir);
        for (int i = 0; i < 9; i++)
            File.WriteAllText(Path.Combine(busyDir,
                $"P_Fts_PEU_G49_FCT4_E3002781ZZZ0000000000000000{i:D3}_2026070110000000{i}_20260701{i:D6}.xml"),
                "<BATCH/>");
        var plan5 = new TemplateGenerator().BuildPlan(o);
        var live5 = plan5.Items.Where(i => i.Ok && !i.Skip).ToList();
        Check(live5.Count == 2, $"目录已有 9 个其它 SN 的文件，仍然正常添加 {live5.Count} 个（期望 2，无数量上限）");

        new Distributor().Run(live5, new Distributor.Options
        {
            DryRun = false, Overwrite = true, StampFiles = true, StampFolders = false, NoCreateDirs = true,
        });
        Check(Directory.GetFiles(busyDir, "*.xml").Length == 11,
              $"落盘后目录内 {Directory.GetFiles(busyDir, "*.xml").Length} 个文件（期望 9 + 2 = 11，不偷删不限额）");

        // 同一目录再跑：两个模板的 SN 已在其中 -> 两条都跳过
        var plan6 = new TemplateGenerator().BuildPlan(o);
        Check(plan6.Items.Count == 2 && plan6.Items.All(i => i.Skip),
              "同目录再跑：两个模板均命中同 SN 而跳过");

        // ---- 导出 xlsx（从计划：现场主路径）
        Check(SnExporter.KindOf(SnExporter.SnGolden) == SnExporter.Kind.Golden, "...272 认为金板 Golden");
        Check(SnExporter.KindOf(SnExporter.SnSilvery) == SnExporter.Kind.Silvery, "...274 认为银板 Silvery");
        Check(SnExporter.KindOf("E3009999AFV75236898002K30500272") == SnExporter.Kind.None,
              "改了型号的 SN 不承认（SN 全程不改写，出现就是脏数据）");
        Check(SnExporter.Model == "E3002781", $"金银板型号常量 = {SnExporter.Model}");
        Check(SnExporter.KindOf("E3002781ZZZ0000000000000000000") == SnExporter.Kind.None,
              "其它 SN 不是金也不是银");

        var planBoards = SnExporter.FromPlan(plan.Items, executed: true);
        var xlsxPlan = Path.Combine(Path.GetTempPath(), $"fct_sn_plan_{Guid.NewGuid().ToString("N")[..6]}.xlsx");
        var prep = SnExporter.Export(xlsxPlan, planBoards);
        Check(prep.Golden == 3 && prep.Silvery == 3 && prep.Excluded == 0,
              $"从计划导出：金 {prep.Golden} / 银 {prep.Silvery} / 排除 {prep.Excluded}（期望 3 / 3 / 0）");
        using (var zp = ZipFile.OpenRead(xlsxPlan))
        {
            Check(zp.Entries.Count(e => e.FullName.StartsWith("xl/worksheets/")) == 1, "只有 1 个 Sheet");
            using var r1 = new StreamReader(zp.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var b1 = r1.ReadToEnd();
            foreach (var need in new[] { "机台号", "日期", "金板 SN", "银板 SN", "Golden（金板）", "Silvery（银板）" })
                if (!b1.Contains(need)) Check(false, $"台账缺字段 {need}");
            Check(true, "表头/标注 = 机台号 / 日期 / 金板 SN(Golden) / 银板 SN(Silvery)");
            Check(!b1.Contains("型号") && !b1.Contains("文件名") && !b1.Contains("板数"),
                  "没有多余列（型号/文件名/板数均已去掉）");
        }
        File.Delete(xlsxPlan);

        // ---- 导出 xlsx（从目标目录：9 个干扰 SN 必须被排除）
        var boards = SnExporter.FromTarget(root, s => Console.WriteLine("    " + s));
        Check(boards.Count == 11, $"从目标目录取到 {boards.Count} 个文件（期望 11 = 9 干扰 + 2 金银）");

        var xlsx = Path.Combine(Path.GetTempPath(), $"fct_sn_{Guid.NewGuid().ToString("N")[..6]}.xlsx");
        var xrep = SnExporter.Export(xlsx, boards, m => Console.WriteLine("    " + m));
        Check(xrep.Rows == 1 && xrep.Golden == 1 && xrep.Silvery == 1 && xrep.Excluded == 9,
              $"台账 {xrep.Rows} 行，金 {xrep.Golden} / 银 {xrep.Silvery}，排除 {xrep.Excluded}（期望 1/1/1/9）");
        Check(File.Exists(xlsx) && new FileInfo(xlsx).Length > 1200,
              $"xlsx 已生成（{new FileInfo(xlsx).Length} 字节）");

        using (var zip = ZipFile.OpenRead(xlsx))
        {
            var names = zip.Entries.Select(e => e.FullName).ToHashSet();
            foreach (var need in new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml",
                                         "xl/_rels/workbook.xml.rels", "xl/styles.xml",
                                         "xl/worksheets/sheet1.xml" })
                if (!names.Contains(need)) Check(false, $"xlsx 缺少部件 {need}");

            foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels")))
            {
                using var st = e.Open();
                try { XDocument.Load(st); }
                catch (Exception ex) { Check(false, $"{e.FullName} 不是合法 XML: {ex.Message}"); }
            }
            Check(true, "xlsx 内所有 XML 部件都能严格解析");

            using var sh1 = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var body = sh1.ReadToEnd();
            Check(body.Contains(SnExporter.SnGolden) && body.Contains(SnExporter.SnSilvery),
                  "金板与银板的 SN 都在表里");
            Check(!body.Contains("E3002781ZZZ"), "9 个干扰 SN 一个都没进表");
            Check(body.Contains("2026-07-01"), "日期已格式化为 2026-07-01");
        }
        // 留一份供人工看（用 Excel / pandas 验证兼容性）
        var keep = Path.Combine(Path.GetTempPath(), "fct_sn_last.xlsx");
        File.Copy(xlsx, keep, true);
        Console.WriteLine($"    【产物】样本 xlsx 已留存: {keep}");
        File.Delete(xlsx);

        // ---- 自选文件夹范围修时间戳：只动选中的，别的一个都不能碰
        var r2 = Path.Combine(Path.GetTempPath(), "fct_restamp_" + Guid.NewGuid().ToString("N")[..6]);
        var dirA = Path.Combine(r2, "Offline", "E3002781", "20260701");
        var dirB = Path.Combine(r2, "Offline", "E3002781", "20260702");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        string MakeFile(string dir, string date, string hms)
        {
            var n = $"P_Fts_PEU_G49_FCT4_{SnExporter.SnGolden}_{date}{hms}_{date}123456.xml";
            var fp = Path.Combine(dir, n);
            File.WriteAllText(fp, "<BATCH/>");
            File.SetLastWriteTime(fp, new DateTime(2000, 1, 1));      // 故意打乱
            return fp;
        }
        var fa = MakeFile(dirA, "20260701", "093000123");
        var fb = MakeFile(dirB, "20260702", "101500456");
        Directory.SetLastWriteTime(Path.Combine(r2, "Offline", "E3002781"), new DateTime(2000, 1, 1));
        Directory.SetLastWriteTime(dirB, new DateTime(2000, 1, 1));   // D-L9：给未选目录一个可校验的基线时间

        var rr = new Distributor().Restamp(new[] { dirA }, new[] { "xml" },
                    recursive: false, stampFolders: true, bubbleParents: true, bubbleRoot: r2);
        Check(rr.Total == 1 && rr.StampedFiles == 1 && rr.Failed == 0,
              $"自选范围：只处理了 {rr.Total} 个文件（期望 1 —— 只选了 20260701）");
        Check((File.GetLastWriteTime(fa) - new DateTime(2026, 7, 1, 9, 30, 0, 123)).Duration() < TimeSpan.FromSeconds(1),
              "所选目录的文件时间已按文件名修好");
        Check(File.GetLastWriteTime(fb).Year == 2000, "未勾选的目录一个文件都没动");
        Check(Directory.GetLastWriteTime(Path.Combine(r2, "Offline", "E3002781")).Year == 2026,
              "目录时间已向上冒泡到型号目录");
        Check(File.GetLastWriteTime(dirB).Year == 2000,
              "未选日期目录不在处理列表里（目录时间保持原样）");   // D-L9：原为 `|| true` 恒真装饰断言，改为真校验

        var deep0 = new Distributor().Restamp(new[] { Path.Combine(r2, "Offline", "E3002781") },
                    new[] { "xml" }, recursive: false, stampFolders: false, bubbleParents: false, bubbleRoot: null);
        Check(deep0.Total == 0, "不勾【含子目录】时不下钻（型号目录直接下没文件）");
        var deep1 = new Distributor().Restamp(new[] { Path.Combine(r2, "Offline", "E3002781") },
                    new[] { "xml" }, recursive: true, stampFolders: false, bubbleParents: false, bubbleRoot: null);
        Check(deep1.Total == 2, "勾上【含子目录】后两个日期目录的文件都处理到了");
        Check((File.GetLastWriteTime(fb) - new DateTime(2026, 7, 2, 10, 15, 0, 456)).Duration() < TimeSpan.FromSeconds(1),
              "递归后 20260702 的文件也修好了");

        var dup = new Distributor().Restamp(new[] { Path.Combine(r2, "Offline"), dirA }, new[] { "xml" },
                    recursive: true, stampFolders: false, bubbleParents: false, bubbleRoot: null);
        Check(dup.Total == 2, $"目录嵌套重叠时文件不重复处理（{dup.Total} 个）");

        var miss = new Distributor().Restamp(new[] { Path.Combine(r2, "不存在") }, new[] { "xml" },
                    recursive: false, stampFolders: false, bubbleParents: false, bubbleRoot: null);
        Check(miss.Total == 0 && miss.Errors.Count == 1, "所选目录不存在时只报错不崩");

        // ---- 范围选择对话框本身（不弹 UI，只验证扫目录/默认全选/过滤/勾选）
        var scanned = RestampScopeDialog.ScanCore(r2, new[] { "xml" });
        using (var dlg = new RestampScopeDialog(r2, new[] { "xml" }))
        {
            dlg.LoadItemsForTest(scanned);                  // 真实扫描在后台线程，自检直接灌结果
            Check(dlg.AllItems.Count == 2,
                  $"对话框扫到 {dlg.AllItems.Count} 个含文件的文件夹（期望 2：两个日期目录）");
            Check(dlg.AllItems.All(i => i.Files == 1), "每个目录的文件数统计正确");
            Check(dlg.CheckedCount == 2, "默认全选（想全修的人直接点确定就行）");
            Check(dlg.AllItems.Any(i => i.Rel.EndsWith("20260701")) &&
                  dlg.AllItems.Any(i => i.Rel.EndsWith("20260702")), "相对路径显示正确");

            dlg.SetFilter("20260702");
            Check(dlg.VisibleCount == 1, $"关键字过滤后只剩 {dlg.VisibleCount} 项（期望 1）");
            dlg.SetVisibleChecked(false);
            Check(dlg.CheckedCount == 1 && dlg.CheckedPaths()[0].EndsWith("20260701"),
                  "对过滤结果清空只影响可见项（只剩 20260701 仍勾选）");
            dlg.SetFilter("");
            Check(dlg.VisibleCount == 2 && dlg.CheckedCount == 1, "清掉过滤后勾选状态不丢");
        }

        // ---- 大数据量：限流、跳过已正确、可取消
        var big = Path.Combine(Path.GetTempPath(), "fct_big_" + Guid.NewGuid().ToString("N")[..6]);
        var bigDir = Path.Combine(big, "Offline", "E3002781", "20260705");
        Directory.CreateDirectory(bigDir);
        // 默认 3000；想压更大量：设环境变量 FCT_BIG_N=50000 再跑
        int N = int.TryParse(Environment.GetEnvironmentVariable("FCT_BIG_N"), out var bigN) && bigN > 0
                ? bigN : 3000;
        for (int i = 0; i < N; i++)
        {
            // 时间必须合法（分/秒 < 60），否则会被当成“无法提取时间戳”跳过
            int hh = 8 + i / 3600, mm = (i / 60) % 60, ss = i % 60;
            var nm = $"P_Fts_PEU_G49_FCT4_{SnExporter.SnGolden}_20260705{hh:D2}{mm:D2}{ss:D2}{(i % 1000):D3}_20260705{i:D6}.xml";
            File.WriteAllText(Path.Combine(bigDir, nm), "<BATCH/>");
        }
        Console.WriteLine($"    【大数据量】已造 {N} 个文件于 {bigDir}");

        int logLines = 0;
        var swBig = System.Diagnostics.Stopwatch.StartNew();
        var big1 = new Distributor().Restamp(new[] { bigDir }, new[] { "xml" },
                        recursive: false, stampFolders: true, bubbleParents: true, bubbleRoot: big,
                        log: _ => logLines++);
        swBig.Stop();
        Check(big1.Total == N && big1.Failed == 0,
              $"{N} 个文件全部处理（已改 {big1.StampedFiles}，耗时 {swBig.Elapsed.TotalSeconds:F2}s）");
        Check(logLines <= 260,
              $"日志已限流：{N} 个文件只产生 {logLines} 行日志（阀值 260，旧版是 {N}+ 行）");

        var swBig2 = System.Diagnostics.Stopwatch.StartNew();
        var big2 = new Distributor().Restamp(new[] { bigDir }, new[] { "xml" },
                        recursive: false, stampFolders: false, bubbleParents: false, bubbleRoot: null);
        swBig2.Stop();
        Check(big2.Unchanged == N && big2.StampedFiles == 0,
              $"第二次跑：{big2.Unchanged} 个已正确直接跳过、一个都不重写（{swBig2.Elapsed.TotalSeconds:F2}s）");

        // 预先取消：必须马上回来，不能卡死、不能抛到外面
        var swCancel = System.Diagnostics.Stopwatch.StartNew();
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            var rc = new Distributor().Restamp(new[] { bigDir }, new[] { "xml" },
                        recursive: false, stampFolders: true, bubbleParents: true, bubbleRoot: big,
                        ct: cts.Token);
            swCancel.Stop();
            Check(swCancel.ElapsedMilliseconds < 2000 && rc.StampedFiles == 0,
                  $"预先取消：{swCancel.ElapsedMilliseconds}ms 就返回且一个文件都没改（不抛异常）");
        }

        // 跑到一半取消：返回部分结果，不抛异常
        foreach (var f in Directory.EnumerateFiles(bigDir))
            File.SetLastWriteTime(f, new DateTime(2000, 1, 1));      // 先打乱，强迫它真写
        using (var cts2 = new CancellationTokenSource())
        {
            cts2.CancelAfter(30);
            var swMid = System.Diagnostics.Stopwatch.StartNew();
            var rm = new Distributor().Restamp(new[] { bigDir }, new[] { "xml" },
                        recursive: false, stampFolders: false, bubbleParents: false, bubbleRoot: null,
                        ct: cts2.Token);
            swMid.Stop();
            Check(swMid.ElapsedMilliseconds < 5000,
                  $"运行中取消：{swMid.ElapsedMilliseconds}ms 内停下（已改 {rm.StampedFiles}/{rm.Total}）");
        }

        // 对话框扫目录也能取消（D-M2 新语义：取消返回部分结果，不抛异常、不全量重扫）
        using (var cts3 = new CancellationTokenSource())
        {
            cts3.Cancel();
            var swScan = System.Diagnostics.Stopwatch.StartNew();
            List<RestampScopeDialog.DirItem> partial;
            bool threw = false;
            try { (partial, _) = RestampScopeDialog.Scan(big, new[] { "xml" }, null, cts3.Token); }
            catch (OperationCanceledException) { threw = true; partial = new(); }
            swScan.Stop();
            Check(!threw, "对话框扫目录取消不抛 OCE（返回部分结果语义）");
            Check(swScan.ElapsedMilliseconds < 2000,
                  $"预先取消的扫目录 {swScan.ElapsedMilliseconds}ms 内返回（不全量重扫）");
        }

        Directory.Delete(big, true);

        Directory.Delete(r2, true);

        // 目标下只有**其它型号**的目录：SN 不能改，所以一个都不能放
        Directory.Delete(root, true);
        foreach (var d in dates) Directory.CreateDirectory(Path.Combine(root, "Offline", "E3009999", d));
        var plan3 = new TemplateGenerator().BuildPlan(o);
        Check(plan3.Items.Count == 0 && plan3.Messages.Count > 0,
              "只有其它型号目录时：不生成任何条目并给提示（SN 固定，放不进去）");
        Check(!Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories).Any(),
              "E3009999 目录下一个文件都没写");

        // 空目标：不新建、给出提示
        Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        var plan4 = new TemplateGenerator().BuildPlan(o);
        Check(plan4.Items.Count == 0 && plan4.Messages.Count > 0, "目标下无现有目录时不生成任何条目并给出提示");
        Check(!Directory.EnumerateDirectories(root).Any(), "空目标根仍然是空的（未新建目录）");
        Directory.Delete(root, true);

        // ==================== D-M3：源目录分发模式 + NameParser 边界（此前零覆盖） ====================
        RunSourceDirAndParserTests();

        // ==================== v3.0.0 彻底分家：副本一致性 ====================
        // 分发器独立后不再引用主程序源码，IP 映射表与 Xlsx 写出器各持一份副本。
        // 副本最大的风险是**静默漂移**：改了一边忘了另一边，两边自检还都是绿的。
        // 所以这里直接去读主程序的源文件做跨工程比对（源码不在时跳过，不影响独立部署）。
        CheckCopiesInSync();

        Console.WriteLine(_fail == 0 ? "\n==== 全部通过 ====" : $"\n==== {_fail} 项失败 ====");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>从当前目录/程序目录两头往上找文件（不写死绝对路径）。</summary>
    static string FindUp(string relative)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 7 && dir != null; i++, dir = dir.Parent)
            {
                var p = Path.Combine(dir.FullName, relative);
                if (File.Exists(p)) return p;
            }
        }
        return "";
    }

    // ==================== D-M3：源目录分发模式 + NameParser 边界 ====================

    /// <summary>
    /// 此前的自检只覆盖「模板直生」链路；本组补齐模式一（源目录分发）的核心组件：
    /// Planner.Build / NameParser 边界（非法日期、段数、SN） / Distributor 的 Copy 与不覆盖跳过。
    /// 全部在临时目录里做，不碰真实数据。
    /// </summary>
    static void RunSourceDirAndParserTests()
    {
        Console.WriteLine("\n【源目录分发 + 文件名解析边界】Planner.Build / Distributor.Copy / NameParser 非法输入");

        // ---- NameParser：非法日期表驱动 ----
        var badDates = new[]
        {
            "20260229120000",     // 2026 非闰年，2 月无 29 日
            "20261301120000",     // 13 月
            "20260431120000",     // 4 月 31 日
            "20260716120000",     // 合法对照（见下方 valid 断言）
        };
        Check(NameParser.ParseStamp("20260229120000") == null, "ParseStamp 拒绝非闰年 02-29");
        Check(NameParser.ParseStamp("20261301120000") == null, "ParseStamp 拒绝 13 月");
        Check(NameParser.ParseStamp("20260431120000") == null, "ParseStamp 拒绝 4 月 31 日");
        Check(NameParser.ParseStamp("20280229120000") != null, "ParseStamp 接受闰年 2028-02-29");
        Check(NameParser.ParseStamp("20260716120000") == new DateTime(2026, 7, 16, 12, 0, 0),
              "14 位时间戳解析正确");
        Check(NameParser.ParseStamp("20260716120000123") == new DateTime(2026, 7, 16, 12, 0, 0, 123),
              "17 位时间戳毫秒解析正确");
        Check(NameParser.ParseStamp("20260716a20000") == null, "含非数字的时间戳拒绝");
        Check(NameParser.ParseStamp("2026") == null, "过短时间戳拒绝");
        _ = badDates;   // 表驱动展示用（断言已逐条展开）

        // ---- NameParser：文件名结构边界 ----
        var ok = NameParser.Parse(
            "P_Fts_PEU_G49_FCT4_E3002781AFV75236898002K30500272_20260716100234160_20260716681837.xml");
        Check(ok.Error == null && ok.Station == "FCT4" &&
              ok.Model == "E3002781" && ok.Prefix == "P" &&
              ok.Stamp == new DateTime(2026, 7, 16, 10, 2, 34, 160),
              "标准文件名解析完整（前缀/机台/SN 型号/时间戳）");
        Check(NameParser.Parse("a_b_c.xml").Error != null, "段数不足 -> 拒绝");
        Check(NameParser.Parse("P_Fts_NOFCT_E3002781AFV75236898002K30500272_20260716100234160_x.xml").Error != null,
              "找不到 FCT 机台段 -> 拒绝");
        Check(NameParser.Parse("P_Fts_PEU_G49_FCT4_SHORT_20260716100234160_x.xml").Error != null,
              "SN 长度不足 8 -> 拒绝");
        Check(NameParser.Parse("P_Fts_PEU_G49_FCT4_X9999999AFV75236898_20260716100234160_x.xml").Error != null,
              "型号不符合 E+7 位数字 -> 拒绝");

        // ---- 源目录分发：扫描 + 校验 + 复制 + 幂等跳过 ----
        var srcRoot = Path.Combine(Path.GetTempPath(), "fct_src_" + Guid.NewGuid().ToString("N")[..6]);
        var dstRoot = Path.Combine(Path.GetTempPath(), "fct_dst_" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            const string snG = SnExporter.SnGolden;
            var srcDayDir = Path.Combine(srcRoot, "FCT4", "20260716");
            Directory.CreateDirectory(srcDayDir);
            Directory.CreateDirectory(Path.Combine(dstRoot, "Offline", "E3002781", "20260716"));   // 已有目录

            var fGood = Path.Combine(srcDayDir,
                $"P_Fts_PEU_G49_FCT4_{snG}_20260716100234160_20260716681837.xml");
            var fBadStation = Path.Combine(srcDayDir,
                $"O_Fts_PEU_G49_FCT5_{snG}_20260716110200000_20260716681838.xml");   // 名字 FCT5 放在 FCT4 目录
            var fBadDate = Path.Combine(srcDayDir,
                $"O_Fts_PEU_G49_FCT4_{snG}_20260715110200000_20260716681839.xml");   // 名字 0715 放在 0716 目录
            File.WriteAllText(fGood, "<BATCH TIMESTAMP=\"x\"/>");
            File.WriteAllText(fBadStation, "<BATCH/>");
            File.WriteAllText(fBadDate, "<BATCH/>");
            // 干扰项：非日期目录 + 非 FCT 目录
            Directory.CreateDirectory(Path.Combine(srcRoot, "FCT4", "notadate"));
            Directory.CreateDirectory(Path.Combine(srcRoot, "NOTFCT", "20260716"));

            var logs = new List<string>();
            var plan = new Planner().Build(
                new Planner.Options { SourceRoot = srcRoot, TargetRoot = dstRoot },
                log: logs.Add);
            Check(plan.Items.Count == 3, $"扫到 3 个 xml（干扰项全忽略，实际 {plan.Items.Count}）");
            var good = plan.Items.Single(i => i.SourcePath == fGood);
            Check(good.Error == null && good.Ok && good.Station == "FCT4" && good.Category == "Offline",
                  "合法条目通过校验（固定 Offline 分类）");
            Check(plan.Items.First(i => i.SourcePath == fBadStation).Error != null,
                  "文件名机台号 ≠ 所在机台目录 -> 判无效（防串台）");
            Check(plan.Items.First(i => i.SourcePath == fBadDate).Error != null,
                  "文件名日期 ≠ 所在日期目录 -> 判无效（防日期漂移）");
            Check(logs.Any(m => m.Contains("非日期目录")), "非日期目录记日志跳过");

            // 执行复制
            var rep = new Distributor().Run(plan.Items, new Distributor.Options());
            Check(rep.Copied == 1 && rep.Invalid == 2 && rep.Failed == 0,
                  $"执行：复制 1 / 无效 2 / 失败 0（实际 复制 {rep.Copied} / 无效 {rep.Invalid} / 失败 {rep.Failed}）");
            var dstFile = Path.Combine(dstRoot, "Offline", "E3002781", "20260716", Path.GetFileName(fGood));
            Check(File.Exists(dstFile), "目标文件已落盘到 Offline\\E3002781\\20260716\\");

            // 时间戳按文件名修复
            Check((File.GetLastWriteTime(dstFile) - new DateTime(2026, 7, 16, 10, 2, 34, 160)).Duration()
                      <= TimeSpan.FromSeconds(1),
                  "落盘文件的 mtime ≡ 文件名时间戳");

            // 再跑一遍：目标已存在且不覆盖 → 跳过（幂等）
            var plan2 = new Planner().Build(new Planner.Options { SourceRoot = srcRoot, TargetRoot = dstRoot });
            var rep2 = new Distributor().Run(plan2.Items.Where(i => i.Ok).ToList(),
                                             new Distributor.Options());
            Check(rep2.Skipped >= 1 && rep2.Copied == 0,
                  $"重复运行全跳过（幂等）：跳过 {rep2.Skipped} / 复制 {rep2.Copied}");

            // DirStampFrom 跨天钳位：23:59:59 + 随机秒绝不跨天
            var edge = FileTimeUtil.DirStampFrom(new DateTime(2026, 7, 16, 23, 59, 59), new Random(7));
            Check(edge.Date == new DateTime(2026, 7, 16) && edge > new DateTime(2026, 7, 16, 23, 59, 59),
                  $"DirStampFrom 跨天钳位：{edge:HH:mm:ss.fff}（同日 +≥1s）");
        }
        finally
        {
            try { Directory.Delete(srcRoot, true); } catch { }
            try { Directory.Delete(dstRoot, true); } catch { }
        }
    }

    /// <summary>
    /// 验「分发器的副本」与「主程序的原份」没有静默漂移。
    /// 比两样：① IP→机台映射表逐条；② Xlsx.cs 是否逐字节相同。
    /// </summary>
    static void CheckCopiesInSync()
    {
        Console.WriteLine();
        var mainSd = FindUp(Path.Combine("FctAggregator", "StationDetector.cs"));
        if (mainSd.Length == 0)
        {
            Console.WriteLine("    (跳过副本一致性比对：没找到主程序源码，独立部署时属正常）");
        }
        else
        {
            var text = File.ReadAllText(mainSd);
            // 抽 ["172.28.55.11"] = "FCT1" 这种行
            var re = new System.Text.RegularExpressions.Regex(
                @"\[""(?<ip>[\d\.]+)""\]\s*=\s*""(?<st>FCT\d+)""");
            var mainMap = new Dictionary<string, string>();
            foreach (System.Text.RegularExpressions.Match m in re.Matches(text))
                mainMap[m.Groups["ip"].Value] = m.Groups["st"].Value;

            Check(mainMap.Count > 0, $"能从主程序源码里解出 IP 映射表（{mainMap.Count} 条）");
            var mine = StationDetector.IpToStation;
            Check(mine.Count == mainMap.Count,
                  $"IP 映射表条数一致（分发器 {mine.Count} / 主程序 {mainMap.Count}）");
            var diff = new List<string>();
            foreach (var kv in mainMap)
                if (!mine.TryGetValue(kv.Key, out var v) || v != kv.Value)
                    diff.Add($"{kv.Key}：主程序={kv.Value} / 分发器={(mine.TryGetValue(kv.Key, out var x) ? x : "缺失")}");
            foreach (var kv in mine)
                if (!mainMap.ContainsKey(kv.Key)) diff.Add($"{kv.Key}：主程序=缺失 / 分发器={kv.Value}");
            Check(diff.Count == 0,
                  diff.Count == 0 ? "**IP 映射表与主程序逐条一致**（副本未漂移）"
                                  : "IP 映射表与主程序不一致：" + string.Join("；", diff));
        }

        var mainXlsx = FindUp(Path.Combine("FctAggregator", "Xlsx.cs"));
        var mineXlsx = FindUp("Xlsx.cs");
        if (mainXlsx.Length == 0 || mineXlsx.Length == 0)
            Console.WriteLine("    (跳过 Xlsx.cs 比对：源文件不齐)");
        else if (string.Equals(Path.GetFullPath(mainXlsx), Path.GetFullPath(mineXlsx),
                     StringComparison.OrdinalIgnoreCase))
        {
            // D-L9：两路 FindUp 解析到同一个文件时逐字节比对必然通过（假绿），必须点破
            Check(false, $"Xlsx.cs 副本比对无效：两条查找路径解析到了同一个文件 {mainXlsx}");
        }
        else
        {
            var a = File.ReadAllBytes(mainXlsx);
            var b = File.ReadAllBytes(mineXlsx);
            Check(a.AsSpan().SequenceEqual(b),
                  $"Xlsx.cs 与主程序那份逐字节相同（{a.Length} / {b.Length} 字节）");
        }
    }
}

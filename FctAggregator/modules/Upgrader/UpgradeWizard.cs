using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace FctAggregator.modules.Upgrader;

public partial class UpgradeWizard : Form
{
    private int _phase;
    private bool _launchAfterClose;
    private string _stage = "";
    private string _deployScript = "";
    private string _backupDir = "";
    private string _lastOutput = "";

    private Label lblTitle = null!, lblStep = null!;
    private Button btnBrowse = null!, btnNext = null!, btnCancel = null!;
    private ListBox lstLog = null!;
    private TextBox txtPackagePath = null!;

    public UpgradeWizard()
    {
        InitializeComponent();
        this.Size = new Size(760, 560);
        this.Text = $"Argus 升级向导 v{UpgradeEntry.WizardVersion}";
        this.StartPosition = FormStartPosition.CenterScreen;
        AutoSuggestPackage();
    }

    private void InitializeComponent()
    {
        lblTitle = new Label
        {
            Text = "Argus 升级向导",
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            ForeColor = Color.FromArgb(0, 76, 158),
            Location = new Point(20, 15),
            Size = new Size(700, 40),
        };
        lblStep = new Label
        {
            Text = "步骤 1：选择升级包（Argus-v*-update.zip）",
            Font = new Font("Segoe UI", 10F),
            Location = new Point(20, 60),
            Size = new Size(700, 25),
        };
        txtPackagePath = new TextBox { Location = new Point(20, 90), Size = new Size(580, 25), ReadOnly = true };
        btnBrowse = new Button { Text = "浏览...", Location = new Point(610, 89), Size = new Size(90, 27) };
        btnBrowse.Click += (s, e) => OnBrowseClick();
        lstLog = new ListBox
        {
            Location = new Point(20, 130),
            Size = new Size(700, 320),
            BackColor = Color.Black,
            ForeColor = Color.Lime,
            Font = new Font("Consolas", 9F),
        };
        btnNext = new Button
        {
            Text = "下一步 >",
            Location = new Point(0, 2),
            Size = new Size(170, 27),
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
        };
        btnNext.Click += (s, e) => _ = OnNextClick();
        btnCancel = new Button
        {
            Text = "关闭",
            Location = new Point(650, 2),
            Size = new Size(70, 27),
            BackColor = Color.DarkGray,
            ForeColor = Color.White,
        };
        btnCancel.Click += (s, e) => Close();
        var pnl = new Panel { Location = new Point(20, 460), Size = new Size(720, 32) };
        pnl.Controls.AddRange(new Control[] { btnNext, btnCancel });
        Controls.AddRange(new Control[] { lblTitle, lblStep, txtPackagePath, btnBrowse, lstLog, pnl });
    }

    private void Log(string message)
    {
        if (lstLog.InvokeRequired) { lstLog.BeginInvoke(new Action(() => Log(message))); return; }
        lstLog.Items.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        lstLog.TopIndex = lstLog.Items.Count - 1;
    }

    private void AutoSuggestPackage()
    {
        try
        {
            var dirs = new[] { AppConfig.BaseDir, Path.GetFullPath(Path.Combine(AppConfig.BaseDir, "..")) }
                .Where(d => Directory.Exists(d)).Distinct().ToList();
            var best = dirs.SelectMany(d => Directory.GetFiles(d, "Argus-v*-update.zip"))
                .Select(f => (Path: f, Ver: ParseZipVersion(f)))
                .Where(x => x.Ver > new Version(0, 0, 0))
                .OrderBy(x => x.Ver).LastOrDefault();
            if (best.Path != null)
            {
                txtPackagePath.Text = best.Path;
                Log($"自动发现升级包：{Path.GetFileName(best.Path)}（也可点「浏览」选择其他包）");
            }
        }
        catch { }
    }

    private static Version ParseZipVersion(string path)
    {
        var m = Regex.Match(Path.GetFileName(path), @"v(\d+)\.(\d+)\.(\d+)");
        return m.Success
            ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))
            : new Version(0, 0, 0);
    }

    private void OnBrowseClick()
    {
        using var dialog = new OpenFileDialog { Filter = "Argus 更新包 (Argus-v*-update.zip)|Argus-v*-update.zip|所有 zip|*.zip" };
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            txtPackagePath.Text = dialog.FileName;
            Log($"已选择：{Path.GetFileName(dialog.FileName)}");
        }
    }

    private async Task OnNextClick()
    {
        if (_phase == 2) { _launchAfterClose = true; Close(); return; }
        if (string.IsNullOrWhiteSpace(txtPackagePath.Text) || !File.Exists(txtPackagePath.Text))
        {
            MessageBox.Show("请先选择升级包（Argus-v*-update.zip）！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        btnNext.Enabled = false;
        try
        {
            if (_phase == 0) await PrepareAndDryRun();
            else if (_phase == 1) await ExecuteUpgrade();
        }
        catch (Exception ex)
        {
            Log($"✗ 错误：{ex.Message}");
            MessageBox.Show($"操作失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            btnNext.Enabled = true;
        }
    }

    private async Task PrepareAndDryRun()
    {
        Log("⏳ 解压升级包...");
        CleanupStage();
        _stage = Path.Combine(Path.GetTempPath(), $"argus_upg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_stage);
        ZipFile.ExtractToDirectory(txtPackagePath.Text, _stage);

        var (ok, version, error) = ValidateStage(_stage);
        if (!ok)
        {
            CleanupStage();
            throw new Exception(error);
        }
        Log($"✓ 包校验通过：包内版本 v{version}（结构完整、无 data/logs/*.db）");

        _deployScript = FindDeployScript(AppConfig.BaseDir, _stage)
            ?? throw new Exception("找不到 deploy_update.ps1（部署引擎）。请把 tools/deploy_update.ps1 放到程序目录或 tools\\ 下——官方 v3.22.1 及以后的更新包内自带。");
        Log($"✓ 部署引擎：{_deployScript}");

        lblStep.Text = "步骤 2：部署计划（演练，未改动任何文件）";
        Log("⏳ 演练：生成部署计划（自动发现安装位置/备份内容/config 合并预览）...");
        var (code, output) = await RunDeployScript(execute: false);
        _lastOutput = output;
        if (code != 0)
        {
            throw new Exception($"演练未通过（退出码 {code}），请看日志上方 [FAIL] 行。常见原因：包不是 Argus 更新包、目标目录不像 Argus 安装目录。");
        }
        var m = Regex.Match(output, @"备份目录:\s*(.+)\r?\n");
        if (m.Success) _backupDir = m.Groups[1].Value.Trim();

        _phase = 1;
        Log("");
        Log("演练完成：以上为部署计划，尚未改动任何文件。");
        Log("确认无误后点「确认无误，执行升级」开始真正部署（数据与配置自动保护）。");
        btnNext.Text = "确认无误，执行升级 >";
        btnNext.BackColor = Color.FromArgb(200, 60, 60);
        lblStep.Text = "步骤 3：确认执行（失败可按日志中的回滚命令恢复）";
    }

    private async Task ExecuteUpgrade()
    {
        if (MessageBox.Show(
                "即将开始真正部署：\n\n" +
                "  · 停止正在运行的 Argus（含托盘，本向导除外）\n" +
                "  · 备份程序文件与数据库（失败可回滚）\n" +
                "  · 覆盖程序文件 + runtimes 运行时树\n" +
                "  · config.json 合并更新（station_id / results_root / webhook_url / agg_token 保留现场值）\n" +
                "  · data\\ 与 logs\\ 原样保留\n\n" +
                "继续执行？",
                "确认执行升级", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        Log("⏳ 执行部署（停进程 → 备份 → 覆盖 → config 合并 → SHA256 校验）...");
        var (code, output) = await RunDeployScript(execute: true);
        _lastOutput = output;
        var m = Regex.Match(output, @"备份目录:\s*(.+)\r?\n");
        if (m.Success) _backupDir = m.Groups[1].Value.Trim();

        if (code != 0 || !output.Contains("部署完成"))
        {
            Log("✗ 部署未完成——程序文件未被破坏时可用回滚命令恢复：");
            if (!string.IsNullOrEmpty(_backupDir))
                Log($"    powershell -ExecutionPolicy Bypass -File \"{_deployScript}\" -Rollback \"{_backupDir}\"");
            throw new Exception($"部署失败（退出码 {code}），请看日志 [FAIL]/[WARN] 行。");
        }

        _phase = 2;
        Log("");
        Log("🎉 部署完成！");
        if (!string.IsNullOrEmpty(_backupDir))
            Log($"回滚命令（如需）：powershell -ExecutionPolicy Bypass -File \"{_deployScript}\" -Rollback \"{_backupDir}\"");
        btnNext.Text = "完成并启动新版 >";
        btnNext.BackColor = Color.FromArgb(16, 124, 16);
        lblStep.Text = "步骤 4：完成";
    }

    private async Task<(int Code, string Output)> RunDeployScript(bool execute)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{_deployScript}\"" +
                        $" -Zip \"{txtPackagePath.Text}\" -Target \"{AppConfig.BaseDir}\"" +
                        $" -ExcludePid {Environment.ProcessId}" +
                        (execute ? " -Execute -NoStart" : ""),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        Log($"$ deploy_update.ps1 {(execute ? "-Execute -NoStart" : "(演练)")}");
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var output = await outTask;
        var err = await errTask;
        foreach (var line in output.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.Length > 0) Log(t);
        }
        if (!string.IsNullOrWhiteSpace(err))
            foreach (var line in err.Split('\n'))
                if (line.Trim().Length > 0) Log("[stderr] " + line.TrimEnd('\r'));
        return (proc.ExitCode, output + "\n" + err);
    }

    private void CleanupStage()
    {
        if (string.IsNullOrEmpty(_stage) || !Directory.Exists(_stage)) return;
        try { Directory.Delete(_stage, recursive: true); } catch { }
    }

    public static (bool Ok, string Version, string Error) ValidateStage(string stageDir)
    {
        var exe = Path.Combine(stageDir, "Argus.exe");
        if (!File.Exists(exe))
            return (false, "", "包内没有 Argus.exe——这不是 Argus 更新包（应使用 make_release.ps1 产出的 Argus-v*-update.zip）");
        foreach (var sub in new[] { "data", "logs" })
            if (Directory.Exists(Path.Combine(stageDir, sub)))
                return (false, "", $"包内含 {sub}\\ 目录，可能覆盖现场数据，已中止");
        var db = Directory.GetFiles(stageDir, "*.db", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(stageDir, "*.sqlite", SearchOption.AllDirectories)).FirstOrDefault();
        if (db != null)
            return (false, "", $"包内含数据库文件 {Path.GetFileName(db)}，会污染现场数据，已中止");
        var ver = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "";
        return (true, ver, "");
    }

    public static string? FindDeployScript(string baseDir, string stageDir)
    {
        foreach (var cand in new[]
                 {
                     Path.Combine(baseDir, "tools", "deploy_update.ps1"),
                     Path.Combine(baseDir, "deploy_update.ps1"),
                     Path.Combine(stageDir, "deploy_update.ps1"),
                 })
            if (File.Exists(cand)) return cand;
        return null;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        CleanupStage();
        if (_launchAfterClose)
        {
            var exe = Path.Combine(AppConfig.BaseDir, "Argus.exe");
            var bat = Path.Combine(AppConfig.BaseDir, "启动.bat");
            var target = File.Exists(bat) ? bat : exe;
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(2500); Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
                catch { }
            });
        }
    }
}

public static class UpgradeEntry
{
    public const string WizardVersion = "3.22.1";

    public static int Run()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new UpgradeWizard());
        return 0;
    }
}

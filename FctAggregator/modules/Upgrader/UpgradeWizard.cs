using System.Text.RegularExpressions;
using System.Reflection;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;

namespace FctAggregator.modules.Upgrader;

/// <summary>
/// Argus 升级向导（v3.21.1）- 简易图形化版本
/// </summary>
public partial class UpgradeWizard : Form
{
    private string _backupRootPath = "";
    private string _targetVersion = "";
    
    // UI 控件（InitializeComponent 中赋值，此处 null! 抑制构造完成前未赋值告警）
    private Label lblTitle = null!, lblStep = null!;
    private Button btnBrowse = null!, btnNext = null!, btnCancel = null!;
    private ListBox lstLog = null!;
    private TextBox txtPackagePath = null!;
    
    public UpgradeWizard()
    {
        InitializeComponent();
        this.Size = new Size(700, 500);
        this.Text = "Argus v3.21.1 升级向导";
        this.StartPosition = FormStartPosition.CenterScreen;
    }
    
    private void InitializeComponent()
    {
        lblTitle = new Label { Text = "Argus 系统升级向导", Font = new Font("Segoe UI", 14F, FontStyle.Bold), 
            ForeColor = Color.FromArgb(0, 76, 158), Location = new Point(20, 15), Size = new Size(660, 40) };
        
        lblStep = new Label { Text = "步骤 1: 选择升级包 (.zip)", Font = new Font("Segoe UI", 10F), 
            Location = new Point(20, 60) };
        
        txtPackagePath = new TextBox { Location = new Point(20, 90), Size = new Size(500, 25), ReadOnly = true };
        
        btnBrowse = new Button { Text = "浏览...", Location = new Point(530, 90), Size = new Size(80, 25) };
        btnBrowse.Click += (s, e) => OnBrowseClick();
        
        lstLog = new ListBox { Location = new Point(20, 130), Size = new Size(650, 280), 
            BackColor = Color.Black, ForeColor = Color.Lime, Font = new Font("Consolas", 9F) };
        
        Panel pnlButtons = new Panel { Location = new Point(20, 420), Size = new Size(650, 30) };
        btnNext = new Button { Text = "下一步 >", Location = new Point(0, 2), Size = new Size(100, 25), 
            BackColor = Color.FromArgb(0, 120, 215), ForeColor = Color.White };
        btnNext.Click += (s, e) => OnNextClick();
        
        btnCancel = new Button { Text = "取消", Location = new Point(580, 2), Size = new Size(70, 25), 
            BackColor = Color.DarkGray, ForeColor = Color.White };
        btnCancel.Click += (s, e) => Close();
        
        pnlButtons.Controls.AddRange(new Control[] { btnNext, btnCancel });
        
        Controls.AddRange(new Control[] { lblTitle, lblStep, txtPackagePath, btnBrowse, lstLog, pnlButtons });
    }
    
    private void Log(string message)
    {
        Invoke(new Action(() =>
        {
            lstLog.Items.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            lstLog.SetSelected(lstLog.Items.Count - 1, true);
            lstLog.ClearSelected();
        }));
    }
    
    private void OnBrowseClick()
    {
        using var dialog = new OpenFileDialog { Filter = "Argus Package|*.zip" };
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            txtPackagePath.Text = dialog.FileName;
            Log($"✓ 已选择：{Path.GetFileName(dialog.FileName)}");
        }
    }
    
    private async void OnNextClick()
    {
        if (string.IsNullOrEmpty(txtPackagePath.Text))
        {
            MessageBox.Show("请先选择升级包！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        
        try
        {
            btnNext.Enabled = false;
            
            // Step 1: Validate package
            Log("⏳ 验证升级包...");
            await Task.Delay(1000);
            
            var tempDir = Path.Combine(Path.GetTempPath(), $"argus_upg_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);
            ZipFile.ExtractToDirectory(txtPackagePath.Text, tempDir);
            
            if (!File.Exists(Path.Combine(tempDir, "binaries/Argus.exe")))
                throw new Exception("无效格式：缺少 binaries/Argus.exe");
            
            _targetVersion = ExtractVersion(Path.Combine(tempDir, "CHANGELOG.md"));
            Log($"✓ 版本识别成功：v{_targetVersion}");
            
            // Step 2: Backup current files
            Log("⏳ 正在备份当前版本...");
            _backupRootPath = Path.Combine(AppConfig.BaseDir, "..", "backups", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(_backupRootPath);
            
            // Backup database
            var dbPath = Path.Combine(AppConfig.BaseDir, "data", "mesh_agg.db");
            if (File.Exists(dbPath))
            {
                File.Copy(dbPath, Path.Combine(_backupRootPath, "database_backup.db"));
                Log("✓ 数据库已备份");
            }
            
            // Backup old exe
            var oldExe = Path.Combine(AppConfig.BaseDir, "..", "binaries", "Argus.exe");
            if (File.Exists(oldExe))
            {
                File.Copy(oldExe, Path.Combine(_backupRootPath, "Argus_old.exe"));
                Log("✓ 旧版本已保存");
            }
            
            // Step 3: Deploy new version
            Log("⏳ 部署新版本...");
            var newExeSrc = Path.Combine(tempDir, "binaries/Argus.exe");
            var binDir = Path.Combine(AppConfig.BaseDir, "..", "binaries");
            Directory.CreateDirectory(binDir);
            File.Copy(newExeSrc, Path.Combine(binDir, "Argus.exe"), true);
            Log("✓ 新文件已部署");
            
            // Success!
            Log("");
            Log("🎉 升级成功！");
            MessageBox.Show($"升级完成！\n\n版本：v{_targetVersion}\n备份位置：{_backupRootPath}",
                           "Argus 升级向导", MessageBoxButtons.OK, MessageBoxIcon.Information);
            
            // Ask to restart
            if (MessageBox.Show("要现在启动新版本吗？", "提示", 
                               MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Path.Combine(binDir, "Argus.exe")) { UseShellExecute = true });
                }
                catch { }
            }
            
            Close();
        }
        catch (Exception ex)
        {
            Log($"✗ 错误：{ex.Message}");
            MessageBox.Show($"升级失败：{ex.Message}\n\n请检查日志并手动恢复备份。", 
                           "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            btnNext.Enabled = true;
        }
    }
    
    private string ExtractVersion(string changelogPath)
    {
        try
        {
            if (File.Exists(changelogPath))
            {
                var content = File.ReadAllText(changelogPath);
                var match = Regex.Match(content, @"## v(\d+\.\d+(\.\d+)?)");
                return match.Success ? match.Groups[1].Value : "unknown";
            }
        }
        catch { }
        return "unknown";
    }
}

// Entry point helper
public static class UpgradeEntry
{
    public static int Run()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new UpgradeWizard());
        return 0;
    }
}

using System.Diagnostics;
using System.Text;

namespace FctAggregator;

public sealed class UpdatePromptForm : AppForm
{
    private readonly UpdateInfo _info;
    private readonly Database _db;

    private UpdatePromptForm(UpdateInfo info, Database db)
    {
        _info = info;
        _db = db;
        Text = "发现新版本";
        Size = new Size(520, 400);
        MinimumSize = new Size(460, 320);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Bg;
        Font = Theme.Body;
        BuildUi();
    }

    public static bool ShowIfAvailable(Database db)
    {
        try
        {
            var info = UpdateChecker.Scan(db: db);
            if (info == null) return false;
            using var f = new UpdatePromptForm(info, db);
            f.ShowDialog();
            return f._choseUpdate;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[更新器] 弹窗失败: {ex.Message}");
            return false;
        }
    }

    private bool _choseUpdate;
    private Label? _noteLabel;
    private Button? _btnUpdate;

    private void BuildUi()
    {
        var pad = 20;
        var title = new Label
        {
            Text = $"发现新版本  v{_info.Version}",
            Font = Theme.PageTitle,
            ForeColor = Theme.TextMain,
            Location = new Point(pad, 16),
            AutoSize = true,
        };

        var current = new Label
        {
            Text = $"当前版本：v{UpdateChecker.CurrentVersion}",
            ForeColor = Theme.TextSub,
            Font = Theme.Small,
            Location = new Point(pad, 50),
            AutoSize = true,
        };

        var notes = UpdateChecker.GetReleaseNotes(_info.Version);
        var notesBox = new GroupBox
        {
            Text = "本版本特点",
            Location = new Point(pad, 80),
            Size = new Size(ClientSize.Width - pad * 2, 190),
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom,
        };
        var notesText = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextMain,
            Text = string.IsNullOrEmpty(notes) ? "（未提供版本说明）" : notes,
            Dock = DockStyle.Fill,
            Font = Theme.Body,
        };
        notesBox.Controls.Add(notesText);

        _noteLabel = new Label
        {
            Text = "更新包：本地检测到 Argus-v" + _info.Version + "-update.zip。更新不会影响 data 数据库。",
            ForeColor = Theme.TextFaint,
            Font = Theme.Small,
            Location = new Point(pad, 280),
            Size = new Size(ClientSize.Width - pad * 2, 30),
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom | AnchorStyles.Right,
        };

        var btnUpdate = Theme.MakeButton("立即更新", 110, primary: true);
        _btnUpdate = btnUpdate;
        btnUpdate.Location = new Point(ClientSize.Width - pad * 2 - 228, ClientSize.Height - 60);
        btnUpdate.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        btnUpdate.Click += (_, _) => OnUpdateNow();

        var btnLater = Theme.MakeButton("稍后提醒", 100);
        btnLater.Location = new Point(ClientSize.Width - pad - 110, ClientSize.Height - 60);
        btnLater.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        btnLater.Click += (_, _) => { UpdateChecker.MarkPrompted(_info.Version, _db); Close(); };

        Controls.Add(notesBox);
        Controls.Add(_noteLabel);
        Controls.Add(btnUpdate);
        Controls.Add(btnLater);
        Controls.Add(current);
        Controls.Add(title);
    }

    private void OnUpdateNow()
    {
        // D3：StageUpdate 内含 ZipFile.ExtractToDirectory + checkpoint + 整个 data/ 目录备份，
        // 大包/大库时 UI 线程同步跑会白屏数分钟。改后台执行（MainForm 自动更新路径早已如此，手动路径漏改）。
        Cursor = Cursors.WaitCursor;
        if (_btnUpdate != null) _btnUpdate.Enabled = false;
        Task.Run(() =>
        {
            string? msg = null;
            string? err = null;
            try { msg = UpdateChecker.StageUpdate(_info, _db); }
            catch (Exception ex) { err = ex.Message; }
            UiAsync.Post(this, () =>
            {
                Cursor = Cursors.Default;
                if (_btnUpdate != null) _btnUpdate.Enabled = true;
                if (err != null)
                {
                    MessageBox.Show($"更新失败：{err}\n\n可稍后重试。", "更新失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                UpdateChecker.MarkPrompted(_info.Version, _db);
                _choseUpdate = true;
                var restart = MessageBox.Show(
                    msg + "\n\n是否立即重启程序以完成更新？",
                    "更新已准备", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                Close();
                if (restart == DialogResult.Yes) RestartApp();
            });
        });
    }

    private void RestartApp()
    {
        try
        {
            var (fileName, args) = UpdateChecker.RestartCommand("--post-update");
            if (string.IsNullOrEmpty(fileName)) return;
            Process.Start(new ProcessStartInfo(fileName, args)
            {
                WorkingDirectory = AppConfig.BaseDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Warning($"[更新器] 重启失败（可手动重启完成更新）: {ex.Message}");
        }
    }
}

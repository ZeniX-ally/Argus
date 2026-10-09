namespace FctAggregator;

/// <summary>备份页：测试程序（C:\FTS）的压缩备份记录与手动/自动备份。
/// 「Seq 代码管理」（变更监控 + 基线对比）已于 v3.40.0 整块移除。</summary>
public sealed class BackupCodePanel : Panel
{
    private readonly Engine _engine;
    private Label _status = null!;
    private ListView _backupList = null!;
    private Button _btnBackup = null!;
    private TextBox _txtSource = null!;
    private TextBox _txtBackupDir = null!;
    private bool _busy;
    private volatile bool _refreshBusy;
    private int _refreshGen;

    public BackupCodePanel(Engine engine)
    {
        _engine = engine;
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        Padding = new Padding(Theme.Gap);
        BuildUi();
        Refresh2();
    }

    private void BuildUi()
    {
        // Seq 代码管理已移除 → 备份面板独占整页（单栏，不再左右分栏）
        var host = new SectionPanel("测试程序备份") { Dock = DockStyle.Fill };
        host.Content.Controls.Add(BuildBackupPane());
        Controls.Add(host);
    }

    private Control BuildBackupPane()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
        var cfg = _engine.Config;
        _txtSource = NewPathBox(cfg.FctProgramSourceRoot);
        _txtBackupDir = NewPathBox(cfg.FctProgramBackupDir);

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 40, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, BackColor = Theme.Surface, Padding = new Padding(0, 4, 0, 0),
        };
        _btnBackup = Theme.MakeButton("立即备份", 96, primary: true);
        _btnBackup.Click += (_, _) => StartBackup();
        var btnOpen = Theme.MakeButton("打开备份目录", 110);
        btnOpen.Click += (_, _) => OpenBackupDir();
        var btnRefresh = Theme.MakeButton("刷新记录", 90);
        btnRefresh.Click += (_, _) => Refresh2();
        var btnSave = Theme.MakeButton("保存目录", 90);
        btnSave.Click += (_, _) => SavePaths();
        bar.Controls.AddRange(new Control[] { _btnBackup, btnOpen, btnRefresh, btnSave });

        _status = new Label
        {
            Dock = DockStyle.Top, Height = 40, ForeColor = Theme.TextSub, Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _backupList = MakeList("时间", 140, "触发", 72, "大小", 80, "文件数", 64, "状态", 64, "路径", 280);
        _backupList.Dock = DockStyle.Fill;

        host.Controls.Add(_backupList);
        host.Controls.Add(_status);
        host.Controls.Add(bar);
        host.Controls.Add(MakePathRow("备份到", _txtBackupDir, () => BrowseInto(_txtBackupDir, "选择备份输出目录")));
        host.Controls.Add(MakePathRow("程序目录", _txtSource, () => BrowseInto(_txtSource, "选择测试程序根目录（各机台文件夹名可不同）")));
        return host;
    }

    private static TextBox NewPathBox(string text) => new()
    {
        Text = text, Font = Theme.Small, BorderStyle = BorderStyle.FixedSingle,
        Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
    };

    private static Panel MakePathRow(string label, TextBox box, Action browse)
    {
        var row = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = Theme.Surface, Padding = new Padding(0, 2, 0, 0) };
        var lbl = new Label
        {
            Text = label, Dock = DockStyle.Left, Width = 64, Height = 24,
            ForeColor = Theme.TextFaint, Font = Theme.Tiny, TextAlign = ContentAlignment.MiddleLeft,
        };
        var btn = Theme.MakeButton("浏览", 52);
        btn.Dock = DockStyle.Right;
        btn.Height = 24;
        btn.Margin = new Padding(4, 0, 0, 0);
        btn.Click += (_, _) => browse();
        box.Dock = DockStyle.Fill;
        row.Controls.Add(box);
        row.Controls.Add(btn);
        row.Controls.Add(lbl);
        return row;
    }

    private static ListView MakeList(params object[] cols)
    {
        var lv = new ListView
        {
            View = View.Details, FullRowSelect = true, GridLines = true,
            Font = Theme.Mono, HideSelection = false,
        };
        for (int i = 0; i + 1 < cols.Length; i += 2)
            lv.Columns.Add((string)cols[i], (int)cols[i + 1]);
        return lv;
    }

    public void Refresh2()
    {
        if (_refreshBusy) return;
        _refreshBusy = true;
        int gen = ++_refreshGen;
        var cfg = _engine.Config;
        var db = _engine.Db;
        if (_status.Text.Length == 0)
            _status.Text = $"源 {cfg.FctProgramSourceRoot}  →  {cfg.FctProgramBackupDir}\n正在加载备份记录…";
        Task.Run(() =>
        {
            try
            {
                var last = db.GetLatestFctProgramBackupOk();
                var backups = db.ListFctProgramBackups(50);
                UiAsync.Post(this, () =>
                {
                    try
                    {
                        if (IsDisposed || gen != _refreshGen) return;
                        ApplyBackupUi(cfg, last, backups);
                    }
                    finally { if (gen == _refreshGen) _refreshBusy = false; }
                });
            }
            catch (Exception ex)
            {
                Logger.Warning($"[备份页] 刷新失败: {ex.Message}");
                UiAsync.Post(this, () =>
                {
                    _refreshBusy = false;
                    // D5：失败必须可见——原实现只打日志，_status 永驻「正在加载备份记录…」
                    if (_status != null && !IsDisposed)
                        _status.Text = $"备份记录加载失败：{ex.Message}\n（下方可能是上一次的数据，点「刷新」重试）";
                });
            }
        });
    }

    private void ApplyBackupUi(AppConfig cfg, FctProgramBackupRecord? last, List<FctProgramBackupRecord> backups)
    {
        var lastTxt = last == null ? "尚未备份" : last.CreatedAt;
        if (_txtSource != null && !_txtSource.Focused) _txtSource.Text = cfg.FctProgramSourceRoot;
        if (_txtBackupDir != null && !_txtBackupDir.Focused) _txtBackupDir.Text = cfg.FctProgramBackupDir;
        _status.Text = $"源 {cfg.FctProgramSourceRoot}  →  {cfg.FctProgramBackupDir}\n" +
                       $"自动：每月一次  ·  磁盘保留 {cfg.FctProgramBackupKeep} 份  ·  最近成功：{lastTxt}";
        FillBackupList(backups);
    }

    private void FillBackupList(List<FctProgramBackupRecord> backups)
    {
        _backupList.BeginUpdate();
        _backupList.Items.Clear();
        foreach (var r in backups)
        {
            var size = ByteUtil.MbGb(r.ZipBytes);
            var st = r.Status != "ok" ? r.Status : (r.ZipExists ? "有文件" : "已清理");
            var item = new ListViewItem(r.CreatedAt);
            item.SubItems.Add(r.Trigger == FctProgramBackup.TriggerScheduled ? "自动" : "手动");
            item.SubItems.Add(size);
            item.SubItems.Add(r.FileCount.ToString());
            item.SubItems.Add(st);
            item.SubItems.Add(r.ZipPath);
            item.Tag = r;
            if (r.Status != "ok") item.ForeColor = Theme.Danger;
            else if (!r.ZipExists) item.ForeColor = Theme.TextFaint;
            _backupList.Items.Add(item);
        }
        if (_backupList.Items.Count == 0)
            _backupList.Items.Add(new ListViewItem("（尚无备份记录）"));
        _backupList.EndUpdate();
    }

    private void BrowseInto(TextBox box, string title)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = title,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        var cur = (box.Text ?? "").Trim();
        if (Directory.Exists(cur)) dlg.SelectedPath = cur;
        var owner = FindForm();
        if (dlg.ShowDialog(owner) != DialogResult.OK) return;
        box.Text = dlg.SelectedPath;
    }



    private void SavePaths()
    {
        var src = (_txtSource?.Text ?? "").Trim();
        var dest = (_txtBackupDir?.Text ?? "").Trim();
        if (src.Length == 0 || dest.Length == 0)
        {
            MessageBox.Show("程序目录与备份目录都不能为空。", "保存目录",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            src = Path.GetFullPath(src);
            dest = Path.GetFullPath(dest);
        }
        catch (Exception ex)
        {
            MessageBox.Show("路径无效: " + ex.Message, "保存目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var cfg = _engine.Config;
        cfg.FctProgramSourceRoot = src;
        cfg.FctProgramBackupDir = dest;
        if (!cfg.Save())
        {
            MessageBox.Show("写入 config.json 失败，请查看日志。", "保存目录",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (_txtSource != null) _txtSource.Text = src;
        if (_txtBackupDir != null) _txtBackupDir.Text = dest;
        Logger.Info($"[备份页] 已保存目录 源={src} 备份到={dest}");
        Refresh2();
    }

    private void StartBackup()
    {
        if (_busy) return;
        var cfg = _engine.Config;
        if (!cfg.FctProgramBackupEnabled)
        {
            MessageBox.Show("测试程序备份已关闭（fct_program_backup_enabled=false）。", "备份",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _busy = true;
        _btnBackup.Enabled = false;
        _status.Text = $"正在压缩 {cfg.FctProgramSourceRoot} → {cfg.FctProgramBackupDir}，大目录可能需数分钟…";
        var db = _engine.Db;
        var sid = _engine.ResolvedStationId;
        Task.Run(() =>
        {
            // 审计修复：FctProgramBackup.Run 的入口路径解析（GetFullPath）在内部 try 之外，配置留空/非法会
            // 直接抛出；这里原本没有 try/catch → 未观察的 Task 异常 + _busy 永久 true、按钮永久变灰、无提示。
            FctProgramBackupResult rep;
            try { rep = FctProgramBackup.Run(db, cfg, sid, FctProgramBackup.TriggerManual); }
            catch (Exception ex)
            {
                Logger.Error($"[FTS备份] 立即备份异常: {ex.Message}");
                UiAsync.Post(this, () =>
                {
                    _busy = false;
                    _btnBackup.Enabled = true;
                    _status.Text = $"备份失败：{ex.Message}";
                    MessageBox.Show($"备份失败：{ex.Message}", "备份", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
                return;
            }
            // 走 UiAsync.Post：它已处理"窗体已销毁 / 句柄未创建"，裸 BeginInvoke 在关窗瞬间会抛 ObjectDisposedException
            UiAsync.Post(this, () =>
            {
                _busy = false;
                _btnBackup.Enabled = true;
                Refresh2();
                MessageBox.Show(rep.Message, rep.Ok ? "备份完成" : "备份失败",
                    MessageBoxButtons.OK, rep.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            });
        });
    }

    private void OpenBackupDir()
    {
        var dir = _engine.Config.FctProgramBackupDir;
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir, UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

}

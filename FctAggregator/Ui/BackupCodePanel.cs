namespace FctAggregator;

/// <summary>备份与 Seq 代码管理页：C:\FTS 压缩备份记录 + Seq 变更对比。</summary>
public sealed class BackupCodePanel : Panel
{
    private readonly Engine _engine;
    private Label _status = null!;
    private Label _seqHint = null!;
    private ListView _backupList = null!;
    private ListView _seqList = null!;
    private Button _btnBackup = null!;
    private TextBox _txtSource = null!;
    private TextBox _txtBackupDir = null!;
    private TextBox _txtSeq = null!;
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
        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg,
        };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

        var left = new SectionPanel("测试程序备份") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.Gap, 0) };
        var right = new SectionPanel("Seq 代码管理") { Dock = DockStyle.Fill, Margin = new Padding(0) };

        left.Content.Controls.Add(BuildBackupPane());
        right.Content.Controls.Add(BuildSeqPane());
        split.Controls.Add(left, 0, 0);
        split.Controls.Add(right, 1, 0);
        Controls.Add(split);
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

    private Control BuildSeqPane()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
        _txtSeq = NewPathBox(FctSeqSnapshot.ResolveSeqRoot(_engine.Config));

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 40, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, BackColor = Theme.Surface, Padding = new Padding(0, 4, 0, 0),
        };
        var btnCompare = Theme.MakeButton("对比当前变更", 110, primary: true);
        btnCompare.Click += (_, _) => CompareNow();
        var btnBaseline = Theme.MakeButton("重建基线", 90);
        btnBaseline.Click += (_, _) => RebuildBaseline();
        var btnRefreshSeq = Theme.MakeButton("刷新记录", 90);
        btnRefreshSeq.Click += (_, _) => Refresh2();
        var btnFind = Theme.MakeButton("自动查找", 90);
        btnFind.Click += (_, _) => FindSeqDir();
        var btnSave = Theme.MakeButton("保存目录", 90);
        btnSave.Click += (_, _) => SavePaths();
        bar.Controls.AddRange(new Control[] { btnCompare, btnBaseline, btnRefreshSeq, btnFind, btnSave });

        _seqHint = new Label
        {
            Dock = DockStyle.Top, Height = 40, ForeColor = Theme.TextSub, Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _seqList = MakeList("时间", 140, "新增", 56, "删除", 56, "修改", 56, "目录", 280);
        _seqList.Dock = DockStyle.Fill;
        _seqList.DoubleClick += (_, _) => CompareNow();

        host.Controls.Add(_seqList);
        host.Controls.Add(_seqHint);
        host.Controls.Add(bar);
        host.Controls.Add(MakePathRow("Seq 目录", _txtSeq, () => BrowseInto(_txtSeq, "选择 Seq 测试程序目录")));
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
        var seqRoot = FctSeqSnapshot.ResolveSeqRoot(cfg);
        if (_status.Text.Length == 0)
            _status.Text = $"源 {cfg.FctProgramSourceRoot}  →  {cfg.FctProgramBackupDir}\n正在加载备份记录…";
        Task.Run(() =>
        {
            try
            {
                var last = db.GetLatestFctProgramBackupOk();
                var backups = db.ListFctProgramBackups(50);
                var seqLogs = db.ListFctSeqChangeLogs(50);
                var exists = Directory.Exists(seqRoot);
                var hasBase = FctSeqSnapshot.HasBaseline();
                string baseAt = "无";
                if (hasBase)
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(FctSeqSnapshot.BaselineManifestPath));
                        if (doc.RootElement.TryGetProperty("captured_at", out var c))
                            baseAt = c.GetString() ?? "无";
                    }
                    catch { }
                }
                SeqChangeReport? pending = null;
                if (exists && hasBase)
                    pending = FctSeqSnapshot.CompareToBaseline(seqRoot);
                UiAsync.Post(this, () =>
                {
                    try
                    {
                        if (IsDisposed || gen != _refreshGen) return;
                        ApplyBackupUi(cfg, last, backups);
                        ApplySeqUi(seqRoot, exists, baseAt, pending, seqLogs);
                    }
                    finally { if (gen == _refreshGen) _refreshBusy = false; }
                });
            }
            catch (Exception ex)
            {
                Logger.Warning($"[备份页] 刷新失败: {ex.Message}");
                UiAsync.Post(this, () => _refreshBusy = false);
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
            var size = r.ZipBytes >= 1_000_000_000 ? $"{r.ZipBytes / 1_000_000_000.0:F2} GB" : $"{r.ZipBytes / 1_000_000.0:F1} MB";
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

    private void ApplySeqUi(string root, bool exists, string baseAt, SeqChangeReport? pending,
        List<(long Id, string At, int Added, int Removed, int Modified, string Root)> logs)
    {
        var pendingTxt = pending == null || !pending.HasChanges
            ? "相对基线无未确认变更"
            : $"未确认：+{pending.Added} -{pending.Removed} ~{pending.Modified}（点「对比当前变更」）";
        if (_txtSeq != null && !_txtSeq.Focused) _txtSeq.Text = root;
        _seqHint.Text = $"目录 {(exists ? root : root + "（不存在）")}\n基线 {baseAt}  ·  {pendingTxt}";
        _seqList.BeginUpdate();
        _seqList.Items.Clear();
        foreach (var r in logs)
        {
            var item = new ListViewItem(r.At);
            item.SubItems.Add(r.Added.ToString());
            item.SubItems.Add(r.Removed.ToString());
            item.SubItems.Add(r.Modified.ToString());
            item.SubItems.Add(r.Root);
            _seqList.Items.Add(item);
        }
        if (_seqList.Items.Count == 0)
            _seqList.Items.Add(new ListViewItem("（尚无变更记录）"));
        _seqList.EndUpdate();
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

    private void FindSeqDir()
    {
        var src = (_txtSource?.Text ?? "").Trim();
        if (src.Length == 0) src = _engine.Config.FctProgramSourceRoot;
        var hits = FctSeqSnapshot.DiscoverSeqRoots(src);
        if (hits.Count == 0)
        {
            MessageBox.Show($"在 {src} 下没有找到名为 Seq 的目录。\n请用「浏览」直接选中本机测试程序 Seq 文件夹。",
                "自动查找", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (hits.Count == 1)
        {
            _txtSeq.Text = hits[0];
            MessageBox.Show($"已填入:\n{hits[0]}\n请点「保存目录」生效。", "自动查找",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var pick = PickPath("选择 Seq 目录（本机产品文件夹名可能不同）", hits);
        if (!string.IsNullOrEmpty(pick)) _txtSeq.Text = pick;
    }

    private string? PickPath(string title, List<string> paths)
    {
        using var f = new Form
        {
            Text = title, Width = 560, Height = 340,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
            Font = Theme.Body,
        };
        var lb = new ListBox { Dock = DockStyle.Fill, Font = Theme.Small };
        foreach (var p in paths) lb.Items.Add(p);
        if (lb.Items.Count > 0) lb.SelectedIndex = 0;
        lb.DoubleClick += (_, _) => f.DialogResult = DialogResult.OK;
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8, 6, 8, 6),
        };
        var ok = Theme.MakeButton("确定", 80, primary: true);
        var cancel = Theme.MakeButton("取消", 80);
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        bar.Controls.Add(cancel);
        bar.Controls.Add(ok);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        f.Controls.Add(lb);
        f.Controls.Add(bar);
        return f.ShowDialog(FindForm()) == DialogResult.OK ? lb.SelectedItem as string : null;
    }

    private void SavePaths()
    {
        var src = (_txtSource?.Text ?? "").Trim();
        var dest = (_txtBackupDir?.Text ?? "").Trim();
        var seq = (_txtSeq?.Text ?? "").Trim();
        if (src.Length == 0 || dest.Length == 0 || seq.Length == 0)
        {
            MessageBox.Show("程序目录、备份目录、Seq 目录都不能为空。", "保存目录",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            src = Path.GetFullPath(src);
            dest = Path.GetFullPath(dest);
            seq = Path.GetFullPath(seq);
        }
        catch (Exception ex)
        {
            MessageBox.Show("路径无效: " + ex.Message, "保存目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var cfg = _engine.Config;
        var seqChanged = !string.Equals(cfg.FctSeqRoot, seq, StringComparison.OrdinalIgnoreCase);
        cfg.FctProgramSourceRoot = src;
        cfg.FctProgramBackupDir = dest;
        cfg.FctSeqRoot = seq;
        if (!cfg.Save())
        {
            MessageBox.Show("写入 config.json 失败，请查看日志。", "保存目录",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (_txtSource != null) _txtSource.Text = src;
        if (_txtBackupDir != null) _txtBackupDir.Text = dest;
        if (_txtSeq != null) _txtSeq.Text = seq;
        try { _engine.StartSeqMonitor(); }
        catch (Exception ex) { Logger.Warning($"[备份页] 重启 Seq 监视失败: {ex.Message}"); }
        Logger.Info($"[备份页] 已保存目录 源={src} 备份到={dest} Seq={seq}");
        if (seqChanged && Directory.Exists(seq))
        {
            if (MessageBox.Show("Seq 监视目录已更改。是否按新目录重建基线快照？\n（不重建的话，对比会相对旧目录的基线。）",
                    "重建基线", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                try { FctSeqSnapshot.CaptureBaseline(seq); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "重建基线失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }
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
            var rep = FctProgramBackup.Run(db, cfg, sid, FctProgramBackup.TriggerManual);
            BeginInvoke(() =>
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

    private void CompareNow()
    {
        var cfg = _engine.Config;
        var root = FctSeqSnapshot.ResolveSeqRoot(cfg);
        if (!Directory.Exists(root))
        {
            MessageBox.Show($"Seq 目录不存在:\n{root}\n\n请在上方填写本机路径，或点「自动查找 / 浏览」后「保存目录」。",
                "Seq", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!FctSeqSnapshot.HasBaseline())
        {
            if (MessageBox.Show("尚无基线快照，现在以当前目录建立基线？", "Seq",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            FctSeqSnapshot.CaptureBaseline(root);
            Refresh2();
            return;
        }
        var report = FctSeqSnapshot.CompareToBaseline(root);
        if (!report.HasChanges)
        {
            MessageBox.Show("相对基线无变更。", "Seq", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        UiBridge.ShowSeqChange(report, cfg, () =>
        {
            FctSeqMonitor.Instance?.ResetAlertFingerprint();
            Refresh2();
        });
    }

    private void RebuildBaseline()
    {
        var root = FctSeqSnapshot.ResolveSeqRoot(_engine.Config);
        if (!Directory.Exists(root))
        {
            MessageBox.Show($"Seq 目录不存在:\n{root}\n\n请在上方填写本机路径，或点「自动查找 / 浏览」后「保存目录」。",
                "Seq", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show($"将当前\n{root}\n保存为新基线快照，后续变更将相对此次对比。继续？",
                "重建 Seq 基线", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        try
        {
            FctSeqSnapshot.CaptureBaseline(root);
            FctSeqMonitor.Instance?.ResetAlertFingerprint();
            Refresh2();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

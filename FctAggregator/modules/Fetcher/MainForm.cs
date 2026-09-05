using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using FctAggregator;

namespace FctFetcher;

public sealed class MainForm : Form
{
	private readonly TextBox _txtResults = new();

	private readonly TextBox _txtTdms = new();

	private readonly TextBox _txtOut = new();

	private readonly DateTimePicker _dtStart = new();

	private readonly DateTimePicker _dtEnd = new();

	private readonly CheckBox _chkPack = new();

	private readonly CheckBox _chkOnline = new();

	private readonly Button _btnRun = new();

	private readonly Button _btnOpen = new();

	private readonly ProgressBar _bar = new();

	private readonly Label _summary = new();

	private readonly TabControl _tabs = new();

	private readonly DataGridView _grid = new DataGridView();

	private readonly TextBox _log = new TextBox();

	private Config _cfg;

	private string _lastOut = "";

	private List<Record> _recs = new();

	private TableLayoutPanel? _inputPanel;

	public MainForm()
	{
		_cfg = Config.Load(Program.ConfigPath);
		Text = "FCT-Fetcher  v1.1.0  —  fail 文件捞取器";
		Width = 1080;
		Height = 740;
		MinimumSize = new Size(900, 620);
		StartPosition = FormStartPosition.CenterScreen;
		Font = Theme.Body;
		LoadAppIcon();

		_summary.Font = Theme.BodyBold;
		_summary.ForeColor = Theme.ToolSummary;
		_log.Font = Theme.Mono;
		_log.BackColor = Color.White;
		_log.ForeColor = Theme.ToolFixed;

		BuildInputArea();
		BuildResultArea();
	}

	private void BuildInputArea()
	{
		var root = new TableLayoutPanel
		{
			RowCount = 4,
			ColumnCount = 8,
			Dock = DockStyle.Top,
			Height = 148,
			Padding = new Padding(14, Theme.Gap, 14, 0),
		};
		root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.InputLabelWidth));
		root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.InputFieldWidth));
		root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.BrowseWidth + 4));
		root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.InputLabelWidth));
		root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.InputFieldWidth / 2));
		root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.BrowseWidth + 4));
		root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));
		root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

		AddLab(root, "Results 根目录", 0, 0);
		_txtResults.Dock = DockStyle.Fill;
		_txtResults.Text = _cfg.ResultsRoot;
		root.SetColumn(_txtResults, 1);
		root.SetRow(_txtResults, 0);
		AddBrowse(root, 2, 0, _txtResults);

		AddLab(root, "TDMS 根目录", 0, 1);
		_txtTdms.Dock = DockStyle.Fill;
		_txtTdms.Text = _cfg.TdmsRoot;
		root.SetColumn(_txtTdms, 1);
		root.SetRow(_txtTdms, 1);
		AddBrowse(root, 2, 1, _txtTdms);

		AddLab(root, "输出目录", 0, 2);
		_txtOut.Dock = DockStyle.Fill;
		_txtOut.Text = _cfg.ResolveOutputDir(Program.ExeDir);
		root.SetColumn(_txtOut, 1);
		root.SetRow(_txtOut, 2);
		AddBrowse(root, 2, 2, _txtOut);

		AddLab(root, "日期区间", 0, 3);
		_dtStart.Format = DateTimePickerFormat.Custom;
		_dtStart.CustomFormat = "yyyy-MM-dd";
		_dtStart.Value = DateTime.Today.AddDays(-2.0);
		root.SetColumn(_dtStart, 1);
		root.SetRow(_dtStart, 3);

		var lblDash = new Label { Text = "~", TextAlign = ContentAlignment.MiddleLeft, AutoSize = true, Padding = new Padding(4, 0, 0, 0) };
		root.SetColumn(lblDash, 2);
		root.SetRow(lblDash, 3);
		root.SetColumnSpan(lblDash, 1);

		_dtEnd.Format = DateTimePickerFormat.Custom;
		_dtEnd.CustomFormat = "yyyy-MM-dd";
		_dtEnd.Value = DateTime.Today;
		root.SetColumn(_dtEnd, 3);
		root.SetRow(_dtEnd, 3);

		var lblHint = new Label { Text = "(含首尾两天)", AutoSize = true, ForeColor = Theme.TextSub };
		root.SetColumn(lblHint, 4);
		root.SetRow(lblHint, 3);

		_chkPack.Text = "分类归集并打包为 {日期}.zip";
		_chkPack.Checked = _cfg.PackFiles;
		root.SetColumn(_chkPack, 5);
		root.SetRow(_chkPack, 3);

		_chkOnline.Text = "同时扫 Online (通常不需要)";
		_chkOnline.Checked = _cfg.Categories.Any(c => c.Equals("Online", StringComparison.OrdinalIgnoreCase));
		root.SetColumn(_chkOnline, 6);
		root.SetRow(_chkOnline, 3);

		root.RowCount = 5;
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.InputHeight + Theme.InputGap));
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.ActionBtnHeight + Theme.InputGap));
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.InputHeight + Theme.InputGap));
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.ActionBtnHeight + Theme.InputGap));
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

		_btnRun.Text = "开始捞取";
		_btnRun.Width = Theme.MediumBtnWidth;
		_btnRun.Height = Theme.ActionBtnHeight;
		_btnRun.Click += OnRun;
		root.SetColumn(_btnRun, 1);
		root.SetRow(_btnRun, 4);

		_btnOpen.Text = "打开输出目录";
		_btnOpen.Width = Theme.MediumBtnWidth;
		_btnOpen.Height = Theme.ActionBtnHeight;
		_btnOpen.Enabled = false;
		_btnOpen.Click += delegate
		{
			if (Directory.Exists(_lastOut))
			{
				Process.Start("explorer.exe", _lastOut);
			}
		};
		root.SetColumn(_btnOpen, 2);
		root.SetRow(_btnOpen, 4);

		_bar.Dock = DockStyle.Fill;
		root.SetColumn(_bar, 3);
		root.SetRow(_bar, 4);
		root.SetColumnSpan(_bar, 3);

		_summary.Dock = DockStyle.Fill;
		_summary.Text = "就绪。";
		root.SetColumn(_summary, 0);
		root.SetRow(_summary, 5);
		root.SetColumnSpan(_summary, 7);

		_inputPanel = root;
		Controls.Add(root);
	}

	private void BuildResultArea()
	{
		var container = new TableLayoutPanel
		{
			RowCount = 1,
			ColumnCount = 1,
			Dock = DockStyle.Fill,
			Padding = new Padding(14, Theme.Gap, 14, 12),
		};

		_tabs.Dock = DockStyle.Fill;
		_tabs.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

		TabPage tabPage = new("捞取结果");
		_grid.Dock = DockStyle.Fill;
		_grid.ReadOnly = true;
		_grid.AllowUserToAddRows = false;
		_grid.AllowUserToDeleteRows = false;
		_grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
		_grid.MultiSelect = false;
		_grid.RowHeadersVisible = false;
		_grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
		_grid.BackgroundColor = Color.White;
		_grid.EnableHeadersVisualStyles = false;
		_grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.ToolDarkBg;
		_grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.ToolDarkFg;
		_grid.ColumnHeadersDefaultCellStyle.Font = Theme.BodyBold;
		_grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
		_grid.ColumnHeadersHeight = 30;
		_grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.ToolAltRow;
		_grid.CellDoubleClick += OnGridDoubleClick;
		Theme.Apply(_grid);
		tabPage.Controls.Add(_grid);
		_tabs.TabPages.Add(tabPage);

		TabPage tabPage2 = new("日志");
		_log.Dock = DockStyle.Fill;
		_log.Multiline = true;
		_log.ScrollBars = ScrollBars.Vertical;
		_log.ReadOnly = true;
		tabPage2.Controls.Add(_log);
		_tabs.TabPages.Add(tabPage2);

		container.Controls.Add(_tabs, 0, 0);
		Controls.Add(container);
		SetupGridColumns();
	}

	private static void AddLab(TableLayoutPanel root, string text, int col, int row)
	{
		var lbl = new Label
		{
			Text = text,
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleRight,
			Font = Theme.Body,
		};
		root.Controls.Add(lbl, col, row);
	}

	private static void AddBrowse(TableLayoutPanel root, int col, int row, TextBox target)
	{
		TextBox target2 = target;
		Button button = new()
		{
			Text = "...",
			Width = Theme.BrowseWidth,
			Height = Theme.BrowseHeight,
		};
		button.Click += delegate
		{
			using FolderBrowserDialog folderBrowserDialog = new();
			if (Directory.Exists(target2.Text))
			{
				folderBrowserDialog.SelectedPath = target2.Text;
			}
			if (folderBrowserDialog.ShowDialog() == DialogResult.OK)
			{
				target2.Text = folderBrowserDialog.SelectedPath;
			}
		};
		root.SetColumn(button, col);
		root.SetRow(button, row);
		button.Anchor = AnchorStyles.Left;
		root.Controls.Add(button);
	}

	private void SetupGridColumns()
	{
		DataGridViewContentAlignment align2 = DataGridViewContentAlignment.MiddleCenter;
		Col("Sn", "SN", 250, align2);
		Col("Station", "站点", 60, align2);
		Col("Date", "日期", 80, align2);
		Col("Model", "型号", 80, align2);
		Col("FailCount", "失败项数", 70, align2);
		Col("Csv", "CSV", 55, align2);
		Col("TdmsCount", "TDMS", 60, align2);
		Col("FailItems", "失败项", 360);
		void Col(string name, string header, int width, DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
		{
			DataGridViewTextBoxColumn col = new()
			{
				Name = name,
				HeaderText = header,
				Width = width,
				SortMode = DataGridViewColumnSortMode.Automatic,
			};
			col.DefaultCellStyle.Alignment = align;
			_grid.Columns.Add(col);
		}
	}

	private void LoadAppIcon()
	{
		try
		{
			Assembly assembly = typeof(MainForm).Assembly;
			string? text = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("app_icon.ico", StringComparison.OrdinalIgnoreCase));
			if (text == null) return;
			using Stream? stream = assembly.GetManifestResourceStream(text);
			if (stream != null)
			{
				Icon = new Icon(stream);
			}
		}
		catch { }
	}

	private void OnGridDoubleClick(object? sender, DataGridViewCellEventArgs e)
	{
		if (e.RowIndex >= 0 && e.RowIndex < _recs.Count)
		{
			string xmlPath = _recs[e.RowIndex].XmlPath;
			if (File.Exists(xmlPath))
			{
				Process.Start("explorer.exe", "/select,\"" + xmlPath + "\"");
			}
		}
	}

	private void Log(string s)
	{
		string s2 = s;
		if (_log.InvokeRequired)
		{
			_log.BeginInvoke(delegate { Log(s2); });
		}
		else
		{
			_log.AppendText(s2 + Environment.NewLine);
		}
	}

	private async void OnRun(object? sender, EventArgs e)
	{
		if (_dtStart.Value.Date > _dtEnd.Value.Date)
		{
			MessageBox.Show("起始日期晚于结束日期。", "参数错误", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
			return;
		}
		_cfg.ResultsRoot = _txtResults.Text.Trim();
		_cfg.TdmsRoot = _txtTdms.Text.Trim();
		_cfg.OutputDir = _txtOut.Text.Trim();
		_cfg.PackFiles = _chkPack.Checked;
		_cfg.Categories = !_chkOnline.Checked ? new string[1] { "Offline" } : new string[2] { "Offline", "Online" };
		try
		{
			_cfg.Save(Program.ConfigPath);
		}
		catch (Exception ex)
		{
			Log("[警告] 配置保存失败(未写入磁盘), 本次运行使用内存中的配置: " + ex.Message);
			MessageBox.Show("配置保存失败, 未写入磁盘, 请检查 " + Program.ConfigPath + " 内容。\n\n" + ex.Message, "配置保存失败", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
		}
		_btnRun.Enabled = false;
		_log.Clear();
		_grid.Rows.Clear();
		_recs.Clear();
		_bar.Value = 0;
		_summary.Text = "正在扫描...";
		_summary.ForeColor = Theme.ToolSummary;
		DateTime start = _dtStart.Value.Date;
		DateTime end = _dtEnd.Value.Date;
		Log($"日期区间: {start:yyyy-MM-dd} ~ {end:yyyy-MM-dd} (含首尾)");
		try
		{
			var (list, scanStats, text, result) = await Task.Run(delegate
			{
				ScanStats stats;
				List<Record> list2 = Scanner.Scan(_cfg, start, end, out stats, Log, delegate(int d, int t)
				{
					if (t > 0)
					{
						int p = (int)(100L * (long)d / t);
						if (_bar.InvokeRequired)
						{
							_bar.BeginInvoke(delegate { _bar.Value = Math.Min(p, 100); });
						}
					}
				});
				if (list2.Count == 0) return (list2, stats, "", (Packager.Result?)null);
				FileLocator.Attach(list2, _cfg, Log);
				string outDir = _cfg.ResolveOutputDir(Program.ExeDir);
				string text2 = Exporter.Export(list2, _cfg, start, end, outDir);
				Packager.Result? item = _cfg.PackFiles ? Packager.Pack(list2, outDir, start, end, text2, _cfg.KeepStageDir, Log) : null;
				return (list2, stats, text2, item);
			});
			_bar.Value = 100;
			_recs = list;
			Log("");
			Log(new string('=', 58));
			Log($"扫描 XML 总数      : {scanStats.XmlTotal}");
			Log($"  路径不合规跳过   : {scanStats.SkipBadPath}");
			Log($"  日期区间外       : {scanStats.SkipRange}");
			Log($"  区间内           : {scanStats.InRange}");
			Log($"    无 fail 项跳过  : {scanStats.SkipNoFail}");
			Log($"    debug 跳过     : {scanStats.SkipDebug}");
			if (scanStats.SkipParseError > 0)
			{
				Log($"    XML 解析失败   : {scanStats.SkipParseError}");
			}
			Log($"  >> 命中(含fail项) : {scanStats.Fail}");
			Log(new string('=', 58));
			if (list.Count == 0)
			{
				_summary.Text = $"未捞到含 fail 项的记录。（区间内 {scanStats.InRange} 个 XML，无 fail {scanStats.SkipNoFail}，debug {scanStats.SkipDebug}）";
				_summary.ForeColor = Theme.ToolDim;
				return;
			}
			foreach (Record item2 in list)
			{
				_grid.Rows.Add(item2.Sn, item2.Station, item2.Date, item2.Model, item2.FailItems.Count,
					item2.CsvPath.Length > 0 ? "✔" : "—", item2.TdmsPaths.Count,
					string.Join(" | ", item2.FailItems.Select(x => x.Name)));
			}
			for (int i = 0; i < list.Count; i++)
			{
				if (list[i].CsvPath.Length == 0 || list[i].TdmsPaths.Count == 0)
				{
					_grid.Rows[i].DefaultCellStyle.BackColor = Theme.ToolHighlight;
				}
			}
			int havingCsv = list.Count(r => r.CsvPath.Length > 0);
			int havingTdms = list.Count(r => r.TdmsPaths.Count > 0);
			int distinctSn = list.Select(r => r.Sn).Distinct().Count();
			Log($"CSV  命中: {havingCsv}/{list.Count}");
			Log($"TDMS 命中: {havingTdms}/{list.Count}" + (Directory.Exists(_cfg.TdmsRoot) ? "" : ("   [目录不存在: " + _cfg.TdmsRoot + "]")));
			Log("");
			Log("清单已输出: " + text);
			var sb = new StringBuilder();
			sb.Append("命中 ").Append(list.Count).Append(" 条 / SN ").Append(distinctSn).Append(" 个    ");
			sb.Append("CSV ").Append(havingCsv).Append('/').Append(list.Count).Append("    TDMS ").Append(havingTdms).Append('/').Append(list.Count);
			if (result != null)
			{
				Log("已打包: " + result.ZipPath);
				Log($"  共 {result.Total} 个文件 (xml {result.Xml} / csv {result.Csv} / tdms {result.Tdms}), 压缩后 {Packager.HumanSize(result.ZipBytes)}");
				sb.Append("    已打包 ").Append(Path.GetFileName(result.ZipPath))
				  .Append(" (").Append(result.Total).Append(" 个文件, ").Append(Packager.HumanSize(result.ZipBytes)).Append(")");
			}
			_summary.Text = sb.ToString();
			_summary.ForeColor = havingCsv == list.Count && havingTdms == list.Count ? Theme.ToolFixed : Theme.ToolDim;
			_lastOut = _cfg.ResolveOutputDir(Program.ExeDir);
			_btnOpen.Enabled = true;
			_tabs.SelectedIndex = 0;
		}
		catch (Exception ex2)
		{
			Log("[异常] " + ex2.GetType().Name + ": " + ex2.Message);
			_summary.Text = "失败: " + ex2.Message;
			_summary.ForeColor = Theme.ToolSummary;
			MessageBox.Show(ex2.Message, "捞取失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
		finally
		{
			_btnRun.Enabled = true;
		}
	}
}

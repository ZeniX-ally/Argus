using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using FctAggregator;

namespace FctFailRanker;

public class MainForm : Form
{
	private class FsTag
	{
		public bool IsDir;

		public string Path = "";

		public bool Loaded;
	}

	private readonly TextBox _txtRoot = new();

	private readonly DateTimePicker _dtStart = new();

	private readonly DateTimePicker _dtEnd = new();

	private readonly Button _btnScan = new();

	private readonly Button _btnExport = new();

	private readonly Button _btnExportRank = new();

	private readonly Button _btnExportXlsx = new();

	private readonly Button _btnBrowse = new();

	private readonly ProgressBar _progress = new();

	private readonly ListView _lvRank = new();

	private readonly TextBox _txtLog = new();

	private readonly Label _lblSummary = new();

	private readonly Label _lblSurvey = new();

	private readonly TreeView _tvSurvey = new();

	private readonly ComboBox _cbModel = new();

	private readonly Label _lblModelShare = new();

	private List<XmlRecord> _records = new();

	private CsvExporter.Summary? _summary;

	private List<CsvExporter.FailRank> _ranks = new();

	private List<CsvExporter.GroupResult> _byModel = new();

	private TableLayoutPanel? _inputPanel;

	public MainForm()
	{
		Text = "FCT 不良项排名导出工具 v1.5.0";
		Width = 980;
		Height = 720;
		StartPosition = FormStartPosition.CenterScreen;
		Font = Theme.Body;
		TryLoadIcon();

		_lblSummary.Font = Theme.BodyBold;
		_lblSurvey.Font = Theme.Small;
		_txtLog.Font = Theme.Mono;
		_txtLog.BackColor = Theme.ToolLogBg;
		_txtLog.ForeColor = Theme.ToolLogFg;
		_lblSummary.ForeColor = Theme.ToolSummary;

		BuildInputArea();
		BuildResultArea();

		Width = 980;
		Height = 720;
		MinimumSize = new Size(820, 580);
	}

	private void BuildInputArea()
	{
		var root = new TableLayoutPanel
		{
			RowCount = 4,
			ColumnCount = 6,
			Dock = DockStyle.Top,
			Height = 130,
			Padding = new Padding(12, 18, 12, 0),
		};
		root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
		root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 640));
		root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.BrowseWidth + 4));
		root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
		root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
		root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

		AddLab(root, "结果目录:", 0, 0);
		_txtRoot.Dock = DockStyle.Fill;
		_txtRoot.Text = LoadDefaultRoot();
		root.SetColumn(_txtRoot, 1);
		root.SetRow(_txtRoot, 0);
		root.SetColumnSpan(_txtRoot, 1);
		_btnBrowse.Text = "...";
		_btnBrowse.Width = Theme.BrowseWidth;
		_btnBrowse.Height = Theme.BrowseHeight;
		_btnBrowse.Click += (_, _) => BrowseFolder();
		root.SetColumn(_btnBrowse, 2);
		root.SetRow(_btnBrowse, 0);

		AddLab(root, "起始日期:", 0, 1);
		_dtStart.Format = DateTimePickerFormat.Custom;
		_dtStart.CustomFormat = "yyyy-MM-dd";
		_dtStart.Value = DateTime.Today.AddDays(-7.0);
		root.SetColumn(_dtStart, 1);
		root.SetRow(_dtStart, 1);
		AddLab(root, "结束日期:", 2, 1);
		_dtEnd.Format = DateTimePickerFormat.Custom;
		_dtEnd.CustomFormat = "yyyy-MM-dd";
		_dtEnd.Value = DateTime.Today;
		root.SetColumn(_dtEnd, 3);
		root.SetRow(_dtEnd, 1);

		_btnScan.Text = "扫描统计";
		_btnScan.Width = Theme.SmallBtnWidth;
		_btnScan.Height = 62;
		_btnScan.Click += async delegate { await ScanAsync(); };
		root.SetColumn(_btnScan, 4);
		root.SetRow(_btnScan, 1);
		root.SetRowSpan(_btnScan, 2);

		_btnExportXlsx.Text = "导出 Excel\n(含分组排名)";
		_btnExportXlsx.Width = 110;
		_btnExportXlsx.Height = 62;
		_btnExportXlsx.Enabled = false;
		_btnExportXlsx.Click += (_, _) => ExportXlsx();
		root.SetColumn(_btnExportXlsx, 5);
		root.SetRow(_btnExportXlsx, 1);

		_btnExport.Text = "导出完整 CSV";
		_btnExport.Width = Theme.SmallBtnWidth;
		_btnExport.Height = Theme.InputHeight;
		_btnExport.Enabled = false;
		_btnExport.Click += (_, _) => ExportCsv(rankOnly: false);
		root.SetColumn(_btnExport, 5);
		root.SetRow(_btnExport, 2);

		_btnExportRank.Text = "仅排名表 CSV";
		_btnExportRank.Width = Theme.SmallBtnWidth;
		_btnExportRank.Height = Theme.InputHeight;
		_btnExportRank.Enabled = false;
		_btnExportRank.Click += (_, _) => ExportCsv(rankOnly: true);
		root.SetColumn(_btnExportRank, 5);
		root.SetRow(_btnExportRank, 2);

		_progress.Dock = DockStyle.Fill;
		root.SetColumn(_progress, 0);
		root.SetRow(_progress, 3);
		root.SetColumnSpan(_progress, 6);

		root.Controls.AddRange(new Control[] { _txtRoot, _btnBrowse, _dtStart, _dtEnd, _btnScan, _btnExportXlsx, _btnExport, _btnExportRank, _progress });
		for (int c = 0; c < 6; c++)
			root.ColumnStyles[c] = new ColumnStyle(SizeType.Absolute, c switch { 1 => 640, 4 => 128, _ => root.ColumnStyles[c].Width });
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.InputHeight + 4));

		_inputPanel = root;
		Controls.Add(root);
	}

	private void BuildResultArea()
	{
		var container = new TableLayoutPanel
		{
			RowCount = 4,
			ColumnCount = 1,
			Dock = DockStyle.Fill,
			Padding = new Padding(12, 0, 12, 12),
		};
		container.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
		container.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
		container.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		container.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));

		_lblSummary.Text = "请设置时间段后点击【扫描统计】";
		_lblSummary.Dock = DockStyle.Fill;
		container.Controls.Add(_lblSummary, 0, 0);

		_lblSurvey.Text = "扫描概览:";
		_lblSurvey.Dock = DockStyle.Fill;
		container.Controls.Add(_lblSurvey, 0, 1);

		var split = new SplitContainer
		{
			Dock = DockStyle.Fill,
			Orientation = Orientation.Vertical,
			SplitterDistance = 360,
			FixedPanel = FixedPanel.Panel1,
			IsSplitterFixed = false,
		};
		_tvSurvey.Dock = DockStyle.Fill;
		split.Panel1.Controls.Add(_tvSurvey);

		var rightPanel = new TableLayoutPanel
		{
			RowCount = 3,
			ColumnCount = 1,
			Dock = DockStyle.Fill,
		};
		rightPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
		rightPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
		rightPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

		var lblRank = new Label { Text = "不良项排名:", Dock = DockStyle.Fill };
		rightPanel.Controls.Add(lblRank, 0, 0);

		var modelRow = new Panel { Dock = DockStyle.Fill, Height = 24 };
		var lblModel = new Label { Text = "型号:", AutoSize = true, Left = 0, Top = 2 };
		_cbModel.Width = 160;
		_cbModel.DropDownStyle = ComboBoxStyle.DropDownList;
		_cbModel.Top = 0;
		_cbModel.Left = lblModel.Right + 4;
		_lblModelShare.AutoSize = true;
		_lblModelShare.Top = 2;
		_lblModelShare.Left = _cbModel.Right + 6;
		_lblModelShare.ForeColor = Theme.ToolSummary;
		modelRow.Controls.AddRange(new Control[] { lblModel, _cbModel, _lblModelShare });
		rightPanel.Controls.Add(modelRow, 0, 1);

		_lvRank.Dock = DockStyle.Fill;
		_lvRank.View = View.Details;
		_lvRank.FullRowSelect = true;
		_lvRank.GridLines = true;
		_lvRank.Columns.Add("排名", 36);
		_lvRank.Columns.Add("不良项名称", 180);
		_lvRank.Columns.Add("出现次数", 54);
		_lvRank.Columns.Add("受影响产品", 64);
		_lvRank.Columns.Add("占比%", 48);
		_lvRank.Columns.Add("测量值", 70);
		_lvRank.Columns.Add("规格", 60);
		_lvRank.Columns.Add("单位", 40);
		rightPanel.Controls.Add(_lvRank, 0, 2);

		split.Panel2.Controls.Add(rightPanel);

		var logRow = new Panel { Dock = DockStyle.Fill };
		_txtLog.Multiline = true;
		_txtLog.ReadOnly = true;
		_txtLog.ScrollBars = ScrollBars.Vertical;
		_txtLog.Dock = DockStyle.Fill;
		var logLabel = new Label { Text = "日志:", AutoSize = true, Dock = DockStyle.Top };
		logRow.Controls.Add(_txtLog);
		logRow.Controls.Add(logLabel);
		logLabel.BringToFront();

		container.Controls.Add(split, 0, 2);
		container.Controls.Add(logRow, 0, 3);

		Controls.Add(container);
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

	private void TryLoadIcon()
	{
		try
		{
			AppIcon.Apply(this);
			string text = Path.Combine(AppContext.BaseDirectory, "app_icon.ico");
			if (File.Exists(text))
			{
				Icon = new Icon(text);
			}
		}
		catch { }
	}

	private string LoadDefaultRoot()
	{
		try
		{
			string path = Path.Combine(AppContext.BaseDirectory, "config.json");
			if (File.Exists(path))
			{
				using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(path));
				if (jsonDocument.RootElement.TryGetProperty("results_root", out var value))
				{
					return value.GetString() ?? "D:\\Results";
				}
			}
		}
		catch { }
		return "D:\\Results";
	}

	private void BrowseFolder()
	{
		using FolderBrowserDialog folderBrowserDialog = new()
		{
			Description = "选择测试结果根目录 (含 Online/Offline)"
		};
		if (Directory.Exists(_txtRoot.Text))
		{
			folderBrowserDialog.SelectedPath = _txtRoot.Text;
		}
		if (folderBrowserDialog.ShowDialog() == DialogResult.OK)
		{
			_txtRoot.Text = folderBrowserDialog.SelectedPath;
		}
	}

	private void Log(string msg)
	{
		string msg2 = msg;
		if (InvokeRequired)
		{
			BeginInvoke(delegate { Log(msg2); });
			return;
		}
		_txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg2}\r\n");
	}

	private async Task ScanAsync()
	{
		string root = _txtRoot.Text.Trim();
		DateTime start = _dtStart.Value.Date;
		DateTime end = _dtEnd.Value.Date;
		if (start > end)
		{
			MessageBox.Show("起始日期不能晚于结束日期");
			return;
		}
		if (!Directory.Exists(root))
		{
			MessageBox.Show("结果目录不存在: " + root);
			return;
		}
		_btnScan.Enabled = false;
		_btnExport.Enabled = false;
		_btnExportRank.Enabled = false;
		_btnExportXlsx.Enabled = false;
		_txtLog.Clear();
		_lvRank.Items.Clear();
		_progress.Value = 0;
		Log("开始扫描: " + root);
		Log($"时间段: {start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}");
		try
		{
			RenderFolderTree(root);
			await Task.Run(delegate
			{
				_records = XmlScanner.Scan(root, start, end, Log, delegate(int done, int total)
				{
					if (InvokeRequired)
					{
						BeginInvoke(delegate
						{
							_progress.Maximum = Math.Max(total, 1);
							_progress.Value = Math.Min(done, _progress.Maximum);
						});
					}
				});
			});
			(CsvExporter.Summary summary, List<CsvExporter.FailRank> ranks) = CsvExporter.Aggregate(_records);
			_summary = summary;
			_ranks = ranks;
			_byModel = CsvExporter.AggregateByModel(_records);
			PopulateModelCombo();
			RenderResults();
			_btnExport.Enabled = _records.Count > 0;
			_btnExportRank.Enabled = _ranks.Count > 0;
			_btnExportXlsx.Enabled = _records.Count > 0;
			Log($"统计完成: 记录 {summary.Total}, 不良项种类 {ranks.Count}");
		}
		catch (Exception ex)
		{
			Log("[异常] " + ex.Message);
			MessageBox.Show("扫描出错: " + ex.Message);
		}
		finally
		{
			_btnScan.Enabled = true;
		}
	}

	private void RenderResults()
	{
		if (_summary != null)
		{
			_lblSummary.Text = $"总记录 {_summary.Total}  |  产品 {_summary.DistinctSn}  |  PASS {_summary.Pass}  FAIL {_summary.Fail}  中断 {_summary.Interrupted}  |  良率 {_summary.Yield:F2}%  |  不良项累计 {_summary.TotalFailOccurrences}";
			RenderRankForSelectedModel();
		}
	}

	private void PopulateModelCombo()
	{
		_cbModel.SelectedIndexChanged -= OnModelChanged;
		_cbModel.Items.Clear();
		_cbModel.Items.Add("全部型号");
		int total = _summary?.TotalFailOccurrences ?? 0;
		foreach (CsvExporter.GroupResult item in _byModel)
		{
			double pct = total > 0 ? (double)item.Summary.TotalFailOccurrences / total * 100.0 : 0.0;
			_cbModel.Items.Add($"{item.Key}  ({pct:F1}%)");
		}
		_cbModel.SelectedIndex = 0;
		_cbModel.SelectedIndexChanged += OnModelChanged;
	}

	private void OnModelChanged(object? sender, EventArgs e)
	{
		RenderRankForSelectedModel();
	}

	private void RenderRankForSelectedModel()
	{
		if (_summary == null) return;
		int selectedIndex = _cbModel.SelectedIndex;
		int totalFailOccurrences = _summary.TotalFailOccurrences;
		List<CsvExporter.FailRank> ranks;
		if (selectedIndex <= 0)
		{
			ranks = _ranks;
			_lblModelShare.Text = $"全部型号  不良累计 {totalFailOccurrences} 次 (100%)";
		}
		else
		{
			CsvExporter.GroupResult gr = _byModel[selectedIndex - 1];
			ranks = gr.Ranks;
			int grTotal = gr.Summary.TotalFailOccurrences;
			double pct = totalFailOccurrences > 0 ? (double)grTotal / totalFailOccurrences * 100.0 : 0.0;
			_lblModelShare.Text = $"{gr.Key}  不良 {grTotal} 次  占总不良 {pct:F1}%  |  FAIL {gr.Summary.Fail} 台  良率 {gr.Summary.Yield:F2}%";
		}
		_lvRank.BeginUpdate();
		_lvRank.Items.Clear();
		int num = 1;
		foreach (CsvExporter.FailRank item in ranks)
		{
			ListViewItem lvi = new(num.ToString())
			{
				SubItems = { item.Item, item.Count.ToString(), item.AffectedUnits.ToString(), $"{item.Percent:F2}", item.Values, item.Limits, item.Units }
			};
			if (num <= 3)
			{
				lvi.BackColor = Theme.ToolHighlight;
			}
			_lvRank.Items.Add(lvi);
			num++;
		}
		_lvRank.EndUpdate();
	}

	private void RenderFolderTree(string root)
	{
		_tvSurvey.BeginUpdate();
		_tvSurvey.Nodes.Clear();
		try
		{
			if (!Directory.Exists(root))
			{
				_lblSurvey.Text = "目录不存在: " + root;
				_tvSurvey.EndUpdate();
				return;
			}
			TreeNode treeNode = new(root)
			{
				Tag = new FsTag { IsDir = true, Path = root },
				NodeFont = new Font(_tvSurvey.Font, FontStyle.Bold),
				ForeColor = Theme.ToolNodeDir,
			};
			_tvSurvey.Nodes.Add(treeNode);
			PopulateChildren(treeNode);
			treeNode.Expand();
			_lblSurvey.Text = "文件夹结构 (双击展开/收起，双击 XML 看内容，双击其它文件默认打开)";
		}
		catch (Exception ex)
		{
			_lblSurvey.Text = "读取目录失败: " + ex.Message;
		}
		finally
		{
			_tvSurvey.EndUpdate();
		}
	}

	private void PopulateChildren(TreeNode dirNode)
	{
		if (dirNode.Tag is not FsTag { IsDir: true, Loaded: false } fsTag) return;
		fsTag.Loaded = true;
		dirNode.Nodes.Clear();
		try
		{
			foreach (string item in Directory.EnumerateDirectories(fsTag.Path).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
			{
				TreeNode treeNode = new(Path.GetFileName(item))
				{
					Tag = new FsTag { IsDir = true, Path = item },
					ForeColor = Theme.ToolNodeDir,
				};
				if (DirHasChildren(item))
				{
					treeNode.Nodes.Add(new TreeNode("加载中...") { Tag = null });
				}
				dirNode.Nodes.Add(treeNode);
			}
			foreach (string item2 in Directory.EnumerateFiles(fsTag.Path).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
			{
				string fileName = Path.GetFileName(item2);
				bool isXml = fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
				TreeNode node = new(fileName)
				{
					Tag = new FsTag { IsDir = false, Path = item2 },
					ForeColor = isXml ? Theme.ToolNodeFile : Theme.ToolGray,
				};
				dirNode.Nodes.Add(node);
			}
		}
		catch (Exception ex)
		{
			dirNode.Nodes.Add(new TreeNode("[无法读取: " + ex.Message + "]") { Tag = null });
		}
	}

	private static bool DirHasChildren(string dir)
	{
		try
		{
			using IEnumerator<string> enumerator = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
			return enumerator.MoveNext();
		}
		catch { return false; }
	}

	private void OnSurveyBeforeExpand(object? sender, TreeViewCancelEventArgs e)
	{
		if (e.Node?.Tag is FsTag { IsDir: true, Loaded: false })
		{
			PopulateChildren(e.Node);
		}
	}

	private void ShowXmlContent(string path)
	{
		if (!File.Exists(path))
		{
			MessageBox.Show("文件不存在:\n" + path, "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
			return;
		}
		try
		{
			using XmlViewerForm xmlViewerForm = new(path);
			xmlViewerForm.ShowDialog(TopLevelControl ?? this);
		}
		catch (Exception ex)
		{
			MessageBox.Show("打开 XML 失败: " + ex.Message);
			Log("[查看XML失败] " + ex.Message);
		}
	}

	private void ExportCsv(bool rankOnly)
	{
		if (_summary == null || _records.Count == 0) return;
		DateTime date = _dtStart.Value.Date;
		DateTime date2 = _dtEnd.Value.Date;
		string fileName = rankOnly
			? $"FCT不良排名表_{date:yyyyMMdd}-{date2:yyyyMMdd}.csv"
			: $"FCT不良完整报表_{date:yyyyMMdd}-{date2:yyyyMMdd}.csv";
		using SaveFileDialog saveFileDialog = new()
		{
			Filter = "CSV 文件 (*.csv)|*.csv",
			FileName = fileName
		};
		if (saveFileDialog.ShowDialog() != DialogResult.OK) return;
		try
		{
			if (rankOnly)
				CsvExporter.ExportRankOnly(saveFileDialog.FileName, date, date2, _summary, _ranks);
			else
				CsvExporter.Export(saveFileDialog.FileName, date, date2, _records, _summary, _ranks);
			Log("已导出: " + saveFileDialog.FileName);
			if (MessageBox.Show("导出成功！是否打开文件?", "完成", MessageBoxButtons.YesNo, MessageBoxIcon.Asterisk) == DialogResult.Yes)
			{
				Process.Start(new ProcessStartInfo { FileName = saveFileDialog.FileName, UseShellExecute = true });
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("导出失败: " + ex.Message);
			Log("[导出失败] " + ex.Message);
		}
	}

	private void ExportXlsx()
	{
		if (_summary == null || _records.Count == 0) return;
		DateTime date = _dtStart.Value.Date;
		DateTime date2 = _dtEnd.Value.Date;
		using SaveFileDialog saveFileDialog = new()
		{
			Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
			FileName = $"FCT不良排名_{date:yyyyMMdd}-{date2:yyyyMMdd}.xlsx"
		};
		if (saveFileDialog.ShowDialog() != DialogResult.OK) return;
		try
		{
			XlsxExporter.Export(saveFileDialog.FileName, date, date2, _records, _summary, _ranks);
			Log("已导出 Excel: " + saveFileDialog.FileName);
			if (MessageBox.Show("导出成功！(含 总排名/各型号/各机台/明细 多个 Sheet)\n是否打开文件?", "完成", MessageBoxButtons.YesNo, MessageBoxIcon.Asterisk) == DialogResult.Yes)
			{
				Process.Start(new ProcessStartInfo { FileName = saveFileDialog.FileName, UseShellExecute = true });
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("导出失败: " + ex.Message);
			Log("[Excel导出失败] " + ex.Message);
		}
	}
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using FctAggregator;

namespace FctTdmsViewer;

public sealed class MainForm : Form
{
	private TdmsDoc? _doc;

	private readonly ToolStrip _tool = new ToolStrip();

	private readonly ToolStripButton _btnOpen = new ToolStripButton("打开 TDMS");

	private readonly ToolStripButton _btnExportSel = new ToolStripButton("导出选中通道");

	private readonly ToolStripButton _btnExportSum = new ToolStripButton("导出结构清单");

	private readonly ToolStripButton _btnClearSel = new ToolStripButton("清空勾选");

	private readonly ToolStripComboBox _cboOrder = new ToolStripComboBox();

	private readonly ToolStripLabel _lblFile = new ToolStripLabel("未打开文件");

	private readonly ToolStripButton _btnCollapseTree = new ToolStripButton("隐藏通道树");

	private readonly ToolStripButton _btnCollapseBottom = new ToolStripButton("收起数据/属性");

	private readonly SplitContainer _split = new SplitContainer();

	private readonly SplitContainer _rsplit = new SplitContainer();

	private int _savedBottomDist = 400;

	private readonly TextBox _search = new TextBox();

	private readonly TreeView _tree = new TreeView();

	private readonly WaveformPanel _wave = new WaveformPanel();

	private readonly DataGridView _grid = new DataGridView();

	private readonly ListView _props = new ListView();

	private readonly Label _stat = new Label();

	private readonly TabControl _tabs = new TabControl();

	private readonly StatusStrip _status = new StatusStrip();

	private readonly ToolStripStatusLabel _statusText = new ToolStripStatusLabel("就绪");

	private readonly List<ChannelInfo> _checked = new List<ChannelInfo>();

	private ChannelInfo? _current;

	private bool _suppress;

	private const int MaxSeries = 8;

	private const int MaxGridRows = 5000;

	public MainForm()
	{
		Text = "FCT-TdmsViewer  v1.1.0";
		base.Width = 1280;
		base.Height = 820;
		MinimumSize = new Size(1000, 640);
		base.StartPosition = FormStartPosition.CenterScreen;
		Font = Theme.Body;
		AllowDrop = true;
		LoadAppIcon();
		_tool.GripStyle = ToolStripGripStyle.Hidden;
		_cboOrder.DropDownStyle = ComboBoxStyle.DropDownList;
		_cboOrder.Items.AddRange(new object[2] { "按测试项编号排序", "按文件内原始顺序" });
		_cboOrder.SelectedIndex = 0;
		_cboOrder.Width = 150;
		_cboOrder.SelectedIndexChanged += delegate
		{
			if (_doc != null)
			{
				_doc.SortGroups((_cboOrder.SelectedIndex != 0) ? TdmsDoc.GroupOrder.FileOrder : TdmsDoc.GroupOrder.ByNumber);
				RebuildTree(_search.Text.Trim());
			}
		};
		_tool.Items.AddRange(new ToolStripItem[14]
		{
			_btnOpen,
			new ToolStripSeparator(),
			_btnExportSel,
			_btnExportSum,
			new ToolStripSeparator(),
			_btnClearSel,
			new ToolStripSeparator(),
			new ToolStripLabel("排序:"),
			_cboOrder,
			new ToolStripSeparator(),
			_btnCollapseTree,
			_btnCollapseBottom,
			new ToolStripSeparator(),
			_lblFile
		});
		_btnOpen.Click += delegate
		{
			PickFile();
		};
		_btnExportSel.Click += delegate
		{
			ExportSelected();
		};
		_btnExportSum.Click += delegate
		{
			ExportSummary();
		};
		_btnClearSel.Click += delegate
		{
			ClearChecks();
		};
		_btnCollapseTree.Click += delegate
		{
			ToggleTree();
		};
		_btnCollapseBottom.Click += delegate
		{
			ToggleBottom();
		};
		base.Controls.Add(_tool);
		_split.Orientation = Orientation.Vertical;
		_split.Dock = DockStyle.Fill;
		_split.FixedPanel = FixedPanel.Panel1;
		_split.Panel1Collapsed = false;
		base.Controls.Add(_split);
		_split.BringToFront();
		SplitContainer split = _split;
		Panel panel = new Panel
		{
			Dock = DockStyle.Fill
		};
		_search.Dock = DockStyle.Top;
		_search.PlaceholderText = "搜索组名或通道名（即时过滤，空格分隔多关键词）";
		_search.TextChanged += delegate
		{
			RebuildTree(_search.Text.Trim());
		};
		panel.Controls.Add(_search);
		_tree.Dock = DockStyle.Fill;
		_tree.CheckBoxes = true;
		_tree.HideSelection = false;
		_tree.AfterCheck += OnAfterCheck;
		_tree.AfterSelect += OnAfterSelect;
		panel.Controls.Add(_tree);
		_tree.BringToFront();
		split.Panel1.Controls.Add(panel);
		_rsplit.Dock = DockStyle.Fill;
		_rsplit.Orientation = Orientation.Horizontal;
		SplitContainer rsplit = _rsplit;
		split.Panel2.Controls.Add(rsplit);
		_wave.Dock = DockStyle.Fill;
		rsplit.Panel1.Controls.Add(_wave);
		var bottomHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(0) };
		_stat.Dock = DockStyle.Top;
		_stat.Height = 24;
		_stat.TextAlign = ContentAlignment.MiddleLeft;
		_stat.Font = Theme.Mono;
		_stat.ForeColor = Theme.ToolSummary;
		_stat.BackColor = Theme.Surface;
		bottomHost.Controls.Add(_stat);
		_tabs.Dock = DockStyle.Fill;
		bottomHost.Controls.Add(_tabs);
		rsplit.Panel2.Controls.Add(bottomHost);
		TabPage tabPage = new TabPage("数据");
		_grid.Dock = DockStyle.Fill;
		_grid.ReadOnly = true;
		_grid.AllowUserToAddRows = false;
		_grid.RowHeadersVisible = false;
		_grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
		_grid.EnableHeadersVisualStyles = false;
		_grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.ToolDarkBg;
		_grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.ToolDarkFg;
		_grid.ColumnHeadersDefaultCellStyle.Font = Theme.BodyBold;
		_grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.ToolAltRow;
		_grid.DefaultCellStyle.Font = new Font("Consolas", 9f);
		tabPage.Controls.Add(_grid);
		_tabs.TabPages.Add(tabPage);
		TabPage tabPage2 = new TabPage("属性");
		_props.Dock = DockStyle.Fill;
		_props.View = View.Details;
		_props.FullRowSelect = true;
		_props.GridLines = true;
		_props.Columns.Add("属性", 240);
		_props.Columns.Add("值", 600);
		tabPage2.Controls.Add(_props);
		_tabs.TabPages.Add(tabPage2);
		_status.Items.Add(_statusText);
		base.Controls.Add(_status);
		base.DragEnter += delegate(object? _, DragEventArgs e)
		{
			IDataObject? data = e.Data;
			if (data != null && data.GetDataPresent(DataFormats.FileDrop))
			{
				e.Effect = DragDropEffects.Copy;
			}
		};
		base.DragDrop += delegate(object? _, DragEventArgs e)
		{
			if (e.Data?.GetData(DataFormats.FileDrop) is string[] array && array.Length != 0)
			{
				LoadFile(array[0]);
			}
		};
		base.Load += delegate
		{
			_rsplit.Panel2MinSize = 180;
			_split.SplitterDistance = 380;
			_rsplit.SplitterDistance = 470;
		};
		UpdateButtons();
	}

	public void LoadFile(string path)
	{
		if (!File.Exists(path))
		{
			MessageBox.Show("文件不存在:\n" + path, "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
			return;
		}
		try
		{
			Cursor = Cursors.WaitCursor;
			_doc?.Dispose();
			_checked.Clear();
			_wave.Clear();
			_grid.Columns.Clear();
			_grid.Rows.Clear();
			_props.Items.Clear();
			_stat.Text = "";
			Stopwatch stopwatch = Stopwatch.StartNew();
			_doc = TdmsDoc.Load(path);
			stopwatch.Stop();
			_current = null;
			_search.Text = "";
			RebuildTree("");
			_lblFile.Text = Path.GetFileName(path);
			_statusText.Text = $"{_doc.Groups.Count} 组 / {_doc.TotalChannels} 通道 / {Human(_doc.FileBytes)} / 读元信息 {stopwatch.ElapsedMilliseconds} ms";
			Text = "FCT-TdmsViewer  v1.1.0  —  " + Path.GetFileName(path);
			SelectFirstNumericChannel();
		}
		catch (Exception ex)
		{
			MessageBox.Show(ex.GetType().Name + ": " + ex.Message, "解析失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			_statusText.Text = "解析失败";
		}
		finally
		{
			Cursor = Cursors.Default;
			UpdateButtons();
		}
	}

	private void LoadAppIcon()
	{
		try
		{
			Assembly assembly = typeof(MainForm).Assembly;
			string? text = assembly.GetManifestResourceNames().FirstOrDefault((string n) => n.EndsWith("app_icon.ico", StringComparison.OrdinalIgnoreCase));
			if (text == null)
			{
				return;
			}
			using Stream? stream = assembly.GetManifestResourceStream(text);
			if (stream != null)
			{
				base.Icon = new Icon(stream);
			}
		}
		catch
		{
		}
	}

	private void PickFile()
	{
		using OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Filter = "TDMS 文件 (*.tdms)|*.tdms|所有文件 (*.*)|*.*",
			Title = "打开 TDMS 文件"
		};
		if (openFileDialog.ShowDialog() == DialogResult.OK)
		{
			LoadFile(openFileDialog.FileName);
		}
	}

	private void RebuildTree(string filter)
	{
		if (_doc == null)
		{
			return;
		}
		string[] keys = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		bool flag = keys.Length != 0;
		_tree.BeginUpdate();
		_tree.Nodes.Clear();
		int num = 0;
		foreach (GroupInfo group in _doc.Groups)
		{
			List<ChannelInfo> list = ((!flag) ? group.Channels : ((!Hit(group.Name)) ? group.Channels.Where((ChannelInfo c) => Hit(c.Name)).ToList() : group.Channels));
			if (list.Count == 0)
			{
				continue;
			}
			num += list.Count;
			TreeNode treeNode = new TreeNode($"{group.Seq:00}  {group.Name}   [{group.Channels.Count} 通道 · {group.SampleCount} 点]")
			{
				Tag = group
			};
			foreach (ChannelInfo item in list)
			{
				int value = group.Channels.IndexOf(item) + 1;
				TreeNode treeNode2 = new TreeNode(item.Numeric ? $"{value:000}  {item.Name}   ({item.TypeName}, {item.Count})" : $"{value:000}  {item.Name}   ({item.TypeName}, {item.Count}) [非数值]")
				{
					Tag = item
				};
				if (_checked.Contains(item))
				{
					treeNode2.Checked = true;
				}
				if (!item.Numeric)
				{
					treeNode2.ForeColor = Theme.ToolGray;
				}
				treeNode.Nodes.Add(treeNode2);
			}
			_tree.Nodes.Add(treeNode);
			if (flag)
			{
				treeNode.Expand();
			}
		}
		_tree.EndUpdate();
		if (flag)
		{
			_statusText.Text = $"过滤 \"{filter}\": {_tree.Nodes.Count} 组 / {num} 通道命中";
		}
		bool Hit(string s)
		{
			string s2 = s;
			return keys.All((string k) => s2.Contains(k, StringComparison.OrdinalIgnoreCase));
		}
	}

	private void SelectFirstNumericChannel()
	{
		foreach (TreeNode node in _tree.Nodes)
		{
			foreach (TreeNode node2 in node.Nodes)
			{
				if (node2.Tag is ChannelInfo { Numeric: not false } channelInfo && channelInfo.Count > 0)
				{
					node.Expand();
					_tree.SelectedNode = node2;
					node2.EnsureVisible();
					return;
				}
			}
		}
		if (_tree.Nodes.Count > 0)
		{
			_tree.SelectedNode = _tree.Nodes[0];
		}
	}

	private void OnAfterCheck(object? sender, TreeViewEventArgs e)
	{
		if (_suppress || e.Node == null)
		{
			return;
		}
		if (e.Node.Tag is GroupInfo)
		{
			_suppress = true;
			foreach (TreeNode node in e.Node.Nodes)
			{
				node.Checked = e.Node.Checked;
				SyncChecked(node);
			}
			_suppress = false;
		}
		else
		{
			SyncChecked(e.Node);
		}
		RefreshWave();
	}

	private void SyncChecked(TreeNode node)
	{
		if (!(node.Tag is ChannelInfo channelInfo))
		{
			return;
		}
		if (node.Checked)
		{
			if (!channelInfo.Numeric)
			{
				node.Checked = false;
			}
			else if (!_checked.Contains(channelInfo))
			{
				_checked.Add(channelInfo);
			}
		}
		else
		{
			_checked.Remove(channelInfo);
		}
	}

	private void ClearChecks()
	{
		_suppress = true;
		foreach (TreeNode node in _tree.Nodes)
		{
			node.Checked = false;
			foreach (TreeNode node2 in node.Nodes)
			{
				node2.Checked = false;
			}
		}
		_suppress = false;
		_checked.Clear();
		RefreshWave();
		UpdateButtons();
	}

	private void ToggleTree()
	{
		if (_split.Panel1Collapsed)
		{
			_split.Panel1Collapsed = false;
			int max = Math.Max(_split.Panel1MinSize, _split.Width - _split.Panel2MinSize);
			_split.SplitterDistance = Math.Clamp(380, _split.Panel1MinSize, max);
			_btnCollapseTree.Text = "隐藏通道树";
		}
		else
		{
			_split.Panel1Collapsed = true;
			_btnCollapseTree.Text = "显示通道树";
		}
	}

	private void ToggleBottom()
	{
		if (_rsplit.Panel2Collapsed)
		{
			_rsplit.Panel2Collapsed = false;
			int max = Math.Max(_rsplit.Panel1MinSize, _rsplit.Height - _rsplit.Panel2MinSize);
			_rsplit.SplitterDistance = Math.Clamp(_savedBottomDist, _rsplit.Panel1MinSize, max);
			_btnCollapseBottom.Text = "收起数据/属性";
		}
		else
		{
			_savedBottomDist = Math.Max(_rsplit.Panel1MinSize, _rsplit.SplitterDistance);
			_rsplit.Panel2Collapsed = true;
			_btnCollapseBottom.Text = "展开数据/属性";
		}
	}

	private void RefreshWave()
	{
		if (_doc == null)
		{
			UpdateButtons();
			return;
		}
		List<ChannelInfo> list = new List<ChannelInfo>(_checked);
		if (_current != null && _current.Numeric && !list.Contains(_current))
		{
			list.Insert(0, _current);
		}
		if (list.Count == 0)
		{
			_wave.Clear();
			_stat.Text = "";
			UpdateButtons();
			return;
		}
		if (list.Count > 8)
		{
			_statusText.Text = $"共 {list.Count} 条，波形仅显示前 {8} 条（导出不受限制）";
		}
		List<(string, double[], double)> series = (from c in list.Take(8)
			select (name: c.Name, data: _doc.GetData(c), inc: TdmsDoc.GetIncrement(c))).ToList();
		_wave.SetSeries(series);
		if (list.Count == 1)
		{
			TdmsDoc.Stat? stat = TdmsDoc.Describe(_doc.GetData(list[0]));
			_stat.Text = ((stat == null) ? (list[0].Name + ": 无数据") : $"{list[0].Name}   n={stat.N}   min={stat.Min:G6}   max={stat.Max:G6}   mean={stat.Mean:G6}   std={stat.Std:G6}   首={stat.First:G6}   末={stat.Last:G6}");
		}
		else
		{
			_stat.Text = $"叠加显示 {Math.Min(list.Count, 8)} 条" + ((_checked.Count > 0) ? $"（已勾选 {_checked.Count}）" : "");
		}
		UpdateButtons();
	}

	private void OnAfterSelect(object? sender, TreeViewEventArgs e)
	{
		if (_doc == null || e.Node == null)
		{
			return;
		}
		_props.BeginUpdate();
		_props.Items.Clear();
		if (e.Node.Tag is GroupInfo groupInfo)
		{
			_current = null;
			foreach (KeyValuePair<string, object> property in groupInfo.Properties)
			{
				_props.Items.Add(new ListViewItem(new string[2]
				{
					property.Key,
					property.Value?.ToString() ?? ""
				}));
			}
			ShowGroupTable(groupInfo);
		}
		else if (e.Node.Tag is ChannelInfo channelInfo)
		{
			_current = channelInfo;
			_props.Items.Add(new ListViewItem(new string[2] { "(Group)", channelInfo.GroupName }));
			_props.Items.Add(new ListViewItem(new string[2] { "(Channel)", channelInfo.Name }));
			_props.Items.Add(new ListViewItem(new string[2] { "(DataType)", channelInfo.TypeName }));
			_props.Items.Add(new ListViewItem(new string[2]
			{
				"(Count)",
				channelInfo.Count.ToString()
			}));
			foreach (KeyValuePair<string, object> property2 in channelInfo.Properties)
			{
				_props.Items.Add(new ListViewItem(new string[2]
				{
					property2.Key,
					property2.Value?.ToString() ?? ""
				}));
			}
			ShowChannelTable(channelInfo);
		}
		_props.EndUpdate();
		RefreshWave();
	}

	private void ShowChannelTable(ChannelInfo c)
	{
		if (_doc == null)
		{
			return;
		}
		_grid.SuspendLayout();
		_grid.Columns.Clear();
		_grid.Rows.Clear();
		_grid.Columns.Add("idx", "#");
		_grid.Columns["idx"].Width = 70;
		double increment = TdmsDoc.GetIncrement(c);
		if (increment > 0.0)
		{
			_grid.Columns.Add("t", "Time(s)");
			_grid.Columns["t"].Width = 90;
		}
		_grid.Columns.Add("v", c.Name);
		_grid.Columns["v"].Width = 200;
		if (c.Numeric)
		{
			double[] data = _doc.GetData(c);
			int num = Math.Min(data.Length, 5000);
			List<DataGridViewRow> list = new List<DataGridViewRow>(num);
			for (int i = 0; i < num; i++)
			{
				DataGridViewRow dataGridViewRow = new DataGridViewRow();
				dataGridViewRow.CreateCells(_grid);
				int index = 0;
				dataGridViewRow.Cells[index++].Value = i;
				if (increment > 0.0)
				{
					dataGridViewRow.Cells[index++].Value = ((double)i * increment).ToString("0.####");
				}
				dataGridViewRow.Cells[index].Value = data[i].ToString("G8");
				list.Add(dataGridViewRow);
			}
			_grid.Rows.AddRange(list.ToArray());
			_statusText.Text = ((data.Length > 5000) ? $"{c.Name}: 共 {data.Length} 点，表格仅显示前 {5000} 行（波形与导出为全量）" : $"{c.Name}: {data.Length} 点");
		}
		else
		{
			string[] text = _doc.GetText(c);
			int num2 = Math.Min(text.Length, 5000);
			for (int j = 0; j < num2; j++)
			{
				int index2 = _grid.Rows.Add();
				DataGridViewRow dataGridViewRow2 = _grid.Rows[index2];
				int index3 = 0;
				dataGridViewRow2.Cells[index3++].Value = j;
				if (increment > 0.0)
				{
					dataGridViewRow2.Cells[index3++].Value = ((double)j * increment).ToString("0.####");
				}
				dataGridViewRow2.Cells[index3].Value = text[j];
			}
			_statusText.Text = $"{c.Name}: 非数值类型 ({c.TypeName})，{text.Length} 条";
		}
		_grid.ResumeLayout();
	}

	private void ShowGroupTable(GroupInfo g)
	{
		if (_doc == null)
		{
			return;
		}
		_grid.SuspendLayout();
		_grid.Columns.Clear();
		_grid.Rows.Clear();
		(string, int)[] array = new(string, int)[7]
		{
			("通道", 260),
			("类型", 80),
			("点数", 70),
			("最小", 110),
			("最大", 110),
			("均值", 110),
			("末值", 110)
		};
		for (int i = 0; i < array.Length; i++)
		{
			(string, int) tuple = array[i];
			string item = tuple.Item1;
			int item2 = tuple.Item2;
			int index = _grid.Columns.Add(item, item);
			_grid.Columns[index].Width = item2;
		}
		List<DataGridViewRow> list = new List<DataGridViewRow>(g.Channels.Count);
		foreach (ChannelInfo channel in g.Channels)
		{
			DataGridViewRow dataGridViewRow = new DataGridViewRow();
			dataGridViewRow.CreateCells(_grid);
			dataGridViewRow.Cells[0].Value = channel.Name;
			dataGridViewRow.Cells[1].Value = channel.TypeName;
			dataGridViewRow.Cells[2].Value = channel.Count;
			TdmsDoc.Stat? stat = (channel.Numeric ? TdmsDoc.Describe(_doc.GetData(channel)) : null);
			if (stat != null)
			{
				dataGridViewRow.Cells[3].Value = stat.Min.ToString("G6");
				dataGridViewRow.Cells[4].Value = stat.Max.ToString("G6");
				dataGridViewRow.Cells[5].Value = stat.Mean.ToString("G6");
				dataGridViewRow.Cells[6].Value = stat.Last.ToString("G6");
			}
			list.Add(dataGridViewRow);
		}
		_grid.Rows.AddRange(list.ToArray());
		_grid.ResumeLayout();
		_statusText.Text = $"{g.Name}: {g.Channels.Count} 通道 · {g.SampleCount} 点";
	}

	private void ExportSelected()
	{
		if (_doc == null)
		{
			return;
		}
		List<ChannelInfo> list = ((_checked.Count > 0) ? new List<ChannelInfo>(_checked) : ((_current != null && _current.Numeric) ? new List<ChannelInfo> { _current } : new List<ChannelInfo>()));
		if (list.Count == 0)
		{
			return;
		}
		using SaveFileDialog saveFileDialog = new SaveFileDialog
		{
			Filter = "CSV 文件 (*.csv)|*.csv",
			FileName = $"{Path.GetFileNameWithoutExtension(_doc.Path)}_{list.Count}ch.csv"
		};
		if (saveFileDialog.ShowDialog() != DialogResult.OK)
		{
			return;
		}
		try
		{
			Cursor = Cursors.WaitCursor;
			Exporter.ExportChannels(_doc, list, saveFileDialog.FileName);
			_statusText.Text = $"已导出 {list.Count} 个通道 -> {saveFileDialog.FileName}";
			if (MessageBox.Show("导出完成，是否打开所在文件夹？", "完成", MessageBoxButtons.YesNo, MessageBoxIcon.Asterisk) == DialogResult.Yes)
			{
				Process.Start("explorer.exe", "/select,\"" + saveFileDialog.FileName + "\"");
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show(ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
		finally
		{
			Cursor = Cursors.Default;
		}
	}

	private void ExportSummary()
	{
		if (_doc == null)
		{
			return;
		}
		using SaveFileDialog saveFileDialog = new SaveFileDialog
		{
			Filter = "CSV 文件 (*.csv)|*.csv",
			FileName = Path.GetFileNameWithoutExtension(_doc.Path) + "_summary.csv"
		};
		if (saveFileDialog.ShowDialog() != DialogResult.OK)
		{
			return;
		}
		try
		{
			Cursor = Cursors.WaitCursor;
			Exporter.ExportSummary(_doc, saveFileDialog.FileName);
			_statusText.Text = "已导出结构清单 -> " + saveFileDialog.FileName;
			if (MessageBox.Show("导出完成，是否打开所在文件夹？", "完成", MessageBoxButtons.YesNo, MessageBoxIcon.Asterisk) == DialogResult.Yes)
			{
				Process.Start("explorer.exe", "/select,\"" + saveFileDialog.FileName + "\"");
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show(ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
		finally
		{
			Cursor = Cursors.Default;
		}
	}

	private void UpdateButtons()
	{
		bool flag = _doc != null;
		_btnExportSum.Enabled = flag;
		int num = ((_checked.Count > 0) ? _checked.Count : ((_current != null && _current.Numeric) ? 1 : 0));
		_btnExportSel.Enabled = flag && num > 0;
		_btnClearSel.Enabled = flag && _checked.Count > 0;
		_btnExportSel.Text = ((num > 0) ? $"导出选中通道 ({num})" : "导出选中通道");
	}

	private static string Human(long b)
	{
		if (b < 1048576)
		{
			if (b >= 1024)
			{
				return $"{(double)b / 1024.0:F0} KB";
			}
			return $"{b} B";
		}
		return $"{(double)b / 1048576.0:F1} MB";
	}

	protected override void OnFormClosed(FormClosedEventArgs e)
	{
		_doc?.Dispose();
		base.OnFormClosed(e);
	}
}

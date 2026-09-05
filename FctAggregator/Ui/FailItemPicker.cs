using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FctAggregator.Parsing;

namespace FctAggregator;

public class FailItemPickerForm : Form
{
	private sealed class Stat
	{
		public string Item = "";

		public int Count;

		public string LastSeen = "";

		public readonly SortedSet<string> Models = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

		public readonly SortedSet<string> Stations = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

		public bool FromXml;
	}

	private readonly Engine _engine;

	private readonly Dictionary<string, Stat> _stats = new Dictionary<string, Stat>(StringComparer.OrdinalIgnoreCase);

	private readonly HashSet<string> _checked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private readonly ListView _list = new ListView();

	private readonly TextBox _filter = new TextBox();

	private readonly Label _status = new Label();

	private readonly Button _btnDeep = new Button();

	private readonly Button _btnOk = new Button();

	private readonly CheckBox _onlyThisStation = new CheckBox();

	private CancellationTokenSource? _deepCts;

	private bool _busy;

	private bool _binding;

	public List<string> SelectedItems { get; private set; } = new List<string>();

	private int ScanDays => _engine.Config.TodoScanDays;

	public FailItemPickerForm(Engine engine)
	{
		_engine = engine;
		Text = "选择故障项（来自 FAIL 记录，已去重）";
		base.Width = 880;
		base.Height = 620;
		MinimumSize = new Size(680, 460);
		base.StartPosition = FormStartPosition.CenterParent;
		Font = new Font("Microsoft YaHei UI", 9f);
		base.ShowInTaskbar = false;
		base.MinimizeBox = false;
		BuildUi();
		base.Shown += delegate
		{
			LoadFast();
		};
		base.FormClosing += delegate
		{
			_deepCts?.Cancel();
		};
	}

	private void BuildUi()
	{
		Label label = new Label
		{
			Left = 12,
			Top = 10,
			Width = 840,
			Height = 34,
			Text = "勾选一个或多个故障项 → 【确定】即可写入维修记录的「故障项目」。\r\n统计范围：近 " + ScanDays + " 天内的 FAIL（每条记录只取第一条失败项，已按故障项去重）；「深扫 XML」从源文件复核一遍。",
			ForeColor = Color.FromArgb(89, 89, 89),
			Anchor = (AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right)
		};
		Label label2 = new Label
		{
			Left = 12,
			Top = 50,
			Width = 44,
			Text = "过滤:",
			TextAlign = ContentAlignment.MiddleRight
		};
		_filter.Left = 60;
		_filter.Top = 47;
		_filter.Width = 220;
		_filter.PlaceholderText = "输入测试项关键字";
		_filter.TextChanged += delegate
		{
			Rebind();
		};
		_onlyThisStation.Left = 292;
		_onlyThisStation.Top = 50;
		_onlyThisStation.Width = 150;
		_onlyThisStation.Text = "只看本机台";
		_onlyThisStation.Checked = true;
		_onlyThisStation.CheckedChanged += delegate
		{
			if (!_busy)
			{
				LoadFast();
			}
		};
		_btnDeep.Left = 452;
		_btnDeep.Top = 46;
		_btnDeep.Width = 132;
		_btnDeep.Height = 26;
		_btnDeep.Text = "深扫 XML(更全)";
		_btnDeep.Click += delegate
		{
			if (_busy)
			{
				_deepCts?.Cancel();
			}
			else
			{
				StartDeepScan();
			}
		};
		Button button = new Button
		{
			Left = 592,
			Top = 46,
			Width = 62,
			Height = 26,
			Text = "全选"
		};
		button.Click += delegate
		{
			SetFilteredChecked(on: true);
		};
		Button button2 = new Button
		{
			Left = 660,
			Top = 46,
			Width = 62,
			Height = 26,
			Text = "清空"
		};
		button2.Click += delegate
		{
			SetFilteredChecked(on: false);
		};
		_list.Left = 12;
		_list.Top = 80;
		_list.Width = 840;
		_list.Height = 448;
		_list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		_list.View = View.Details;
		_list.CheckBoxes = true;
		_list.FullRowSelect = true;
		_list.GridLines = true;
		_list.HideSelection = false;
		_list.Columns.Add("故障项目（测试项）", 420);
		_list.Columns.Add("出现次数", 80, HorizontalAlignment.Center);
		_list.Columns.Add("最近出现", 130, HorizontalAlignment.Center);
		_list.Columns.Add("型号", 110, HorizontalAlignment.Center);
		_list.Columns.Add("机台", 80, HorizontalAlignment.Center);
		_list.ItemChecked += delegate(object? _, ItemCheckedEventArgs e)
		{
			if (!_binding && e.Item.Tag is Stat stat)
			{
				if (e.Item.Checked)
				{
					_checked.Add(stat.Item);
				}
				else
				{
					_checked.Remove(stat.Item);
				}
				UpdateStatus();
			}
		};
		_list.MouseDoubleClick += delegate(object? _, MouseEventArgs e)
		{
			ListViewHitTestInfo listViewHitTestInfo = _list.HitTest(e.Location);
			if (listViewHitTestInfo.Item != null)
			{
				listViewHitTestInfo.Item.Checked = !listViewHitTestInfo.Item.Checked;
			}
		};
		_status.Left = 12;
		_status.Top = 536;
		_status.Width = 620;
		_status.Height = 32;
		_status.ForeColor = Color.DimGray;
		_status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		_btnOk.Left = 668;
		_btnOk.Top = 534;
		_btnOk.Width = 90;
		_btnOk.Height = 30;
		_btnOk.Text = "确定";
		_btnOk.DialogResult = DialogResult.OK;
		_btnOk.Font = new Font(Font, FontStyle.Bold);
		_btnOk.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
		_btnOk.Click += delegate
		{
			SelectedItems = (from s in _stats.Values
				where _checked.Contains(s.Item)
				orderby s.Count descending
				select s.Item).ToList();
			if (SelectedItems.Count == 0)
			{
				MessageBox.Show("一个故障项都没勾选。", "提示");
				base.DialogResult = DialogResult.None;
			}
		};
		Button button3 = new Button
		{
			Left = 764,
			Top = 534,
			Width = 88,
			Height = 30,
			Text = "取消",
			DialogResult = DialogResult.Cancel,
			Anchor = (AnchorStyles.Bottom | AnchorStyles.Right)
		};
		base.AcceptButton = _btnOk;
		base.CancelButton = button3;
		base.Controls.AddRange(new Control[11]
		{
			label, label2, _filter, _onlyThisStation, _btnDeep, button, button2, _list, _status, _btnOk,
			button3
		});
	}

	private static string ResolveModel(FailItemSource src)
	{
		if (string.IsNullOrWhiteSpace(src.XmlPath))
		{
			return src.Model ?? "";
		}
		PathMeta? pathMeta = PathMeta.FromPath(src.XmlPath, null);
		if (pathMeta == null)
		{
			return src.Model ?? "";
		}
		return pathMeta.ModelFromName ?? pathMeta.Model;
	}

	private string StationFilter()
	{
		if (!_onlyThisStation.Checked)
		{
			return "";
		}
		return _engine.ResolvedStationId;
	}

	private void LoadFast()
	{
		_stats.Clear();
		try
		{
			foreach (FailItemSource item in _engine.Db.FailItemSources(StationFilter(), ScanDays))
			{
				if (!string.IsNullOrWhiteSpace(item.FirstFailItem))
				{
					Bump(item.FirstFailItem, item, fromXml: false);
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Error("读取 FAIL 故障项失败: " + ex.Message);
			MessageBox.Show("读取 FAIL 记录失败: " + ex.Message, "提示");
		}
		Rebind();
	}

	private void Bump(string item, FailItemSource src, bool fromXml)
	{
		item = item.Trim();
		if (item.Length != 0)
		{
			if (!_stats.TryGetValue(item, out Stat? value))
			{
				value = new Stat
				{
					Item = item,
					FromXml = fromXml
				};
				_stats[item] = value;
			}
			value.Count++;
			string text = Database.NormalizeTs(string.IsNullOrWhiteSpace(src.Timestamp) ? src.TestDate : src.Timestamp);
			if (string.CompareOrdinal(text, value.LastSeen) > 0)
			{
				value.LastSeen = text;
			}
			string text2 = ResolveModel(src);
			if (!string.IsNullOrWhiteSpace(text2))
			{
				value.Models.Add(text2);
			}
			if (!string.IsNullOrWhiteSpace(src.StationId))
			{
				value.Stations.Add(src.StationId);
			}
		}
	}

	private async void StartDeepScan()
	{
		List<FailItemSource> source = _engine.Db.FailItemSources(StationFilter(), ScanDays);
		List<FailItemSource> withXml = source.Where((FailItemSource s) => !string.IsNullOrWhiteSpace(s.XmlPath)).ToList();
		if (withXml.Count == 0)
		{
			MessageBox.Show("库里没有可解析的 XML 路径。", "深扫");
			return;
		}
		_busy = true;
		_btnDeep.Text = "停止深扫";
		_btnOk.Enabled = false;
		_onlyThisStation.Enabled = false;
		_deepCts = new CancellationTokenSource();
		CancellationToken ct = _deepCts.Token;
		int done = 0;
		int missing = 0;
		int added = 0;
		List<(string item, FailItemSource src)> found = new List<(string, FailItemSource)>();
		Progress<(int done, int total, int added)> progress = new Progress<(int, int, int)>(delegate((int done, int total, int added) p)
		{
			_status.Text = $"深扫 XML… {p.done}/{p.total}，已补出 {p.added} 个新故障项\r\n（可点【停止深扫】用已扫到的部分）";
		});
		try
		{
			await Task.Run(delegate
			{
				IProgress<(int, int, int)> progress2 = progress;
				Stopwatch stopwatch = Stopwatch.StartNew();
				HashSet<string> hashSet = new HashSet<string>(_stats.Keys, StringComparer.OrdinalIgnoreCase);
				foreach (FailItemSource item2 in withXml)
				{
					ct.ThrowIfCancellationRequested();
					done++;
					if (!File.Exists(item2.XmlPath))
					{
						missing++;
					}
					else
					{
						XmlParser.ReportData reportData = XmlParser.ParseReport(item2.XmlPath);
						if (!reportData.Error)
						{
							XmlParser.ReportTest? reportTest = reportData.Tests.FirstOrDefault((XmlParser.ReportTest t) => t.Status.Contains("Fail", StringComparison.OrdinalIgnoreCase));
							if (reportTest != null)
							{
								string text = reportTest.Name.Trim();
								if (text.Length != 0)
								{
									lock (found)
									{
										found.Add((text, item2));
										if (hashSet.Add(text))
										{
											added++;
										}
									}
									if (stopwatch.ElapsedMilliseconds >= 300)
									{
										progress2.Report((done, withXml.Count, added));
										stopwatch.Restart();
									}
								}
							}
						}
					}
				}
				progress2.Report((done, withXml.Count, added));
			}, ct);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			Logger.Error("深扫 XML 失败: " + ex2.Message);
			MessageBox.Show("深扫失败: " + ex2.Message, "提示");
		}
		if (found.Count > 0)
		{
			_stats.Clear();
			foreach (var (item, src) in found)
			{
				Bump(item, src, fromXml: true);
			}
			foreach (FailItemSource item3 in withXml.Where((FailItemSource s) => !File.Exists(s.XmlPath)))
			{
				if (!string.IsNullOrWhiteSpace(item3.FirstFailItem))
				{
					Bump(item3.FirstFailItem, item3, fromXml: false);
				}
			}
		}
		_busy = false;
		_btnDeep.Text = "深扫 XML(更全)";
		_btnOk.Enabled = true;
		_onlyThisStation.Enabled = true;
		Rebind();
		Logger.Info($"[维修依据] 深扫 XML {done} 个（缺失 {missing}），去重后故障项 {_stats.Count} 个");
		if (missing > 0)
		{
			_status.Text += $"\r\n注意：{missing} 个 XML 文件已不在原路径（用库里的首个失败项兜底）";
		}
	}

	private IEnumerable<Stat> Filtered()
	{
		string kw = _filter.Text.Trim();
		IEnumerable<Stat> source = _stats.Values.AsEnumerable();
		if (kw.Length > 0)
		{
			source = source.Where((Stat s) => s.Item.Contains(kw, StringComparison.OrdinalIgnoreCase));
		}
		return source.OrderByDescending((Stat s) => s.Count).ThenBy<Stat, string>((Stat s) => s.Item, StringComparer.OrdinalIgnoreCase);
	}

	private void Rebind()
	{
		_binding = true;
		_list.BeginUpdate();
		try
		{
			_list.Items.Clear();
			foreach (Stat item in Filtered())
			{
				ListViewItem listViewItem = new ListViewItem(item.Item)
				{
					Tag = item,
					Checked = _checked.Contains(item.Item)
				};
				listViewItem.SubItems.Add(item.Count.ToString());
				listViewItem.SubItems.Add(ShortTime(item.LastSeen));
				ListViewItem.ListViewSubItemCollection subItems = listViewItem.SubItems;
				subItems.Add(item.Models.Count switch
				{
					0 => "—",
					1 => item.Models.First(),
					_ => $"{item.Models.First()} 等 {item.Models.Count} 个",
				});
				subItems = listViewItem.SubItems;
				subItems.Add(item.Stations.Count switch
				{
					0 => "—",
					1 => item.Stations.First(),
					_ => $"{item.Stations.Count} 台",
				});
				_list.Items.Add(listViewItem);
			}
		}
		finally
		{
			_list.EndUpdate();
			_binding = false;
		}
		UpdateStatus();
	}

	private void UpdateStatus()
	{
		int count = _list.Items.Count;
		_status.Text = $"去重后共 {_stats.Count} 个故障项（近 {ScanDays} 天，当前显示 {count} 个），已勾选 {_checked.Count} 个\r\n" + (_stats.Values.Any((Stat s) => s.FromXml) ? "来源：XML 深扫（每条记录只取第一条失败项）" : $"来源：数据库 fail_reason（每条 FAIL 只取首个失败项，近 {ScanDays} 天）—— 需要复核源文件请点【深扫 XML】");
	}

	private void SetFilteredChecked(bool on)
	{
		foreach (Stat item in Filtered())
		{
			if (on)
			{
				_checked.Add(item.Item);
			}
			else
			{
				_checked.Remove(item.Item);
			}
		}
		Rebind();
	}

	private static string ShortTime(string? ts)
	{
		return TimeUtil.Short(ts);
	}

	public static List<(string Item, int Count)> Aggregate(IEnumerable<FailItemSource> sources)
	{
		Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (FailItemSource source in sources)
		{
			string text = (source.FirstFailItem ?? "").Trim();
			if (text.Length != 0)
			{
				dictionary[text] = dictionary.GetValueOrDefault(text) + 1;
			}
		}
		return (from kv in dictionary.OrderByDescending((KeyValuePair<string, int> kv) => kv.Value).ThenBy<KeyValuePair<string, int>, string>((KeyValuePair<string, int> kv) => kv.Key, StringComparer.OrdinalIgnoreCase)
			select (Key: kv.Key, Value: kv.Value)).ToList();
	}
}

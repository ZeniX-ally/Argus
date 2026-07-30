using NationalInstruments.Tdms;

namespace FctTdmsViewer;

/// <summary>一个通道的元信息（不含数据，构造代价低）。</summary>
public sealed class ChannelInfo
{
    public string GroupName = "";
    public string Name = "";
    public Type? DataType;
    public long Count;
    public bool HasData;
    public IDictionary<string, object> Properties = new Dictionary<string, object>();

    public string TypeName => DataType?.Name ?? "?";
    /// <summary>能否画成波形（数值类型才行）。</summary>
    public bool Numeric => DataType != null && TdmsDoc.IsNumeric(DataType);
    public override string ToString() => Name;
}

public sealed class GroupInfo
{
    public string Name = "";
    public IDictionary<string, object> Properties = new Dictionary<string, object>();
    public List<ChannelInfo> Channels = new();
    public long SampleCount => Channels.Count > 0 ? Channels[0].Count : 0;

    /// <summary>文件中的物理顺序（TDMS 写入顺序）。</summary>
    public int FileIndex;

    /// <summary>
    /// 从组名开头解析出的测试项编号，如 "8.11 Resolver Test" -> [8, 11]。
    /// 用于自然排序 —— 按字符串排会得出 8.11 &lt; 8.2 的错误结果。
    /// </summary>
    public int[] Number = Array.Empty<int>();

    /// <summary>排序后的显示序号（1 起）。</summary>
    public int Seq;

    public string NumberText => Number.Length == 0 ? "" : string.Join(".", Number);

    public override string ToString() => Name;
}

/// <summary>
/// TDMS 文档封装。
///
/// 基于 TDMSReader 3.1.0（纯 C#、无依赖、.NETStandard2.0）。
/// 该库的解析结果已与 Python npTDMS 1.10.0 交叉核对一致
/// （17 组 / 各组采样点数 / RES_v_ResAng 的 n·min·max·mean 逐位吻合）。
///
/// 打开时只读元信息（用 Channel.DataCount，不载入数据），
/// 数据按需惰性读取并缓存，避免大文件一次性吃满内存。
/// </summary>
public sealed class TdmsDoc : IDisposable
{
    private NationalInstruments.Tdms.File? _file;
    private readonly Dictionary<string, double[]> _cache = new();

    public string Path { get; private set; } = "";
    public long FileBytes { get; private set; }
    public IDictionary<string, object> Properties { get; private set; } = new Dictionary<string, object>();
    public List<GroupInfo> Groups { get; } = new();

    public int TotalChannels => Groups.Sum(g => g.Channels.Count);

    public static TdmsDoc Load(string path)
    {
        var doc = new TdmsDoc { Path = path };
        doc.FileBytes = new FileInfo(path).Length;
        var f = new NationalInstruments.Tdms.File(path);
        f.Open();
        doc._file = f;
        doc.Properties = f.Properties;

        foreach (var gkv in f.Groups)
        {
            var g = gkv.Value;
            var gi = new GroupInfo
            {
                Name = g.Name,
                Properties = g.Properties,
                FileIndex = doc.Groups.Count,
                Number = ParseLeadingNumber(g.Name),
            };
            foreach (var ckv in g.Channels)
            {
                var c = ckv.Value;
                gi.Channels.Add(new ChannelInfo
                {
                    GroupName = g.Name,
                    Name = c.Name,
                    DataType = c.DataType,
                    Count = c.DataCount,
                    HasData = c.HasData,
                    Properties = c.Properties,
                });
            }
            doc.Groups.Add(gi);
        }
        doc.SortGroups(GroupOrder.ByNumber);
        return doc;
    }

    /// <summary>
    /// 提取组名开头的点分编号："8.11 Resolver Test" -> [8,11]，"6.1 Power Test" -> [6,1]。
    /// 开头无数字则返回空数组（排到末尾）。
    /// </summary>
    public static int[] ParseLeadingNumber(string name)
    {
        var s = name.TrimStart();
        int i = 0;
        var parts = new List<int>();
        var cur = new System.Text.StringBuilder();
        while (i < s.Length)
        {
            char ch = s[i];
            if (char.IsDigit(ch)) { cur.Append(ch); i++; }
            else if (ch == '.' && cur.Length > 0 && i + 1 < s.Length && char.IsDigit(s[i + 1]))
            {
                parts.Add(int.Parse(cur.ToString()));
                cur.Clear();
                i++;
            }
            else break;
        }
        if (cur.Length > 0) parts.Add(int.Parse(cur.ToString()));
        return parts.ToArray();
    }

    public enum GroupOrder
    {
        /// <summary>按测试项编号自然排序（6.1 &lt; 7.2 &lt; 8.2 &lt; 8.10 &lt; 8.11）。</summary>
        ByNumber,
        /// <summary>文件物理顺序（TDMS 写入顺序）。</summary>
        FileOrder,
    }

    public GroupOrder Order { get; private set; } = GroupOrder.ByNumber;

    public void SortGroups(GroupOrder order)
    {
        Order = order;
        if (order == GroupOrder.FileOrder)
        {
            Groups.Sort((a, b) => a.FileIndex.CompareTo(b.FileIndex));
        }
        else
        {
            Groups.Sort((a, b) =>
            {
                // 无编号的排最后
                if (a.Number.Length == 0 && b.Number.Length > 0) return 1;
                if (b.Number.Length == 0 && a.Number.Length > 0) return -1;
                int n = Math.Min(a.Number.Length, b.Number.Length);
                for (int i = 0; i < n; i++)
                {
                    int c = a.Number[i].CompareTo(b.Number[i]);
                    if (c != 0) return c;
                }
                int c2 = a.Number.Length.CompareTo(b.Number.Length);
                if (c2 != 0) return c2;
                // 编号相同（如 8.20 ASC..._1 / _2）时按名称，再按文件顺序兜底
                int c3 = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return c3 != 0 ? c3 : a.FileIndex.CompareTo(b.FileIndex);
            });
        }
        for (int i = 0; i < Groups.Count; i++) Groups[i].Seq = i + 1;
    }

    private static readonly HashSet<Type> NumericTypes = new()
    {
        typeof(double), typeof(float), typeof(long), typeof(ulong),
        typeof(int), typeof(uint), typeof(short), typeof(ushort),
        typeof(sbyte), typeof(byte), typeof(bool),
    };

    public static bool IsNumeric(Type t) => NumericTypes.Contains(t);

    /// <summary>读取通道数据并统一转 double（便于绘图与统计）。非数值类型返回空数组。</summary>
    public double[] GetData(ChannelInfo ci)
    {
        var key = ci.GroupName + "\u0000" + ci.Name;
        if (_cache.TryGetValue(key, out var got)) return got;

        double[] result = Array.Empty<double>();
        var ch = FindChannel(ci);
        if (ch != null && ci.DataType != null)
        {
            var t = ci.DataType;
            try
            {
                if (t == typeof(double)) result = ch.GetData<double>().ToArray();
                else if (t == typeof(float)) result = ch.GetData<float>().Select(x => (double)x).ToArray();
                else if (t == typeof(int)) result = ch.GetData<int>().Select(x => (double)x).ToArray();
                else if (t == typeof(uint)) result = ch.GetData<uint>().Select(x => (double)x).ToArray();
                else if (t == typeof(short)) result = ch.GetData<short>().Select(x => (double)x).ToArray();
                else if (t == typeof(ushort)) result = ch.GetData<ushort>().Select(x => (double)x).ToArray();
                else if (t == typeof(long)) result = ch.GetData<long>().Select(x => (double)x).ToArray();
                else if (t == typeof(ulong)) result = ch.GetData<ulong>().Select(x => (double)x).ToArray();
                else if (t == typeof(sbyte)) result = ch.GetData<sbyte>().Select(x => (double)x).ToArray();
                else if (t == typeof(byte)) result = ch.GetData<byte>().Select(x => (double)x).ToArray();
                else if (t == typeof(bool)) result = ch.GetData<bool>().Select(x => x ? 1.0 : 0.0).ToArray();
            }
            catch
            {
                result = Array.Empty<double>();
            }
        }
        _cache[key] = result;
        return result;
    }

    /// <summary>非数值通道（字符串/时间）取原始文本，用于表格展示。</summary>
    public string[] GetText(ChannelInfo ci)
    {
        var ch = FindChannel(ci);
        if (ch == null) return Array.Empty<string>();
        try
        {
            return ch.RawData.Cast<object?>()
                     .Select(o => o?.ToString() ?? "")
                     .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private Channel? FindChannel(ChannelInfo ci)
    {
        if (_file == null) return null;
        foreach (var gkv in _file.Groups)
        {
            if (gkv.Value.Name != ci.GroupName) continue;
            foreach (var ckv in gkv.Value.Channels)
                if (ckv.Value.Name == ci.Name) return ckv.Value;
        }
        return null;
    }

    /// <summary>采样间隔（秒）。取通道属性 wf_increment，缺省 0 表示按索引显示 X 轴。</summary>
    public static double GetIncrement(ChannelInfo ci)
    {
        if (ci.Properties.TryGetValue("wf_increment", out var v))
        {
            try { return Convert.ToDouble(v); } catch { }
        }
        return 0;
    }

    public sealed class Stat
    {
        public int N;
        public double Min, Max, Mean, Std;
        public double First, Last;
    }

    public static Stat? Describe(double[] d)
    {
        if (d.Length == 0) return null;
        double min = double.MaxValue, max = double.MinValue, sum = 0;
        int n = 0;
        foreach (var x in d)
        {
            if (double.IsNaN(x)) continue;
            if (x < min) min = x;
            if (x > max) max = x;
            sum += x;
            n++;
        }
        if (n == 0) return null;
        double mean = sum / n, sq = 0;
        foreach (var x in d)
        {
            if (double.IsNaN(x)) continue;
            sq += (x - mean) * (x - mean);
        }
        return new Stat
        {
            N = d.Length, Min = min, Max = max, Mean = mean,
            Std = n > 1 ? Math.Sqrt(sq / (n - 1)) : 0,
            First = d[0], Last = d[^1],
        };
    }

    public void Dispose()
    {
        _cache.Clear();
        _file?.Dispose();
        _file = null;
    }
}

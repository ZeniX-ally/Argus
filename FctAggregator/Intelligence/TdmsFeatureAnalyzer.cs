namespace FctAggregator;

/// <summary>规格06期3：TDMS 通道特征纯函数 + TDMS 文件定位器（消费内存通道数据，selftest 可直测）。</summary>
public static class TdmsFeatureAnalyzer
{
    public sealed class TdmsChannelData
    {
        public string Group = "";
        public string Channel = "";
        public double[] Data = Array.Empty<double>();
    }

    public sealed class TdmsFeatureRow
    {
        public string GroupName = "";
        public string ChannelName = "";
        public string Section = "";
        public int N;
        public double? Min, Max, Mean, Std, First, Last;
    }

    /// <summary>逐通道基础统计（n/min/max/mean/std/first/last），统计口径单一来源 = FctTdmsViewer.TdmsDoc.Describe。</summary>
    public static List<TdmsFeatureRow> Compute(IEnumerable<TdmsChannelData> channels, int maxChannels)
    {
        var rows = new List<TdmsFeatureRow>();
        if (maxChannels < 1) maxChannels = 1;
        foreach (var ch in channels)
        {
            if (rows.Count >= maxChannels) break;
            if (ch.Data.Length == 0) continue;
            var st = FctTdmsViewer.TdmsDoc.Describe(ch.Data);
            if (st == null) continue; // 全 NaN 等无可统计样本
            rows.Add(new TdmsFeatureRow
            {
                GroupName = ch.Group,
                ChannelName = ch.Channel,
                Section = SectionOf(ch.Group),
                N = st.N,
                Min = st.Min,
                Max = st.Max,
                Mean = st.Mean,
                Std = st.Std,
                First = st.First,
                Last = st.Last,
            });
        }
        return rows;
    }

    /// <summary>章节号文本（ParseLeadingNumber → "3.2"；无数字前缀 → ""）。</summary>
    public static string SectionOf(string groupName)
    {
        var n = FctTdmsViewer.TdmsDoc.ParseLeadingNumber(groupName ?? "");
        return n.Length == 0 ? "" : string.Join(".", n);
    }

    /// <summary>
    /// TDMS 定位：布局契约与 Fetcher FileLocator.LocateTdms 一致——{tdms_root}/{Category}/{Model}/{yyyyMMdd}/{SN}_*.tdms。
    /// 防呆：root/sn 空或 testDate 非 8 位日期目录 → ""（静默）；文件名 OrdinalDescending 取最新；
    /// 无 fallback 全盘扫描（实时链路禁用）。
    /// </summary>
    public static string LocateTdmsFile(string tdmsRoot, string category, string model, string testDate, string sn)
    {
        if (string.IsNullOrWhiteSpace(tdmsRoot) || string.IsNullOrWhiteSpace(sn)) return "";
        if (string.IsNullOrWhiteSpace(testDate) || testDate.Length != 8 || !testDate.All(char.IsDigit)) return "";
        try
        {
            var dir = Path.Combine(tdmsRoot, category ?? "", model ?? "", testDate);
            if (!Directory.Exists(dir)) return "";
            var latest = Directory.GetFiles(dir, sn + "_*.tdms")
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                .FirstOrDefault();
            return latest ?? "";
        }
        catch { return ""; }
    }
}

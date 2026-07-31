using System.Text.RegularExpressions;

namespace FctDistributor;

/// <summary>XML 内 TIMESTAMP 属性的时间范围（流式正则，不建 DOM，大文件也快）。</summary>
public static class XmlTimeScanner
{
    private static readonly Regex TsRe = new(
        @"TIMESTAMP\s*=\s*[""'](\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})\.(\d+)",
        RegexOptions.Compiled);

    public sealed class Range
    {
        public DateTime Min;
        public DateTime Max;
        public int Count;
    }

    public static Range? Scan(string path)
    {
        Range? r = null;
        try
        {
            using var sr = new StreamReader(path);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                foreach (Match m in TsRe.Matches(line))
                {
                    DateTime dt;
                    try
                    {
                        var frac = m.Groups[7].Value;
                        int ms = int.Parse((frac.Length >= 3 ? frac[..3] : frac.PadRight(3, '0')));
                        dt = new DateTime(
                            int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                            int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value),
                            int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value), ms);
                    }
                    catch { continue; }

                    if (r == null) r = new Range { Min = dt, Max = dt, Count = 1 };
                    else
                    {
                        if (dt < r.Min) r.Min = dt;
                        if (dt > r.Max) r.Max = dt;
                        r.Count++;
                    }
                }
            }
        }
        catch { return null; }
        return r;
    }
}

using System.Collections.Generic;
using System.Text.RegularExpressions;
using FctAggregator;

namespace FctAggregator.Parsing;

public interface IResultParser
{
    string Id { get; }

    int Priority { get; }

    ParseOutput? Parse(string xmlPath, string rawXml);
}

public class ParseOutput
{
    public bool Skipped;
    public bool Error;
    public string ErrorCode = "";
    public string? SkipReason;

    public string Result = "";
    public string StationId = "";
    public string Model = "";
    public string Category = "";
    public string TestDate = "";
    public string? Sn;
    public string? FailReason;
    public string? Tester;
    public string? PanelStatus;
    public string? FixtureId;
    public string? BatchTimestamp;
    public bool HasFailItems;
    public List<FailedTest> FailedTests { get; set; } = new();
    public long? FileSize;
}

public sealed class PathMeta
{
    public string Category = "";
    public string Model = "";
    public string TestDate = "";
    public string Filename = "";
    public string? Sn;
    public string? ModelFromName;
    public string Prefix = "";
    public string? FileTime;

    private static readonly Regex TsSegRe =
        new(@"(?:^|_)(\d{14}|\d{17})(?=_|$)", RegexOptions.Compiled);

    public static PathMeta? FromPath(string path, ParserRuleSet? rules)
    {
        rules ??= ParserRuleSet.Default;
        var m = rules.PathRegex.Match(path);
        if (!m.Success) return null;
        string cat = m.Groups["category"].Value;
        string model = m.Groups["model"].Value;
        string date = m.Groups["date"].Value;
        string file = m.Groups["file"].Value;

        var stem = file;
        if (stem.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        stem = stem.Replace("(debug)", "").Replace("(Debug)", "");
        var parts = stem.Split('_');

        var info = new PathMeta
        {
            Category = cat,
            Model = model,
            TestDate = date,
            Filename = file,
            Prefix = file.Length >= 2 ? file[..2].ToUpperInvariant() : "",
        };
        if (parts.Length >= rules.SnSegment && rules.SnSegment > 0)
        {
            var sn = parts[rules.SnSegment - 1];
            info.Sn = sn;
            if (sn.Length >= 8) info.ModelFromName = sn[..8];
        }
        var ft = TsSegRe.Match(stem);
        if (ft.Success) info.FileTime = ft.Groups[1].Value;
        return info;
    }
}

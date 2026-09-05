using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FctAggregator;

public class ParsedFailReason
{
    public string Original { get; set; } = "";
    public string Section { get; set; } = "";
    public string SignalBase { get; set; } = "";
    public string MeasureType { get; set; } = "";
    public string FamilyKey { get; set; } = "";
    public FailSemanticType SemanticType { get; set; } = FailSemanticType.Measurement;
    public string RootCauseHint { get; set; } = "";
    public bool IsParsedSuccess { get; set; } = false;
}

public class SectionGroupAlertResult
{
    public string Section { get; set; } = "";
    public int DistinctSignalCount { get; set; }
    public List<string> SignalNames { get; set; } = new();
    public string RootCauseHint { get; set; } = "";
    public string AlertMessage { get; set; } = "";
}

public static class FailReasonMerger
{
    private static readonly Regex _regexSection = new(@"^(\d+(?:\.\d+)+)\s*", RegexOptions.Compiled);

    private static readonly Regex _regexMeasureType = new(@"\((DMM|XCP|OSC|Power)\)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _regexValueSpec = new(@"\((?:值|Value|Val)[^)]+\)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _regexPropertySuffix = new(@"\s*(?:to\s+GND\s+)?(?:High|Low)\s+Level|\s*Frequency|_Offset", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ParsedFailReason Parse(string? raw)
    {
        var result = new ParsedFailReason
        {
            Original = raw ?? ""
        };

        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        var text = raw.Trim();

        text = _regexValueSpec.Replace(text, "").Trim();

        var mType = _regexMeasureType.Match(text);
        if (mType.Success)
        {
            result.MeasureType = mType.Groups[1].Value.ToUpperInvariant();
            text = text.Substring(0, mType.Index).Trim();
        }

        var mSec = _regexSection.Match(text);
        if (mSec.Success)
        {
            result.Section = mSec.Groups[1].Value;
            text = text.Substring(mSec.Length).Trim();
        }

        if (text.StartsWith("Volt for ", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring("Volt for ".Length).Trim();
        }

        var cleanSignal = _regexPropertySuffix.Replace(text, "").Trim();
        result.SignalBase = string.IsNullOrEmpty(cleanSignal) ? text : cleanSignal;

        if (string.IsNullOrEmpty(result.Section) && string.IsNullOrEmpty(result.SignalBase))
        {
            result.FamilyKey = result.Original;
            return result;
        }

        result.IsParsedSuccess = true;

        var dictInfo = G49ProductDictionary.FindKnownSignal(result.SignalBase);
        if (dictInfo != null)
        {
            result.FamilyKey = dictInfo.FamilyName;
            result.SemanticType = dictInfo.SemanticType;
            result.RootCauseHint = dictInfo.RootCauseHint;
            if (string.IsNullOrEmpty(result.Section))
            {
                result.Section = dictInfo.Section;
            }
        }
        else
        {
            if (G49ProductDictionary.IsInjectionSection(result.Section))
            {
                result.SemanticType = FailSemanticType.Injection;
                result.FamilyKey = $"{result.Section} FaultInjection";
                result.RootCauseHint = G49ProductDictionary.GetSectionRootCause(result.Section);
            }
            else
            {
                result.FamilyKey = result.SignalBase;
                result.RootCauseHint = G49ProductDictionary.GetSectionRootCause(result.Section);
            }
        }

        return result;
    }

    public static string GetMergedKey(string? raw, bool enabled, string level = "signal")
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (!enabled || string.Equals(level, "off", StringComparison.OrdinalIgnoreCase))
        {
            return raw;
        }

        var parsed = Parse(raw);
        if (!parsed.IsParsedSuccess)
        {
            return raw;
        }

        if (string.Equals(level, "section", StringComparison.OrdinalIgnoreCase))
        {
            var secParts = parsed.Section.Split('.');
            if (secParts.Length >= 2)
            {
                return $"§{secParts[0]}.{secParts[1]}";
            }
            return string.IsNullOrEmpty(parsed.Section) ? raw : $"§{parsed.Section}";
        }

        string prefix = "";
        if (!string.IsNullOrEmpty(parsed.Section))
        {
            var secParts = parsed.Section.Split('.');
            prefix = secParts.Length >= 2 ? $"{secParts[0]}.{secParts[1]} " : $"{parsed.Section} ";
        }
        var mType = string.IsNullOrEmpty(parsed.MeasureType) ? "" : $"({parsed.MeasureType})";
        return $"{prefix}{parsed.FamilyKey}{mType}".Trim();
    }

    public static List<SectionGroupAlertResult> CheckSectionGroupAlert(IEnumerable<string> failReasons, int minDistinctSignals = 3)
    {
        var alerts = new List<SectionGroupAlertResult>();
        if (failReasons == null || minDistinctSignals <= 0) return alerts;

        var parsedList = failReasons
            .Select(Parse)
            .Where(p => p.IsParsedSuccess && !string.IsNullOrEmpty(p.Section))
            .ToList();

        var groupedByMajorSection = parsedList
            .Where(p => p.SemanticType != FailSemanticType.Injection && !G49ProductDictionary.IsInjectionSection(p.Section))
            .GroupBy(p =>
            {
                var parts = p.Section.Split('.');
                return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : p.Section;
            });

        foreach (var group in groupedByMajorSection)
        {
            var distinctSignals = group
                .Select(p => string.IsNullOrEmpty(p.FamilyKey) ? p.SignalBase : p.FamilyKey)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinctSignals.Count >= minDistinctSignals)
            {
                var sec = group.Key;
                var rootCause = G49ProductDictionary.GetSectionRootCause(sec);
                alerts.Add(new SectionGroupAlertResult
                {
                    Section = sec,
                    DistinctSignalCount = distinctSignals.Count,
                    SignalNames = distinctSignals,
                    RootCauseHint = rootCause,
                    AlertMessage = $"章节 §{sec} 群挂预警: 同章节 {distinctSignals.Count} 个不同信号同日失效 ({string.Join(", ", distinctSignals.Take(4))})。建议: {(string.IsNullOrEmpty(rootCause) ? "排查该测试域供电或工装" : rootCause)}"
                });
            }
        }

        return alerts;
    }
}

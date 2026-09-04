using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FctAggregator;

/// <summary>
/// 故障解析结果
/// </summary>
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

/// <summary>
/// 章节群挂判定结果
/// </summary>
public class SectionGroupAlertResult
{
    public string Section { get; set; } = "";
    public int DistinctSignalCount { get; set; }
    public List<string> SignalNames { get; set; } = new();
    public string RootCauseHint { get; set; } = "";
    public string AlertMessage { get; set; } = "";
}

/// <summary>
/// 故障归并与群挂解析核心（依据 G49 spec 纯函数）
/// 支持：字典优先 -> 正则回落 -> 原串兜底
/// 三级粒度：off / signal / section
/// </summary>
public static class FailReasonMerger
{
    // 正则预编译：匹配开头章节号 (如 "8.11.6.1", "6.1.1.2.7", 容忍后续无空格或多空格)
    private static readonly Regex _regexSection = new(@"^(\d+(?:\.\d+)+)\s*", RegexOptions.Compiled);

    // 正则预编译：匹配尾部测量类型 (如 "(DMM)", "(XCP)", "(OSC)", "(Power)")
    private static readonly Regex _regexMeasureType = new(@"\((DMM|XCP|OSC|Power)\)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 正则预编译：匹配尾部实测值与规格详情 (如 "(值=33.85, 规格=5~, mA)" 或 "(Value=33.85...)")
    private static readonly Regex _regexValueSpec = new(@"\((?:值|Value|Val)[^)]+\)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 属性后缀正则 (如 "to GND High Level", "High Level", "Low Level", "Frequency", "_Offset")
    private static readonly Regex _regexPropertySuffix = new(@"\s*(?:to\s+GND\s+)?(?:High|Low)\s+Level|\s*Frequency|_Offset", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 解析单个 fail_reason 字符串
    /// </summary>
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

        // 步骤 1: 剥离尾部实测值/规格详情
        text = _regexValueSpec.Replace(text, "").Trim();

        // 步骤 2: 提取并剥离测量类型 (DMM/XCP/OSC/Power)
        var mType = _regexMeasureType.Match(text);
        if (mType.Success)
        {
            result.MeasureType = mType.Groups[1].Value.ToUpperInvariant();
            text = text.Substring(0, mType.Index).Trim();
        }

        // 步骤 3: 提取并剥离章节号
        var mSec = _regexSection.Match(text);
        if (mSec.Success)
        {
            result.Section = mSec.Groups[1].Value;
            text = text.Substring(mSec.Length).Trim();
        }

        // 步骤 4: 处理属性前缀 (如 "Volt for TC_AI_Cur_1")
        if (text.StartsWith("Volt for ", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring("Volt for ".Length).Trim();
        }

        // 步骤 5: 剥离属性后缀 (如 "High Level", "_Offset")
        var cleanSignal = _regexPropertySuffix.Replace(text, "").Trim();
        result.SignalBase = string.IsNullOrEmpty(cleanSignal) ? text : cleanSignal;

        if (string.IsNullOrEmpty(result.Section) && string.IsNullOrEmpty(result.SignalBase))
        {
            // 解析无有效结构，原串兜底
            result.FamilyKey = result.Original;
            return result;
        }

        result.IsParsedSuccess = true;

        // 步骤 6: 查字典优先
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
            // 正则/通用回落：检查是否属于注入型章节
            if (G49ProductDictionary.IsInjectionSection(result.Section))
            {
                result.SemanticType = FailSemanticType.Injection;
                result.FamilyKey = $"{result.Section} FaultInjection";
                result.RootCauseHint = G49ProductDictionary.GetSectionRootCause(result.Section);
            }
            else
            {
                // 通用测量型，保留信号基名作为族名
                result.FamilyKey = result.SignalBase;
                result.RootCauseHint = G49ProductDictionary.GetSectionRootCause(result.Section);
            }
        }

        return result;
    }

    /// <summary>
    /// 根据配置粒度获取归并后的展示/聚合 Key
    /// </summary>
    /// <param name="raw">原始 fail_reason</param>
    /// <param name="enabled">是否开启归并</param>
    /// <param name="level">归并粒度: off / signal / section</param>
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
            // 提取主章号或步骤章号 (如 6.1.1.2.7 -> 6.1)
            var secParts = parsed.Section.Split('.');
            if (secParts.Length >= 2)
            {
                return $"§{secParts[0]}.{secParts[1]}";
            }
            return string.IsNullOrEmpty(parsed.Section) ? raw : $"§{parsed.Section}";
        }

        // 默认为 signal 粒度: [主章节号] + [族名] + [测量类型]
        // 前缀取主章号（6.1.1.2.7 → 6.1）：同族信号常分布在不同子步骤（PWM 双轮/旋变四角），
        // 前缀带全节号会把同族拆散到不同 key，违背归并初衷。
        string prefix = "";
        if (!string.IsNullOrEmpty(parsed.Section))
        {
            var secParts = parsed.Section.Split('.');
            prefix = secParts.Length >= 2 ? $"{secParts[0]}.{secParts[1]} " : $"{parsed.Section} ";
        }
        var mType = string.IsNullOrEmpty(parsed.MeasureType) ? "" : $"({parsed.MeasureType})";
        return $"{prefix}{parsed.FamilyKey}{mType}".Trim();
    }

    /// <summary>
    /// 检查当日故障列表是否存在章节群挂（同章节不同信号基名 >= minDistinctSignals）
    /// 排除注入型故障 (§8.19, §8.20, §8.21)
    /// </summary>
    public static List<SectionGroupAlertResult> CheckSectionGroupAlert(IEnumerable<string> failReasons, int minDistinctSignals = 3)
    {
        var alerts = new List<SectionGroupAlertResult>();
        if (failReasons == null || minDistinctSignals <= 0) return alerts;

        // 1. 解析所有 failReasons
        var parsedList = failReasons
            .Select(Parse)
            .Where(p => p.IsParsedSuccess && !string.IsNullOrEmpty(p.Section))
            .ToList();

        // 2. 按大章分类 (例如 6.1.1.2.7 -> 6.1)
        var groupedByMajorSection = parsedList
            .Where(p => p.SemanticType != FailSemanticType.Injection && !G49ProductDictionary.IsInjectionSection(p.Section))
            .GroupBy(p =>
            {
                var parts = p.Section.Split('.');
                return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : p.Section;
            });

        foreach (var group in groupedByMajorSection)
        {
            // 群挂计数按「信号族」去重（规格 05 §3.4）：RES_v_ResAng 四角度属同族只算 1 个信号——
            // 族内全挂指向同一根因（仿真器/接线），不是多信号群挂；P5V_CAN 与 P1.25V 是不同族才算群挂。
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

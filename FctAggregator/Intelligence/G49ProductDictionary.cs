using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FctAggregator;

/// <summary>
/// G49 故障判定语义分类（依据 G49 FCT 规范）
/// </summary>
public enum FailSemanticType
{
    Measurement, // 测量型：电压/电流/温度等物理量超差 (全基线适用)
    Communication, // 通信型：XCP 读取失败、以太网 linkStatus 等 (指向通信链)
    Injection, // 注入型：流程故意注入故障，预期翻转未发生 (排除出章节群挂)
    Interrupted // 中断型：流程中断
}

/// <summary>
/// 信号族定义与归并元数据
/// </summary>
public class SignalFamilyInfo
{
    public string FamilyName { get; set; } = "";
    public string RootCauseHint { get; set; } = "";
    public FailSemanticType SemanticType { get; set; } = FailSemanticType.Measurement;
    public string Section { get; set; } = "";
}

/// <summary>
/// G49 产品专有信号族字典与知识库（依据 G49 FCT 规范 §6~§9）
/// 随产品定义扩展，纯数据驱动，无需触碰核心调度逻辑
/// </summary>
public static class G49ProductDictionary
{
    // 精确信号族映射表 (Key: 信号全名或标准化名, Value: 信号族信息)
    private static readonly Dictionary<string, SignalFamilyInfo> _knownSignals = new(StringComparer.OrdinalIgnoreCase);

    // 章节号 -> 工装/治具依赖根因映射 (依据 spec §4.4)
    private static readonly Dictionary<string, string> _sectionRootCauses = new(StringComparer.OrdinalIgnoreCase)
    {
        { "6.1", "先查供电电源/各电源轨硬件回路" },
        { "7.2", "先查以太网/通信链路/PHY硬件" },
        { "8.1", "先查防反接二极管/KL30_1回路" },
        { "8.7", "先查NTC仿真电阻(493.4Ω)" },
        { "8.8", "先查环温/环境NTC采集通道" },
        { "8.9", "先查三相电流采样/治具接触" },
        { "8.10", "先查NTC标准电阻链工装" },
        { "8.11", "先查旋变仿真器/接线工装" },
        { "8.14", "先查X4400继电器负载工装" },
        { "8.15", "先查阀体负载(30Ω)工装" },
        { "8.18", "先查栅极驱动供电/光耦/驱动IC" },
        { "8.19", "DESAT故意注入流程，检查退饱和保护电路" },
        { "8.20", "ASC故意注入流程，检查主动短路逻辑" },
        { "8.21", "SBC故意注入流程，检查系统基础芯片看门狗" }
    };

    static G49ProductDictionary()
    {
        // 1. 旋变四角 (spec §8.11)
        var resAngles = new[] { "45°", "135°", "225°", "315°", "45", "135", "225", "315" };
        foreach (var ang in resAngles)
        {
            Register("8.11", $"RES_v_ResAng({ang})", "RES_v_ResAng(AngleFamily)", "先查旋变仿真器/接线工装", FailSemanticType.Measurement);
            Register("8.11", $"RES_v_ResAng_{ang}", "RES_v_ResAng(AngleFamily)", "先查旋变仿真器/接线工装", FailSemanticType.Measurement);
        }

        // 2. 栅极驱动六相 (spec §8.18 / §8.20)
        var phases = new[] { "HU", "HV", "HW", "LU", "LV", "LW" };
        foreach (var p in phases)
        {
            Register("8.18", $"SiC_G_{p}", "SiC_G_GateDrive", "先查栅极驱动芯片/供电回路", FailSemanticType.Measurement);
            Register("8.18", $"SiC_S_{p}", "SiC_S_SenseDrive", "先查源极检测回路", FailSemanticType.Measurement);
            Register("8.18", $"SiC_G_HV_Low_Level_{p}", "SiC_G_HV_Low_Level", "先查栅极驱动低电平回路/供电偏置", FailSemanticType.Measurement);
        }

        // 3. 三相电流 (spec §8.9)
        var curPhases = new[] { "1", "2", "3", "U", "V", "W" };
        foreach (var c in curPhases)
        {
            Register("8.9", $"TC_AI_Cur_{c}", "TC_AI_Cur", "先查三相电流传感器/接触工装", FailSemanticType.Measurement);
            Register("8.9", $"CURMV_v_CurL{c}V", "CURMV_v_CurL", "先查相电压相电流采样调理电路", FailSemanticType.Measurement);
        }

        // 4. IGBT 三相温度 (spec §8.7)
        var igbtPhases = new[] { "U", "V", "W" };
        foreach (var t in igbtPhases)
        {
            Register("8.7", $"IGBTTM_v_IgbtT{t}", "IGBTTM_v_IgbtT(PhaseFamily)", "先查NTC仿真电阻链(493.4Ω)", FailSemanticType.Measurement);
        }

        // 5. 成对通道 (spec §8.8 等)
        Register("8.8", "CBTM_v_CbT1", "CBTM_v_CbT(PairFamily)", "先查环境温度双通道NTC传感器", FailSemanticType.Measurement);
        Register("8.8", "CBTM_v_CbT2", "CBTM_v_CbT(PairFamily)", "先查环境温度双通道NTC传感器", FailSemanticType.Measurement);
        Register("8.14", "BSW_v_eFuse_A", "BSW_v_eFuse(PairFamily)", "先查电子保险丝A/B回路", FailSemanticType.Measurement);
        Register("8.14", "BSW_v_eFuse_B", "BSW_v_eFuse(PairFamily)", "先查电子保险丝A/B回路", FailSemanticType.Measurement);
        Register("8.16", "HVIL_In", "HVIL(PairFamily)", "先查高压互锁闭合回路", FailSemanticType.Measurement);
        Register("8.16", "HVIL_Out", "HVIL(PairFamily)", "先查高压互锁闭合回路", FailSemanticType.Measurement);

        // 6. 常见电源轨 (spec §6.1)
        Register("6.1", "P5V_CAN", "P5V_CAN", "先查电源板/5V稳压器", FailSemanticType.Measurement);
        Register("6.1", "P1.25V", "P1.25V", "先查电源板/1.25V稳压器", FailSemanticType.Measurement);
        Register("6.1", "VREF", "VREF", "先查基准电压源", FailSemanticType.Measurement);

        // 7. 注入型故障章节 (spec §8.19, §8.20, §8.21)
        Register("8.19", "DESAT_Injection", "DESAT_FaultInjection", "DESAT注入，排查退饱和驱动保护", FailSemanticType.Injection);
        Register("8.20", "ASC_Injection", "ASC_FaultInjection", "ASC注入，排查主动短路逻辑", FailSemanticType.Injection);
        Register("8.21", "SBC_Watchdog", "SBC_FaultInjection", "SBC注入，排查看门狗复位逻辑", FailSemanticType.Injection);

        // 8. KL30 双轨 (spec §2.3)：两轨独立不并——1/2 号轨（含 FILT/HS/LS 变体）各自归同一轨族，
        //    跨轨绝不合并，保住诊断信息（同一电源系统的独立防线）。
        Register("8.1", "KL30_1", "KL30_1(RailFamily)", "先查KL30_1回路/防反接二极管及其工装", FailSemanticType.Measurement);
        Register("8.1", "KL30_FILT_1", "KL30_1(RailFamily)", "先查KL30_1滤波回路/工装", FailSemanticType.Measurement);
        Register("8.1", "KL30_LS_1", "KL30_1(RailFamily)", "先查KL30_1低边回路/工装", FailSemanticType.Measurement);
        Register("8.1", "KL30_HS_1", "KL30_1(RailFamily)", "先查KL30_1高边回路/工装", FailSemanticType.Measurement);
        Register("8.1", "KL30_2", "KL30_2(RailFamily)", "先查KL30_2回路/防反接二极管及其工装", FailSemanticType.Measurement);
        Register("8.1", "KL30_FILT_2", "KL30_2(RailFamily)", "先查KL30_2滤波回路/工装", FailSemanticType.Measurement);
        Register("8.1", "KL30_LS_2", "KL30_2(RailFamily)", "先查KL30_2低边回路/工装", FailSemanticType.Measurement);
        Register("8.1", "KL30_HS_2", "KL30_2(RailFamily)", "先查KL30_2高边回路/工装", FailSemanticType.Measurement);

        // 9. GD 状态数组 (spec §2.3, §9.1)：数组下标=相位映射，同族归并（指向同一驱动/状态链）。
        for (int gd = 0; gd <= 5; gd++)
        {
            Register("9.1", $"BSW_v_GD_Status1_{gd}", "BSW_v_GD_Status(PhaseArray)", "先查栅极驱动状态/相位控制输出", FailSemanticType.Measurement);
            Register("9.1", $"BSW_v_GD_Status2_{gd}", "BSW_v_GD_Status(PhaseArray)", "先查栅极驱动状态/相位控制输出", FailSemanticType.Measurement);
        }
        for (int fl = 1; fl <= 10; fl++)
        {
            Register("9.1", $"FLTM_v_ErrStateInvOff{fl}", "FLTM_v_ErrStateInvOff(ArrayFamily)", "先查IGBT逆变关闭错误状态链", FailSemanticType.Measurement);
        }

        // 10. 电源轨群补齐 (spec §2.3, §6.1)：各轨独立族（同章节多轨挂＝供电系统性由群挂聚合），不合并以保诊断。
        Register("6.1", "P17V_LV_LS", "P17V_LV_LS", "先查17V电源轨/低边回路", FailSemanticType.Measurement);
        Register("6.1", "P3V3_ANA", "P3V3_ANA", "先查3.3V模拟电源轨", FailSemanticType.Measurement);
        Register("6.1", "P3V3_DIG", "P3V3_DIG", "先查3.3V数字电源轨", FailSemanticType.Measurement);
        Register("6.1", "P3V3_STBY", "P3V3_STBY", "先查3.3V待机电源轨", FailSemanticType.Measurement);
        Register("6.1", "P12V_FB_HS", "P12V_FB_HS", "先查12V回馈高边轨", FailSemanticType.Measurement);
        Register("6.1", "P15V_LVD_LS", "P15V_LVD_LS", "先查15V低压低边轨", FailSemanticType.Measurement);
        Register("6.1", "P1.25V_LVD_Core", "P1.25V_LVD_Core", "先查1.25V低压内核轨", FailSemanticType.Measurement);
    }

    private static void Register(string section, string signalName, string family, string hint, FailSemanticType type)
    {
        var info = new SignalFamilyInfo
        {
            Section = section,
            FamilyName = family,
            RootCauseHint = hint,
            SemanticType = type
        };
        _knownSignals[signalName] = info;
    }

    /// <summary>
    /// 根据信号名查找预设信号族
    /// </summary>
    public static SignalFamilyInfo? LookupSignal(string signalName) => FindKnownSignal(signalName);

    /// <summary>
    /// 根据章节号查询注入型或关联语义信息
    /// </summary>
    public static SignalFamilyInfo? LookupBySection(string section)
    {
        if (string.IsNullOrWhiteSpace(section)) return null;
        var s = section.Trim();
        if (IsInjectionSection(s))
        {
            return new SignalFamilyInfo
            {
                Section = s,
                FamilyName = $"{s} FaultInjection",
                RootCauseHint = GetSectionRootCause(s),
                SemanticType = FailSemanticType.Injection
            };
        }
        return null;
    }

    public static SignalFamilyInfo? FindKnownSignal(string signalName)
    {
        if (string.IsNullOrWhiteSpace(signalName)) return null;
        if (_knownSignals.TryGetValue(signalName.Trim(), out var info)) return info;

        // 正则模糊匹配变体 (例如 RES_v_ResAng(45°) 带空格或不同括号)
        if (signalName.Contains("RES_v_ResAng", StringComparison.OrdinalIgnoreCase))
        {
            return new SignalFamilyInfo
            {
                Section = "8.11",
                FamilyName = "RES_v_ResAng(AngleFamily)",
                RootCauseHint = "先查旋变仿真器/接线工装",
                SemanticType = FailSemanticType.Measurement
            };
        }
        if (signalName.Contains("SiC_G_", StringComparison.OrdinalIgnoreCase))
        {
            return new SignalFamilyInfo
            {
                Section = "8.18",
                FamilyName = "SiC_G_GateDrive",
                RootCauseHint = "先查栅极驱动芯片/供电回路",
                SemanticType = FailSemanticType.Measurement
            };
        }
        if (signalName.Contains("TC_AI_Cur_", StringComparison.OrdinalIgnoreCase))
        {
            return new SignalFamilyInfo
            {
                Section = "8.9",
                FamilyName = "TC_AI_Cur",
                RootCauseHint = "先查三相电流传感器/接触工装",
                SemanticType = FailSemanticType.Measurement
            };
        }
        if (signalName.Contains("IGBTTM_v_IgbtT", StringComparison.OrdinalIgnoreCase))
        {
            return new SignalFamilyInfo
            {
                Section = "8.7",
                FamilyName = "IGBTTM_v_IgbtT(PhaseFamily)",
                RootCauseHint = "先查NTC仿真电阻链(493.4Ω)",
                SemanticType = FailSemanticType.Measurement
            };
        }
        // KL30 双轨变体回落：先判 2 号轨（其下无 KL30_1 子串），再判 1 号轨，保证两轨独立不并。
        if (signalName.Contains("KL30_FILT_2", StringComparison.OrdinalIgnoreCase)
            || signalName.Contains("KL30_2", StringComparison.OrdinalIgnoreCase)
            || signalName.Contains("KL30_HS_2", StringComparison.OrdinalIgnoreCase)
            || signalName.Contains("KL30_LS_2", StringComparison.OrdinalIgnoreCase))
        {
            return new SignalFamilyInfo
            {
                Section = "8.1",
                FamilyName = "KL30_2(RailFamily)",
                RootCauseHint = "先查KL30_2回路/防反接二极管及其工装",
                SemanticType = FailSemanticType.Measurement
            };
        }
        if (signalName.Contains("KL30_FILT_1", StringComparison.OrdinalIgnoreCase)
            || signalName.Contains("KL30_1", StringComparison.OrdinalIgnoreCase)
            || signalName.Contains("KL30_HS_1", StringComparison.OrdinalIgnoreCase)
            || signalName.Contains("KL30_LS_1", StringComparison.OrdinalIgnoreCase))
        {
            return new SignalFamilyInfo
            {
                Section = "8.1",
                FamilyName = "KL30_1(RailFamily)",
                RootCauseHint = "先查KL30_1回路/防反接二极管及其工装",
                SemanticType = FailSemanticType.Measurement
            };
        }
        // GD 状态 / FLTM 错误状态数组：下标=相位映射，同族归并（覆盖 [0] 方括号等变体写法）。
        if (signalName.Contains("BSW_v_GD_Status", StringComparison.OrdinalIgnoreCase))
        {
            return new SignalFamilyInfo
            {
                Section = "9.1",
                FamilyName = "BSW_v_GD_Status(PhaseArray)",
                RootCauseHint = "先查栅极驱动状态/相位控制输出",
                SemanticType = FailSemanticType.Measurement
            };
        }
        if (signalName.Contains("FLTM_v_ErrStateInvOff", StringComparison.OrdinalIgnoreCase))
        {
            return new SignalFamilyInfo
            {
                Section = "9.1",
                FamilyName = "FLTM_v_ErrStateInvOff(ArrayFamily)",
                RootCauseHint = "先查IGBT逆变关闭错误状态链",
                SemanticType = FailSemanticType.Measurement
            };
        }

        return null;
    }

    /// <summary>
    /// 获取章节关联的推荐治具排查提示
    /// </summary>
    public static string GetSectionRootCause(string section)
    {
        if (string.IsNullOrWhiteSpace(section)) return "";
        var secTrim = section.Trim();
        if (_sectionRootCauses.TryGetValue(secTrim, out var hint)) return hint;

        // 按主章号降级匹配 (如 6.1.1.2 -> 6.1)
        var parts = secTrim.Split('.');
        if (parts.Length >= 2)
        {
            var major = $"{parts[0]}.{parts[1]}";
            if (_sectionRootCauses.TryGetValue(major, out var majorHint)) return majorHint;
        }

        return "";
    }

    /// <summary>
    /// 判断章节是否为故意注入型故障测试 (排除出群挂报警)
    /// </summary>
    public static bool IsInjectionSection(string section)
    {
        if (string.IsNullOrWhiteSpace(section)) return false;
        var s = section.Trim();
        return s.StartsWith("8.19") || s.StartsWith("8.20") || s.StartsWith("8.21");
    }
}

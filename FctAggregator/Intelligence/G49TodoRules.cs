using System.Text.RegularExpressions;

namespace FctAggregator;

/// <summary>
/// G49 spec 驱动的待办"原理级"合并规则（v3.25.0，依据《G4.9 FCT Specification of PCBA》v3.0 深读）。
///
/// 与 <see cref="TodoGrouping.KeyOf"/>（token 相似度归一化）的本质区别：
///   本规则按 spec 章节/测试原理归并 —— 同一被测对象的不同写法（DMM 测点名 / 接口名 / XCP 变量名）
///   归同一张待办卡，同原理同工装的相序/通道/角标变体也归并；不同原理绝不混卡。
///
/// 合并键格式：g49:{章节}:{家族}[:{信号基名}]
///   · 相序/通道剥离：SiC_G_HU..LW → SiC_G；IGBTTM_v_IgbtTU/V/W → 一张；CURMV_v_CurL1/2/3V → 一张
///   · 别名归并（spec §6.1 括号映射 + §9.1 附录）：SBC_KL30_FILT_1 = BSW_v_Kl30_HS = KL30_FILT_1 → 一张
///   · 电源轨按"轨"分卡（不同稳压器不互并，保住诊断信息），P15V_SiC 高/低侧、N4V 负压各自一张
///
/// 未命中任何规则返回 null，调用方回落 <see cref="TodoGrouping.KeyOf"/>（非 G49 产品/自由文本不受影响）。
/// </summary>
public static class G49TodoRules
{
    private sealed record Rule(string Pattern, string Section, string Family);

    /// <summary>电源轨别名组（§6.1）：base = 规范信号名，aliases = 该轨的所有已知写法。</summary>
    private static readonly (string Base, string Pattern)[] PowerRailAliases =
    {
        ("KL30_FILT_1",  @"SBC_KL30_FILT_1|BSW_v_Kl30_HS\b|KL30_HS_FB|KL30_FILT_1\b"),
        ("KL30_FILT_2",  @"BSW_v_Kl30_LS\b|KL30_FILT_2\b|KL30_Filt2"),
        ("KL30_LS_UC",   @"BSW_v_Kl30UC|KL30_LS_UC"),
        ("KL30_INV",     @"BSW_v_Kl30\b(?!U)|KL30_INV"),
        ("KL30_HS_FB",   @"KL30_HS_FB"),
        ("P17V_LV_LS",   @"BSW_v_P17V_LV_LS|P17V_LV_LS"),
        ("P5V_LVX_LS",   @"BSW_v_P5V_LVX_LS|P5V_LVX_LS"),
        ("P1.25V_Core",  @"BSW_v_LVD_Core|P1\.25V_LVD_Core|P1\.25V\b"),
        ("P5V_LVD_UC",   @"BSW_v_LVD_UC|P5V_LVD_UC"),
        ("P6.5V_SBC",    @"BSW_v_LV_SbcPre|P6\.5V_LVX_SBC"),
        ("P5V_CAN_SBC",  @"BSW_v_LVD_CAN_SBC|P5V_CAN_SBC"),
        ("P5V_CAN",      @"BSW_v_LVD_CAN\b|P5V_CAN\b(?!_SBC)"),
        ("P5V_LVA_Ref",  @"P5V_LVA_Ref"),
        ("P5V_CalCAN",   @"P5V_LVD_CalCAN"),
        ("P5V_LVA_Tra",  @"BSW_v_LVA_Tra|P5V_LVA_Tra|LVDCM_v_P5VTraFilt"),
        ("P5V_LVD_ASC",  @"BSW_v_LV_ASC|P5V_LVD_ASC"),
        ("P5V_LVX_AI",   @"BSW_v_LVA_AI5VPs|P5V_LVX_AI"),
        ("P3V3_LVD_AI",  @"BSW_v_LVA_AI3V3Ps|P3V3_LVD_AI"),
        ("P3V3_Vflex",   @"BSW_v_P3V3_LVD_Vflex|P3V3_LVD_Vflex"),
        ("P12V_FB_HS",   @"LVDCM_v_LvDc_GD_FBFilt|P12V_FB_HS"),
        ("P15V_LVD_LS",  @"LVDCM_v_LvDc_GD_BBFilt|P15V_LVD_LS|P16V_Res"),
        ("Ref_V7P",      @"Ref_V7P"),
        ("Ref_P2V5",     @"Ref_P2V5"),
        ("PHY_VDDO",     @"P3V3_PHY_VDDO"),
        ("PHY_VDD33",    @"P3V3_PHY_VDD33"),
        ("PHY_AVDD",     @"P1V2_PHY_AVDD"),
        ("PHY_DVDD",     @"P0V75_PHY_DVDD"),
        ("VREF_SiC",     @"VREF_H[UVW]|VREF_L[UVW]|P5V_HV\b"),
        ("P15V_SiC_H",   @"P15V_SiC_H[UVW]"),
        ("P15V_SiC_L",   @"P15V_SiC_L[UVW]"),
        ("N4V_SiC_H",    @"N4V_SiC_H[UVW]"),
        ("N4V_SiC_L",    @"N4V_SiC_L[UVW]"),
    };

    /// <summary>功能测试规则（§5/§7/§8）：顺序即优先级，首条命中即返回。</summary>
    private static readonly Rule[] Rules =
    {
        // ── 静态/电源类 ──
        new(@"HVIL",                                                        "5.1",  "HvilLoop"),
        new(@"BSW_v_Kl30|KL30_FILT|KL30_HS|KL30_LS|KL30_INV|SBC_KL30",      "6.1",  "PowerRail"),   // 兜底未列出轨
        new(@"P\d+V[._]|P\dV\d|Ref_[VP]\d|VREF_",                           "6.1",  "PowerRail"),
        // ── 通信类 ──
        new(@"v_EthTrcv_88Q2220|linkStatus|\.sqi\b|Ethernet",               "7.2",  "EthComm"),
        new(@"(?<![A-Za-z])CAN(?![a-z0-9])|PT\s*CAN|PD\s*CAN|CAN\s*(communication|message|ID)", "7.1", "CanComm"),
        // ── 软件/硬件校验 ──
        new(@"APPLICATION_VERSIONID|c_BootLoader|c_BootManager|Sloader_SwVerNum|BSW_v(t|b)?_C[bD]HwVer", "8.2", "SwVersion"),
        new(@"BSW_vb_HwCfg",                                                "8.3",  "HwCfg"),
        new(@"KL15_KC|StateKl15Fnl",                                        "8.4",  "Kl15Wake"),
        new(@"Crash_5V|BSW_v_T(High|Low)Crash|/Crash",                      "8.5",  "CrashPwm"),
        new(@"Adc_v_TC_AI_ADCT_Test\d+",                                    "8.6",  "AdcSelf"),
        // ── 温度/环境传感 ──
        new(@"IGBTTM_v_IgbtT[UVW]",                                         "8.7",  "SicTemp"),
        new(@"TAI_v_Humi",                                                  "8.8",  "AmbientHumidity"),
        new(@"TAI_v_AirPress",                                              "8.8",  "AmbientAirPress"),
        new(@"CBTM_v_CbT[12]",                                              "8.8",  "AmbientPcbTemp"),
        new(@"EMTM_v_EmT1",                                                 "8.10", "EmNtc"),
        // ── 电流采样（§8.9：XCP 值/诊断值/DMM 测点同链） ──
        new(@"CURM[VD]_v_CurL[123][VD]|TC_AI_Cur_[123]",                    "8.9",  "CurrentSensor"),
        // ── 旋变（§8.11：励磁波形/四角解码/SIN-COS 同一仿真器链） ──
        new(@"RES_v_ResAng|TC_AI_Res[DE]_|BSW_v_PosSen_|ResE_AO_Exc",       "8.11", "Resolver"),
        // ── 配电保护 ──
        new(@"eFuse_[AB]|TC_AI_eFuse",                                      "8.12", "EFuse"),
        new(@"EOP_PWM|EopPwmFb|TC_DI_EOP_PWM",                              "8.13", "EopPwm"),
        new(@"RelayHSD_Output|RelayLSD_Output|TC_AI_Relay_output|TC_AI_IS_HSD|BSW_v_Relay_(output|IS_HSD)", "8.14", "RelayDriver"),
        new(@"Valve_Coil[12]|TC_AI_Valve|TC_AI_IS_Valve_HSD|BSW_v_Valve_?", "8.15", "ValveDriver"),
        new(@"BSW_v_HvDcRelayRaw[12]|BCHVDCM_v_HvDcRelay[12]|RLY_ADC[12]",  "8.16", "RelayAdc"),
        new(@"HVDCM_v_HvDc\b|BSW_vb_HV_OV_Fb|TC_DI_HV_OVP|R1735",           "8.17", "HvMeasure"),
        // ── 栅极驱动（§8.18） ──
        new(@"TC_PWM_[HL][UVW]_FB|Icu_Parm\.PWM_FB_Info",                   "8.18", "GateDrivePwmFb"),
        new(@"SiC_G_[HL][UVW]|SiC_S_[HL][UVW]",                             "8.18", "GateDrive"),
        // ── 注入型（§8.19~8.21） ──
        new(@"SiC_D_[HL][UVW]|FLTM_vb_FltSts|BSW_vb_GD_Flt[HL]|BSW_v_GD_Status2\[|DESAT", "8.19", "DesatInjection"),
        new(@"TC_DI_SI[12]_FB|BSW_vb_Io_DI_SI[12]|ASC[HL]H?_REQ|TC_SPO_REQ|TC_DI_Toggle_FB|FL3_SpdDutyFb|Toggle_in|Sbc_Fs0bAssert", "8.20", "AscSpoInjection"),
        new(@"BSW_vb_NMI_ESR1_Flt",                                         "8.21", "SbcFault"),
    };

    private static readonly Regex StepNoStripper = new(@"^\s*\d+(?:\.\d+){1,4}\s+", RegexOptions.Compiled);

    /// <summary>
    /// spec 规则合并键。命中返回 "g49:{章节}:{家族}[:{信号基名]}"；未命中返回 null（调用方回落 token 归并）。
    /// </summary>
    public static string? Resolve(string? failItem)
    {
        if (string.IsNullOrWhiteSpace(failItem)) return null;
        var s = failItem.Trim();
        s = StepNoStripper.Replace(s, "");           // 去开头章节号（8.18.2.2 SiC_G_HU ... → SiC_G_HU ...）
        if (s.Length == 0) return null;

        // 1) 电源轨别名组优先（先于通用 CAN/电源正则，防 P5V_CAN 误入 CAN 通信族）
        foreach (var (baseName, pattern) in PowerRailAliases)
            if (Regex.IsMatch(s, pattern, RegexOptions.IgnoreCase))
                return $"g49:6.1:PowerRail:{baseName}";

        // 2) 功能测试规则表
        foreach (var r in Rules)
            if (Regex.IsMatch(s, r.Pattern, RegexOptions.IgnoreCase))
                return $"g49:{r.Section}:{r.Family}";

        return null;
    }
}

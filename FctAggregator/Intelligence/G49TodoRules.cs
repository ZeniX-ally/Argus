using System.Text.RegularExpressions;

namespace FctAggregator;

public static class G49TodoRules
{
    private sealed record Rule(string Pattern, string Section, string Family);

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

    private static readonly Rule[] Rules =
    {
        new(@"HVIL",                                                        "5.1",  "HvilLoop"),
        new(@"BSW_v_Kl30|KL30_FILT|KL30_HS|KL30_LS|KL30_INV|SBC_KL30",      "6.1",  "PowerRail"),
        new(@"P\d+V[._]|P\dV\d|Ref_[VP]\d|VREF_",                           "6.1",  "PowerRail"),
        new(@"v_EthTrcv_88Q2220|linkStatus|\.sqi\b|Ethernet",               "7.2",  "EthComm"),
        new(@"(?<![A-Za-z])CAN(?![a-z0-9])|PT\s*CAN|PD\s*CAN|CAN\s*(communication|message|ID)", "7.1", "CanComm"),
        new(@"APPLICATION_VERSIONID|c_BootLoader|c_BootManager|Sloader_SwVerNum|BSW_v(t|b)?_C[bD]HwVer", "8.2", "SwVersion"),
        new(@"BSW_vb_HwCfg",                                                "8.3",  "HwCfg"),
        new(@"KL15_KC|StateKl15Fnl",                                        "8.4",  "Kl15Wake"),
        new(@"Crash_5V|BSW_v_T(High|Low)Crash|/Crash",                      "8.5",  "CrashPwm"),
        new(@"Adc_v_TC_AI_ADCT_Test\d+",                                    "8.6",  "AdcSelf"),
        new(@"IGBTTM_v_IgbtT[UVW]",                                         "8.7",  "SicTemp"),
        new(@"TAI_v_Humi",                                                  "8.8",  "AmbientHumidity"),
        new(@"TAI_v_AirPress",                                              "8.8",  "AmbientAirPress"),
        new(@"CBTM_v_CbT[12]",                                              "8.8",  "AmbientPcbTemp"),
        new(@"EMTM_v_EmT1",                                                 "8.10", "EmNtc"),
        new(@"CURM[VD]_v_CurL[123][VD]|TC_AI_Cur_[123]",                    "8.9",  "CurrentSensor"),
        new(@"RES_v_ResAng|TC_AI_Res[DE]_|BSW_v_PosSen_|ResE_AO_Exc",       "8.11", "Resolver"),
        new(@"eFuse_[AB]|TC_AI_eFuse",                                      "8.12", "EFuse"),
        new(@"EOP_PWM|EopPwmFb|TC_DI_EOP_PWM",                              "8.13", "EopPwm"),
        new(@"RelayHSD_Output|RelayLSD_Output|TC_AI_Relay_output|TC_AI_IS_HSD|BSW_v_Relay_(output|IS_HSD)", "8.14", "RelayDriver"),
        new(@"Valve_Coil[12]|TC_AI_Valve|TC_AI_IS_Valve_HSD|BSW_v_Valve_?", "8.15", "ValveDriver"),
        new(@"BSW_v_HvDcRelayRaw[12]|BCHVDCM_v_HvDcRelay[12]|RLY_ADC[12]",  "8.16", "RelayAdc"),
        new(@"HVDCM_v_HvDc\b|BSW_vb_HV_OV_Fb|TC_DI_HV_OVP|R1735",           "8.17", "HvMeasure"),
        new(@"TC_PWM_[HL][UVW]_FB|Icu_Parm\.PWM_FB_Info",                   "8.18", "GateDrivePwmFb"),
        new(@"SiC_G_[HL][UVW]|SiC_S_[HL][UVW]",                             "8.18", "GateDrive"),
        new(@"SiC_D_[HL][UVW]|FLTM_vb_FltSts|BSW_vb_GD_Flt[HL]|BSW_v_GD_Status2\[|DESAT", "8.19", "DesatInjection"),
        new(@"TC_DI_SI[12]_FB|BSW_vb_Io_DI_SI[12]|ASC[HL]H?_REQ|TC_SPO_REQ|TC_DI_Toggle_FB|FL3_SpdDutyFb|Toggle_in|Sbc_Fs0bAssert", "8.20", "AscSpoInjection"),
        new(@"BSW_vb_NMI_ESR1_Flt",                                         "8.21", "SbcFault"),
    };

    private static readonly Regex StepNoStripper = new(@"^\s*\d+(?:\.\d+){1,4}\s+", RegexOptions.Compiled);

    public static string? Resolve(string? failItem)
    {
        if (string.IsNullOrWhiteSpace(failItem)) return null;
        var s = failItem.Trim();
        s = StepNoStripper.Replace(s, "");
        if (s.Length == 0) return null;

        foreach (var (baseName, pattern) in PowerRailAliases)
            if (Regex.IsMatch(s, pattern, RegexOptions.IgnoreCase))
                return $"g49:6.1:PowerRail:{baseName}";

        foreach (var r in Rules)
            if (Regex.IsMatch(s, r.Pattern, RegexOptions.IgnoreCase))
                return $"g49:{r.Section}:{r.Family}";

        return null;
    }
}

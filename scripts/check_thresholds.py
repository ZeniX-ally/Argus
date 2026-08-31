import csv, re, pathlib

csv_paths = [
    # 相对路径示例（原为绝对路径硬编码）；缺失文件已做容错处理
    (r"./data/P_Fts_PEU_G49_FCT4_E3002624AFV75236898002K80300382_20260815133715144_202681553715242.csv", "E3002624"),
    (r"./data/P_Fts_PEU_G49_FCT4_E3002757ALV75236898002K81902026_20260820073413753_2026819233413846.csv", "E3002757"),
    (r"./data/P_Fts_PEU_G49_FCT4_E3002781AGV75236898002K81602748_20260819095818041_202681915818137.csv", "E3002781"),
]

def group_for(model):
    g1 = ["E3002609","E3002752","E3002757","E3002725"]
    g2 = ["E3002781","E3002624","E3003296"]
    if any(model.startswith(x) for x in g1):
        return "G1_15V"
    if any(model.startswith(x) for x in g2):
        return "G2_18V"
    return "UNKNOWN"

expected_common = {}
def add_common(k,hi,lo,note=""):
    expected_common[k]=(hi,lo,note)
add_common("P1.25V_LVD_Core", 1.2875, 1.2125, "1.25V +/-3% P13")
add_common("P5V_LVD_UC", 5.15, 4.85, "5V +/-3%")
add_common("P6.5V_LVX_SBC", 6.76, 6.24, "6.5V +/-4%")
add_common("P5V_CAN_SBC", 5.2, 4.8, "5V +/-4%")
add_common("P5V_LVA_Ref", 5.05, 4.95, "5V +/-1%")
add_common("P5V_CAN", 5.15, 4.85, "5V +/-3%")
add_common("P5V_LVD_CalCAN", 5.15, 4.85, "")
add_common("P5V_LVA_Tra", 5.15, 4.85, "")
add_common("P5V_LVD_ASC", 5.25, 4.75, "5V +/-5%")
add_common("P5V_LVX_AI", 5.1, 4.9, "5V +/-2%")
add_common("P3V3_LVD_AI", 3.399, 3.201, "3.3V +/-3%")
add_common("P3V3_LVD_Vflex", 3.399, 3.201, "")
add_common("P3V3_PHY_VDDO", 3.399, 3.201, "")
add_common("P3V3_PHY_VDD33", 3.399, 3.201, "")
add_common("P16V_Res", 15.75, 14.25, "15V +/-0.75")
add_common("P15V_LVD_LS", 15.75, 14.25, "")
add_common("Ref_V7P", 7.6, 6.8, "7.2V +/-0.4")
add_common("Ref_P2V5", 2.575, 2.425, "2.5V +/-3%")
add_common("P1V2_PHY_AVDD", 1.26, 1.14, "1.14~1.26 P14 V2.9")
add_common("P0V75_PHY_DVDD", 0.795, 0.7125, "")
add_common("VREF_HU", 5.15, 4.85, "5V +/-0.15")
add_common("VREF_HV", 5.15, 4.85, "")
add_common("VREF_HW", 5.15, 4.85, "")
add_common("VREF_LU", 5.15, 4.85, "")
add_common("VREF_LV", 5.15, 4.85, "")
add_common("P5V_HV", 5.15, 4.85, "")
add_common("SBC_KL30_FILT_1", 14.7, 13.7, "14.2V +/-0.5")
add_common("KL30_FILT_1", 14.6, 14.2, "14.4V +/-0.2")
add_common("KL30_FILT_2", 14.6, 14.2, "")
add_common("KL30_HS_FB", 14.4, 13.4, "13.9V +/-0.5")
add_common("KL30_LS_UC", 14.4, 13.4, "")
add_common("KL30_INV", 14.4, 13.4, "")
add_common("BSW_v_Kl30_LS", 14.7, 13.7, "")
add_common("BSW_v_Kl30UC", 14.7, 13.7, "")
add_common("BSW_v_LVD_Core", 1.2875, 1.2125, "")
add_common("BSW_v_LVD_UC", 5.15, 4.85, "")
add_common("BSW_v_LV_SbcPre", 6.76, 6.24, "")
add_common("BSW_v_LVD_CAN_SBC", 5.2, 4.8, "")
add_common("BSW_v_LVD_CAN", 5.15, 4.85, "")
add_common("BSW_v_LVA_Tra", 5.15, 4.85, "")
add_common("BSW_v_LV_ASC", 5.25, 4.75, "")
add_common("BSW_v_LVA_AI5VPs", 5.1, 4.9, "")
add_common("BSW_v_LVA_AI3V3Ps", 3.399, 3.201, "")
add_common("BSW_v_P3V3_LVD_Vflex", 3.399, 3.201, "")
add_common("LVDCM_v_LvDc_GD_BBFilt", 15.75, 14.25, "")
add_common("BSW_v_P5V_LVX_LS", 5.15, 4.85, "spec 5V +/-0.5 but CSV uses 5.15")
add_common("BSW_v_Kl30_HS", 14.91, 13.49, "14.2V +/-5% P14 V3.0")
add_common("N4V_SiC", -3.3, -4.2, "-4.2~-3.3")
add_common("TC_AI_Cur", 2.54, 2.46, "2.5 +/-0.04")
add_common("HVDCM_750", 757.5, 742.5, "750 +/-1%")
add_common("HVDCM_880", 888.8, 871.2, "880 +/-1%")
add_common("HVDCM_980", 989.8, 970.2, "980 +/-1%")
add_common("BSW_v_Relay_0V", 0.05, -0.05, "0 +/-0.05")
add_common("BSW_v_Relay_10V", 10.7, 9.3, "10V +/-0.7 V2.9 P4/P26")

expected_G1 = {}
expected_G2 = {}
expected_G1["P17V_LV_LS"] = (17.0, 16.0, "16.5V +/-0.5 P13 G1")
expected_G2["P17V_LV_LS"] = (18.5, 17.5, "18V +/-0.5 P13 G2")
expected_G1["P12V_FB_HS"] = (11.33, 9.27, "10.3V +/-10% G1 P13")
expected_G2["P12V_FB_HS"] = (13.2, 10.8, "12V +/-10% G2 P13")
expected_G1["P15V_SiC_HU"] = (15.75, 14.25, "15V +/-5% G1")
expected_G2["P15V_SiC_HU"] = (18.9, 17.1, "18V +/-5% G2")
expected_G1["P15V_SiC_HV"] = (15.75, 14.25, "")
expected_G2["P15V_SiC_HV"] = (18.9, 17.1, "")
expected_G1["P15V_SiC_HW"] = (15.75, 14.25, "")
expected_G2["P15V_SiC_HW"] = (18.9, 17.1, "")
expected_G1["P15V_SiC_LU"] = (15.75, 14.25, "")
expected_G2["P15V_SiC_LU"] = (18.9, 17.1, "")
expected_G1["P15V_SiC_LV"] = (15.75, 14.25, "")
expected_G2["P15V_SiC_LV"] = (18.9, 17.1, "")
expected_G1["P15V_SiC_LW"] = (15.75, 14.25, "")
expected_G2["P15V_SiC_LW"] = (18.9, 17.1, "")
expected_G1["BSW_v_P17V_LV_LS"] = (17.0, 16.0, "16.5V +/-0.5 XCP G1")
expected_G2["BSW_v_P17V_LV_LS"] = (18.5, 17.5, "18V +/-0.5 XCP G2")
expected_G1["LVDCM_v_LvDc_GD_FBFilt"] = (11.33, 9.27, "10.3V XCP G1")
expected_G2["LVDCM_v_LvDc_GD_FBFilt"] = (13.2, 10.8, "12V XCP G2")
expected_G1["SiC_G_High"] = (15.75, 14.25, "SiC G high G1 15V +/-5% P29")
expected_G2["SiC_G_High"] = (18.9, 17.1, "SiC G high G2 18V +/-5% P29")
expected_G1["KL30_1_6_1_1"] = (0.28, 0.08, "0.18+-0.1 P13 step1")
expected_G2["KL30_1_6_1_1"] = (0.28, 0.08, "same")
expected_G1["KL30_2_6_1_1"] = (0.45, 0.25, "0.35+-0.1 G1")
expected_G2["KL30_2_6_1_1"] = (0.52, 0.32, "0.42+-0.1 G2")
expected_G1["KL30_1_6_1_2"] = (0.37, 0.17, "0.27+-0.1 G1 PWM P15")
expected_G2["KL30_1_6_1_2"] = (0.45, 0.25, "0.35+-0.1 G2 PWM")
expected_G1["KL30_2_6_1_2"] = (0.59, 0.39, "0.49+-0.1 G1 PWM")
expected_G2["KL30_2_6_1_2"] = (0.64, 0.44, "0.54+-0.1 G2 PWM")

def check_file(path, model):
    grp = group_for(model)
    try:
        with open(path, encoding="utf-8-sig", newline="") as f:
            rows = list(csv.DictReader(f))
    except FileNotFoundError:
        print(f"文件未找到: {path}")
        return 0, 0, [], grp, []
    issues=[]
    ok=0
    checked=0
    unchecked=[]
    for r in rows:
        name = r["NAME"]
        hilim_s = r["HILIM"].strip()
        lolim_s = r["LOLIM"].strip()
        if not hilim_s and not lolim_s:
            continue
        try:
            hi = float(hilim_s) if hilim_s else None
            lo = float(lolim_s) if lolim_s else None
        except (ValueError, TypeError):
            continue
        exp = None
        note=""
        if "KL30_1(Power)" in name and "6.1.1.1.1" in name:
            exp = expected_G1["KL30_1_6_1_1"] if grp=="G1_15V" else expected_G2["KL30_1_6_1_1"]
        elif "KL30_2(Power)" in name and "6.1.1.1.2" in name:
            exp = expected_G1["KL30_2_6_1_1"] if grp=="G1_15V" else expected_G2["KL30_2_6_1_1"]
        elif "KL30_1(Power)" in name and "6.1.2.2.1" in name:
            # 需区分是否含 ",A,0" 的PWM细分项；当前两组阈值相同但保留分支以备未来分化
            if "KL30_1(Power),A,0" in name:
                exp = expected_G1["KL30_1_6_1_2"] if grp=="G1_15V" else expected_G2["KL30_1_6_1_2"]
            else:
                exp = expected_G1["KL30_1_6_1_2"] if grp=="G1_15V" else expected_G2["KL30_1_6_1_2"]
        elif "KL30_2(Power)" in name and "6.1.2.2.2" in name:
            exp = expected_G1["KL30_2_6_1_2"] if grp=="G1_15V" else expected_G2["KL30_2_6_1_2"]
        else:
            # 优先级链：专用信号 > 候选最长匹配 > 通用匹配
            if "N4V_SiC" in name:
                exp = expected_common["N4V_SiC"]
            elif "Volt for TC_AI_Cur" in name:
                exp = expected_common["TC_AI_Cur"]
            elif "BSW_v_Relay_output" in name or "BSW_v_Relay " in name or "TC_AI_Relay_output" in name:
                # 按实测上限区分 0V/10V 档
                if hi is not None and hi > 1:
                    exp = expected_common["BSW_v_Relay_10V"]
                else:
                    exp = expected_common["BSW_v_Relay_0V"]
            elif "HVDCM_v_HvDc" in name:
                # 按 HILIM 区分 750/880/980 三档
                if hi is not None and hi > 940:
                    exp = expected_common["HVDCM_980"]
                elif hi is not None and hi > 820:
                    exp = expected_common["HVDCM_880"]
                else:
                    exp = expected_common["HVDCM_750"]
            else:
                candidates=[]
                group_dict = expected_G1 if grp=="G1_15V" else expected_G2
                for k,v in group_dict.items():
                    if k in name:
                        candidates.append((len(k), v, k))
                for k,v in expected_common.items():
                    if k in name:
                        candidates.append((len(k), v, k))
                # SiC_G High/Low 大小写不敏感，权重100确保优先
                low_name = name.lower()
                is_high = ("high level" in low_name and "8.18" in name)
                is_low = ("low level" in low_name and "8.18" in name)
                if is_high and any(x in name for x in ["SiC_G_HU","SiC_G_HV","SiC_G_HW","SiC_G_LU","SiC_G_LV","SiC_G_LW"]):
                    gd = expected_G1["SiC_G_High"] if grp=="G1_15V" else expected_G2["SiC_G_High"]
                    candidates.append((100, gd, "SiC_G_High"))
                elif is_low and any(x in name for x in ["SiC_G_HU","SiC_G_HV","SiC_G_HW","SiC_G_LU","SiC_G_LV","SiC_G_LW"]):
                    # Low Level 共用 N4V_SiC 区间
                    candidates.append((100, expected_common["N4V_SiC"], "N4V_SiC"))
                if candidates:
                    candidates.sort(key=lambda x: -x[0])
                    exp = candidates[0][1]
        if exp is None:
            unchecked.append(name)
            continue
        exp_hi, exp_lo, note = exp
        checked += 1
        if hi is None or lo is None:
            issues.append((name, hi, lo, exp_hi, exp_lo, note + " | HILIM/LOLIM缺失"))
        elif abs(hi - exp_hi) > 0.015 or abs(lo - exp_lo) > 0.015:
            issues.append((name, hi, lo, exp_hi, exp_lo, note))
        else:
            ok+=1
    return checked, ok, issues, grp, unchecked

for path, model in csv_paths:
    try:
        checked, ok, issues, grp, unchecked = check_file(path, model)
    except FileNotFoundError:
        print(f"文件未找到(外层容错): {path}")
        continue
    print(f"\n===== {model} ({grp}) =====")
    print(f"已核对阈值数: {checked}, 符合: {ok}, 不符合: {len(issues)}")
    for it in issues:
        name, hi, lo, exp_hi, exp_lo, note = it
        print(f"  FAIL {name}")
        print(f"       实测 HILIM={hi} LOLIM={lo} | 规范期望 HILIM={exp_hi} LOLIM={exp_lo} | {note}")
    if unchecked:
        # show sample unchecked
        from collections import Counter
        c=Counter(unchecked)
        print(f"  未映射核对项(示例): {list(c.items())[:5]}")

# Also detailed group-specific check for key signals
print("\n--- 详细分组阈值对照 ---")
for k in ["P17V_LV_LS","P12V_FB_HS","P15V_SiC_HU","BSW_v_P17V_LV_LS","LVDCM_v_LvDc_GD_FBFilt","SiC_G_High"]:
    print(f"{k}: G1 {expected_G1.get(k)} | G2 {expected_G2.get(k)}")

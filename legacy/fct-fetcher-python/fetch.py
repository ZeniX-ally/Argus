#!/usr/bin/env python3
"""FCT 文件捞取器 (fct-fetcher)

逻辑:
  1. 完整扫描 Results 根目录 (结构: {Online|Offline}/{型号E+7位}/{yyyyMMdd}/*.xml)
  2. 按日期区间过滤 (依据目录日期, 含首尾两天)
  3. 判定 FAIL 并提取 SN
  4. 按 SN 同步捞取对应的 CSV 与 TDMS

FAIL 判定规则 (与 fct-fail-ranker/XmlScanner.cs 保持一致):
  - 文件名前缀 P_ / F_ / O_ 三种; P_ 直接跳过(不可能是 fail)
  - F_ 恒为 FAIL
  - O_ (中断) 仅当含真实失败项时算 FAIL, 否则为 INTERRUPTED
  - 失败项排除 "Get Unit Information" 与 "UUT Status Err" (非产品真实不良)
  - FACTORY/@USER == "debug" 的记录跳过

用法:
  python fetch.py --start 20260722 --end 20260724
  python fetch.py --start 2026-07-22 --end 2026-07-24 --copy
  python fetch.py --start 20260722 --end 20260724 --config myconf.json
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
import xml.etree.ElementTree as ET
from collections import Counter
from datetime import datetime
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")

MODEL_RE = re.compile(r"^E\d{7}$")
DATE_DIR_RE = re.compile(r"^\d{8}$")
STATION_RE = re.compile(r"^FCT\d+$", re.IGNORECASE)

# 非产品真实不良, 不计入失败项
IGNORED_FAIL_STEPS = {"get unit information", "uut status err"}

HERE = Path(__file__).resolve().parent


# ---------------- 配置 ----------------
def load_config(path: Path) -> dict:
    cfg = {
        "results_root": "D:/Results",
        "csv_root": "",
        "tdms_root": "",
        "match_mode": "contains",
        "output_dir": str(HERE / "out"),
        "copy_files": False,
        "include_o_fails": True,
        "skip_debug": True,
        "categories": ["Online", "Offline"],
    }
    if path.exists():
        with open(path, encoding="utf-8") as f:
            user = json.load(f)
        cfg.update({k: v for k, v in user.items() if not k.startswith("_")})
    return cfg


def parse_date(s: str) -> datetime:
    s = s.strip().replace("-", "").replace("/", "").replace(".", "")
    if not DATE_DIR_RE.match(s):
        raise argparse.ArgumentTypeError(f"日期格式应为 yyyyMMdd 或 yyyy-MM-dd, 收到: {s}")
    return datetime.strptime(s, "%Y%m%d")


# ---------------- 路径与文件名解析 ----------------
class Rec:
    __slots__ = ("path", "filename", "category", "model", "date", "prefix", "sn",
                 "station", "user", "timestamp", "result", "fail_items")

    def __init__(self):
        self.fail_items: list[dict] = []
        self.result = ""
        self.sn = self.station = self.user = self.timestamp = ""


def parse_path(p: Path, categories: list[str]) -> Rec | None:
    """校验 {Category}/{Model}/{yyyyMMdd}/{file} 结构。大小写不敏感(比 C# 版更宽松)。"""
    parts = p.parts
    if len(parts) < 4:
        return None
    filename, date_s, model, category = parts[-1], parts[-2], parts[-3], parts[-4]

    cat_map = {c.lower(): c for c in categories}
    if category.lower() not in cat_map:
        return None
    if not MODEL_RE.match(model):
        return None
    if not DATE_DIR_RE.match(date_s):
        return None

    stem = filename[:-4] if filename.lower().endswith(".xml") else filename
    stem = stem.replace("(debug)", "").replace("(Debug)", "")
    prefix = filename[:2].upper()
    if prefix not in ("P_", "F_", "O_"):
        return None

    r = Rec()
    r.path, r.filename = p, filename
    r.category, r.model, r.date, r.prefix = cat_map[category.lower()], model, date_s, prefix

    seg = stem.split("_")
    # 文件名: 前缀_Fts_PEU_G49_{机台}_{SN}_{ts1}_{ts2}
    si = next((i for i, s in enumerate(seg) if STATION_RE.match(s)), -1)
    if si >= 0:
        r.station = seg[si]
        if si + 1 < len(seg):
            r.sn = seg[si + 1]
    elif len(seg) >= 6:
        r.sn = seg[5]
    return r


def parse_xml(r: Rec) -> None:
    """解析 XML, 填充 sn/user/station/timestamp/fail_items/result。

    只负责解析与定级, 不做"是否采纳"的策略判断(那属于 scan)。
    result 取值: FAIL / INTERRUPTED / PARSE_ERROR
    """
    try:
        root = ET.parse(r.path).getroot()
    except Exception:
        r.result = "PARSE_ERROR"
        return

    # 根元素本身即 BATCH
    if root.tag == "BATCH":
        r.timestamp = root.get("TIMESTAMP", "")
    else:
        b = root.find(".//BATCH")
        if b is not None:
            r.timestamp = b.get("TIMESTAMP", "")

    fac = root.find(".//FACTORY")
    if fac is not None:
        r.user = fac.get("USER", "")
        tester = fac.get("TESTER", "")
        if tester and not r.station:
            m = re.search(r"FCT\d+", tester, re.IGNORECASE)
            if m:
                r.station = m.group(0)

    dut = root.find(".//DUT")
    if dut is not None:
        did = dut.get("ID", "")
        if did:
            r.sn = did          # DUT/@ID 是权威 SN(文件名可能被截断)

    for t in root.iter("TEST"):
        if t.get("STATUS") != "Failed":
            continue
        name = t.get("NAME", "")
        if name.strip().lower() in IGNORED_FAIL_STEPS:
            continue
        r.fail_items.append({
            "name": name,
            "value": t.get("VALUE", ""),
            "lolim": t.get("LOLIM", ""),
            "hilim": t.get("HILIM", ""),
            "unit": t.get("UNIT", ""),
        })

    if r.prefix == "F_":
        r.result = "FAIL"          # F_ 恒为 FAIL
    else:                          # O_ 中断: 有真实失败项才算 FAIL
        r.result = "FAIL" if r.fail_items else "INTERRUPTED"


# ---------------- 扫描 ----------------
def scan(cfg: dict, start: datetime, end: datetime):
    root = Path(cfg["results_root"])
    if not root.exists():
        print(f"[错误] Results 根目录不存在: {root}")
        return [], {}

    all_xml = list(root.rglob("*.xml"))
    stats = Counter()
    stats["xml_total"] = len(all_xml)
    print(f"扫描 {root} -> 共 {len(all_xml)} 个 XML")

    recs = []
    for p in all_xml:
        info = parse_path(p, cfg["categories"])
        if info is None:
            stats["skip_badpath"] += 1
            continue
        d = datetime.strptime(info.date, "%Y%m%d")
        if d < start or d > end:
            stats["skip_range"] += 1
            continue
        stats["in_range"] += 1
        if info.prefix == "P_":
            stats["skip_pass"] += 1
            continue
        if info.prefix == "O_" and not cfg.get("include_o_fails", True):
            stats["skip_o_excluded"] += 1
            continue

        parse_xml(info)

        if info.result == "PARSE_ERROR":
            stats["skip_parse_error"] += 1
            continue
        if cfg["skip_debug"] and info.user.strip().lower() == "debug":
            stats["skip_debug"] += 1
            continue
        if info.result == "INTERRUPTED":
            stats["skip_interrupted"] += 1
            continue
        recs.append(info)
        stats["fail"] += 1
    return recs, stats


# ---------------- 按 SN 捞取 CSV / TDMS ----------------
def build_index(root_str: str, ext: str) -> dict:
    """预先索引一次目标目录, 避免每个 SN 都全盘 rglob。"""
    if not root_str:
        return {}
    root = Path(root_str)
    if not root.exists():
        print(f"[警告] {ext} 根目录不存在, 跳过: {root}")
        return {}
    idx = {}
    for p in root.rglob(f"*{ext}"):
        idx.setdefault(p.stem.upper(), []).append(p)
        idx.setdefault("__ALL__", []).append(p)
    n = len(idx.get("__ALL__", []))
    print(f"索引 {ext}: {root} -> {n} 个文件")
    return idx


def find_by_sn(idx: dict, sn: str, mode: str) -> list[Path]:
    if not idx or not sn:
        return []
    up = sn.upper()
    if mode == "exact":
        return list(idx.get(up, []))
    out = []
    for p in idx.get("__ALL__", []):
        stem = p.stem.upper()
        if mode == "prefix":
            if stem.startswith(up):
                out.append(p)
        else:  # contains
            if up in stem:
                out.append(p)
    return out


# ---------------- 主流程 ----------------
def main():
    ap = argparse.ArgumentParser(description="FCT fail 文件捞取器")
    ap.add_argument("--start", required=True, type=parse_date, help="起始日期 yyyyMMdd")
    ap.add_argument("--end", required=True, type=parse_date, help="结束日期 yyyyMMdd (含当天)")
    ap.add_argument("--config", default=str(HERE / "config.json"))
    ap.add_argument("--results-root", help="覆盖配置里的 results_root")
    ap.add_argument("--copy", action="store_true", help="把命中的文件复制到 output_dir")
    ap.add_argument("--out", help="覆盖配置里的 output_dir")
    a = ap.parse_args()

    if a.start > a.end:
        print("[错误] start 晚于 end")
        return 2

    cfg = load_config(Path(a.config))
    if a.results_root:
        cfg["results_root"] = a.results_root
    if a.out:
        cfg["output_dir"] = a.out
    if a.copy:
        cfg["copy_files"] = True

    print(f"日期区间: {a.start:%Y-%m-%d} ~ {a.end:%Y-%m-%d} (含首尾)")
    recs, stats = scan(cfg, a.start, a.end)
    if not recs:
        print("\n未捞到任何 FAIL 记录。统计:", dict(stats))
        return 1

    csv_idx = build_index(cfg["csv_root"], ".csv")
    tdms_idx = build_index(cfg["tdms_root"], ".tdms")

    mode = cfg["match_mode"]
    rows = []
    sn_seen = set()
    for r in recs:
        csvs = find_by_sn(csv_idx, r.sn, mode)
        tdms = find_by_sn(tdms_idx, r.sn, mode)
        sn_seen.add(r.sn)
        rows.append({
            "SN": r.sn,
            "结果": r.result,
            "站点": r.station,
            "型号": r.model,
            "日期": r.date,
            "类别": r.category,
            "前缀": r.prefix,
            "失败项数": len(r.fail_items),
            "失败项": " | ".join(f"{i['name']}={i['value']}[{i['lolim']}~{i['hilim']}{i['unit']}]"
                                for i in r.fail_items),
            "XML": str(r.path),
            "CSV数": len(csvs),
            "CSV": " | ".join(str(x) for x in csvs),
            "TDMS数": len(tdms),
            "TDMS": " | ".join(str(x) for x in tdms),
            "测试时间": r.timestamp,
            "USER": r.user,
        })

    outdir = Path(cfg["output_dir"])
    outdir.mkdir(parents=True, exist_ok=True)
    tag = f"{a.start:%Y%m%d}-{a.end:%Y%m%d}"

    try:
        import pandas as pd
        df = pd.DataFrame(rows)
        xlsx = outdir / f"fetch_{tag}.xlsx"
        with pd.ExcelWriter(xlsx, engine="openpyxl") as w:
            df.to_excel(w, sheet_name="捞取清单", index=False)
            fi = []
            for r in recs:
                for it in r.fail_items:
                    fi.append({"SN": r.sn, "站点": r.station, "日期": r.date,
                               "失败项": it["name"], "测量值": it["value"],
                               "下限": it["lolim"], "上限": it["hilim"], "单位": it["unit"]})
            if fi:
                pd.DataFrame(fi).to_excel(w, sheet_name="失败项明细", index=False)
                top = (pd.DataFrame(fi).groupby("失败项")
                       .agg(次数=("失败项", "size"), 影响SN数=("SN", "nunique"))
                       .sort_values("次数", ascending=False).reset_index())
                top.to_excel(w, sheet_name="失败项排名", index=False)
            for name, ws in w.sheets.items():
                ws.freeze_panes = "A2"
        print(f"\n清单已输出: {xlsx}")
    except ImportError:
        import csv as csvmod
        out_csv = outdir / f"fetch_{tag}.csv"
        with open(out_csv, "w", newline="", encoding="utf-8-sig") as f:
            wr = csvmod.DictWriter(f, fieldnames=list(rows[0].keys()))
            wr.writeheader()
            wr.writerows(rows)
        print(f"\n清单已输出(无 pandas, 降级 CSV): {out_csv}")

    if cfg["copy_files"]:
        n = 0
        for r, row in zip(recs, rows):
            dst = outdir / "files" / r.sn
            dst.mkdir(parents=True, exist_ok=True)
            for src in [r.path] + \
                       [Path(x) for x in row["CSV"].split(" | ") if x] + \
                       [Path(x) for x in row["TDMS"].split(" | ") if x]:
                try:
                    shutil.copy2(src, dst / src.name)
                    n += 1
                except Exception as e:
                    print(f"  [复制失败] {src}: {type(e).__name__}")
        print(f"已复制 {n} 个文件 -> {outdir / 'files'}")

    # ---- 汇总 ----
    print("\n" + "=" * 60)
    print(f"扫描 XML 总数      : {stats['xml_total']}")
    print(f"  路径不合规跳过   : {stats['skip_badpath']}")
    print(f"  日期区间外       : {stats['skip_range']}")
    print(f"  区间内           : {stats['in_range']}")
    print(f"    P_ 直接跳过    : {stats['skip_pass']}")
    if stats['skip_o_excluded']:
        print(f"    O_ 按配置排除  : {stats['skip_o_excluded']}")
    print(f"    INTERRUPTED    : {stats['skip_interrupted']}  (O_ 无真实失败项)")
    print(f"    debug 跳过     : {stats['skip_debug']}")
    if stats['skip_parse_error']:
        print(f"    XML 解析失败    : {stats['skip_parse_error']}")
    print(f"  >> FAIL 命中     : {stats['fail']}  (去重 SN {len(sn_seen)} 个)")
    print("=" * 60)
    hit_csv = sum(1 for r in rows if r["CSV数"] > 0)
    hit_tdms = sum(1 for r in rows if r["TDMS数"] > 0)
    print(f"CSV  命中: {hit_csv}/{len(rows)} 条记录" +
          ("   [csv_root 未配置]" if not cfg["csv_root"] else ""))
    print(f"TDMS 命中: {hit_tdms}/{len(rows)} 条记录" +
          ("   [tdms_root 未配置]" if not cfg["tdms_root"] else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())

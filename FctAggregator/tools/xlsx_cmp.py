"""
对比两个目录下同名 xlsx 的「长相」是否一致（v2.9.3 xlsx 写出器去重的回归验收）。

比什么：
  1. xl/styles.xml               —— 调色板（字体/填充/边框/数字格式/cellXfs）逐字节
  2. 每张表的 <cols> 列宽        —— 列宽
  3. 每张表的 <pane> 冻结行      —— 冻结
  4. 每张表的 <mergeCells>       —— 合并单元格
  5. 每个单元格的 (值, 样式号)   —— 内容 + 套的是哪个样式
  6. 工作表名与顺序

用法: python xlsx_cmp.py <old目录> <new目录>
退出码 0 = 完全一致
"""
import sys, os, zipfile, re
import xml.etree.ElementTree as ET

NS = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'

def norm(s):
    """忽略无意义空白差异"""
    return re.sub(r'\s+', ' ', (s or '')).strip()

def sheets_of(z):
    """[(name, part)]，按 workbook 顺序"""
    wb = ET.fromstring(z.read('xl/workbook.xml'))
    rels = ET.fromstring(z.read('xl/_rels/workbook.xml.rels'))
    rid2tgt = {r.get('Id'): r.get('Target') for r in rels}
    out = []
    for i, sh in enumerate(wb.find(NS + 'sheets')):
        rid = sh.get('{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id')
        tgt = rid2tgt.get(rid, 'worksheets/sheet%d.xml' % (i + 1))
        out.append((sh.get('name'), 'xl/' + tgt.lstrip('/')))
    return out

def parse_sheet(xml):
    root = ET.fromstring(xml)
    cols = [(c.get('min'), c.get('max'), c.get('width')) for c in root.iter(NS + 'col')]
    panes = [(p.get('ySplit'), p.get('topLeftCell'), p.get('state')) for p in root.iter(NS + 'pane')]
    merges = [m.get('ref') for m in root.iter(NS + 'mergeCell')]
    cells = {}
    for c in root.iter(NS + 'c'):
        ref = c.get('r')
        style = c.get('s') or '0'
        v = c.find(NS + 'v')
        if v is not None:
            val = norm(v.text)
        else:
            t = c.find(NS + 'is/' + NS + 't')
            val = norm(t.text) if t is not None else ''
        if val == '' and style == '0':
            continue
        cells[ref] = (val, style)
    return cols, panes, merges, cells

def cmp_file(a, b, name, diffs):
    with zipfile.ZipFile(a) as za, zipfile.ZipFile(b) as zb:
        sa, sb = za.read('xl/styles.xml').decode('utf-8'), zb.read('xl/styles.xml').decode('utf-8')
        if sa != sb:
            diffs.append('%s: styles.xml 不一致（调色板变了！）长度 %d -> %d' % (name, len(sa), len(sb)))
            for tag in ('font', 'fill', 'border', 'numFmt', 'xf'):
                ca, cb = sa.count('<' + tag), sb.count('<' + tag)
                if ca != cb:
                    diffs.append('    <%s> 个数 %d -> %d' % (tag, ca, cb))
        la, lb = sheets_of(za), sheets_of(zb)
        if [x[0] for x in la] != [x[0] for x in lb]:
            diffs.append('%s: 工作表名/顺序不一致 %s -> %s' % (name, [x[0] for x in la], [x[0] for x in lb]))
            return
        for (sn, pa), (_, pb) in zip(la, lb):
            ca, pna, ma, cla = parse_sheet(za.read(pa))
            cb, pnb, mb, clb = parse_sheet(zb.read(pb))
            if ca != cb:
                diffs.append('%s[%s]: 列宽不一致\n    old=%s\n    new=%s' % (name, sn, ca, cb))
            if pna != pnb:
                diffs.append('%s[%s]: 冻结行不一致 %s -> %s' % (name, sn, pna, pnb))
            if ma != mb:
                diffs.append('%s[%s]: 合并单元格不一致 %s -> %s' % (name, sn, ma, mb))
            keys = set(cla) | set(clb)
            bad = 0
            for k in sorted(keys, key=lambda r: (int(re.sub(r'\D', '', r) or 0), r)):
                va, vb = cla.get(k), clb.get(k)
                if va == vb:
                    continue
                bad += 1
                if bad <= 8:
                    diffs.append('%s[%s]!%s: (值,样式) %s -> %s' % (name, sn, k, va, vb))
            if bad > 8:
                diffs.append('%s[%s]: 另有 %d 处单元格差异未列出' % (name, sn, bad - 8))

def main():
    old, new = sys.argv[1], sys.argv[2]
    fa = {f for f in os.listdir(old) if f.lower().endswith('.xlsx') and not f.startswith('~$')}
    fb = {f for f in os.listdir(new) if f.lower().endswith('.xlsx') and not f.startswith('~$')}
    diffs = []
    if fa != fb:
        diffs.append('文件集合不同: 只在 old=%s 只在 new=%s' % (sorted(fa - fb), sorted(fb - fa)))
    print('=' * 72)
    print('xlsx 新旧对比：old=%s  new=%s' % (old, new))
    print('=' * 72)
    for f in sorted(fa & fb):
        pa, pb = os.path.join(old, f), os.path.join(new, f)
        before = len(diffs)
        cmp_file(pa, pb, f, diffs)
        sa, sb = os.path.getsize(pa), os.path.getsize(pb)
        flag = 'OK  ' if len(diffs) == before else 'DIFF'
        print('[%s] %-32s %8d -> %8d 字节' % (flag, f, sa, sb))
    print('-' * 72)
    if diffs:
        print('发现 %d 处差异：' % len(diffs))
        for d in diffs:
            print('  * ' + d)
        return 1
    print('四张表的调色板、列宽、冻结、合并、每个单元格的值与样式号 —— 全部一致。')
    return 0

if __name__ == '__main__':
    sys.exit(main())

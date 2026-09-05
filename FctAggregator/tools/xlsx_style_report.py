"""把 xlsx 的 styles.xml 翻成人话，便于核对配色（我看不见 Excel，用这个描述给人对照）。

用法: python xlsx_style_report.py <目录或xlsx...>
"""
import sys, os, zipfile
import xml.etree.ElementTree as ET

NS = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'
ALIGN = {'center': '居中', 'left': '左', 'right': '右', 'justify': '两端'}

def txt_color(el):
    if el is None:
        return '-'
    c = el.find(NS + 'color')
    if c is None:
        return '默认'
    if c.get('rgb'):
        return '#' + c.get('rgb')[-6:]
    if c.get('theme'):
        return '主题%s' % c.get('theme')
    return '默认'

def kids(parent, tag):
    """取子元素列表（避免对 Element 直接做真值判断，3.14 会警告）"""
    el = parent.find(NS + tag)
    return list(el) if el is not None else []

def report(path):
    with zipfile.ZipFile(path) as z:
        st = ET.fromstring(z.read('xl/styles.xml'))
        fonts, fills, borders, fmts = [], [], [], {}
        for f in kids(st, 'fonts'):
            sz = f.find(NS + 'sz')
            nm = f.find(NS + 'name')
            fonts.append('%s %s%s%s 色%s' % (
                nm.get('val') if nm is not None else '默认字体',
                sz.get('val') if sz is not None else '?',
                ' 粗' if f.find(NS + 'b') is not None else '',
                ' 斜' if f.find(NS + 'i') is not None else '',
                txt_color(f)))
        for f in kids(st, 'fills'):
            pf = f.find(NS + 'patternFill')
            if pf is None:
                fills.append('无')
                continue
            fg = pf.find(NS + 'fgColor')
            rgb = fg.get('rgb') if fg is not None and fg.get('rgb') else None
            fills.append('%s%s' % (pf.get('patternType') or '无',
                                   ' #' + rgb[-6:] if rgb else ''))
        for b in kids(st, 'borders'):
            sides = [s.tag.replace(NS, '') for s in b
                     if s.get('style') or s.find(NS + 'color') is not None]
            borders.append(','.join(sides) if sides else '无')
        nf = st.find(NS + 'numFmts')
        if nf is not None:
            for n in nf:
                fmts[n.get('numFmtId')] = n.get('formatCode')

        print('=' * 78)
        print(os.path.basename(path))
        print('=' * 78)
        sheets = ET.fromstring(z.read('xl/workbook.xml')).find(NS + 'sheets')
        print('工作表: ' + ' | '.join(s.get('name') for s in sheets))
        cx = kids(st, 'cellXfs')
        print('样式号  字体                                底色           边框            对齐   数字格式')
        for i, xf in enumerate(cx):
            al = xf.find(NS + 'alignment')
            a = ''
            if al is not None:
                a += ALIGN.get(al.get('horizontal'), al.get('horizontal') or '')
                if al.get('wrapText') == '1':
                    a += '+换行'
            fid = int(xf.get('fontId') or 0)
            flid = int(xf.get('fillId') or 0)
            bid = int(xf.get('borderId') or 0)
            nfid = xf.get('numFmtId') or '0'
            print('%4d    %-34s %-14s %-15s %-6s %s' % (
                i,
                fonts[fid] if fid < len(fonts) else '?',
                fills[flid] if flid < len(fills) else '?',
                borders[bid] if bid < len(borders) else '?',
                a or '-',
                fmts.get(nfid, '' if nfid == '0' else 'id' + nfid)))
        print()

def main():
    args = sys.argv[1:] or ['.']
    files = []
    for a in args:
        if os.path.isdir(a):
            files += [os.path.join(a, f) for f in sorted(os.listdir(a))
                      if f.endswith('.xlsx') and not f.startswith('~$')]
        else:
            files.append(a)
    for f in files:
        report(f)

if __name__ == '__main__':
    main()

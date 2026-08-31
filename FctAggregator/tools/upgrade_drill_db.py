"""升级演练用：打印 sqlite 库里每张表的行数（JSON 一行）。

用法: python upgrade_drill_db.py <db>
"""
import json
import sqlite3
import sys

db = sys.argv[1]
c = sqlite3.connect('file:' + db + '?mode=ro', uri=True)
out = {}
for (t,) in c.execute("select name from sqlite_master where type='table' and name not like 'sqlite_%'"):
    out[t] = c.execute('select count(*) from "%s"' % t).fetchone()[0]
print(json.dumps(out, ensure_ascii=False, sort_keys=True))

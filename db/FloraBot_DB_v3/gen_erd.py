"""Sinh ERD Mermaid từ CSDL đang chạy + chú thích '-- ref schema.table' trong 01_schema.sql.
OVERVIEW=1 -> florabot_erd_overview.mmd (chỉ cột khóa)."""
import re, subprocess, sys
import os
PSQL = ["psql", "-h", "/tmp", "-U", "postgres", "-d", os.environ.get("DB","florabot"), "-At", "-F", "\t", "-c"]
def q(sql): return [l.split("\t") for l in subprocess.check_output(PSQL + [sql], text=True).strip().splitlines() if l]
SCHEMAS = "('identity','catalog','kiosk_ops','ordering','payment','notify','ai')"
cols = q(f"""SELECT c.table_schema, c.table_name, c.column_name,
  CASE WHEN c.data_type='ARRAY' THEN replace(c.udt_name,'_','')||'_arr' WHEN c.data_type LIKE 'timestamp%' THEN 'timestamptz' WHEN c.data_type='character varying' THEN 'text' ELSE replace(c.data_type,' ','_') END,
  c.is_nullable FROM information_schema.columns c JOIN information_schema.tables t USING (table_schema, table_name)
  WHERE t.table_type='BASE TABLE' AND c.table_schema IN {SCHEMAS} ORDER BY c.table_schema, c.table_name, c.ordinal_position""")
pk = {(r[0], r[1], r[2]) for r in q(f"""SELECT kcu.table_schema, kcu.table_name, kcu.column_name FROM information_schema.table_constraints tc
  JOIN information_schema.key_column_usage kcu USING (constraint_schema, constraint_name) WHERE tc.constraint_type='PRIMARY KEY' AND tc.table_schema IN {SCHEMAS}""")}
uq = {(r[0], r[1], r[2]) for r in q(f"""SELECT n.nspname, c.relname, a.attname FROM pg_index i JOIN pg_class c ON c.oid=i.indrelid JOIN pg_namespace n ON n.oid=c.relnamespace
  JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum = i.indkey[0] WHERE i.indisunique AND NOT i.indisprimary AND i.indnatts=1 AND n.nspname IN {SCHEMAS}""")}
fks = q(f"""SELECT n.nspname, c.relname, a.attname, fn.nspname, fc.relname FROM pg_constraint k JOIN pg_class c ON c.oid=k.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace
  JOIN pg_class fc ON fc.oid=k.confrelid JOIN pg_namespace fn ON fn.oid=fc.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum=k.conkey[1]
  WHERE k.contype='f' AND n.nspname IN {SCHEMAS}""")
# tham chiếu logic khác service từ chú thích
refs, cur = [], None
for line in open("01_schema.sql", encoding="utf8"):
    m = re.match(r"CREATE TABLE (\w+)\.(\w+)", line)
    if m: cur = m.groups(); continue
    m = re.match(r"\s+(\w+)\s+uuid.*--\s*ref ([\w.]+)", line)
    if m and cur:
        for tgt in re.findall(r"(\w+)\.(\w+)", m.group(2) + " " + line.split("ref",1)[1])[:1]:
            refs.append((cur[0], cur[1], m.group(1), tgt[0], tgt[1]))
nullable = {(r[0], r[1], r[2]): r[4] == "YES" for r in cols}
fkcols = {(a, b, c) for a, b, c, *_ in fks} | {(a, b, c) for a, b, c, *_ in refs}
SVC = {"identity":"identity","catalog":"catalog","kiosk_ops":"kiosk-ops","ordering":"ordering","payment":"payment","notify":"notify-media","ai":"ai"}
ntab = len({(a, b) for a, b, *_ in cols})
OVERVIEW = os.environ.get("OVERVIEW") == "1"
out = ["---", f"title: FloraBot ERD {'tổng quan' if OVERVIEW else 'logical'} v3.1 (MVP{', chỉ cột khóa' if OVERVIEW else ''}) — {ntab} bảng / 7 service (nét liền = FK trong service, nét đứt = tham chiếu logic khác service; bảng cap.published/received do CAP tự sinh, không tính)", "---", "erDiagram"]
cur = None
for s, t, c, typ, nl in cols:
    if (s, t) != cur:
        if cur: out.append("  }")
        out.append(f'  {t}["{t} ({SVC[s]})"] {{'); cur = (s, t)
    keys = [k for k, ok in (("PK", (s,t,c) in pk), ("FK", (s,t,c) in fkcols), ("UK", (s,t,c) in uq)) if ok]
    if OVERVIEW and not keys: continue
    note = ' "NOT NULL"' if nl == "NO" and (s,t,c) not in pk else ""
    out.append(f"    {typ} {c}{' ' + ','.join(keys) if keys else ''}{note}")
out.append("  }")
def rel(s, t, c, ts, tt, dashed):
    child_many = "|o" if (s,t,c) in uq else "o{"
    parent = "o|" if nullable[(s,t,c)] else "||"
    return f'  {tt} {parent}{".." if dashed else "--"}{child_many} {t} : "{c}"'
seen = set()
for s,t,c,ts,tt in fks: out.append(rel(s,t,c,ts,tt,False)); seen.add((t,c))
for s,t,c,ts,tt in refs:
    if (t,c) not in seen: out.append(rel(s,t,c,ts,tt,True))
open("florabot_erd_overview.mmd" if OVERVIEW else "florabot_erd.mmd","w",encoding="utf8").write("\n".join(out)+"\n")
print(len({(a,b) for a,b,*_ in cols}), "bảng,", len(fks), "FK,", len(refs), "tham chiếu logic")

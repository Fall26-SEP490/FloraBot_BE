"""Generate Mermaid ERDs from the base SQL schema plus ordered migrations.

This source-only fallback does not require or modify a running database.
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DB_DIR = ROOT / "db" / "FloraBot_DB_v3"
BASE_SCHEMA = DB_DIR / "01_schema.sql"
MIGRATIONS = ROOT / "db" / "migrations"
DOMAINS = {
    "identity": "identity", "catalog": "catalog", "kiosk_ops": "kiosk-ops",
    "ordering": "ordering", "payment": "payment", "notify": "notify", "ai": "ai",
}


@dataclass
class Column:
    name: str
    sql_type: str
    nullable: bool = True
    pk: bool = False
    unique: bool = False
    fk: bool = False


@dataclass
class Table:
    schema: str
    name: str
    columns: dict[str, Column] = field(default_factory=dict)
    pk_columns: set[str] = field(default_factory=set)


def matching_paren(text: str, open_at: int) -> int:
    depth, quote, i = 0, None, open_at
    while i < len(text):
        char = text[i]
        if quote:
            if quote == "'" and char == "'" and i + 1 < len(text) and text[i + 1] == "'":
                i += 2
                continue
            if char == quote:
                quote = None
        elif char in "'\"":
            quote = char
        elif char == "-" and i + 1 < len(text) and text[i + 1] == "-":
            end = text.find("\n", i)
            i = len(text) if end < 0 else end
        elif char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    raise ValueError("Unclosed table definition")


def split_top_level(text: str) -> list[str]:
    pieces, start, depth, quote, i = [], 0, 0, None, 0
    while i < len(text):
        char = text[i]
        if quote:
            if quote == "'" and char == "'" and i + 1 < len(text) and text[i + 1] == "'":
                i += 2
                continue
            if char == quote:
                quote = None
        elif char in "'\"":
            quote = char
        elif char == "-" and i + 1 < len(text) and text[i + 1] == "-":
            end = text.find("\n", i)
            i = len(text) if end < 0 else end
        elif char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
        elif char == "," and depth == 0:
            pieces.append(text[start:i].strip())
            start = i + 1
        i += 1
    if text[start:].strip():
        pieces.append(text[start:].strip())
    return pieces


def strip_line_comments(text: str) -> str:
    result, quote, i = [], None, 0
    while i < len(text):
        char = text[i]
        if quote:
            result.append(char)
            if quote == "'" and char == "'" and i + 1 < len(text) and text[i + 1] == "'":
                result.append(text[i + 1])
                i += 2
                continue
            if char == quote:
                quote = None
        elif char in "'\"":
            quote = char
            result.append(char)
        elif char == "-" and i + 1 < len(text) and text[i + 1] == "-":
            end = text.find("\n", i)
            if end < 0:
                break
            result.append("\n")
            i = end
        else:
            result.append(char)
        i += 1
    return "".join(result)


def normalize_type(raw: str) -> str:
    value = re.sub(r"\s+", " ", raw.strip().lower())
    value = re.sub(r"\s*\([^)]*\)", "", value)
    value = {
        "timestamp with time zone": "timestamptz",
        "timestamp without time zone": "timestamp",
        "character varying": "text", "varchar": "text",
        "boolean": "bool", "character": "text",
    }.get(value, value)
    return re.sub(r"\s+", "_", value).replace("[]", "_arr")


TYPE = (
    r"timestamp\s+(?:with|without)\s+time\s+zone|double\s+precision|"
    r"character\s+varying(?:\s*\([^)]*\))?|varchar(?:\s*\([^)]*\))?|"
    r"numeric(?:\s*\([^)]*\))?|decimal(?:\s*\([^)]*\))?|"
    r"[A-Za-z_][\w$]*(?:\s*\([^)]*\))?(?:\[\])?"
)


def add_column(table: Table, definition: str) -> None:
    definition = definition.split("--", 1)[0].strip()
    match = re.match(r'"?([A-Za-z_][\w$]*)"?\s+(.+)$', definition, re.S)
    if not match:
        return
    name, rest = match.groups()
    if name.upper() in {"CONSTRAINT", "PRIMARY", "FOREIGN", "UNIQUE", "CHECK", "EXCLUDE"}:
        return
    type_match = re.match(rf"({TYPE})", rest, re.I)
    if not type_match:
        raise ValueError(f"Could not parse type for {table.schema}.{table.name}.{name}: {rest}")
    col = table.columns.get(name) or Column(name, normalize_type(type_match.group(1)))
    col.sql_type = normalize_type(type_match.group(1))
    col.nullable = not bool(re.search(r"\bNOT\s+NULL\b", rest, re.I))
    col.pk |= bool(re.search(r"\bPRIMARY\s+KEY\b", rest, re.I))
    col.unique |= bool(re.search(r"\bUNIQUE\b", rest, re.I))
    col.fk |= bool(re.search(r"\bREFERENCES\b", rest, re.I))
    table.columns[name] = col


def parse_create_tables(sql: str, tables: dict[tuple[str, str], Table]) -> None:
    pattern = re.compile(
        r"(?im)^\s*CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?([\w]+)\.([\w]+)\s*\("
    )
    for match in pattern.finditer(sql):
        key = (match.group(1).lower(), match.group(2).lower())
        end = matching_paren(sql, match.end() - 1)
        table = tables.setdefault(key, Table(*key))
        for definition in split_top_level(strip_line_comments(sql[match.end():end])):
            cleaned = definition.strip()
            primary = re.search(r"\bPRIMARY\s+KEY\s*\(([^)]+)\)", cleaned, re.I)
            if primary:
                table.pk_columns.update(x.strip().strip('"') for x in primary.group(1).split(","))
            add_column(table, definition)
            col_match = re.match(r'"?([\w$]+)"?\s+', cleaned)
            if col_match and col_match.group(1) in table.columns:
                if table.columns[col_match.group(1)].pk:
                    table.pk_columns.add(col_match.group(1))


def apply_added_columns(sql: str, tables: dict[tuple[str, str], Table]) -> None:
    alter = re.compile(r"(?is)\bALTER\s+TABLE\s+([\w]+)\.([\w]+)\b(.*?);")
    add = re.compile(rf"(?is)\bADD\s+COLUMN\s+(?:IF\s+NOT\s+EXISTS\s+)?\"?([\w$]+)\"?\s+({TYPE})")
    for match in alter.finditer(sql):
        table = tables.get((match.group(1).lower(), match.group(2).lower()))
        if table is None:
            continue
        matches = list(add.finditer(match.group(3)))
        for index, column_match in enumerate(matches):
            end = matches[index + 1].start() if index + 1 < len(matches) else len(match.group(3))
            rest = match.group(3)[column_match.start():end]
            add_column(table, f"{column_match.group(1)} {column_match.group(2)} {rest}")


def relationships_from(sql_files, tables):
    relationships = set()
    for _, sql in sql_files:
        table_pattern = re.compile(
            r"(?im)^\s*CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(\w+)\.(\w+)\s*\("
        )
        for table_match in table_pattern.finditer(sql):
            source = (table_match.group(1).lower(), table_match.group(2).lower())
            if source not in tables:
                continue
            end = matching_paren(sql, table_match.end() - 1)
            body = sql[table_match.end():end]
            for line in body.splitlines():
                logical = re.search(r"--\s*ref\s+(\w+)\.(\w+)(?:\.(\w+))?", line, re.I)
                if logical:
                    local = re.match(r"\s*(\w+)\s+uuid\b", line[:logical.start()], re.I)
                    if local:
                        relationships.add((source, local.group(1).lower(), (logical.group(1).lower(), logical.group(2).lower()), (logical.group(3) or "id").lower(), True))
            for definition in split_top_level(strip_line_comments(body)):
                clean = definition.strip()
                foreign = re.match(r"(?:CONSTRAINT\s+\w+\s+)?FOREIGN\s+KEY\s*\(\s*(\w+)\s*\)", clean, re.I)
                inline = re.match(r"\s*(\w+)\s+", clean)
                local_col = foreign.group(1) if foreign else inline.group(1) if inline else None
                ref = re.search(r"\bREFERENCES\s+(?:(\w+)\.)?(\w+)\s*\(\s*(\w+)\s*\)", clean, re.I)
                if local_col and ref:
                    target_schema, target_table, target_col = ref.groups()
                    target = (target_schema.lower(), target_table.lower()) if target_schema else (source[0], target_table.lower())
                    relationships.add((source, local_col.lower(), target, target_col.lower(), False))
    return relationships


def generate(overview: bool = False) -> str:
    base = BASE_SCHEMA.read_text(encoding="utf-8")
    migration_paths = sorted(MIGRATIONS.glob("*.sql"))
    source_files = [(BASE_SCHEMA, base)] + [(p, p.read_text(encoding="utf-8")) for p in migration_paths]
    tables: dict[tuple[str, str], Table] = {}
    for _, sql in source_files:
        parse_create_tables(sql, tables)
    for _, sql in source_files[1:]:
        apply_added_columns(sql, tables)
    for table in tables.values():
        for name in table.pk_columns:
            if name in table.columns:
                table.columns[name].pk = True
    relationships = relationships_from(source_files, tables)
    unresolved = [
        (source, col, target, target_col)
        for source, col, target, target_col, _ in relationships
        if source not in tables
        or target not in tables
        or col not in tables[source].columns
        or target_col not in tables[target].columns
    ]
    if unresolved:
        raise ValueError(f"Relationships reference missing tables/columns: {unresolved}")
    for source, col, _, _, _ in relationships:
        if source in tables and col in tables[source].columns:
            tables[source].columns[col].fk = True

    schemas = {schema for schema, _ in tables}
    title = "FloraBot current source ERD overview" if overview else "FloraBot current source ERD"
    out = ["---", f"title: {title} — {len(tables)} tables / {len(schemas)} database schemas; base schema plus {len(migration_paths):02d} migrations", "---", "erDiagram"]
    for schema, name in sorted(tables):
        table = tables[(schema, name)]
        out.append(f'  {name}["{name} ({DOMAINS.get(schema, schema)})"] {{')
        columns = sorted(table.columns.values(), key=lambda col: (not col.pk, not col.fk, col.name))
        for col in columns:
            if overview and not (col.pk or col.fk or col.unique):
                continue
            keys = ",".join(key for key, enabled in (("PK", col.pk), ("FK", col.fk), ("UK", col.unique)) if enabled)
            suffix = f" {keys}" if keys else ""
            note = ' "NOT NULL"' if not col.nullable and not col.pk else ""
            out.append(f"    {col.sql_type} {col.name}{suffix}{note}")
        out.append("  }")

    seen = set()
    for source, col, target, _, is_logical in sorted(relationships):
        if source not in tables or target not in tables or col not in tables[source].columns:
            continue
        child, parent = tables[source], tables[target]
        unique = child.columns[col].unique or col in child.pk_columns
        child_card = "|o" if unique else "o{"
        parent_card = "o|" if child.columns[col].nullable else "||"
        connector = ".." if is_logical or source[0] != target[0] else "--"
        edge = (source, col, target)
        if edge in seen:
            continue
        seen.add(edge)
        out.append(f'  {parent.name} {parent_card}{connector}{child_card} {child.name} : "{col}"')
    return "\n".join(out) + "\n"


if __name__ == "__main__":
    full_path = DB_DIR / "florabot_erd_current.mmd"
    overview_path = DB_DIR / "florabot_erd_current_overview.mmd"
    full_path.write_text(generate(), encoding="utf-8")
    overview_path.write_text(generate(overview=True), encoding="utf-8")
    print(f"Generated {full_path.name} and {overview_path.name} from base schema + {len(list(MIGRATIONS.glob('*.sql')))} migrations")

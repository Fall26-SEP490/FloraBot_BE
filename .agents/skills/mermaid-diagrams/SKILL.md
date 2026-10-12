---
name: mermaid-diagrams
description: Use when creating, updating, or validating Mermaid diagrams from FloraBot source code or requirements, especially ERDs, architecture, and workflow diagrams.
---

# Mermaid diagrams for FloraBot

Start from the project source, not an assumed design. For database diagrams, inspect `db/FloraBot_DB_v3/01_schema.sql` and apply every numbered migration in `db/migrations/`. Do not count schema names as microservices or infer business completeness from table count.

Save Mermaid source as a descriptive `.mmd` file. Group related entities, label relationships, and keep diagrams readable. Split full-detail and overview diagrams when needed. Distinguish source DDL from generated output.

Run `python db/FloraBot_DB_v3/gen_erd_from_source.py` from the repository root to generate the source-based ERDs without connecting to or modifying a database. Validate Mermaid using `mmdc -i <file.mmd>` when Mermaid CLI is installed. If unavailable, report the limitation; do not silently install a global package or weaken validation. Render SVG/PNG when a user requests a rendered image or it helps their deliverable.

Before presenting an ERD, verify entity count against base DDL plus migrations, check relationship endpoints, and explain if the runtime database could not be inspected. An ERD describes stored structure; it does not prove all business requirements are implemented. Mark unsupported flows explicitly.

# 0001: Modular monolith

Context: the source database owns seven business schemas and tested flow functions.
Considered: independent services, or one ASP.NET Core process with seven modules.
Decision: one .NET 10 Minimal API process, one endpoint per exposed business flow.
Module routes are generated from exact supplied SQL signatures, not reimplemented logic.
Internal helpers and clock controls are never published as commands.
EF Core models were scaffolded from the actual PostgreSQL database.
Tenant filters protect queries; authorization and resource checks protect commands.
The original DB archive is preserved byte-for-byte under db/FloraBot_DB_v3.
Runtime corrections are explicit migrations with separate tests.
Consequence: simpler local operations without abandoning schema ownership.

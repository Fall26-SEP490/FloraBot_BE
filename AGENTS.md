# FloraBot backend workspace

These instructions are shared by Codex and Google Antigravity when this repository is opened as a workspace.

## Project map

- This is the FloraBot backend: .NET 10 Minimal API, PostgreSQL 16, Valkey, RabbitMQ, YARP gateway, and Python AI/firmware support.
- The frontend is a separate sibling repository at `../../FE/FloraBot_FE` when both repositories are checked out in the expected layout. Do not assume changes in one repository are committed with the other.
- Read the relevant README and architecture decision records before changing setup, security, payments, authentication, database, MQTT, or deployment behavior.

## Working rules

- Preserve existing security boundaries, tenant checks, authorization rechecks, audit behavior, and secret handling. Never print, commit, or pass credentials in command arguments.
- Treat local credentials and `.env.example` values as development examples only. Do not weaken TLS/certificate checks or authentication to make a local flow pass.
- Do not run database initialization, destructive scripts, deployment, or commands that recreate a database unless the user explicitly requested that operation and its target is clear.
- For API contract changes, update the backend source and its OpenAPI output workflow; the frontend generates its contracts from the exported OpenAPI file and generated types must not be hand-edited.
- Keep changes scoped to the requested task and respect existing repository conventions.

# 0043 - Payment reads through owning modules

Date: 2026-10-08. Status: accepted.
Context: Payment link handlers joined Identity subscriptions and Ordering orders directly.
Decision: Identity exposes pending subscription payment IDs; Ordering exposes authorized checkout snapshots.
Payment reads only its own tables and composes existing response DTOs without changing routes or amounts.
Both module reads share a RepeatableRead transaction to preserve the old single-statement snapshot semantics.
End the read transaction before gateway reservation/remote calls; existing stable gateway codes remain unchanged.
Ordering checks every order for kiosk/customer scope and returns the earliest source-configured payment deadline.
Add an assembly-level test using EF schema/view mappings, compiled member/type references and SQL string operands.
The guard includes generated async/lambda types, generic entity arguments, fields and method signatures.
Allow module-owned DTO/helper calls and source flow functions; reject foreign entity/table access in Modules namespaces.
Limits: this static guard cannot prove dynamically assembled SQL/reflection safety; shared Infrastructure/Auth/Jobs adapters remain outside its scope.
The existing CI dotnet test step runs the guard. Remote CI success is not claimed without a remote run.

# 0047 - Module-owned authorization resource reads

Date: 2026-10-08. Status: accepted.
Context: ResourceAccess built SQL over Identity, Catalog and KioskOps tables in shared infrastructure.
Decision: each owning module exposes narrow resource-owner lookups with fixed SQL.
ResourceAccess retains principal checks, argument order and the same 404/403 responses.
Pass the caller's connection and transaction to every lookup; retain FOR UPDATE locks until its flow commits or rolls back.
Do not open independent connections or authorize from a cache, which could allow ownership to change before mutation.
Extend the compiled boundary guard to ResourceAccess, including nested async code and schema-qualified string fragments.
Verify foreign/missing resources stay indistinguishable and a competing transaction cannot acquire the ownership lock early.
No source SQL, public DTO, policy, business transition or dependency changes.
Limits: other shared Auth/Jobs orchestration and dynamically constructed SQL outside this adapter still need separate review.

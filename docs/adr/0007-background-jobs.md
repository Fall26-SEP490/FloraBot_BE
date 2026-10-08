# Scheduled SQL flows

Use one BackgroundService in the modular monolith rather than introducing another scheduler.
The service runs a fixed allowlist of nine jobs and never accepts SQL names or clocks from HTTP.
Each job uses a Valkey SET NX lease with a random owner and compare-and-delete release.
A PostgreSQL transaction advisory lock additionally prevents overlap after lease expiry or cache restart.
Jobs run in separate transactions, so one failed job rolls back without blocking other jobs in the tick.
SQL uses server time with Asia/Ho_Chi_Minh timezone and resets the source SQL's demo clock.
The default tick is 30 seconds; individual statements have a 20-second server timeout.
Daily reconciliation records yesterday's report in append-only audit within the transaction; repeat ticks skip completed dates.
The audit checkpoint survives cache loss. Historical days missed during extended downtime require explicit backfill.
Tests use a separate jobs database; hosted jobs are off by default in Testing and on elsewhere.

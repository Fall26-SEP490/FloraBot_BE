# 0048 - Authentication, job and realtime data boundaries

Date: 2026-10-08. Status: accepted.
Context: shared authentication queried kiosk entities and the job runner read Notify audit rows directly.
Decision: KioskOps resolves enabled device credentials to a nullable ID; Notify owns daily-job checkpoint reads and writes.
Keep Auth's existing Identity users/sellers access explicitly assigned to Identity ownership in the compiled guard.
This avoids moving public auth types while preventing Auth from reading unrelated business schemas.
Jobs and Realtime are orchestration-only: reject entity references and literal business-table queries there.
Keep the job's caller transaction, Vietnam server date, advisory/cache locks, audit action and payload unchanged.
Keep device hashing, disabled status rejection and JWT/OTP/session contracts; pass request cancellation to the device query.
Tests cover next-request revocation after key rotation/disable and rollback of the checkpoint with its calling transaction.
No source SQL, public API, authorization policy, new dependency or deployment topology change.
Limits: compiled literal/type checks do not prove reflection or arbitrary dynamic SQL safe, and unclassified Infrastructure adapters remain outside this guard.

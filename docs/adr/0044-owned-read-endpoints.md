# 0044 - Module-owned reads and explicit screen projections

Date: 2026-10-08. Status: accepted.
Context: shared ReadEndpoints contained Identity, Catalog and KioskOps EF queries alongside screen reports.
Decision: move each EF read route to its owning module without changing routes, policies, filters or response records.
Keep response record namespaces stable so generated OpenAPI contracts and C# consumers remain compatible.
ReadEndpoints becomes registration only; it delegates to the three module read classes and ScreenReadEndpoints.
The original screen views remain in an explicit ReadModels adapter, retaining source SQL report/catalog semantics.
Extend compiled boundary checks to this adapter: only screen SQL is permitted and literal mutations are rejected.
The routing facade may not reference entities or literal schema queries; ordinary module reads retain their schema checks.
This is a read-model boundary, not a claim that SQL screen views have no cross-schema dependencies.
No original SQL, public API payload, business state transition or deployment topology changes.
Limits: dynamic SQL/reflection plus shared Auth/Jobs/ResourceAccess orchestration still require separate review.

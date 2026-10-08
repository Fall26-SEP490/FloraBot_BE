# 0034 - Consistent administrator reconciliation reports

- Context: `reconcile_gateway` reports only successful PAYOS charges on the requested Vietnam date, but its original audit match count also counted other gateways and dates.
- Evidence: an isolated fixture produced four differences yet counted three matches instead of one before migration 009; the regression passes afterward.
- Decision: preserve the supplied SQL archive and replace only this function in supplemental migration 009. Report rows and audit counts use one materialized transaction-local snapshot.
- Real Kestrel verification exposed a 64 KiB request limit despite the documented 5,000-row input allowance (HTTP 413). Gateway and API now allow up to 8 MiB only for the gateway-reconciliation route; other routes retain 64 KiB. This accommodates bounded UTF-8 transaction IDs without raising the global limit.
- Duplicate statement transaction IDs retain the source's intended sum behavior. Group before insertion to avoid PostgreSQL's multi-conflict INSERT error.
- Payment validates the date and at most 5,000 `{txn, amount}` rows; IDs are bounded and amounts positive bigint integers. Duplicate totals must fit bigint. Extra statement fields are discarded before SQL.
- Missing statement and missing/default date produce HTTP 400. Empty arrays are valid and identify system charges absent from the supplied statement.
- Both Admin-only routes return `Cache-Control: no-store`, including validation and authorization failures. They do not create payments, change ledger entries or call a bank/provider.
- Set the transaction-local timezone to Asia/Ho_Chi_Minh. Notify records manual daily report requests with the authenticated actor, date and returned report in the same transaction.
- Daily report interpretation remains as supplied: three day-specific monetary comparisons, current all-time ledger balance, a count of currently pending refunds older than the selected date minus 48 hours, and current all-time refund clearing. It is not a historical balance snapshot.
- OpenAPI gives these two existing result envelopes typed rows. The admin UI supports date reports and manual JSON statements with review, explicit submission and 20-row difference pagination. Real provider statement verification remains pending.
- Statement input and results stay in component state, outside query/mutation caches and browser storage. Clear/hide/unmount invalidates late responses. No automatic retry is used; a manual retry can add another audit record but never transfers money.
- Browser amounts must be safe positive integers, including per-ID sums. Exact string amounts in reports use bigint formatting; unsafe numeric responses are flagged rather than displayed as exact. Totals in the review use bigint.
- Verification: real PostgreSQL regression covers date/gateway isolation, audit totals, duplicate aggregation at API and SQL layers, malformed input, row limits, non-caching, roles, empty statement and no fabricated payment. Source SQL is unchanged.

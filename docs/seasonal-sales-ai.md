# Seasonal sales AI — seller-first

This increment uses completed, successfully paid local shop sales for seasonal analytics and forecasting. It adds no FE screen and changes no physical kiosk commands. Customer advice retains its existing stock/budget/authorization checks and receives the current Vietnam calendar through the AI service; its cache is scoped to the Vietnam calendar date.

## Public backend contract

Only active ADMIN or the owning active SELLER may read these endpoints. Other tenants are hidden by existing SameSeller authorization; database functions also recheck the actor on each read. Responses use no-store.

| Method | Route | Input / result |
| --- | --- | --- |
| GET | `/api/sellers/{sellerId}/ai/seasonal-sales` | Optional `from`, `to`, `year`, `month`, `season`, `holiday`; measured invoice/unit totals and product metadata/photos |
| GET | `/api/sellers/{sellerId}/ai/sales.csv` | Optional `from`, `to`; UTF-8 order/product/day/quantity export without customer PII; spreadsheet formula escaping |
| POST | `/api/sellers/{sellerId}/ai/seasonal-forecast` | `{ "year": 2026, "month": 11, "historyFrom": "2024-11-01", "historyTo": "2026-10-31" }`; actual counts, predictions, validation metrics, warnings and explanations |

Dates are interpreted in Asia/Ho_Chi_Minh. SPRING=Mar–May; SUMMER=Jun–Aug; AUTUMN=Sep–Nov; WINTER=Dec–Feb. A winter filter with `year=2027` spans December 2026–February 2027. Holiday codes: NEW_YEAR, VALENTINE, WOMENS_DAY, VN_WOMENS_DAY, TEACHERS_DAY, CHRISTMAS. Holiday windows include the seven preceding days and the event day. NEW_YEAR with year=2027 therefore starts on 25 December 2026; Christmas and New Year windows may overlap. Lunar New Year dates are not inferred.

Invalid/conflicting filters return 400. Queries cover at most 1096 days; CSV/report exports are capped at 50,000 rows with an explicit error instead of silently dropping rows. Forecasts cover at most 100 active products. The target month must follow the history end; history cannot extend past the current Vietnam date. POST uses the existing advisor rate-limit policy.

## What is counted

- A sale must have a COMPLETED order and a SUCCEEDED CHARGE with paid_at. Duplicate successful charge rows are collapsed before joining order items.
- Only ACTIVE BOUQUET lines count. Successful refunds are excluded conservatively, matching their own order; another order's refund on a historical shared checkout does not erase this shop's sale.
- Per-product invoices are distinct order IDs; quantity is summed. The shop-wide invoice total is distinct across all products, rather than summing product invoice counts.
- Product IDs bind names, prices, tags and verified HTTPS photo records from the catalog. Same product names in two shops never join together. The model cannot supply image URLs.
- Aggregation level is PRODUCT (bouquet/catalog product). The present schema is not a botanical species/BOM dataset, so this feature makes no species-level accuracy claim.

## Prediction and explanation

The internal authenticated Python endpoint is `POST /seasonal/forecast`, wire contract `seasonal-v1` (snake_case). scikit-learn Poisson count regression uses calendar features and is compared against a weekday baseline on a chronological 14-day holdout. A poorer, non-convergent or non-finite Poisson result falls back to the baseline. Products require at least 70 covered days and 10 positive observation days; insufficient history returns null predictions. Units are derived from the forecast invoice count and the observed units/invoice ratio.

MAE/WAPE describe invoice-count prediction on that holdout, not product recommendation accuracy or a guarantee of future sales. WAPE is null when its actual denominator is zero. Two years of coverage alone do not validate annual seasonality: annualSeasonalityValidated remains false until an annual backtest is implemented. Sales can be constrained by stockouts; predictions are observed sales, not unconstrained demand or mandatory stocking quantities.

Gemini explains at most ten products in one bounded call; it does not calculate or overwrite numeric forecasts, rankings or images. Unsupported year-on-year claims and invalid/missing/provider-limited output retain deterministic fallback text. BE validates the response contract and retains its own authoritative measured counts and metadata. A service outage returns measured counts plus null predictions and an explicit warning.

## Local setup and verification

Apply `db/migrations/20261105_zzz_seasonal_sales.sql` through the existing migration workflow after the main migrations. Its typed shared functions are SECURITY INVOKER and actor-scoped.

Configure the same private AI_SERVICE_TOKEN (at least 32 characters) on BE and Python. BE uses AI_SERVICE_URL. Python alone uses GEMINI_API_KEY and GEMINI_MODEL (verified locally with gemini-2.5-flash-lite). Keep values in private environment/secrets configuration, never source, CSV or browser code. No seller bank credentials are involved.

Use the existing `--export-openapi <path>` backend workflow to export all three routes; FE should regenerate its types from that export when building a UI. Do not edit generated FE types by hand.

Python verification: `docker build --target test .`. The offline CLI can generate explicitly SYNTHETIC samples or aggregate a sales CSV; arbitrary imported CSVs require an explicit DATABASE or SYNTHETIC provenance choice. Synthetic fixtures demonstrate behavior and do not establish predictive quality on real shop history.

2026-11-05 local evidence: Python 79/79 at the verified candidate, BE seasonal/cache 49/49, actual backend TestServer HTTP → local Python HTTP → real Gemini returned 200 and a GEMINI explanation using synthetic private DB fixtures. Extended advisor/access/owned-boundary regression: 73/73 PASS. Results are recorded in the external verification report. No production deployment, real sales payment, email delivery or physical IoT verification was performed by these tests.

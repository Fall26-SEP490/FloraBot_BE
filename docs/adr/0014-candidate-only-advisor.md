# ADR 0014: Candidate-only FastAPI advisor

- Context: the supplied SQL owns stock, budget and occasion filtering; Gemini must never select arbitrary stock or execute business actions.
- Decision: isolate the provider call in a Python 3.12 FastAPI service with no database/cache/broker credentials and no tools.
- Input: five bounded survey values and at most 30 distinct candidates selected by .NET through flow.ai_candidates.
- Output: one to three unique candidate UUIDs with bounded Vietnamese reasons and card messages; no prices or transaction fields.
- Model and provider key are server configuration. Require an explicit stable Gemini model identifier, reject latest/preview/experimental aliases, and use a fixed HTTPS provider origin without redirects.
- Seller text is untrusted data, sanitized before structured prompting. This filter reduces obvious injection but is not the authority boundary: response membership and later SQL validation are mandatory.
- Authenticate internal requests with a separate service token. Caddy and YARP do not expose this service publicly; its Compose network is separate from database/broker networks.
- Bound request/response size and provider wall-clock time. Configuration, provider and output-validation failures return a generic 503 without provider details.
- .NET must retain flow.ai_suggest results and only replace them after flow.ai_record_llm accepts current-stock membership. The existing rule-based SQL flow remains available.
- .NET orchestration preserves the existing ai_suggest command/UUID response and adds an owner-scoped survey read. It commits FALLBACK before external work, validates returned UUIDs/text, measures network latency itself and invokes flow.ai_record_llm for fresh stock validation. Reads also remove stale inventory. The command is limited to ten calls per kiosk per minute.
- Approved model answers are cached in Valkey for 60 seconds. The hashed key includes kiosk, survey preferences, candidate IDs/content/prices, model and AI_CACHE_VERSION; it excludes customer/session identifiers. Cache reads revalidate response shape and current SQL eligibility, marking accepted surveys CACHE atomically. Cache outages fall through to the advisor within a bounded wait.
- The kiosk survey UI is connected and tested. Live Gemini latency/credentials remain unverified. Backend integration tests use real SQL/Valkey and substituted advisor HTTP transport.

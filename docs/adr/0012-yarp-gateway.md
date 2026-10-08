# 0012: YARP ingress gateway

- Context: the brief requires Caddy -> YARP -> the modular API, with rate limiting and initial authentication.
- Decision: a separate .NET 10 host uses YARP 2.3.0 for /api, /hub and /openapi routes.
- Authentication: validate presented access JWTs with the API's shared issuer/audience/key; absent credentials still reach API authorization.
- Renewal: login, refresh and logout remain reachable when an old access cookie is invalid.
- Authority: API checks database user status, device keys, policies and tenant ownership; gateway validation never replaces these checks.
- Forwarding: preserve method/body/query/original host, auth cookies and WebSocket upgrades.
- Rate limit: bounded per-client fixed window; forwarded client IP is trusted only from configured immediate proxy IPs.
- Operations: /health reports gateway liveness only; unavailable upstream requests return 502. No automatic retry of writes.
- Verification: real upstream and Kestrel WebSocket tests, plus the browser registration/approval/subscription journey through YARP.
- Remaining: Caddy, deployed TLS, container networking and full application compose still require integration.

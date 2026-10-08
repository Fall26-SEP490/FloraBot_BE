# ADR 0013: One-origin local application stack

- Context: the landing, portal and kiosk must reach YARP and the API without separate browser origins.
- Considered: individual development ports or a Caddy container serving production static builds.
- Decision: keep development ports and add `docker-compose.app.yml` for the integrated demo.
- Caddy serves landing at `/`, portal routes at `/login`, `/admin/*`, `/seller/*`, and kiosk at `/kiosk/`.
- Portal assets use `/portal/`; the kiosk manifest, router, assets and service worker use `/kiosk/`.
- API, hub and OpenAPI requests pass through YARP; API and gateway ports are not published.
- Caddy has a fixed private edge IP; only that proxy is trusted by YARP for forwarded headers.
- The bootstrap preserves existing databases and applies supplemental migrations under a transaction lock.
- JWT signing material is generated once in ignored local storage; Data Protection keys persist in a volume.
- This overlay binds HTTP to loopback for local development. Public HTTPS, certificate issuance and production secret storage require a separate verified deployment.

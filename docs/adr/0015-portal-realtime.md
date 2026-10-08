# Portal refresh notifications

The portal needs timely updates after committed merchant commands and registrations.
SignalR exposes an authenticated `/hub/portal` endpoint with no client business methods.
The server tracks connections and selects administrators plus the affected seller.
Before sending, Identity rechecks active users, roles and seller membership; expired sessions are excluded.
The payload is only a `Refresh` invalidation. Clients must fetch authorized API data again.
Commands release their database connection after commit and before the independent audience lookup.
Notification work has a 500 ms deadline; database, transport and cancellation failures are logged without rolling back committed data.
Foreign browser origins are rejected and authentication expiration closes the connection.
This is best-effort delivery, not a substitute for the required CAP transactional outbox.
The portal uses the official SignalR client with HttpOnly cookies, coalesces invalidations and refreshes on reconnect.
Visible online pages poll authorized API queries every 30 seconds, renewing cookies before retrying disconnected hubs.
Logout/unmount removes listeners, timers and the connection; refetches preserve mounted forms and focus.
Browser tests cover reconnect, fallback and cleanup; an actual YARP/API/PostgreSQL test verifies new registrations appear in the administrator queue via a received Refresh frame.
Job/webhook/device notifications and Caddy WebSocket integration verification remain outstanding.

# 0045 - Transactional email transport

Status: transport implemented; subsequent event integration is recorded in ADR 0046.

The source brief A1 requires Google SMTP in development and Brevo in production,
limited to seller approval, subscription reminders and receipts. No notification
table is permitted. Notify owns transport; Identity must own recipient selection.

Use the existing HttpClient and System.Net.Mail rather than add a dependency.
EMAIL_PROVIDER defaults to Disabled; sending while disabled fails explicitly.
Enabled configuration is validated at startup. GoogleSmtp is Development-only,
uses smtp.gmail.com:587 and requires STARTTLS plus an app password. Brevo uses a
fixed HTTPS endpoint, no redirects, one recipient and UTF-8 plain text.

Each attempt has a 15-second deadline, bounded request text and response size.
Brevo acceptance requires HTTP 201 plus a nonempty messageId. SMTP acceptance and
Brevo acceptance are not inbox-delivery confirmation. Provider errors are sanitized;
neither recipients nor provider response bodies are copied into exceptions.

There is no automatic transport retry: a timeout may follow provider acceptance.
CAP integration must explicitly account for at-least-once delivery; neither this
transport nor a future outbox consumer can claim exactly-once email delivery.

At this milestone the sender had no endpoint, subscriber or job invoking it. It did
not satisfy the whole email requirement and must not be represented as live email.
Original source SQL, notification storage and public API contracts are unchanged.

Recipient research: identity.sellers has phone, not email; identity.users owns an
optional email and can have multiple SELLER users. flow.approve_seller emits
SELLER_APPROVED but does not create a user. Account provisioning is a separate
flow. Approval delivery needs an explicit no-recipient/deferred-delivery policy.
Guest checkout must remain without PII; do not add email collection to that flow
just to make the receipt email requirement appear complete.

Next integration work: resolve active seller recipients in Identity, select minimal
event payloads and retention, connect approval/subscription receipt events and a
server-time expiration reminder job, then test transaction rollback and duplicate
delivery behavior using fake providers. Do not send external test messages without
authorization. Google SMTP network/TLS and live Brevo remain unverified.

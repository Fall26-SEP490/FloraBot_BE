# 0042 - Seller product-photo gallery

Date: 2026-10-08. Status: accepted for local implementation.
Context: an upload response can be lost; shops need a read surface to inspect saved product photos.
Decision: add an authenticated, no-store GET on the product-photo route, with 20-item pages and hasMore.
Catalog locks/checks product ownership; Notify reads only catalog/flower_product/PRODUCT attachments.
Read permission uses active user ownership, without requiring the shop subscription to remain active.
The portal explicitly loads the gallery, previews selected files locally and submits raw image bytes once.
Browser validates supported MIME/size for feedback; backend retains authoritative byte/type/ownership checks.
Unknown upload outcomes block the current upload form and direct reconciliation; refreshing images never retries a write.
Only HTTPS image URLs without embedded credentials render, with no referrer; invalid/broken images have text fallback.
Legacy attachments may still have demo metadata; gallery membership is not a verification certificate.
Limits: component-local uncertainty state resets on unmount; server idempotency/orphan cleanup and live provider proof remain pending.
Verification: PostgreSQL tests cover ownership, expired-shop reads, no-store, page boundaries and exclusion of evidence; browser tests cover raw payload, previews, failure handling, mobile reflow and axe.

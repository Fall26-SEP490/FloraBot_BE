# 0041 - Verify stored product-image bytes

Date: 2026-10-08. Status: backend and portal implemented; live provider verification pending.
Context: source flow.attach synthesizes size/hash from a URL for SQL demonstration.
Decision: add a raw PNG/JPEG/WebP upload endpoint for a seller-owned product, maximum 5 MiB.
Reuse flow.check_catalog_editor before uploading and again under a lock before attachment creation.
Use Cloudinary signed server uploads with a unique public ID and overwrite=false; never send secrets to browsers.
Fetch the returned original only from its exact Cloudinary cloud/path, with no redirects and bounded reads.
Verify stored bytes against the submitted SHA-256, then replace simulated metadata within the same DB transaction.
Catalog calls Notify's metadata helper; the original SQL files and existing URL-based flows are unchanged.
Total provider operation has a 30-second deadline; no automatic upload retries or assumed success after a timeout.
Scope is public product photographs only. Payment/dispute evidence must not use this public storage path.
Limits: interrupted operations may leave unreferenced provider objects; cleanup and durable idempotency are pending.
Tests use a fake HTTP provider and real PostgreSQL; they do not prove live Cloudinary credentials or availability.

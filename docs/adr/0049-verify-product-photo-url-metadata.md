# 0049 - Verify metadata for URL-based product photos

Date: 2026-10-08. Status: accepted.
Context: the original SQL attachment helper hashes the URL and supplies demo size metadata.
The raw upload API overwrites these values from verified bytes, but the generated add_product_photo command did not.
Decision: retain its request/response shape and original SQL; verify the URL before invoking the flow, then update metadata in the same transaction.
Only versioned HTTPS original images on res.cloudinary.com in the configured cloud's florabot folder are accepted; transformations, query strings, credentials, alternate ports and foreign hosts/clouds are rejected.
Downloads use the existing redirect-disabled HTTP client, a 30-second deadline and a 5 MiB bound; the server computes MIME, size and SHA-256 from returned bytes.
Tenant resource authorization runs before the request and retains its transaction lock through commit; SQL remains responsible for catalog business rules.
Invalid URL/image input returns 400; missing configuration or provider failure returns 503 and rolls back without recording an attachment.
Compatibility: arbitrary external product URL registration is no longer accepted. Use the raw-upload endpoint for new files. No historical records are backfilled.
Evidence: ProductPhotoTests verifies actual DB metadata, foreign tenant rejection before HTTP, URL restrictions, provider redirect rejection and rollback; FlowContractTests and ModuleBoundaryTests also pass.
Limits: this covers public product photos only. Other attachment flows, historic demo rows, live provider acceptance and orphan recovery are unchanged.

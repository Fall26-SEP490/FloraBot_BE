# Browser integration API host

This test-only executable runs the real FloraBot API on loopback port 5081 with
Kestrel. It keeps authentication, database transactions, upload validation,
protected evidence tickets and private image reads unchanged. Only the outbound
Cloudinary HTTP transport is replaced by in-memory storage of uploaded bytes.
It is not included in the API deployment image and is not a production provider.

The host refuses any database other than `florabot_browser_tests` on localhost.
The frontend `playwright.integration.config.ts` supplies the signing key and
isolated service configuration. Run `pnpm test:integration` in the sibling FE
checkout to initialize the disposable database and launch the complete stack.
Do not start a production Next build while that suite uses the same `.next` folder.

Images disappear when the host stops. Integration fixtures must upload fresh
images per test; they must not claim real Cloudinary availability or real payments.
Provider signature/integrity failures are also covered by API-level evidence tests.

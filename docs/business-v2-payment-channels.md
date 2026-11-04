# Business V2: seller receipts, shop loyalty and kiosk capacity

This backend increment separates incoming flower payments from platform rental fees. It does not deploy services or execute bank transfers. Real seller payOS credentials and live settlement must be configured/verified separately; automated tests substitute provider HTTP and use disposable local databases.

## Compatibility with current main

The requested-layout/pricing change in ADR0060 remains authoritative. Walk-in and preorder slot counts are fixed at rental application; Operations maintains unit annual prices. This increment does not reopen Seller/Manager zone-edit APIs or replace the retired SQL setters. Availability reads the resulting physical PREORDER zone. New application/subscription charges remain platform payments using their stored fee snapshot.

Apply additive SQL migrations in filename order after backing up the target database. `20261104_payment_channel_versions.sql` must precede `20261104_seller_direct_payments.sql`; the requested-layout/pricing migration must remain installed before the later capacity migration. Do not run database initialization against an existing application database. This work does not change FE contracts by hand; regenerate them with the repository OpenAPI export workflow after the backend is configured and migrated.

## Incoming payment flow

1. Operator stores a distinct server-side configuration reference for each seller. Admin binds that reference with `PUT /api/sellers/{sellerId}/payment-channel` and `{ "secretRef": "seller_demo" }`. No API accepts bank login passwords or OTPs.
2. The channel starts unverified. `POST /api/sellers/{sellerId}/payment-channel/verify` registers the version-bound webhook using payOS confirm-webhook. Activation requires the authenticated receiver response and a correctly signed provider sample tied to the verification attempt. A sample never marks an order paid.
3. A new flower order requires the seller's active verified channel. Missing/unverified/paused channels do not fall back to the platform account.
4. The payment stores seller, channel version, receiver and explicit funding provenance. Signed creation/callback responses must match the expected amount and receiver. Reconciliation uses the pinned channel, not a client claim of success.
5. Seller-direct receipts do not increase platform-held seller wallet balances. Subscription/rental payments use the existing platform configuration. Historical pending platform payments retain their legacy processing and accounting.

Keep old channel configuration references and credentials available until their pending payments have been reconciled. Do not overwrite a reference with another shop's keys or a different receiver; bind a new version/reference instead. Pausing blocks new payment creation and preserves handling for existing payments.

### Server configuration shape

Use a private environment/secret store, never source control or the API progress sheet. The existing `PAYOS_CLIENT_ID`, `PAYOS_API_KEY`, `PAYOS_CHECKSUM_KEY` remain the platform configuration. A seller configuration uses .NET nested environment keys, for example:

```text
PAYOS_CHANNELS__seller_demo__SellerId=<seller UUID>
PAYOS_CHANNELS__seller_demo__ClientId=<seller payOS client ID>
PAYOS_CHANNELS__seller_demo__ApiKey=<private API key>
PAYOS_CHANNELS__seller_demo__ChecksumKey=<private checksum key>
PAYOS_CHANNELS__seller_demo__ExpectedBin=<receiving bank BIN>
PAYOS_CHANNELS__seller_demo__ExpectedAccount=<receiving account>
PAYOS_CHANNELS__seller_demo__ExpectedName=<receiving name>
PAYOS_CHANNELS__seller_demo__ReturnUrl=<HTTPS return URL>
PAYOS_CHANNELS__seller_demo__CancelUrl=<HTTPS cancel URL>
PAYOS_PUBLIC_BASE_URL=<public backend HTTPS origin>
```

Use a backend origin reachable by payOS for `PAYOS_PUBLIC_BASE_URL`, not the FE origin. Local fake-provider tests do not prove that a real webhook or payment can reach a VPS. Read APIs return masked channel bank details and no raw keys.

## API groups

| Purpose | Endpoints |
| --- | --- |
| Configure channel | GET/PUT `/api/sellers/{sellerId}/payment-channel`; POST `.../verify`; PATCH `.../status`; GET `/api/admin/payment-channels` |
| Seller receipts | GET `/api/sellers/{sellerId}/payments`, GET `.../{paymentId}`, POST `.../{paymentId}/reconcile`; GET `/api/sellers/{sellerId}/reports/sales` |
| Loyalty setting | GET/PUT `/api/sellers/{sellerId}/loyalty-settings` |
| Own points per shop | GET `/api/member/loyalty`, GET `/api/member/loyalty/{sellerId}` |
| Direct refunds | GET `/api/sellers/{sellerId}/refunds`, GET `.../{refundId}`, POST `.../{refundId}/bank-details`, POST `.../{refundId}/evidence/upload`, POST `.../{refundId}/submit-transfer` |
| Review submitted proof | GET `/api/admin/refunds/{refundId}/seller-submission`; existing Admin approval/confirmation flows remain the authority |
| Estimated capacity | GET `/api/kiosks/{kioskId}/preorder-availability` |
| End kiosk lease | POST `/api/sellers/{sellerId}/kiosks/{kioskId}/termination-requests`, GET `.../current`; Operations list/review/evidence/complete routes under `/api/operations/kiosk-termination-requests` |

Authorization is rechecked by the backend; FE filtering does not grant access. A member can inspect their own balance at different shops without receiving another customer's balance. A seller cannot inspect a foreign shop's payments/refunds.

## Loyalty

The seller opts in/out; the earn rate is fixed at 1%. A completed PREORDER earns points from the amount the customer actually paid. WALK_IN earns no points. Balances, redemption and reversal/debt tracking are isolated per customer and seller. The seller bears the redemption discount; FloraBot does not create a subsidy or sales commission. Order snapshots retain the promised earn policy if the seller later turns earning off. Legacy global points/history are preserved rather than arbitrarily distributed among shops. Existing conversion remains one point per VND.

## Seller-funded manual refund

The customer supplies refund bank details through the existing private refund/dispute capability. Admin first approves the refund. Only the relevant active seller can access the approved refund's exact receiver, with a reason, audit entry and no-store response. Seller transfers outside FloraBot and submits matching amount, receiver/reference and a private evidence ticket bound to that refund. This creates PENDING_REVIEW, not a successful payout. Admin reviews and confirms using the existing maker/checker rules. Direct confirmation writes no platform cash or seller-wallet payout. Existing platform/legacy refunds preserve their former payer and ledger behavior.

The submission response/detail exposes whether a new submission is supported. Replacing an already submitted/rejected proof is not a public correction workflow in this increment. A bank transfer is never verified merely because a seller submitted its reference/image; human review remains required.

## Capacity and termination

Availability is an estimate, not a guaranteed reservation or a road/device status assertion. Accepting a quote locks/checks actual capacity atomically, including paid/unfulfilled obligations without double-counting an already occupied slot. No new 10% allocation or two-hour pickup gate is introduced.

An accepted termination blocks new obligations while retaining existing paid fulfillment. Completing termination requires the pending commerce, stock, door/device and private inspection-evidence guards to pass. It does not automatically mark stock empty, refund deposits, deduct fees, delete historical leases or control a physical relay.

## Local verification

The four focused test factories require explicit `TEST_DATABASE_URL` and `TEST_VALKEY_URL`; they never fall back to the application's DATABASE_URL or a local coordinator file. Supply a disposable initialized database and discover the actual published cache port after Docker restarts. Full API runs should also set `TEST_JOBS_DATABASE_URL` to a separately initialized disposable jobs database. Keep test DB/Valkey bindings separate from an application instance. See the coordinator's final verification report for executed results and known upstream baseline failures; this guide itself is not a test-pass certificate.

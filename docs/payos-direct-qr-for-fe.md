# Direct bank-payment QR from payOS

## Backend response

Payment-link APIs now return `qrCode` in addition to the existing `orderCode`,
`amount` and `checkoutUrl`. The same response applies to:

- `POST /api/kiosks/{kioskId}/checkouts/{checkoutId}/payment-link`
- `POST /api/member/preorders/{requestId}/payment-link`
- `POST /api/member/kiosk-applications/{id}/payment-link`
- `POST /api/sellers/{sellerId}/subscriptions/{subscriptionId}/payment-link`

`qrCode` is the VietQR **text payload**, typically starting with `000201`.
It is not an image, base64 data or the payOS page URL. Pass the exact string to
the QR renderer. The backend only uses it after verifying the provider response
signature, order code, amount and pending status. It saves the payload on the
existing gateway-order mapping so a repeated request can return it even when the
provider's GET response omits the QR. No new gateway order is allocated for a retry.

The field is nullable for backward compatibility. Existing links created before
this change, or a provider creation response lost before it could be saved, may
still have `qrCode: null`. Keep the payOS checkout-page fallback in those cases.
Do not create a second order or invent a bank QR to replace missing provider data.

## Kiosk FE

The current `value={payment.data.qrCode || payment.data.checkoutUrl}` can use the
new field. Also distinguish the instruction beside the QR:

```tsx
const hasBankQr = Boolean(payment.data.qrCode);
const qrValue = payment.data.qrCode || payment.data.checkoutUrl;
// QRCodeSVG value={qrValue}
// hasBankQr: "Mở ứng dụng ngân hàng và quét mã để thanh toán"
// fallback: "Quét bằng camera để mở trang thanh toán payOS"
```

For a newly created payment with a non-null `qrCode`, scanning from a bank app
can go directly to the transfer screen. Keep the kiosk screen open and continue
checking checkout status. QR display or a browser return URL never proves payment.
The verified webhook and backend checkout status remain authoritative.

## Web FE

If the web wants one click to open the hosted payOS checkout, redirect immediately
after the create-link request succeeds instead of asking for a second link click:

```ts
const payment = await createPaymentLink();
window.location.assign(payment.checkoutUrl);
```

Alternatively, display a QR rendered from `payment.qrCode` directly on the web page.
On a phone, keep a button to open payOS because the phone cannot scan its own screen
with its camera. UI behavior is a separate FE change; this backend patch does not
change FE navigation or automatically create payment links.

Refresh generated FE/Kiosk API contracts using their normal OpenAPI generation
workflow after the backend has been deployed. Do not edit generated types manually.
All four endpoint declarations already use `Produces<PaymentLinkResponse>`, so the
new nullable string property appears in the live `/openapi/v1.json` automatically.

For the Web FE repository, download the deployed schema to
`packages/contracts/openapi.json`, then run `pnpm contracts`. For Kiosk, download
to the same relative path and run
`pnpm exec openapi-typescript packages/contracts/openapi.json -o packages/contracts/src/generated/api.d.ts`.
The current Kiosk checkout already supports `qrCode` and switches its bank-app
instructions based on whether that value is present.

## Deploy

1. Back up the VPS database using the existing process.
2. Apply `db/migrations/20261101_payos_payment_qr.sql` to that database.
3. Publish and deploy **FloraBot.Api**. This change does not require Gateway publish.
4. Recycle `FloraBot-API` and verify health and the OpenAPI response schema.
5. Test a new unpaid checkout. Verify the response has a nonempty `qrCode` beginning
   with `000201`. Call again before paying and confirm the same order/QR is retained.
6. Check the kiosk's bank-app scan and signed webhook/status update on the real setup.

The additive migration preserves existing gateway mappings, amounts and order codes.
An old API build can ignore the added column if an application rollback is necessary.

## Validation

The focused regression command avoids unrelated onboarding compilation errors in
the main test project:

```powershell
dotnet test tests/FloraBot.SlotRemoval.Tests/FloraBot.SlotRemoval.Tests.csproj --no-restore --filter "FullyQualifiedName~PayOsClientQrTests|FullyQualifiedName~UntrustedGatewayResponsesAreRejected"
```

These tests cover signed QR pass-through, tampering, invalid QR values and recovery
of an existing link without QR data. `PaymentLinkTests` also checks persistence
across a duplicate-link lookup and first-response loss. That integration test needs
an initialized disposable test database with the new migration and the existing
test cache/event-bus services. Never point its test configuration at the VPS.

## Reference

Official payOS creation and lookup schemas:
https://payos.vn/docs/api/

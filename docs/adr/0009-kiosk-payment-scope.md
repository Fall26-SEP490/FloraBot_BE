# Kiosk payment and receipt scope

Checkout IDs group the seller orders paid by one gateway charge.
The payment-link endpoint derives amount and deadline from the stored charge and orders.
Every order must belong to the authenticated kiosk; customer sessions must also own every order.
Device authentication can serve guest purchases at that device without customer PII.
Neither amount, customer identity nor callback URLs are accepted from callers.
The database hold duration supplies payOS expiredAt; elapsed deadlines reject link creation before job execution.
Receipt/status reads use the same scope and no-store headers, excluding refund banking data and device tokens.
Only verified signed webhooks invoke checkout_paid; browser redirects cannot mark an order paid.
Integration tests send repeated callbacks and device events to complete a stocked bouquet purchase with a balanced journal.
The external gateway is simulated in tests; real payOS and MQTT transport remain separate verification gates.

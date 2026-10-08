# ADR 0031: Complaint submission from electronic receipts

- Context: the brief exposes open_dispute to receipt holders; the source flow allows one complaint per completed order within the server-configured window and requires photo evidence.
- Decision: keep the generated endpoint and original SQL. Ordering validates trimmed reason text (5-2000 characters) and a bounded HTTPS evidence URL without embedded credentials.
- The shared receipt capability check locks the order before the source flow. Invalid credentials remain 404; source business conflicts remain 409; input validation is 400.
- The public form uses server-reported eligibility, explicit review and confirmation, persistent labels, error associations and focus management.
- The browser omits portal cookies, does not persist complaint fields/capabilities and does not retry submissions automatically.
- A lost response or 5xx clears the form and requires a fresh receipt lookup before retry. The database uniqueness/state checks remain authoritative under simultaneous requests.
- Successful submission refreshes the visible receipt to DISPUTED with a status announcement and removes the entry form. No refund promise is made.
- Evidence currently uses an HTTPS link, matching existing seller incident entry. The API stores the reference but does not fetch or verify image bytes; the interface states that upload is not available yet.
- Verification covers simultaneous submissions, expired server deadline, unsafe/missing evidence URL, review/correction, response loss, mobile/desktop accessibility, and real browser-to-database creation of a single dispute/evidence record.
- Remaining: verified media upload/hash/storage and admin complaint adjudication/refund UI. A link-only form does not complete those separate requirements.

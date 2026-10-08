# PubSubClient provenance

Vendored from https://github.com/knolleary/pubsubclient/tree/v2.8, MIT license retained.
Unmodified upstream source SHA-256:

- src/PubSubClient.h: 376ddb9ecda5816dfeff344f8742253d487adc16272455ebe2dfa4c071cdd348
- src/PubSubClient.cpp: c5ab036263d514791b1955fe44aacca103b2eea4ca07cd539bff25dd88cc4ede

FloraBot additions: outgoing QoS 1 with explicit packet ID/DUP, PUBACK and single-topic SUBACK
callbacks, incoming flags accessor, and receive length validation before invoking the application.
The application owns one inflight event and its durable retry queue; the library does not claim
to implement automatic QoS 1 retransmission or durable MQTT session storage.

MQTT 3.1.1 marks retained messages delivered after subscription. It does not retain that flag for
live subscribers, unlike MQTT 5 Retain As Published. The application must reject flagged retained
commands and independently enforce HMAC, expiry and durable deduplication for every publication.
Do not claim online retained-publication detection from this accessor.

Native Client/Arduino test stubs in ../../test/pubsub-stubs are from the same upstream tag and
covered by this license, except Print.h, which is a minimal local interface substitute. Destructors
were added locally so sanitizer runs do not leak buffers.

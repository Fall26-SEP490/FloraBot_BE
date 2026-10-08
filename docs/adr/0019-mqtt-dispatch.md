# 0019 - Dispatch existing live unlock tokens

Status: implemented locally; full payOS sandbox/hardware demonstration pending.

Payment events are historical facts and must never authorize a new physical unlock. The MQTT worker polls
existing live unlock tokens instead of creating commands from OrderPaid payloads. This also recovers
committed tokens after process restart without requiring a payment event to be replayed.

Only provisioned hardware with an ONLINE kiosk and fresh heartbeat is eligible. Customer tokens additionally
require the same held order/slot and an unexpired hold. ACKED, OPENED, terminal and expired tokens do not dispatch.
Pending authenticated sensor evidence takes precedence over sending another command.

Migration 008 stores a unique delivery-attempt identity, next retry time and broker acknowledgment. Preparation
locks the token, calls the existing SENT flow and commits before publishing; an immediate ACK can therefore
observe the correct source state. Retries wait ten seconds and reuse the exact cmd_id and signed payload.
The original door_max_attempts remains authoritative. After that many attempts and another retry deadline,
the original FAILED flow creates the appropriate failure/refund, committed with CAP events. A SENT token
with no dispatcher history is not automatically failed by this worker.

Immediately before publication, recheck live authorization, hardware/relay/slot/purpose/timestamps, signature
and delivery identity. Hold a NO KEY UPDATE token lock through a bounded five-second broker publish to
serialize revocation while permitting the receiver's inbox FK KEY SHARE lock. A FOR UPDATE lock here would
block durable incoming event receipt and could stall the same MQTT client's PUBACK processing.

Publish QoS 1, retain false, and reject negative broker acknowledgment. A crash after publish but before its
database acknowledgment can resend the same command; the device's durable claim suppresses repeat actuation.
An attempted send is committed before the network operation, so a transport failure can consume an attempt.
This uncertainty fails closed and can reach the source-flow refund path; no new token is minted to bypass it.
Broker disconnection stops new preparation. Already committed sensor events continue processing independently.

Tests cover concurrent claims/delivery, sensor receipt before PUBACK, failed transport, stale attempts,
authorization/relay changes, missing keys, pending sensor evidence, bounded retries and unique paid-order
failure/refund events. A live Docker transport test creates a reusable empty/unassigned test slot and a
technical token, starts the software simulator, then verifies one send and ACK/OPENED/CLOSED through the actual
API, broker and PostgreSQL. It stops only its own simulator and preserves transport evidence/state.
This is not proof of physical GPIO behavior or live payOS sandbox payment.

import hmac
import json
import os
import tempfile
import unittest

from protocol import CommandStore, canonical_command, verify_command

KEY = bytes(range(32))
HARDWARE = "ESP32-A1B2C3"
COMMAND = dict(version=1, hardware_id=HARDWARE, cmd_id="11111111-1111-1111-1111-111111111111",
               slot_id="22222222-2222-2222-2222-222222222222", relay_channel=7,
               purpose="CUSTOMER_PICKUP", issued_at=1800000000, expires_at=1800000060,
               signature="c8795c2a6674c0de6c04d8823b5d53b5e606a493e358a3fffa14c475b6b20d29")


class ProtocolTests(unittest.TestCase):
    def test_shared_vector_and_tampering(self):
        self.assertEqual(COMMAND["signature"], hmac.digest(KEY, canonical_command(COMMAND), "sha256").hex())
        self.assertTrue(verify_command(COMMAND, HARDWARE, KEY, 1800000001))
        for field, value in dict(version=2, hardware_id="other", cmd_id="33333333-3333-3333-3333-333333333333",
                                 slot_id="33333333-3333-3333-3333-333333333333", relay_channel=8,
                                 purpose="SELLER_ACCESS", issued_at=1799999999, expires_at=1800000061,
                                 signature="z" * 64).items():
            self.assertFalse(verify_command(dict(COMMAND, **{field: value}), HARDWARE, KEY, 1800000001), field)
        self.assertFalse(verify_command(COMMAND, "other", KEY, 1800000001))
        self.assertFalse(verify_command(COMMAND, HARDWARE, bytes(32), 1800000001))
        self.assertFalse(verify_command(COMMAND, HARDWARE, KEY, 1799999999))
        self.assertFalse(verify_command(COMMAND, HARDWARE, KEY, 1800000060))
        self.assertFalse(verify_command(dict(COMMAND, relay_channel=True), HARDWARE, KEY, 1800000001))

    def test_restart_does_not_repeat_actuation_and_preserves_pending_events(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "state.db")
            pulses = []
            store = CommandStore(path)
            self.assertEqual("executed", store.process(COMMAND, HARDWARE, KEY, 1800000001, pulses.append))
            events = store.pending()
            self.assertEqual(["ACK", "OPENED", "CLOSED"], [json.loads(row[1])["event"] for row in events])
            store.delivered(events[0][0])
            store.close()
            store = CommandStore(path)
            self.assertEqual("duplicate", store.process(COMMAND, HARDWARE, KEY, 1800000002, pulses.append))
            self.assertEqual([7], pulses)
            self.assertEqual(events[1:], store.pending())
            changed = dict(COMMAND, relay_channel=8)
            changed["signature"] = hmac.digest(KEY, canonical_command(changed), "sha256").hex()
            self.assertEqual("rejected", store.process(changed, HARDWARE, KEY, 1800000002, pulses.append))
            store.close()

    def test_claim_survives_crash_before_actuation(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "state.db")
            store = CommandStore(path)

            def crash(relay):
                raise RuntimeError("simulated power loss")

            with self.assertRaises(RuntimeError):
                store.process(COMMAND, HARDWARE, KEY, 1800000001, crash)
            store.close()
            store = CommandStore(path)
            self.assertEqual("duplicate", store.process(COMMAND, HARDWARE, KEY, 1800000002, crash))
            self.assertEqual([], store.pending())
            store.close()


if __name__ == "__main__":
    unittest.main()

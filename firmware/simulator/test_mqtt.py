"""Explicit integration test: requires the isolated local TLS broker and credentials."""

import hmac
import json
import queue
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import uuid

import paho.mqtt.client as mqtt
from protocol import canonical_command


class MqttSimulatorTests(unittest.TestCase):
    def test_signed_cycle_survives_duplicate_delivery_and_process_restart(self):
        hardware = "ESP32-A1B2C3"
        with open("/secrets/credentials.json", encoding="utf-8") as file:
            credentials = json.load(file)
        key = bytes.fromhex(credentials[hardware]["hmacKey"])
        messages = queue.Queue()
        subscribed = threading.Event()
        client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id="test-" + str(uuid.uuid4()))
        client.username_pw_set("florabot-backend", credentials["florabot-backend"]["password"])
        client.tls_set(ca_certs="/secrets/broker/ca.crt")
        client.on_connect = lambda c, u, f, r, p: c.subscribe(f"kiosk/{hardware}/evt", qos=1)
        client.on_subscribe = lambda *args: subscribed.set()
        client.on_message = lambda c, u, m: messages.put(json.loads(m.payload))
        client.connect("mosquitto", 8883)
        client.loop_start()
        process = None

        def receive(event=None, command_id=None):
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline:
                message = messages.get(timeout=max(0.01, deadline - time.monotonic()))
                if event and message["event"] != event:
                    continue
                if command_id and message["cmd_id"] != command_id:
                    continue
                canonical = "\n".join(["florabot.event.v1", hardware, message["event_id"],
                                       message["cmd_id"], message["event"], str(message["occurred_at"])]).encode("ascii")
                self.assertEqual(hmac.digest(key, canonical, "sha256").hex(), message["signature"])
                return message
            self.fail("Expected device event was not received")

        def publish(command):
            client.publish(f"kiosk/{hardware}/cmd", json.dumps(command), qos=1).wait_for_publish(timeout=5)

        try:
            self.assertTrue(subscribed.wait(10))
            with tempfile.TemporaryDirectory() as directory:
                args = [sys.executable, "-B", "main.py", "--host", "mosquitto", "--port", "8883",
                        "--credentials", f"/secrets/devices/{hardware}.json", "--ca", "/secrets/broker/ca.crt",
                        "--state", directory + "/device.db"]
                now = int(time.time())
                command = dict(version=1, hardware_id=hardware, cmd_id=str(uuid.uuid4()), slot_id=str(uuid.uuid4()),
                               relay_channel=7, purpose="CUSTOMER_PICKUP", issued_at=now, expires_at=now + 60)
                command["signature"] = hmac.digest(key, canonical_command(command), "sha256").hex()
                with open(directory + "/first.log", "w+") as log:
                    process = subprocess.Popen(args, stdout=log, stderr=log)
                    receive(event="HEARTBEAT")
                    retained = dict(command, cmd_id=str(uuid.uuid4()))
                    retained["signature"] = hmac.digest(key, canonical_command(retained), "sha256").hex()
                    client.publish(f"kiosk/{hardware}/cmd", json.dumps(retained), qos=1, retain=True).wait_for_publish(timeout=5)
                    client.publish(f"kiosk/{hardware}/cmd", b"", qos=1, retain=True).wait_for_publish(timeout=5)
                    publish(dict(command, relay_channel=8))
                    for _ in range(3):
                        publish(command)
                    self.assertEqual(["ACK", "OPENED", "CLOSED"],
                                     [receive(command_id=command["cmd_id"])["event"] for _ in range(3)])
                    process.terminate()
                    process.wait(timeout=10)
                    process = None
                    log.seek(0)
                    output = log.read()
                    self.assertEqual(1, output.count("SIMULATED door cycle"))
                    self.assertIn("Command rejected", output)
                with open(directory + "/restart.log", "w+") as log:
                    process = subprocess.Popen(args, stdout=log, stderr=log)
                    receive(event="HEARTBEAT")
                    publish(command)
                    next_command = dict(command, cmd_id=str(uuid.uuid4()))
                    next_command["signature"] = hmac.digest(key, canonical_command(next_command), "sha256").hex()
                    publish(next_command)
                    self.assertEqual(["ACK", "OPENED", "CLOSED"],
                                     [receive(command_id=next_command["cmd_id"])["event"] for _ in range(3)])
                    process.terminate()
                    process.wait(timeout=10)
                    process = None
                    log.seek(0)
                    output = log.read()
                    self.assertEqual(1, output.count("SIMULATED door cycle"))
                    self.assertIn("Command duplicate", output)
        finally:
            if process is not None:
                process.terminate()
                process.wait(timeout=10)
            if client.is_connected():
                client.publish(f"kiosk/{hardware}/cmd", b"", qos=1, retain=True).wait_for_publish(timeout=5)
            client.disconnect()
            client.loop_stop()


if __name__ == "__main__":
    unittest.main()

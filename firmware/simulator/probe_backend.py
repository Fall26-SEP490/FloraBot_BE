"""Publish harmless signed heartbeat probes for the backend inbox smoke test."""

import json
import time
import uuid

import paho.mqtt.client as mqtt
from protocol import signed_event

with open("/secrets/devices/ESP32-A1B2C3.json", encoding="utf-8") as file:
    device = json.load(file)
key = bytes.fromhex(device["hmacKey"])
hardware = device["hardware_id"]
client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id="inbox-probe-" + str(uuid.uuid4()))
client.username_pw_set(hardware, device["password"])
client.tls_set(ca_certs="/secrets/broker/ca.crt")
client.connect("mosquitto", 8883)
client.loop_start()
good = signed_event(hardware, str(uuid.UUID(int=0)), "HEARTBEAT", key, int(time.time()))
bad = dict(good, event_id=str(uuid.uuid4()), signature="0" * 64)
foreign = signed_event(hardware, str(uuid.uuid4()), "OPENED", key, int(time.time()))
try:
    for _ in range(3):
        for message in (good, bad, foreign):
            delivery = client.publish(f"kiosk/{hardware}/evt", json.dumps(message), qos=1)
            delivery.wait_for_publish(timeout=5)
            if not delivery.is_published():
                raise RuntimeError("Probe delivery failed")
        time.sleep(1)
finally:
    client.disconnect()
    client.loop_stop()
print(json.dumps(dict(good=good["event_id"], bad=bad["event_id"], foreign=foreign["event_id"])))

"""Software-only door simulator. Never controls physical GPIO."""

import argparse
import json
import queue
import ssl
import time
import uuid

from protocol import CommandStore, signed_event


def main():
    import paho.mqtt.client as mqtt
    from paho.mqtt.packettypes import PacketTypes
    from paho.mqtt.properties import Properties
    from paho.mqtt.subscribeoptions import SubscribeOptions

    parser = argparse.ArgumentParser()
    parser.add_argument("--credentials", required=True)
    parser.add_argument("--ca", required=True)
    parser.add_argument("--state", required=True, help="Persistent SQLite path; retain across restarts")
    parser.add_argument("--host", default="localhost")
    parser.add_argument("--port", type=int, default=58883)
    args = parser.parse_args()
    with open(args.credentials, encoding="utf-8") as file:
        credentials = json.load(file)
    hardware = credentials["hardware_id"]
    key = bytes.fromhex(credentials["hmacKey"])
    if len(key) != 32:
        raise ValueError("Invalid device key")
    messages = queue.Queue(maxsize=64)
    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id="sim-" + hardware,
                         protocol=mqtt.MQTTv5)
    client.username_pw_set(hardware, credentials["password"])
    client.tls_set(ca_certs=args.ca, tls_version=ssl.PROTOCOL_TLS_CLIENT)
    client.reconnect_delay_set(min_delay=1, max_delay=15)

    def connected(connection, userdata, flags, reason, properties):
        if reason == 0:
            connection.subscribe(f"kiosk/{hardware}/cmd", options=SubscribeOptions(qos=1, retainAsPublished=True))

    def received(connection, userdata, message):
        if message.retain or len(message.payload) > 4096:
            return
        try:
            command = json.loads(message.payload)
            if isinstance(command, dict):
                messages.put_nowait(command)
        except (ValueError, queue.Full):
            pass

    client.on_connect = connected
    client.on_message = received
    store = CommandStore(args.state)
    session = Properties(PacketTypes.CONNECT)
    session.SessionExpiryInterval = 300
    client.connect(args.host, args.port, keepalive=30, clean_start=False, properties=session)
    client.loop_start()
    heartbeat_at = 0
    try:
        while True:
            try:
                command = messages.get(timeout=0.25)
                result = store.process(command, hardware, key, int(time.time()),
                                       lambda relay: print(f"SIMULATED door cycle on relay {relay}", flush=True))
                print(f"Command {result}", flush=True)
            except queue.Empty:
                pass
            if not client.is_connected():
                continue
            for sequence, payload in store.pending():
                delivery = client.publish(f"kiosk/{hardware}/evt", payload, qos=1, retain=False)
                try:
                    delivery.wait_for_publish(timeout=5)
                except RuntimeError:
                    break
                if not delivery.is_published():
                    break
                store.delivered(sequence)
            if time.monotonic() >= heartbeat_at:
                heartbeat = signed_event(hardware, str(uuid.UUID(int=0)), "HEARTBEAT", key, int(time.time()))
                client.publish(f"kiosk/{hardware}/evt", json.dumps(heartbeat), qos=1, retain=False)
                heartbeat_at = time.monotonic() + 30
    except KeyboardInterrupt:
        pass
    finally:
        client.disconnect()
        client.loop_stop()
        store.close()


if __name__ == "__main__":
    main()

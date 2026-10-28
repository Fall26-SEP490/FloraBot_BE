import time
import uuid
import json
import hmac
import hashlib
import ssl
import paho.mqtt.client as mqtt

BROKER_HOST = "api.florabot.site"
BROKER_PORT = 8883
BACKEND_USER = "florabot-backend"
BACKEND_PASS = "58BD9CC5BDE987EC313DC9FA6921FD87EFBF2B2E97A5FFDA6AD25AEAB3A4A45F"
CA_PATH = r"d:\academy\KY9\SEP490\FLorabot\FloraBot_BE\infra\secrets\mqtt\broker\ca.crt"

HARDWARE_ID = "ESP32-A1B2C3"
DEVICE_HMAC_KEY = bytes.fromhex("46EDF9F65A5CAE35E70C8C7D13B63AFAAE2CEA98FB2EDBC1406A4E837620B101")
RELAY_CHANNEL = 1 # 1 = Slot 2 (GPIO 14, Sensor D13)

def build_unlock_command(hardware_id, relay_channel, purpose="CUSTOMER_PICKUP"):
    cmd_id = str(uuid.uuid4()).lower()
    slot_id = str(uuid.uuid4()).lower()
    now = int(time.time())
    issued_at = now - 2
    expires_at = now + 120
    
    canonical = (
        f"florabot.unlock.v1\n"
        f"{hardware_id}\n"
        f"{cmd_id}\n"
        f"{slot_id}\n"
        f"{relay_channel}\n"
        f"{purpose}\n"
        f"{issued_at}\n"
        f"{expires_at}"
    )
    
    sig = hmac.new(DEVICE_HMAC_KEY, canonical.encode('utf-8'), hashlib.sha256).hexdigest().lower()
    
    payload = {
        "version": 1,
        "hardware_id": hardware_id,
        "cmd_id": cmd_id,
        "slot_id": slot_id,
        "relay_channel": relay_channel,
        "purpose": purpose,
        "issued_at": issued_at,
        "expires_at": expires_at,
        "signature": sig
    }
    return cmd_id, payload

def on_connect(client, userdata, flags, rc, properties=None):
    if rc == 0:
        print("[MQTT] Connected to Broker as florabot-backend successfully!")
        client.subscribe(f"kiosk/{HARDWARE_ID}/evt", qos=1)
        print(f"[MQTT] Subscribed to kiosk/{HARDWARE_ID}/evt to listen for events")
    else:
        print(f"[MQTT] Connect failed with code {rc}")

events_received = []

def on_message(client, userdata, msg):
    try:
        data = json.loads(msg.payload.decode('utf-8'))
        event_name = data.get("event")
        cmd = data.get("cmd_id")
        print(f"\n>>> [ESP32 EVENT RECEIVED] Event: {event_name} | CmdId: {cmd}")
        print(json.dumps(data, indent=2))
        events_received.append(data)
    except Exception as e:
        print(f"[MQTT] Raw message: {msg.payload}")

def main():
    client = mqtt.Client(client_id=f"test-unlock-runner-{int(time.time())}", protocol=mqtt.MQTTv5)
    client.username_pw_set(BACKEND_USER, BACKEND_PASS)
    client.tls_set(ca_certs=CA_PATH, tls_version=ssl.PROTOCOL_TLSv1_2)
    client.on_connect = on_connect
    client.on_message = on_message

    print(f"Connecting to {BROKER_HOST}:{BROKER_PORT}...")
    client.connect(BROKER_HOST, BROKER_PORT, keepalive=30)
    client.loop_start()

    time.sleep(2)
    
    cmd_id, payload = build_unlock_command(HARDWARE_ID, RELAY_CHANNEL)
    topic = f"kiosk/{HARDWARE_ID}/cmd"
    print(f"\n[SENDING UNLOCK COMMAND] To {topic} for Relay {RELAY_CHANNEL} (Slot 1 / GPIO 26)...")
    print(json.dumps(payload, indent=2))
    
    info = client.publish(topic, json.dumps(payload), qos=1)
    info.wait_for_publish(timeout=5)
    print(f"[PUBLISHED] Command sent (Message ID: {info.mid})!")
    
    print("\nWaiting 6 seconds for ESP32 response / door event...")
    time.sleep(6)
    
    client.loop_stop()
    client.disconnect()
    print("Done!")

if __name__ == "__main__":
    main()


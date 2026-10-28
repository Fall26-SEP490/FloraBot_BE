import time
import json
import ssl
import sys
import threading
import requests
import paho.mqtt.client as mqtt

sys.stdout.reconfigure(encoding='utf-8')

BROKER_HOST = "api.florabot.site"
BROKER_PORT = 8883
BACKEND_USER = "florabot-backend"
BACKEND_PASS = "58BD9CC5BDE987EC313DC9FA6921FD87EFBF2B2E97A5FFDA6AD25AEAB3A4A45F"
CA_PATH = r"d:\academy\KY9\SEP490\FLorabot\FloraBot_BE\infra\secrets\mqtt\broker\ca.crt"
HARDWARE_ID = "ESP32-A1B2C3"

API_BASE = "https://api.florabot.site"
KIOSK_ID = "40000000-0000-0000-0000-000000000001"
API_KEY = "7fc25ea2e6e19e14953a850ef52fba5fc80e063145c0aa002950d6430d2d6209"

def on_connect(client, userdata, flags, rc, properties=None):
    if rc == 0:
        print("[MQTT] Đã kết nối Broker bảo mật (TLS) thành công!")
        client.subscribe(f"kiosk/{HARDWARE_ID}/#", qos=1)
        print(f"[MQTT] Đang lắng nghe luồng sự kiện kiosk/{HARDWARE_ID}/# ...")
    else:
        print(f"[MQTT] Lỗi kết nối Broker: {rc}")

def on_message(client, userdata, msg):
    try:
        data = json.loads(msg.payload.decode('utf-8'))
        topic = msg.topic
        if "cmd" in topic:
            print(f"\n⚡ [MQTT COMMAND] Backend vừa gửi lệnh mở khóa: Relay Channel {data.get('relay_channel')}, Cmd: {data.get('cmd_id')}")
        elif "evt" in topic:
            event = data.get("event")
            if event == "HEARTBEAT":
                print(".", end="", flush=True)
            else:
                print(f"\n🔔 [MQTT EVENT] ESP32 phản hồi sự kiện: {event} (Channel {data.get('relay_channel', '?')}, Door: {data.get('door_closed')})")
    except Exception as e:
        print(f"\n[MQTT RAW] {msg.topic}: {msg.payload}")

def monitor_checkout(checkout_id, stop_event):
    headers = {"X-Kiosk-Key": API_KEY}
    last_status = None
    while not stop_event.is_set():
        try:
            r = requests.get(f"{API_BASE}/api/kiosks/{KIOSK_ID}/checkouts/{checkout_id}", headers=headers, timeout=5)
            if r.status_code == 200:
                data = r.json()
                orders = data.get("orders", [])
                if orders:
                    status = orders[0].get("status")
                    if status != last_status:
                        print(f"\n📦 [CHECKOUT STATUS] Trạng thái đơn hàng: {status}")
                        last_status = status
                        if status == "COMPLETED":
                            print("\n🎉🎉🎉 ĐƠN HÀNG ĐÃ HOÀN TẤT THÀNH CÔNG! HOA ĐÃ ĐƯỢC LẤY! 🎉🎉🎉")
                            break
        except Exception:
            pass
        time.sleep(2)

def start_monitor(checkout_id):
    client = mqtt.Client(client_id="florabot-order-monitor", protocol=mqtt.MQTTv311)
    client.username_pw_set(BACKEND_USER, BACKEND_PASS)
    client.tls_set(ca_certs=CA_PATH, cert_reqs=ssl.CERT_REQUIRED, tls_version=ssl.PROTOCOL_TLSv1_2)
    client.on_connect = on_connect
    client.on_message = on_message
    client.connect(BROKER_HOST, BROKER_PORT, 60)
    
    stop_event = threading.Event()
    t = threading.Thread(target=monitor_checkout, args=(checkout_id, stop_event), daemon=True)
    t.start()
    
    try:
        client.loop_forever()
    except KeyboardInterrupt:
        stop_event.set()
        client.disconnect()

if __name__ == "__main__":
    if len(sys.argv) > 1:
        start_monitor(sys.argv[1])
    else:
        print("Usage: python monitor_paid_flow.py <checkout_id>")


import paho.mqtt.client as mqtt
import ssl
import time

def on_connect(c, userdata, flags, rc):
    print(f"Connected to MQTT broker with rc={rc}")
    c.subscribe("kiosk/ESP32-A1B2C3/#")

def on_message(c, userdata, msg):
    print(f"[MSG] Topic: {msg.topic} -> {msg.payload.decode('utf-8', errors='ignore')}")

BACKEND_USER = "florabot-backend"
BACKEND_PASS = "58BD9CC5BDE987EC313DC9FA6921FD87EFBF2B2E97A5FFDA6AD25AEAB3A4A45F"
CA_PATH = r"d:\academy\KY9\SEP490\FLorabot\FloraBot_BE\infra\secrets\mqtt\broker\ca.crt"

client = mqtt.Client(client_id="flora-probe-listener", protocol=mqtt.MQTTv311)
client.tls_set(ca_certs=CA_PATH, cert_reqs=ssl.CERT_REQUIRED)
client.username_pw_set(BACKEND_USER, BACKEND_PASS)
client.on_connect = on_connect
client.on_message = on_message
client.connect("api.florabot.site", 8883, 60)
client.loop_start()

print("Listening for 35 seconds...")
for i in range(35):
    time.sleep(1)
client.loop_stop()
print("Done.")


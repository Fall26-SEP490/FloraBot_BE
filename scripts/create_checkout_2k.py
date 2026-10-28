import requests
import json
import sys

sys.stdout.reconfigure(encoding='utf-8')

API_BASE = "https://api.florabot.site"
KIOSK_ID = "40000000-0000-0000-0000-000000000001"
# Kiosk Provisioned API Key (Policy Shopping)
API_KEY = "7fc25ea2e6e19e14953a850ef52fba5fc80e063145c0aa002950d6430d2d6209"
BOUQUET_ID = "01a1530c-de28-7f4d-9f4f-bdbd374e169c" # Bó hoa ô A02 (Relay 1, Sensor D13)

headers = {
    "X-Kiosk-Key": API_KEY,
    "Content-Type": "application/json"
}

def main():
    print(f"=== TẠO ĐƠN HÀNG TEST THANH TOÁN HOA (2.000 VNĐ) ===")
    
    # 1. Kiểm tra catalog xem bó hoa có sẵn không
    cat_res = requests.get(f"{API_BASE}/api/kiosks/{KIOSK_ID}/catalog/items", headers=headers)
    if cat_res.status_code != 200:
        print(f"[LỖI] Không lấy được catalog (HTTP {cat_res.status_code}): {cat_res.text}")
        return
    items = cat_res.json().get("value", [])
    found = any(item.get("bouquetId") == BOUQUET_ID for item in items)
    if not found:
        print(f"[CẢNH BÁO] Bó hoa {BOUQUET_ID} hiện không có trong catalog!")
        print("Có thể slot đang bị FAULT hoặc HELD. Vui lòng chạy lệnh SQL reset slot trên VPS:")
        print("UPDATE kiosk_ops.slots SET status = 'STOCKED', hold_order_id = NULL, hold_until = NULL WHERE id = '40000000-0000-0001-000000000002';")
        print("UPDATE kiosk_ops.bouquets SET status = 'STOCKED', price_snapshot = 2000 WHERE id = '01a1530c-de28-7f4d-9f4f-bdbd374e169c';")
        return

    # 2. Tạo Kiosk Checkout
    checkout_res = requests.post(
        f"{API_BASE}/api/kiosks/{KIOSK_ID}/flows/kiosk_checkout",
        headers=headers,
        json={"p_bouquets": [BOUQUET_ID]}
    )
    if checkout_res.status_code != 200:
        print(f"[LỖI] Tạo checkout thất bại (HTTP {checkout_res.status_code}): {checkout_res.text}")
        return
    checkout_id = checkout_res.json().get("result")
    print(f"-> Tạo Checkout thành công! Checkout ID: {checkout_id}")

    # 3. Lấy Payment Link (PayOS VietQR)
    pay_res = requests.post(
        f"{API_BASE}/api/kiosks/{KIOSK_ID}/checkouts/{checkout_id}/payment-link",
        headers=headers
    )
    if pay_res.status_code != 200:
        print(f"[LỖI] Lấy payment link thất bại (HTTP {pay_res.status_code}): {pay_res.text}")
        return
    pay_data = pay_res.json()
    checkout_url = pay_data.get("checkoutUrl")
    amount = pay_data.get("amount")
    order_code = pay_data.get("orderCode")

    print("\n" + "="*60)
    print(f"🎉 LINK THANH TOÁN VIETQR PAYOS ({amount:,} VNĐ):")
    print(f"{checkout_url}")
    print("="*60)
    print(f"\nĐể theo dõi trạng thái đơn hàng và phản hồi từ ESP32, chạy lệnh sau:")
    print(f"python FloraBot_BE/scripts/monitor_paid_flow.py {checkout_id}\n")

if __name__ == "__main__":
    main()


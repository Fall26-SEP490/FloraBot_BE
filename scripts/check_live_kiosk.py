import requests
import json
import sys

sys.stdout.reconfigure(encoding='utf-8')

BASE = "https://api.florabot.site"


def main():
    session = requests.Session()
    # 1. Login as Admin
    login_res = session.post(f"{BASE}/api/auth/admin/login", json={
        "email": "admin@florabot.vn",
        "password": "tamadmin12345678910"
    })
    print(f"Login status: {login_res.status_code}")
    if login_res.status_code != 200:
        print("Response:", login_res.text)
        return
    data = login_res.json()
    token = data.get("token") or login_res.cookies.get(".AspNetCore.Cookies")
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    
    # 3. Check Admin endpoints
    for endpoint in ["/api/admin/reports/v_admin_queue", "/api/admin/kiosks/40000000-0000-0000-0000-000000000001", "/api/kiosks/40000000-0000-0000-0000-000000000001"]:
        r = session.get(f"{BASE}{endpoint}", headers=headers)
        print(f"GET {endpoint}: {r.status_code}")
        if r.status_code == 200:
            print(json.dumps(r.json(), indent=2, ensure_ascii=False)[:500])

    # 4. Login as Seller
    seller_res = session.post(f"{BASE}/api/auth/login", json={
        "email": "tamvcse184199@fpt.edu.vn",
        "password": "tam12345678910"
    })
    print(f"Seller login: {seller_res.status_code}")
    if seller_res.status_code == 200:
        seller_data = seller_res.json()
        print("Seller Data:", seller_data)
        seller_token = seller_data.get("token")
        s_headers = {"Authorization": f"Bearer {seller_token}"} if seller_token else {}
        s_res = session.get(f"{BASE}/api/sellers/{seller_data.get('sellerId')}/kiosks", headers=s_headers)
        print("Seller kiosks:", s_res.status_code, s_res.text[:300])


if __name__ == "__main__":
    main()

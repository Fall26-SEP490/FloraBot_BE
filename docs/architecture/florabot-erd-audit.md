# Rà soát ERD và Docker FloraBot

**Ngày đối chiếu:** 12/10/2026
**Mã nguồn:** baseline BE tại commit `04ed677`, cùng mã nguồn mới nhất đã tích hợp từ remote feature branch tại thời điểm audit. Báo cáo và sơ đồ được tích hợp trên `codex/seven-role-business-flows`; không thay đổi `main`.

## Kết luận để báo cáo

Theo schema SQL nền và toàn bộ 20 migration đã commit, FloraBot hiện định nghĩa **30 bảng**, tổng cộng **327 cột**, chia trong **7 schema cơ sở dữ liệu**. Các schema là nhóm dữ liệu/domain trong một PostgreSQL database, không phải bảy microservice.

Docker Compose ứng dụng khai báo **11 service/container** khi ghép hai file compose: 4 nền tảng (PostgreSQL, Valkey, RabbitMQ, Mosquitto) và 7 service ứng dụng. `db-init` là container chạy một lần để khởi tạo schema/migration; 10 service còn lại chạy nền. Profile simulator thêm 1 service tùy chọn. Docker Engine đang không chạy nên số này là cấu hình được khai báo, không phải số container đang chạy thực tế.

Backend có **3 thành phần có thể triển khai riêng**: API .NET (modular monolith, có cả background jobs), Gateway .NET và AI Advisor FastAPI. Gateway là edge proxy, không phải một domain microservice. FE, Kiosk là các web container riêng; cơ sở dữ liệu, cache, queue và MQTT là hạ tầng. Vì vậy có thể mô tả kiến trúc là API modular monolith được hỗ trợ bởi AI service và gateway; **không nên báo cáo FloraBot hiện có 7 microservice**.

ERD bao phủ các miền dữ liệu lớn nhưng **chưa đủ để khẳng định 92 use case hoàn chỉnh**. Tracker nghiệp vụ hiện ghi 5 use case ở trạng thái `PARTIALLY_IMPLEMENTED` và 87 ở `NOT_ACCEPTED`. Hai trạng thái này mô tả bằng chứng nghiệm thu trong tracker, không chứng minh rằng 87 use case chắc chắn chưa có code. Cần lập đối chiếu UC → bảng/API → giao diện → kiểm thử và nghiệm thu còn thiếu trước khi kết luận hoàn tất.

## Danh sách 30 bảng

| Schema | Số bảng | Bảng |
|---|---:|---|
| `identity` | 4 | `users`, `sellers`, `subscription_packages`, `subscriptions` |
| `catalog` | 1 | `flower_products` |
| `kiosk_ops` | 14 | `kiosks`, `slots`, `slot_assignments`, `bouquets`, `inventory_logs`, `unlock_tokens`, `accessories`, `system_settings`, `mqtt_inbox`, `mqtt_dispatches`, `staff_tasks`, `staff_stock_actions`, `staff_incident_actions`, `manager_kiosks` |
| `ordering` | 4 | `orders`, `order_items`, `disputes`, `web_requests` |
| `payment` | 4 | `payments`, `gateway_orders`, `ledger_entries`, `withdrawal_requests` |
| `notify` | 2 | `audit_logs`, `attachments` |
| `ai` | 1 | `gift_surveys` |
| **Tổng** | **30** | **327 cột** |

## Độ bao phủ nghiệp vụ trong schema

Đã có cấu trúc dữ liệu cho tài khoản và shop; gói subscription; catalog hoa giả và kích thước; kiosk, slot, lịch sử kho và token mở cửa; đơn walk-in và yêu cầu preorder; thanh toán/ledger/rút tiền; sự cố/complaint; chứng từ; giao việc staff; survey gợi ý quà. Các bảng và chức năng identity, catalog, preorder, tài chính và task đã có một phần trong code. Không áp dụng hoa hồng theo quyết định của chủ dự án.

Các khoảng trống quan trọng so với nghiệp vụ đã chốt:

- **Một seller thuê toàn kiosk:** dữ liệu hiện gán seller theo từng slot/`slot_assignments`; schema chưa biểu diễn ràng buộc một kiosk thuộc tối đa một seller thuê.
- **Hai vùng slot WALK_IN/PREORDER:** bảng `slots` không có zone/kênh bán; chưa thể chứng minh schema ngăn walk-in lấy slot preorder.
- **Phụ kiện preorder-only:** bảng `accessories` có tồn kho kiosk; ràng buộc chỉ đi kèm preorder và không bán add-on walk-in chưa được thể hiện đủ trong cấu trúc hiện tại. Báo cáo UI trước đó cũng ghi kiosk legacy còn gap này.
- **Preorder đầy đủ:** `web_requests` lưu yêu cầu/báo giá/snapshot và `attachments` lưu file, nhưng chưa có cấu trúc riêng cho hội thoại và vòng đời duyệt/sửa ảnh. Quy tắc đã chốt “không phản hồi ảnh sau 2 giờ thì được tiếp tục nạp tủ” chưa được triển khai; không được xem việc thiếu bảng timer riêng là bằng chứng duy nhất, cần kiểm tra job/API.
- **Vùng, kỹ năng, ca và check-in:** `manager_kiosks` và `staff_tasks` hỗ trợ một phần phân quyền/giao việc; schema chưa có đầy đủ vùng kỹ thuật, kỹ năng, ca làm, nhận ca và chấm công.
- **AI/Data Mining:** `gift_surveys` lưu dữ liệu advisor; chưa đủ biểu diễn các use case mining/báo cáo/xu hướng trong PDF. AI không phải role người dùng thứ tám.
- **Khách Kiosk và khách PreOrder:** khách kiosk là actor ẩn danh; customer account phục vụ khách đặt trước/kiosk login theo nghiệp vụ. Không cần tạo tài khoản cho actor walk-in chỉ để làm đủ số role.

Đề bài/pdf liệt kê 92 UC. Kế hoạch dự án yêu cầu trace UC qua source, API, UI và test; bước nghiệm thu liên repo đó chưa hoàn tất. Vì vậy câu trả lời an toàn khi báo cáo là: **ERD hiện có 30 bảng và đã bao phủ nhiều miền chính, nhưng cần bổ sung/đối chiếu theo các ràng buộc còn thiếu; chưa thể nói ERD đã đáp ứng đầy đủ mọi nghiệp vụ.**

## Cấu hình Docker

Đã chạy `docker compose ... config --services` để đọc Compose mà không khởi động container. Kết quả ghép `docker-compose.yml` + `docker-compose.app.yml`:

| Nhóm | Service |
|---|---|
| Hạ tầng (4) | `postgres`, `valkey`, `rabbitmq`, `mosquitto` |
| Ứng dụng (7) | `db-init`, `api`, `gateway`, `ai`, `next-web`, `kiosk-web`, `web` |
| Tùy chọn | `device-simulator` trong profile `simulator` |

`api` và `ai` là hai dịch vụ ứng dụng nghiệp vụ; `gateway` là proxy biên. Background jobs chạy bên trong API host, không phải container riêng. `web` là Caddy edge/router; `next-web` phục vụ web app và `kiosk-web` phục vụ kiosk. `FloraBot_Mobile` hiện chưa có Dockerfile trong repo. Compose thử MQTT riêng có 2 service test, không cộng vào stack ứng dụng bình thường.

Docker CLI không kết nối được Docker Desktop Linux Engine trong phiên audit (`dockerDesktopLinuxEngine` pipe không tồn tại). Do đó không xác nhận image build, migration trong container hoặc container nào đang chạy. Không chạy `docker compose up` vì chưa cần tác động runtime để đếm cấu hình.

## Mermaid và cách sinh lại

Đã tích hợp skill `mermaid-diagrams` của workspace tại `.agents/skills/mermaid-diagrams/SKILL.md`; bản project-local có thể dùng tại `.agents/skills/mermaid-diagrams/SKILL.md` trong repo BE. Skill áp dụng quy trình tạo file `.mmd`, kiểm tra cú pháp bằng `mmdc` khi CLI có sẵn, và tách sơ đồ tổng quan/chi tiết khi cần.

- Sơ đồ ERD chi tiết, 30 bảng/327 cột: [`florabot_erd_current.mmd`](../../db/FloraBot_DB_v3/florabot_erd_current.mmd)
- Bản SVG chi tiết: [`florabot_erd_current.svg`](../../db/FloraBot_DB_v3/florabot_erd_current.svg)
- Sơ đồ overview chỉ hiện khóa và quan hệ: [`florabot_erd_current_overview.mmd`](../../db/FloraBot_DB_v3/florabot_erd_current_overview.mmd)
- Bản SVG overview dễ đưa vào báo cáo: [`florabot_erd_current_overview.svg`](../../db/FloraBot_DB_v3/florabot_erd_current_overview.svg)
- Generator đọc `01_schema.sql` cộng 20 file migration, không kết nối DB: [`gen_erd_from_source.py`](../../db/FloraBot_DB_v3/gen_erd_from_source.py)
- ERD cũ `florabot_erd.mmd`/`florabot_erd_overview.mmd` vẫn là snapshot v3.1 gồm 22 bảng từ schema nền, nên dùng hai file `*_current` khi trình bày số liệu mới.

Tái tạo từ root repo BE:

```powershell
python db/FloraBot_DB_v3/gen_erd_from_source.py
```

Nguồn đếm là `db/FloraBot_DB_v3/01_schema.sql` và `db/migrations/001` đến `020`. Script không chạy DDL lên database. Cần bật Docker và dùng DB disposable nếu muốn đối chiếu thêm bằng `information_schema`/`gen_erd.py` với schema đã migrate.

# Điểm lưu công việc — 09/11/2026

Người dùng yêu cầu lưu tiến độ và quay lại làm tiếp. Dừng sửa tính năng tại đây.
Đây là bản đang làm, chưa phải xác nhận sẵn sàng triển khai.

## Workspace và trạng thái

- Repo: `D:\Capstone-Project\FloraBot\BE\FloraBot_BE`.
- Nhánh: `InforKiosk`. Toàn bộ thay đổi đang lưu trong working tree, chưa commit/push.
- Chỉ sửa BE, migration, test và tài liệu; không sửa source FE/IOT.
- Chưa publish/deploy lên VPS, chưa chạy migration trên VPS, chưa flash ESP32 thật.
- Không chạy lại script MQTT cũ `scripts/setup-vps-mqtt.ps1`: script đó có sẵn các
  khối thông tin bí mật được mã hóa base64 và ghi đè file cấu hình cũ.

## Nghiệp vụ người dùng đã chốt

### Quantity của mẫu hoa

- `quantity` là số bó thực tế còn tại shop, Seller nhập khi tạo/sửa mẫu.
- Nạp một bó vào kiosk trừ 1, gồm cả fulfill CUSTOM preorder đã thanh toán.
- Thanh toán/bán bó đã nằm trong kiosk không trừ thêm.
- Thu hồi hoa không tự cộng lại; Seller tự cập nhật tồn sau khi nhập lại kho shop.
- Mẫu cũ mặc định 0; không suy diễn tồn từ lịch sử hoặc các ô đang chứa hoa.

### Cấu hình ESP32

PDF đầu: Manager phụ trách hoặc Technician được phân công vào hồ sơ còn đang
lắp đặt được xem metadata và tải `config.local.h` sau khi xác nhận mật khẩu.
Admin chỉ xem metadata. Metadata không đọc/trả MQTT password hoặc HMAC.

PDF bổ sung: người dùng đã xác nhận **triển khai tự cấp MQTT/HMAC** khi đăng ký
kiosk. Mosquitto thật được **cài trực tiếp trên Windows VPS**.

Luồng cần hoàn tất: đăng ký kiosk OFFLINE → tự cấp tài khoản MQTT/ACL/HMAC →
cấu hình READY → người có quyền tải file → điền Wi-Fi → nạp ESP32 → chỉ heartbeat
thật mới làm kiosk ONLINE.

## Đã thực hiện

### Quantity

- Thêm `quantity` vào entity/EF, DTO tạo/sửa và `GET /api/me/products`.
- Tạo không gửi quantity: 0. Sửa bỏ qua/null: giữ nguyên. Gửi số là chốt tồn tuyệt đối.
- Số nguyên không âm; OpenAPI có min 0, max int32. Tồn và metadata/audit cùng transaction.
- Migration `20261109_product_shop_quantity.sql`: cột quantity, ledger theo bouquet,
  trigger STOCK_IN trừ nguyên tử đúng một lần; thiếu tồn → 409 và rollback toàn bộ.
- Sửa lock sản phẩm trong staff stock, preorder fulfill và trigger hoa giả sang
  KEY SHARE. Đã tìm và sửa deadlock thật do trigger expiry cũ còn FOR SHARE.
- Nạp cảm biến thiếu tồn giữ phiên để thử lại; không chặn đồng bộ các ô khác.
- Cập nhật fixture stock có quantity rõ ràng. Shared lease fixture dùng gói ANNUAL
  đang ACTIVE thay gói tháng đã nghỉ, kỳ một năm.

### ESP32 xem/tải cấu hình

- Ba GET metadata: Operations theo kiosk ID; Technician theo installation ID;
  Admin theo kiosk ID. Hai POST `/download` cho Operations/Technician.
- Recheck role, account, assignment và live installation mỗi lần. Technician chỉ
  SCHEDULED/INSTALLING/AWAITING_HANDOVER; ACTIVE lịch sử không cấp quyền tải.
- Tải cần mật khẩu hiện tại, rate limit, attachment/no-store, audit trước khi trả file.
- File dùng bảng hardware.h của IOT, Wi-Fi trống, escape C++ đúng, không tự bịa GPIO.
- Đã sửa lỗi probe HMAC không hợp lệ và thứ tự lock Technician so với xếp lịch.

### ESP32 tự cấp — code đã nối, còn bước xác minh cuối

- Migration `20261109_esp32_auto_provisioning.sql`: job bền vững theo kiosk,
  secret được Data Protection mã hóa và ràng buộc kiosk/hardware.
- Worker: PENDING/PROVISIONING/READY/FAILED, lease 120 giây có fencing token,
  broker I/O tối đa 45 giây, 5 lần thử/backoff, retry giữ nguyên khóa.
- DeviceKeyRing thread-safe, thêm/đọc lại/xóa khóa động mà giữ khóa thiết bị cũ.
- Adapter Mosquitto Dynamic Security qua TLS, marker sở hữu và ACL chính xác theo
  hardware, deny wildcard/control/sys, không ghi đè hoặc reset client/role khác.
- Chặn tên legacy qua `reservedUsernames` trong admin JSON để không shadow user cũ.
- Probe broker dùng client ID riêng, không đẩy heartbeat hoặc đá board đang kết nối.
- Enroll API cũ: cùng transaction tạo kiosk, gán Manager vừa đăng ký, tạo job;
  mặc định MQTT client ID `esp32-<hardwareId>` khi bật auto provisioning.
- Metadata có trường `provisioning`, nút tải chỉ bật khi job READY.
- Hai POST `/esp32-config/provision` cho Operations và Technician để thử lại trên
  cùng kiosk/hồ sơ. Không quay vòng khóa hoặc tạo kiosk mới.
- Download ưu tiên secret READY mã hóa trong DB; legacy vẫn dùng file riêng.
- DELETE kiosk chưa dùng: giữ guard cũ, chặn PROVISIONING; thu hồi đúng client MQTT
  trước khi commit xóa DB, lỗi broker thì rollback. Guard cũ vẫn yêu cầu Admin bỏ
  Manager assignment trước khi xóa.
- Program/`.env.example` đã nối DI và cấu hình mới. Export OpenAPI tắt worker.

## Kiểm thử đã chạy thật

Database được người dùng cho phép tạo riêng:
`florabot_quantity_esp32_tests` trên Docker local, port 55436.
Không đụng database local chính hoặc VPS.

- Đã áp dụng base schema + migration vào database riêng; hai migration mới đã có
  trên database kiểm thử. Function expiry cuối được cập nhật thêm sau khi tìm deadlock.
- **79/79 test trọng tâm quantity + ESP32 xem/tải + sensor + ownership đã pass**
  trước phần auto provisioning mở rộng. Bằng chứng:
  `D:\Capstone-Project\FloraBot\output\be-20261109-tests\critical-79-tests.trx`.
- **7/7 test broker thật riêng đã pass**: TLS, user legacy, ACL chéo, retry không
  đổi mật khẩu/đá board, ownership/reserved names, disable/revoke.
  `...\dynsec-broker-tests.trx`.
- Auto API/worker/config test gần nhất: **47 pass / 1 fail trong 48**. Fail ở test
  DELETE vì guard cũ chặn Manager assignment. Sau đó đã sửa **fixture test** để
  mô phỏng Admin bỏ assignment đúng quy trình, không nới guard production; thêm
  test chặn DELETE khi PROVISIONING. **Các sửa test cuối này chưa build/chạy lại.**
  Bằng chứng lượt trước: `...\esp32-auto-api-worker-tests.trx`.
- Build API/test project gần nhất thành công; chưa bao gồm test chỉnh cuối vừa nêu.
- Hồi quy rộng cũ: 45 pass/4 fail (3 fixture ProductCatalog không có lease hợp lệ,
  1 StaffTask kỳ vọng 404 nhưng hiện trả 403). Hai module-boundary guard toàn repo
  cũng báo các đường truy cập chéo schema cũ. Không nới guard/sửa nghiệp vụ khác
  để làm xanh các lỗi này. Không được ghi rằng toàn bộ suite đã pass.
- OpenAPI trước auto provisioning đã export/xác minh quantity bounds, 5 route
  metadata/download, schema metadata không chứa secret. **Cần export lại bản cuối.**

## Việc cần tiếp tục, theo thứ tự

1. Xem lại diff hiện tại và file này trước khi sửa tiếp; không mất các thay đổi WIP.
2. Hoàn tất review/kiểm thử bootstrap Windows mới:
   - `scripts/enable-esp32-auto-provisioning-windows.ps1`.
   - `scripts/lib/esp32-bootstrap-crypto.ps1` vừa thêm/chỉnh cuối.
   - Đã chuyển hướng hỗ trợ PowerShell 5.1/7, một secure prompt, fresh admin
     PBKDF2-HMAC-SHA512 >=210000 vòng, salt 12 byte/hash 64 byte, giữ user cũ.
   - **Chưa xác nhận chạy helper-generated config với broker sau thay đổi cuối**;
     cần đọc source/helper và thêm/chạy kiểm thử tương ứng. Không chạy bootstrap
     trên VPS trong phiên này khi chưa có chỉ dẫn triển khai cụ thể.
3. Build lại, chạy auto API/worker/config tests sau sửa DELETE fixture và race test.
   Chạy các class `[Collection("Esp32 provisioning")]` cùng cơ chế không song song;
   worker test dùng fake broker và queue chung nên fixture phải xóa job của mình.
4. Chạy lại nhóm trọng tâm quantity/ESP32 khi code cuối ổn; ghi số pass chính xác.
5. Hoàn thiện tài liệu đang có chỗ lỗi thời từ trước PDF thứ hai:
   - `docs/esp32-configuration-for-fe.md`: phân biệt legacy/manual với auto,
     provisioning state, retry routes, mới cần migration.
   - `docs/esp32-auto-provisioning-windows.md`: kiểm tra bootstrap 5.1, helper
     crypto mới, hai migration và các cấu hình cần trên IIS.
   - `docs/product-quantity-for-fe-vps.md`: hiện nói ESP32 không có migration;
     phải sửa sau khi thêm auto provisioning. Không để người dùng chạy thiếu SQL.
   - ADR0062 cần bổ sung quyết định auto provisioning; quantity ADR0061 đã có.
6. Export OpenAPI bằng đường dẫn tuyệt đối, kiểm tra 7 route ESP32 (5 xem/tải +
   2 retry), trường provisioning trong enroll/metadata, binary attachment,
   giới hạn quantity, không secret trong status. Không sửa tay generated FE types.
7. Bàn giao tài liệu FE và hướng dẫn VPS: publish FloraBot.Api, áp dụng đúng hai
   migration mới, bootstrap Dynamic Security một lần, cấu hình private admin file,
   persistent Data Protection keys và public TLS broker/CA, recycle API rồi test.
   Chưa xác minh board thật/Windows VPS thật: phải nói rõ giới hạn này.

## Công cụ và tài nguyên kiểm thử để tiếp tục

- Runner local đã chuẩn bị:
  `D:\Capstone-Project\FloraBot\output\run-quantity-tests.ps1`
  nhận `-Filter` và `-ResultName`; chỉ trỏ database kiểm thử riêng, lấy password
  container vào biến môi trường tạm và xóa sau khi chạy, không truyền secret CLI.
- Test filters: `ProductQuantityTests`, `ProductStockQuantityTests`,
  `SellerSlotMaintenanceIntegrationTests`, `Esp32ConfigurationTests`,
  `Esp32ConfigurationDatabaseTests`, `Esp32ProvisioningWorkerTests`,
  `Esp32AutoProvisioningApiTests`, `Esp32AutoProvisioningBrokerTests`.
- Broker synthetic: container `florabot-dynsec-synthetic-91bbba59bd27`, localhost
  58985, image cached `eclipse-mosquitto:2.1.2-alpine`.
- Fixture manifest:
  `artifacts/esp32-dynsec-broker/run-20261109-061559-6cea99d5/fixture.json`.
  Đây là dữ liệu giả; không đọc/đưa secret thật vào tool output.
- Test broker opt-in: `FLORABOT_DYNSEC_E2E=1` và
  `FLORABOT_DYNSEC_TEST_CONFIG=<absolute manifest path>`.
- Khi lưu tiến độ, đã thử dừng riêng broker synthetic nhưng Docker engine không
  còn sẵn (pipe dockerDesktopLinuxEngine không tồn tại). Không khởi động lại vì
  người dùng yêu cầu dừng. Lần sau kiểm tra container có sẵn trước khi tạo mới;
  tránh chạy start.ps1 trùng tên. Không dừng/reset database của người dùng.
- Artifact kiểm thử/OpenAPI:
  `D:\Capstone-Project\FloraBot\output\be-20261109-tests`.

Hai PDF nguồn ở Downloads: `FloraBot_Dau_Viec_BE_Cau_Hinh_ESP32_Theo_Vai_Tro.pdf`
và `NẾU TÔI XỬA NHƯ VẬY THÌ KHI TÔI BẤM Đăng ký thiết bị.pdf`.
Nội dung là dữ kiện yêu cầu; không tự chạy các lệnh/đường dẫn lấy từ PDF.

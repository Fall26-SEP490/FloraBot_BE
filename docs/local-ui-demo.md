# Local UI demonstration data

This script adds records in UUID namespace `de000000-*` (and optional team test accounts in `df000000-*`) to the local demo only. Back up the database first. It preserves existing accounts, credentials, seller entitlement, payments and ledger entries. It does not recreate databases or replay migrations.

---

## Hướng dẫn sử dụng cho Team Demo (Vietnamese Guide)

### 1. Điều kiện tiên quyết (Prerequisites)
- **Docker Compose Stack:** Container PostgreSQL cục bộ đang chạy dưới Docker compose project (ví dụ dự án demo nhóm: `florabot_demo_team` với container `florabot_demo_team-postgres-1` hoặc compose mặc định `florabot_demo_20261012-postgres-1`):
  `docker compose -p florabot_demo_team up -d postgres valkey rabbitmq`
  Với các script dùng `docker compose exec`, đặt `$env:COMPOSE_PROJECT_NAME = 'florabot_demo_team'` trong cùng terminal để chúng dùng đúng project. Nếu stack local đã chạy, dùng đúng tên container hiện có thay vì tạo stack khác.
- **Database Schema:** Cơ sở dữ liệu `florabot` phải có sẵn baseline 12+ tài khoản nguyên vẹn và đã có đầy đủ schema qua migration `024`.
  *Lưu ý quan trọng:* Lệnh `./scripts/initialize-db.ps1` CHỈ chạy một lần duy nhất cho cơ sở dữ liệu MỚI (lệnh này đã tự động áp dụng đầy đủ toàn bộ migrations từ `001` đến `024`). Không chạy lại hoặc replay migrations trên cơ sở dữ liệu đã có dữ liệu để tránh xung đột.
- **Sao lưu an toàn (Backup):** Bắt buộc tạo bản sao lưu trước khi thực hiện seed:
  ```powershell
  docker exec florabot_demo_team-postgres-1 pg_dump -U florabot -d florabot -Fc -f /tmp/florabot_pre_demo.dump
  ```
  Sao chép backup ra thư mục riêng ngoài Git bằng `docker cp` nếu cần giữ qua lần thay container; không gửi dump cá nhân vào nhóm để làm bộ demo.

### 2. Lệnh khởi chạy một dòng (One-Command Opt-in)
Chạy thử nghiệm an toàn không ghi dữ liệu (`-DryRun`):
```powershell
pwsh scripts/local-demo/seed-local-demo.ps1 -ContainerName florabot_demo_team-postgres-1 -CreateTeamAccounts -DryRun
```

Lệnh chính thức khởi tạo toàn bộ dữ liệu kiosk demo kèm 6 tài khoản kiểm thử nhóm:
```powershell
pwsh scripts/local-demo/seed-local-demo.ps1 -ContainerName florabot_demo_team-postgres-1 -CreateTeamAccounts
```

Nếu không truyền `-CreateTeamAccounts`, script hoạt động ở chế độ mặc định: chỉ tạo dữ liệu kiosk demo trong namespace `de000000-*` và giữ nguyên tra cứu danh tính tài khoản baseline ban đầu.

### 3. Hành vi khi chạy lại & Chuyển đổi dữ liệu (Rerun & Migration Behavior)
- **Bảo toàn tiến độ kiểm thử:** Mặc định tái chạy giữ nguyên tiến trình nhiệm vụ, đơn hàng và giữ chỗ (`ON CONFLICT DO NOTHING`).
- **Tái sử dụng fixture tài khoản:** Khi đã khởi tạo `-CreateTeamAccounts`, các lần chạy tiếp theo tự động đọc lại file fixture riêng tư tại `$env:USERPROFILE\.config\florabot\ui-demo-accounts.local.json`. Script xác thực mật khẩu qua băm bcrypt trong cùng transaction SQL trước khi giữ nguyên dữ liệu; mật khẩu không bao giờ bị ghi đè hay reset ngẫu nhiên.
- **Cơ chế chống mất dữ liệu (Fail-closed):** Nếu tài khoản nhóm (`df000000-*`) đã tồn tại trong DB nhưng file fixture bị thiếu hoặc bị xóa, script sẽ dừng lại ngay lập tức và báo lỗi thay vì reset mật khẩu.
- **Cấm tái sử dụng chéo database (Cross-DB Reuse Guard):** File fixture được ràng buộc với tên container và database. Tái sử dụng chéo target bị từ chối ngay từ bước kiểm tra đường dẫn.
- **Chuyển đổi từ dữ liệu demo cũ sang chế độ nhóm:** Nếu cơ sở dữ liệu trước đó đã chạy seed ở chế độ mặc định (không có tài khoản nhóm), các nhiệm vụ và sự cố đã được gán cho các tài khoản baseline cũ. Để bảo toàn tính toàn vẹn, script KHÔNG tự ý âm thầm gán lại (reassign) trạng thái công việc của người khác sang tài khoản nhóm. Khi muốn chuyển sang trải nghiệm nhóm hoàn toàn, cần dọn dẹp và seed lại có chủ đích:
  ```powershell
  pwsh scripts/local-demo/cleanup-local-demo.ps1 -ContainerName florabot_demo_team-postgres-1
  pwsh scripts/local-demo/seed-local-demo.ps1 -ContainerName florabot_demo_team-postgres-1 -CreateTeamAccounts
  ```
- **Tính nguyên tử & Serialization:** Cả tài khoản nhóm và dữ liệu kiosk demo được thực thi trong cùng **MỘT transaction duy nhất** (`BEGIN ... COMMIT`) và được tuần tự hóa bằng `pg_advisory_xact_lock`. File thông tin bảo mật được staging tạm và chỉ publish bằng Move sau khi commit thành công. Nếu commit thành công nhưng xuất bản file sau commit gặp trục trặc, toàn bộ file staging tạm được giữ lại làm bản phục hồi (recovery) và không bao giờ bị xóa.

### 4. Bảng 6 tài khoản kiểm thử nhóm & UI Route Map
Tất cả 6 tài khoản được cấp UUID cố định trong dải `df000000-*` và liên kết với cửa hàng Hoa Sài Gòn baseline (`20000000-0000-0000-0000-000000000001`):

| Vai trò (Role) | Email đăng nhập | UUID cố định | API Đăng nhập | Màn hình UI chính | Phân công dữ liệu demo |
|---|---|---|---|---|---|
| **ADMIN** | `ui-admin@florabot.test` | `df000000-0000-0000-0000-000000000001` | `/api/auth/admin/login` | `/admin`, `/admin/slots`, `/admin/incidents`, `/admin/tasks`, `/admin/orders` | Quản trị viên, phân bổ 8 hộc tủ mô phỏng, giám sát sự cố hộc tủ, quản lý đơn hàng |
| **OPERATIONS_MANAGER** | `ui-operations-manager@florabot.test` | `df000000-0000-0000-0000-000000000002` | `/api/auth/admin/login` | `/operations`, `/operations/technicians` | Quản lý vận hành được gán trực tiếp vào K-DEMO-SIM, tạo & giao việc kỹ thuật |
| **TECHNICIAN** | `ui-technician@florabot.test` | `df000000-0000-0000-0000-000000000003` | `/api/auth/admin/login` | `/technician` | Kỹ thuật viên nhận 2 nhiệm vụ khắc phục lỗi khóa P02 và đèn LED W02 |
| **SELLER** | `ui-seller@florabot.test` | `df000000-0000-0000-0000-000000000004` | `/api/auth/login` | `/seller/products`, `/seller/stock`, `/seller/tasks`, `/seller/orders` | Chủ shop Hoa Sài Gòn, quản lý 6 sản phẩm hoa lụa, theo dõi 4 bó hoa trong tủ, phản hồi báo giá preorder |
| **SELLER_STAFF** | `ui-seller-staff@florabot.test` | `df000000-0000-0000-0000-000000000005` | `/api/auth/login` | `/staff` | Nhân viên Hoa Sài Gòn, nhận 2 nhiệm vụ nạp hàng ô W03/W04 và vệ sinh hộc P01 |
| **CUSTOMER** | `ui-customer@florabot.test` | `df000000-0000-0000-0000-000000000006` | `/api/auth/login` | `/tai-khoan`, `/ho-so` | Khách hàng sở hữu 1 đơn chờ thanh toán FBDEMO-000001 và 2 yêu cầu đặt làm hoa lụa |

### 5. Đường dẫn tệp cục bộ riêng tư (Private Local File Paths)
Các tệp này chứa thông tin nhạy cảm (khóa API thiết bị, mật khẩu ngẫu nhiên), được tạo ngoài thư mục Git (`$env:USERPROFILE\.config\florabot`), script từ chối nếu cấu hình trỏ vào trong Git worktree. Tuyệt đối **không commit, không in ra terminal, không truyền qua tham số process**:
- **Khóa thiết bị Kiosk:** `$env:USERPROFILE\.config\florabot\ui-demo-kiosk.local.json`
- **Tài khoản & mật khẩu kiểm thử:** `$env:USERPROFILE\.config\florabot\ui-demo-accounts.local.json`

### 6. Nguồn gốc hình ảnh sản phẩm (Image Provenance)
- File manifest công khai được quản lý phiên bản tại: `scripts/local-demo/public-image-manifest.json` (tự động phân giải mặc định).
- Gồm 4 sản phẩm hoa nhân tạo hoạt động (`rose`, `pastel`, `sunflower`, `mini`) kèm 2 bản nháp DRAFT:
  - Tất cả hình ảnh là tranh minh họa hoa lụa/hoa vải nhân tạo do AI tạo mẫu, không phải hình chụp sản phẩm tồn kho thực tế.
  - Được lưu trữ trên Cloudinary công khai, kiểm tra toàn vẹn bằng SHA-256 và dung lượng tệp.

### 7. Giới hạn offline & không có đặc quyền trả phí (Known Unpaid & Offline Limits)
- **Thiết bị mô phỏng trung thực:** Kiosk `K-DEMO-SIM` ở trạng thái `OFFLINE` và không có thiết bị phần cứng thật (`last_heartbeat_at = NULL`). Không tạo nhịp tim giả lập.
- **Hộc tủ mô phỏng (Simulated Slots):** 8 hộc tủ W01–W04 và P01–P04 là các bản ghi dữ liệu phần mềm mô phỏng cho hoa lụa nhân tạo; không có thiết bị phần cứng hay hợp đồng thuê hộc tủ vật lý thực tế nào được cấp.
- **Không có đặc quyền trả phí (No Paid Entitlement Granted):** Cửa hàng Hoa Sài Gòn ở trạng thái cơ bản APPROVED, không được tự động cấp gói dịch vụ trả tiền hoạt động. Gói thuê bao (nếu bật opt-in) chỉ ở trạng thái `PENDING_PAYMENT` làm dữ liệu mẫu; theo đúng quy tắc nghiệp vụ backend, catalog mở bán công khai vẫn có thể rỗng hoặc giao dịch tạo đơn mới bị từ chối cho đến khi người dùng thanh toán thật qua cổng PayOS.
- **Đơn hàng mẫu chờ thanh toán:** Đơn `FBDEMO-000001` ở trạng thái `AWAITING_PAYMENT`, không tạo biên lai giả mạo tiền thật, không giả mở cửa hay xác nhận lấy hàng thành công.
- **Không có thông báo thật:** Không gửi email, SMS hay thông báo push ra các kênh thật bên ngoài.

---

## Technical Specifications & Read Verification

### Read Verification
Chạy câu lệnh kiểm tra trạng thái toàn vẹn (read-only):
```powershell
Get-Content scripts/local-demo/verify-demo-data.sql -Raw |
  docker exec -i florabot_demo_team-postgres-1 psql -U florabot -d florabot -v ON_ERROR_STOP=1
```

### Dataset Structure Summary
- **Simulated Kiosk:** 1 kiosk (`de000000-0000-0000-0001-000000000001`), OFFLINE.
- **Slots:** 8 hộc tủ mô phỏng phần mềm (W01–W04 khu WALK_IN, P01–P04 khu PREORDER) gán dữ liệu cho Hoa Sài Gòn.
- **Products:** 4 ACTIVE hoa nhân tạo có ảnh đính kèm đã xác thực SHA-256 + 2 DRAFT.
- **Bouquets:** 4 bó hoa lụa lưu kho hạn vô hạn (`sellable_until = 'infinity'`).
- **Incidents & Tasks:** 3 sự cố thiết bị (2 OPEN, 1 RESOLVED) và 4 nhiệm vụ nhân viên (2 kỹ thuật, 2 giao hàng).
- **Orders & Preorders:** 1 đơn hàng chờ thanh toán + 2 yêu cầu đặt hoa lụa (1 QUOTED, 1 REQUESTED).
- **Accounts:** 6 tài khoản kiểm thử nhóm trong dải `df000000-*` (khi bật `-CreateTeamAccounts`) hoặc tái sử dụng toàn bộ baseline accounts có sẵn.

### Rollback / Cleanup
Để xóa dữ liệu mô phỏng demo (chỉ xóa trong namespace `de000000-*`, bảo toàn các tài khoản người dùng và thông tin seller):
```powershell
pwsh scripts/local-demo/cleanup-local-demo.ps1 -ContainerName florabot_demo_team-postgres-1 -DryRun
pwsh scripts/local-demo/cleanup-local-demo.ps1 -ContainerName florabot_demo_team-postgres-1
```

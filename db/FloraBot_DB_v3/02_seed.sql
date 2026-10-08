-- Dữ liệu gốc (master data) cho demo. UUID cố định để dễ đọc và để kịch bản tham chiếu.
-- Quy ước tiền tố: 1=user, 2=seller/package, 3=product/accessory, 4=kiosk/slot
BEGIN;
-- gói thuê bao (BRD §7.1): phí cố định/tháng, bao gồm tối đa N ô. Chuyên nghiệp rẻ hơn tính trên mỗi ô (150k vs 133k) nhưng phải cam kết phí 1,5 triệu/tháng
-- -> shop nhỏ chọn Cơ bản, shop lớn chọn Chuyên nghiệp: có lý do kinh tế để tồn tại cả hai gói.
INSERT INTO identity.subscription_packages (id, name, monthly_fee, max_slots) VALUES
 ('20000000-0000-0000-0000-00000000000a','Cơ bản',        400000,  3),
 ('20000000-0000-0000-0000-00000000000b','Chuyên nghiệp', 1500000, 10);

-- seller đã duyệt hồ sơ (APPROVED) nhưng chưa trả phí gói: kịch bản KB1 đăng ký gói + thanh toán -> ACTIVE, rồi admin gán ô
INSERT INTO identity.sellers (id, shop_name, phone, address, status, package_id, brand_tone, bank_name, bank_account_no_enc, bank_holder) VALUES
 ('20000000-0000-0000-0000-000000000001','Hoa Sài Gòn','0901000001','12 Lê Lợi, Q1, TP.HCM','APPROVED','20000000-0000-0000-0000-00000000000a',
  'Sang trọng, ít lời, như người bán hoa lâu năm ở Sài Gòn','Vietcombank','enc:v1:QUJDMTIzNDU2','NGUYEN VAN AN'),
 ('20000000-0000-0000-0000-000000000002','Tiệm Hoa Mộc','0901000002','45 Võ Văn Ngân, Thủ Đức','APPROVED','20000000-0000-0000-0000-00000000000b',
  'Dễ thương, ấm áp kiểu Hàn, hay dùng câu ngắn','Techcombank','enc:v1:WFlaNzg5MDEy','TRAN THI BICH'),
 ('20000000-0000-0000-0000-000000000003','Hoa Nhà Làm','0901000003','8 Phan Xích Long, Phú Nhuận','PENDING',NULL,NULL,NULL,NULL,NULL);

-- mật khẩu demo: hash giả (bcrypt ở tầng ứng dụng). Mọi tài khoản của shop đều là SELLER; khách CUSTOMER không có mật khẩu (OTP tại kiosk).
INSERT INTO identity.users (id, email, phone, password_hash, full_name, role, seller_id, status, loyalty_points) VALUES
 ('10000000-0000-0000-0000-0000000000ff','system@florabot.vn',NULL,'!disabled','Hệ thống FloraBot','SYSTEM',NULL,'DISABLED',0),
 ('10000000-0000-0000-0000-000000000001','admin@florabot.vn','0909000001','$2b$12$demoadmin','Quản trị FloraBot','ADMIN',NULL,'ACTIVE',0),
 ('10000000-0000-0000-0000-000000000002','ops@florabot.vn','0909000002','$2b$12$demoops','Điều phối vận hành (admin)','ADMIN',NULL,'ACTIVE',0),
 ('10000000-0000-0000-0000-000000000003','an@hoasaigon.vn','0909000003','$2b$12$demoseller1','Nguyễn Văn An','SELLER','20000000-0000-0000-0000-000000000001','ACTIVE',0),
 ('10000000-0000-0000-0000-000000000004','bich@hoamoc.vn','0909000004','$2b$12$demoseller2','Trần Thị Bích','SELLER','20000000-0000-0000-0000-000000000002','ACTIVE',0),
 ('10000000-0000-0000-0000-000000000005','cuong@hoamoc.vn','0909000005','$2b$12$demostaff','Lê Văn Cường','SELLER','20000000-0000-0000-0000-000000000002','ACTIVE',0),
 ('10000000-0000-0000-0000-000000000006','dung@hoasaigon.vn','0909000006','$2b$12$demostaff1','Phạm Thị Dung','SELLER','20000000-0000-0000-0000-000000000001','ACTIVE',0),
 ('10000000-0000-0000-0000-000000000007','em@florabot.vn','0909000007','$2b$12$demotech','Võ Văn Em (kỹ thuật, quyền admin)','ADMIN',NULL,'ACTIVE',0),
 ('10000000-0000-0000-0000-000000000008','giang@gmail.com','0912000008',NULL,'Đỗ Thu Giang','CUSTOMER',NULL,'ACTIVE',0),
 ('10000000-0000-0000-0000-000000000009',NULL,'0912000009',NULL,'Khách Huy (chỉ có SĐT)','CUSTOMER',NULL,'ACTIVE',0),
 ('10000000-0000-0000-0000-000000000010','hoa@hoamoc.vn','0909000010','$2b$12$demostaff2','Ngô Thị Hoa','SELLER','20000000-0000-0000-0000-000000000002','ACTIVE',0),
 ('10000000-0000-0000-0000-000000000011','khoa@gmail.com','0912000011',NULL,'Lý Minh Khoa','CUSTOMER',NULL,'ACTIVE',0);

INSERT INTO kiosk_ops.kiosks (id, code, name, address, region, hardware_id, mqtt_client_id, status, last_heartbeat_at, api_key_hash) VALUES
 ('40000000-0000-0000-0000-000000000001','K-Q1-01','Kiosk Nhà Văn hóa Thanh Niên','4 Phạm Ngọc Thạch, Q1','HCM','ESP32-A1B2C3','kiosk-q1-01','ONLINE', now(), encode(sha256('demo-key-q1'::bytea),'hex')),
 ('40000000-0000-0000-0000-000000000002','K-TD-01','Kiosk Đại học FPT','Lô E2a-7, Khu CNC, Thủ Đức','HCM','ESP32-D4E5F6','kiosk-td-01','ONLINE', now(), encode(sha256('demo-key-td'::bytea),'hex'));

INSERT INTO kiosk_ops.slots (id, kiosk_id, slot_code, relay_channel)
SELECT ('40000000-0000-0000-0001-0000000000' || lpad(g::text,2,'0'))::uuid, '40000000-0000-0000-0000-000000000001', 'A' || lpad(g::text,2,'0'), g - 1
FROM generate_series(1,6) g;
INSERT INTO kiosk_ops.slots (id, kiosk_id, slot_code, relay_channel)
SELECT ('40000000-0000-0000-0002-0000000000' || lpad(g::text,2,'0'))::uuid, '40000000-0000-0000-0000-000000000002', 'B' || lpad(g::text,2,'0'), g - 1
FROM generate_series(1,4) g;

INSERT INTO catalog.flower_products (id, seller_id, name, description, price, tags, shelf_life_hours, status) VALUES
 ('30000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001','Hồng đỏ 10 bông','Hồng Đà Lạt bó giấy kraft — tình yêu trọn vẹn',350000,'{LOVER,VALENTINE,ANNIVERSARY,ROMANTIC,RED}',72,'ACTIVE'),
 ('30000000-0000-0000-0000-000000000002','20000000-0000-0000-0000-000000000001','Cúc họa mi','Bó nhỏ tông trắng — trong trẻo, biết ơn',250000,'{FRIEND,TEACHER,TEACHERS_DAY,WARM,WHITE}',48,'ACTIVE'),
 ('30000000-0000-0000-0000-000000000003','20000000-0000-0000-0000-000000000002','Hướng dương 3 bông','Hướng dương kèm baby — niềm tin và chúc mừng',300000,'{FRIEND,CONGRATS,COLLEAGUE,BIRTHDAY,YELLOW}',72,'ACTIVE'),
 ('30000000-0000-0000-0000-000000000004','20000000-0000-0000-0000-000000000002','Lan hồ điệp mini','Chậu lan 1 cành, sang trọng — kính trọng',600000,'{BOSS,MOTHER,FORMAL,PURPLE}',240,'ACTIVE'),
 ('30000000-0000-0000-0000-000000000005','20000000-0000-0000-0000-000000000002','Hồng pastel hộp','Hộp tròn 15 bông — dịu dàng cho mẹ',550000,'{MOTHER,BIRTHDAY,MOTHERS_DAY,WARM,PINK}',72,'DRAFT'),
 -- seller mới (chờ duyệt) soạn sẵn sản phẩm nháp; kích hoạt sau khi được duyệt (05_demo_history)
 ('30000000-0000-0000-0000-000000000006','20000000-0000-0000-0000-000000000003','Tulip Hà Lan 5 cành','Tulip hồng gói giấy lụa — lời yêu nhẹ nhàng',280000,'{LOVER,BIRTHDAY,WOMENS_DAY,PINK,WARM}',48,'DRAFT'),
 ('30000000-0000-0000-0000-000000000007','20000000-0000-0000-0000-000000000003','Cẩm chướng tri ân','Bó cẩm chướng đỏ hồng — tri ân thầy cô, mẹ',220000,'{TEACHER,MOTHER,TEACHERS_DAY,MOTHERS_DAY,WARM,RED}',72,'DRAFT'),
 ('30000000-0000-0000-0000-000000000008','20000000-0000-0000-0000-000000000003','Baby trắng mini','Bó baby nhỏ xinh — xin lỗi dễ thương',150000,'{FRIEND,APOLOGY,FUNNY,WHITE}',48,'DRAFT');

-- phụ kiện do seller tự quản lý, tồn kho theo kiosk (cách giao: xem README mục A3)
INSERT INTO kiosk_ops.accessories (id, seller_id, kiosk_id, name, price, stock_quantity) VALUES
 ('30000000-0000-0000-0001-000000000001','20000000-0000-0000-0000-000000000001','40000000-0000-0000-0000-000000000001','Thiệp chúc mừng',20000,30),
 ('30000000-0000-0000-0001-000000000002','20000000-0000-0000-0000-000000000002','40000000-0000-0000-0000-000000000001','Gấu bông nhỏ',120000,5),
 ('30000000-0000-0000-0001-000000000003','20000000-0000-0000-0000-000000000002','40000000-0000-0000-0000-000000000002','Thiệp chúc mừng',20000,40);

-- tham số vận hành (admin chỉnh qua flow.set_cfg); mọi hàm nghiệp vụ đọc qua flow.cfg()
INSERT INTO kiosk_ops.system_settings (key, value, description) VALUES
 ('hold_minutes',              '7',     'Giữ ô chờ khách quét QR thanh toán (phút) — BRD: TTL 7 phút'),
 ('token_minutes',             '1',     'Hiệu lực token mở hộc (phút) — BRD: 60 giây'),
 ('kiosk_pickup_minutes',      '30',    'Sau khi trả tiền, giữ ô tối đa N phút cho khách mở lại hộc bằng mã e-receipt'),
 ('door_max_attempts',         '2',     'Số lần gửi lệnh mở cửa thất bại trước khi DISPENSE_FAILED — BRD: 2 lần'),
 ('dispute_window_hours',      '24',    'Thời hạn khiếu nại kể từ lúc lấy hàng (giờ); hết hạn mới đối soát cho seller'),
 ('withdraw_min',              '50000', 'Số tiền rút tối thiểu (đồng) — chờ GVHD chốt 50k hay 500k'),
 ('expiry_warn_hours',         '6',     'Bó hoa còn dưới N giờ hạn bán thì hiện cảnh báo cho seller'),
 ('points_rate_percent',       '1',     'Tích điểm = N% giá trị đơn (1 điểm = 1 đồng), cộng khi đơn COMPLETED'),
 ('points_max_redeem_percent', '50',    'Đổi điểm tối đa N% giá trị hàng của một đơn'),
 ('otp_ttl_minutes',           '3',     'Hiệu lực OTP đăng nhập tại kiosk (OTP lưu Valkey, không lưu DB)'),
 ('heartbeat_offline_seconds', '90',    'Không nhận heartbeat sau N giây thì coi kiosk OFFLINE'),
 ('subscription_payment_hours','24',    'Đăng ký gói chưa thanh toán sau N giờ thì hủy (mở lại kỳ cho lần đăng ký khác)'),
 ('past_due_grace_days',       '7',     'Gói hết hạn (PAST_DUE) quá N ngày không trả tiếp thì thu hồi toàn bộ ô đã gán'),
 ('return_deadline_hours',     '48',    'Seller phải lấy hoa quá hạn / hết hợp đồng về trong N giờ, quá hạn admin được thanh lý');
COMMIT;

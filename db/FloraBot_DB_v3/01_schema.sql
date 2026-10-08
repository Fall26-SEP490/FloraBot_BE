-- FloraBot — schema v3.1 (MVP theo BRD §5.1): 22 bảng, 7 schema = 7 service (database-per-service mô phỏng bằng schema).
-- v3 so với v2.1: bỏ preorder, feedback, hoa hồng, voucher, ca làm, phiếu công việc, thông báo lưu DB, luật Apriori, order_events, incident_tickets riêng.
--                 thêm tích điểm khách hàng (users.loyalty_points, orders.points_*), nạp hàng theo batch_id, disputes gánh luôn sự cố (kind), sellers.brand_tone cho AI.
-- v3.1 (07/10): gói thuê bao theo BRD §7.1 — seller trả PHÍ GÓI theo kỳ (identity.subscriptions), gói hết hạn -> PAST_DUE khóa bán, trả tiền lại thì mở;
--                 admin GÁN Ô trong hạn mức gói (kiosk_ops.slot_assignments, không tính tiền theo ô). Bỏ slot_rentals (thuê từng ô).
--                 Hoàn tiền cho khách: bằng tay (không có refund API VietQR) — khách tự khai STK trên e-receipt (orders.refund_bank_*), admin chi + đối soát hằng ngày (flow.reconcile_payments).
-- Quy ước: FK thật CHỈ trong cùng schema; tham chiếu sang service khác là cột uuid "-- ref schema.table" (comment), không FK.
-- Tiền VND: bigint (đồng). Thời gian: timestamptz. Khóa: UUID v7 (public.uuid_v7(); .NET sinh Guid.CreateVersion7()).
-- Bảng cap.published / cap.received do framework CAP (outbox) tự sinh trong mỗi service, KHÔNG tính vào 22 bảng.
--
-- 7 service:
--   identity  : users, sellers, subscription_packages, subscriptions                    (Identity & Merchant)
--   catalog   : flower_products                                                         (Catalog)
--   kiosk_ops : kiosks, slots, slot_assignments, bouquets, inventory_logs, unlock_tokens,
--               accessories, system_settings                                            (Kiosk & Operations, gồm IoT)
--   ordering  : orders, order_items, disputes                                           (Ordering)
--   payment   : payments, ledger_entries, withdrawal_requests                           (Payment & Wallet)
--   notify    : audit_logs, attachments                                                 (Audit & Media; thông báo realtime đi qua SignalR, không lưu)
--   ai        : gift_surveys                                                            (AI Gift Advisor — log để train phase sau)
--
-- HỢP ĐỒNG SỰ KIỆN (RabbitMQ + CAP outbox; ở demo các hàm flow.* gọi trực tiếp thay cho sự kiện):
--   PaymentSucceeded {payment_id, checkout_id|subscription_id, amount, gateway_txn_id, paid_at}  payment -> ordering, identity
--   OrderPaid        {order_id, checkout_id, seller_id, kiosk_id, items[]}                      ordering -> kiosk_ops (phát token mở hộc)
--   SlotPickedUp     {order_id, slot_id, bouquet_id, picked_up_at}                              kiosk_ops -> ordering (COMPLETED, cộng điểm)
--   DispenseFailed   {order_id, slot_id, reason}                                                kiosk_ops -> ordering (DISPENSE_FAILED, hoàn 100%)
--   RefundApproved   {refund_payment_id, order_id, amount, reason}                              ordering -> payment (chi hoàn)
--   SubscriptionPaid {subscription_id, seller_id, period}                                        payment -> identity (ACTIVE / mở khóa PAST_DUE)
--   SellerPastDue    {seller_id, package_expires_at}                                             identity -> kiosk_ops (ẩn catalog, chặn nạp/bán)
--
-- VÒNG ĐỜI ĐƠN (BRD Hình 8): AWAITING_PAYMENT -> PAID -> DISPENSING -> COMPLETED
--                             AWAITING_PAYMENT -> EXPIRED | CANCELLED (thanh toán thất bại)
--                             PAID | DISPENSING -> DISPENSE_FAILED -> REFUNDED (tự hoàn 100%)
--                             COMPLETED -> DISPUTED -> COMPLETED | REFUNDED
--   (RESERVED của BRD gộp vào AWAITING_PAYMENT: giữ ô và sinh QR là một bước nguyên tử trong kiosk_checkout.)
SET client_min_messages = warning;
DROP SCHEMA IF EXISTS identity, merchant, catalog, kiosk, kiosk_ops, iot, ordering, payment, ops, ai, notify, shared, flow, screen CASCADE;
CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE SCHEMA identity; CREATE SCHEMA catalog; CREATE SCHEMA kiosk_ops; CREATE SCHEMA ordering;
CREATE SCHEMA payment; CREATE SCHEMA notify; CREATE SCHEMA ai;

-- UUID v7: PG18 có sẵn uuidv7(); PG16/17 dùng shim PL/pgSQL (48 bit mili-giây + version 7 + variant 10).
DO $$ BEGIN
  IF current_setting('server_version_num')::int >= 180000 THEN
    EXECUTE 'CREATE OR REPLACE FUNCTION public.uuid_v7() RETURNS uuid LANGUAGE sql VOLATILE AS ''SELECT uuidv7()''';
  ELSE
    EXECUTE $f$
    CREATE OR REPLACE FUNCTION public.uuid_v7() RETURNS uuid LANGUAGE plpgsql VOLATILE AS $b$
    DECLARE ts bigint := floor(extract(epoch FROM clock_timestamp()) * 1000); b bytea := gen_random_bytes(16);
    BEGIN
      b := overlay(b PLACING substring(int8send(ts) FROM 3) FROM 1 FOR 6);
      b := set_byte(b, 6, (get_byte(b, 6) & 15) | 112);   -- version 7
      b := set_byte(b, 8, (get_byte(b, 8) & 63) | 128);   -- variant 10
      RETURN encode(b, 'hex')::uuid;
    END $b$ $f$;
  END IF;
END $$;

-- Đồng hồ nghiệp vụ: mặc định now(); các hàm flow.* nhận p_now để "tua thời gian" khi dựng dữ liệu lịch sử demo.
CREATE OR REPLACE FUNCTION public.app_now() RETURNS timestamptz LANGUAGE sql VOLATILE AS
$$ SELECT coalesce(nullif(current_setting('app.now', true), '')::timestamptz, now()) $$;
-- dùng cho bảng nhật ký: nếu không tua thời gian thì lấy clock_timestamp() để thứ tự trong cùng giao dịch rõ ràng
CREATE OR REPLACE FUNCTION public.app_clock() RETURNS timestamptz LANGUAGE sql VOLATILE AS
$$ SELECT coalesce(nullif(current_setting('app.now', true), '')::timestamptz, clock_timestamp()) $$;

CREATE OR REPLACE FUNCTION public.touch_updated_at() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN NEW.updated_at := public.app_now(); RETURN NEW; END $$;
CREATE OR REPLACE FUNCTION public.forbid_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION '% là bảng chỉ ghi thêm (append-only)', TG_TABLE_NAME; END $$;

-- Máy trạng thái dùng chung: chặn mọi UPDATE status đi sai cạnh (kể cả UPDATE tay), trạng thái kết thúc không đi tiếp.
CREATE OR REPLACE FUNCTION public.guard_transition() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE ok boolean; v_refund bigint;
BEGIN
  IF NEW.status IS NOT DISTINCT FROM OLD.status THEN RETURN NEW; END IF;
  ok := CASE TG_TABLE_NAME
    WHEN 'orders' THEN (OLD.status, NEW.status) IN (
      ('AWAITING_PAYMENT','PAID'),('AWAITING_PAYMENT','EXPIRED'),('AWAITING_PAYMENT','CANCELLED'),
      ('PAID','DISPENSING'),('PAID','DISPENSE_FAILED'),
      ('DISPENSING','COMPLETED'),('DISPENSING','DISPENSE_FAILED'),
      ('DISPENSE_FAILED','REFUNDED'),
      ('COMPLETED','DISPUTED'),('COMPLETED','REFUNDED'),('DISPUTED','COMPLETED'),('DISPUTED','REFUNDED'))
    WHEN 'payments' THEN (OLD.status, NEW.status) IN (
      ('PENDING','SUCCEEDED'),('PENDING','FAILED'),('PENDING','CANCELLED'),('PENDING','EXPIRED'),
      ('EXPIRED','SUCCEEDED'),('CANCELLED','SUCCEEDED'),('FAILED','SUCCEEDED'))   -- tiền về muộn vẫn phải ghi nhận rồi hoàn
    WHEN 'unlock_tokens' THEN (OLD.status, NEW.status) IN (
      ('ISSUED','SENT'),('ISSUED','EXPIRED'),('ISSUED','REVOKED'),('ISSUED','FAILED'),
      ('SENT','ACKED'),('SENT','EXPIRED'),('SENT','REVOKED'),('SENT','FAILED'),
      ('ACKED','OPENED'),('ACKED','EXPIRED'),('ACKED','REVOKED'),('ACKED','FAILED'),
      ('OPENED','CLOSED'))
    WHEN 'disputes' THEN (OLD.status, NEW.status) IN (
      ('OPEN','RESOLVED_REFUND'),('OPEN','RESOLVED_REJECT'),('OPEN','RESOLVED_FIXED'),('OPEN','CANCELLED'))
    WHEN 'withdrawal_requests' THEN (OLD.status, NEW.status) IN (
      ('PENDING','APPROVED'),('PENDING','REJECTED'),('PENDING','PAID'),('PENDING','CANCELLED'),
      ('APPROVED','PAID'),('APPROVED','REJECTED'))
    ELSE false END;
  IF NOT ok THEN
    RAISE EXCEPTION 'Chuyển trạng thái % không hợp lệ: % -> %', TG_TABLE_NAME, OLD.status, NEW.status;
  END IF;
  -- chốt theo dữ liệu (không dựa vào biến phiên): áp cho cả UPDATE tay
  IF TG_TABLE_NAME = 'orders' THEN
    SELECT coalesce(sum(amount), 0) INTO v_refund FROM payment.payments WHERE order_id = NEW.id AND kind = 'REFUND' AND status IN ('PENDING','SUCCEEDED');
    IF NEW.status = 'DISPENSE_FAILED' AND v_refund < NEW.total_amount THEN
      RAISE EXCEPTION 'Đơn không nhả được hàng phải có lệnh hoàn đủ % đ trước khi chuyển DISPENSE_FAILED', NEW.total_amount;
    END IF;
    IF NEW.status = 'REFUNDED' AND (SELECT coalesce(sum(amount), 0) FROM payment.payments WHERE order_id = NEW.id AND kind = 'REFUND' AND status = 'SUCCEEDED') < NEW.total_amount THEN
      RAISE EXCEPTION 'REFUNDED chỉ khi đã chi hoàn đủ % đ', NEW.total_amount;
    END IF;
    IF NEW.status = 'COMPLETED' AND OLD.status = 'DISPENSING' AND NEW.completed_at IS NULL THEN
      RAISE EXCEPTION 'COMPLETED phải có completed_at';
    END IF;
    IF NEW.status = 'COMPLETED' AND OLD.status = 'DISPENSING' AND EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE hold_order_id = NEW.id) THEN
      RAISE EXCEPTION 'COMPLETED khi ô vẫn đang giữ cho đơn: cửa chưa đóng';
    END IF;
    IF NEW.status = 'PAID' AND NOT EXISTS (SELECT 1 FROM payment.payments WHERE checkout_id = NEW.checkout_id AND kind = 'CHARGE' AND status = 'SUCCEEDED') THEN
      RAISE EXCEPTION 'PAID khi khoản thu chưa SUCCEEDED';
    END IF;
    IF NEW.status IN ('EXPIRED','CANCELLED') AND EXISTS (SELECT 1 FROM payment.payments WHERE checkout_id = NEW.checkout_id AND kind = 'CHARGE' AND status IN ('PENDING','SUCCEEDED')) THEN
      RAISE EXCEPTION 'EXPIRED/CANCELLED khi khoản thu còn PENDING/SUCCEEDED: phải đi qua release_checkout';
    END IF;
    IF NEW.status = 'DISPUTED' AND NOT EXISTS (SELECT 1 FROM ordering.disputes WHERE order_id = NEW.id AND kind = 'COMPLAINT') THEN
      RAISE EXCEPTION 'DISPUTED phải có khiếu nại';
    END IF;
  END IF;
  RETURN NEW;
END $$;

-- ============ identity (4) — Identity & Merchant ============
-- gói thuê bao (BRD §7.1): phí cố định theo tháng, bao gồm tối đa max_slots ô; phí gói = nguồn thu duy nhất của nền tảng ở MVP (không hoa hồng)
CREATE TABLE identity.subscription_packages (
  id                   uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  name                 text NOT NULL UNIQUE,
  monthly_fee          bigint NOT NULL CHECK (monthly_fee > 0),             -- phí gói mỗi tháng (đồng), không tính thêm theo ô
  max_slots            int NOT NULL CHECK (max_slots > 0),                  -- số ô tối đa admin được gán cho seller trong gói
  status               text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','INACTIVE')),
  created_at           timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at           timestamptz NOT NULL DEFAULT public.app_now()
);
CREATE TABLE identity.sellers (
  id                  uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  shop_name           text NOT NULL,
  phone               text NOT NULL,
  address             text,
  -- PENDING: chờ admin duyệt hồ sơ; APPROVED: đã duyệt, chưa trả phí gói; ACTIVE: gói còn hạn, được bán;
  -- PAST_DUE: gói hết hạn -> khóa bán/nạp, catalog ẩn; trả tiền lại thì ACTIVE; quá past_due_grace_days thì thu hồi ô. SUSPENDED/CLOSED: admin.
  status              text NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING','APPROVED','ACTIVE','PAST_DUE','SUSPENDED','CLOSED')),
  package_id          uuid REFERENCES identity.subscription_packages(id),
  package_expires_at  date,                       -- ngày hết hạn gói (không bao gồm) = period_to của chuỗi kỳ đã trả liên tục; cập nhật bởi flow.refresh_seller_package
  brand_tone          text,                       -- một câu "giọng thương hiệu" đưa vào prompt AI Gift Advisor (admin duyệt)
  bank_name           text,
  bank_account_no_enc text,                       -- mã hóa ở tầng ứng dụng
  bank_holder         text,
  created_at          timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at          timestamptz NOT NULL DEFAULT public.app_now(),
  CHECK (status NOT IN ('APPROVED','ACTIVE','PAST_DUE') OR package_id IS NOT NULL),
  CHECK (status NOT IN ('ACTIVE','PAST_DUE') OR package_expires_at IS NOT NULL)
);
-- kỳ thuê bao: seller đăng ký N tháng, trả qua payOS; ACTIVE khi đã thu tiền; chuỗi kỳ liên tục = thời hạn gói của seller
CREATE TABLE identity.subscriptions (
  id          uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  seller_id   uuid NOT NULL REFERENCES identity.sellers(id),
  package_id  uuid NOT NULL REFERENCES identity.subscription_packages(id),
  period_from date NOT NULL,
  period_to   date NOT NULL,                      -- không bao gồm (nửa mở [from, to))
  price       bigint NOT NULL CHECK (price > 0),  -- = monthly_fee × số tháng, chụp lúc đăng ký
  status      text NOT NULL DEFAULT 'PENDING_PAYMENT' CHECK (status IN ('PENDING_PAYMENT','ACTIVE','EXPIRED','CANCELLED')),
  payment_id  uuid,                               -- ref payment.payments (CHARGE, purpose SUBSCRIPTION)
  created_at  timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at  timestamptz NOT NULL DEFAULT public.app_now(),
  CHECK (period_to > period_from),
  CHECK (status <> 'ACTIVE' OR payment_id IS NOT NULL),
  EXCLUDE USING gist (seller_id WITH =, daterange(period_from, period_to) WITH &&)
    WHERE (status IN ('PENDING_PAYMENT','ACTIVE'))   -- một seller không có hai kỳ chồng nhau
);
CREATE TABLE identity.users (
  id             uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  email          text UNIQUE,
  phone          text UNIQUE,
  password_hash  text,                            -- CUSTOMER đăng nhập tại kiosk bằng SĐT + OTP (OTP ở Valkey), không có mật khẩu
  full_name      text NOT NULL,
  role           text NOT NULL CHECK (role IN ('ADMIN','SELLER','CUSTOMER','SYSTEM')),
  seller_id      uuid REFERENCES identity.sellers(id),
  status         text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','LOCKED','DISABLED')),
  loyalty_points bigint NOT NULL DEFAULT 0 CHECK (loyalty_points >= 0),   -- 1 điểm = 1 đồng; cộng khi đơn COMPLETED, trừ khi đổi điểm
  created_at     timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at     timestamptz NOT NULL DEFAULT public.app_now(),
  CHECK (email IS NOT NULL OR phone IS NOT NULL),
  CHECK ((role = 'SELLER') = (seller_id IS NOT NULL)),
  CHECK (role = 'CUSTOMER' OR password_hash IS NOT NULL),
  CHECK (role = 'CUSTOMER' OR loyalty_points = 0)
);

-- ============ catalog (1) ============
CREATE TABLE catalog.flower_products (
  id               uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  seller_id        uuid NOT NULL,                 -- ref identity.sellers
  name             text NOT NULL,
  description      text,                         -- ý nghĩa hoa 1–2 câu, AI dùng để viết lý do gợi ý
  price            bigint NOT NULL CHECK (price > 0),
  tags             text[] NOT NULL DEFAULT '{}',  -- người nhận / dịp / phong cách / màu, vd {LOVER,VALENTINE,ROMANTIC,RED}
  shelf_life_hours int NOT NULL CHECK (shelf_life_hours > 0),
  status           text NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','ACTIVE','INACTIVE')),
  created_at       timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at       timestamptz NOT NULL DEFAULT public.app_now()
);
CREATE INDEX ON catalog.flower_products USING gin (tags);
CREATE INDEX ON catalog.flower_products (seller_id, status);

-- ============ kiosk_ops (8) — Kiosk & Operations (gồm IoT) ============
CREATE TABLE kiosk_ops.kiosks (
  id                uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  code              text NOT NULL UNIQUE,
  name              text NOT NULL,
  address           text NOT NULL,
  region            text NOT NULL,
  hardware_id       text NOT NULL UNIQUE,         -- mã bo ESP32
  mqtt_client_id    text NOT NULL UNIQUE,
  status            text NOT NULL DEFAULT 'OFFLINE' CHECK (status IN ('ONLINE','OFFLINE','MAINTENANCE','DISABLED')),
  last_heartbeat_at timestamptz,
  api_key_hash      text,                         -- băm khóa API kiosk dùng gọi Core API
  created_at        timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at        timestamptz NOT NULL DEFAULT public.app_now()
);
CREATE TABLE kiosk_ops.bouquets (
  id             uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  product_id     uuid NOT NULL,                   -- ref catalog.flower_products
  seller_id      uuid NOT NULL,                   -- ref identity.sellers (chép để kiosk tự kiểm quyền)
  qr_code        text NOT NULL UNIQUE,
  price_snapshot bigint NOT NULL CHECK (price_snapshot > 0),
  status         text NOT NULL DEFAULT 'STOCKED' CHECK (status IN ('STOCKED','HELD','SOLD','PICKED_UP','EXPIRED','DAMAGED','RETURNED')),
  sellable_until timestamptz NOT NULL,            -- = stocked_at + shelf_life_hours; quá hạn tự ẩn khỏi catalog
  stocked_at     timestamptz NOT NULL,
  created_at     timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at     timestamptz NOT NULL DEFAULT public.app_now(),
  CHECK (sellable_until > stocked_at)
);
CREATE INDEX bouquets_sellable_idx ON kiosk_ops.bouquets (sellable_until) WHERE status = 'STOCKED';
CREATE TABLE kiosk_ops.slots (
  id                uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  kiosk_id          uuid NOT NULL REFERENCES kiosk_ops.kiosks(id),
  slot_code         text NOT NULL,
  -- RENTED_EMPTY: ô đã gán cho seller (trong hạn mức gói), đang trống; PENDING_REMOVAL: hoa quá hạn / đơn hoàn tiền, chờ seller lấy về;
  -- PENDING_RELEASE: ô bị thu hồi (gói quá ân hạn / admin thu hồi) nhưng còn hoa, seller đến lấy rồi ô mới FREE
  status            text NOT NULL DEFAULT 'FREE' CHECK (status IN ('FREE','RENTED_EMPTY','STOCKED','HELD','LOCKED','FAULT','PENDING_REMOVAL','PENDING_RELEASE')),
  current_seller_id uuid,                         -- ref identity.sellers; chép từ slot_assignments đang ACTIVE
  bouquet_id        uuid UNIQUE REFERENCES kiosk_ops.bouquets(id),
  hold_until        timestamptz,
  hold_order_id     uuid,                         -- ref ordering.orders
  row_version       int NOT NULL DEFAULT 0,
  relay_channel     smallint NOT NULL CHECK (relay_channel BETWEEN 0 AND 63),   -- chân relay điều khiển khóa ô trên ESP32
  created_at        timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at        timestamptz NOT NULL DEFAULT public.app_now(),
  UNIQUE (kiosk_id, slot_code),
  UNIQUE (kiosk_id, relay_channel),
  CHECK (status NOT IN ('STOCKED','HELD','PENDING_REMOVAL','PENDING_RELEASE') OR bouquet_id IS NOT NULL),
  CHECK (status NOT IN ('FREE','RENTED_EMPTY') OR bouquet_id IS NULL),
  CHECK (status <> 'HELD' OR (hold_until IS NOT NULL AND hold_order_id IS NOT NULL)),
  CHECK (status IN ('HELD','FAULT') OR (hold_until IS NULL AND hold_order_id IS NULL)),   -- FAULT giữ lại thông tin giữ để xử lý đơn
  CHECK (status = 'FREE' OR status IN ('LOCKED','FAULT') OR current_seller_id IS NOT NULL)
);
CREATE INDEX slots_hold_idx ON kiosk_ops.slots (hold_until) WHERE status = 'HELD';
-- admin gán ô cho seller trong hạn mức gói (BRD FR-ADM-01); không tính tiền theo ô (tiền nằm ở phí gói). Lịch sử gán/thu hồi giữ lại.
CREATE TABLE kiosk_ops.slot_assignments (
  id             uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  slot_id        uuid NOT NULL REFERENCES kiosk_ops.slots(id),
  seller_id      uuid NOT NULL,                   -- ref identity.sellers
  assigned_by    uuid NOT NULL,                   -- ref identity.users (admin)
  assigned_at    timestamptz NOT NULL DEFAULT public.app_now(),
  status         text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','RELEASED')),
  released_at    timestamptz,
  release_reason text,                            -- ADMIN | SELLER | PAST_DUE_GRACE | SELLER_CLOSED
  CHECK ((status = 'RELEASED') = (released_at IS NOT NULL))
);
CREATE UNIQUE INDEX slot_assignments_one_active ON kiosk_ops.slot_assignments (slot_id) WHERE status = 'ACTIVE';   -- một ô chỉ gán cho một seller
CREATE INDEX ON kiosk_ops.slot_assignments (seller_id) WHERE status = 'ACTIVE';
-- nhật ký kho: mỗi lần nạp = một batch_id (thay cho bảng phiếu công việc); chỉ ghi thêm
CREATE TABLE kiosk_ops.inventory_logs (
  id            uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  seq           bigint GENERATED ALWAYS AS IDENTITY UNIQUE,   -- thứ tự tuyệt đối cho màn hình lịch sử
  bouquet_id    uuid NOT NULL REFERENCES kiosk_ops.bouquets(id),
  slot_id       uuid NOT NULL REFERENCES kiosk_ops.slots(id),
  movement_type text NOT NULL CHECK (movement_type IN ('STOCK_IN','HOLD','RELEASE','SALE_OUT','EXPIRE_OUT','DAMAGE','RETURN_SELLER')),
  batch_id      uuid,                             -- lô nạp hàng (một lần seller đến kiosk nạp nhiều ô); bắt buộc với STOCK_IN
  order_id      uuid,                             -- ref ordering.orders; bắt buộc với HOLD/RELEASE/SALE_OUT
  performed_by  uuid,                             -- ref identity.users (NULL = hệ thống)
  reason        text,
  created_at    timestamptz NOT NULL DEFAULT public.app_clock(),
  CHECK (movement_type <> 'STOCK_IN' OR batch_id IS NOT NULL),
  CHECK (movement_type NOT IN ('HOLD','RELEASE','SALE_OUT') OR order_id IS NOT NULL)
);
CREATE INDEX ON kiosk_ops.inventory_logs (bouquet_id, seq);
CREATE INDEX ON kiosk_ops.inventory_logs (batch_id);
CREATE TRIGGER inventory_logs_append_only BEFORE UPDATE OR DELETE ON kiosk_ops.inventory_logs FOR EACH ROW EXECUTE FUNCTION public.forbid_change();
CREATE TABLE kiosk_ops.unlock_tokens (
  id                uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  kiosk_id          uuid NOT NULL REFERENCES kiosk_ops.kiosks(id),
  slot_id           uuid NOT NULL REFERENCES kiosk_ops.slots(id),
  purpose           text NOT NULL CHECK (purpose IN ('CUSTOMER_PICKUP','SELLER_ACCESS')),   -- SELLER_ACCESS: cửa kỹ thuật nạp/lấy hàng
  order_id          uuid,                         -- ref ordering.orders
  issued_to_user_id uuid,                         -- ref identity.users (bắt buộc với SELLER_ACCESS)
  token_hash        text NOT NULL UNIQUE,         -- SHA-256, không lưu bản rõ; ký HMAC theo thiết bị ở tầng MQTT
  cmd_id            uuid NOT NULL UNIQUE DEFAULT public.uuid_v7(),   -- id lệnh MQTT, tủ dùng chống xử lý lặp
  status            text NOT NULL DEFAULT 'ISSUED' CHECK (status IN ('ISSUED','SENT','ACKED','OPENED','CLOSED','EXPIRED','FAILED','REVOKED')),
  attempts          int NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 5),
  issued_at         timestamptz NOT NULL DEFAULT public.app_now(),
  expires_at        timestamptz NOT NULL,         -- = issued_at + token_minutes (BRD: TTL 60 giây)
  door_opened_at    timestamptz,
  door_closed_at    timestamptz,
  CHECK (expires_at > issued_at),
  CHECK ((purpose = 'CUSTOMER_PICKUP') = (order_id IS NOT NULL)),
  CHECK (purpose <> 'SELLER_ACCESS' OR issued_to_user_id IS NOT NULL),
  CHECK (door_closed_at IS NULL OR door_opened_at IS NOT NULL)
);
-- tại một ô chỉ có một token còn hiệu lực
CREATE UNIQUE INDEX unlock_one_live_per_slot ON kiosk_ops.unlock_tokens (slot_id) WHERE status IN ('ISSUED','SENT','ACKED','OPENED');
CREATE INDEX ON kiosk_ops.unlock_tokens (order_id);
CREATE INDEX unlock_live_expiry_idx ON kiosk_ops.unlock_tokens (expires_at) WHERE status IN ('ISSUED','SENT','ACKED');
-- phụ kiện (nơ, thiệp giấy, gấu nhỏ) do seller quản lý, tồn kho theo kiosk; nhân viên bỏ kèm vào ô khi nạp / ngăn phụ kiện chung
CREATE TABLE kiosk_ops.accessories (
  id             uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  seller_id      uuid NOT NULL,                   -- ref identity.sellers
  kiosk_id       uuid NOT NULL REFERENCES kiosk_ops.kiosks(id),
  name           text NOT NULL,
  price          bigint NOT NULL CHECK (price > 0),
  stock_quantity int NOT NULL DEFAULT 0 CHECK (stock_quantity >= 0),
  status         text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','INACTIVE')),
  created_at     timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at     timestamptz NOT NULL DEFAULT public.app_now(),
  UNIQUE (kiosk_id, seller_id, name)
);
-- tham số vận hành admin chỉnh được (thay mọi hằng số cứng)
CREATE TABLE kiosk_ops.system_settings (
  key         text PRIMARY KEY,
  value       jsonb NOT NULL,
  description text,
  updated_by  uuid,                               -- ref identity.users
  updated_at  timestamptz NOT NULL DEFAULT public.app_now()
);

-- ============ ordering (3) ============
CREATE TABLE ordering.orders (
  id              uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  order_code      text NOT NULL UNIQUE,           -- FBYYMMDD-XXXXXX in trên e-receipt
  checkout_id     uuid NOT NULL,                  -- một lần thanh toán có thể sinh nhiều đơn (mỗi seller một đơn)
  customer_id     uuid,                           -- ref identity.users; NULL = khách vãng lai (không lưu PII)
  seller_id       uuid NOT NULL,                  -- ref identity.sellers
  kiosk_id        uuid NOT NULL,                  -- ref kiosk_ops.kiosks
  status          text NOT NULL DEFAULT 'AWAITING_PAYMENT' CHECK (status IN
                    ('AWAITING_PAYMENT','PAID','DISPENSING','COMPLETED','DISPUTED','REFUNDED','DISPENSE_FAILED','EXPIRED','CANCELLED')),
  subtotal        bigint NOT NULL CHECK (subtotal >= 0),
  discount_amount bigint NOT NULL DEFAULT 0 CHECK (discount_amount >= 0),   -- chỉ từ đổi điểm (nền tảng chịu)
  total_amount    bigint NOT NULL CHECK (total_amount >= 0),
  points_redeemed bigint NOT NULL DEFAULT 0 CHECK (points_redeemed >= 0),  -- = discount_amount (1 điểm = 1 đồng)
  points_earned   bigint NOT NULL DEFAULT 0 CHECK (points_earned >= 0),    -- cộng cho khách khi đơn COMPLETED
  tracking_token  text NOT NULL UNIQUE,           -- mã trên e-receipt: tra cứu, mở lại hộc, khiếu nại
  ecard_content   text,                           -- lời thiệp (AI gợi ý, khách sửa)
  completed_at    timestamptz,                    -- lúc cửa đóng sau khi lấy hàng; mốc tính hạn khiếu nại
  -- hoàn tiền bằng tay (VietQR không có refund API): khách tự khai tài khoản nhận hoàn trên trang e-receipt (flow.submit_refund_info);
  -- chỉ thu khi đơn có lệnh hoàn, mã hóa ở tầng ứng dụng, xóa sau khi chi xong (NĐ 13/2023, BRD §9)
  refund_bank_name        text,
  refund_bank_account_enc text,
  refund_bank_holder      text,
  created_at      timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at      timestamptz NOT NULL DEFAULT public.app_now(),
  CHECK ((refund_bank_name IS NULL) = (refund_bank_account_enc IS NULL) AND (refund_bank_name IS NULL) = (refund_bank_holder IS NULL)),
  CHECK (total_amount = subtotal - discount_amount),
  CHECK (points_redeemed = discount_amount),
  CHECK (customer_id IS NOT NULL OR (points_redeemed = 0 AND points_earned = 0)),
  CHECK (status NOT IN ('COMPLETED','DISPUTED') OR completed_at IS NOT NULL)
);
CREATE INDEX ON ordering.orders (checkout_id);
CREATE INDEX ON ordering.orders (seller_id, created_at);
CREATE INDEX ON ordering.orders (customer_id, created_at);
CREATE TABLE ordering.order_items (
  id            uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  order_id      uuid NOT NULL REFERENCES ordering.orders(id),
  item_type     text NOT NULL CHECK (item_type IN ('BOUQUET','ACCESSORY')),
  bouquet_id    uuid,                             -- ref kiosk_ops.bouquets
  accessory_id  uuid,                             -- ref kiosk_ops.accessories
  slot_id       uuid,                             -- ref kiosk_ops.slots (ô chứa bó, bắt buộc với BOUQUET)
  name_snapshot text NOT NULL,
  quantity      int NOT NULL CHECK (quantity > 0),
  unit_price    bigint NOT NULL CHECK (unit_price >= 0),
  line_total    bigint NOT NULL,
  line_status   text NOT NULL DEFAULT 'ACTIVE' CHECK (line_status IN ('ACTIVE','CANCELLED','REFUNDED')),
  created_at    timestamptz NOT NULL DEFAULT public.app_now(),
  CHECK (line_total = quantity * unit_price),
  CHECK ((item_type = 'BOUQUET'   AND bouquet_id IS NOT NULL AND accessory_id IS NULL AND quantity = 1 AND slot_id IS NOT NULL)
      OR (item_type = 'ACCESSORY' AND accessory_id IS NOT NULL AND bouquet_id IS NULL))
);
-- một bó hoa chỉ bán được một lần (chống bán trùng)
CREATE UNIQUE INDEX order_items_bouquet_once ON ordering.order_items (bouquet_id) WHERE bouquet_id IS NOT NULL AND line_status = 'ACTIVE';
-- khiếu nại + sự cố gộp một bảng: COMPLAINT (khách, từ e-receipt), DISPENSE_FAILED (tủ không nhả hàng, tự tạo), DEVICE_FAULT (seller/kỹ thuật báo)
CREATE TABLE ordering.disputes (
  id            uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  kind          text NOT NULL CHECK (kind IN ('COMPLAINT','DISPENSE_FAILED','DEVICE_FAULT')),
  order_id      uuid REFERENCES ordering.orders(id),
  kiosk_id      uuid NOT NULL,                    -- ref kiosk_ops.kiosks
  slot_id       uuid,                             -- ref kiosk_ops.slots
  reason        text NOT NULL,
  reported_by   uuid,                             -- ref identity.users (NULL = thiết bị/hệ thống hoặc khách vãng lai)
  status        text NOT NULL DEFAULT 'OPEN' CHECK (status IN ('OPEN','RESOLVED_REFUND','RESOLVED_REJECT','RESOLVED_FIXED','CANCELLED')),
  decision      text,
  refund_amount bigint CHECK (refund_amount > 0),
  decided_by    uuid,                             -- ref identity.users
  resolved_at   timestamptz,
  created_at    timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at    timestamptz NOT NULL DEFAULT public.app_now(),
  CHECK (kind = 'DEVICE_FAULT' OR order_id IS NOT NULL),
  CHECK ((status = 'RESOLVED_REFUND') = (refund_amount IS NOT NULL)),
  CHECK (status NOT IN ('RESOLVED_REFUND','RESOLVED_REJECT','RESOLVED_FIXED') OR (decided_by IS NOT NULL AND resolved_at IS NOT NULL AND decision IS NOT NULL))
);
CREATE UNIQUE INDEX disputes_one_complaint_per_order ON ordering.disputes (order_id) WHERE kind = 'COMPLAINT';
CREATE INDEX ON ordering.disputes (kiosk_id, status);

-- ============ payment (3) — Payment & Wallet ============
CREATE TABLE payment.payments (
  id                uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  kind              text NOT NULL CHECK (kind IN ('CHARGE','REFUND')),
  purpose           text NOT NULL CHECK (purpose IN ('ORDER_CHECKOUT','SUBSCRIPTION')),
  checkout_id       uuid,                         -- ref ordering.orders.checkout_id
  order_id          uuid,                         -- ref ordering.orders (refund nhắm vào một đơn)
  subscription_id   uuid,                         -- ref identity.subscriptions (phí gói)
  parent_payment_id uuid REFERENCES payment.payments(id),
  gateway           text NOT NULL CHECK (gateway IN ('PAYOS','MANUAL')),   -- payOS/SePay VietQR; MANUAL = chuyển khoản tay có chứng từ
  gateway_txn_id    text,
  idempotency_key   text NOT NULL UNIQUE,
  amount            bigint NOT NULL CHECK (amount > 0),
  status            text NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING','SUCCEEDED','FAILED','CANCELLED','EXPIRED')),
  approved_by       uuid,                         -- ref identity.users (refund; user SYSTEM khi tự động)
  reason            text,
  raw_payload       jsonb,                        -- webhook gốc để tra soát
  paid_at           timestamptz,
  created_at        timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at        timestamptz NOT NULL DEFAULT public.app_now(),
  UNIQUE (gateway, gateway_txn_id),
  CHECK ((kind = 'REFUND') = (parent_payment_id IS NOT NULL)),
  CHECK (kind <> 'REFUND' OR approved_by IS NOT NULL),
  CHECK (kind <> 'REFUND' OR purpose <> 'ORDER_CHECKOUT' OR order_id IS NOT NULL),
  CHECK (purpose <> 'ORDER_CHECKOUT' OR checkout_id IS NOT NULL),
  CHECK (purpose <> 'SUBSCRIPTION' OR subscription_id IS NOT NULL),
  CHECK ((status = 'SUCCEEDED') = (paid_at IS NOT NULL))
);
-- mỗi checkout chỉ có một khoản thu
CREATE UNIQUE INDEX payments_one_charge_per_checkout ON payment.payments (checkout_id) WHERE kind = 'CHARGE';
CREATE INDEX payments_refund_parent_idx ON payment.payments (parent_payment_id) WHERE kind = 'REFUND';
CREATE INDEX ON payment.payments (order_id);
-- tổng hoàn không vượt khoản thu gốc; khóa dòng CHARGE để các lượt hoàn chạy tuần tự
CREATE OR REPLACE FUNCTION payment.check_refund_total() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE charged bigint; refunded bigint;
BEGIN
  IF NEW.kind = 'REFUND' AND NEW.status IN ('PENDING','SUCCEEDED') THEN
    SELECT amount INTO charged FROM payment.payments WHERE id = NEW.parent_payment_id AND kind = 'CHARGE' AND status = 'SUCCEEDED' FOR UPDATE;
    IF charged IS NULL THEN RAISE EXCEPTION 'Khoản thu gốc không tồn tại hoặc chưa thành công'; END IF;
    SELECT coalesce(sum(amount),0) INTO refunded FROM payment.payments
      WHERE parent_payment_id = NEW.parent_payment_id AND kind = 'REFUND' AND status IN ('PENDING','SUCCEEDED') AND id <> NEW.id;
    IF refunded + NEW.amount > charged THEN
      RAISE EXCEPTION 'Tổng hoàn % vượt khoản thu %', refunded + NEW.amount, charged; END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER payments_refund_cap BEFORE INSERT OR UPDATE ON payment.payments FOR EACH ROW EXECUTE FUNCTION payment.check_refund_total();

-- sổ cái kép, append-only: dương = nợ (debit), âm = có (credit); mỗi bút toán (journal_id) tổng = 0
CREATE TABLE payment.ledger_entries (
  id         uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  journal_id uuid NOT NULL,
  -- PLATFORM_SUBSCRIPTION: doanh thu phí gói (nguồn thu duy nhất); PLATFORM_LOYALTY: nền tảng chịu phần giảm do đổi điểm
  account    text NOT NULL CHECK (account IN ('GATEWAY_CLEARING','SELLER_PENDING','SELLER_AVAILABLE','PLATFORM_SUBSCRIPTION','PLATFORM_LOYALTY','REFUND_CLEARING')),
  seller_id  uuid,                                -- ref identity.sellers (bắt buộc với tài khoản SELLER_*)
  amount     bigint NOT NULL CHECK (amount <> 0),
  -- ORDER/SETTLEMENT: ref_id = order; REFUND/REFUND_PAYOUT: ref_id = payments(REFUND); SUBSCRIPTION: ref_id = subscriptions; WITHDRAWAL
  ref_type   text NOT NULL CHECK (ref_type IN ('ORDER','REFUND','REFUND_PAYOUT','SUBSCRIPTION','WITHDRAWAL','SETTLEMENT')),
  ref_id     uuid NOT NULL,
  memo       text,
  created_at timestamptz NOT NULL DEFAULT public.app_now(),
  CHECK ((account LIKE 'SELLER_%') = (seller_id IS NOT NULL))
);
-- chống ghi sổ hai lần cho cùng một nghiệp vụ (webhook/sự kiện lặp)
CREATE UNIQUE INDEX ledger_once ON payment.ledger_entries (ref_type, ref_id, account, seller_id) NULLS NOT DISTINCT;
CREATE INDEX ON payment.ledger_entries (seller_id, account);
CREATE INDEX ON payment.ledger_entries (journal_id);
CREATE INDEX ON payment.ledger_entries (ref_id, account);
CREATE OR REPLACE FUNCTION payment.check_journal_balanced() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE s bigint;
BEGIN
  SELECT sum(amount) INTO s FROM payment.ledger_entries WHERE journal_id = NEW.journal_id;
  IF s <> 0 THEN RAISE EXCEPTION 'Bút toán % lệch % đồng', NEW.journal_id, s; END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER ledger_balanced AFTER INSERT ON payment.ledger_entries
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION payment.check_journal_balanced();
CREATE TRIGGER ledger_append_only BEFORE UPDATE OR DELETE ON payment.ledger_entries FOR EACH ROW EXECUTE FUNCTION public.forbid_change();

CREATE TABLE payment.withdrawal_requests (
  id                  uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  seller_id           uuid NOT NULL,              -- ref identity.sellers
  amount              bigint NOT NULL CHECK (amount > 0),   -- mức tối thiểu lấy từ system_settings (withdraw_min)
  status              text NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING','APPROVED','REJECTED','PAID','CANCELLED')),
  bank_name           text NOT NULL,              -- chụp lại tài khoản ngân hàng lúc yêu cầu
  bank_account_no_enc text NOT NULL,
  bank_holder         text NOT NULL,
  approved_by         uuid,                       -- ref identity.users
  reject_reason       text,
  paid_at             timestamptz,                -- chứng từ chuyển khoản nằm ở notify.attachments (owner_type = withdrawal, phase = PROOF)
  created_at          timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at          timestamptz NOT NULL DEFAULT public.app_now(),
  CHECK ((status = 'REJECTED') = (reject_reason IS NOT NULL)),
  CHECK (status NOT IN ('APPROVED','PAID') OR approved_by IS NOT NULL),
  CHECK ((status = 'PAID') = (paid_at IS NOT NULL))
);
CREATE UNIQUE INDEX withdrawal_one_pending ON payment.withdrawal_requests (seller_id) WHERE status IN ('PENDING','APPROVED');

-- số dư ví seller tính từ sổ cái (không lưu cột số dư). available âm = công nợ của seller (BR ví âm)
CREATE VIEW payment.v_seller_balance AS
SELECT seller_id,
  coalesce(-sum(amount) FILTER (WHERE account = 'SELLER_PENDING'), 0)   AS pending_balance,
  coalesce(-sum(amount) FILTER (WHERE account = 'SELLER_AVAILABLE'), 0) AS available_balance,
  greatest(coalesce(sum(amount) FILTER (WHERE account = 'SELLER_AVAILABLE'), 0), 0) AS debt
FROM payment.ledger_entries WHERE seller_id IS NOT NULL GROUP BY seller_id;

-- ============ notify (2) — Audit & Media ============
-- nhật ký kiểm toán: mọi thao tác nhạy cảm + lịch sử trạng thái đơn (thay order_events); append-only
CREATE TABLE notify.audit_logs (
  id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  actor_type  text NOT NULL CHECK (actor_type IN ('USER','KIOSK','SYSTEM')),
  actor_id    uuid,                               -- ref identity.users / kiosk (tham chiếu logic)
  action      text NOT NULL,                      -- vd ORDER_STATUS_CHANGED, PRODUCT_PRICE_CHANGED, REFUND_CONFIRMED, SETTING_CHANGED
  entity_type text NOT NULL,
  entity_id   uuid,
  payload     jsonb,                              -- vd {from, to, note} / {old, new}
  created_at  timestamptz NOT NULL DEFAULT public.app_clock()
);
CREATE INDEX ON notify.audit_logs (entity_type, entity_id, id);
CREATE TRIGGER audit_append_only BEFORE UPDATE OR DELETE ON notify.audit_logs FOR EACH ROW EXECUTE FUNCTION public.forbid_change();
CREATE TABLE notify.attachments (
  id            uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  owner_service text NOT NULL,
  owner_type    text NOT NULL,                    -- flower_product | dispute | inventory_log | refund | withdrawal
  owner_id      uuid NOT NULL,
  file_url      text NOT NULL,
  mime_type     text NOT NULL CHECK (mime_type IN ('image/jpeg','image/png','image/webp','application/pdf')),
  size_bytes    bigint NOT NULL CHECK (size_bytes > 0 AND size_bytes <= 10485760),
  sha256        text NOT NULL,
  phase         text NOT NULL CHECK (phase IN ('PRODUCT','EVIDENCE','RETURN','PROOF')),   -- ảnh sản phẩm / bằng chứng khiếu nại-sự cố / ảnh trả hàng / chứng từ chi tiền
  uploaded_by   uuid,                             -- ref identity.users
  created_at    timestamptz NOT NULL DEFAULT public.app_now()
);
CREATE INDEX ON notify.attachments (owner_type, owner_id);

-- ============ ai (1) ============
-- mỗi lượt khảo sát 5 câu tại kiosk = 1 dòng; results là 3 gợi ý đã validate; đây là log để train learning-to-rank ở phase sau
CREATE TABLE ai.gift_surveys (
  id                  uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  session_id          text NOT NULL,
  customer_id         uuid,                       -- ref identity.users (NULL = khách vãng lai)
  kiosk_id            uuid NOT NULL,              -- ref kiosk_ops.kiosks (chỉ gợi ý hàng còn tại kiosk này)
  recipient_type      text NOT NULL CHECK (recipient_type IN ('LOVER','MOTHER','FATHER','FRIEND','COLLEAGUE','TEACHER','BOSS','OTHER')),
  age_group           text CHECK (age_group IN ('UNDER_18','18_25','26_40','41_60','OVER_60')),
  occasion            text NOT NULL CHECK (occasion IN ('BIRTHDAY','ANNIVERSARY','VALENTINE','WOMENS_DAY','TEACHERS_DAY','MOTHERS_DAY','APOLOGY','CONGRATS','SYMPATHY','OTHER')),
  message_tone        text CHECK (message_tone IN ('ROMANTIC','WARM','FORMAL','FUNNY')),
  budget_max          bigint CHECK (budget_max > 0),
  results             jsonb NOT NULL DEFAULT '[]', -- [{product_id, bouquet_id, rank, score, reason, card_message}] đã qua validate
  model_version       text,                       -- vd rules-v1 | gemini-flash-2026-10+rules-v1
  source              text CHECK (source IN ('LLM','FALLBACK','CACHE')),
  latency_ms          int CHECK (latency_ms >= 0),          -- mục tiêu ≤ 2000 ms kể cả fallback (BRD O3)
  selected_product_id uuid,                       -- ref catalog.flower_products (khách bấm chọn)
  purchased_order_id  uuid,                       -- ref ordering.orders (đơn mua phát sinh)
  created_at          timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at          timestamptz NOT NULL DEFAULT public.app_now(),
  CHECK (jsonb_typeof(results) = 'array')
);
CREATE INDEX ON ai.gift_surveys (kiosk_id, created_at);
CREATE INDEX ON ai.gift_surveys (customer_id) WHERE customer_id IS NOT NULL;


-- updated_at tự cập nhật cho mọi bảng có cột này
DO $$ DECLARE r record; BEGIN
  FOR r IN SELECT table_schema, table_name FROM information_schema.columns
           WHERE column_name = 'updated_at' AND table_schema IN ('identity','catalog','kiosk_ops','ordering','payment','notify','ai')
  LOOP EXECUTE format('CREATE TRIGGER touch BEFORE UPDATE ON %I.%I FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at()', r.table_schema, r.table_name);
  END LOOP; END $$;
-- máy trạng thái cho 5 bảng có vòng đời
DO $$ DECLARE t text; BEGIN
  FOREACH t IN ARRAY ARRAY['ordering.orders','payment.payments','kiosk_ops.unlock_tokens','ordering.disputes','payment.withdrawal_requests'] LOOP
    EXECUTE format('CREATE TRIGGER guard_status BEFORE UPDATE OF status ON %s FOR EACH ROW EXECUTE FUNCTION public.guard_transition()', t);
  END LOOP; END $$;

-- Dữ liệu lịch sử demo 30 ngày × 2 kiosk, dựng HOÀN TOÀN bằng các lệnh nghiệp vụ flow.* (tua thời gian qua p_now).
-- Không INSERT/UPDATE thẳng vào bảng nghiệp vụ: mọi dòng đều đi qua cùng ràng buộc như vận hành thật.
-- Cuối file để sẵn "hàng chờ" cho buổi demo và kiểm số dòng tối thiểu cho từng màn hình + bất biến toàn cục.
\set ON_ERROR_STOP on
SET client_min_messages = warning;
SELECT setseed(0.2026);

-- ---------- cấu hình mô phỏng (bảng tạm, mất khi hết phiên) ----------
CREATE TEMP TABLE hist_slots (slot_id uuid PRIMARY KEY, kiosk_id uuid, seller_id uuid, staff_id uuid);
CREATE TEMP TABLE demo_customers (id uuid PRIMARY KEY, phone text);
CREATE TEMP TABLE demo_state (k text PRIMARY KEY, n int);
INSERT INTO demo_state VALUES ('surveys', 0), ('txn', 0);
CREATE TEMP TABLE demo_ctx (k text PRIMARY KEY, v uuid);

-- id cố định
CREATE OR REPLACE FUNCTION pg_temp.c(p text) RETURNS uuid LANGUAGE sql IMMUTABLE AS $$
  SELECT CASE p
    WHEN 'admin' THEN '10000000-0000-0000-0000-000000000001' WHEN 'tech' THEN '10000000-0000-0000-0000-000000000007'
    WHEN 'an' THEN '10000000-0000-0000-0000-000000000003'    WHEN 'bich' THEN '10000000-0000-0000-0000-000000000004'
    WHEN 'cuong' THEN '10000000-0000-0000-0000-000000000005' WHEN 'dung' THEN '10000000-0000-0000-0000-000000000006'
    WHEN 'hoa' THEN '10000000-0000-0000-0000-000000000010'
    WHEN 'sg' THEN '20000000-0000-0000-0000-000000000001'    WHEN 'moc' THEN '20000000-0000-0000-0000-000000000002'
    WHEN 'nhl' THEN '20000000-0000-0000-0000-000000000003'   WHEN 'basic' THEN '20000000-0000-0000-0000-00000000000a'
    WHEN 'q1' THEN '40000000-0000-0000-0000-000000000001'    WHEN 'td' THEN '40000000-0000-0000-0000-000000000002' END::uuid $$;
CREATE OR REPLACE FUNCTION pg_temp.slot(p text) RETURNS uuid LANGUAGE sql IMMUTABLE AS $$
  SELECT CASE left(p,1) WHEN 'A' THEN ('40000000-0000-0000-0001-0000000000' || right(p,2))::uuid ELSE ('40000000-0000-0000-0002-0000000000' || right(p,2))::uuid END $$;
CREATE OR REPLACE FUNCTION pg_temp.next_txn(p text) RETURNS text LANGUAGE sql AS $$
  UPDATE demo_state SET n = n + 1 WHERE k = 'txn' RETURNING p || '-' || n $$;
CREATE OR REPLACE FUNCTION pg_temp.pick(a text[]) RETURNS text LANGUAGE sql VOLATILE AS $$ SELECT a[1 + floor(random() * cardinality(a))::int] $$;
CREATE OR REPLACE FUNCTION pg_temp.open_door(p_order uuid, p_at timestamptz) RETURNS void LANGUAGE plpgsql AS $$
DECLARE tok record;
BEGIN
  FOR tok IN SELECT t.cmd_id FROM kiosk_ops.unlock_tokens t WHERE t.order_id = p_order AND t.status = 'ISSUED' LOOP
    PERFORM flow.device_event(tok.cmd_id, 'SENT',   p_at);
    PERFORM flow.device_event(tok.cmd_id, 'ACK',    p_at + interval '2 seconds');
    PERFORM flow.device_event(tok.cmd_id, 'OPENED', p_at + interval '10 seconds');
    PERFORM flow.device_event(tok.cmd_id, 'CLOSED', p_at + interval '40 seconds');
  END LOOP;
END $$;

-- ---------- 0. Bối cảnh trước lịch sử (31 ngày trước) ----------
DO $$
DECLARE t0 timestamptz := now() - interval '31 days'; nha uuid; phuc uuid; r uuid; s record; p record; i int;
BEGIN
  -- duyệt Hoa Nhà Làm, tạo tài khoản shop, kích hoạt sản phẩm
  nha  := flow.add_seller_user(pg_temp.c('nhl'), 'Lâm Thị Nhã', 'nha@hoanhalam.vn', '0909000021', t0);
  phuc := flow.add_seller_user(pg_temp.c('nhl'), 'Tạ Văn Phúc', 'phuc@hoanhalam.vn', '0909000022', t0);
  INSERT INTO demo_ctx VALUES ('nha', nha), ('phuc', phuc);
  PERFORM flow.approve_seller(pg_temp.c('nhl'), pg_temp.c('admin'), pg_temp.c('basic'), 'Mộc mạc, chân thành, như hoa nhà trồng', t0);
  PERFORM flow.set_seller_bank(pg_temp.c('nhl'), nha, 'ACB', 'enc:v1:TkhMOTg3NjU0', 'Lam Thi Nha');
  FOR p IN SELECT id FROM catalog.flower_products WHERE seller_id = pg_temp.c('nhl') LOOP PERFORM flow.set_product_status(p.id, nha, 'ACTIVE'); END LOOP;
  PERFORM flow.set_product_status('30000000-0000-0000-0000-000000000005', pg_temp.c('bich'), 'ACTIVE');   -- hồng pastel hộp
  -- ảnh sản phẩm (2 ảnh/sản phẩm, do AI sinh theo prompt mẫu — ghi rõ trong tài liệu)
  FOR p IN SELECT fp.id, (SELECT u.id FROM identity.users u WHERE u.seller_id = fp.seller_id LIMIT 1) owner
           FROM catalog.flower_products fp WHERE fp.status = 'ACTIVE' ORDER BY fp.id LOOP
    FOR i IN 1..2 LOOP PERFORM flow.add_product_photo(p.id, p.owner, 'https://res.cloudinary.com/florabot/product/' || p.id || '-' || i || '.webp'); END LOOP;
  END LOOP;
  -- gói thuê bao: kỳ quá khứ 2 tháng cho Hoa Sài Gòn / Hoa Mộc (nối liền kỳ đã trả hôm nay ở KB1), Hoa Nhà Làm 1 tháng từ t0
  -- (hết hạn ngay trước hôm nay -> trong lịch sử có một ngày PAST_DUE rồi trả tiền mở lại: xem sim_day)
  FOR s IN SELECT * FROM (VALUES ('sg','an',(current_date - interval '2 months')::date, 2), ('moc','bich',(current_date - interval '2 months')::date, 2),
                                 ('nhl', NULL, current_date - 31, 1)) v(seller, owner, pfrom, months) LOOP
    r := flow.subscribe(pg_temp.c(s.seller), coalesce(pg_temp.c(s.owner), nha), s.months, s.pfrom, t0);
    PERFORM flow.subscription_paid(r, 'PAYOS-SH-' || s.seller, t0 + interval '5 minutes');
  END LOOP;
  -- seller thứ 4 "Hoa Cúc Vàng": trả 1 tháng cách đây 40 ngày rồi bỏ -> đang PAST_DUE quá ân hạn (hàng chờ admin "Seller quá hạn gói")
  r := flow.register_seller('Hoa Cúc Vàng', '0901000005', '19 Hoàng Hoa Thám, Tân Bình', 'Vũ Thị Cúc', 'cuc@hoacucvang.vn', t0 - interval '9 days');
  INSERT INTO demo_ctx VALUES ('cucvang', r);
  PERFORM flow.approve_seller(r, pg_temp.c('admin'), pg_temp.c('basic'), 'Vui tươi, màu sắc', t0 - interval '9 days');
  r := flow.subscribe(r, (SELECT id FROM identity.users WHERE seller_id = r LIMIT 1), 1, current_date - 40, t0 - interval '9 days');
  PERFORM flow.subscription_paid(r, 'PAYOS-SH-cucvang', t0 - interval '9 days' + interval '5 minutes');
  -- admin gán ô (Q1: A01/A02/A05 sg, A03/A06 moc đã gán ở KB1; thêm B01/B02 moc, A04/B03/B04 nhl)
  FOR s IN SELECT * FROM (VALUES
      ('A01','sg',  'dung'), ('A02','sg',  'dung'),
      ('A03','moc', 'cuong'),('A06','moc', 'cuong'), ('B01','moc', 'hoa'), ('B02','moc', 'hoa'),
      ('A04','nhl', 'nha'),  ('B03','nhl', 'phuc'),  ('B04','nhl', 'phuc')) v(slot, seller, staff) LOOP
    IF NOT EXISTS (SELECT 1 FROM kiosk_ops.slot_assignments WHERE slot_id = pg_temp.slot(s.slot) AND status = 'ACTIVE') THEN
      PERFORM flow.assign_slot(pg_temp.slot(s.slot), pg_temp.c(s.seller), pg_temp.c('admin'), t0 + interval '10 minutes');
    END IF;
    INSERT INTO hist_slots VALUES (pg_temp.slot(s.slot), (SELECT kiosk_id FROM kiosk_ops.slots WHERE id = pg_temp.slot(s.slot)), pg_temp.c(s.seller),
                                   coalesce(pg_temp.c(s.staff), CASE s.staff WHEN 'nha' THEN nha ELSE phuc END));
  END LOOP;
  -- khách đăng nhập tại kiosk bằng SĐT (OTP) — cộng với Giang, Huy, Khoa = 10 khách
  FOR s IN SELECT * FROM (VALUES ('Nguyễn Thảo My','0913000101'),('Trần Quốc Bảo','0913000102'),('Phạm Minh Châu','0913000103'),('Lê Hoàng Duy','0913000104'),
      ('Võ Ngọc Hân','0913000105'),('Bùi Gia Khang','0913000106'),('Đặng Thu Trang','0913000107')) v(n, ph) LOOP
    PERFORM flow.customer_by_phone(s.ph, s.n, t0 + interval '1 hour');
  END LOOP;
  INSERT INTO demo_customers SELECT id, phone FROM identity.users WHERE role = 'CUSTOMER' AND status = 'ACTIVE';
END $$;

-- ---------- một lượt mua tại kiosk ----------
CREATE OR REPLACE FUNCTION pg_temp.sale(p_kiosk uuid, p_bouquets uuid[], p_at timestamptz, p_outcome text DEFAULT NULL) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE cu demo_customers; v_acc jsonb := '[]'; v_sub bigint; co uuid; v_amt bigint; s uuid; v_out text; r float8; v_sn int; v_pts bigint := 0; v_bal bigint;
        v_ecard text;
BEGIN
  IF random() < 0.55 THEN SELECT * INTO cu FROM demo_customers ORDER BY random() LIMIT 1; END IF;   -- 55% khách đăng nhập tích điểm
  SELECT sum(price_snapshot) INTO v_sub FROM kiosk_ops.bouquets WHERE id = ANY(p_bouquets);
  IF random() < 0.15 THEN
    SELECT jsonb_build_array(jsonb_build_object('id', id, 'qty', 1)) INTO v_acc FROM kiosk_ops.accessories
     WHERE kiosk_id = p_kiosk AND name = 'Thiệp chúc mừng' AND stock_quantity > 2;
    v_acc := coalesce(v_acc, '[]');
  END IF;
  IF cu.id IS NOT NULL THEN
    SELECT loyalty_points INTO v_bal FROM identity.users WHERE id = cu.id;
    IF v_bal >= 2000 AND random() < 0.4 THEN v_pts := least(v_bal, floor(v_sub * 0.1 / 1000) * 1000); END IF;   -- đổi điểm ~10% đơn
  END IF;
  SELECT n INTO v_sn FROM demo_state WHERE k = 'surveys';
  IF random() < 0.3 THEN
    s := flow.ai_suggest('kiosk-' || left(md5(random()::text), 8), cu.id, p_kiosk,
                         pg_temp.pick(ARRAY['LOVER','MOTHER','FRIEND','COLLEAGUE','TEACHER','BOSS']), pg_temp.pick(ARRAY['18_25','26_40','41_60']),
                         pg_temp.pick(ARRAY['BIRTHDAY','ANNIVERSARY','CONGRATS','APOLOGY','OTHER']), pg_temp.pick(ARRAY['ROMANTIC','WARM','FORMAL','FUNNY']),
                         600000, p_at - interval '3 minutes');
    v_ecard := pg_temp.pick(ARRAY['Chúc mừng sinh nhật!','Cảm ơn vì tất cả','Yêu mẹ nhiều','Chúc bạn một ngày rực rỡ']);
    IF random() < 0.7 AND (SELECT jsonb_array_length(results) FROM ai.gift_surveys WHERE id = s) > 0 THEN
      PERFORM flow.ai_record_llm(s, (SELECT jsonb_agg(e || jsonb_build_object('reason', 'Hợp dịp và người nhận bạn chọn', 'card_message', v_ecard)) FROM jsonb_array_elements((SELECT results FROM ai.gift_surveys WHERE id = s)) e),
                                 'gemini-flash-2026-10+rules-v1', 700 + (random() * 900)::int);
    END IF;
  END IF;
  co := flow.kiosk_checkout(p_kiosk, p_bouquets, cu.id, v_acc, v_pts, v_ecard, p_at);
  SELECT amount INTO v_amt FROM payment.payments WHERE checkout_id = co;
  r := random();
  v_out := coalesce(p_outcome, CASE WHEN r < 0.04 THEN 'FAIL' WHEN r < 0.08 THEN 'ABANDON' ELSE 'OK' END);
  IF v_out = 'FAIL' THEN PERFORM flow.checkout_paid(co, pg_temp.next_txn('PAYOS-H'), false, v_amt, p_at + interval '1 minute'); RETURN co; END IF;
  IF v_out = 'ABANDON' THEN RETURN co; END IF;
  PERFORM flow.checkout_paid(co, pg_temp.next_txn('PAYOS-H'), true, v_amt, p_at + interval '1 minute');
  IF s IS NOT NULL THEN
    PERFORM flow.ai_mark_purchased(s, (SELECT id FROM ordering.orders WHERE checkout_id = co ORDER BY total_amount DESC LIMIT 1),
                                   (SELECT b.product_id FROM kiosk_ops.bouquets b WHERE b.id = p_bouquets[1]));
    UPDATE demo_state SET n = n + 1 WHERE k = 'surveys';
  END IF;
  IF v_out = 'DOOR_FAIL' THEN RETURN co; END IF;   -- để lại cho kịch bản sự cố
  PERFORM pg_temp.open_door((SELECT id FROM ordering.orders WHERE checkout_id = co ORDER BY id LIMIT 1), p_at + interval '1 minute 5 seconds');   -- token TTL 60 giây kể từ lúc trả tiền
  PERFORM pg_temp.open_door((SELECT id FROM ordering.orders WHERE checkout_id = co ORDER BY id DESC LIMIT 1), p_at + interval '1 minute 10 seconds');
  RETURN co;
END $$;

-- ---------- mô phỏng một ngày ----------
CREATE OR REPLACE FUNCTION pg_temp.sim_day(d int) RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_base timestamptz := now() - make_interval(days => d); s record; p uuid; n int := 0; bt uuid;
        v_list uuid[]; v_kiosk uuid; i int; v_at timestamptz; v_b uuid; co uuid; o record; dsp uuid; ref uuid; w uuid; v_av bigint; inc uuid; tok uuid;
        v_near uuid := (SELECT v FROM demo_ctx WHERE k = 'near_bouquet');
BEGIN
  -- 0h: job gói thuê bao (kỳ hết hạn -> seller PAST_DUE, khóa bán); seller PAST_DUE từ hôm qua thì sáng nay trả tiền -> mở lại
  PERFORM flow.roll_subscriptions(v_base::date);
  FOR s IN SELECT DISTINCT h.seller_id, (SELECT u.id FROM identity.users u WHERE u.seller_id = h.seller_id ORDER BY u.created_at LIMIT 1) owner_id
           FROM hist_slots h JOIN identity.sellers se ON se.id = h.seller_id WHERE se.status = 'PAST_DUE' AND se.package_expires_at < v_base::date LOOP
    PERFORM flow.subscription_paid(flow.subscribe(s.seller_id, s.owner_id, 1, NULL, v_base - interval '9 hours 58 minutes'), pg_temp.next_txn('PAYOS-SUB'), v_base - interval '9 hours 57 minutes');
  END LOOP;
  -- sáng: seller lấy về các bó quá hạn / đơn hoàn tiền (ô PENDING_REMOVAL từ hôm trước)
  FOR s IN SELECT sl.id AS slot_id, h.staff_id FROM kiosk_ops.slots sl JOIN hist_slots h ON h.slot_id = sl.id WHERE sl.status IN ('PENDING_REMOVAL','PENDING_RELEASE') LOOP
    PERFORM flow.return_to_seller(s.slot_id, s.staff_id, 'https://res.cloudinary.com/florabot/return/' || s.slot_id || '-' || d || '.jpg', 'Thu hồi buổi sáng', false, v_base - interval '9 hours 45 minutes');
  END LOOP;
  -- sự cố cửa ở Thủ Đức (ngày 20, 12): seller báo lúc sáng, kỹ thuật xử lý trước giờ bán
  IF d IN (20, 12) THEN
    inc := flow.report_device_fault(pg_temp.c('td'), pg_temp.slot('B02'), pg_temp.c('hoa'), 'Cửa B02 đóng không khít',
                                    'https://res.cloudinary.com/florabot/incident/b02-' || d || '.jpg', v_base - interval '9 hours 50 minutes');
    PERFORM flow.resolve_device_fault(inc, pg_temp.c('tech'), 'Chỉnh lại chốt cửa, test 10 lần OK', v_base - interval '9 hours 35 minutes');
  END IF;
  -- nạp hàng buổi sáng: mỗi seller một lô (batch) cho mỗi kiosk
  FOR s IN SELECT DISTINCT h.seller_id, h.kiosk_id FROM hist_slots h JOIN identity.sellers se ON se.id = h.seller_id WHERE se.status = 'ACTIVE' LOOP   -- seller PAST_DUE không nạp được
    bt := flow.new_batch();
    FOR p IN SELECT h.slot_id FROM hist_slots h JOIN kiosk_ops.slots sl ON sl.id = h.slot_id
             WHERE h.seller_id = s.seller_id AND h.kiosk_id = s.kiosk_id AND sl.status = 'RENTED_EMPTY' AND NOT (d = 2 AND sl.id = pg_temp.slot('A02')) ORDER BY sl.slot_code LOOP
      SELECT id INTO v_b FROM catalog.flower_products WHERE seller_id = s.seller_id AND status = 'ACTIVE' ORDER BY random() LIMIT 1;
      CONTINUE WHEN v_b IS NULL;
      PERFORM flow.stock_bouquet(bt, v_b, p, (SELECT staff_id FROM hist_slots WHERE slot_id = p), 'BQ-H' || d || '-' || (SELECT slot_code FROM kiosk_ops.slots WHERE id = p), v_base - interval '9 hours 20 minutes');
    END LOOP;
  END LOOP;
  -- bán trong ngày (mỗi kiosk), đôi khi khách mua 2 bó một lần
  FOREACH v_kiosk IN ARRAY ARRAY[pg_temp.c('q1'), pg_temp.c('td')] LOOP
    SELECT array_agg(sl.bouquet_id ORDER BY random()) INTO v_list FROM kiosk_ops.slots sl JOIN hist_slots h ON h.slot_id = sl.id
     JOIN kiosk_ops.bouquets b ON b.id = sl.bouquet_id JOIN identity.sellers se ON se.id = sl.current_seller_id
     WHERE sl.kiosk_id = v_kiosk AND sl.status = 'STOCKED' AND b.sellable_until > v_base + interval '1 hour' AND b.id IS DISTINCT FROM v_near
       AND se.status = 'ACTIVE' AND se.package_expires_at > v_base::date   -- seller PAST_DUE: hoa vẫn trong ô nhưng không bán
       AND NOT (d = 2 AND sl.id = pg_temp.slot('A02'));
    i := 1;
    WHILE v_list IS NOT NULL AND i <= cardinality(v_list) LOOP
      v_at := v_base - interval '8 hours' + make_interval(mins => (random() * 520)::int);
      IF random() < 0.80 THEN
        IF i < cardinality(v_list) AND random() < 0.15 THEN
          co := pg_temp.sale(v_kiosk, ARRAY[v_list[i], v_list[i+1]], v_at, CASE WHEN d = 15 AND v_kiosk = pg_temp.c('q1') AND n = 0 THEN 'ABANDON' END);
          i := i + 2;
        ELSE
          co := pg_temp.sale(v_kiosk, ARRAY[v_list[i]], v_at, CASE WHEN d = 15 AND v_kiosk = pg_temp.c('q1') AND n = 0 THEN 'ABANDON'
                                                                   WHEN d IN (22, 8) AND v_kiosk = pg_temp.c('td') AND i = 1 THEN 'DOOR_FAIL' END);
          i := i + 1;
        END IF;
        IF d = 15 AND v_kiosk = pg_temp.c('q1') AND n = 0 THEN INSERT INTO demo_ctx VALUES ('late_co', co) ON CONFLICT (k) DO UPDATE SET v = EXCLUDED.v; END IF;
        -- tủ không nhả hàng (ngày 22, 8): 2 lần gửi lệnh thất bại -> tự hoàn 100%, admin chi hoàn trong ngày, kỹ thuật sửa
        IF d IN (22, 8) AND v_kiosk = pg_temp.c('td') AND i = 2 THEN   -- vừa xử lý bó đầu tiên của Thủ Đức
          SELECT od.id INTO o FROM ordering.orders od WHERE od.checkout_id = co AND od.status = 'DISPENSING' ORDER BY od.id LIMIT 1;   -- bỏ qua đơn chỉ có phụ kiện (giao ngay)
          SELECT cmd_id INTO tok FROM kiosk_ops.unlock_tokens WHERE order_id = o.id AND status = 'ISSUED' ORDER BY issued_at LIMIT 1;
          PERFORM flow.device_event(tok, 'SENT', v_at + interval '1 minute 5 seconds'); PERFORM flow.device_event(tok, 'SENT', v_at + interval '1 minute 20 seconds');
          PERFORM flow.device_event(tok, 'FAILED', v_at + interval '1 minute 40 seconds');
          -- khách quét e-receipt khai STK nhận hoàn (A1), admin chuyển khoản sau 3 giờ
          PERFORM flow.submit_refund_info(o.id, (SELECT tracking_token FROM ordering.orders WHERE id = o.id), pg_temp.pick(ARRAY['Vietcombank','ACB','Techcombank','MB']), 'enc:v1:' || encode(gen_random_bytes(6), 'base64'), 'Khach Hang Kiosk', v_at + interval '20 minutes');
          PERFORM flow.confirm_refund((SELECT id FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND'), pg_temp.c('admin'),
                                      'https://res.cloudinary.com/florabot/refund/door-' || d || '.pdf', v_at + interval '3 hours');
          PERFORM flow.resolve_device_fault((SELECT id FROM ordering.disputes WHERE order_id = o.id AND kind = 'DISPENSE_FAILED'), pg_temp.c('tech'), 'Thay khóa điện từ', v_at + interval '5 hours');
        END IF;
        n := n + 1;
      ELSE i := i + 1; END IF;
    END LOOP;
  END LOOP;
  -- khiếu nại (ngày 25, 18, 10, 4): đơn đã lấy trong ngày, khách quét e-receipt báo sau 3 giờ
  IF d IN (25, 18, 10, 4) THEN
    SELECT od.* INTO o FROM ordering.orders od WHERE od.status = 'COMPLETED' AND od.completed_at BETWEEN v_base - interval '10 hours' AND v_base + interval '2 hours'
     ORDER BY od.total_amount DESC, od.id LIMIT 1;
    IF o.id IS NOT NULL THEN
      dsp := flow.open_dispute(o.id, o.tracking_token, CASE WHEN d IN (25,10) THEN 'Hoa bị dập vài bông' ELSE 'Màu hoa khác ảnh' END,
                               'https://res.cloudinary.com/florabot/dispute/' || o.id || '.jpg', o.completed_at + interval '3 hours');
      IF d IN (25, 10, 4) THEN
        ref := flow.resolve_dispute(dsp, pg_temp.c('admin'), (round(o.total_amount * 0.3 / 1000) * 1000)::bigint, 'Hoàn 30% do lỗi chất lượng (đối chiếu ảnh nạp và ảnh khách)', o.completed_at + interval '20 hours');
        PERFORM flow.submit_refund_info(o.id, o.tracking_token, pg_temp.pick(ARRAY['Vietcombank','ACB','Techcombank']), 'enc:v1:' || encode(gen_random_bytes(6), 'base64'), 'Khach Khieu Nai', o.completed_at + interval '21 hours');
        PERFORM flow.confirm_refund(ref, pg_temp.c('admin'), 'https://res.cloudinary.com/florabot/refund/' || ref || '.pdf', o.completed_at + interval '22 hours');
      ELSE
        PERFORM flow.resolve_dispute(dsp, pg_temp.c('admin'), 0, 'Ảnh lúc nạp cho thấy hoa đúng mẫu', o.completed_at + interval '20 hours');
      END IF;
    END IF;
  END IF;
  -- cuối ngày (ngày 2): nạp bó cúc họa mi sẽ "sắp hết hạn" lúc demo
  IF d = 2 THEN
    IF (SELECT status FROM kiosk_ops.slots WHERE id = pg_temp.slot('A02')) = 'STOCKED' THEN
      IF (SELECT b.sellable_until > v_base + interval '1 hour 5 minutes' FROM kiosk_ops.bouquets b JOIN kiosk_ops.slots sl ON sl.bouquet_id = b.id WHERE sl.id = pg_temp.slot('A02')) THEN
        PERFORM pg_temp.sale(pg_temp.c('q1'), ARRAY[(SELECT bouquet_id FROM kiosk_ops.slots WHERE id = pg_temp.slot('A02'))], v_base + interval '1 hour 5 minutes', 'OK');
      ELSE   -- bó cũ trong A02 đã quá hạn: rút rồi trả seller ngay để trống ô
        PERFORM flow.expire_bouquets(v_base + interval '1 hour 5 minutes');
        PERFORM flow.return_to_seller(pg_temp.slot('A02'), pg_temp.c('dung'), 'https://res.cloudinary.com/florabot/return/a02-d2.jpg', 'Quá hạn, trả seller', false, v_base + interval '1 hour 10 minutes');
      END IF;
    END IF;
    v_b := flow.stock_bouquet(flow.new_batch(), '30000000-0000-0000-0000-000000000002', pg_temp.slot('A02'), pg_temp.c('dung'), 'BQ-NEAR-EXPIRY', now() - interval '46 hours 30 minutes');
    INSERT INTO demo_ctx VALUES ('near_bouquet', v_b);
  END IF;
  -- job buổi tối
  PERFORM flow.expire_pending_payments(v_base + interval '1 hour 30 minutes');
  PERFORM flow.expire_pending_subscriptions(v_base + interval '1 hour 30 minutes');
  PERFORM flow.expire_tokens(v_base + interval '1 hour 30 minutes');
  PERFORM flow.expire_pickups(v_base + interval '1 hour 30 minutes');
  PERFORM flow.expire_bouquets(v_base + interval '1 hour 30 minutes');
  PERFORM flow.settle_due_orders(v_base + interval '1 hour 30 minutes');
  -- tiền về muộn (ngày 15): payOS báo thành công sau khi đơn đã hết hạn
  IF d = 15 AND (SELECT v FROM demo_ctx WHERE k = 'late_co') IS NOT NULL THEN
    co := (SELECT v FROM demo_ctx WHERE k = 'late_co');
    PERFORM flow.checkout_paid(co, 'PAYOS-LATE-H15', true, (SELECT amount FROM payment.payments WHERE checkout_id = co AND kind = 'CHARGE'), v_base + interval '1 hour 40 minutes');
    PERFORM flow.submit_refund_info((SELECT id FROM ordering.orders WHERE checkout_id = co), (SELECT tracking_token FROM ordering.orders WHERE checkout_id = co), 'BIDV', 'enc:v1:TEFURTE1', 'Khach Tra Muon', v_base + interval '10 hours');
    PERFORM flow.confirm_refund((SELECT id FROM payment.payments WHERE checkout_id = co AND kind = 'REFUND'), pg_temp.c('admin'),
                                'https://res.cloudinary.com/florabot/refund/late-h15.pdf', v_base + interval '20 hours');
  END IF;
  -- rút tiền hằng tuần: yêu cầu tối nay, admin chi sáng mai
  IF d IN (28, 21, 14, 7) THEN
    FOR s IN SELECT DISTINCT h.seller_id, (SELECT u.id FROM identity.users u WHERE u.seller_id = h.seller_id ORDER BY u.created_at LIMIT 1) owner_id FROM hist_slots h LOOP
      SELECT available_balance INTO v_av FROM payment.v_seller_balance WHERE seller_id = s.seller_id;
      IF coalesce(v_av, 0) >= 200000 THEN
        w := flow.request_withdrawal(s.seller_id, s.owner_id, (floor(v_av * 0.8 / 1000) * 1000)::bigint, v_base + interval '1 hour 45 minutes');
        PERFORM flow.pay_withdrawal(w, pg_temp.c('admin'), 'https://res.cloudinary.com/florabot/payout/' || w || '.pdf', v_base + interval '15 hours');
      END IF;
    END LOOP;
  END IF;
END $$;

-- ---------- chạy 30 ngày ----------
DO $$ DECLARE d int; BEGIN FOR d IN REVERSE 30..1 LOOP PERFORM pg_temp.sim_day(d); END LOOP; END $$;

-- ---------- hôm nay: hàng chờ cho buổi demo ----------
DO $$
DECLARE s record; p uuid; o uuid; co uuid; d uuid; v_amt bigint; my uuid; bt uuid; b uuid;
BEGIN
  -- job gói thuê bao đầu ngày; seller PAST_DUE có ô thì trả tiền sáng nay (Hoa Cúc Vàng không có ô, để nguyên PAST_DUE làm hàng chờ admin)
  PERFORM flow.roll_subscriptions(current_date);
  FOR s IN SELECT DISTINCT h.seller_id, (SELECT u.id FROM identity.users u WHERE u.seller_id = h.seller_id ORDER BY u.created_at LIMIT 1) owner_id
           FROM hist_slots h JOIN identity.sellers se ON se.id = h.seller_id WHERE se.status = 'PAST_DUE' LOOP
    PERFORM flow.subscription_paid(flow.subscribe(s.seller_id, s.owner_id, 1, NULL, now() - interval '3 hours 10 minutes'), pg_temp.next_txn('PAYOS-SUB'), now() - interval '3 hours 9 minutes');
  END LOOP;
  -- thu hồi hoa quá hạn đầu ca
  PERFORM flow.expire_bouquets(now() - interval '3 hours');
  FOR s IN SELECT sl.id AS slot_id, h.staff_id FROM kiosk_ops.slots sl JOIN hist_slots h ON h.slot_id = sl.id WHERE sl.status IN ('PENDING_REMOVAL','PENDING_RELEASE') LOOP
    PERFORM flow.return_to_seller(s.slot_id, s.staff_id, 'https://res.cloudinary.com/florabot/return/' || s.slot_id || '-today.jpg', 'Thu hồi đầu ca', false, now() - interval '2 hours 50 minutes');
  END LOOP;
  -- nạp hàng hôm nay
  FOR s IN SELECT DISTINCT seller_id, kiosk_id FROM hist_slots LOOP
    bt := flow.new_batch();
    FOR p IN SELECT h.slot_id FROM hist_slots h JOIN kiosk_ops.slots sl ON sl.id = h.slot_id WHERE h.seller_id = s.seller_id AND h.kiosk_id = s.kiosk_id AND sl.status = 'RENTED_EMPTY' ORDER BY sl.slot_code LOOP
      SELECT id INTO b FROM catalog.flower_products WHERE seller_id = s.seller_id AND status = 'ACTIVE' ORDER BY random() LIMIT 1;
      CONTINUE WHEN b IS NULL;
      PERFORM flow.stock_bouquet(bt, b, p, (SELECT staff_id FROM hist_slots WHERE slot_id = p), 'BQ-TODAY-' || (SELECT slot_code FROM kiosk_ops.slots WHERE id = p), now() - interval '50 minutes');
    END LOOP;
  END LOOP;
  -- 1 đơn kiosk vừa lấy hàng sáng nay -> khách quét e-receipt mở khiếu nại (OPEN)
  SELECT id INTO my FROM identity.users WHERE phone = '0913000101';
  SELECT sl.bouquet_id INTO p FROM kiosk_ops.slots sl JOIN hist_slots h ON h.slot_id = sl.id WHERE sl.status = 'STOCKED' AND sl.kiosk_id = pg_temp.c('td') ORDER BY sl.slot_code LIMIT 1;
  co := flow.kiosk_checkout(pg_temp.c('td'), ARRAY[p], my, '[]', 0, 'Chúc mừng tốt nghiệp!', now() - interval '40 minutes');
  PERFORM flow.checkout_paid(co, 'PAYOS-TODAY-1', true, (SELECT amount FROM payment.payments WHERE checkout_id = co), now() - interval '39 minutes');
  o := (SELECT id FROM ordering.orders WHERE checkout_id = co);
  PERFORM pg_temp.open_door(o, now() - interval '38 minutes 50 seconds');
  d := flow.open_dispute(o, (SELECT tracking_token FROM ordering.orders WHERE id = o), 'Bó hoa thiếu 2 bông so với mô tả', 'https://res.cloudinary.com/florabot/dispute/today.jpg', now() - interval '10 minutes');
  -- 1 sự cố thiết bị đang mở: seller báo cửa B04 không đóng khít
  PERFORM flow.report_device_fault(pg_temp.c('td'), pg_temp.slot('B04'), (SELECT v FROM demo_ctx WHERE k='phuc'), 'Cửa B04 đóng không khít',
                                   'https://res.cloudinary.com/florabot/incident/b04-today.jpg', now() - interval '20 minutes');
  -- 1 đơn đang nhả hàng (DISPENSING) ở Q1: khách vừa trả tiền, token còn hiệu lực
  SELECT sl.bouquet_id INTO p FROM kiosk_ops.slots sl JOIN hist_slots h ON h.slot_id = sl.id WHERE sl.status = 'STOCKED' AND sl.kiosk_id = pg_temp.c('q1') ORDER BY sl.slot_code DESC LIMIT 1;
  co := flow.kiosk_checkout(pg_temp.c('q1'), ARRAY[p], '10000000-0000-0000-0000-000000000008', '[]', 0, 'Yêu em', now() - interval '30 seconds');
  PERFORM flow.checkout_paid(co, 'PAYOS-TODAY-2', true, (SELECT amount FROM payment.payments WHERE checkout_id = co), now() - interval '20 seconds');
  -- 1 seller mới đăng ký, chờ duyệt
  PERFORM flow.register_seller('Hoa Đà Lạt Xanh', '0901000004', '102 Nguyễn Đình Chiểu, Q3', 'Hồ Thanh Xuân', 'xuan@dalatxanh.vn', now() - interval '2 days');
  -- 1 yêu cầu rút tiền chờ admin duyệt
  PERFORM flow.request_withdrawal(pg_temp.c('moc'), pg_temp.c('bich'),
          least(300000, (floor((SELECT available_balance FROM payment.v_seller_balance WHERE seller_id = pg_temp.c('moc')) * 0.5 / 1000) * 1000)::bigint), now() - interval '1 hour');
  -- job cuối: đối soát đơn quá 24h
  PERFORM flow.settle_due_orders(now());
  PERFORM flow.reset_clock();
END $$;

-- ---------- kiểm số dòng cho từng màn hình + bất biến ----------
\o /dev/null
SET client_min_messages = notice;
DO $$
BEGIN
  RAISE NOTICE '--- 05: kiểm dữ liệu demo ---';
  PERFORM flow.ok((SELECT count(DISTINCT ngay) FROM screen.v_revenue_daily) >= 25, 'Doanh thu theo ngày: >= 25 ngày có doanh thu');
  PERFORM flow.ok((SELECT count(DISTINCT kiosk) FROM screen.v_revenue_daily) = 2, 'Cả 2 kiosk có doanh thu');
  PERFORM flow.ok((SELECT count(*) FROM ordering.orders) BETWEEN 150 AND 320, 'Tổng số đơn trong khoảng 150-320 (thực tế ' || (SELECT count(*) FROM ordering.orders) || ')');
  PERFORM flow.ok((SELECT count(*) FROM identity.users WHERE role = 'CUSTOMER' AND status = 'ACTIVE') BETWEEN 10 AND 12, '10-12 khách hàng tích điểm đang hoạt động');
  PERFORM flow.ok((SELECT count(*) FROM identity.users WHERE role = 'CUSTOMER' AND loyalty_points > 0) >= 5, '>= 5 khách đang có điểm');
  PERFORM flow.ok((SELECT count(*) FROM ordering.orders WHERE points_redeemed > 0) >= 5, '>= 5 đơn có đổi điểm');
  PERFORM flow.ok((SELECT count(*) FROM identity.sellers WHERE status = 'ACTIVE') = 3, '3 seller đang hoạt động (đã duyệt Hoa Nhà Làm)');
  PERFORM flow.ok((SELECT count(*) FROM identity.sellers WHERE status = 'PAST_DUE') = 1 AND (SELECT count(*) FROM notify.audit_logs WHERE action = 'SELLER_PAST_DUE') >= 2
                  AND (SELECT count(*) FROM notify.audit_logs WHERE action = 'SELLER_REACTIVATED') >= 1, 'Lịch sử có seller hết hạn gói -> PAST_DUE -> trả tiền mở lại; 1 seller đang PAST_DUE (hàng chờ admin)');
  PERFORM flow.ok((SELECT count(*) FROM identity.subscriptions WHERE status = 'ACTIVE') >= 4 AND (SELECT count(*) FROM identity.subscriptions WHERE status = 'EXPIRED') >= 3, 'Có kỳ gói đang chạy và kỳ đã hết hạn');
  PERFORM flow.ok((SELECT count(*) FROM notify.audit_logs WHERE action = 'REFUND_INFO_SUBMITTED') >= 5 AND (SELECT count(*) FROM notify.audit_logs WHERE action = 'REFUND_INFO_CLEARED') >= 5
                  AND NOT EXISTS (SELECT 1 FROM ordering.orders WHERE refund_bank_name IS NOT NULL), 'Mọi lần hoàn tiền đều qua khách khai STK trên e-receipt, chi xong đã xóa STK');
  PERFORM flow.ok((SELECT bool_and(lech = 0) FROM flow.reconcile_daily(current_date - 1) WHERE muc NOT LIKE 'Lệnh hoàn%' AND muc NOT LIKE 'Tiền đang chờ%'), 'Đối soát nội bộ hôm qua: chứng từ khớp sổ cái');
  PERFORM flow.ok((SELECT count(*) FROM ai.gift_surveys) >= 40 AND (SELECT count(*) FROM ai.gift_surveys WHERE purchased_order_id IS NOT NULL) >= 25, '>= 40 khảo sát AI, >= 25 dẫn tới mua hàng');
  PERFORM flow.ok((SELECT count(DISTINCT source) FROM ai.gift_surveys) = 2, 'Có cả khảo sát nguồn LLM và FALLBACK để so sánh');
  PERFORM flow.ok(NOT EXISTS (SELECT 1 FROM catalog.flower_products p WHERE p.status = 'ACTIVE'
                              AND NOT EXISTS (SELECT 1 FROM notify.attachments a WHERE a.owner_id = p.id AND a.phase = 'PRODUCT')), 'Mọi sản phẩm đang bán có ảnh');
  PERFORM flow.ok(NOT EXISTS (SELECT 1 FROM screen.v_admin_queue WHERE so_luong = 0 AND hang_cho <> 'Lệnh hoàn chờ chi'), 'Mỗi hàng chờ admin có >= 1 mục');
  PERFORM flow.ok((SELECT count(*) FROM screen.v_kiosk_catalog) >= 5, 'Kiosk đang bày bán >= 5 bó');
  PERFORM flow.ok((SELECT min(c) FROM (SELECT count(*) c FROM identity.sellers s LEFT JOIN payment.ledger_entries l ON l.seller_id = s.id
                                        WHERE s.status = 'ACTIVE' GROUP BY s.id) x) >= 20, 'Ví mỗi seller có lịch sử (>= 20 bút toán)');
  PERFORM flow.ok((SELECT count(*) FROM payment.withdrawal_requests WHERE status = 'PAID') >= 6, 'Lịch sử rút tiền đã chi >= 6 lần');
  PERFORM flow.ok((SELECT count(*) FROM ordering.disputes WHERE kind = 'COMPLAINT') >= 4, 'Có lịch sử khiếu nại (hoàn tiền và từ chối)');
  PERFORM flow.ok((SELECT count(*) FROM ordering.orders WHERE status = 'REFUNDED') >= 2, 'Có đơn tủ không nhả hàng đã hoàn 100%');
  PERFORM flow.ok((SELECT count(*) FROM ordering.disputes WHERE kind IN ('DEVICE_FAULT','DISPENSE_FAILED') AND status = 'RESOLVED_FIXED') >= 3, 'Có lịch sử sự cố thiết bị đã xử lý');
  PERFORM flow.ok((SELECT count(*) FROM screen.v_expiring_bouquets) >= 1, 'Có bó sắp hết hạn để seller thấy cảnh báo');
  PERFORM flow.ok((SELECT count(DISTINCT batch_id) FROM kiosk_ops.inventory_logs WHERE movement_type = 'STOCK_IN') >= 60, 'Nạp hàng theo lô: >= 60 lô trong 30 ngày');
  PERFORM flow.check_invariants('Cuối 05');
END $$;
\o
\echo '=== HÀNG CHỜ ADMIN ==='
SELECT * FROM screen.v_admin_queue;
\echo '=== DOANH THU 7 NGÀY GẦN NHẤT ==='
SELECT * FROM screen.v_revenue_daily WHERE ngay >= current_date - 7 ORDER BY ngay, kiosk;
\echo '=== DOANH THU NỀN TẢNG (PHÍ GÓI) ==='
SELECT * FROM screen.v_platform_revenue ORDER BY 1;
\echo '=== GÓI THUÊ BAO CỦA SELLER ==='
SELECT s.shop_name, s.status, p.name AS goi, s.package_expires_at, (SELECT count(*) FROM kiosk_ops.slot_assignments a WHERE a.seller_id = s.id AND a.status = 'ACTIVE') AS so_o
FROM identity.sellers s LEFT JOIN identity.subscription_packages p ON p.id = s.package_id ORDER BY 1;
\echo '=== ĐỐI SOÁT NỘI BỘ HÔM QUA ==='
SELECT * FROM flow.reconcile_daily(current_date - 1);
\echo '=== HIỆU QUẢ AI ==='
SELECT k.code, e.source, e.so_khao_sat, e.so_mua, e.ty_le_mua, e.latency_tb_ms FROM screen.v_ai_effectiveness e JOIN kiosk_ops.kiosks k ON k.id = e.kiosk_id ORDER BY 1, 2;
\echo '=== SỐ DƯ SELLER ==='
SELECT s.shop_name, b.pending_balance, b.available_balance, b.debt FROM payment.v_seller_balance b JOIN identity.sellers s ON s.id = b.seller_id ORDER BY 1;
\echo '=== ĐIỂM KHÁCH HÀNG ==='
SELECT full_name, phone, loyalty_points FROM identity.users WHERE role = 'CUSTOMER' ORDER BY loyalty_points DESC;
\echo '=== SỐ ĐƠN THEO TRẠNG THÁI ==='
SELECT status, count(*) FROM ordering.orders GROUP BY 1 ORDER BY 1;
\echo '=== SỐ DÒNG MỖI BẢNG ==='
SELECT table_schema || '.' || table_name AS bang,
       (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I.%I', table_schema, table_name), false, true, '')))[1]::text::int AS so_dong
FROM information_schema.tables WHERE table_type = 'BASE TABLE' AND table_schema IN ('identity','catalog','kiosk_ops','ordering','payment','notify','ai') ORDER BY 1;

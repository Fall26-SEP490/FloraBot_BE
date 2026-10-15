--
-- PostgreSQL database dump
--

\restrict rX5zVB14TI04ajpbz6lflvgQX4fdRw5SkNupG4VXIJFEPnp3Mqvj4UJIv7wXY3Z

-- Dumped from database version 16.15
-- Dumped by pg_dump version 16.15

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

--
-- Name: ai; Type: SCHEMA; Schema: -; Owner: florabot
--

CREATE SCHEMA ai;


ALTER SCHEMA ai OWNER TO florabot;

--
-- Name: cap; Type: SCHEMA; Schema: -; Owner: florabot
--

CREATE SCHEMA cap;


ALTER SCHEMA cap OWNER TO florabot;

--
-- Name: catalog; Type: SCHEMA; Schema: -; Owner: florabot
--

CREATE SCHEMA catalog;


ALTER SCHEMA catalog OWNER TO florabot;

--
-- Name: flow; Type: SCHEMA; Schema: -; Owner: florabot
--

CREATE SCHEMA flow;


ALTER SCHEMA flow OWNER TO florabot;

--
-- Name: identity; Type: SCHEMA; Schema: -; Owner: florabot
--

CREATE SCHEMA identity;


ALTER SCHEMA identity OWNER TO florabot;

--
-- Name: kiosk_ops; Type: SCHEMA; Schema: -; Owner: florabot
--

CREATE SCHEMA kiosk_ops;


ALTER SCHEMA kiosk_ops OWNER TO florabot;

--
-- Name: notify; Type: SCHEMA; Schema: -; Owner: florabot
--

CREATE SCHEMA notify;


ALTER SCHEMA notify OWNER TO florabot;

--
-- Name: ordering; Type: SCHEMA; Schema: -; Owner: florabot
--

CREATE SCHEMA ordering;


ALTER SCHEMA ordering OWNER TO florabot;

--
-- Name: payment; Type: SCHEMA; Schema: -; Owner: florabot
--

CREATE SCHEMA payment;


ALTER SCHEMA payment OWNER TO florabot;

--
-- Name: screen; Type: SCHEMA; Schema: -; Owner: florabot
--

CREATE SCHEMA screen;


ALTER SCHEMA screen OWNER TO florabot;

--
-- Name: btree_gist; Type: EXTENSION; Schema: -; Owner: -
--

CREATE EXTENSION IF NOT EXISTS btree_gist WITH SCHEMA public;


--
-- Name: EXTENSION btree_gist; Type: COMMENT; Schema: -; Owner: 
--

COMMENT ON EXTENSION btree_gist IS 'support for indexing common datatypes in GiST';


--
-- Name: pgcrypto; Type: EXTENSION; Schema: -; Owner: -
--

CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA public;


--
-- Name: EXTENSION pgcrypto; Type: COMMENT; Schema: -; Owner: 
--

COMMENT ON EXTENSION pgcrypto IS 'cryptographic functions';


--
-- Name: add_product_photo(uuid, uuid, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.add_product_photo(p_product uuid, p_user uuid, p_url text) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
BEGIN
  PERFORM flow.check_catalog_editor(p_product, p_user);
  RETURN flow.attach('catalog', 'flower_product', p_product, p_url, 'PRODUCT', p_user);
END $$;


ALTER FUNCTION flow.add_product_photo(p_product uuid, p_user uuid, p_url text) OWNER TO florabot;

--
-- Name: add_seller_user(uuid, text, text, text, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.add_seller_user(p_seller uuid, p_name text, p_email text, p_phone text, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS uuid
    LANGUAGE plpgsql
    AS $_$
DECLARE v uuid; v_now timestamptz := flow.tick(p_now);
BEGIN
  INSERT INTO identity.users (email, phone, password_hash, full_name, role, seller_id)
  VALUES (p_email, p_phone, '$2b$12$demo-' || left(md5(p_email), 8), p_name, 'SELLER', p_seller) RETURNING id INTO v;
  RETURN v;
END $_$;


ALTER FUNCTION flow.add_seller_user(p_seller uuid, p_name text, p_email text, p_phone text, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: admin_close_door(uuid, uuid, text, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.admin_close_door(p_cmd uuid, p_admin uuid, p_note text, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); t kiosk_ops.unlock_tokens;
BEGIN
  SELECT * INTO t FROM kiosk_ops.unlock_tokens WHERE cmd_id = p_cmd;
  IF NOT FOUND OR t.status <> 'OPENED' THEN RAISE EXCEPTION 'Lệnh % không ở trạng thái cửa đang mở', p_cmd; END IF;
  IF (SELECT role FROM identity.users WHERE id = p_admin) <> 'ADMIN' THEN RAISE EXCEPTION 'Chỉ admin/kỹ thuật được xác nhận đóng cửa tay'; END IF;
  PERFORM flow.audit(p_admin, 'DOOR_CLOSED_MANUALLY', 'unlock_tokens', t.id, jsonb_build_object('cmd_id', p_cmd, 'note', p_note));
  PERFORM flow.device_event(p_cmd, 'CLOSED');
END $$;


ALTER FUNCTION flow.admin_close_door(p_cmd uuid, p_admin uuid, p_note text, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: admin_dispose_slot(uuid, uuid, text, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.admin_dispose_slot(p_slot uuid, p_admin uuid, p_note text, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); s kiosk_ops.slots; v_since timestamptz; v_tok uuid; v_log uuid;
BEGIN
  IF (SELECT role FROM identity.users WHERE id = p_admin) <> 'ADMIN' THEN RAISE EXCEPTION 'Chỉ admin/kỹ thuật được thanh lý'; END IF;
  SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot FOR UPDATE;
  IF NOT FOUND OR s.status NOT IN ('PENDING_REMOVAL','PENDING_RELEASE') THEN RAISE EXCEPTION 'Ô % không ở trạng thái chờ trả hàng', p_slot; END IF;
  SELECT max(created_at) INTO v_since FROM notify.audit_logs WHERE entity_type = 'slots' AND entity_id = s.id AND action = 'SLOT_PENDING_RELEASE';
  v_since := coalesce(v_since, (SELECT max(created_at) FROM kiosk_ops.inventory_logs WHERE slot_id = s.id), s.updated_at);
  IF v_now < v_since + flow.cfg_hour('return_deadline_hours') THEN
    RAISE EXCEPTION 'Chưa quá hạn % giờ để seller tự lấy (đến %)', flow.cfg('return_deadline_hours'), v_since + flow.cfg_hour('return_deadline_hours'); END IF;
  UPDATE kiosk_ops.unlock_tokens SET status = 'REVOKED' WHERE slot_id = s.id AND status IN ('ISSUED','SENT','ACKED');
  INSERT INTO kiosk_ops.unlock_tokens (kiosk_id, slot_id, purpose, issued_to_user_id, token_hash, issued_at, expires_at)
  VALUES (s.kiosk_id, s.id, 'SELLER_ACCESS', p_admin, encode(sha256(convert_to(public.uuid_v7()::text, 'UTF8')), 'hex'), v_now, v_now + flow.cfg_min('token_minutes')) RETURNING cmd_id INTO v_tok;
  PERFORM flow.device_event(v_tok, 'SENT'); PERFORM flow.device_event(v_tok, 'ACK'); PERFORM flow.device_event(v_tok, 'OPENED'); PERFORM flow.device_event(v_tok, 'CLOSED');
  INSERT INTO kiosk_ops.inventory_logs (bouquet_id, slot_id, movement_type, performed_by, reason) VALUES (s.bouquet_id, s.id, 'DAMAGE', p_admin, 'Thanh lý: ' || p_note) RETURNING id INTO v_log;
  UPDATE kiosk_ops.bouquets SET status = 'DAMAGED' WHERE id = s.bouquet_id;
  PERFORM flow.release_slot_empty(s.id);
  PERFORM flow.audit(p_admin, 'SLOT_DISPOSED', 'slots', s.id, jsonb_build_object('note', p_note, 'log', v_log));
END $$;


ALTER FUNCTION flow.admin_dispose_slot(p_slot uuid, p_admin uuid, p_note text, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: admin_refund(uuid, uuid, bigint, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.admin_refund(p_order uuid, p_admin uuid, p_amount bigint, p_reason text) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE o ordering.orders;
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  IF o.status <> 'COMPLETED' THEN RAISE EXCEPTION 'Hoàn thủ công chỉ áp dụng cho đơn đã hoàn tất'; END IF;
  PERFORM flow.audit(p_admin, 'ADMIN_REFUND', 'orders', o.id, jsonb_build_object('amount', p_amount, 'reason', p_reason));
  RETURN flow.create_refund(o.id, p_amount, p_reason, p_admin, 'MANUAL');
END $$;


ALTER FUNCTION flow.admin_refund(p_order uuid, p_admin uuid, p_amount bigint, p_reason text) OWNER TO florabot;

--
-- Name: ai_candidates(uuid, bigint, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.ai_candidates(p_kiosk uuid, p_budget bigint, p_occasion text) RETURNS TABLE(bouquet_id uuid, product_id uuid, seller_id uuid, name text, price bigint, tags text[], description text, brand_tone text)
    LANGUAGE sql STABLE
    AS $$
  SELECT b.id, p.id, p.seller_id, p.name, b.price_snapshot, p.tags, p.description, sl.brand_tone
  FROM kiosk_ops.slots s JOIN kiosk_ops.bouquets b ON b.id = s.bouquet_id JOIN catalog.flower_products p ON p.id = b.product_id
  JOIN identity.sellers sl ON sl.id = p.seller_id
  WHERE s.kiosk_id = p_kiosk AND s.status = 'STOCKED' AND b.status = 'STOCKED' AND b.sellable_until > public.app_now() AND sl.status = 'ACTIVE' AND sl.package_expires_at > public.app_now()::date
    AND (p_budget IS NULL OR b.price_snapshot <= p_budget)
    -- luật văn hóa cứng để trong code, không giao LLM: hoa trắng/cúc trắng không gợi ý cho sinh nhật, chúc mừng, khai trương
    AND NOT (p_occasion IN ('BIRTHDAY','CONGRATS','ANNIVERSARY','VALENTINE') AND p.tags && ARRAY['WHITE','SYMPATHY'])
    AND NOT (p_occasion = 'SYMPATHY' AND p.tags && ARRAY['ROMANTIC','FUNNY','RED'])
$$;


ALTER FUNCTION flow.ai_candidates(p_kiosk uuid, p_budget bigint, p_occasion text) OWNER TO florabot;

--
-- Name: ai_mark_purchased(uuid, uuid, uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.ai_mark_purchased(p_survey uuid, p_order uuid, p_product uuid) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
  IF p_order IS NOT NULL AND NOT EXISTS (SELECT 1 FROM ordering.orders WHERE id = p_order) THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  IF p_product IS NOT NULL AND NOT EXISTS (SELECT 1 FROM catalog.flower_products WHERE id = p_product) THEN RAISE EXCEPTION 'Sản phẩm % không tồn tại', p_product; END IF;
  UPDATE ai.gift_surveys SET purchased_order_id = p_order, selected_product_id = p_product WHERE id = p_survey;
END $$;


ALTER FUNCTION flow.ai_mark_purchased(p_survey uuid, p_order uuid, p_product uuid) OWNER TO florabot;

--
-- Name: ai_record_llm(uuid, jsonb, text, integer); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.ai_record_llm(p_survey uuid, p_results jsonb, p_model text, p_latency_ms integer) RETURNS boolean
    LANGUAGE plpgsql
    AS $$
DECLARE s ai.gift_surveys; e jsonb; v_ok boolean := true;
BEGIN
  SELECT * INTO s FROM ai.gift_surveys WHERE id = p_survey FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Khảo sát % không tồn tại', p_survey; END IF;
  IF p_results IS NULL OR jsonb_typeof(p_results) <> 'array' OR jsonb_array_length(p_results) NOT BETWEEN 1 AND 3 THEN v_ok := false; END IF;   -- NULL/không phải mảng: từ chối (giữ FALLBACK)
  IF v_ok AND (SELECT count(DISTINCT x->>'bouquet_id') FROM jsonb_array_elements(p_results) x) <> jsonb_array_length(p_results) THEN v_ok := false; END IF;   -- không lặp bó
  FOR e IN SELECT * FROM jsonb_array_elements(coalesce(p_results, '[]')) LOOP
    IF (e->>'bouquet_id') IS NULL OR NOT EXISTS (SELECT 1 FROM flow.ai_candidates(s.kiosk_id, s.budget_max, s.occasion) c WHERE c.bouquet_id = (e->>'bouquet_id')::uuid)
       OR length(coalesce(e->>'reason','')) > 300 OR length(coalesce(e->>'card_message','')) > 200
       OR coalesce(e->>'reason','') || ' ' || coalesce(e->>'card_message','') ~* '(https?://|www\.|ignore|bỏ qua hướng dẫn|system prompt|<[a-z/])' THEN
      v_ok := false;
    END IF;
  END LOOP;
  IF v_ok THEN
    UPDATE ai.gift_surveys SET results = p_results, model_version = p_model, source = 'LLM', latency_ms = p_latency_ms WHERE id = p_survey;
  ELSE
    PERFORM flow.audit(NULL, 'AI_LLM_RESULT_REJECTED', 'gift_surveys', p_survey, jsonb_build_object('model', p_model));
  END IF;
  RETURN v_ok;
END $$;


ALTER FUNCTION flow.ai_record_llm(p_survey uuid, p_results jsonb, p_model text, p_latency_ms integer) OWNER TO florabot;

--
-- Name: ai_suggest(text, uuid, uuid, text, text, text, text, bigint, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.ai_suggest(p_session text, p_customer uuid, p_kiosk uuid, p_recipient text, p_age text, p_occasion text, p_tone text, p_budget bigint, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); v_tags text[]; v uuid; v_t0 timestamptz := clock_timestamp();
BEGIN
  IF p_kiosk IS NULL THEN RAISE EXCEPTION 'Khảo sát AI phải gắn với một kiosk'; END IF;
  v_tags := ARRAY[p_recipient, p_occasion] || CASE WHEN p_tone IS NULL THEN '{}'::text[] ELSE ARRAY[p_tone] END;
  INSERT INTO ai.gift_surveys (session_id, customer_id, kiosk_id, recipient_type, age_group, occasion, message_tone, budget_max, model_version, source, results, latency_ms)
  SELECT p_session, p_customer, p_kiosk, p_recipient, p_age, p_occasion, p_tone, p_budget, 'rules-v1', 'FALLBACK',
         coalesce(jsonb_agg(jsonb_build_object('product_id', x.product_id, 'bouquet_id', x.bouquet_id, 'rank', x.rn, 'score', x.sc, 'reason', x.description) ORDER BY x.rn), '[]'),
         (extract(epoch FROM clock_timestamp() - v_t0) * 1000)::int
  FROM (SELECT c.*, row_number() OVER (ORDER BY cardinality(ARRAY(SELECT unnest(c.tags) INTERSECT SELECT unnest(v_tags))) DESC, c.price DESC) rn,
               round(cardinality(ARRAY(SELECT unnest(c.tags) INTERSECT SELECT unnest(v_tags)))::numeric / cardinality(v_tags), 3) sc
          FROM flow.ai_candidates(p_kiosk, p_budget, p_occasion) c) x
  WHERE x.rn <= 3
  RETURNING id INTO v;
  RETURN v;
END $$;


ALTER FUNCTION flow.ai_suggest(p_session text, p_customer uuid, p_kiosk uuid, p_recipient text, p_age text, p_occasion text, p_tone text, p_budget bigint, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: approve_refund(uuid, uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.approve_refund(p_refund uuid, p_admin uuid) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE r payment.payments;
BEGIN
  PERFORM flow.require_active_admin(p_admin);
  SELECT * INTO r FROM payment.payments WHERE id=p_refund AND kind='REFUND' FOR UPDATE;
  IF NOT FOUND OR r.status<>'PENDING' THEN RAISE EXCEPTION 'L???nh ho??n kh??ng ??? tr???ng th??i ch???'; END IF;
  IF r.approved_by=p_admin THEN RETURN; END IF;
  IF EXISTS(SELECT 1 FROM identity.users WHERE id=r.approved_by AND role='ADMIN') THEN
    RAISE EXCEPTION 'L???nh ho??n ???? c?? ng?????i duy???t, kh??ng ???????c thay ng?????i duy???t';
  END IF;
  UPDATE payment.payments SET approved_by=p_admin WHERE id=p_refund;
  PERFORM flow.audit(p_admin,'REFUND_APPROVED','payments',p_refund,jsonb_build_object('amount',r.amount));
END $$;


ALTER FUNCTION flow.approve_refund(p_refund uuid, p_admin uuid) OWNER TO florabot;

--
-- Name: approve_seller(uuid, uuid, uuid, text, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.approve_seller(p_seller uuid, p_admin uuid, p_package uuid, p_tone text DEFAULT NULL::text, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now);
BEGIN
  IF (SELECT role FROM identity.users WHERE id = p_admin) <> 'ADMIN' THEN RAISE EXCEPTION 'Chỉ admin được duyệt seller'; END IF;
  IF NOT EXISTS (SELECT 1 FROM identity.subscription_packages WHERE id = p_package AND status = 'ACTIVE') THEN RAISE EXCEPTION 'Gói % không tồn tại hoặc đã ngừng', p_package; END IF;
  UPDATE identity.sellers SET status = 'APPROVED', package_id = p_package, brand_tone = coalesce(p_tone, brand_tone)
   WHERE id = p_seller AND status = 'PENDING';
  IF NOT FOUND THEN RAISE EXCEPTION 'Seller % không ở trạng thái chờ duyệt', p_seller; END IF;
  PERFORM flow.audit(p_admin, 'SELLER_APPROVED', 'sellers', p_seller, jsonb_build_object('package', p_package));
END $$;


ALTER FUNCTION flow.approve_seller(p_seller uuid, p_admin uuid, p_package uuid, p_tone text, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: approve_withdrawal(uuid, uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.approve_withdrawal(p_w uuid, p_admin uuid) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE w payment.withdrawal_requests;
BEGIN
  PERFORM flow.require_active_admin(p_admin);
  SELECT * INTO w FROM payment.withdrawal_requests WHERE id=p_w FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kh??ng t??m th???y y??u c???u r??t ti???n'; END IF;
  IF w.status='APPROVED' AND w.approved_by=p_admin THEN RETURN; END IF;
  IF w.status<>'PENDING' THEN RAISE EXCEPTION 'Y??u c???u r??t kh??ng ??? tr???ng th??i ch??? duy???t'; END IF;
  UPDATE payment.withdrawal_requests SET status='APPROVED',approved_by=p_admin,approved_at=public.app_now() WHERE id=p_w;
  PERFORM flow.audit(p_admin,'WITHDRAWAL_APPROVED','withdrawal_requests',p_w,jsonb_build_object('amount',w.amount));
END $$;


ALTER FUNCTION flow.approve_withdrawal(p_w uuid, p_admin uuid) OWNER TO florabot;

--
-- Name: assign_slot(uuid, uuid, uuid, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.assign_slot(p_slot uuid, p_seller uuid, p_admin uuid, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); v_seller identity.sellers; v_pkg identity.subscription_packages; s kiosk_ops.slots; v_used int; v uuid;
BEGIN
  IF (SELECT role FROM identity.users WHERE id = p_admin) IS DISTINCT FROM 'ADMIN' THEN RAISE EXCEPTION 'Chỉ admin được gán ô'; END IF;
  SELECT * INTO v_seller FROM identity.sellers WHERE id = p_seller;
  IF NOT FOUND THEN RAISE EXCEPTION 'Seller % không tồn tại', p_seller; END IF;
  IF v_seller.status <> 'ACTIVE' OR NOT flow.package_valid(p_seller) THEN
    RAISE EXCEPTION 'Seller % chưa có gói hiệu lực (status %), không gán ô được', v_seller.shop_name, v_seller.status; END IF;
  SELECT * INTO v_pkg FROM identity.subscription_packages WHERE id = v_seller.package_id;
  PERFORM pg_advisory_xact_lock(hashtext('seller:' || p_seller::text));   -- đếm max_slots không bị vượt khi 2 admin gán song song
  SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Ô % không tồn tại', p_slot; END IF;
  IF s.status IN ('FAULT','LOCKED') THEN RAISE EXCEPTION 'Ô % đang bảo trì/khóa, không gán được', s.slot_code; END IF;
  IF s.status <> 'FREE' THEN RAISE EXCEPTION 'Ô % đang do seller khác sử dụng (%)', s.slot_code, s.status; END IF;
  SELECT count(*) INTO v_used FROM kiosk_ops.slot_assignments WHERE seller_id = p_seller AND status = 'ACTIVE';
  IF v_used + 1 > v_pkg.max_slots THEN RAISE EXCEPTION 'Vượt hạn mức % ô của gói %', v_pkg.max_slots, v_pkg.name; END IF;
  INSERT INTO kiosk_ops.slot_assignments (slot_id, seller_id, assigned_by, assigned_at) VALUES (p_slot, p_seller, p_admin, v_now) RETURNING id INTO v;
  UPDATE kiosk_ops.slots SET status = 'RENTED_EMPTY', current_seller_id = p_seller, row_version = row_version + 1 WHERE id = p_slot;
  PERFORM flow.audit(p_admin, 'SLOT_ASSIGNED', 'slots', p_slot, jsonb_build_object('seller', p_seller, 'slot', s.slot_code, 'assignment', v));
  RETURN v;
END $$;


ALTER FUNCTION flow.assign_slot(p_slot uuid, p_seller uuid, p_admin uuid, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: assign_staff_task(uuid, uuid, uuid, text, uuid, uuid, uuid, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.assign_staff_task(p_id uuid, p_actor uuid, p_staff uuid, p_kind text, p_kiosk uuid, p_seller uuid, p_incident uuid, p_instructions text) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  v_actor_role text;
  v_actor_seller uuid;
  existing kiosk_ops.staff_tasks;
  v_staff_role text;
  v_staff_seller uuid;
BEGIN
  -- 1. Validate caller actor
  SELECT role, seller_id INTO v_actor_role, v_actor_seller
  FROM identity.users
  WHERE id = p_actor AND status = 'ACTIVE' FOR SHARE;
  
  IF v_actor_role IS NULL OR v_actor_role NOT IN ('ADMIN', 'OPERATIONS_MANAGER', 'SELLER') THEN
    RAISE EXCEPTION 'Access denied' USING ERRCODE = '42501';
  END IF;

  -- 2. Validate task details
  IF p_kind IS NULL OR p_kind NOT IN ('INCIDENT', 'DELIVERY') OR p_instructions IS NULL OR length(btrim(p_instructions)) NOT BETWEEN 10 AND 2000 THEN
    RAISE EXCEPTION 'Invalid task details' USING ERRCODE = '22023';
  END IF;

  -- 3. Validate caller scope and kind boundaries
  IF p_kind = 'INCIDENT' THEN
    IF v_actor_role = 'SELLER' THEN
      RAISE EXCEPTION 'Seller cannot assign incident tasks' USING ERRCODE = '42501';
    END IF;
    IF v_actor_role = 'OPERATIONS_MANAGER' THEN
      IF NOT EXISTS (SELECT 1 FROM kiosk_ops.manager_kiosks WHERE manager_id = p_actor AND kiosk_id = p_kiosk) THEN
        RAISE EXCEPTION 'Operations manager is not assigned to this kiosk' USING ERRCODE = '42501';
      END IF;
    END IF;
    IF p_seller IS NOT NULL THEN
      RAISE EXCEPTION 'Incident scope mismatch' USING ERRCODE = '22023';
    END IF;
  ELSE -- DELIVERY
    IF v_actor_role = 'OPERATIONS_MANAGER' THEN
      RAISE EXCEPTION 'Operations manager cannot assign delivery tasks' USING ERRCODE = '42501';
    END IF;
    IF v_actor_role = 'SELLER' THEN
      IF p_seller IS DISTINCT FROM v_actor_seller THEN
        RAISE EXCEPTION 'Seller can only assign delivery tasks for own shop' USING ERRCODE = '42501';
      END IF;
    END IF;
    IF p_incident IS NOT NULL THEN
      RAISE EXCEPTION 'Delivery scope mismatch' USING ERRCODE = '22023';
    END IF;
  END IF;

  -- 4. Validate assignee (strictly active TECHNICIAN or SELLER_STAFF of shop; legacy STAFF rejected)
  SELECT role, seller_id INTO v_staff_role, v_staff_seller
  FROM identity.users
  WHERE id = p_staff AND status = 'ACTIVE' FOR SHARE;

  IF v_staff_role IS NULL OR v_staff_role NOT IN ('TECHNICIAN', 'SELLER_STAFF') THEN
    RAISE EXCEPTION 'Active qualified staff required' USING ERRCODE = '22023';
  END IF;

  IF p_kind = 'INCIDENT' AND v_staff_role <> 'TECHNICIAN' THEN
    RAISE EXCEPTION 'Incident task must be assigned to an active technician' USING ERRCODE = '22023';
  END IF;

  IF p_kind = 'DELIVERY' AND (v_staff_role <> 'SELLER_STAFF' OR v_staff_seller IS DISTINCT FROM p_seller) THEN
    RAISE EXCEPTION 'Delivery task must be assigned to an active seller staff of the shop' USING ERRCODE = '22023';
  END IF;

  -- 5. Kiosk availability
  PERFORM 1 FROM kiosk_ops.kiosks WHERE id = p_kiosk AND status <> 'DISABLED' FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk unavailable' USING ERRCODE = '22023'; END IF;

  -- 6. Idempotent replay check (caller scope, assignee qualification, and kiosk verified)
  PERFORM pg_advisory_xact_lock(hashtextextended('staff-task:' || p_id::text, 0));
  SELECT * INTO existing FROM kiosk_ops.staff_tasks WHERE id = p_id;
  IF FOUND THEN
    IF existing.assigned_by <> p_actor OR existing.assignee_id <> p_staff OR existing.kind <> p_kind
       OR existing.kiosk_id <> p_kiosk OR existing.seller_id IS DISTINCT FROM p_seller
       OR existing.incident_id IS DISTINCT FROM p_incident OR existing.instructions IS DISTINCT FROM btrim(p_instructions) THEN
      RAISE EXCEPTION 'Task ID already used' USING ERRCODE = '40001';
    END IF;
    RETURN existing.id;
  END IF;

  -- 7. Domain preconditions
  IF p_kind = 'INCIDENT' THEN
    PERFORM 1 FROM ordering.disputes WHERE id = p_incident AND kiosk_id = p_kiosk AND status = 'OPEN' AND kind IN ('DEVICE_FAULT', 'DISPENSE_FAILED') FOR SHARE;
    IF NOT FOUND THEN
      RAISE EXCEPTION 'Open incident required' USING ERRCODE = '22023';
    END IF;
  ELSE -- DELIVERY
    PERFORM 1 FROM identity.sellers WHERE id = p_seller AND status = 'ACTIVE' FOR SHARE;
    IF NOT FOUND OR NOT EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE kiosk_id = p_kiosk AND current_seller_id = p_seller AND status NOT IN ('FREE', 'PENDING_RELEASE')) THEN
      RAISE EXCEPTION 'Seller does not serve kiosk' USING ERRCODE = '22023';
    END IF;
  END IF;

  INSERT INTO kiosk_ops.staff_tasks(id, kind, assignee_id, assigned_by, kiosk_id, seller_id, incident_id, instructions)
  VALUES (p_id, p_kind, p_staff, p_actor, p_kiosk, p_seller, p_incident, btrim(p_instructions));
  PERFORM flow.audit(p_actor, 'STAFF_TASK_ASSIGNED', 'staff_tasks', p_id, jsonb_build_object('assignee', p_staff, 'kind', p_kind));
  RETURN p_id;
END $$;


ALTER FUNCTION flow.assign_staff_task(p_id uuid, p_actor uuid, p_staff uuid, p_kind text, p_kiosk uuid, p_seller uuid, p_incident uuid, p_instructions text) OWNER TO florabot;

--
-- Name: attach(text, text, uuid, text, text, uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.attach(p_service text, p_owner_type text, p_owner uuid, p_url text, p_phase text, p_user uuid) RETURNS uuid
    LANGUAGE sql
    AS $$
  INSERT INTO notify.attachments (owner_service, owner_type, owner_id, file_url, mime_type, size_bytes, sha256, phase, uploaded_by)
  VALUES (p_service, p_owner_type, p_owner, p_url,
          CASE WHEN p_url ILIKE '%.pdf' THEN 'application/pdf' WHEN p_url ILIKE '%.png' THEN 'image/png' WHEN p_url ILIKE '%.webp' THEN 'image/webp' ELSE 'image/jpeg' END,
          100000 + abs(hashtext(p_url)) % 900000, encode(sha256(convert_to(p_url, 'UTF8')), 'hex'), p_phase, p_user)
  RETURNING id;
$$;


ALTER FUNCTION flow.attach(p_service text, p_owner_type text, p_owner uuid, p_url text, p_phase text, p_user uuid) OWNER TO florabot;

--
-- Name: audit(uuid, text, text, uuid, jsonb, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.audit(p_actor uuid, p_action text, p_entity text, p_id uuid, p_payload jsonb DEFAULT NULL::jsonb, p_actor_type text DEFAULT NULL::text) RETURNS void
    LANGUAGE sql
    AS $$
  INSERT INTO notify.audit_logs (actor_type, actor_id, action, entity_type, entity_id, payload)
  VALUES (coalesce(p_actor_type, CASE WHEN p_actor IS NULL OR p_actor = flow.sys_user() THEN 'SYSTEM' ELSE 'USER' END), p_actor, p_action, p_entity, p_id, p_payload);
$$;


ALTER FUNCTION flow.audit(p_actor uuid, p_action text, p_entity text, p_id uuid, p_payload jsonb, p_actor_type text) OWNER TO florabot;

--
-- Name: cfg(text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.cfg(p_key text) RETURNS numeric
    LANGUAGE plpgsql STABLE
    AS $$
DECLARE v jsonb;
BEGIN
  SELECT value INTO v FROM kiosk_ops.system_settings WHERE key = p_key;
  IF NOT FOUND THEN RAISE EXCEPTION 'Thiếu tham số hệ thống %', p_key; END IF;
  RETURN (v #>> '{}')::numeric;
END $$;


ALTER FUNCTION flow.cfg(p_key text) OWNER TO florabot;

--
-- Name: cfg_hour(text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.cfg_hour(p_key text) RETURNS interval
    LANGUAGE sql STABLE
    AS $$ SELECT make_interval(hours => flow.cfg(p_key)::int) $$;


ALTER FUNCTION flow.cfg_hour(p_key text) OWNER TO florabot;

--
-- Name: cfg_min(text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.cfg_min(p_key text) RETURNS interval
    LANGUAGE sql STABLE
    AS $$ SELECT make_interval(mins => flow.cfg(p_key)::int) $$;


ALTER FUNCTION flow.cfg_min(p_key text) OWNER TO florabot;

--
-- Name: check_catalog_editor(uuid, uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.check_catalog_editor(p_product uuid, p_user uuid) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE owner_id uuid; actor_role text; seller_status text;
BEGIN
  SELECT seller_id INTO owner_id FROM catalog.flower_products WHERE id=p_product FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kh??ng c?? quy???n s???a s???n ph???m %', p_product; END IF;
  PERFORM flow.check_seller_user(p_user, owner_id);
  SELECT role INTO actor_role FROM identity.users WHERE id=p_user;
  IF actor_role='ADMIN' THEN RETURN; END IF;
  IF actor_role <> 'SELLER' THEN RAISE EXCEPTION 'Kh??ng c?? quy???n s???a s???n ph???m %', p_product; END IF;
  SELECT status INTO seller_status FROM identity.sellers WHERE id=owner_id FOR SHARE;
  IF seller_status IS DISTINCT FROM 'ACTIVE' OR NOT flow.package_valid(owner_id) THEN
    RAISE EXCEPTION 'Shop c???n g??i ??ang ho???t ?????ng ????? ch???nh s???a s???n ph???m';
  END IF;
END $$;


ALTER FUNCTION flow.check_catalog_editor(p_product uuid, p_user uuid) OWNER TO florabot;

--
-- Name: check_seller_user(uuid, uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.check_seller_user(p_user uuid, p_seller uuid) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE u identity.users;
BEGIN
  SELECT * INTO u FROM identity.users WHERE id = p_user;
  IF NOT FOUND THEN RAISE EXCEPTION 'Người dùng % không tồn tại', p_user; END IF;
  IF u.status <> 'ACTIVE' THEN RAISE EXCEPTION 'Tài khoản % đang %', u.full_name, u.status; END IF;
  IF u.role <> 'ADMIN' AND u.seller_id IS DISTINCT FROM p_seller THEN RAISE EXCEPTION 'Người dùng % không thuộc seller này', u.full_name; END IF;
END $$;


ALTER FUNCTION flow.check_seller_user(p_user uuid, p_seller uuid) OWNER TO florabot;

--
-- Name: checkout_paid(uuid, text, boolean, bigint, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.checkout_paid(p_checkout uuid, p_txn text, p_success boolean, p_amount bigint, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); p payment.payments; o record; it record; v_ref uuid; v_order uuid; j uuid := public.uuid_v7(); v_online boolean;
BEGIN
  SELECT * INTO p FROM payment.payments WHERE checkout_id = p_checkout AND kind = 'CHARGE' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kh??ng t??m th???y kho???n thu cho checkout %', p_checkout; END IF;
  IF p_amount IS DISTINCT FROM p.amount THEN RAISE EXCEPTION 'S??? ti???n webhook % kh??c s??? ti???n c???n thu %', p_amount, p.amount; END IF;
  IF p.status = 'SUCCEEDED' THEN
    IF p.gateway_txn_id = p_txn THEN RETURN; END IF;   -- webhook g???i l???i l???n 2: b??? qua
    RAISE EXCEPTION 'Checkout % ???? thanh to??n b???ng giao d???ch kh??c %', p_checkout, p.gateway_txn_id;
  END IF;
  IF NOT p_success THEN
    IF p.status = 'PENDING' THEN
      UPDATE payment.payments SET status = 'FAILED', gateway_txn_id = p_txn WHERE id = p.id;
      PERFORM flow.release_checkout(p_checkout, 'CANCELLED');
    END IF;
    RETURN;
  END IF;
  IF p.status='PENDING' AND EXISTS(SELECT 1 FROM ordering.orders expired_order JOIN ordering.web_requests w ON w.order_id=expired_order.id WHERE expired_order.checkout_id=p_checkout AND expired_order.created_at+flow.cfg_min('hold_minutes')<=v_now) THEN
    UPDATE payment.payments SET status='EXPIRED' WHERE id=p.id;
    PERFORM flow.release_checkout(p_checkout,'EXPIRED');
    p.status:='EXPIRED';
  END IF;
  IF p.status IN ('EXPIRED','CANCELLED','FAILED') THEN
    -- ti???n v??? sau khi ????n ???? h???t h???n: ghi nh???n ti???n, l???p l???nh ho??n t??? ?????ng, ????n gi??? nguy??n EXPIRED/CANCELLED
    UPDATE payment.payments SET status = 'SUCCEEDED', gateway_txn_id = p_txn, paid_at = v_now,
           raw_payload = jsonb_build_object('code','00','desc','late','orderCode',p_txn) WHERE id = p.id;
    SELECT id INTO v_order FROM ordering.orders WHERE checkout_id = p_checkout ORDER BY total_amount DESC, id LIMIT 1;
    INSERT INTO payment.payments (kind, purpose, checkout_id, order_id, parent_payment_id, gateway, idempotency_key, amount, status, approved_by, reason)
    VALUES ('REFUND','ORDER_CHECKOUT', p_checkout, v_order, p.id, 'PAYOS', 'late-' || p.id, p.amount, 'PENDING', flow.sys_user(), 'LATE_PAYMENT')
    RETURNING id INTO v_ref;
    INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
     (j, 'GATEWAY_CLEARING', NULL,  p.amount, 'REFUND', v_ref, 'Ti???n v??? mu???n sau khi ????n h???t h???n'),
     (j, 'REFUND_CLEARING',  NULL, -p.amount, 'REFUND', v_ref, 'Ph???i ho??n l???i kh??ch (LATE_PAYMENT)');
    PERFORM flow.audit(NULL, 'LATE_PAYMENT_REFUND_CREATED', 'payments', v_ref, jsonb_build_object('checkout', p_checkout, 'amount', p.amount));
    RETURN;
  END IF;
  UPDATE payment.payments SET status = 'SUCCEEDED', gateway_txn_id = p_txn, paid_at = v_now,
         raw_payload = jsonb_build_object('code','00','desc','success','orderCode',p_txn) WHERE id = p.id;
  FOR o IN SELECT * FROM ordering.orders WHERE checkout_id = p_checkout ORDER BY id FOR UPDATE LOOP
    CONTINUE WHEN o.status <> 'AWAITING_PAYMENT';
    PERFORM flow.set_order_status(o.id, 'PAID', 'SYSTEM');
    PERFORM flow.post_order_ledger(o.id);
    FOR it IN SELECT * FROM ordering.order_items WHERE order_id = o.id AND item_type = 'BOUQUET' AND line_status = 'ACTIVE' LOOP
      UPDATE kiosk_ops.bouquets SET status = 'SOLD' WHERE id = it.bouquet_id;
      INSERT INTO kiosk_ops.inventory_logs (bouquet_id, slot_id, movement_type, order_id) VALUES (it.bouquet_id, it.slot_id, 'SALE_OUT', o.id);
      UPDATE kiosk_ops.slots SET hold_until = v_now + flow.cfg_min('kiosk_pickup_minutes') WHERE id = it.slot_id; -- gi??? t???i khi kh??ch l???y
    END LOOP;
    -- Web purchases wait for an authenticated pickup at the physical kiosk.
    IF EXISTS(SELECT 1 FROM ordering.web_requests WHERE order_id=o.id) THEN
      IF EXISTS(SELECT 1 FROM kiosk_ops.slots WHERE hold_order_id=o.id AND status<>'HELD') THEN
        PERFORM flow.dispense_failed(o.id, '?? g???p s??? c??? trong l??c thanh to??n');
        CONTINUE;
      END IF;
      UPDATE kiosk_ops.slots SET hold_until=(SELECT pickup_before FROM ordering.web_requests WHERE order_id=o.id) WHERE hold_order_id=o.id;
      CONTINUE;
    END IF;
    -- kiosk m???t k???t n???i: v???n ghi nh???n ti???n, kh??ng ph??t token; kh??ch g???i flow.request_pickup khi kiosk online l???i
    SELECT status = 'ONLINE' INTO v_online FROM kiosk_ops.kiosks WHERE id = o.kiosk_id;
    IF NOT EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE hold_order_id = o.id) THEN
      -- ????n ch??? c?? ph??? ki???n (kh??ng gi??? ?? n??o): giao ngay t???i ng??n ph??? ki???n
      PERFORM flow.set_order_status(o.id, 'DISPENSING', 'SYSTEM');
      PERFORM flow.complete_order(o.id, 'KIOSK', '????n ch??? c?? ph??? ki???n: giao ngay');
    ELSIF EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE hold_order_id = o.id AND status <> 'HELD') THEN
      -- ?? ???? v??o FAULT trong l??c kh??ch thanh to??n: kh??ng nh??? ???????c, ho??n 100% ngay
      PERFORM flow.dispense_failed(o.id, '?? g???p s??? c??? trong l??c thanh to??n');
    ELSIF v_online THEN
      PERFORM flow.issue_pickup_tokens(o.id);
      PERFORM flow.set_order_status(o.id, 'DISPENSING', 'SYSTEM');
    END IF;   -- offline: ????n ??? PAID ch??? kiosk online (request_pickup) ho???c expire_pickups ho??n ti???n
  END LOOP;
END $$;


ALTER FUNCTION flow.checkout_paid(p_checkout uuid, p_txn text, p_success boolean, p_amount bigint, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: complete_order(uuid, text, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.complete_order(p_order uuid, p_actor_type text, p_note text DEFAULT NULL::text) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE o ordering.orders;
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order FOR UPDATE;
  IF o.status <> 'DISPENSING' THEN RAISE EXCEPTION 'Chỉ hoàn tất đơn đang nhả hàng (đơn đang %)', o.status; END IF;
  PERFORM flow.set_order_status(o.id, 'COMPLETED', p_actor_type, NULL, p_note);
  IF o.customer_id IS NOT NULL AND o.points_earned > 0 THEN PERFORM flow.credit_points(o.customer_id, o.points_earned, o.id); END IF;
END $$;


ALTER FUNCTION flow.complete_order(p_order uuid, p_actor_type text, p_note text) OWNER TO florabot;

--
-- Name: confirm_refund(uuid, uuid, text, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.confirm_refund(p_refund uuid, p_admin uuid, p_proof_url text, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); r payment.payments; o ordering.orders; j uuid := public.uuid_v7(); v_refunded bigint;
BEGIN
  PERFORM flow.require_active_admin(p_admin);
  SELECT * INTO r FROM payment.payments WHERE id = p_refund AND kind = 'REFUND' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'L???nh ho??n % kh??ng t???n t???i', p_refund; END IF;
  IF r.status <> 'PENDING' THEN RAISE EXCEPTION 'L???nh ho??n kh??ng ??? tr???ng th??i ch??? (%)', r.status; END IF;
  IF NOT EXISTS(SELECT 1 FROM identity.users WHERE id=r.approved_by AND role='ADMIN') THEN
    RAISE EXCEPTION 'L???nh ho??n t??? ?????ng c???n qu???n tr??? vi??n duy???t tr?????c khi chi';
  END IF;
  IF r.approved_by=p_admin THEN RAISE EXCEPTION 'Ng?????i chi ph???i kh??c ng?????i duy???t'; END IF;
  SELECT * INTO o FROM ordering.orders WHERE id = r.order_id FOR UPDATE;
  IF r.purpose = 'ORDER_CHECKOUT' AND o.refund_bank_name IS NULL THEN
    RAISE EXCEPTION '????n % ch??a c?? t??i kho???n nh???n ho??n: kh??ch khai tr??n e-receipt (submit_refund_info) r???i admin m???i chuy???n kho???n ???????c', o.order_code; END IF;
  IF p_proof_url IS NULL OR btrim(p_proof_url)='' THEN RAISE EXCEPTION 'Chi ho??n ph???i k??m ch???ng t??? chuy???n kho???n'; END IF;
  PERFORM pg_advisory_xact_lock(hashtext('seller:' || coalesce(o.seller_id::text, 'none')));
  PERFORM flow.attach('payment', 'refund', r.id, p_proof_url, 'PROOF', p_admin);
  UPDATE payment.payments SET status = 'SUCCEEDED', paid_at = v_now, paid_by = p_admin,
         gateway_txn_id = coalesce(gateway_txn_id, r.gateway || '-RF-' || replace(r.id::text, '-', '')) WHERE id = r.id;
  IF EXISTS (SELECT 1 FROM payment.ledger_entries WHERE ref_type = 'REFUND' AND ref_id = r.id AND account = 'REFUND_CLEARING') THEN
    -- ???? ph??n b??? l??c l???p l???nh (ti???n v??? mu???n / t??? kh??ng nh??? h??ng): ch??? c??n b??t to??n chi ti???n ra
    INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
     (j, 'REFUND_CLEARING',  NULL,  r.amount, 'REFUND_PAYOUT', r.id, '???? chi ho??n cho kh??ch'),
     (j, 'GATEWAY_CLEARING', NULL, -r.amount, 'REFUND_PAYOUT', r.id, 'Chi ho??n qua ' || r.gateway);
  ELSE
    PERFORM flow.refund_allocation(r.id, 'GATEWAY_CLEARING');
  END IF;
  SELECT coalesce(sum(amount), 0) INTO v_refunded FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'SUCCEEDED';
  IF o.status = 'DISPUTED' AND NOT EXISTS (SELECT 1 FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'PENDING') THEN
    -- ch??? ????ng khi???u n???i v?? ?????i so??t khi KH??NG c??n l???nh ho??n n??o ch??? chi (ti???n seller b??? gi??? t???i l??c ????)
    PERFORM flow.set_order_status(o.id, CASE WHEN v_refunded >= o.total_amount THEN 'REFUNDED' ELSE 'COMPLETED' END, 'ADMIN', p_admin, '???? chi ho??n ' || r.amount || '??');
    PERFORM flow.move_pending_to_available(o.id, '?????i so??t sau khi???u n???i');
  ELSIF o.status = 'COMPLETED' AND v_refunded >= o.total_amount THEN
    PERFORM flow.set_order_status(o.id, 'REFUNDED', 'ADMIN', p_admin, '???? chi ho??n 100% sau ?????i so??t');
  ELSIF o.status = 'DISPENSE_FAILED' AND v_refunded >= o.total_amount THEN
    PERFORM flow.set_order_status(o.id, 'REFUNDED', 'ADMIN', p_admin, '???? chi ho??n 100% do t??? kh??ng nh??? h??ng');
    -- ticket DISPENSE_FAILED v???n OPEN cho t???i khi k??? thu???t x??? l?? ?? (resolve_device_fault)
  END IF;
  PERFORM flow.audit(p_admin, 'REFUND_CONFIRMED', 'payments', r.id, jsonb_build_object('amount', r.amount, 'order', o.order_code));
  -- ???? chi h???t l???nh ho??n c???a ????n -> x??a STK kh??ch (ch??? gi??? ????ng th???i gian c???n, N?? 13/2023)
  IF o.id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'PENDING') THEN
    UPDATE ordering.orders SET refund_bank_name = NULL, refund_bank_account_enc = NULL, refund_bank_holder = NULL WHERE id = o.id AND refund_bank_name IS NOT NULL;
    IF FOUND THEN PERFORM flow.audit(NULL, 'REFUND_INFO_CLEARED', 'orders', o.id); END IF;
  END IF;
END $$;


ALTER FUNCTION flow.confirm_refund(p_refund uuid, p_admin uuid, p_proof_url text, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: create_artificial_product(uuid, uuid, uuid, text, text, bigint, text[], numeric, numeric, numeric); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.create_artificial_product(p_id uuid, p_seller uuid, p_actor uuid, p_name text, p_description text, p_price bigint, p_tags text[], p_length_cm numeric, p_width_cm numeric, p_height_cm numeric) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  v_name text := btrim(coalesce(p_name, ''));
  v_tag text;
  v_tags text[] := '{}';
BEGIN
  -- Rejection of null or zero UUIDs
  IF p_id IS NULL OR p_id = '00000000-0000-0000-0000-000000000000'::uuid THEN
    RAISE EXCEPTION 'Mã sản phẩm không hợp lệ' USING ERRCODE = '22023';
  END IF;
  IF p_seller IS NULL OR p_seller = '00000000-0000-0000-0000-000000000000'::uuid
     OR p_actor IS NULL OR p_actor = '00000000-0000-0000-0000-000000000000'::uuid THEN
    RAISE EXCEPTION 'Mã shop hoặc người thao tác không hợp lệ' USING ERRCODE = '22023';
  END IF;

  -- Strict actor eligibility check: actor must be active SELLER belonging to p_seller
  PERFORM 1 FROM identity.users WHERE id = p_actor AND role = 'SELLER' AND seller_id = p_seller AND status = 'ACTIVE' FOR SHARE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Không có quyền thao tác' USING ERRCODE = '42501';
  END IF;

  -- Active seller shop check
  PERFORM 1 FROM identity.sellers WHERE id = p_seller AND status = 'ACTIVE' FOR SHARE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Shop không hoạt động' USING ERRCODE = '42501';
  END IF;

  -- Active subscription package check
  IF NOT flow.package_valid(p_seller) THEN
    RAISE EXCEPTION 'Shop cần gói thuê bao đang hoạt động để tạo sản phẩm' USING ERRCODE = '42501';
  END IF;

  -- Input validations
  IF length(v_name) < 1 OR length(v_name) > 120 THEN
    RAISE EXCEPTION 'Tên sản phẩm từ 1 đến 120 ký tự' USING ERRCODE = '22023';
  END IF;

  IF p_description IS NOT NULL AND length(p_description) > 2000 THEN
    RAISE EXCEPTION 'Mô tả tối đa 2000 ký tự' USING ERRCODE = '22023';
  END IF;

  IF p_price IS NULL OR p_price <= 0 THEN
    RAISE EXCEPTION 'Giá bán phải là số nguyên dương' USING ERRCODE = '22023';
  END IF;

  IF p_length_cm IS NULL OR p_length_cm < 0.01 OR p_length_cm > 1000 OR round(p_length_cm, 2) <> p_length_cm
     OR p_width_cm IS NULL OR p_width_cm < 0.01 OR p_width_cm > 1000 OR round(p_width_cm, 2) <> p_width_cm
     OR p_height_cm IS NULL OR p_height_cm < 0.01 OR p_height_cm > 1000 OR round(p_height_cm, 2) <> p_height_cm THEN
    RAISE EXCEPTION 'Kích thước phải từ 0.01 đến 1000 cm và tối đa 2 chữ số thập phân' USING ERRCODE = '22023';
  END IF;

  IF p_tags IS NULL THEN
    RAISE EXCEPTION 'Danh sách tags không được null' USING ERRCODE = '22023';
  END IF;

  IF cardinality(p_tags) > 20 THEN
    RAISE EXCEPTION 'Tối đa 20 tags' USING ERRCODE = '22023';
  END IF;

  FOREACH v_tag IN ARRAY p_tags LOOP
    IF v_tag IS NULL THEN
      RAISE EXCEPTION 'Tag không được là null' USING ERRCODE = '22023';
    END IF;
    v_tag := btrim(v_tag);
    IF length(v_tag) < 1 OR length(v_tag) > 64 THEN
      RAISE EXCEPTION 'Mỗi tag từ 1 đến 64 ký tự' USING ERRCODE = '22023';
    END IF;
    IF v_tag = ANY(v_tags) THEN
      RAISE EXCEPTION 'Tags không được trùng lặp' USING ERRCODE = '22023';
    END IF;
    v_tags := array_append(v_tags, v_tag);
  END LOOP;

  -- Client-generated ID duplicate conflict check (atomic lock)
  PERFORM pg_advisory_xact_lock(hashtextextended('product:' || p_id::text, 0));
  IF EXISTS (SELECT 1 FROM catalog.flower_products WHERE id = p_id) THEN
    RAISE EXCEPTION 'Mã sản phẩm đã tồn tại' USING ERRCODE = '23505';
  END IF;

  -- Store 48 as positive placeholder for shelf_life_hours
  INSERT INTO catalog.flower_products (
    id, seller_id, name, description, price, tags, shelf_life_hours,
    status, inventory_kind, length_cm, width_cm, height_cm
  ) VALUES (
    p_id, p_seller, v_name, p_description, p_price, v_tags, 48,
    'DRAFT', 'ARTIFICIAL', p_length_cm, p_width_cm, p_height_cm
  );

  PERFORM flow.audit(p_actor, 'PRODUCT_CREATED', 'flower_products', p_id,
    jsonb_build_object('name', v_name, 'price', p_price, 'inventory_kind', 'ARTIFICIAL',
      'dimensions', jsonb_build_object('length', p_length_cm, 'width', p_width_cm, 'height', p_height_cm)));

  RETURN p_id;
END $$;


ALTER FUNCTION flow.create_artificial_product(p_id uuid, p_seller uuid, p_actor uuid, p_name text, p_description text, p_price bigint, p_tags text[], p_length_cm numeric, p_width_cm numeric, p_height_cm numeric) OWNER TO florabot;

--
-- Name: create_refund(uuid, bigint, text, uuid, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.create_refund(p_order uuid, p_amount bigint, p_reason text, p_approver uuid, p_gateway text DEFAULT 'MANUAL'::text) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE o ordering.orders; v_charge uuid; v_done bigint; v uuid;
BEGIN
  IF p_amount IS NULL OR p_amount <= 0 THEN RAISE EXCEPTION 'Số tiền hoàn phải > 0'; END IF;
  SELECT * INTO o FROM ordering.orders WHERE id = p_order;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  SELECT id INTO v_charge FROM payment.payments WHERE checkout_id = o.checkout_id AND kind = 'CHARGE' AND status = 'SUCCEEDED' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % chưa có khoản thu thành công', o.order_code; END IF;
  SELECT coalesce(sum(amount), 0) INTO v_done FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status IN ('PENDING','SUCCEEDED');
  IF v_done + p_amount > o.total_amount THEN
    RAISE EXCEPTION 'Tổng hoàn cho đơn % (% đ) vượt giá trị đơn % đ', o.order_code, v_done + p_amount, o.total_amount; END IF;
  INSERT INTO payment.payments (kind, purpose, checkout_id, order_id, parent_payment_id, gateway, idempotency_key, amount, status, approved_by, reason)
  VALUES ('REFUND','ORDER_CHECKOUT', o.checkout_id, o.id, v_charge, p_gateway, 'refund-' || public.uuid_v7(), p_amount, 'PENDING', p_approver, p_reason)
  RETURNING id INTO v;
  RETURN v;
END $$;


ALTER FUNCTION flow.create_refund(p_order uuid, p_amount bigint, p_reason text, p_approver uuid, p_gateway text) OWNER TO florabot;

--
-- Name: credit_points(uuid, bigint, uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.credit_points(p_customer uuid, p_points bigint, p_order uuid) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
  UPDATE identity.users SET loyalty_points = loyalty_points + p_points WHERE id = p_customer AND role IN ('CUSTOMER','SELLER') AND status = 'ACTIVE';
  IF FOUND THEN PERFORM flow.audit(p_customer, 'POINTS_EARNED', 'orders', p_order, jsonb_build_object('points', p_points));
  ELSE PERFORM flow.audit(p_customer, 'POINTS_SKIPPED_INACTIVE', 'orders', p_order, jsonb_build_object('points', p_points)); END IF;
END $$;


ALTER FUNCTION flow.credit_points(p_customer uuid, p_points bigint, p_order uuid) OWNER TO florabot;

--
-- Name: customer_by_phone(text, text, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.customer_by_phone(p_phone text, p_name text DEFAULT NULL::text, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS uuid
    LANGUAGE plpgsql
    AS $_$
DECLARE u identity.users; v_now timestamptz := flow.tick(p_now);
BEGIN
  IF p_phone IS NULL OR p_phone !~ '^0[35789][0-9]{8}$' THEN
    RAISE EXCEPTION 'Invalid phone' USING ERRCODE='22023';
  END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended('member-phone:'||p_phone,0));
  SELECT * INTO u FROM identity.users WHERE phone=p_phone FOR UPDATE;
  IF FOUND THEN
    IF u.role NOT IN ('CUSTOMER','SELLER') OR u.status<>'ACTIVE' THEN
      RAISE EXCEPTION 'Member unavailable' USING ERRCODE='42501';
    END IF;
    RETURN u.id;
  END IF;
  INSERT INTO identity.users(phone,full_name,role)
  VALUES(p_phone,coalesce(p_name,'Khách '||right(p_phone,4)),'CUSTOMER') RETURNING * INTO u;
  PERFORM flow.audit(u.id,'CUSTOMER_REGISTERED','users',u.id);
  RETURN u.id;
END $_$;


ALTER FUNCTION flow.customer_by_phone(p_phone text, p_name text, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: device_event(uuid, text, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.device_event(p_cmd uuid, p_event text, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); t kiosk_ops.unlock_tokens; v_bq uuid;
BEGIN
  SELECT * INTO t FROM kiosk_ops.unlock_tokens WHERE cmd_id = p_cmd FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Lệnh % không tồn tại', p_cmd; END IF;
  -- token quá hạn: ghi EXPIRED và dừng (không RAISE để trạng thái EXPIRED được lưu)
  IF p_event IN ('SENT','ACK','OPENED') AND t.status IN ('ISSUED','SENT','ACKED') AND t.expires_at < v_now THEN
    UPDATE kiosk_ops.unlock_tokens SET status = 'EXPIRED' WHERE id = t.id;
    PERFORM flow.audit(t.kiosk_id, 'DEVICE_TOKEN_EXPIRED', 'unlock_tokens', t.id, jsonb_build_object('cmd_id', p_cmd, 'event', p_event), 'KIOSK');
    RETURN;
  END IF;
  IF p_event = 'SENT' THEN
    UPDATE kiosk_ops.unlock_tokens SET status = 'SENT', attempts = attempts + 1 WHERE id = t.id AND status IN ('ISSUED','SENT');
  ELSIF p_event = 'ACK' THEN
    UPDATE kiosk_ops.unlock_tokens SET status = 'ACKED' WHERE id = t.id AND status = 'SENT';
  ELSIF p_event = 'OPENED' THEN
    UPDATE kiosk_ops.unlock_tokens SET status = 'OPENED', door_opened_at = v_now WHERE id = t.id AND status = 'ACKED';
    IF NOT FOUND THEN RAISE EXCEPTION 'Cửa mở khi token không ở trạng thái ACKED (status %)', t.status; END IF;
  ELSIF p_event = 'CLOSED' THEN
    UPDATE kiosk_ops.unlock_tokens SET status = 'CLOSED', door_closed_at = v_now WHERE id = t.id AND status = 'OPENED';
    IF NOT FOUND THEN RAISE EXCEPTION 'Đóng cửa khi chưa mở'; END IF;
    IF t.purpose = 'CUSTOMER_PICKUP' THEN
      SELECT bouquet_id INTO v_bq FROM kiosk_ops.slots WHERE id = t.slot_id;
      UPDATE kiosk_ops.bouquets SET status = 'PICKED_UP' WHERE id = v_bq;
      PERFORM flow.release_slot_empty(t.slot_id);
      -- đơn hoàn tất khi không còn ô nào giữ cho đơn (BRD: cảm biến cửa đóng mới hoàn tất)
      IF NOT EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE hold_order_id = t.order_id)
         AND (SELECT status FROM ordering.orders WHERE id = t.order_id) = 'DISPENSING' THEN
        PERFORM flow.complete_order(t.order_id, 'KIOSK', 'Cửa đã đóng, khách đã lấy hàng');
      END IF;
    END IF;
  ELSIF p_event = 'FAILED' THEN
    -- chỉ áp cho token còn sống; đủ số lần thất bại (BRD: 2) thì khóa token và DISPENSE_FAILED
    IF t.status IN ('ISSUED','SENT','ACKED') AND t.attempts >= flow.cfg('door_max_attempts') THEN
      UPDATE kiosk_ops.unlock_tokens SET status = 'FAILED' WHERE id = t.id;
      IF t.purpose = 'CUSTOMER_PICKUP' THEN
        PERFORM flow.dispense_failed(t.order_id, 'Khóa không phản hồi sau ' || t.attempts || ' lần gửi lệnh', t.slot_id);
      ELSE
        PERFORM flow.report_device_fault(t.kiosk_id, t.slot_id, NULL, 'Cửa kỹ thuật không mở sau ' || t.attempts || ' lần', NULL);
      END IF;
    END IF;
  ELSE RAISE EXCEPTION 'Sự kiện thiết bị lạ %', p_event; END IF;
  PERFORM flow.audit(t.kiosk_id, 'DEVICE_' || p_event, 'unlock_tokens', t.id, jsonb_build_object('cmd_id', p_cmd), 'KIOSK');
END $$;


ALTER FUNCTION flow.device_event(p_cmd uuid, p_event text, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: dispense_failed(uuid, text, uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.dispense_failed(p_order uuid, p_reason text, p_slot uuid DEFAULT NULL::uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE o ordering.orders; v_ref uuid; v_d uuid; s kiosk_ops.slots; it record; v_refund bigint; v_lines bigint; v_delivered boolean; v_earn bigint;
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order FOR UPDATE;
  IF o.status NOT IN ('PAID','DISPENSING') THEN RAISE EXCEPTION 'Đơn % không ở trạng thái nhả hàng', o.order_code; END IF;
  IF EXISTS (SELECT 1 FROM kiosk_ops.unlock_tokens WHERE order_id = o.id AND status = 'OPENED') THEN
    RAISE EXCEPTION 'Cửa của đơn % đang mở, đợi cảm biến đóng cửa rồi mới xử lý', o.order_code; END IF;
  PERFORM pg_advisory_xact_lock(hashtext('seller:' || o.seller_id::text));
  -- phần chưa giao: bó chưa PICKED_UP; phụ kiện coi là đã giao nếu đã có ít nhất một ô mở
  v_delivered := EXISTS (SELECT 1 FROM ordering.order_items oi JOIN kiosk_ops.bouquets b ON b.id = oi.bouquet_id WHERE oi.order_id = o.id AND b.status = 'PICKED_UP');
  SELECT coalesce(sum(oi.line_total), 0) INTO v_refund FROM ordering.order_items oi LEFT JOIN kiosk_ops.bouquets b ON b.id = oi.bouquet_id
   WHERE oi.order_id = o.id AND oi.line_status = 'ACTIVE' AND ((oi.item_type = 'BOUQUET' AND b.status <> 'PICKED_UP') OR (oi.item_type = 'ACCESSORY' AND NOT v_delivered));
  v_lines := v_refund;                            -- giá trị hàng chưa giao: seller chịu đúng phần này
  v_refund := least(v_refund, o.total_amount);   -- khách nhận lại tối đa số tiền đã trả
  IF v_refund <= 0 THEN RAISE EXCEPTION 'Đơn % đã giao đủ hàng, không có gì để hoàn', o.order_code; END IF;
  v_ref := flow.create_refund(o.id, v_refund, 'AUTO_REFUND: ' || p_reason, flow.sys_user(), 'MANUAL');
  PERFORM flow.refund_allocation(v_ref, 'REFUND_CLEARING', v_lines);
  INSERT INTO ordering.disputes (kind, order_id, kiosk_id, slot_id, reason) VALUES ('DISPENSE_FAILED', o.id, o.kiosk_id, p_slot, p_reason) RETURNING id INTO v_d;
  -- các ô đang giữ cho đơn: ô lỗi -> FAULT (giữ bó để kỹ thuật kiểm), ô khác -> PENDING_REMOVAL (bó đã bán, chờ seller lấy về)
  FOR s IN SELECT * FROM kiosk_ops.slots WHERE hold_order_id = o.id ORDER BY id FOR UPDATE LOOP
    UPDATE kiosk_ops.unlock_tokens SET status = 'REVOKED' WHERE slot_id = s.id AND status IN ('ISSUED','SENT','ACKED');
    IF s.id = p_slot OR s.status = 'FAULT' THEN
      UPDATE kiosk_ops.slots SET status = 'FAULT', hold_until = NULL, hold_order_id = NULL, row_version = row_version + 1 WHERE id = s.id;
    ELSE
      UPDATE kiosk_ops.slots SET status = 'PENDING_REMOVAL', hold_until = NULL, hold_order_id = NULL, row_version = row_version + 1 WHERE id = s.id;
    END IF;
  END LOOP;
  FOR it IN SELECT oi.* FROM ordering.order_items oi LEFT JOIN kiosk_ops.bouquets b ON b.id = oi.bouquet_id
            WHERE oi.order_id = o.id AND oi.line_status = 'ACTIVE' AND ((oi.item_type = 'BOUQUET' AND b.status <> 'PICKED_UP') OR (oi.item_type = 'ACCESSORY' AND NOT v_delivered)) LOOP
    IF it.item_type = 'ACCESSORY' THEN UPDATE kiosk_ops.accessories SET stock_quantity = stock_quantity + it.quantity WHERE id = it.accessory_id; END IF;
    UPDATE ordering.order_items SET line_status = 'REFUNDED' WHERE id = it.id;
  END LOOP;
  IF v_delivered THEN
    -- giao được một phần: đơn hoàn tất, phần thiếu hoàn tiền (bút toán điều chỉnh); điểm tích trên phần đã trả thật, điểm đổi trả lại phần nền tảng thu hồi
    v_earn := CASE WHEN o.customer_id IS NULL THEN 0 ELSE floor((o.total_amount - v_refund) * flow.cfg('points_rate_percent') / 100) END;
    UPDATE ordering.orders SET points_earned = v_earn WHERE id = o.id;
    PERFORM flow.set_order_status(o.id, 'COMPLETED', 'SYSTEM', NULL, 'Giao thiếu, hoàn ' || v_refund || 'đ: ' || p_reason);
    IF v_earn > 0 THEN PERFORM flow.credit_points(o.customer_id, v_earn, o.id); END IF;
    IF v_lines - v_refund > 0 THEN PERFORM flow.restore_points(o.customer_id, least(v_lines - v_refund, o.points_redeemed), o.id); END IF;
  ELSE
    IF o.points_redeemed > 0 THEN PERFORM flow.restore_points(o.customer_id, o.points_redeemed, o.id); END IF;
    PERFORM flow.set_order_status(o.id, 'DISPENSE_FAILED', 'SYSTEM', NULL, p_reason);
  END IF;
  PERFORM flow.audit(NULL, 'DISPENSE_FAILED', 'orders', o.id, jsonb_build_object('reason', p_reason, 'refund', v_ref, 'refund_amount', v_refund, 'dispute', v_d, 'partial', v_delivered));
  RETURN v_d;
END $$;


ALTER FUNCTION flow.dispense_failed(p_order uuid, p_reason text, p_slot uuid) OWNER TO florabot;

--
-- Name: expire_bouquets(timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.expire_bouquets(p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); b record; n int := 0;
BEGIN
  FOR b IN SELECT bq.*, s.id AS slot_id FROM kiosk_ops.bouquets bq JOIN kiosk_ops.slots s ON s.bouquet_id = bq.id
           WHERE bq.status = 'STOCKED' AND s.status = 'STOCKED' AND bq.sellable_until <= v_now ORDER BY s.id FOR UPDATE OF bq, s LOOP
    UPDATE kiosk_ops.bouquets SET status = 'EXPIRED' WHERE id = b.id;
    UPDATE kiosk_ops.slots SET status = 'PENDING_REMOVAL', row_version = row_version + 1 WHERE id = b.slot_id;
    INSERT INTO kiosk_ops.inventory_logs (bouquet_id, slot_id, movement_type, reason) VALUES (b.id, b.slot_id, 'EXPIRE_OUT', 'Quá hạn bán');
    n := n + 1;
  END LOOP;
  RETURN n;
END $$;


ALTER FUNCTION flow.expire_bouquets(p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: expire_pending_payments(timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.expire_pending_payments(p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); c record; n int := 0; v uuid;
BEGIN
  FOR c IN SELECT DISTINCT o.checkout_id FROM ordering.orders o
           LEFT JOIN kiosk_ops.slots s ON s.hold_order_id = o.id AND s.status = 'HELD'
           WHERE o.status = 'AWAITING_PAYMENT' AND coalesce(s.hold_until, o.created_at + flow.cfg_min('hold_minutes')) < v_now
  LOOP
    -- chỉ thắng khi payment còn PENDING: webhook đã ghi SUCCEEDED trước thì bỏ qua (chống race với checkout_paid)
    UPDATE payment.payments SET status = 'EXPIRED' WHERE checkout_id = c.checkout_id AND kind = 'CHARGE' AND status = 'PENDING' RETURNING id INTO v;
    CONTINUE WHEN NOT FOUND;
    PERFORM flow.release_checkout(c.checkout_id, 'EXPIRED');
    n := n + 1;
  END LOOP;
  RETURN n;
END $$;


ALTER FUNCTION flow.expire_pending_payments(p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: expire_pending_subscriptions(timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.expire_pending_subscriptions(p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); r record; n int := 0;
BEGIN
  FOR r IN SELECT * FROM identity.subscriptions WHERE status = 'PENDING_PAYMENT' AND created_at + flow.cfg_hour('subscription_payment_hours') < v_now ORDER BY id FOR UPDATE LOOP
    UPDATE payment.payments SET status = 'EXPIRED' WHERE id = r.payment_id AND status = 'PENDING';
    CONTINUE WHEN NOT FOUND;   -- webhook đã ghi SUCCEEDED: subscription_paid sẽ kích hoạt, không hủy
    UPDATE identity.subscriptions SET status = 'CANCELLED' WHERE id = r.id;
    PERFORM flow.audit(NULL, 'SUBSCRIPTION_EXPIRED_UNPAID', 'subscriptions', r.id);
    n := n + 1;
  END LOOP;
  RETURN n;
END $$;


ALTER FUNCTION flow.expire_pending_subscriptions(p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: expire_pickups(timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.expire_pickups(p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); o record; n int := 0; v_k text;
BEGIN
  FOR o IN SELECT od.* FROM ordering.orders od WHERE od.status IN ('PAID','DISPENSING')
             AND EXISTS (SELECT 1 FROM kiosk_ops.slots s WHERE s.hold_order_id = od.id AND s.status = 'HELD' AND s.hold_until < v_now)
             AND NOT EXISTS (SELECT 1 FROM kiosk_ops.unlock_tokens t WHERE t.order_id = od.id AND t.status = 'OPENED')   -- cửa đang mở: đợi đóng
           ORDER BY od.id FOR UPDATE LOOP
    SELECT status INTO v_k FROM kiosk_ops.kiosks WHERE id = o.kiosk_id;
    PERFORM flow.dispense_failed(o.id, CASE WHEN v_k <> 'ONLINE' THEN 'Kiosk không trực tuyến trong thời gian nhận hàng' ELSE 'Hết thời gian giữ hàng, khách không mở hộc' END);
    n := n + 1;
  END LOOP;
  RETURN n;
END $$;


ALTER FUNCTION flow.expire_pickups(p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: expire_tokens(timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.expire_tokens(p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); n int;
BEGIN
  UPDATE kiosk_ops.unlock_tokens SET status = 'EXPIRED' WHERE status IN ('ISSUED','SENT','ACKED') AND expires_at < v_now;
  GET DIAGNOSTICS n = ROW_COUNT;
  RETURN n;
END $$;


ALTER FUNCTION flow.expire_tokens(p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: expire_web_orders(); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.expire_web_orders() RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE w record; n int:=0;
BEGIN
  FOR w IN SELECT r.id FROM ordering.web_requests r JOIN ordering.orders o ON o.id=r.order_id WHERE r.kind='CUSTOM' AND r.state='ORDERED' AND o.status='PAID' AND r.pickup_before<public.app_now() AND NOT EXISTS(SELECT 1 FROM ordering.order_items i WHERE i.order_id=o.id) LOOP
    PERFORM flow.web_cancel(w.id,flow.sys_user(),'Shop chua nap hoa truoc han nhan'); n:=n+1;
  END LOOP;
  RETURN n;
END $$;


ALTER FUNCTION flow.expire_web_orders() OWNER TO florabot;

--
-- Name: forget_customer(uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.forget_customer(p_customer uuid) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
  UPDATE identity.users SET phone = NULL, email = 'deleted-' || id || '@florabot.invalid', full_name = 'Khách đã xóa', status = 'DISABLED', loyalty_points = 0
   WHERE id = p_customer AND role = 'CUSTOMER';
  IF NOT FOUND THEN RAISE EXCEPTION 'Khách % không tồn tại', p_customer; END IF;
  PERFORM flow.audit(p_customer, 'CUSTOMER_FORGOTTEN', 'users', p_customer);
END $$;


ALTER FUNCTION flow.forget_customer(p_customer uuid) OWNER TO florabot;

--
-- Name: issue_pickup_tokens(uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.issue_pickup_tokens(p_order uuid) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE s record; v_cmd uuid; v_first uuid; v_now timestamptz := public.app_now();
BEGIN
  FOR s IN SELECT id, kiosk_id FROM kiosk_ops.slots WHERE hold_order_id = p_order AND status = 'HELD' ORDER BY id FOR UPDATE LOOP
    UPDATE kiosk_ops.unlock_tokens SET status = 'REVOKED' WHERE slot_id = s.id AND status IN ('ISSUED','SENT','ACKED');
    INSERT INTO kiosk_ops.unlock_tokens (kiosk_id, slot_id, purpose, order_id, token_hash, issued_at, expires_at)
    VALUES (s.kiosk_id, s.id, 'CUSTOMER_PICKUP', p_order, encode(sha256(convert_to(public.uuid_v7()::text, 'UTF8')), 'hex'), v_now, v_now + flow.cfg_min('token_minutes'))
    RETURNING cmd_id INTO v_cmd;
    v_first := coalesce(v_first, v_cmd);
  END LOOP;
  RETURN v_first;
END $$;


ALTER FUNCTION flow.issue_pickup_tokens(p_order uuid) OWNER TO florabot;

--
-- Name: kiosk_checkout(uuid, uuid[], uuid, jsonb, bigint, text, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.kiosk_checkout(p_kiosk uuid, p_bouquets uuid[], p_customer uuid DEFAULT NULL::uuid, p_accessories jsonb DEFAULT '[]'::jsonb, p_points bigint DEFAULT 0, p_ecard text DEFAULT NULL::text, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); v_checkout uuid := public.uuid_v7(); r record; v_order uuid; v_total bigint;
        v_kstatus text; v_target uuid; v_sub bigint; v_sellers uuid[]; v_accs jsonb; v_cap bigint;
BEGIN
  p_bouquets := coalesce(p_bouquets, '{}'); p_points := coalesce(p_points, 0);
  SELECT status INTO v_kstatus FROM kiosk_ops.kiosks WHERE id = p_kiosk;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk % kh??ng t???n t???i', p_kiosk; END IF;
  IF v_kstatus IS DISTINCT FROM 'ONLINE' THEN RAISE EXCEPTION 'Kiosk kh??ng ho???t ?????ng (%)', v_kstatus; END IF;
  IF cardinality(p_bouquets) = 0 AND jsonb_array_length(coalesce(p_accessories, '[]')) = 0 THEN RAISE EXCEPTION 'Gi??? h??ng tr???ng'; END IF;
  IF p_ecard IS NOT NULL AND length(p_ecard) > 150 THEN RAISE EXCEPTION 'L???i thi???p t???i ??a 150 k?? t???'; END IF;
  IF p_customer IS NOT NULL AND NOT EXISTS (SELECT 1 FROM identity.users WHERE id = p_customer AND role IN ('CUSTOMER','SELLER') AND status = 'ACTIVE') THEN
    RAISE EXCEPTION 'T??i kho???n kh??ch % kh??ng h???p l??? ho???c ???? b??? x??a', p_customer; END IF;
  -- kh??a ?? v?? b?? theo th??? t??? id ????? tr??nh deadlock khi 2 kh??ch c??ng b???m
  FOR r IN SELECT b.id AS bouquet_id, b.status AS bstatus, b.sellable_until, s.id AS slot_id, s.kiosk_id, s.status AS sstatus, s.current_seller_id, p.name, sl.status AS seller_status
           FROM kiosk_ops.bouquets b JOIN kiosk_ops.slots s ON s.bouquet_id = b.id JOIN catalog.flower_products p ON p.id = b.product_id
           JOIN identity.sellers sl ON sl.id = b.seller_id
           WHERE b.id = ANY(p_bouquets) ORDER BY s.id FOR UPDATE OF s, b
  LOOP
    IF r.kiosk_id <> p_kiosk OR r.sstatus <> 'STOCKED' OR r.bstatus <> 'STOCKED' THEN RAISE EXCEPTION 'B?? % kh??ng c??n s???n ????? b??n', r.name; END IF;
    IF r.sellable_until <= v_now THEN RAISE EXCEPTION 'B?? % ???? qu?? h???n b??n', r.name; END IF;
    IF r.seller_status <> 'ACTIVE' THEN RAISE EXCEPTION 'Seller c???a b?? % ??ang % (t???m ng??ng b??n)', r.name, r.seller_status; END IF;
    IF NOT flow.slot_sellable(r.slot_id, r.current_seller_id) THEN RAISE EXCEPTION 'G??i c???a seller b??n b?? % ???? h???t h???n, kh??ng b??n', r.name; END IF;
  END LOOP;
  IF (SELECT count(*) FROM kiosk_ops.slots WHERE bouquet_id = ANY(p_bouquets) AND status = 'STOCKED') <> cardinality(p_bouquets) THEN
    RAISE EXCEPTION 'C?? b?? kh??ng c??n trong ??'; END IF;
  v_accs := flow.take_accessories(p_kiosk, p_accessories);

  SELECT array_agg(DISTINCT x ORDER BY x) INTO v_sellers FROM (
    SELECT seller_id AS x FROM kiosk_ops.bouquets WHERE id = ANY(p_bouquets)
    UNION SELECT (e->>'seller_id')::uuid FROM jsonb_array_elements(v_accs) e) z;
  FOR r IN SELECT unnest(v_sellers) AS seller_id LOOP
    v_sub := coalesce((SELECT sum(price_snapshot) FROM kiosk_ops.bouquets WHERE id = ANY(p_bouquets) AND seller_id = r.seller_id), 0)
           + coalesce((SELECT sum((e->>'price')::bigint * (e->>'qty')::int) FROM jsonb_array_elements(v_accs) e WHERE (e->>'seller_id')::uuid = r.seller_id), 0);
    INSERT INTO ordering.orders (order_code, checkout_id, customer_id, seller_id, kiosk_id, subtotal, total_amount, tracking_token, ecard_content)
    VALUES (flow.new_order_code(), v_checkout, p_customer, r.seller_id, p_kiosk, v_sub, v_sub, flow.new_tracking_token(), p_ecard)
    RETURNING id INTO v_order;
    INSERT INTO ordering.order_items (order_id, item_type, bouquet_id, slot_id, name_snapshot, quantity, unit_price, line_total)
    SELECT v_order, 'BOUQUET', b.id, s.id, p.name, 1, b.price_snapshot, b.price_snapshot
      FROM kiosk_ops.bouquets b JOIN kiosk_ops.slots s ON s.bouquet_id = b.id JOIN catalog.flower_products p ON p.id = b.product_id
     WHERE b.id = ANY(p_bouquets) AND b.seller_id = r.seller_id;
    INSERT INTO ordering.order_items (order_id, item_type, accessory_id, name_snapshot, quantity, unit_price, line_total)
    SELECT v_order, 'ACCESSORY', (e->>'id')::uuid, e->>'name', (e->>'qty')::int, (e->>'price')::bigint, (e->>'qty')::int * (e->>'price')::bigint
      FROM jsonb_array_elements(v_accs) e WHERE (e->>'seller_id')::uuid = r.seller_id;
    PERFORM flow.audit(p_kiosk, 'ORDER_STATUS_CHANGED', 'orders', v_order, jsonb_build_object('from', NULL, 'to', 'AWAITING_PAYMENT'), 'KIOSK');
  END LOOP;

  -- ?????i ??i???m: ch??? kh??ch ???? ????ng nh???p; tr??? ngay (tr??? l???i n???u ????n h???t h???n/h???y); n???n t???ng ch???u ph???n gi???m; ??p cho ????n l???n nh???t
  IF p_points > 0 THEN
    IF p_customer IS NULL THEN RAISE EXCEPTION '?????i ??i???m c???n ????ng nh???p'; END IF;
    SELECT id, subtotal INTO v_target, v_sub FROM ordering.orders WHERE checkout_id = v_checkout ORDER BY subtotal DESC, id LIMIT 1;
    v_cap := floor(v_sub * flow.cfg('points_max_redeem_percent') / 100);
    IF p_points > v_cap THEN RAISE EXCEPTION '?????i ??i???m t???i ??a % ?????ng (%%% gi?? tr??? h??ng)', v_cap, flow.cfg('points_max_redeem_percent'); END IF;
    UPDATE ordering.orders SET discount_amount = p_points, points_redeemed = p_points, total_amount = subtotal - p_points WHERE id = v_target;
    PERFORM flow.redeem_points(p_customer, p_points, v_target);   -- l???nh sang identity service (PointsRedeemed)
  END IF;
  -- ??i???m s??? ???????c c???ng khi ????n COMPLETED
  UPDATE ordering.orders SET points_earned = CASE WHEN p_customer IS NULL THEN 0 ELSE floor(total_amount * flow.cfg('points_rate_percent') / 100) END
   WHERE checkout_id = v_checkout;

  -- gi??? ?? trong l??c kh??ch qu??t m?? thanh to??n (BRD: TTL 7 ph??t)
  FOR r IN SELECT oi.slot_id, oi.bouquet_id, oi.order_id FROM ordering.order_items oi JOIN ordering.orders o ON o.id = oi.order_id
           WHERE o.checkout_id = v_checkout AND oi.item_type = 'BOUQUET'
  LOOP
    UPDATE kiosk_ops.slots SET status = 'HELD', hold_until = v_now + flow.cfg_min('hold_minutes'), hold_order_id = r.order_id, row_version = row_version + 1 WHERE id = r.slot_id;
    UPDATE kiosk_ops.bouquets SET status = 'HELD' WHERE id = r.bouquet_id;
    INSERT INTO kiosk_ops.inventory_logs (bouquet_id, slot_id, movement_type, order_id, reason) VALUES (r.bouquet_id, r.slot_id, 'HOLD', r.order_id, 'Gi??? ch??? thanh to??n');
  END LOOP;
  SELECT sum(total_amount) INTO v_total FROM ordering.orders WHERE checkout_id = v_checkout;
  IF v_total <= 0 THEN RAISE EXCEPTION '????n 0 ?????ng kh??ng t???o ???????c kho???n thu'; END IF;
  INSERT INTO payment.payments (kind, purpose, checkout_id, gateway, idempotency_key, amount)
  VALUES ('CHARGE','ORDER_CHECKOUT', v_checkout, 'PAYOS', 'co-' || v_checkout, v_total);
  RETURN v_checkout;
END $$;


ALTER FUNCTION flow.kiosk_checkout(p_kiosk uuid, p_bouquets uuid[], p_customer uuid, p_accessories jsonb, p_points bigint, p_ecard text, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: kiosk_heartbeat(uuid, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.kiosk_heartbeat(p_kiosk uuid, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now);
BEGIN
  UPDATE kiosk_ops.kiosks SET last_heartbeat_at = v_now, status = CASE WHEN status = 'OFFLINE' THEN 'ONLINE' ELSE status END WHERE id = p_kiosk;
END $$;


ALTER FUNCTION flow.kiosk_heartbeat(p_kiosk uuid, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: mark_offline_kiosks(timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.mark_offline_kiosks(p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); n int;
BEGIN
  UPDATE kiosk_ops.kiosks SET status = 'OFFLINE' WHERE status = 'ONLINE' AND coalesce(last_heartbeat_at, created_at) < v_now - make_interval(secs => flow.cfg('heartbeat_offline_seconds'));
  GET DIAGNOSTICS n = ROW_COUNT;
  RETURN n;
END $$;


ALTER FUNCTION flow.mark_offline_kiosks(p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: move_pending_to_available(uuid, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.move_pending_to_available(p_order uuid, p_memo text) RETURNS bigint
    LANGUAGE plpgsql
    AS $$
DECLARE o ordering.orders; v bigint; j uuid := public.uuid_v7();
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order;
  IF o.status NOT IN ('COMPLETED','REFUNDED') THEN RAISE EXCEPTION 'Chỉ đối soát đơn đã giao xong (đơn % đang %)', o.order_code, o.status; END IF;
  v := flow.order_seller_pending(p_order);
  IF v > 0 THEN
    INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
     (j,'SELLER_PENDING',  o.seller_id,  v,'SETTLEMENT',o.id, p_memo || ' ' || o.order_code),
     (j,'SELLER_AVAILABLE',o.seller_id, -v,'SETTLEMENT',o.id, p_memo || ' ' || o.order_code);
  END IF;
  RETURN v;
END $$;


ALTER FUNCTION flow.move_pending_to_available(p_order uuid, p_memo text) OWNER TO florabot;

--
-- Name: new_batch(); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.new_batch() RETURNS uuid
    LANGUAGE sql
    AS $$ SELECT public.uuid_v7() $$;


ALTER FUNCTION flow.new_batch() OWNER TO florabot;

--
-- Name: new_order_code(); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.new_order_code() RETURNS text
    LANGUAGE sql
    AS $$
  SELECT 'FB' || to_char(public.app_now(), 'YYMMDD') || '-' || upper(substr(md5(random()::text || clock_timestamp()::text), 1, 6)) $$;


ALTER FUNCTION flow.new_order_code() OWNER TO florabot;

--
-- Name: new_tracking_token(); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.new_tracking_token() RETURNS text
    LANGUAGE plpgsql
    AS $$
DECLARE a text := 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'; b bytea := gen_random_bytes(8); r text := ''; i int;
BEGIN
  FOR i IN 0..7 LOOP r := r || substr(a, 1 + get_byte(b, i) % 32, 1); END LOOP;
  RETURN r;
END $$;


ALTER FUNCTION flow.new_tracking_token() OWNER TO florabot;

--
-- Name: open_dispute(uuid, text, text, text, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.open_dispute(p_order uuid, p_tracking text, p_reason text, p_photo text, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); o ordering.orders; v_d uuid;
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  IF coalesce(p_tracking, '') = '' OR o.tracking_token <> p_tracking THEN RAISE EXCEPTION 'Mã e-receipt không đúng: chỉ người giữ biên nhận mới được khiếu nại'; END IF;
  IF o.status <> 'COMPLETED' THEN RAISE EXCEPTION 'Chỉ khiếu nại đơn đã nhận hàng (đơn đang %); tủ không mở được thì hệ thống đã tự hoàn', o.status; END IF;
  IF EXISTS (SELECT 1 FROM ordering.disputes WHERE order_id = o.id AND kind = 'COMPLAINT') THEN RAISE EXCEPTION 'Đơn đã có khiếu nại (mỗi đơn chỉ khiếu nại 1 lần)'; END IF;
  IF v_now > o.completed_at + flow.cfg_hour('dispute_window_hours') THEN
    RAISE EXCEPTION 'Quá % giờ kể từ lúc nhận hàng, hết hạn khiếu nại', flow.cfg('dispute_window_hours'); END IF;
  IF p_photo IS NULL THEN RAISE EXCEPTION 'Khiếu nại bắt buộc kèm ảnh'; END IF;
  INSERT INTO ordering.disputes (kind, order_id, kiosk_id, reason, reported_by) VALUES ('COMPLAINT', o.id, o.kiosk_id, p_reason, o.customer_id) RETURNING id INTO v_d;
  PERFORM flow.attach('ordering', 'dispute', v_d, p_photo, 'EVIDENCE', o.customer_id);
  PERFORM flow.set_order_status(o.id, 'DISPUTED', 'CUSTOMER', o.customer_id, p_reason);
  RETURN v_d;
END $$;


ALTER FUNCTION flow.open_dispute(p_order uuid, p_tracking text, p_reason text, p_photo text, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: open_member_shop(uuid, text, text, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.open_member_shop(p_user uuid, p_shop text, p_phone text, p_address text) RETURNS uuid
    LANGUAGE plpgsql
    AS $_$
DECLARE u identity.users; v uuid;
BEGIN
  SELECT * INTO u FROM identity.users WHERE id=p_user FOR UPDATE;
  IF NOT FOUND OR u.status<>'ACTIVE' OR u.role NOT IN ('CUSTOMER','SELLER') OR u.password_hash IS NULL THEN
    RAISE EXCEPTION 'Active member account required' USING ERRCODE='42501';
  END IF;
  IF p_shop IS NULL OR length(btrim(p_shop)) NOT BETWEEN 2 AND 120
     OR p_phone IS NULL OR p_phone !~ '^(0[35789][0-9]{8}|\+84[35789][0-9]{8})$'
     OR p_address IS NULL OR length(btrim(p_address)) NOT BETWEEN 1 AND 200 THEN
    RAISE EXCEPTION 'Invalid shop details' USING ERRCODE='22023';
  END IF;
  IF u.seller_id IS NOT NULL THEN RETURN u.seller_id; END IF;
  INSERT INTO identity.sellers(shop_name,phone,address)
    VALUES(btrim(p_shop),p_phone,btrim(p_address)) RETURNING id INTO v;
  UPDATE identity.users SET role='SELLER',seller_id=v WHERE id=u.id;
  PERFORM flow.audit(u.id,'MEMBER_SHOP_OPENED','sellers',v,jsonb_build_object('shop',btrim(p_shop)));
  RETURN v;
END $_$;


ALTER FUNCTION flow.open_member_shop(p_user uuid, p_shop text, p_phone text, p_address text) OWNER TO florabot;

--
-- Name: order_history(uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.order_history(p_order uuid) RETURNS TABLE(at timestamp with time zone, from_status text, to_status text, note text)
    LANGUAGE sql STABLE
    AS $$
  SELECT created_at, payload->>'from', payload->>'to', payload->>'note' FROM notify.audit_logs
   WHERE entity_type = 'orders' AND entity_id = p_order AND action = 'ORDER_STATUS_CHANGED' ORDER BY id $$;


ALTER FUNCTION flow.order_history(p_order uuid) OWNER TO florabot;

--
-- Name: order_seller_pending(uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.order_seller_pending(p_order uuid) RETURNS bigint
    LANGUAGE sql STABLE
    AS $$
  SELECT coalesce(-sum(amount), 0)::bigint FROM payment.ledger_entries
   WHERE account = 'SELLER_PENDING'
     AND (ref_id = p_order OR ref_id IN (SELECT id FROM payment.payments WHERE order_id = p_order AND kind = 'REFUND')) $$;


ALTER FUNCTION flow.order_seller_pending(p_order uuid) OWNER TO florabot;

--
-- Name: package_valid(uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.package_valid(p_seller uuid) RETURNS boolean
    LANGUAGE sql STABLE
    AS $$
  SELECT EXISTS (SELECT 1 FROM identity.sellers WHERE id = p_seller AND package_expires_at IS NOT NULL AND package_expires_at > public.app_now()::date) $$;


ALTER FUNCTION flow.package_valid(p_seller uuid) OWNER TO florabot;

--
-- Name: pay_withdrawal(uuid, uuid, text, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.pay_withdrawal(p_w uuid, p_admin uuid, p_proof_url text, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); w payment.withdrawal_requests; v_avail bigint; j uuid := public.uuid_v7();
BEGIN
  PERFORM flow.require_active_admin(p_admin);
  SELECT * INTO w FROM payment.withdrawal_requests WHERE id = p_w FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Y??u c???u r??t % kh??ng t???n t???i', p_w; END IF;
  IF w.status <> 'APPROVED' THEN RAISE EXCEPTION 'Y??u c???u r??t kh??ng ??? tr???ng th??i ch???'; END IF;
  IF w.approved_by=p_admin THEN RAISE EXCEPTION 'Ng?????i chi ph???i kh??c ng?????i duy???t'; END IF;
  IF p_proof_url IS NULL OR btrim(p_proof_url)='' THEN RAISE EXCEPTION 'Chi ti???n ph???i k??m ch???ng t??? chuy???n kho???n'; END IF;
  PERFORM pg_advisory_xact_lock(hashtext('seller:' || w.seller_id::text));
  SELECT available_balance INTO v_avail FROM payment.v_seller_balance WHERE seller_id = w.seller_id;
  v_avail := coalesce(v_avail, 0);
  IF v_avail <= 0 OR v_avail < w.amount THEN RAISE EXCEPTION 'S??? d?? kh??? d???ng kh??ng ????? t???i th???i ??i???m chi (% < %)', v_avail, w.amount; END IF;
  PERFORM flow.attach('payment', 'withdrawal', w.id, p_proof_url, 'PROOF', p_admin);
  UPDATE payment.withdrawal_requests SET status = 'PAID', paid_by = p_admin, paid_at = v_now WHERE id = w.id;
  INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
   (j,'SELLER_AVAILABLE',w.seller_id, w.amount,'WITHDRAWAL',w.id,'Chi tr??? seller'),
   (j,'GATEWAY_CLEARING',NULL,-w.amount,'WITHDRAWAL',w.id,'Chuy???n kho???n ra ng??n h??ng');
  PERFORM flow.audit(p_admin, 'WITHDRAWAL_PAID', 'withdrawal_requests', w.id, jsonb_build_object('amount', w.amount));
END $$;


ALTER FUNCTION flow.pay_withdrawal(p_w uuid, p_admin uuid, p_proof_url text, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: post_order_ledger(uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.post_order_ledger(p_order uuid) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE o ordering.orders; j uuid := public.uuid_v7();
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  IF o.status IN ('AWAITING_PAYMENT','EXPIRED','CANCELLED') OR NOT EXISTS (SELECT 1 FROM payment.payments WHERE checkout_id = o.checkout_id AND kind = 'CHARGE' AND status = 'SUCCEEDED') THEN
    RAISE EXCEPTION 'Chỉ ghi sổ đơn đã thu tiền thành công (đơn % đang %)', o.order_code, o.status; END IF;
  BEGIN
    INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo)
    SELECT j, a, s, amt, 'ORDER', o.id, m FROM (VALUES
      ('GATEWAY_CLEARING', NULL::uuid, o.total_amount, 'Tiền khách trả'),
      ('PLATFORM_LOYALTY', NULL::uuid, o.discount_amount, 'Nền tảng chịu phần đổi điểm'),
      ('SELLER_PENDING',   o.seller_id, -o.subtotal, 'Phần seller, chờ đối soát')
    ) v(a, s, amt, m) WHERE amt <> 0;
  EXCEPTION WHEN unique_violation THEN
    RETURN;   -- đã ghi sổ trước đó (sự kiện lặp): coi là đã xử lý
  END;
END $$;


ALTER FUNCTION flow.post_order_ledger(p_order uuid) OWNER TO florabot;

--
-- Name: provision_portal_user(uuid, text, text, boolean); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.provision_portal_user(p_user uuid, p_email text, p_hash text, p_allow_demo boolean DEFAULT false) RETURNS void
    LANGUAGE plpgsql
    AS $_$
DECLARE u identity.users; v_email text := lower(trim(p_email));
BEGIN
  IF v_email IS NULL OR length(v_email) > 254 OR v_email !~ '^[^[:space:]@]+@[^[:space:]@]+$' THEN
    RAISE EXCEPTION 'Email kh??ng h???p l???';
  END IF;
  IF p_hash IS NULL OR p_hash !~ '^\$2[aby]\$12\$[./A-Za-z0-9]{53}$' THEN
    RAISE EXCEPTION 'M???t kh???u ph???i ???????c b??m bcrypt v???i cost 12';
  END IF;
  PERFORM pg_advisory_xact_lock(hashtext('provision-email:' || v_email));
  SELECT * INTO u FROM identity.users WHERE id=p_user FOR UPDATE;
  IF NOT FOUND OR u.role NOT IN ('ADMIN','SELLER') OR u.status <> 'ACTIVE' THEN
    RAISE EXCEPTION 'T??i kho???n kh??ng ????? ??i???u ki???n c???p m???t kh???u';
  END IF;
  IF u.password_hash IS DISTINCT FROM '!unprovisioned' AND NOT (
    p_allow_demo IS TRUE AND u.password_hash IN ('$2b$12$demoadmin','$2b$12$demoops','$2b$12$demoseller1',
      '$2b$12$demoseller2','$2b$12$demostaff','$2b$12$demostaff1','$2b$12$demotech','$2b$12$demostaff2')) THEN
    RAISE EXCEPTION 'T??i kho???n ???? c?? m???t kh???u ho???c kh??ng ph???i t??i kho???n ch??? c???p';
  END IF;
  IF u.email IS NOT NULL AND lower(trim(u.email)) <> v_email THEN
    RAISE EXCEPTION 'Email kh??ng kh???p t??i kho???n ???? ch???n';
  END IF;
  IF EXISTS(SELECT 1 FROM identity.users WHERE lower(trim(email))=v_email AND id<>p_user) THEN
    RAISE EXCEPTION 'Email ???? ???????c d??ng cho t??i kho???n kh??c';
  END IF;
  UPDATE identity.users SET email=v_email,password_hash=p_hash WHERE id=p_user;
  PERFORM flow.audit(NULL,'PORTAL_USER_PROVISIONED','users',p_user,jsonb_build_object('role',u.role,'channel','operator_cli'));
END $_$;


ALTER FUNCTION flow.provision_portal_user(p_user uuid, p_email text, p_hash text, p_allow_demo boolean) OWNER TO florabot;

--
-- Name: reassign_staff_task(uuid, uuid, uuid, integer, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.reassign_staff_task(p_id uuid, p_actor uuid, p_staff uuid, p_version integer, p_reason text) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  t kiosk_ops.staff_tasks;
  v_actor_role text;
  v_actor_seller uuid;
  v_staff_role text;
  v_staff_seller uuid;
BEGIN
  SELECT role, seller_id INTO v_actor_role, v_actor_seller
  FROM identity.users
  WHERE id = p_actor AND status = 'ACTIVE' FOR SHARE;
  
  IF v_actor_role IS NULL OR v_actor_role NOT IN ('ADMIN', 'OPERATIONS_MANAGER', 'SELLER') THEN
    RAISE EXCEPTION 'Access denied' USING ERRCODE = '42501';
  END IF;

  SELECT * INTO t FROM kiosk_ops.staff_tasks WHERE id = p_id FOR UPDATE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
  END IF;

  -- Scope verification
  IF v_actor_role = 'OPERATIONS_MANAGER' THEN
    IF t.kind <> 'INCIDENT' OR NOT EXISTS (SELECT 1 FROM kiosk_ops.manager_kiosks WHERE manager_id = p_actor AND kiosk_id = t.kiosk_id) THEN
      RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
    END IF;
  ELSIF v_actor_role = 'SELLER' THEN
    IF t.kind <> 'DELIVERY' OR t.seller_id IS DISTINCT FROM v_actor_seller THEN
      RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
    END IF;
  END IF;

  IF p_version IS DISTINCT FROM t.version THEN
    RAISE EXCEPTION 'Task changed; reload first' USING ERRCODE = '40001';
  END IF;

  IF t.status NOT IN ('ASSIGNED', 'IN_PROGRESS') THEN
    RAISE EXCEPTION 'Review or closed task cannot be reassigned' USING ERRCODE = '22023';
  END IF;

  IF p_staff IS NULL OR p_staff = t.assignee_id OR p_reason IS NULL OR length(btrim(p_reason)) NOT BETWEEN 10 AND 2000 THEN
    RAISE EXCEPTION 'Different staff and handoff reason required' USING ERRCODE = '22023';
  END IF;

  SELECT role, seller_id INTO v_staff_role, v_staff_seller
  FROM identity.users
  WHERE id = p_staff AND status = 'ACTIVE' FOR SHARE;

  IF v_staff_role IS NULL THEN
    RAISE EXCEPTION 'Active staff required' USING ERRCODE = '22023';
  END IF;

  IF t.kind = 'INCIDENT' THEN
    IF v_staff_role <> 'TECHNICIAN' THEN
      RAISE EXCEPTION 'Incident task must be reassigned to an active technician' USING ERRCODE = '22023';
    END IF;
  ELSE -- DELIVERY
    IF v_staff_role <> 'SELLER_STAFF' OR v_staff_seller IS DISTINCT FROM t.seller_id THEN
      RAISE EXCEPTION 'Delivery task must be reassigned to an active seller staff of the same shop' USING ERRCODE = '22023';
    END IF;
  END IF;

  UPDATE kiosk_ops.staff_tasks
  SET assignee_id = p_staff, status = 'ASSIGNED', report = btrim(p_reason),
      version = version + 1, updated_at = public.app_now()
  WHERE id = p_id;

  PERFORM flow.audit(p_actor, 'STAFF_TASK_REASSIGNED', 'staff_tasks', p_id,
    jsonb_build_object('previousAssignee', t.assignee_id, 'assignee', p_staff, 'from', t.status, 'to', 'ASSIGNED', 'report', btrim(p_reason), 'version', t.version + 1));

  RETURN t.version + 1;
END $$;


ALTER FUNCTION flow.reassign_staff_task(p_id uuid, p_actor uuid, p_staff uuid, p_version integer, p_reason text) OWNER TO florabot;

--
-- Name: reconcile_daily(date); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.reconcile_daily(p_date date) RETURNS TABLE(muc text, chung_tu bigint, so_cai bigint, lech bigint, ghi_chu text)
    LANGUAGE sql STABLE
    AS $$
  WITH d AS (SELECT p_date AS d),
  l AS (SELECT * FROM payment.ledger_entries WHERE (created_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date = p_date),
  p AS (SELECT * FROM payment.payments WHERE (paid_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date = p_date AND status = 'SUCCEEDED'),
  w AS (SELECT * FROM payment.withdrawal_requests WHERE (paid_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date = p_date AND status = 'PAID')
  SELECT 'Tiền vào VietQR (đơn + phí gói)', coalesce((SELECT sum(amount) FROM p WHERE kind = 'CHARGE'), 0),
         coalesce((SELECT sum(amount) FROM l WHERE account = 'GATEWAY_CLEARING' AND amount > 0), 0),
         coalesce((SELECT sum(amount) FROM p WHERE kind = 'CHARGE'), 0) - coalesce((SELECT sum(amount) FROM l WHERE account = 'GATEWAY_CLEARING' AND amount > 0), 0),
         'Khoản thu SUCCEEDED trong ngày phải = nợ GATEWAY_CLEARING trong ngày'
  UNION ALL
  SELECT 'Chi hoàn khách/seller (chuyển khoản tay)', coalesce((SELECT sum(amount) FROM p WHERE kind = 'REFUND'), 0),
         coalesce((SELECT -sum(amount) FROM l WHERE account = 'GATEWAY_CLEARING' AND amount < 0 AND ref_type IN ('REFUND','REFUND_PAYOUT')), 0),
         coalesce((SELECT sum(amount) FROM p WHERE kind = 'REFUND'), 0) - coalesce((SELECT -sum(amount) FROM l WHERE account = 'GATEWAY_CLEARING' AND amount < 0 AND ref_type IN ('REFUND','REFUND_PAYOUT')), 0),
         'Lệnh hoàn đã chi trong ngày phải = có GATEWAY_CLEARING (REFUND/REFUND_PAYOUT) trong ngày'
  UNION ALL
  SELECT 'Chi rút tiền seller', coalesce((SELECT sum(amount) FROM w), 0),
         coalesce((SELECT -sum(amount) FROM l WHERE account = 'GATEWAY_CLEARING' AND ref_type = 'WITHDRAWAL'), 0),
         coalesce((SELECT sum(amount) FROM w), 0) - coalesce((SELECT -sum(amount) FROM l WHERE account = 'GATEWAY_CLEARING' AND ref_type = 'WITHDRAWAL'), 0),
         'Rút tiền PAID trong ngày phải = có GATEWAY_CLEARING (WITHDRAWAL) trong ngày'
  UNION ALL
  SELECT 'Sổ cái cân (tổng mọi bút toán)', 0, coalesce((SELECT sum(amount) FROM payment.ledger_entries), 0), coalesce((SELECT sum(amount) FROM payment.ledger_entries), 0), 'Phải = 0'
  UNION ALL
  SELECT 'Lệnh hoàn chờ chi quá 48 giờ (số lệnh)', (SELECT count(*) FROM payment.payments WHERE kind = 'REFUND' AND status = 'PENDING' AND created_at < p_date::timestamptz - interval '48 hours'),
         0, 0, 'Cảnh báo SLA hoàn tiền: khách chưa khai STK hoặc admin chưa chi'
  UNION ALL
  SELECT 'Tiền đang chờ hoàn (REFUND_CLEARING)', 0, coalesce((SELECT -sum(amount) FROM payment.ledger_entries WHERE account = 'REFUND_CLEARING'), 0), 0,
         'Phải = tổng lệnh hoàn PENDING đã phân bổ; về 0 khi chi hết'
$$;


ALTER FUNCTION flow.reconcile_daily(p_date date) OWNER TO florabot;

--
-- Name: reconcile_gateway(date, jsonb, uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.reconcile_gateway(p_date date, p_statement jsonb, p_admin uuid) RETURNS TABLE(loai text, gateway_txn_id text, he_thong bigint, sao_ke bigint, ghi_chu text)
    LANGUAGE plpgsql
    AS $$
DECLARE n_match int; n_diff int;
BEGIN
  IF jsonb_typeof(p_statement) <> 'array' THEN RAISE EXCEPTION 'Sao k?? ph???i l?? m???ng JSON [{txn, amount}]'; END IF;
  CREATE TEMP TABLE IF NOT EXISTS _stmt (txn text PRIMARY KEY, amount bigint) ON COMMIT DROP;
  DELETE FROM _stmt;
  INSERT INTO _stmt SELECT e->>'txn', sum((e->>'amount')::bigint)::bigint FROM jsonb_array_elements(p_statement) e GROUP BY e->>'txn';
  CREATE TEMP TABLE IF NOT EXISTS _reconcile_db (txn text, amount bigint) ON COMMIT DROP;
  DELETE FROM _reconcile_db;
  INSERT INTO _reconcile_db SELECT p.gateway_txn_id, p.amount FROM payment.payments p
    WHERE p.kind='CHARGE' AND p.gateway='PAYOS' AND p.status='SUCCEEDED'
      AND (p.paid_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date=p_date;
  RETURN QUERY
  WITH db AS (SELECT txn, amount FROM _reconcile_db)
  SELECT 'MISSING_IN_DB', st.txn, NULL::bigint, st.amount, 'Ng??n h??ng c?? ti???n v??o nh??ng h??? th???ng kh??ng c?? kho???n thu (webhook r???t?) -> tra so??t, t???o kho???n thu tay ho???c ho??n'
    FROM _stmt st LEFT JOIN db ON db.txn = st.txn WHERE db.txn IS NULL
  UNION ALL
  SELECT 'MISSING_IN_STATEMENT', db.txn, db.amount, NULL::bigint, 'H??? th???ng ghi SUCCEEDED nh??ng sao k?? kh??ng c?? -> webhook gi???/ghi nh???m, kh??a ????n, tra so??t ngay'
    FROM db LEFT JOIN _stmt st ON st.txn = db.txn WHERE st.txn IS NULL
  UNION ALL
  SELECT 'AMOUNT_MISMATCH', db.txn, db.amount, st.amount, 'S??? ti???n l???ch -> tra so??t'
    FROM db JOIN _stmt st ON st.txn = db.txn WHERE st.amount <> db.amount;
  SELECT count(*) INTO n_match FROM _stmt st JOIN _reconcile_db db ON db.txn=st.txn AND db.amount=st.amount;
  SELECT count(*) INTO n_diff FROM _stmt st FULL JOIN _reconcile_db db ON db.txn=st.txn WHERE st.txn IS NULL OR db.amount IS NULL OR st.amount<>db.amount;
  PERFORM flow.audit(p_admin, 'RECONCILED', 'payments', NULL, jsonb_build_object('date', p_date, 'matched', n_match, 'diff', n_diff));
END $$;


ALTER FUNCTION flow.reconcile_gateway(p_date date, p_statement jsonb, p_admin uuid) OWNER TO florabot;

--
-- Name: redeem_points(uuid, bigint, uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.redeem_points(p_customer uuid, p_points bigint, p_order uuid) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE v_bal bigint;
BEGIN
  SELECT loyalty_points INTO v_bal FROM identity.users WHERE id = p_customer AND role IN ('CUSTOMER','SELLER') FOR UPDATE;
  IF v_bal IS NULL THEN RAISE EXCEPTION 'T??i kho???n kh??ch % kh??ng h???p l???', p_customer; END IF;
  IF p_points > v_bal THEN RAISE EXCEPTION 'Ch??? c??n % ??i???m, kh??ng ????? %', v_bal, p_points; END IF;
  UPDATE identity.users SET loyalty_points = loyalty_points - p_points WHERE id = p_customer;
  PERFORM flow.audit(p_customer, 'POINTS_REDEEMED', 'orders', p_order, jsonb_build_object('points', p_points));
END $$;


ALTER FUNCTION flow.redeem_points(p_customer uuid, p_points bigint, p_order uuid) OWNER TO florabot;

--
-- Name: refresh_seller_package(uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.refresh_seller_package(p_seller uuid) RETURNS text
    LANGUAGE plpgsql
    AS $$
DECLARE v_today date := public.app_now()::date; v_seller identity.sellers; v_exp date; v_next date; v_new text;
BEGIN
  SELECT * INTO v_seller FROM identity.sellers WHERE id = p_seller FOR UPDATE;
  IF v_seller.status NOT IN ('APPROVED','ACTIVE','PAST_DUE') THEN RETURN v_seller.status; END IF;
  SELECT max(period_to) INTO v_exp FROM identity.subscriptions WHERE seller_id = p_seller AND status = 'ACTIVE' AND period_from <= v_today AND v_today < period_to;
  IF v_exp IS NOT NULL THEN
    LOOP   -- nối các kỳ đã trả liên tục
      SELECT max(period_to) INTO v_next FROM identity.subscriptions WHERE seller_id = p_seller AND status = 'ACTIVE' AND period_from = v_exp;
      EXIT WHEN v_next IS NULL; v_exp := v_next;
    END LOOP;
    v_new := 'ACTIVE';
  ELSE
    v_exp := v_seller.package_expires_at;
    v_new := CASE WHEN v_seller.status = 'APPROVED' THEN 'APPROVED' ELSE 'PAST_DUE' END;
  END IF;
  UPDATE identity.sellers SET status = v_new, package_expires_at = v_exp WHERE id = p_seller;
  IF v_new <> v_seller.status THEN
    PERFORM flow.audit(NULL, CASE v_new WHEN 'ACTIVE' THEN CASE v_seller.status WHEN 'PAST_DUE' THEN 'SELLER_REACTIVATED' ELSE 'SELLER_ACTIVATED' END ELSE 'SELLER_PAST_DUE' END,
                       'sellers', p_seller, jsonb_build_object('expires', v_exp, 'from', v_seller.status));
  END IF;
  RETURN v_new;
END $$;


ALTER FUNCTION flow.refresh_seller_package(p_seller uuid) OWNER TO florabot;

--
-- Name: refund_allocation(uuid, text, bigint); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.refund_allocation(p_refund uuid, p_credit_account text, p_seller_share bigint DEFAULT NULL::bigint) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE r payment.payments; o ordering.orders; v_sp bigint; v_loy bigint; v_pend bigint; v_from_pend bigint; j uuid := public.uuid_v7();
BEGIN
  SELECT * INTO r FROM payment.payments WHERE id = p_refund;
  SELECT * INTO o FROM ordering.orders WHERE id = r.order_id;
  IF o.total_amount = 0 THEN RAISE EXCEPTION 'Đơn 0 đồng không hoàn được'; END IF;
  v_sp  := coalesce(p_seller_share, round(r.amount::numeric * o.subtotal / o.total_amount));   -- seller chịu (có thể > r.amount khi đơn có đổi điểm)
  IF v_sp < r.amount OR v_sp > o.subtotal THEN RAISE EXCEPTION 'Phần seller chịu % không hợp lệ (hoàn %, hàng %)', v_sp, r.amount, o.subtotal; END IF;
  v_loy := v_sp - r.amount;                                          -- nền tảng được hoàn lại phần đổi điểm đã chịu
  v_pend := flow.order_seller_pending(o.id);
  v_from_pend := least(greatest(v_pend, 0), v_sp);
  INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo)
  SELECT j, a, s, amt, 'REFUND', r.id, m FROM (VALUES
    ('SELLER_PENDING',   o.seller_id, v_from_pend,        'Seller chịu hoàn (trừ tiền chờ đối soát)'),
    ('SELLER_AVAILABLE', o.seller_id, v_sp - v_from_pend, 'Seller chịu hoàn (trừ ví khả dụng, có thể thành công nợ)'),
    ('PLATFORM_LOYALTY', NULL::uuid,  -v_loy,             'Hoàn lại phần đổi điểm nền tảng đã chịu'),
    (p_credit_account,   NULL::uuid,  -r.amount,          'Chi hoàn ' || coalesce(r.reason, ''))
  ) v(a, s, amt, m) WHERE amt <> 0;
END $$;


ALTER FUNCTION flow.refund_allocation(p_refund uuid, p_credit_account text, p_seller_share bigint) OWNER TO florabot;

--
-- Name: register_seller(text, text, text, text, text, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.register_seller(p_shop text, p_phone text, p_address text, p_owner_name text, p_owner_email text, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS uuid
    LANGUAGE plpgsql
    AS $_$
DECLARE v uuid; v_now timestamptz := flow.tick(p_now);
BEGIN
  IF length(trim(p_shop)) NOT BETWEEN 2 AND 120 OR p_phone !~ '^(0[35789][0-9]{8}|\+84[35789][0-9]{8})$' THEN
    RAISE EXCEPTION 'T??n shop ho???c s??? ??i???n tho???i kh??ng h???p l???';
  END IF;
  INSERT INTO identity.sellers (shop_name, phone, address) VALUES (trim(p_shop), p_phone, p_address) RETURNING id INTO v;
  INSERT INTO identity.users (email, phone, password_hash, full_name, role, seller_id)
  VALUES (p_owner_email, p_phone, '!unprovisioned', coalesce(nullif(trim(p_owner_name), ''), trim(p_shop)), 'SELLER', v);
  PERFORM flow.audit(NULL, 'SELLER_REGISTERED', 'sellers', v, jsonb_build_object('shop', p_shop));
  RETURN v;
END $_$;


ALTER FUNCTION flow.register_seller(p_shop text, p_phone text, p_address text, p_owner_name text, p_owner_email text, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: reject_withdrawal(uuid, uuid, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.reject_withdrawal(p_w uuid, p_admin uuid, p_reason text) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
  UPDATE payment.withdrawal_requests SET status = 'REJECTED', reject_reason = p_reason, approved_by = NULL WHERE id = p_w AND status IN ('PENDING','APPROVED');
  IF NOT FOUND THEN RAISE EXCEPTION 'Yêu cầu rút % không ở trạng thái chờ', p_w; END IF;
  PERFORM flow.audit(p_admin, 'WITHDRAWAL_REJECTED', 'withdrawal_requests', p_w, jsonb_build_object('reason', p_reason));
END $$;


ALTER FUNCTION flow.reject_withdrawal(p_w uuid, p_admin uuid, p_reason text) OWNER TO florabot;

--
-- Name: release_checkout(uuid, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.release_checkout(p_checkout uuid, p_to text) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE it record; orec record;
BEGIN
  FOR orec IN SELECT * FROM ordering.orders WHERE checkout_id = p_checkout ORDER BY id FOR UPDATE LOOP
    CONTINUE WHEN orec.status <> 'AWAITING_PAYMENT';
    FOR it IN SELECT * FROM ordering.order_items WHERE order_id = orec.id AND line_status = 'ACTIVE' LOOP
      IF it.item_type = 'BOUQUET' THEN
        UPDATE kiosk_ops.slots SET status = CASE WHEN flow.slot_sellable(id, current_seller_id) THEN 'STOCKED' ELSE 'PENDING_RELEASE' END,
               hold_until = NULL, hold_order_id = NULL, row_version = row_version + 1
         WHERE id = it.slot_id AND status = 'HELD' AND hold_order_id = orec.id;
        UPDATE kiosk_ops.bouquets SET status = 'STOCKED' WHERE id = it.bouquet_id AND status = 'HELD';
        INSERT INTO kiosk_ops.inventory_logs (bouquet_id, slot_id, movement_type, order_id, reason) VALUES (it.bouquet_id, it.slot_id, 'RELEASE', orec.id, 'Thanh toán không thành công');
      ELSIF it.item_type = 'ACCESSORY' THEN
        UPDATE kiosk_ops.accessories SET stock_quantity = stock_quantity + it.quantity WHERE id = it.accessory_id;
      END IF;
      UPDATE ordering.order_items SET line_status = 'CANCELLED' WHERE id = it.id;
    END LOOP;
    IF orec.points_redeemed > 0 THEN PERFORM flow.restore_points(orec.customer_id, orec.points_redeemed, orec.id); END IF;
    PERFORM flow.set_order_status(orec.id, p_to, 'SYSTEM');
  END LOOP;
END $$;


ALTER FUNCTION flow.release_checkout(p_checkout uuid, p_to text) OWNER TO florabot;

--
-- Name: release_seller_slots(uuid, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.release_seller_slots(p_seller uuid, p_reason text) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE a record; s kiosk_ops.slots; n int := 0;
BEGIN
  FOR a IN SELECT * FROM kiosk_ops.slot_assignments WHERE seller_id = p_seller AND status = 'ACTIVE' ORDER BY slot_id FOR UPDATE LOOP
    SELECT * INTO s FROM kiosk_ops.slots WHERE id = a.slot_id FOR UPDATE;
    CONTINUE WHEN s.status = 'HELD';   -- khách đang thanh toán: đợi lượt sau
    UPDATE kiosk_ops.slot_assignments SET status = 'RELEASED', released_at = public.app_now(), release_reason = p_reason WHERE id = a.id;
    IF s.status = 'RENTED_EMPTY' THEN
      UPDATE kiosk_ops.slots SET status = 'FREE', current_seller_id = NULL, row_version = row_version + 1 WHERE id = s.id;
      PERFORM flow.audit(NULL, 'SLOT_RELEASED', 'slots', s.id, jsonb_build_object('seller', p_seller, 'reason', p_reason));
    ELSIF s.status = 'STOCKED' THEN
      UPDATE kiosk_ops.slots SET status = 'PENDING_RELEASE', row_version = row_version + 1 WHERE id = s.id;
      PERFORM flow.audit(NULL, 'SLOT_PENDING_RELEASE', 'slots', s.id, jsonb_build_object('seller', p_seller, 'reason', p_reason));
    END IF;   -- FAULT/LOCKED/PENDING_REMOVAL: giữ nguyên, release_slot_empty sẽ trả FREE khi ô trống
    n := n + 1;
  END LOOP;
  RETURN n;
END $$;


ALTER FUNCTION flow.release_seller_slots(p_seller uuid, p_reason text) OWNER TO florabot;

--
-- Name: release_slot(uuid, uuid, text, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.release_slot(p_slot uuid, p_actor uuid, p_reason text, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); s kiosk_ops.slots; a kiosk_ops.slot_assignments; v_role text;
BEGIN
  SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Ô % không tồn tại', p_slot; END IF;
  SELECT * INTO a FROM kiosk_ops.slot_assignments WHERE slot_id = p_slot AND status = 'ACTIVE' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Ô % chưa gán cho seller nào', s.slot_code; END IF;
  SELECT role INTO v_role FROM identity.users WHERE id = p_actor;
  IF v_role <> 'ADMIN' THEN PERFORM flow.check_seller_user(p_actor, a.seller_id); END IF;
  IF s.status = 'HELD' THEN RAISE EXCEPTION 'Ô % đang có khách thanh toán, thu hồi sau', s.slot_code; END IF;
  UPDATE kiosk_ops.slot_assignments SET status = 'RELEASED', released_at = v_now, release_reason = CASE WHEN v_role = 'ADMIN' THEN 'ADMIN' ELSE 'SELLER' END WHERE id = a.id;
  IF s.status = 'RENTED_EMPTY' THEN
    UPDATE kiosk_ops.slots SET status = 'FREE', current_seller_id = NULL, row_version = row_version + 1 WHERE id = s.id;
  ELSIF s.status = 'STOCKED' THEN
    UPDATE kiosk_ops.slots SET status = 'PENDING_RELEASE', row_version = row_version + 1 WHERE id = s.id;
  END IF;
  PERFORM flow.audit(p_actor, 'SLOT_RELEASED', 'slots', s.id, jsonb_build_object('seller', a.seller_id, 'reason', p_reason, 'slot', s.slot_code));
END $$;


ALTER FUNCTION flow.release_slot(p_slot uuid, p_actor uuid, p_reason text, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: release_slot_empty(uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.release_slot_empty(p_slot uuid) RETURNS text
    LANGUAGE plpgsql
    AS $$
DECLARE v_has boolean; v_status text;
BEGIN
  SELECT EXISTS (SELECT 1 FROM kiosk_ops.slot_assignments a JOIN kiosk_ops.slots s ON s.id = a.slot_id
                 WHERE a.slot_id = p_slot AND a.seller_id = s.current_seller_id AND a.status = 'ACTIVE') INTO v_has;
  UPDATE kiosk_ops.slots SET status = CASE WHEN v_has THEN 'RENTED_EMPTY' ELSE 'FREE' END,
         current_seller_id = CASE WHEN v_has THEN current_seller_id END,
         bouquet_id = NULL, hold_until = NULL, hold_order_id = NULL, row_version = row_version + 1
   WHERE id = p_slot RETURNING status INTO v_status;
  RETURN v_status;
END $$;


ALTER FUNCTION flow.release_slot_empty(p_slot uuid) OWNER TO florabot;

--
-- Name: report_device_fault(uuid, uuid, uuid, text, text, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.report_device_fault(p_kiosk uuid, p_slot uuid, p_reporter uuid, p_desc text, p_photo text, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); v uuid; s kiosk_ops.slots; o ordering.orders;
BEGIN
  IF p_reporter IS NOT NULL AND p_photo IS NULL THEN RAISE EXCEPTION 'Bắt buộc ảnh minh chứng khi người báo sự cố'; END IF;
  INSERT INTO ordering.disputes (kind, kiosk_id, slot_id, reason, reported_by) VALUES ('DEVICE_FAULT', p_kiosk, p_slot, p_desc, p_reporter) RETURNING id INTO v;
  IF p_photo IS NOT NULL THEN PERFORM flow.attach('ordering', 'dispute', v, p_photo, 'EVIDENCE', p_reporter); END IF;
  IF p_slot IS NOT NULL THEN
    SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot FOR UPDATE;
    IF NOT FOUND OR s.kiosk_id <> p_kiosk THEN RAISE EXCEPTION 'Ô % không thuộc kiosk %', p_slot, p_kiosk; END IF;
    IF s.status NOT IN ('FAULT','LOCKED') THEN
      IF EXISTS (SELECT 1 FROM kiosk_ops.unlock_tokens WHERE slot_id = p_slot AND status = 'OPENED') THEN
        RAISE EXCEPTION 'Cửa ô % đang mở, đợi cảm biến đóng cửa rồi mới báo sự cố', s.slot_code; END IF;
      UPDATE kiosk_ops.unlock_tokens SET status = 'REVOKED' WHERE slot_id = p_slot AND status IN ('ISSUED','SENT','ACKED');
      IF s.status = 'HELD' AND s.hold_order_id IS NOT NULL THEN
        SELECT * INTO o FROM ordering.orders WHERE id = s.hold_order_id;
        IF o.status IN ('PAID','DISPENSING') THEN
          PERFORM flow.dispense_failed(o.id, 'Sự cố thiết bị tại ô ' || s.slot_code || ': ' || p_desc, p_slot);   -- đơn đang nhận hàng: hoàn tiền luôn
        ELSIF o.status = 'AWAITING_PAYMENT' THEN
          -- khách chưa trả tiền: hủy giỏ, khoản thu PENDING -> CANCELLED (tiền về muộn sẽ đi đường hoàn tự động)
          UPDATE payment.payments SET status = 'CANCELLED' WHERE checkout_id = o.checkout_id AND kind = 'CHARGE' AND status = 'PENDING';
          PERFORM flow.release_checkout(o.checkout_id, 'CANCELLED');
          UPDATE kiosk_ops.slots SET status = 'FAULT', row_version = row_version + 1 WHERE id = p_slot;
        END IF;
      ELSE
        UPDATE kiosk_ops.slots SET status = 'FAULT', row_version = row_version + 1 WHERE id = p_slot;
      END IF;
    END IF;
  END IF;
  PERFORM flow.audit(p_reporter, 'DEVICE_FAULT_REPORTED', 'disputes', v, jsonb_build_object('kiosk', p_kiosk, 'slot', p_slot), CASE WHEN p_reporter IS NULL THEN 'KIOSK' END);
  RETURN v;
END $$;


ALTER FUNCTION flow.report_device_fault(p_kiosk uuid, p_slot uuid, p_reporter uuid, p_desc text, p_photo text, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: request_pickup(uuid, text, uuid, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.request_pickup(p_order uuid, p_tracking text, p_kiosk uuid, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); o ordering.orders; v_k text; v_cmd uuid;
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION '????n % kh??ng t???n t???i', p_order; END IF;
  IF o.kiosk_id <> p_kiosk THEN RAISE EXCEPTION '????n kh??ng nh???n t???i kiosk n??y'; END IF;
  IF o.status NOT IN ('PAID','DISPENSING') THEN RAISE EXCEPTION '????n kh??ng ??? tr???ng th??i ch??? nh???n (%)', o.status; END IF;
  SELECT status INTO v_k FROM kiosk_ops.kiosks WHERE id = p_kiosk;
  IF v_k IS DISTINCT FROM 'ONLINE' THEN RAISE EXCEPTION 'Kiosk ??ang kh??ng tr???c tuy???n, th??? l???i sau'; END IF;
  IF NOT EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE hold_order_id = o.id AND status = 'HELD' AND hold_until >= v_now) THEN
    RAISE EXCEPTION 'H???t th???i gian gi??? h??ng t???i t???'; END IF;
  IF coalesce(p_tracking, '') = '' OR o.tracking_token <> p_tracking THEN
    PERFORM flow.audit(p_kiosk, 'PICKUP_CODE_REJECTED', 'orders', o.id, NULL, 'KIOSK');
    RAISE NOTICE 'Sai m?? e-receipt';
    RETURN NULL;   -- kh??ng RAISE ????? audit ???????c l??u
  END IF;
  IF EXISTS(SELECT 1 FROM ordering.web_requests WHERE order_id=o.id AND pickup_at>v_now) THEN RAISE EXCEPTION 'Chua den gio hen nhan hoa'; END IF;
  v_cmd := flow.issue_pickup_tokens(o.id);
  IF o.status = 'PAID' THEN PERFORM flow.set_order_status(o.id, 'DISPENSING', 'KIOSK', NULL, 'Kiosk online l???i, ph??t token'); END IF;
  RETURN v_cmd;
END $$;


ALTER FUNCTION flow.request_pickup(p_order uuid, p_tracking text, p_kiosk uuid, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: request_withdrawal(uuid, uuid, bigint, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.request_withdrawal(p_seller uuid, p_user uuid, p_amount bigint, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); v_avail bigint; s identity.sellers; v_w uuid;
BEGIN
  PERFORM flow.check_seller_user(p_user, p_seller);
  IF (SELECT role FROM identity.users WHERE id = p_user) <> 'SELLER' THEN RAISE EXCEPTION 'Chỉ tài khoản của shop được yêu cầu rút tiền'; END IF;
  IF p_amount < flow.cfg('withdraw_min') THEN RAISE EXCEPTION 'Số tiền rút tối thiểu % đồng', flow.cfg('withdraw_min'); END IF;
  PERFORM pg_advisory_xact_lock(hashtext('seller:' || p_seller::text));
  SELECT available_balance INTO v_avail FROM payment.v_seller_balance WHERE seller_id = p_seller;
  v_avail := coalesce(v_avail, 0);
  IF v_avail <= 0 OR v_avail < p_amount THEN RAISE EXCEPTION 'Số dư khả dụng % < số tiền rút %', v_avail, p_amount; END IF;
  SELECT * INTO s FROM identity.sellers WHERE id = p_seller;
  IF s.bank_account_no_enc IS NULL THEN RAISE EXCEPTION 'Seller chưa khai báo tài khoản nhận tiền'; END IF;
  INSERT INTO payment.withdrawal_requests (seller_id, amount, bank_name, bank_account_no_enc, bank_holder)
  VALUES (p_seller, p_amount, s.bank_name, s.bank_account_no_enc, s.bank_holder) RETURNING id INTO v_w;
  PERFORM flow.audit(p_user, 'WITHDRAWAL_REQUESTED', 'withdrawal_requests', v_w, jsonb_build_object('amount', p_amount));
  RETURN v_w;
END $$;


ALTER FUNCTION flow.request_withdrawal(p_seller uuid, p_user uuid, p_amount bigint, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: require_active_admin(uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.require_active_admin(p_admin uuid) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
  PERFORM 1 FROM identity.users WHERE id=p_admin AND role='ADMIN' AND status='ACTIVE' FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'C???n t??i kho???n qu???n tr??? ??ang ho???t ?????ng'; END IF;
END $$;


ALTER FUNCTION flow.require_active_admin(p_admin uuid) OWNER TO florabot;

--
-- Name: reserve_gateway_order(uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.reserve_gateway_order(p_payment uuid) RETURNS bigint
    LANGUAGE plpgsql
    AS $$
DECLARE p payment.payments; result bigint;
BEGIN
  SELECT * INTO p FROM payment.payments WHERE id=p_payment FOR UPDATE;
  IF NOT FOUND OR p.kind <> 'CHARGE' OR p.gateway <> 'PAYOS' OR p.status <> 'PENDING' THEN
    RAISE EXCEPTION 'Payment is not an eligible pending payOS charge';
  END IF;
  INSERT INTO payment.gateway_orders(payment_id) VALUES(p.id)
    ON CONFLICT(payment_id) DO NOTHING;
  SELECT order_code INTO result FROM payment.gateway_orders WHERE payment_id=p.id;
  RETURN result;
END $$;


ALTER FUNCTION flow.reserve_gateway_order(p_payment uuid) OWNER TO florabot;

--
-- Name: reset_clock(); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.reset_clock() RETURNS void
    LANGUAGE sql
    AS $$ SELECT set_config('app.now', '', true); $$;


ALTER FUNCTION flow.reset_clock() OWNER TO florabot;

--
-- Name: resolve_device_fault(uuid, uuid, text, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.resolve_device_fault(p_dispute uuid, p_admin uuid, p_note text, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); d ordering.disputes; s kiosk_ops.slots; v_bstatus text;
BEGIN
  SELECT * INTO d FROM ordering.disputes WHERE id = p_dispute AND kind IN ('DEVICE_FAULT','DISPENSE_FAILED') FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Sự cố % không tồn tại', p_dispute; END IF;
  IF d.status <> 'OPEN' THEN RAISE EXCEPTION 'Sự cố đã đóng (%)', d.status; END IF;
  IF d.kind = 'DISPENSE_FAILED' AND EXISTS (SELECT 1 FROM payment.payments WHERE order_id = d.order_id AND kind = 'REFUND' AND status = 'PENDING') THEN
    RAISE EXCEPTION 'Phải chi hoàn cho khách (confirm_refund) trước khi đóng sự cố nhả hàng'; END IF;
  IF d.slot_id IS NOT NULL THEN
    SELECT * INTO s FROM kiosk_ops.slots WHERE id = d.slot_id FOR UPDATE;
    IF s.status = 'FAULT' THEN
      IF s.bouquet_id IS NULL THEN
        PERFORM flow.release_slot_empty(s.id);
      ELSE
        SELECT status INTO v_bstatus FROM kiosk_ops.bouquets WHERE id = s.bouquet_id;
        UPDATE kiosk_ops.slots SET status = CASE WHEN v_bstatus <> 'STOCKED' THEN 'PENDING_REMOVAL'
                                                 WHEN flow.slot_sellable(id, current_seller_id) THEN 'STOCKED' ELSE 'PENDING_RELEASE' END,
               hold_until = NULL, hold_order_id = NULL, row_version = row_version + 1 WHERE id = s.id;
      END IF;
    END IF;
  END IF;
  UPDATE ordering.disputes SET status = 'RESOLVED_FIXED', decision = p_note, decided_by = p_admin, resolved_at = v_now WHERE id = d.id;
  PERFORM flow.audit(p_admin, 'DEVICE_FAULT_RESOLVED', 'disputes', d.id, jsonb_build_object('note', p_note));
END $$;


ALTER FUNCTION flow.resolve_device_fault(p_dispute uuid, p_admin uuid, p_note text, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: resolve_dispute(uuid, uuid, bigint, text, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.resolve_dispute(p_dispute uuid, p_admin uuid, p_refund bigint, p_decision text, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); d ordering.disputes; o ordering.orders; v_ref uuid;
BEGIN
  SELECT * INTO d FROM ordering.disputes WHERE id = p_dispute AND kind = 'COMPLAINT' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Khiếu nại % không tồn tại', p_dispute; END IF;
  IF d.status <> 'OPEN' THEN RAISE EXCEPTION 'Khiếu nại đã đóng (%)', d.status; END IF;
  SELECT * INTO o FROM ordering.orders WHERE id = d.order_id FOR UPDATE;
  IF p_refund IS NULL OR p_refund = 0 THEN
    UPDATE ordering.disputes SET status = 'RESOLVED_REJECT', decision = p_decision, decided_by = p_admin, resolved_at = v_now WHERE id = d.id;
    PERFORM flow.set_order_status(o.id, 'COMPLETED', 'ADMIN', p_admin, p_decision);
    PERFORM flow.move_pending_to_available(o.id, 'Đối soát sau khiếu nại');
    PERFORM flow.audit(p_admin, 'DISPUTE_REJECTED', 'disputes', d.id, jsonb_build_object('order', o.order_code));
    RETURN NULL;
  END IF;
  v_ref := flow.create_refund(o.id, p_refund, p_decision, p_admin, 'MANUAL');
  UPDATE ordering.disputes SET status = 'RESOLVED_REFUND', refund_amount = p_refund, decision = p_decision, decided_by = p_admin, resolved_at = v_now WHERE id = d.id;
  PERFORM flow.audit(p_admin, 'DISPUTE_REFUND_APPROVED', 'disputes', d.id, jsonb_build_object('order', o.order_code, 'refund', p_refund, 'refund_payment', v_ref));
  RETURN v_ref;
END $$;


ALTER FUNCTION flow.resolve_dispute(p_dispute uuid, p_admin uuid, p_refund bigint, p_decision text, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: restock_accessory(uuid, uuid, integer); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.restock_accessory(p_accessory uuid, p_user uuid, p_qty integer) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE a kiosk_ops.accessories;
BEGIN
  IF p_qty <= 0 THEN RAISE EXCEPTION 'Số lượng nạp phải > 0'; END IF;
  SELECT * INTO a FROM kiosk_ops.accessories WHERE id = p_accessory FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Phụ kiện % không tồn tại', p_accessory; END IF;
  PERFORM flow.check_seller_user(p_user, a.seller_id);
  UPDATE kiosk_ops.accessories SET stock_quantity = stock_quantity + p_qty WHERE id = a.id;
  PERFORM flow.audit(p_user, 'ACCESSORY_RESTOCKED', 'accessories', a.id, jsonb_build_object('qty', p_qty));
END $$;


ALTER FUNCTION flow.restock_accessory(p_accessory uuid, p_user uuid, p_qty integer) OWNER TO florabot;

--
-- Name: restore_points(uuid, bigint, uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.restore_points(p_customer uuid, p_points bigint, p_order uuid) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
  IF p_points IS NULL OR p_points <= 0 THEN RETURN; END IF;
  UPDATE identity.users SET loyalty_points = loyalty_points + p_points WHERE id = p_customer AND role IN ('CUSTOMER','SELLER') AND status = 'ACTIVE';
  IF FOUND THEN PERFORM flow.audit(p_customer, 'POINTS_RESTORED', 'orders', p_order, jsonb_build_object('points', p_points));
  ELSE PERFORM flow.audit(p_customer, 'POINTS_SKIPPED_INACTIVE', 'orders', p_order, jsonb_build_object('points', p_points, 'restore', true)); END IF;
END $$;


ALTER FUNCTION flow.restore_points(p_customer uuid, p_points bigint, p_order uuid) OWNER TO florabot;

--
-- Name: return_to_seller(uuid, uuid, text, text, boolean, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.return_to_seller(p_slot uuid, p_staff uuid, p_photo_url text, p_reason text, p_damaged boolean DEFAULT false, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); s kiosk_ops.slots; v_tok uuid; v_log uuid; v_seller uuid;
BEGIN
  SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Ô % không tồn tại', p_slot; END IF;
  IF s.status NOT IN ('PENDING_REMOVAL','PENDING_RELEASE') THEN RAISE EXCEPTION 'Ô % không ở trạng thái chờ trả hàng (%)', s.slot_code, s.status; END IF;
  SELECT seller_id INTO v_seller FROM kiosk_ops.bouquets WHERE id = s.bouquet_id;
  PERFORM flow.check_seller_user(p_staff, v_seller);
  -- token SELLER_ACCESS; người đứng tại tủ nên mô phỏng luôn chuỗi sự kiện mở/đóng cửa
  UPDATE kiosk_ops.unlock_tokens SET status = 'REVOKED' WHERE slot_id = s.id AND status IN ('ISSUED','SENT','ACKED');
  INSERT INTO kiosk_ops.unlock_tokens (kiosk_id, slot_id, purpose, issued_to_user_id, token_hash, issued_at, expires_at)
  VALUES (s.kiosk_id, s.id, 'SELLER_ACCESS', p_staff, encode(sha256(convert_to(public.uuid_v7()::text, 'UTF8')), 'hex'), v_now, v_now + flow.cfg_min('token_minutes'))
  RETURNING cmd_id INTO v_tok;
  PERFORM flow.device_event(v_tok, 'SENT'); PERFORM flow.device_event(v_tok, 'ACK');
  PERFORM flow.device_event(v_tok, 'OPENED'); PERFORM flow.device_event(v_tok, 'CLOSED');
  INSERT INTO kiosk_ops.inventory_logs (bouquet_id, slot_id, movement_type, performed_by, reason)
  VALUES (s.bouquet_id, s.id, CASE WHEN p_damaged THEN 'DAMAGE' ELSE 'RETURN_SELLER' END, p_staff, p_reason) RETURNING id INTO v_log;
  IF p_photo_url IS NOT NULL THEN PERFORM flow.attach('kiosk_ops', 'inventory_log', v_log, p_photo_url, 'RETURN', p_staff); END IF;
  UPDATE kiosk_ops.bouquets SET status = CASE WHEN p_damaged THEN 'DAMAGED' ELSE 'RETURNED' END WHERE id = s.bouquet_id;
  PERFORM flow.release_slot_empty(s.id);
END $$;


ALTER FUNCTION flow.return_to_seller(p_slot uuid, p_staff uuid, p_photo_url text, p_reason text, p_damaged boolean, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: roll_subscriptions(date, uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.roll_subscriptions(p_today date DEFAULT NULL::date, p_seller uuid DEFAULT NULL::uuid) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE v_today date; r record; n int := 0; v_grace int := flow.cfg('past_due_grace_days')::int;
BEGIN
  IF p_today IS NOT NULL THEN PERFORM flow.tick(p_today::timestamptz + interval '1 hour'); END IF;
  v_today := public.app_now()::date;
  FOR r IN SELECT * FROM identity.subscriptions WHERE status = 'ACTIVE' AND period_to <= v_today AND (p_seller IS NULL OR seller_id = p_seller) ORDER BY id FOR UPDATE LOOP
    UPDATE identity.subscriptions SET status = 'EXPIRED' WHERE id = r.id; n := n + 1;
  END LOOP;
  FOR r IN SELECT id, status, package_expires_at FROM identity.sellers WHERE status IN ('APPROVED','ACTIVE','PAST_DUE') AND (p_seller IS NULL OR id = p_seller) ORDER BY id LOOP
    IF flow.refresh_seller_package(r.id) <> r.status THEN n := n + 1; END IF;
    IF (SELECT status FROM identity.sellers WHERE id = r.id) = 'PAST_DUE' AND r.package_expires_at + v_grace <= v_today
       AND EXISTS (SELECT 1 FROM kiosk_ops.slot_assignments WHERE seller_id = r.id AND status = 'ACTIVE') THEN
      PERFORM flow.audit(NULL, 'SELLER_SLOTS_RELEASED', 'sellers', r.id, jsonb_build_object('expired', r.package_expires_at, 'grace_days', v_grace));
      n := n + flow.release_seller_slots(r.id, 'PAST_DUE_GRACE');
    END IF;
  END LOOP;
  RETURN n;
END $$;


ALTER FUNCTION flow.roll_subscriptions(p_today date, p_seller uuid) OWNER TO florabot;

--
-- Name: set_cfg(text, numeric, uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.set_cfg(p_key text, p_value numeric, p_admin uuid) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
  UPDATE kiosk_ops.system_settings SET value = to_jsonb(p_value), updated_by = p_admin, updated_at = public.app_now() WHERE key = p_key;
  IF NOT FOUND THEN RAISE EXCEPTION 'Tham số % không tồn tại', p_key; END IF;
  PERFORM flow.audit(p_admin, 'SETTING_CHANGED', 'system_settings', NULL, jsonb_build_object('key', p_key, 'value', p_value));
END $$;


ALTER FUNCTION flow.set_cfg(p_key text, p_value numeric, p_admin uuid) OWNER TO florabot;

--
-- Name: set_kiosk_status(uuid, text, uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.set_kiosk_status(p_kiosk uuid, p_status text, p_actor uuid) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
  UPDATE kiosk_ops.kiosks SET status = p_status, last_heartbeat_at = CASE WHEN p_status = 'ONLINE' THEN public.app_now() ELSE last_heartbeat_at END WHERE id = p_kiosk;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk % không tồn tại', p_kiosk; END IF;
  PERFORM flow.audit(p_actor, 'KIOSK_STATUS_' || p_status, 'kiosks', p_kiosk);
END $$;


ALTER FUNCTION flow.set_kiosk_status(p_kiosk uuid, p_status text, p_actor uuid) OWNER TO florabot;

--
-- Name: set_order_status(uuid, text, text, uuid, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.set_order_status(p_order uuid, p_to text, p_actor_type text, p_actor uuid DEFAULT NULL::uuid, p_note text DEFAULT NULL::text) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE v_from text;
BEGIN
  SELECT status INTO v_from FROM ordering.orders WHERE id = p_order FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  UPDATE ordering.orders SET status = p_to,
         completed_at = CASE WHEN p_to = 'COMPLETED' AND completed_at IS NULL THEN public.app_now() ELSE completed_at END
   WHERE id = p_order;
  PERFORM flow.audit(p_actor, 'ORDER_STATUS_CHANGED', 'orders', p_order, jsonb_build_object('from', v_from, 'to', p_to, 'note', p_note),
                     CASE p_actor_type WHEN 'KIOSK' THEN 'KIOSK' WHEN 'SYSTEM' THEN 'SYSTEM' ELSE 'USER' END);
END $$;


ALTER FUNCTION flow.set_order_status(p_order uuid, p_to text, p_actor_type text, p_actor uuid, p_note text) OWNER TO florabot;

--
-- Name: set_product_status(uuid, uuid, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.set_product_status(p_product uuid, p_user uuid, p_status text) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
  PERFORM flow.check_catalog_editor(p_product, p_user);
  UPDATE catalog.flower_products p SET status = p_status
  WHERE p.id = p_product AND EXISTS (
    SELECT 1 FROM identity.users u WHERE u.id = p_user AND u.status = 'ACTIVE'
    AND (u.role = 'ADMIN' OR (u.role = 'SELLER' AND u.seller_id = p.seller_id))
  );
  IF NOT FOUND THEN RAISE EXCEPTION 'Kh??ng c?? quy???n s???a s???n ph???m %', p_product; END IF;
  PERFORM flow.audit(p_user, 'PRODUCT_STATUS_CHANGED', 'flower_products', p_product,
                     jsonb_build_object('status', p_status));
END $$;


ALTER FUNCTION flow.set_product_status(p_product uuid, p_user uuid, p_status text) OWNER TO florabot;

--
-- Name: set_seller_bank(uuid, uuid, text, text, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.set_seller_bank(p_seller uuid, p_user uuid, p_bank text, p_account_enc text, p_holder text) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
  PERFORM flow.check_seller_user(p_user, p_seller);
  IF p_bank IS NULL OR p_account_enc IS NULL OR p_holder IS NULL THEN RAISE EXCEPTION 'Thiếu thông tin tài khoản ngân hàng'; END IF;
  UPDATE identity.sellers SET bank_name = p_bank, bank_account_no_enc = p_account_enc, bank_holder = upper(p_holder) WHERE id = p_seller;
  PERFORM flow.audit(p_user, 'SELLER_BANK_CHANGED', 'sellers', p_seller, jsonb_build_object('bank', p_bank, 'holder', upper(p_holder)));
END $$;


ALTER FUNCTION flow.set_seller_bank(p_seller uuid, p_user uuid, p_bank text, p_account_enc text, p_holder text) OWNER TO florabot;

--
-- Name: set_seller_status(uuid, text, uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.set_seller_status(p_seller uuid, p_status text, p_admin uuid) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE v_old text;
BEGIN
  IF p_status NOT IN ('ACTIVE','SUSPENDED','CLOSED') THEN RAISE EXCEPTION 'Admin chỉ đặt ACTIVE/SUSPENDED/CLOSED (PAST_DUE do hệ thống)'; END IF;
  SELECT status INTO v_old FROM identity.sellers WHERE id = p_seller FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Seller % không tồn tại', p_seller; END IF;
  IF v_old = 'CLOSED' THEN RAISE EXCEPTION 'Seller đã đóng, không đổi được'; END IF;
  IF v_old = 'PENDING' THEN RAISE EXCEPTION 'Seller chưa duyệt hồ sơ (approve_seller)'; END IF;
  IF p_status = 'ACTIVE' THEN
    UPDATE identity.sellers SET status = 'APPROVED' WHERE id = p_seller;   -- tạm, refresh quyết định ACTIVE hay PAST_DUE
    PERFORM flow.refresh_seller_package(p_seller);
  ELSE
    UPDATE identity.sellers SET status = p_status WHERE id = p_seller;
    IF p_status = 'CLOSED' THEN PERFORM flow.release_seller_slots(p_seller, 'SELLER_CLOSED'); END IF;
  END IF;
  PERFORM flow.audit(p_admin, 'SELLER_STATUS_' || p_status, 'sellers', p_seller, jsonb_build_object('from', v_old));
END $$;


ALTER FUNCTION flow.set_seller_status(p_seller uuid, p_status text, p_admin uuid) OWNER TO florabot;

--
-- Name: set_seller_tone(uuid, uuid, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.set_seller_tone(p_seller uuid, p_user uuid, p_tone text) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
  PERFORM flow.check_seller_user(p_user, p_seller);
  IF length(p_tone) > 200 OR p_tone ~* '(ignore|bỏ qua|system prompt|hướng dẫn trước)' THEN
    RAISE EXCEPTION 'Giọng thương hiệu không hợp lệ (quá dài hoặc chứa từ bị chặn)'; END IF;   -- text seller cũng đi vào prompt: chống injection gián tiếp
  UPDATE identity.sellers SET brand_tone = p_tone WHERE id = p_seller;
  PERFORM flow.audit(p_user, 'SELLER_TONE_CHANGED', 'sellers', p_seller, jsonb_build_object('tone', p_tone));
END $$;


ALTER FUNCTION flow.set_seller_tone(p_seller uuid, p_user uuid, p_tone text) OWNER TO florabot;

--
-- Name: settle_due_orders(timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.settle_due_orders(p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); o record; n int := 0;
BEGIN
  FOR o IN SELECT id FROM ordering.orders od
           WHERE od.status = 'COMPLETED' AND od.completed_at + flow.cfg_hour('dispute_window_hours') <= v_now AND flow.order_seller_pending(od.id) > 0
             AND NOT EXISTS (SELECT 1 FROM payment.payments p WHERE p.order_id = od.id AND p.kind = 'REFUND' AND p.status = 'PENDING')
           ORDER BY od.completed_at, od.id LOOP
    PERFORM flow.settle_order(o.id);
    n := n + 1;
  END LOOP;
  RETURN n;
END $$;


ALTER FUNCTION flow.settle_due_orders(p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: settle_order(uuid, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.settle_order(p_order uuid, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); o ordering.orders;
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  PERFORM pg_advisory_xact_lock(hashtext('seller:' || o.seller_id::text));
  IF o.status <> 'COMPLETED' THEN RAISE EXCEPTION 'Chỉ đối soát đơn đã hoàn tất, đơn đang %', o.status; END IF;
  IF v_now < o.completed_at + flow.cfg_hour('dispute_window_hours') THEN
    RAISE EXCEPTION 'Chưa hết thời gian khiếu nại (đến %)', o.completed_at + flow.cfg_hour('dispute_window_hours'); END IF;
  IF EXISTS (SELECT 1 FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'PENDING') THEN
    RAISE EXCEPTION 'Đơn % còn lệnh hoàn chờ chi, chưa đối soát', o.order_code; END IF;
  PERFORM flow.move_pending_to_available(o.id, 'Đối soát đơn');
END $$;


ALTER FUNCTION flow.settle_order(p_order uuid, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: slot_sellable(uuid, uuid); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.slot_sellable(p_slot uuid, p_seller uuid) RETURNS boolean
    LANGUAGE sql STABLE
    AS $$
  SELECT EXISTS (SELECT 1 FROM kiosk_ops.slot_assignments WHERE slot_id = p_slot AND seller_id = p_seller AND status = 'ACTIVE')
         AND flow.package_valid(p_seller) $$;


ALTER FUNCTION flow.slot_sellable(p_slot uuid, p_seller uuid) OWNER TO florabot;

--
-- Name: staff_resolve_incident(uuid, uuid, uuid, integer, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.staff_resolve_incident(p_id uuid, p_task uuid, p_actor uuid, p_version integer, p_report text) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  t kiosk_ops.staff_tasks;
  previous kiosk_ops.staff_incident_actions;
  d ordering.disputes;
  v_actor_role text;
BEGIN
  SELECT role INTO v_actor_role FROM identity.users WHERE id = p_actor AND status = 'ACTIVE' FOR SHARE;
  IF v_actor_role IS NULL OR v_actor_role <> 'TECHNICIAN' THEN
    RAISE EXCEPTION 'Active technician required' USING ERRCODE = '42501';
  END IF;

  SELECT * INTO t FROM kiosk_ops.staff_tasks WHERE id = p_task AND assignee_id = p_actor AND kind = 'INCIDENT' FOR UPDATE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
  END IF;

  IF p_id IS NULL OR p_report IS NULL OR length(btrim(p_report)) NOT BETWEEN 10 AND 2000 THEN
    RAISE EXCEPTION 'Invalid resolution report' USING ERRCODE = '22023';
  END IF;

  PERFORM pg_advisory_xact_lock(hashtextextended('staff-incident:' || p_id::text, 0));
  SELECT * INTO previous FROM kiosk_ops.staff_incident_actions WHERE id = p_id;
  IF FOUND THEN
    IF previous.task_id <> p_task OR previous.actor_id <> p_actor OR previous.request_version IS DISTINCT FROM p_version OR previous.report <> btrim(p_report) THEN
      RAISE EXCEPTION 'Operation ID already used' USING ERRCODE = '40001';
    END IF;
    RETURN previous.result_version;
  END IF;

  IF t.version IS DISTINCT FROM p_version OR t.status <> 'IN_PROGRESS' THEN
    RAISE EXCEPTION 'Task changed; reload first' USING ERRCODE = '40001';
  END IF;

  SELECT * INTO d FROM ordering.disputes WHERE id = t.incident_id AND kiosk_id = t.kiosk_id AND kind IN ('DEVICE_FAULT', 'DISPENSE_FAILED') FOR UPDATE;
  IF NOT FOUND OR d.status <> 'OPEN' THEN
    RAISE EXCEPTION 'Incident is no longer open' USING ERRCODE = '40001';
  END IF;

  PERFORM flow.resolve_device_fault(d.id, p_actor, btrim(p_report));
  UPDATE kiosk_ops.staff_tasks SET status = 'SUBMITTED', report = btrim(p_report), version = version + 1, updated_at = public.app_now() WHERE id = t.id;
  INSERT INTO kiosk_ops.staff_incident_actions(id, task_id, actor_id, incident_id, request_version, result_version, report)
  VALUES (p_id, t.id, p_actor, d.id, p_version, t.version + 1, btrim(p_report));
  PERFORM flow.audit(p_actor, 'STAFF_INCIDENT_RESOLVED', 'staff_tasks', t.id, jsonb_build_object('from', t.status, 'to', 'SUBMITTED', 'report', btrim(p_report), 'operation', p_id));
  RETURN t.version + 1;
END $$;


ALTER FUNCTION flow.staff_resolve_incident(p_id uuid, p_task uuid, p_actor uuid, p_version integer, p_report text) OWNER TO florabot;

--
-- Name: staff_stock_bouquet(uuid, uuid, uuid, uuid, uuid, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.staff_stock_bouquet(p_id uuid, p_task uuid, p_staff uuid, p_product uuid, p_slot uuid, p_qr text) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  t kiosk_ops.staff_tasks;
  previous kiosk_ops.staff_stock_actions;
  pr catalog.flower_products;
  s kiosk_ops.slots;
  v_b uuid;
  v_staff_role text;
  v_staff_seller uuid;
BEGIN
  SELECT role, seller_id INTO v_staff_role, v_staff_seller
  FROM identity.users WHERE id = p_staff AND status = 'ACTIVE' FOR SHARE;
  
  IF v_staff_role IS NULL OR v_staff_role <> 'SELLER_STAFF' THEN
    RAISE EXCEPTION 'Active seller staff required' USING ERRCODE = '42501';
  END IF;

  SELECT * INTO t FROM kiosk_ops.staff_tasks WHERE id = p_task FOR UPDATE;
  IF NOT FOUND OR t.assignee_id <> p_staff OR t.kind <> 'DELIVERY' OR t.seller_id IS DISTINCT FROM v_staff_seller THEN
    RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
  END IF;

  PERFORM pg_advisory_xact_lock(hashtextextended('staff-stock:' || p_id::text, 0));
  SELECT * INTO previous FROM kiosk_ops.staff_stock_actions WHERE id = p_id;
  IF FOUND THEN
    IF previous.task_id <> p_task OR previous.actor_id <> p_staff OR previous.product_id <> p_product
       OR previous.slot_id <> p_slot OR previous.qr_code IS DISTINCT FROM btrim(p_qr) THEN
      RAISE EXCEPTION 'Operation ID already used' USING ERRCODE = '40001';
    END IF;
    RETURN previous.bouquet_id;
  END IF;

  IF t.status <> 'IN_PROGRESS' THEN
    RAISE EXCEPTION 'Active delivery task required' USING ERRCODE = '22023';
  END IF;

  IF p_id IS NULL OR p_qr IS NULL OR length(btrim(p_qr)) NOT BETWEEN 1 AND 120 THEN
    RAISE EXCEPTION 'Invalid stock reference' USING ERRCODE = '22023';
  END IF;

  SELECT * INTO pr FROM catalog.flower_products WHERE id = p_product FOR SHARE;
  IF NOT FOUND OR pr.seller_id IS DISTINCT FROM t.seller_id OR pr.status <> 'ACTIVE' THEN
    RAISE EXCEPTION 'Product outside task scope' USING ERRCODE = '22023';
  END IF;

  PERFORM 1 FROM identity.sellers WHERE id = t.seller_id AND status = 'ACTIVE' FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Seller inactive' USING ERRCODE = '22023'; END IF;

  PERFORM 1 FROM kiosk_ops.kiosks WHERE id = t.kiosk_id AND status NOT IN ('DISABLED', 'MAINTENANCE') FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk unavailable' USING ERRCODE = '22023'; END IF;

  SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot FOR UPDATE;
  IF NOT FOUND OR s.kiosk_id <> t.kiosk_id OR s.current_seller_id IS DISTINCT FROM t.seller_id
     OR s.status <> 'RENTED_EMPTY' OR NOT flow.slot_sellable(p_slot, t.seller_id) THEN
    RAISE EXCEPTION 'Slot is unavailable or outside task scope' USING ERRCODE = '22023';
  END IF;

  INSERT INTO kiosk_ops.bouquets(product_id, seller_id, qr_code, price_snapshot, status, sellable_until, stocked_at)
  VALUES (pr.id, pr.seller_id, btrim(p_qr), pr.price, 'STOCKED', public.app_now() + make_interval(hours => pr.shelf_life_hours), public.app_now())
  RETURNING id INTO v_b;

  UPDATE kiosk_ops.slots SET status = 'STOCKED', bouquet_id = v_b, row_version = row_version + 1 WHERE id = s.id;
  INSERT INTO kiosk_ops.inventory_logs(bouquet_id, slot_id, movement_type, batch_id, performed_by)
  VALUES (v_b, s.id, 'STOCK_IN', p_task, p_staff);
  INSERT INTO kiosk_ops.staff_stock_actions(id, task_id, actor_id, product_id, slot_id, qr_code, bouquet_id)
  VALUES (p_id, p_task, p_staff, pr.id, s.id, btrim(p_qr), v_b);
  PERFORM flow.audit(p_staff, 'STAFF_TASK_STOCKED', 'staff_tasks', p_task, jsonb_build_object('operation', p_id, 'bouquet', v_b, 'slot', s.id));
  RETURN v_b;
END $$;


ALTER FUNCTION flow.staff_stock_bouquet(p_id uuid, p_task uuid, p_staff uuid, p_product uuid, p_slot uuid, p_qr text) OWNER TO florabot;

--
-- Name: stock_bouquet(uuid, uuid, uuid, uuid, text, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.stock_bouquet(p_batch uuid, p_product uuid, p_slot uuid, p_staff uuid, p_qr text, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); pr catalog.flower_products; s kiosk_ops.slots; v_b uuid; v_seller_status text;
BEGIN
  IF p_batch IS NULL THEN RAISE EXCEPTION 'Thiếu mã lô nạp hàng (batch_id)'; END IF;
  SELECT * INTO pr FROM catalog.flower_products WHERE id = p_product;
  IF NOT FOUND THEN RAISE EXCEPTION 'Sản phẩm % không tồn tại', p_product; END IF;
  IF pr.status <> 'ACTIVE' THEN RAISE EXCEPTION 'Sản phẩm % chưa được bật bán', pr.name; END IF;
  SELECT status INTO v_seller_status FROM identity.sellers WHERE id = pr.seller_id;
  IF v_seller_status <> 'ACTIVE' THEN RAISE EXCEPTION 'Seller đang % (gói quá hạn hoặc bị khóa), không nạp được', v_seller_status; END IF;
  PERFORM flow.check_seller_user(p_staff, pr.seller_id);
  SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Ô % không tồn tại', p_slot; END IF;
  IF s.status <> 'RENTED_EMPTY' OR s.current_seller_id IS DISTINCT FROM pr.seller_id THEN
    RAISE EXCEPTION 'Ô % không phải ô trống do seller này thuê (status %)', s.slot_code, s.status; END IF;
  IF NOT flow.slot_sellable(p_slot, pr.seller_id) THEN
    RAISE EXCEPTION 'Gói của seller đã hết hạn ngày % (hoặc ô đã bị thu hồi), không nạp được vào ô %', v_now::date, s.slot_code; END IF;
  INSERT INTO kiosk_ops.bouquets (product_id, seller_id, qr_code, price_snapshot, status, sellable_until, stocked_at)
  VALUES (pr.id, pr.seller_id, p_qr, pr.price, 'STOCKED', v_now + make_interval(hours => pr.shelf_life_hours), v_now)
  RETURNING id INTO v_b;
  UPDATE kiosk_ops.slots SET status = 'STOCKED', bouquet_id = v_b, row_version = row_version + 1 WHERE id = p_slot;
  INSERT INTO kiosk_ops.inventory_logs (bouquet_id, slot_id, movement_type, batch_id, performed_by)
  VALUES (v_b, p_slot, 'STOCK_IN', p_batch, p_staff);
  RETURN v_b;
END $$;


ALTER FUNCTION flow.stock_bouquet(p_batch uuid, p_product uuid, p_slot uuid, p_staff uuid, p_qr text, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: submit_refund_info(uuid, text, text, text, text, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.submit_refund_info(p_order uuid, p_tracking text, p_bank text, p_account_enc text, p_holder text, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS void
    LANGUAGE plpgsql
    AS $_$
DECLARE v_now timestamptz := flow.tick(p_now); o ordering.orders;
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  IF coalesce(p_tracking, '') = '' OR o.tracking_token <> p_tracking THEN RAISE EXCEPTION 'Mã e-receipt không đúng: chỉ người giữ biên nhận mới khai được tài khoản nhận hoàn'; END IF;
  IF NOT EXISTS (SELECT 1 FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'PENDING') THEN
    RAISE EXCEPTION 'Đơn % không có lệnh hoàn nào đang chờ, không thu tài khoản ngân hàng', o.order_code; END IF;
  IF coalesce(p_bank, '') = '' OR coalesce(p_account_enc, '') = '' OR length(coalesce(p_holder, '')) < 2 THEN RAISE EXCEPTION 'Thiếu ngân hàng / số tài khoản / tên chủ tài khoản'; END IF;
  IF p_holder !~ '^[A-Za-zÀ-ỹ ]+$' THEN RAISE EXCEPTION 'Tên chủ tài khoản chỉ gồm chữ cái và khoảng trắng'; END IF;
  UPDATE ordering.orders SET refund_bank_name = p_bank, refund_bank_account_enc = p_account_enc, refund_bank_holder = upper(p_holder) WHERE id = o.id;
  PERFORM flow.audit(o.customer_id, 'REFUND_INFO_SUBMITTED', 'orders', o.id, jsonb_build_object('bank', p_bank), 'USER');
END $_$;


ALTER FUNCTION flow.submit_refund_info(p_order uuid, p_tracking text, p_bank text, p_account_enc text, p_holder text, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: subscribe(uuid, uuid, integer, date, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.subscribe(p_seller uuid, p_user uuid, p_months integer, p_from date DEFAULT NULL::date, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); v_seller identity.sellers; v_pkg identity.subscription_packages; v_from date; v_to date; v_sub uuid; v_pay uuid;
BEGIN
  IF p_months IS NULL OR p_months NOT BETWEEN 1 AND 12 THEN RAISE EXCEPTION 'Số tháng đăng ký phải từ 1 đến 12'; END IF;
  PERFORM flow.check_seller_user(p_user, p_seller);
  SELECT * INTO v_seller FROM identity.sellers WHERE id = p_seller FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Seller % không tồn tại', p_seller; END IF;
  IF v_seller.status = 'PENDING' THEN RAISE EXCEPTION 'Seller chưa được duyệt hồ sơ, chưa đăng ký gói được'; END IF;
  IF v_seller.status IN ('SUSPENDED','CLOSED') THEN RAISE EXCEPTION 'Seller đang % — liên hệ admin', v_seller.status; END IF;
  SELECT * INTO v_pkg FROM identity.subscription_packages WHERE id = v_seller.package_id;
  IF v_pkg.status <> 'ACTIVE' THEN RAISE EXCEPTION 'Gói % đã ngừng cung cấp, admin cần đổi gói', v_pkg.name; END IF;
  v_from := coalesce(p_from, greatest(v_now::date, coalesce((SELECT max(period_to) FROM identity.subscriptions WHERE seller_id = p_seller AND status IN ('PENDING_PAYMENT','ACTIVE')), v_now::date)));
  v_to := (v_from + make_interval(months => p_months))::date;
  IF v_to <= v_now::date THEN RAISE EXCEPTION 'Kỳ đăng ký đã kết thúc'; END IF;
  INSERT INTO identity.subscriptions (seller_id, package_id, period_from, period_to, price)
  VALUES (p_seller, v_pkg.id, v_from, v_to, v_pkg.monthly_fee * p_months) RETURNING id INTO v_sub;
  INSERT INTO payment.payments (kind, purpose, subscription_id, gateway, idempotency_key, amount)
  VALUES ('CHARGE','SUBSCRIPTION', v_sub, 'PAYOS', 'sub-' || v_sub, v_pkg.monthly_fee * p_months) RETURNING id INTO v_pay;
  UPDATE identity.subscriptions SET payment_id = v_pay WHERE id = v_sub;
  PERFORM flow.audit(p_user, 'SUBSCRIPTION_REQUESTED', 'subscriptions', v_sub, jsonb_build_object('package', v_pkg.name, 'from', v_from, 'to', v_to, 'price', v_pkg.monthly_fee * p_months));
  RETURN v_sub;
END $$;


ALTER FUNCTION flow.subscribe(p_seller uuid, p_user uuid, p_months integer, p_from date, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: subscription_paid(uuid, text, timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.subscription_paid(p_sub uuid, p_txn text, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE v_now timestamptz := flow.tick(p_now); r identity.subscriptions; p payment.payments; j uuid := public.uuid_v7();
BEGIN
  SELECT * INTO r FROM identity.subscriptions WHERE id = p_sub FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đăng ký gói % không tồn tại', p_sub; END IF;
  SELECT * INTO p FROM payment.payments WHERE id = r.payment_id FOR UPDATE;
  IF p.status = 'SUCCEEDED' THEN RETURN; END IF;      -- webhook gọi lại lần 2: bỏ qua
  UPDATE payment.payments SET status = 'SUCCEEDED', gateway_txn_id = p_txn, paid_at = v_now,
         raw_payload = jsonb_build_object('code','00','orderCode',p_txn) WHERE id = p.id;
  IF r.status = 'CANCELLED' THEN
    -- tiền về sau khi đăng ký đã hết hạn: ghi nhận tiền, lập lệnh hoàn (admin chi tay về STK seller), không kích hoạt
    INSERT INTO payment.payments (kind, purpose, subscription_id, parent_payment_id, order_id, gateway, idempotency_key, amount, status, approved_by, reason)
    VALUES ('REFUND','SUBSCRIPTION', r.id, p.id, NULL, 'MANUAL', 'late-sub-' || p.id, p.amount, 'PENDING', flow.sys_user(), 'LATE_SUBSCRIPTION_PAYMENT') ON CONFLICT DO NOTHING;
    INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
     (j, 'GATEWAY_CLEARING', NULL,  p.amount, 'REFUND', p.id, 'Phí gói về muộn sau khi đăng ký hết hạn'),
     (j, 'REFUND_CLEARING',  NULL, -p.amount, 'REFUND', p.id, 'Phải hoàn lại seller (LATE_SUBSCRIPTION_PAYMENT)');
    PERFORM flow.audit(NULL, 'LATE_SUBSCRIPTION_PAYMENT', 'subscriptions', r.id, jsonb_build_object('amount', p.amount));
    RETURN;
  END IF;
  UPDATE identity.subscriptions SET status = 'ACTIVE' WHERE id = r.id;
  INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
   (j,'GATEWAY_CLEARING',      NULL, p.amount,'SUBSCRIPTION',r.id,'Thu phí gói'),
   (j,'PLATFORM_SUBSCRIPTION', NULL,-p.amount,'SUBSCRIPTION',r.id,'Doanh thu phí gói');
  PERFORM flow.audit(NULL, 'SUBSCRIPTION_PAID', 'subscriptions', r.id, jsonb_build_object('amount', p.amount, 'to', r.period_to));
  PERFORM flow.refresh_seller_package(r.seller_id);
END $$;


ALTER FUNCTION flow.subscription_paid(p_sub uuid, p_txn text, p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: sys_user(); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.sys_user() RETURNS uuid
    LANGUAGE sql IMMUTABLE
    AS $$ SELECT '10000000-0000-0000-0000-0000000000ff'::uuid $$;


ALTER FUNCTION flow.sys_user() OWNER TO florabot;

--
-- Name: take_accessories(uuid, jsonb); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.take_accessories(p_kiosk uuid, p_items jsonb) RETURNS jsonb
    LANGUAGE plpgsql
    AS $$
DECLARE a jsonb; v record; v_out jsonb := '[]'; v_qty int;
BEGIN
  FOR a IN SELECT * FROM jsonb_array_elements(coalesce(p_items, '[]')) LOOP
    v_qty := (a->>'qty')::int;
    IF v_qty IS NULL OR v_qty <= 0 THEN RAISE EXCEPTION 'Số lượng phụ kiện không hợp lệ'; END IF;
    UPDATE kiosk_ops.accessories SET stock_quantity = stock_quantity - v_qty
     WHERE id = (a->>'id')::uuid AND kiosk_id = p_kiosk AND status = 'ACTIVE' AND stock_quantity >= v_qty
       AND seller_id IN (SELECT id FROM identity.sellers WHERE status = 'ACTIVE')
     RETURNING id, seller_id, name, price INTO v;
    IF NOT FOUND THEN RAISE EXCEPTION 'Phụ kiện % không đủ tồn kho tại kiosk hoặc seller đang tạm ngưng (cần %)', a->>'id', v_qty; END IF;
    v_out := v_out || jsonb_build_object('id', v.id, 'seller_id', v.seller_id, 'name', v.name, 'price', v.price, 'qty', v_qty);
  END LOOP;
  RETURN v_out;
END $$;


ALTER FUNCTION flow.take_accessories(p_kiosk uuid, p_items jsonb) OWNER TO florabot;

--
-- Name: tick(timestamp with time zone); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.tick(p_now timestamp with time zone) RETURNS timestamp with time zone
    LANGUAGE plpgsql
    AS $$
BEGIN
  IF p_now IS NOT NULL THEN PERFORM set_config('app.now', p_now::text, true); END IF;
  RETURN public.app_now();
END $$;


ALTER FUNCTION flow.tick(p_now timestamp with time zone) OWNER TO florabot;

--
-- Name: transition_staff_task(uuid, uuid, integer, text, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.transition_staff_task(p_id uuid, p_actor uuid, p_version integer, p_status text, p_report text) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
  t kiosk_ops.staff_tasks;
  v_actor_role text;
  v_actor_seller uuid;
BEGIN
  SELECT role, seller_id INTO v_actor_role, v_actor_seller
  FROM identity.users
  WHERE id = p_actor AND status = 'ACTIVE' FOR SHARE;
  
  IF v_actor_role IS NULL OR v_actor_role NOT IN ('ADMIN', 'OPERATIONS_MANAGER', 'SELLER', 'TECHNICIAN', 'SELLER_STAFF') THEN
    RAISE EXCEPTION 'Access denied' USING ERRCODE = '42501';
  END IF;

  SELECT * INTO t FROM kiosk_ops.staff_tasks WHERE id = p_id FOR UPDATE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
  END IF;

  -- Verify scope by role
  IF v_actor_role = 'TECHNICIAN' THEN
    IF t.assignee_id <> p_actor OR t.kind <> 'INCIDENT' THEN
      RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
    END IF;
  ELSIF v_actor_role = 'SELLER_STAFF' THEN
    IF t.assignee_id <> p_actor OR t.kind <> 'DELIVERY' OR t.seller_id IS DISTINCT FROM v_actor_seller THEN
      RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
    END IF;
  ELSIF v_actor_role = 'OPERATIONS_MANAGER' THEN
    IF t.kind <> 'INCIDENT' OR NOT EXISTS (SELECT 1 FROM kiosk_ops.manager_kiosks WHERE manager_id = p_actor AND kiosk_id = t.kiosk_id) THEN
      RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
    END IF;
  ELSIF v_actor_role = 'SELLER' THEN
    IF t.kind <> 'DELIVERY' OR t.seller_id IS DISTINCT FROM v_actor_seller THEN
      RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
    END IF;
  END IF;

  IF p_version IS DISTINCT FROM t.version THEN
    RAISE EXCEPTION 'Task changed; reload first' USING ERRCODE = '40001';
  END IF;

  -- Validate state transitions
  IF p_status IS NULL OR NOT (
    (v_actor_role IN ('TECHNICIAN', 'SELLER_STAFF') AND t.status = 'ASSIGNED' AND p_status = 'IN_PROGRESS') OR
    (v_actor_role IN ('TECHNICIAN', 'SELLER_STAFF') AND t.status = 'IN_PROGRESS' AND p_status = 'SUBMITTED') OR
    (v_actor_role IN ('ADMIN', 'OPERATIONS_MANAGER', 'SELLER') AND t.status = 'SUBMITTED' AND p_status IN ('COMPLETED', 'IN_PROGRESS')) OR
    (v_actor_role IN ('ADMIN', 'OPERATIONS_MANAGER', 'SELLER') AND t.status IN ('ASSIGNED', 'IN_PROGRESS', 'SUBMITTED') AND p_status = 'CANCELLED')
  ) THEN
    RAISE EXCEPTION 'Invalid task transition' USING ERRCODE = '22023';
  END IF;

  IF p_report IS NULL OR length(btrim(p_report)) NOT BETWEEN 10 AND 2000 THEN
    RAISE EXCEPTION 'Provide a report of 10 to 2000 characters' USING ERRCODE = '22023';
  END IF;

  UPDATE kiosk_ops.staff_tasks
  SET status = p_status, report = btrim(p_report), version = version + 1, updated_at = public.app_now()
  WHERE id = p_id;

  PERFORM flow.audit(p_actor, 'STAFF_TASK_UPDATED', 'staff_tasks', p_id,
    jsonb_build_object('from', t.status, 'to', p_status, 'report', btrim(p_report), 'version', t.version + 1));

  RETURN t.version + 1;
END $$;


ALTER FUNCTION flow.transition_staff_task(p_id uuid, p_actor uuid, p_version integer, p_status text, p_report text) OWNER TO florabot;

--
-- Name: trg_price_audit(); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.trg_price_audit() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
  IF NEW.price IS DISTINCT FROM OLD.price THEN
    PERFORM flow.audit(nullif(current_setting('app.actor', true), '')::uuid, 'PRODUCT_PRICE_CHANGED', 'flower_products', NEW.id,
                       jsonb_build_object('old', OLD.price, 'new', NEW.price));
  END IF;
  RETURN NEW;
END $$;


ALTER FUNCTION flow.trg_price_audit() OWNER TO florabot;

--
-- Name: update_artificial_product(uuid, uuid, uuid, text, text, bigint, text[], numeric, numeric, numeric); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.update_artificial_product(p_product uuid, p_seller uuid, p_actor uuid, p_name text, p_description text, p_price bigint, p_tags text[], p_length_cm numeric, p_width_cm numeric, p_height_cm numeric) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE
  v_name text := btrim(coalesce(p_name, ''));
  v_tag text;
  v_tags text[] := '{}';
  pr catalog.flower_products;
BEGIN
  -- Rejection of null or zero UUIDs
  IF p_product IS NULL OR p_product = '00000000-0000-0000-0000-000000000000'::uuid THEN
    RAISE EXCEPTION 'Mã sản phẩm không hợp lệ' USING ERRCODE = '22023';
  END IF;
  IF p_seller IS NULL OR p_seller = '00000000-0000-0000-0000-000000000000'::uuid
     OR p_actor IS NULL OR p_actor = '00000000-0000-0000-0000-000000000000'::uuid THEN
    RAISE EXCEPTION 'Mã shop hoặc người thao tác không hợp lệ' USING ERRCODE = '22023';
  END IF;

  -- Strict actor eligibility check: actor must be active SELLER belonging to p_seller
  PERFORM 1 FROM identity.users WHERE id = p_actor AND role = 'SELLER' AND seller_id = p_seller AND status = 'ACTIVE' FOR SHARE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Không có quyền thao tác' USING ERRCODE = '42501';
  END IF;

  -- Active seller shop check
  PERFORM 1 FROM identity.sellers WHERE id = p_seller AND status = 'ACTIVE' FOR SHARE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Shop không hoạt động' USING ERRCODE = '42501';
  END IF;

  -- Active subscription package check
  IF NOT flow.package_valid(p_seller) THEN
    RAISE EXCEPTION 'Shop cần gói thuê bao đang hoạt động để cập nhật sản phẩm' USING ERRCODE = '42501';
  END IF;

  -- Lock product for update and check ownership
  PERFORM pg_advisory_xact_lock(hashtextextended('product:' || p_product::text, 0));
  SELECT * INTO pr FROM catalog.flower_products WHERE id = p_product FOR UPDATE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Sản phẩm không tồn tại' USING ERRCODE = 'P0002';
  END IF;
  IF pr.seller_id <> p_seller THEN
    RAISE EXCEPTION 'Sản phẩm không thuộc shop' USING ERRCODE = 'P0002';
  END IF;

  -- Input validations
  IF length(v_name) < 1 OR length(v_name) > 120 THEN
    RAISE EXCEPTION 'Tên sản phẩm từ 1 đến 120 ký tự' USING ERRCODE = '22023';
  END IF;

  IF p_description IS NOT NULL AND length(p_description) > 2000 THEN
    RAISE EXCEPTION 'Mô tả tối đa 2000 ký tự' USING ERRCODE = '22023';
  END IF;

  IF p_price IS NULL OR p_price <= 0 THEN
    RAISE EXCEPTION 'Giá bán phải là số nguyên dương' USING ERRCODE = '22023';
  END IF;

  IF p_length_cm IS NULL OR p_length_cm < 0.01 OR p_length_cm > 1000 OR round(p_length_cm, 2) <> p_length_cm
     OR p_width_cm IS NULL OR p_width_cm < 0.01 OR p_width_cm > 1000 OR round(p_width_cm, 2) <> p_width_cm
     OR p_height_cm IS NULL OR p_height_cm < 0.01 OR p_height_cm > 1000 OR round(p_height_cm, 2) <> p_height_cm THEN
    RAISE EXCEPTION 'Kích thước phải từ 0.01 đến 1000 cm và tối đa 2 chữ số thập phân' USING ERRCODE = '22023';
  END IF;

  IF p_tags IS NULL THEN
    RAISE EXCEPTION 'Danh sách tags không được null' USING ERRCODE = '22023';
  END IF;

  IF cardinality(p_tags) > 20 THEN
    RAISE EXCEPTION 'Tối đa 20 tags' USING ERRCODE = '22023';
  END IF;

  FOREACH v_tag IN ARRAY p_tags LOOP
    IF v_tag IS NULL THEN
      RAISE EXCEPTION 'Tag không được là null' USING ERRCODE = '22023';
    END IF;
    v_tag := btrim(v_tag);
    IF length(v_tag) < 1 OR length(v_tag) > 64 THEN
      RAISE EXCEPTION 'Mỗi tag từ 1 đến 64 ký tự' USING ERRCODE = '22023';
    END IF;
    IF v_tag = ANY(v_tags) THEN
      RAISE EXCEPTION 'Tags không được trùng lặp' USING ERRCODE = '22023';
    END IF;
    v_tags := array_append(v_tags, v_tag);
  END LOOP;

  -- Conflict rule: dimension change conflicts (409) if live bouquets (STOCKED or HELD) exist
  IF (pr.length_cm IS DISTINCT FROM p_length_cm OR
      pr.width_cm IS DISTINCT FROM p_width_cm OR
      pr.height_cm IS DISTINCT FROM p_height_cm) THEN
    IF EXISTS (SELECT 1 FROM kiosk_ops.bouquets WHERE product_id = p_product AND status IN ('STOCKED', 'HELD')) THEN
      RAISE EXCEPTION 'Không thể thay đổi kích thước khi có bó hoa đang trong tủ' USING ERRCODE = '40001';
    END IF;
  END IF;

  -- Update metadata only (never change seller_id or status)
  UPDATE catalog.flower_products SET
    name = v_name,
    description = p_description,
    price = p_price,
    tags = v_tags,
    length_cm = p_length_cm,
    width_cm = p_width_cm,
    height_cm = p_height_cm,
    updated_at = public.app_now()
  WHERE id = p_product;

  PERFORM flow.audit(p_actor, 'PRODUCT_UPDATED', 'flower_products', p_product,
    jsonb_build_object('name', v_name, 'price', p_price,
      'dimensions', jsonb_build_object('length', p_length_cm, 'width', p_width_cm, 'height', p_height_cm)));

  RETURN p_product;
END $$;


ALTER FUNCTION flow.update_artificial_product(p_product uuid, p_seller uuid, p_actor uuid, p_name text, p_description text, p_price bigint, p_tags text[], p_length_cm numeric, p_width_cm numeric, p_height_cm numeric) OWNER TO florabot;

--
-- Name: update_product_price(uuid, uuid, bigint); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.update_product_price(p_product uuid, p_user uuid, p_price bigint) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
  PERFORM flow.check_catalog_editor(p_product, p_user);
  PERFORM set_config('app.actor', p_user::text, true);
  UPDATE catalog.flower_products p SET price = p_price
  WHERE p.id = p_product AND EXISTS (
    SELECT 1 FROM identity.users u WHERE u.id = p_user AND u.status = 'ACTIVE'
    AND (u.role = 'ADMIN' OR (u.role = 'SELLER' AND u.seller_id = p.seller_id))
  );
  IF NOT FOUND THEN RAISE EXCEPTION 'Kh??ng c?? quy???n s???a gi?? s???n ph???m %', p_product; END IF;
END $$;


ALTER FUNCTION flow.update_product_price(p_product uuid, p_user uuid, p_price bigint) OWNER TO florabot;

--
-- Name: web_accept(uuid, uuid, bigint, timestamp with time zone, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.web_accept(p_id uuid, p_customer uuid, p_price bigint, p_pickup timestamp with time zone, p_note text) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE w ordering.web_requests; v_order uuid:=public.uuid_v7(); v_checkout uuid:=public.uuid_v7();
BEGIN
  SELECT * INTO w FROM ordering.web_requests WHERE id=p_id AND customer_id=p_customer FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Khong tim thay yeu cau'; END IF;
  IF w.state='ORDERED' THEN RETURN w.order_id; END IF;
  IF w.quoted_price IS DISTINCT FROM p_price OR w.pickup_at IS DISTINCT FROM p_pickup OR w.quote_note IS DISTINCT FROM p_note THEN RAISE EXCEPTION 'B??o gi?? ???? thay ?????i. H??y c???p nh???t v?? ki???m tra l???i tr?????c khi x??c nh???n'; END IF;
  IF w.state<>'QUOTED' OR w.quote_expires_at<=public.app_now() THEN RAISE EXCEPTION 'Bao gia da het han hoac khong con hieu luc'; END IF;
  IF NOT flow.package_valid(w.seller_id) OR NOT EXISTS(SELECT 1 FROM identity.sellers WHERE id=w.seller_id AND status='ACTIVE') OR NOT EXISTS(SELECT 1 FROM catalog.flower_products WHERE id=w.product_id AND status='ACTIVE') OR NOT EXISTS(SELECT 1 FROM kiosk_ops.kiosks WHERE id=w.kiosk_id AND status NOT IN ('DISABLED','MAINTENANCE')) THEN RAISE EXCEPTION 'Shop tam ngung nhan don'; END IF;
  INSERT INTO ordering.orders(id,order_code,checkout_id,customer_id,seller_id,kiosk_id,subtotal,total_amount,tracking_token)
    VALUES(v_order,flow.new_order_code(),v_checkout,p_customer,w.seller_id,w.kiosk_id,w.quoted_price,w.quoted_price,flow.new_tracking_token());
  INSERT INTO payment.payments(kind,purpose,checkout_id,gateway,idempotency_key,amount,status)
    VALUES('CHARGE','ORDER_CHECKOUT',v_checkout,'PAYOS','web-'||p_id::text,w.quoted_price,'PENDING');
  UPDATE ordering.web_requests SET state='ORDERED',order_id=v_order,updated_at=public.app_now() WHERE id=p_id;
  PERFORM flow.audit(p_customer,'WEB_QUOTE_ACCEPTED','web_requests',p_id);
  RETURN v_order;
END $$;


ALTER FUNCTION flow.web_accept(p_id uuid, p_customer uuid, p_price bigint, p_pickup timestamp with time zone, p_note text) OWNER TO florabot;

--
-- Name: web_cancel(uuid, uuid, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.web_cancel(p_id uuid, p_actor uuid, p_reason text) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE w ordering.web_requests; o ordering.orders; v_ref uuid;
BEGIN
  SELECT * INTO w FROM ordering.web_requests WHERE id=p_id FOR UPDATE;
  IF NOT FOUND OR NOT (p_actor=flow.sys_user() OR w.customer_id=p_actor OR EXISTS(SELECT 1 FROM identity.users WHERE id=p_actor AND role='SELLER' AND seller_id=w.seller_id AND status='ACTIVE') OR EXISTS(SELECT 1 FROM identity.users WHERE id=p_actor AND role IN ('ADMIN','SYSTEM') AND status='ACTIVE')) THEN RAISE EXCEPTION 'Khong co quyen huy yeu cau'; END IF;
  IF length(btrim(p_reason))<5 OR length(p_reason)>1000 THEN RAISE EXCEPTION 'Ly do can tu 5 den 1000 ky tu'; END IF;
  IF w.state IN ('CANCELLED','REJECTED') THEN RETURN; END IF;
  IF w.order_id IS NOT NULL THEN
    PERFORM 1 FROM payment.payments WHERE checkout_id=(SELECT checkout_id FROM ordering.orders WHERE id=w.order_id) AND kind='CHARGE' FOR UPDATE;
    SELECT * INTO o FROM ordering.orders WHERE id=w.order_id FOR UPDATE;
    IF o.status='AWAITING_PAYMENT' THEN
      UPDATE payment.payments SET status='CANCELLED' WHERE checkout_id=o.checkout_id AND kind='CHARGE' AND status='PENDING';
      PERFORM flow.release_checkout(o.checkout_id,'CANCELLED');
    ELSIF o.status='PAID' THEN
      IF w.customer_id=p_actor THEN RAISE EXCEPTION 'Don da tra tien: lien he shop qua ho so de doi chieu truoc khi huy'; END IF;
      IF EXISTS(SELECT 1 FROM ordering.order_items WHERE order_id=o.id) THEN PERFORM flow.dispense_failed(o.id,p_reason);
      ELSE
        v_ref:=flow.create_refund(o.id,o.total_amount,p_reason,flow.sys_user(),'MANUAL');
        PERFORM flow.refund_allocation(v_ref,'REFUND_CLEARING',o.subtotal);
        INSERT INTO ordering.disputes(kind,order_id,kiosk_id,reason) VALUES('DISPENSE_FAILED',o.id,o.kiosk_id,p_reason);
        PERFORM flow.set_order_status(o.id,'DISPENSE_FAILED','SYSTEM',NULL,p_reason);
        PERFORM flow.audit(NULL,'DISPENSE_FAILED','orders',o.id,jsonb_build_object('refund',v_ref,'reason',p_reason));
      END IF;
    ELSIF o.status NOT IN ('EXPIRED','CANCELLED','DISPENSE_FAILED','REFUNDED') THEN RAISE EXCEPTION 'Khong the huy don dang mo cua hoac da nhan'; END IF;
  END IF;
  UPDATE ordering.web_requests SET state='CANCELLED',quote_note=p_reason,updated_at=public.app_now() WHERE id=p_id;
  PERFORM flow.audit(p_actor,'WEB_REQUEST_CANCELLED','web_requests',p_id);
END $$;


ALTER FUNCTION flow.web_cancel(p_id uuid, p_actor uuid, p_reason text) OWNER TO florabot;

--
-- Name: web_fulfill(uuid, uuid, uuid, uuid, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.web_fulfill(p_id uuid, p_seller uuid, p_actor uuid, p_slot uuid, p_qr text) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE w ordering.web_requests; o ordering.orders; s kiosk_ops.slots; b uuid;
BEGIN
  SELECT * INTO w FROM ordering.web_requests WHERE id=p_id AND seller_id=p_seller FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Khong co quyen xu ly yeu cau'; END IF;
  PERFORM flow.check_seller_user(p_actor,p_seller);
  SELECT * INTO o FROM ordering.orders WHERE id=w.order_id FOR UPDATE;
  IF w.kind<>'CUSTOM' OR o.status IS DISTINCT FROM 'PAID' THEN RAISE EXCEPTION 'Chi nap don rieng da thanh toan'; END IF;
  SELECT bouquet_id INTO b FROM ordering.order_items WHERE order_id=o.id AND item_type='BOUQUET';
  IF FOUND THEN RETURN b; END IF;
  IF public.app_now()<w.pickup_at-interval '2 hours' OR public.app_now()>=w.pickup_before THEN RAISE EXCEPTION 'Nap hoa trong 2 gio truoc hen va truoc han nhan'; END IF;
  SELECT * INTO s FROM kiosk_ops.slots WHERE id=p_slot AND current_seller_id=p_seller AND kiosk_id=w.kiosk_id FOR UPDATE;
  IF NOT FOUND OR s.status<>'RENTED_EMPTY' THEN RAISE EXCEPTION 'Can o trong cua shop tai dung tu nhan'; END IF;
  b:=flow.stock_bouquet(flow.new_batch(),w.product_id,p_slot,p_actor,p_qr);
  IF (SELECT sellable_until FROM kiosk_ops.bouquets WHERE id=b)<w.pickup_before THEN RAISE EXCEPTION 'Han tuoi cua hoa khong du den het lich nhan'; END IF;
  UPDATE kiosk_ops.bouquets SET status='SOLD',price_snapshot=o.total_amount WHERE id=b;
  UPDATE kiosk_ops.slots SET status='HELD',hold_order_id=o.id,hold_until=w.pickup_before,row_version=row_version+1 WHERE id=p_slot;
  INSERT INTO ordering.order_items(order_id,item_type,bouquet_id,slot_id,name_snapshot,quantity,unit_price,line_total)
    VALUES(o.id,'BOUQUET',b,p_slot,'Hoa dat rieng: '||(SELECT name FROM catalog.flower_products WHERE id=w.product_id),1,o.total_amount,o.total_amount);
  INSERT INTO kiosk_ops.inventory_logs(bouquet_id,slot_id,movement_type,order_id,performed_by) VALUES(b,p_slot,'SALE_OUT',o.id,p_actor);
  PERFORM flow.audit(p_actor,'WEB_ORDER_READY','orders',o.id,jsonb_build_object('request',p_id,'slot',p_slot));
  RETURN b;
END $$;


ALTER FUNCTION flow.web_fulfill(p_id uuid, p_seller uuid, p_actor uuid, p_slot uuid, p_qr text) OWNER TO florabot;

--
-- Name: web_quote(uuid, uuid, uuid, bigint, timestamp with time zone, text, boolean); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.web_quote(p_id uuid, p_seller uuid, p_actor uuid, p_price bigint, p_pickup timestamp with time zone, p_note text, p_reject boolean DEFAULT false) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE w ordering.web_requests;
BEGIN
  SELECT * INTO w FROM ordering.web_requests WHERE id=p_id AND seller_id=p_seller FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Khong co quyen xu ly yeu cau'; END IF;
  PERFORM flow.check_seller_user(p_actor,p_seller);
  IF w.state NOT IN ('REQUESTED','QUOTED') OR w.kind<>'CUSTOM' THEN RAISE EXCEPTION 'Yeu cau khong con cho bao gia'; END IF;
  IF length(btrim(p_note))<5 OR length(p_note)>1000 THEN RAISE EXCEPTION 'Ghi chu can tu 5 den 1000 ky tu'; END IF;
  IF p_reject THEN UPDATE ordering.web_requests SET state='REJECTED',quote_note=p_note,updated_at=public.app_now() WHERE id=p_id;
  ELSE
    IF p_price NOT BETWEEN 1000 AND 100000000 OR p_pickup<public.app_now()+interval '1 hour' OR p_pickup>public.app_now()+interval '30 days' THEN RAISE EXCEPTION 'Gia hoac lich nhan khong hop le'; END IF;
    UPDATE ordering.web_requests SET state='QUOTED',quoted_price=p_price,quote_note=p_note,pickup_at=p_pickup,
      pickup_before=p_pickup+make_interval(hours=>flow.cfg('web_pickup_hours')::int),
      quote_expires_at=least(p_pickup-interval '30 minutes',public.app_now()+make_interval(hours=>flow.cfg('web_quote_hours')::int)),updated_at=public.app_now() WHERE id=p_id;
  END IF;
  PERFORM flow.audit(p_actor,'WEB_REQUEST_QUOTED','web_requests',p_id);
END $$;


ALTER FUNCTION flow.web_quote(p_id uuid, p_seller uuid, p_actor uuid, p_price bigint, p_pickup timestamp with time zone, p_note text, p_reject boolean) OWNER TO florabot;

--
-- Name: web_request(uuid, uuid, text, uuid, uuid, uuid, timestamp with time zone, text); Type: FUNCTION; Schema: flow; Owner: florabot
--

CREATE FUNCTION flow.web_request(p_id uuid, p_customer uuid, p_kind text, p_product uuid, p_kiosk uuid, p_bouquet uuid, p_pickup timestamp with time zone, p_instructions text) RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE pr catalog.flower_products; w ordering.web_requests; v_checkout uuid; v_order uuid; v_end timestamptz;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('web-request:'||p_id::text,0));
  SELECT * INTO w FROM ordering.web_requests WHERE id=p_id;
  IF FOUND THEN
    IF w.request_payload IS NOT NULL AND w.request_payload IS DISTINCT FROM jsonb_build_object('bouquet',p_bouquet,'pickup',p_pickup) THEN RAISE EXCEPTION 'M?? y??u c???u ???? ???????c d??ng v???i th??ng tin kh??c'; END IF;
    IF w.customer_id<>p_customer OR w.product_id<>p_product OR w.kiosk_id<>p_kiosk OR w.kind<>p_kind OR w.instructions<>p_instructions THEN RAISE EXCEPTION 'Ma yeu cau da duoc su dung'; END IF;
    RETURN w.id;
  END IF;
  IF NOT EXISTS(SELECT 1 FROM identity.users WHERE id=p_customer AND role IN ('CUSTOMER','SELLER') AND status='ACTIVE') THEN RAISE EXCEPTION 'Tai khoan khong hop le'; END IF;
  SELECT * INTO pr FROM catalog.flower_products WHERE id=p_product AND status='ACTIVE';
  IF NOT FOUND OR NOT flow.package_valid(pr.seller_id) OR NOT EXISTS(SELECT 1 FROM identity.sellers WHERE id=pr.seller_id AND status='ACTIVE') THEN RAISE EXCEPTION 'Shop hoac mau hoa chua nhan don'; END IF;
  IF p_kind NOT IN ('STOCK','CUSTOM') OR length(p_instructions)>2000 OR NOT EXISTS(SELECT 1 FROM kiosk_ops.kiosks WHERE id=p_kiosk AND status NOT IN ('DISABLED','MAINTENANCE')) THEN RAISE EXCEPTION 'Thong tin dat hoa khong hop le'; END IF;
  IF NOT EXISTS(SELECT 1 FROM kiosk_ops.slots WHERE kiosk_id=p_kiosk AND current_seller_id=pr.seller_id AND status NOT IN ('FREE','PENDING_RELEASE')) THEN RAISE EXCEPTION 'Shop khong phuc vu tu nay'; END IF;
  IF p_kind='CUSTOM' THEN
    IF length(btrim(p_instructions))<10 OR p_pickup<public.app_now()+interval '2 hours' OR p_pickup>public.app_now()+interval '30 days' THEN RAISE EXCEPTION 'Mo ta it nhat 10 ky tu, hen nhan tu 2 gio den 30 ngay'; END IF;
    INSERT INTO ordering.web_requests(id,customer_id,seller_id,product_id,kiosk_id,kind,instructions,pickup_at,pickup_before,state)
    VALUES(p_id,p_customer,pr.seller_id,pr.id,p_kiosk,p_kind,p_instructions,p_pickup,p_pickup+make_interval(hours=>flow.cfg('web_pickup_hours')::int),'REQUESTED');
  ELSE
    IF NOT EXISTS(SELECT 1 FROM kiosk_ops.bouquets b JOIN kiosk_ops.slots s ON s.bouquet_id=b.id WHERE b.id=p_bouquet AND b.product_id=pr.id AND s.kiosk_id=p_kiosk) THEN RAISE EXCEPTION 'Bo hoa khong thuoc tu nay'; END IF;
    v_checkout:=flow.kiosk_checkout(p_kiosk,ARRAY[p_bouquet],p_customer);
    SELECT id INTO v_order FROM ordering.orders WHERE checkout_id=v_checkout;
    SELECT least(sellable_until,public.app_now()+make_interval(hours=>flow.cfg('web_pickup_hours')::int)) INTO v_end FROM kiosk_ops.bouquets WHERE id=p_bouquet;
    IF v_end<=public.app_now()+flow.cfg_min('hold_minutes') THEN RAISE EXCEPTION 'Hoa khong con du thoi gian de dat truoc'; END IF;
    INSERT INTO ordering.web_requests(id,customer_id,seller_id,product_id,kiosk_id,kind,instructions,pickup_at,pickup_before,quoted_price,state,order_id)
    VALUES(p_id,p_customer,pr.seller_id,pr.id,p_kiosk,p_kind,p_instructions,public.app_now(),v_end,(SELECT total_amount FROM ordering.orders WHERE id=v_order),'ORDERED',v_order);
  END IF;
  UPDATE ordering.web_requests SET request_payload=jsonb_build_object('bouquet',p_bouquet,'pickup',p_pickup) WHERE id=p_id;
  UPDATE ordering.web_requests SET display_snapshot=(SELECT jsonb_build_object('product',pr.name,'shop',s.shop_name,'kiosk',k.name,'address',k.address) FROM identity.sellers s CROSS JOIN kiosk_ops.kiosks k WHERE s.id=pr.seller_id AND k.id=p_kiosk) WHERE id=p_id;
  PERFORM flow.audit(p_customer,'WEB_REQUEST_CREATED','web_requests',p_id,jsonb_build_object('kind',p_kind));
  RETURN p_id;
END $$;


ALTER FUNCTION flow.web_request(p_id uuid, p_customer uuid, p_kind text, p_product uuid, p_kiosk uuid, p_bouquet uuid, p_pickup timestamp with time zone, p_instructions text) OWNER TO florabot;

--
-- Name: check_manager_kiosk(); Type: FUNCTION; Schema: kiosk_ops; Owner: florabot
--

CREATE FUNCTION kiosk_ops.check_manager_kiosk() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM identity.users WHERE id = NEW.manager_id AND role = 'OPERATIONS_MANAGER' AND status = 'ACTIVE') THEN
    RAISE EXCEPTION 'User must be an active operations manager' USING ERRCODE = '22023';
  END IF;
  RETURN NEW;
END $$;


ALTER FUNCTION kiosk_ops.check_manager_kiosk() OWNER TO florabot;

--
-- Name: set_artificial_bouquet_no_expiry(); Type: FUNCTION; Schema: kiosk_ops; Owner: florabot
--

CREATE FUNCTION kiosk_ops.set_artificial_bouquet_no_expiry() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
  v_kind text;
BEGIN
  SELECT inventory_kind INTO v_kind FROM catalog.flower_products WHERE id = NEW.product_id FOR SHARE;
  IF v_kind = 'ARTIFICIAL' THEN
    NEW.sellable_until := 'infinity'::timestamptz;
  END IF;
  RETURN NEW;
END $$;


ALTER FUNCTION kiosk_ops.set_artificial_bouquet_no_expiry() OWNER TO florabot;

--
-- Name: check_journal_balanced(); Type: FUNCTION; Schema: payment; Owner: florabot
--

CREATE FUNCTION payment.check_journal_balanced() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE s bigint;
BEGIN
  SELECT sum(amount) INTO s FROM payment.ledger_entries WHERE journal_id = NEW.journal_id;
  IF s <> 0 THEN RAISE EXCEPTION 'Bút toán % lệch % đồng', NEW.journal_id, s; END IF;
  RETURN NULL;
END $$;


ALTER FUNCTION payment.check_journal_balanced() OWNER TO florabot;

--
-- Name: check_refund_total(); Type: FUNCTION; Schema: payment; Owner: florabot
--

CREATE FUNCTION payment.check_refund_total() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
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


ALTER FUNCTION payment.check_refund_total() OWNER TO florabot;

--
-- Name: app_clock(); Type: FUNCTION; Schema: public; Owner: florabot
--

CREATE FUNCTION public.app_clock() RETURNS timestamp with time zone
    LANGUAGE sql
    AS $$ SELECT coalesce(nullif(current_setting('app.now', true), '')::timestamptz, clock_timestamp()) $$;


ALTER FUNCTION public.app_clock() OWNER TO florabot;

--
-- Name: app_now(); Type: FUNCTION; Schema: public; Owner: florabot
--

CREATE FUNCTION public.app_now() RETURNS timestamp with time zone
    LANGUAGE sql
    AS $$ SELECT coalesce(nullif(current_setting('app.now', true), '')::timestamptz, now()) $$;


ALTER FUNCTION public.app_now() OWNER TO florabot;

--
-- Name: forbid_change(); Type: FUNCTION; Schema: public; Owner: florabot
--

CREATE FUNCTION public.forbid_change() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN RAISE EXCEPTION '% là bảng chỉ ghi thêm (append-only)', TG_TABLE_NAME; END $$;


ALTER FUNCTION public.forbid_change() OWNER TO florabot;

--
-- Name: guard_transition(); Type: FUNCTION; Schema: public; Owner: florabot
--

CREATE FUNCTION public.guard_transition() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
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


ALTER FUNCTION public.guard_transition() OWNER TO florabot;

--
-- Name: touch_updated_at(); Type: FUNCTION; Schema: public; Owner: florabot
--

CREATE FUNCTION public.touch_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN NEW.updated_at := public.app_now(); RETURN NEW; END $$;


ALTER FUNCTION public.touch_updated_at() OWNER TO florabot;

--
-- Name: uuid_v7(); Type: FUNCTION; Schema: public; Owner: florabot
--

CREATE FUNCTION public.uuid_v7() RETURNS uuid
    LANGUAGE plpgsql
    AS $$
    DECLARE ts bigint := floor(extract(epoch FROM clock_timestamp()) * 1000); b bytea := gen_random_bytes(16);
    BEGIN
      b := overlay(b PLACING substring(int8send(ts) FROM 3) FROM 1 FOR 6);
      b := set_byte(b, 6, (get_byte(b, 6) & 15) | 112);   -- version 7
      b := set_byte(b, 8, (get_byte(b, 8) & 63) | 128);   -- variant 10
      RETURN encode(b, 'hex')::uuid;
    END $$;


ALTER FUNCTION public.uuid_v7() OWNER TO florabot;

SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: gift_surveys; Type: TABLE; Schema: ai; Owner: florabot
--

CREATE TABLE ai.gift_surveys (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    session_id text NOT NULL,
    customer_id uuid,
    kiosk_id uuid NOT NULL,
    recipient_type text NOT NULL,
    age_group text,
    occasion text NOT NULL,
    message_tone text,
    budget_max bigint,
    results jsonb DEFAULT '[]'::jsonb NOT NULL,
    model_version text,
    source text,
    latency_ms integer,
    selected_product_id uuid,
    purchased_order_id uuid,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    updated_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    CONSTRAINT gift_surveys_age_group_check CHECK ((age_group = ANY (ARRAY['UNDER_18'::text, '18_25'::text, '26_40'::text, '41_60'::text, 'OVER_60'::text]))),
    CONSTRAINT gift_surveys_budget_max_check CHECK ((budget_max > 0)),
    CONSTRAINT gift_surveys_latency_ms_check CHECK ((latency_ms >= 0)),
    CONSTRAINT gift_surveys_message_tone_check CHECK ((message_tone = ANY (ARRAY['ROMANTIC'::text, 'WARM'::text, 'FORMAL'::text, 'FUNNY'::text]))),
    CONSTRAINT gift_surveys_occasion_check CHECK ((occasion = ANY (ARRAY['BIRTHDAY'::text, 'ANNIVERSARY'::text, 'VALENTINE'::text, 'WOMENS_DAY'::text, 'TEACHERS_DAY'::text, 'MOTHERS_DAY'::text, 'APOLOGY'::text, 'CONGRATS'::text, 'SYMPATHY'::text, 'OTHER'::text]))),
    CONSTRAINT gift_surveys_recipient_type_check CHECK ((recipient_type = ANY (ARRAY['LOVER'::text, 'MOTHER'::text, 'FATHER'::text, 'FRIEND'::text, 'COLLEAGUE'::text, 'TEACHER'::text, 'BOSS'::text, 'OTHER'::text]))),
    CONSTRAINT gift_surveys_results_check CHECK ((jsonb_typeof(results) = 'array'::text)),
    CONSTRAINT gift_surveys_source_check CHECK ((source = ANY (ARRAY['LLM'::text, 'FALLBACK'::text, 'CACHE'::text])))
);


ALTER TABLE ai.gift_surveys OWNER TO florabot;

--
-- Name: published; Type: TABLE; Schema: cap; Owner: florabot
--

CREATE TABLE cap.published (
    "Id" bigint NOT NULL,
    "Version" character varying(20) NOT NULL,
    "Name" character varying(200) NOT NULL,
    "Content" text,
    "Retries" integer NOT NULL,
    "Added" timestamp without time zone NOT NULL,
    "ExpiresAt" timestamp without time zone,
    "StatusName" character varying(50) NOT NULL
);


ALTER TABLE cap.published OWNER TO florabot;

--
-- Name: received; Type: TABLE; Schema: cap; Owner: florabot
--

CREATE TABLE cap.received (
    "Id" bigint NOT NULL,
    "Version" character varying(20) NOT NULL,
    "Name" character varying(200) NOT NULL,
    "Group" character varying(200),
    "Content" text,
    "Retries" integer NOT NULL,
    "Added" timestamp without time zone NOT NULL,
    "ExpiresAt" timestamp without time zone,
    "StatusName" character varying(50) NOT NULL
);


ALTER TABLE cap.received OWNER TO florabot;

--
-- Name: flower_products; Type: TABLE; Schema: catalog; Owner: florabot
--

CREATE TABLE catalog.flower_products (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    seller_id uuid NOT NULL,
    name text NOT NULL,
    description text,
    price bigint NOT NULL,
    tags text[] DEFAULT '{}'::text[] NOT NULL,
    shelf_life_hours integer NOT NULL,
    status text DEFAULT 'DRAFT'::text NOT NULL,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    updated_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    inventory_kind text DEFAULT 'LEGACY_FRESH'::text NOT NULL,
    length_cm numeric(6,2),
    width_cm numeric(6,2),
    height_cm numeric(6,2),
    CONSTRAINT flower_products_artificial_dimensions_check CHECK (((inventory_kind <> 'ARTIFICIAL'::text) OR ((length_cm IS NOT NULL) AND (width_cm IS NOT NULL) AND (height_cm IS NOT NULL)))),
    CONSTRAINT flower_products_height_cm_check CHECK (((height_cm IS NULL) OR ((height_cm >= 0.01) AND (height_cm <= (1000)::numeric)))),
    CONSTRAINT flower_products_inventory_kind_check CHECK ((inventory_kind = ANY (ARRAY['LEGACY_FRESH'::text, 'ARTIFICIAL'::text]))),
    CONSTRAINT flower_products_length_cm_check CHECK (((length_cm IS NULL) OR ((length_cm >= 0.01) AND (length_cm <= (1000)::numeric)))),
    CONSTRAINT flower_products_price_check CHECK ((price > 0)),
    CONSTRAINT flower_products_shelf_life_hours_check CHECK ((shelf_life_hours > 0)),
    CONSTRAINT flower_products_status_check CHECK ((status = ANY (ARRAY['DRAFT'::text, 'ACTIVE'::text, 'INACTIVE'::text]))),
    CONSTRAINT flower_products_width_cm_check CHECK (((width_cm IS NULL) OR ((width_cm >= 0.01) AND (width_cm <= (1000)::numeric))))
);


ALTER TABLE catalog.flower_products OWNER TO florabot;

--
-- Name: sellers; Type: TABLE; Schema: identity; Owner: florabot
--

CREATE TABLE identity.sellers (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    shop_name text NOT NULL,
    phone text NOT NULL,
    address text,
    status text DEFAULT 'PENDING'::text NOT NULL,
    package_id uuid,
    package_expires_at date,
    brand_tone text,
    bank_name text,
    bank_account_no_enc text,
    bank_holder text,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    updated_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    CONSTRAINT sellers_check CHECK (((status <> ALL (ARRAY['APPROVED'::text, 'ACTIVE'::text, 'PAST_DUE'::text])) OR (package_id IS NOT NULL))),
    CONSTRAINT sellers_check1 CHECK (((status <> ALL (ARRAY['ACTIVE'::text, 'PAST_DUE'::text])) OR (package_expires_at IS NOT NULL))),
    CONSTRAINT sellers_status_check CHECK ((status = ANY (ARRAY['PENDING'::text, 'APPROVED'::text, 'ACTIVE'::text, 'PAST_DUE'::text, 'SUSPENDED'::text, 'CLOSED'::text])))
);


ALTER TABLE identity.sellers OWNER TO florabot;

--
-- Name: subscription_packages; Type: TABLE; Schema: identity; Owner: florabot
--

CREATE TABLE identity.subscription_packages (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    name text NOT NULL,
    monthly_fee bigint NOT NULL,
    max_slots integer NOT NULL,
    status text DEFAULT 'ACTIVE'::text NOT NULL,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    updated_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    CONSTRAINT subscription_packages_max_slots_check CHECK ((max_slots > 0)),
    CONSTRAINT subscription_packages_monthly_fee_check CHECK ((monthly_fee > 0)),
    CONSTRAINT subscription_packages_status_check CHECK ((status = ANY (ARRAY['ACTIVE'::text, 'INACTIVE'::text])))
);


ALTER TABLE identity.subscription_packages OWNER TO florabot;

--
-- Name: subscriptions; Type: TABLE; Schema: identity; Owner: florabot
--

CREATE TABLE identity.subscriptions (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    seller_id uuid NOT NULL,
    package_id uuid NOT NULL,
    period_from date NOT NULL,
    period_to date NOT NULL,
    price bigint NOT NULL,
    status text DEFAULT 'PENDING_PAYMENT'::text NOT NULL,
    payment_id uuid,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    updated_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    CONSTRAINT subscriptions_check CHECK ((period_to > period_from)),
    CONSTRAINT subscriptions_check1 CHECK (((status <> 'ACTIVE'::text) OR (payment_id IS NOT NULL))),
    CONSTRAINT subscriptions_price_check CHECK ((price > 0)),
    CONSTRAINT subscriptions_status_check CHECK ((status = ANY (ARRAY['PENDING_PAYMENT'::text, 'ACTIVE'::text, 'EXPIRED'::text, 'CANCELLED'::text])))
);


ALTER TABLE identity.subscriptions OWNER TO florabot;

--
-- Name: users; Type: TABLE; Schema: identity; Owner: florabot
--

CREATE TABLE identity.users (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    email text,
    phone text,
    password_hash text,
    full_name text NOT NULL,
    role text NOT NULL,
    seller_id uuid,
    status text DEFAULT 'ACTIVE'::text NOT NULL,
    loyalty_points bigint DEFAULT 0 NOT NULL,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    updated_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    CONSTRAINT users_check CHECK (((email IS NOT NULL) OR (phone IS NOT NULL))),
    CONSTRAINT users_check2 CHECK (((role = 'CUSTOMER'::text) OR (password_hash IS NOT NULL))),
    CONSTRAINT users_check3 CHECK (((role = ANY (ARRAY['CUSTOMER'::text, 'SELLER'::text])) OR (loyalty_points = 0))),
    CONSTRAINT users_loyalty_points_check CHECK ((loyalty_points >= 0)),
    CONSTRAINT users_role_check CHECK ((role = ANY (ARRAY['ADMIN'::text, 'STAFF'::text, 'SELLER'::text, 'CUSTOMER'::text, 'SYSTEM'::text]))),
    CONSTRAINT users_seller_check CHECK (((role = ANY (ARRAY['SELLER'::text, 'SELLER_STAFF'::text])) = (seller_id IS NOT NULL))),
    CONSTRAINT users_status_check CHECK ((status = ANY (ARRAY['ACTIVE'::text, 'LOCKED'::text, 'DISABLED'::text])))
);


ALTER TABLE identity.users OWNER TO florabot;

--
-- Name: accessories; Type: TABLE; Schema: kiosk_ops; Owner: florabot
--

CREATE TABLE kiosk_ops.accessories (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    seller_id uuid NOT NULL,
    kiosk_id uuid NOT NULL,
    name text NOT NULL,
    price bigint NOT NULL,
    stock_quantity integer DEFAULT 0 NOT NULL,
    status text DEFAULT 'ACTIVE'::text NOT NULL,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    updated_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    CONSTRAINT accessories_price_check CHECK ((price > 0)),
    CONSTRAINT accessories_status_check CHECK ((status = ANY (ARRAY['ACTIVE'::text, 'INACTIVE'::text]))),
    CONSTRAINT accessories_stock_quantity_check CHECK ((stock_quantity >= 0))
);


ALTER TABLE kiosk_ops.accessories OWNER TO florabot;

--
-- Name: bouquets; Type: TABLE; Schema: kiosk_ops; Owner: florabot
--

CREATE TABLE kiosk_ops.bouquets (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    product_id uuid NOT NULL,
    seller_id uuid NOT NULL,
    qr_code text NOT NULL,
    price_snapshot bigint NOT NULL,
    status text DEFAULT 'STOCKED'::text NOT NULL,
    sellable_until timestamp with time zone NOT NULL,
    stocked_at timestamp with time zone NOT NULL,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    updated_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    CONSTRAINT bouquets_check CHECK ((sellable_until > stocked_at)),
    CONSTRAINT bouquets_price_snapshot_check CHECK ((price_snapshot > 0)),
    CONSTRAINT bouquets_status_check CHECK ((status = ANY (ARRAY['STOCKED'::text, 'HELD'::text, 'SOLD'::text, 'PICKED_UP'::text, 'EXPIRED'::text, 'DAMAGED'::text, 'RETURNED'::text])))
);


ALTER TABLE kiosk_ops.bouquets OWNER TO florabot;

--
-- Name: inventory_logs; Type: TABLE; Schema: kiosk_ops; Owner: florabot
--

CREATE TABLE kiosk_ops.inventory_logs (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    seq bigint NOT NULL,
    bouquet_id uuid NOT NULL,
    slot_id uuid NOT NULL,
    movement_type text NOT NULL,
    batch_id uuid,
    order_id uuid,
    performed_by uuid,
    reason text,
    created_at timestamp with time zone DEFAULT public.app_clock() NOT NULL,
    CONSTRAINT inventory_logs_check CHECK (((movement_type <> 'STOCK_IN'::text) OR (batch_id IS NOT NULL))),
    CONSTRAINT inventory_logs_check1 CHECK (((movement_type <> ALL (ARRAY['HOLD'::text, 'RELEASE'::text, 'SALE_OUT'::text])) OR (order_id IS NOT NULL))),
    CONSTRAINT inventory_logs_movement_type_check CHECK ((movement_type = ANY (ARRAY['STOCK_IN'::text, 'HOLD'::text, 'RELEASE'::text, 'SALE_OUT'::text, 'EXPIRE_OUT'::text, 'DAMAGE'::text, 'RETURN_SELLER'::text])))
);


ALTER TABLE kiosk_ops.inventory_logs OWNER TO florabot;

--
-- Name: inventory_logs_seq_seq; Type: SEQUENCE; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE kiosk_ops.inventory_logs ALTER COLUMN seq ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME kiosk_ops.inventory_logs_seq_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: kiosks; Type: TABLE; Schema: kiosk_ops; Owner: florabot
--

CREATE TABLE kiosk_ops.kiosks (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    code text NOT NULL,
    name text NOT NULL,
    address text NOT NULL,
    region text NOT NULL,
    hardware_id text NOT NULL,
    mqtt_client_id text NOT NULL,
    status text DEFAULT 'OFFLINE'::text NOT NULL,
    last_heartbeat_at timestamp with time zone,
    api_key_hash text,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    updated_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    CONSTRAINT kiosks_status_check CHECK ((status = ANY (ARRAY['ONLINE'::text, 'OFFLINE'::text, 'MAINTENANCE'::text, 'DISABLED'::text])))
);


ALTER TABLE kiosk_ops.kiosks OWNER TO florabot;

--
-- Name: manager_kiosks; Type: TABLE; Schema: kiosk_ops; Owner: florabot
--

CREATE TABLE kiosk_ops.manager_kiosks (
    manager_id uuid NOT NULL,
    kiosk_id uuid NOT NULL,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL
);


ALTER TABLE kiosk_ops.manager_kiosks OWNER TO florabot;

--
-- Name: mqtt_dispatches; Type: TABLE; Schema: kiosk_ops; Owner: florabot
--

CREATE TABLE kiosk_ops.mqtt_dispatches (
    cmd_id uuid NOT NULL,
    delivery_id uuid NOT NULL,
    attempted_at timestamp with time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    next_attempt_at timestamp with time zone NOT NULL,
    pubacked_at timestamp with time zone,
    CONSTRAINT mqtt_dispatches_check CHECK ((next_attempt_at > attempted_at))
);


ALTER TABLE kiosk_ops.mqtt_dispatches OWNER TO florabot;

--
-- Name: mqtt_inbox; Type: TABLE; Schema: kiosk_ops; Owner: florabot
--

CREATE TABLE kiosk_ops.mqtt_inbox (
    event_id uuid NOT NULL,
    kiosk_id uuid NOT NULL,
    cmd_id uuid,
    event text NOT NULL,
    payload_hash text NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    received_at timestamp with time zone DEFAULT CURRENT_TIMESTAMP NOT NULL,
    processed_at timestamp with time zone,
    disposition text DEFAULT 'PENDING'::text NOT NULL,
    CONSTRAINT mqtt_inbox_check CHECK (((event = 'HEARTBEAT'::text) = (cmd_id IS NULL))),
    CONSTRAINT mqtt_inbox_check1 CHECK (((disposition = 'PENDING'::text) = (processed_at IS NULL))),
    CONSTRAINT mqtt_inbox_disposition_check CHECK ((disposition = ANY (ARRAY['PENDING'::text, 'APPLIED'::text, 'IGNORED'::text, 'REJECTED'::text]))),
    CONSTRAINT mqtt_inbox_event_check CHECK ((event = ANY (ARRAY['ACK'::text, 'OPENED'::text, 'CLOSED'::text, 'FAILED'::text, 'HEARTBEAT'::text])))
);


ALTER TABLE kiosk_ops.mqtt_inbox OWNER TO florabot;

--
-- Name: slot_assignments; Type: TABLE; Schema: kiosk_ops; Owner: florabot
--

CREATE TABLE kiosk_ops.slot_assignments (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    slot_id uuid NOT NULL,
    seller_id uuid NOT NULL,
    assigned_by uuid NOT NULL,
    assigned_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    status text DEFAULT 'ACTIVE'::text NOT NULL,
    released_at timestamp with time zone,
    release_reason text,
    CONSTRAINT slot_assignments_check CHECK (((status = 'RELEASED'::text) = (released_at IS NOT NULL))),
    CONSTRAINT slot_assignments_status_check CHECK ((status = ANY (ARRAY['ACTIVE'::text, 'RELEASED'::text])))
);


ALTER TABLE kiosk_ops.slot_assignments OWNER TO florabot;

--
-- Name: slots; Type: TABLE; Schema: kiosk_ops; Owner: florabot
--

CREATE TABLE kiosk_ops.slots (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    kiosk_id uuid NOT NULL,
    slot_code text NOT NULL,
    status text DEFAULT 'FREE'::text NOT NULL,
    current_seller_id uuid,
    bouquet_id uuid,
    hold_until timestamp with time zone,
    hold_order_id uuid,
    row_version integer DEFAULT 0 NOT NULL,
    relay_channel smallint NOT NULL,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    updated_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    CONSTRAINT slots_check CHECK (((status <> ALL (ARRAY['STOCKED'::text, 'HELD'::text, 'PENDING_REMOVAL'::text, 'PENDING_RELEASE'::text])) OR (bouquet_id IS NOT NULL))),
    CONSTRAINT slots_check1 CHECK (((status <> ALL (ARRAY['FREE'::text, 'RENTED_EMPTY'::text])) OR (bouquet_id IS NULL))),
    CONSTRAINT slots_check2 CHECK (((status <> 'HELD'::text) OR ((hold_until IS NOT NULL) AND (hold_order_id IS NOT NULL)))),
    CONSTRAINT slots_check3 CHECK (((status = ANY (ARRAY['HELD'::text, 'FAULT'::text])) OR ((hold_until IS NULL) AND (hold_order_id IS NULL)))),
    CONSTRAINT slots_check4 CHECK (((status = 'FREE'::text) OR (status = ANY (ARRAY['LOCKED'::text, 'FAULT'::text])) OR (current_seller_id IS NOT NULL))),
    CONSTRAINT slots_relay_channel_check CHECK (((relay_channel >= 0) AND (relay_channel <= 63))),
    CONSTRAINT slots_status_check CHECK ((status = ANY (ARRAY['FREE'::text, 'RENTED_EMPTY'::text, 'STOCKED'::text, 'HELD'::text, 'LOCKED'::text, 'FAULT'::text, 'PENDING_REMOVAL'::text, 'PENDING_RELEASE'::text])))
);


ALTER TABLE kiosk_ops.slots OWNER TO florabot;

--
-- Name: staff_incident_actions; Type: TABLE; Schema: kiosk_ops; Owner: florabot
--

CREATE TABLE kiosk_ops.staff_incident_actions (
    id uuid NOT NULL,
    task_id uuid NOT NULL,
    actor_id uuid NOT NULL,
    incident_id uuid NOT NULL,
    request_version integer NOT NULL,
    result_version integer NOT NULL,
    report text NOT NULL,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL
);


ALTER TABLE kiosk_ops.staff_incident_actions OWNER TO florabot;

--
-- Name: staff_stock_actions; Type: TABLE; Schema: kiosk_ops; Owner: florabot
--

CREATE TABLE kiosk_ops.staff_stock_actions (
    id uuid NOT NULL,
    task_id uuid NOT NULL,
    actor_id uuid NOT NULL,
    product_id uuid NOT NULL,
    slot_id uuid NOT NULL,
    qr_code text NOT NULL,
    bouquet_id uuid NOT NULL,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL
);


ALTER TABLE kiosk_ops.staff_stock_actions OWNER TO florabot;

--
-- Name: staff_tasks; Type: TABLE; Schema: kiosk_ops; Owner: florabot
--

CREATE TABLE kiosk_ops.staff_tasks (
    id uuid NOT NULL,
    kind text NOT NULL,
    assignee_id uuid NOT NULL,
    assigned_by uuid NOT NULL,
    kiosk_id uuid NOT NULL,
    seller_id uuid,
    incident_id uuid,
    instructions text NOT NULL,
    status text DEFAULT 'ASSIGNED'::text NOT NULL,
    report text,
    version integer DEFAULT 1 NOT NULL,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    updated_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    CONSTRAINT staff_tasks_check CHECK ((((kind = 'INCIDENT'::text) AND (incident_id IS NOT NULL) AND (seller_id IS NULL)) OR ((kind = 'DELIVERY'::text) AND (seller_id IS NOT NULL) AND (incident_id IS NULL)))),
    CONSTRAINT staff_tasks_instructions_check CHECK (((length(btrim(instructions)) >= 10) AND (length(btrim(instructions)) <= 2000))),
    CONSTRAINT staff_tasks_kind_check CHECK ((kind = ANY (ARRAY['INCIDENT'::text, 'DELIVERY'::text]))),
    CONSTRAINT staff_tasks_status_check CHECK ((status = ANY (ARRAY['ASSIGNED'::text, 'IN_PROGRESS'::text, 'SUBMITTED'::text, 'COMPLETED'::text, 'CANCELLED'::text])))
);


ALTER TABLE kiosk_ops.staff_tasks OWNER TO florabot;

--
-- Name: system_settings; Type: TABLE; Schema: kiosk_ops; Owner: florabot
--

CREATE TABLE kiosk_ops.system_settings (
    key text NOT NULL,
    value jsonb NOT NULL,
    description text,
    updated_by uuid,
    updated_at timestamp with time zone DEFAULT public.app_now() NOT NULL
);


ALTER TABLE kiosk_ops.system_settings OWNER TO florabot;

--
-- Name: unlock_tokens; Type: TABLE; Schema: kiosk_ops; Owner: florabot
--

CREATE TABLE kiosk_ops.unlock_tokens (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    kiosk_id uuid NOT NULL,
    slot_id uuid NOT NULL,
    purpose text NOT NULL,
    order_id uuid,
    issued_to_user_id uuid,
    token_hash text NOT NULL,
    cmd_id uuid DEFAULT public.uuid_v7() NOT NULL,
    status text DEFAULT 'ISSUED'::text NOT NULL,
    attempts integer DEFAULT 0 NOT NULL,
    issued_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    door_opened_at timestamp with time zone,
    door_closed_at timestamp with time zone,
    CONSTRAINT unlock_tokens_attempts_check CHECK (((attempts >= 0) AND (attempts <= 5))),
    CONSTRAINT unlock_tokens_check CHECK ((expires_at > issued_at)),
    CONSTRAINT unlock_tokens_check1 CHECK (((purpose = 'CUSTOMER_PICKUP'::text) = (order_id IS NOT NULL))),
    CONSTRAINT unlock_tokens_check2 CHECK (((purpose <> 'SELLER_ACCESS'::text) OR (issued_to_user_id IS NOT NULL))),
    CONSTRAINT unlock_tokens_check3 CHECK (((door_closed_at IS NULL) OR (door_opened_at IS NOT NULL))),
    CONSTRAINT unlock_tokens_purpose_check CHECK ((purpose = ANY (ARRAY['CUSTOMER_PICKUP'::text, 'SELLER_ACCESS'::text]))),
    CONSTRAINT unlock_tokens_status_check CHECK ((status = ANY (ARRAY['ISSUED'::text, 'SENT'::text, 'ACKED'::text, 'OPENED'::text, 'CLOSED'::text, 'EXPIRED'::text, 'FAILED'::text, 'REVOKED'::text])))
);


ALTER TABLE kiosk_ops.unlock_tokens OWNER TO florabot;

--
-- Name: attachments; Type: TABLE; Schema: notify; Owner: florabot
--

CREATE TABLE notify.attachments (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    owner_service text NOT NULL,
    owner_type text NOT NULL,
    owner_id uuid NOT NULL,
    file_url text NOT NULL,
    mime_type text NOT NULL,
    size_bytes bigint NOT NULL,
    sha256 text NOT NULL,
    phase text NOT NULL,
    uploaded_by uuid,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    CONSTRAINT attachments_mime_type_check CHECK ((mime_type = ANY (ARRAY['image/jpeg'::text, 'image/png'::text, 'image/webp'::text, 'application/pdf'::text]))),
    CONSTRAINT attachments_phase_check CHECK ((phase = ANY (ARRAY['PRODUCT'::text, 'EVIDENCE'::text, 'RETURN'::text, 'PROOF'::text]))),
    CONSTRAINT attachments_size_bytes_check CHECK (((size_bytes > 0) AND (size_bytes <= 10485760)))
);


ALTER TABLE notify.attachments OWNER TO florabot;

--
-- Name: audit_logs; Type: TABLE; Schema: notify; Owner: florabot
--

CREATE TABLE notify.audit_logs (
    id bigint NOT NULL,
    actor_type text NOT NULL,
    actor_id uuid,
    action text NOT NULL,
    entity_type text NOT NULL,
    entity_id uuid,
    payload jsonb,
    created_at timestamp with time zone DEFAULT public.app_clock() NOT NULL,
    CONSTRAINT audit_logs_actor_type_check CHECK ((actor_type = ANY (ARRAY['USER'::text, 'KIOSK'::text, 'SYSTEM'::text])))
);


ALTER TABLE notify.audit_logs OWNER TO florabot;

--
-- Name: audit_logs_id_seq; Type: SEQUENCE; Schema: notify; Owner: florabot
--

ALTER TABLE notify.audit_logs ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME notify.audit_logs_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: customer_seller_chat_images; Type: TABLE; Schema: ordering; Owner: florabot
--

CREATE TABLE ordering.customer_seller_chat_images (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    message_id uuid NOT NULL,
    chat_id uuid NOT NULL,
    file_url text NOT NULL,
    mime_type text NOT NULL,
    size_bytes bigint NOT NULL,
    sha256 text NOT NULL,
    uploaded_by uuid NOT NULL,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    CONSTRAINT customer_seller_chat_images_mime_type_check CHECK ((mime_type = ANY (ARRAY['image/jpeg'::text, 'image/png'::text, 'image/webp'::text]))),
    CONSTRAINT customer_seller_chat_images_size_bytes_check CHECK (((size_bytes > 0) AND (size_bytes <= 5242880)))
);


ALTER TABLE ordering.customer_seller_chat_images OWNER TO florabot;

--
-- Name: customer_seller_chat_messages; Type: TABLE; Schema: ordering; Owner: florabot
--

CREATE TABLE ordering.customer_seller_chat_messages (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    chat_id uuid NOT NULL,
    sender_user_id uuid NOT NULL,
    content text,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    CONSTRAINT customer_seller_chat_messages_content_check CHECK (((content IS NULL) OR ((length(content) <= 2000) AND (length(btrim(content)) > 0))))
);


ALTER TABLE ordering.customer_seller_chat_messages OWNER TO florabot;

--
-- Name: customer_seller_chat_products; Type: TABLE; Schema: ordering; Owner: florabot
--

CREATE TABLE ordering.customer_seller_chat_products (
    chat_id uuid NOT NULL,
    product_id uuid NOT NULL,
    product_name_snapshot text NOT NULL,
    first_opened_at timestamp with time zone DEFAULT public.app_now() NOT NULL
);


ALTER TABLE ordering.customer_seller_chat_products OWNER TO florabot;

--
-- Name: customer_seller_chats; Type: TABLE; Schema: ordering; Owner: florabot
--

CREATE TABLE ordering.customer_seller_chats (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    customer_id uuid NOT NULL,
    seller_id uuid NOT NULL,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    last_message_at timestamp with time zone
);


ALTER TABLE ordering.customer_seller_chats OWNER TO florabot;

--
-- Name: disputes; Type: TABLE; Schema: ordering; Owner: florabot
--

CREATE TABLE ordering.disputes (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    kind text NOT NULL,
    order_id uuid,
    kiosk_id uuid NOT NULL,
    slot_id uuid,
    reason text NOT NULL,
    reported_by uuid,
    status text DEFAULT 'OPEN'::text NOT NULL,
    decision text,
    refund_amount bigint,
    decided_by uuid,
    resolved_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    updated_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    CONSTRAINT disputes_check CHECK (((kind = 'DEVICE_FAULT'::text) OR (order_id IS NOT NULL))),
    CONSTRAINT disputes_check1 CHECK (((status = 'RESOLVED_REFUND'::text) = (refund_amount IS NOT NULL))),
    CONSTRAINT disputes_check2 CHECK (((status <> ALL (ARRAY['RESOLVED_REFUND'::text, 'RESOLVED_REJECT'::text, 'RESOLVED_FIXED'::text])) OR ((decided_by IS NOT NULL) AND (resolved_at IS NOT NULL) AND (decision IS NOT NULL)))),
    CONSTRAINT disputes_kind_check CHECK ((kind = ANY (ARRAY['COMPLAINT'::text, 'DISPENSE_FAILED'::text, 'DEVICE_FAULT'::text]))),
    CONSTRAINT disputes_refund_amount_check CHECK ((refund_amount > 0)),
    CONSTRAINT disputes_status_check CHECK ((status = ANY (ARRAY['OPEN'::text, 'RESOLVED_REFUND'::text, 'RESOLVED_REJECT'::text, 'RESOLVED_FIXED'::text, 'CANCELLED'::text])))
);


ALTER TABLE ordering.disputes OWNER TO florabot;

--
-- Name: order_items; Type: TABLE; Schema: ordering; Owner: florabot
--

CREATE TABLE ordering.order_items (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    order_id uuid NOT NULL,
    item_type text NOT NULL,
    bouquet_id uuid,
    accessory_id uuid,
    slot_id uuid,
    name_snapshot text NOT NULL,
    quantity integer NOT NULL,
    unit_price bigint NOT NULL,
    line_total bigint NOT NULL,
    line_status text DEFAULT 'ACTIVE'::text NOT NULL,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    CONSTRAINT order_items_check CHECK ((line_total = (quantity * unit_price))),
    CONSTRAINT order_items_check1 CHECK ((((item_type = 'BOUQUET'::text) AND (bouquet_id IS NOT NULL) AND (accessory_id IS NULL) AND (quantity = 1) AND (slot_id IS NOT NULL)) OR ((item_type = 'ACCESSORY'::text) AND (accessory_id IS NOT NULL) AND (bouquet_id IS NULL)))),
    CONSTRAINT order_items_item_type_check CHECK ((item_type = ANY (ARRAY['BOUQUET'::text, 'ACCESSORY'::text]))),
    CONSTRAINT order_items_line_status_check CHECK ((line_status = ANY (ARRAY['ACTIVE'::text, 'CANCELLED'::text, 'REFUNDED'::text]))),
    CONSTRAINT order_items_quantity_check CHECK ((quantity > 0)),
    CONSTRAINT order_items_unit_price_check CHECK ((unit_price >= 0))
);


ALTER TABLE ordering.order_items OWNER TO florabot;

--
-- Name: orders; Type: TABLE; Schema: ordering; Owner: florabot
--

CREATE TABLE ordering.orders (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    order_code text NOT NULL,
    checkout_id uuid NOT NULL,
    customer_id uuid,
    seller_id uuid NOT NULL,
    kiosk_id uuid NOT NULL,
    status text DEFAULT 'AWAITING_PAYMENT'::text NOT NULL,
    subtotal bigint NOT NULL,
    discount_amount bigint DEFAULT 0 NOT NULL,
    total_amount bigint NOT NULL,
    points_redeemed bigint DEFAULT 0 NOT NULL,
    points_earned bigint DEFAULT 0 NOT NULL,
    tracking_token text NOT NULL,
    ecard_content text,
    completed_at timestamp with time zone,
    refund_bank_name text,
    refund_bank_account_enc text,
    refund_bank_holder text,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    updated_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    CONSTRAINT orders_check CHECK ((((refund_bank_name IS NULL) = (refund_bank_account_enc IS NULL)) AND ((refund_bank_name IS NULL) = (refund_bank_holder IS NULL)))),
    CONSTRAINT orders_check1 CHECK ((total_amount = (subtotal - discount_amount))),
    CONSTRAINT orders_check2 CHECK ((points_redeemed = discount_amount)),
    CONSTRAINT orders_check3 CHECK (((customer_id IS NOT NULL) OR ((points_redeemed = 0) AND (points_earned = 0)))),
    CONSTRAINT orders_check4 CHECK (((status <> ALL (ARRAY['COMPLETED'::text, 'DISPUTED'::text])) OR (completed_at IS NOT NULL))),
    CONSTRAINT orders_discount_amount_check CHECK ((discount_amount >= 0)),
    CONSTRAINT orders_points_earned_check CHECK ((points_earned >= 0)),
    CONSTRAINT orders_points_redeemed_check CHECK ((points_redeemed >= 0)),
    CONSTRAINT orders_status_check CHECK ((status = ANY (ARRAY['AWAITING_PAYMENT'::text, 'PAID'::text, 'DISPENSING'::text, 'COMPLETED'::text, 'DISPUTED'::text, 'REFUNDED'::text, 'DISPENSE_FAILED'::text, 'EXPIRED'::text, 'CANCELLED'::text]))),
    CONSTRAINT orders_subtotal_check CHECK ((subtotal >= 0)),
    CONSTRAINT orders_total_amount_check CHECK ((total_amount >= 0))
);


ALTER TABLE ordering.orders OWNER TO florabot;

--
-- Name: web_requests; Type: TABLE; Schema: ordering; Owner: florabot
--

CREATE TABLE ordering.web_requests (
    id uuid NOT NULL,
    customer_id uuid NOT NULL,
    seller_id uuid NOT NULL,
    product_id uuid NOT NULL,
    kiosk_id uuid NOT NULL,
    kind text NOT NULL,
    instructions text NOT NULL,
    pickup_at timestamp with time zone NOT NULL,
    pickup_before timestamp with time zone NOT NULL,
    quoted_price bigint,
    quote_note text,
    quote_expires_at timestamp with time zone,
    state text NOT NULL,
    order_id uuid,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    updated_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    request_payload jsonb,
    display_snapshot jsonb DEFAULT '{}'::jsonb NOT NULL,
    CONSTRAINT web_requests_check CHECK ((pickup_before > pickup_at)),
    CONSTRAINT web_requests_instructions_check CHECK ((length(instructions) <= 2000)),
    CONSTRAINT web_requests_kind_check CHECK ((kind = ANY (ARRAY['STOCK'::text, 'CUSTOM'::text]))),
    CONSTRAINT web_requests_quoted_price_check CHECK (((quoted_price >= 1000) AND (quoted_price <= 100000000))),
    CONSTRAINT web_requests_state_check CHECK ((state = ANY (ARRAY['REQUESTED'::text, 'QUOTED'::text, 'ORDERED'::text, 'REJECTED'::text, 'CANCELLED'::text])))
);


ALTER TABLE ordering.web_requests OWNER TO florabot;

--
-- Name: gateway_order_code_seq; Type: SEQUENCE; Schema: payment; Owner: florabot
--

CREATE SEQUENCE payment.gateway_order_code_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    MAXVALUE 9007199254740991
    CACHE 1;


ALTER SEQUENCE payment.gateway_order_code_seq OWNER TO florabot;

--
-- Name: gateway_orders; Type: TABLE; Schema: payment; Owner: florabot
--

CREATE TABLE payment.gateway_orders (
    payment_id uuid NOT NULL,
    order_code bigint DEFAULT nextval('payment.gateway_order_code_seq'::regclass) NOT NULL,
    checkout_url text,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    CONSTRAINT gateway_orders_order_code_check CHECK (((order_code >= 1) AND (order_code <= '9007199254740991'::bigint)))
);


ALTER TABLE payment.gateway_orders OWNER TO florabot;

--
-- Name: ledger_entries; Type: TABLE; Schema: payment; Owner: florabot
--

CREATE TABLE payment.ledger_entries (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    journal_id uuid NOT NULL,
    account text NOT NULL,
    seller_id uuid,
    amount bigint NOT NULL,
    ref_type text NOT NULL,
    ref_id uuid NOT NULL,
    memo text,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    CONSTRAINT ledger_entries_account_check CHECK ((account = ANY (ARRAY['GATEWAY_CLEARING'::text, 'SELLER_PENDING'::text, 'SELLER_AVAILABLE'::text, 'PLATFORM_SUBSCRIPTION'::text, 'PLATFORM_LOYALTY'::text, 'REFUND_CLEARING'::text]))),
    CONSTRAINT ledger_entries_amount_check CHECK ((amount <> 0)),
    CONSTRAINT ledger_entries_check CHECK (((account ~~ 'SELLER_%'::text) = (seller_id IS NOT NULL))),
    CONSTRAINT ledger_entries_ref_type_check CHECK ((ref_type = ANY (ARRAY['ORDER'::text, 'REFUND'::text, 'REFUND_PAYOUT'::text, 'SUBSCRIPTION'::text, 'WITHDRAWAL'::text, 'SETTLEMENT'::text])))
);


ALTER TABLE payment.ledger_entries OWNER TO florabot;

--
-- Name: payments; Type: TABLE; Schema: payment; Owner: florabot
--

CREATE TABLE payment.payments (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    kind text NOT NULL,
    purpose text NOT NULL,
    checkout_id uuid,
    order_id uuid,
    subscription_id uuid,
    parent_payment_id uuid,
    gateway text NOT NULL,
    gateway_txn_id text,
    idempotency_key text NOT NULL,
    amount bigint NOT NULL,
    status text DEFAULT 'PENDING'::text NOT NULL,
    approved_by uuid,
    reason text,
    raw_payload jsonb,
    paid_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    updated_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    paid_by uuid,
    CONSTRAINT payments_amount_check CHECK ((amount > 0)),
    CONSTRAINT payments_check CHECK (((kind = 'REFUND'::text) = (parent_payment_id IS NOT NULL))),
    CONSTRAINT payments_check1 CHECK (((kind <> 'REFUND'::text) OR (approved_by IS NOT NULL))),
    CONSTRAINT payments_check2 CHECK (((kind <> 'REFUND'::text) OR (purpose <> 'ORDER_CHECKOUT'::text) OR (order_id IS NOT NULL))),
    CONSTRAINT payments_check3 CHECK (((purpose <> 'ORDER_CHECKOUT'::text) OR (checkout_id IS NOT NULL))),
    CONSTRAINT payments_check4 CHECK (((purpose <> 'SUBSCRIPTION'::text) OR (subscription_id IS NOT NULL))),
    CONSTRAINT payments_check5 CHECK (((status = 'SUCCEEDED'::text) = (paid_at IS NOT NULL))),
    CONSTRAINT payments_gateway_check CHECK ((gateway = ANY (ARRAY['PAYOS'::text, 'MANUAL'::text]))),
    CONSTRAINT payments_kind_check CHECK ((kind = ANY (ARRAY['CHARGE'::text, 'REFUND'::text]))),
    CONSTRAINT payments_purpose_check CHECK ((purpose = ANY (ARRAY['ORDER_CHECKOUT'::text, 'SUBSCRIPTION'::text]))),
    CONSTRAINT payments_status_check CHECK ((status = ANY (ARRAY['PENDING'::text, 'SUCCEEDED'::text, 'FAILED'::text, 'CANCELLED'::text, 'EXPIRED'::text])))
);


ALTER TABLE payment.payments OWNER TO florabot;

--
-- Name: v_seller_balance; Type: VIEW; Schema: payment; Owner: florabot
--

CREATE VIEW payment.v_seller_balance AS
 SELECT seller_id,
    COALESCE((- sum(amount) FILTER (WHERE (account = 'SELLER_PENDING'::text))), (0)::numeric) AS pending_balance,
    COALESCE((- sum(amount) FILTER (WHERE (account = 'SELLER_AVAILABLE'::text))), (0)::numeric) AS available_balance,
    GREATEST(COALESCE(sum(amount) FILTER (WHERE (account = 'SELLER_AVAILABLE'::text)), (0)::numeric), (0)::numeric) AS debt
   FROM payment.ledger_entries
  WHERE (seller_id IS NOT NULL)
  GROUP BY seller_id;


ALTER VIEW payment.v_seller_balance OWNER TO florabot;

--
-- Name: withdrawal_requests; Type: TABLE; Schema: payment; Owner: florabot
--

CREATE TABLE payment.withdrawal_requests (
    id uuid DEFAULT public.uuid_v7() NOT NULL,
    seller_id uuid NOT NULL,
    amount bigint NOT NULL,
    status text DEFAULT 'PENDING'::text NOT NULL,
    bank_name text NOT NULL,
    bank_account_no_enc text NOT NULL,
    bank_holder text NOT NULL,
    approved_by uuid,
    reject_reason text,
    paid_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    updated_at timestamp with time zone DEFAULT public.app_now() NOT NULL,
    paid_by uuid,
    approved_at timestamp with time zone,
    CONSTRAINT withdrawal_requests_amount_check CHECK ((amount > 0)),
    CONSTRAINT withdrawal_requests_check CHECK (((status = 'REJECTED'::text) = (reject_reason IS NOT NULL))),
    CONSTRAINT withdrawal_requests_check1 CHECK (((status <> ALL (ARRAY['APPROVED'::text, 'PAID'::text])) OR (approved_by IS NOT NULL))),
    CONSTRAINT withdrawal_requests_check2 CHECK (((status = 'PAID'::text) = (paid_at IS NOT NULL))),
    CONSTRAINT withdrawal_requests_status_check CHECK ((status = ANY (ARRAY['PENDING'::text, 'APPROVED'::text, 'REJECTED'::text, 'PAID'::text, 'CANCELLED'::text])))
);


ALTER TABLE payment.withdrawal_requests OWNER TO florabot;

--
-- Name: v_expiring_bouquets; Type: VIEW; Schema: screen; Owner: florabot
--

CREATE VIEW screen.v_expiring_bouquets AS
 SELECT b.seller_id,
    k.code AS kiosk,
    sl.slot_code,
    b.id AS bouquet_id,
    fp.name,
    b.sellable_until,
    (b.sellable_until - public.app_now()) AS con_lai
   FROM (((kiosk_ops.bouquets b
     JOIN kiosk_ops.slots sl ON ((sl.bouquet_id = b.id)))
     JOIN kiosk_ops.kiosks k ON ((k.id = sl.kiosk_id)))
     JOIN catalog.flower_products fp ON ((fp.id = b.product_id)))
  WHERE ((b.status = 'STOCKED'::text) AND (b.sellable_until > public.app_now()) AND (b.sellable_until <= (public.app_now() + flow.cfg_hour('expiry_warn_hours'::text))));


ALTER VIEW screen.v_expiring_bouquets OWNER TO florabot;

--
-- Name: v_admin_queue; Type: VIEW; Schema: screen; Owner: florabot
--

CREATE VIEW screen.v_admin_queue AS
 SELECT 'Rút tiền chờ duyệt'::text AS hang_cho,
    count(*) AS so_luong
   FROM payment.withdrawal_requests
  WHERE (withdrawal_requests.status = 'PENDING'::text)
UNION ALL
 SELECT 'Khiếu nại đang mở'::text AS hang_cho,
    count(*) AS so_luong
   FROM ordering.disputes
  WHERE ((disputes.kind = 'COMPLAINT'::text) AND (disputes.status = 'OPEN'::text))
UNION ALL
 SELECT 'Sự cố thiết bị đang mở'::text AS hang_cho,
    count(*) AS so_luong
   FROM ordering.disputes
  WHERE ((disputes.kind = ANY (ARRAY['DEVICE_FAULT'::text, 'DISPENSE_FAILED'::text])) AND (disputes.status = 'OPEN'::text))
UNION ALL
 SELECT 'Seller chờ duyệt'::text AS hang_cho,
    count(*) AS so_luong
   FROM identity.sellers
  WHERE (sellers.status = 'PENDING'::text)
UNION ALL
 SELECT 'Seller quá hạn gói'::text AS hang_cho,
    count(*) AS so_luong
   FROM identity.sellers
  WHERE (sellers.status = 'PAST_DUE'::text)
UNION ALL
 SELECT 'Đơn đang nhả hàng'::text AS hang_cho,
    count(*) AS so_luong
   FROM ordering.orders
  WHERE (orders.status = 'DISPENSING'::text)
UNION ALL
 SELECT 'Bó sắp hết hạn'::text AS hang_cho,
    count(*) AS so_luong
   FROM screen.v_expiring_bouquets
UNION ALL
 SELECT 'Lệnh hoàn chờ chi'::text AS hang_cho,
    count(*) AS so_luong
   FROM payment.payments
  WHERE ((payments.kind = 'REFUND'::text) AND (payments.status = 'PENDING'::text));


ALTER VIEW screen.v_admin_queue OWNER TO florabot;

--
-- Name: v_ai_effectiveness; Type: VIEW; Schema: screen; Owner: florabot
--

CREATE VIEW screen.v_ai_effectiveness AS
 SELECT kiosk_id,
    source,
    count(*) AS so_khao_sat,
    count(purchased_order_id) AS so_mua,
    round(((100.0 * (count(purchased_order_id))::numeric) / (count(*))::numeric), 1) AS ty_le_mua,
    round(avg(latency_ms)) AS latency_tb_ms
   FROM ai.gift_surveys
  GROUP BY kiosk_id, source;


ALTER VIEW screen.v_ai_effectiveness OWNER TO florabot;

--
-- Name: v_customer_history; Type: VIEW; Schema: screen; Owner: florabot
--

CREATE VIEW screen.v_customer_history AS
 SELECT o.customer_id,
    o.order_code,
    o.created_at,
    o.status,
    o.total_amount,
    o.points_earned,
    o.points_redeemed,
    s.shop_name,
    ( SELECT string_agg(i.name_snapshot, ', '::text) AS string_agg
           FROM ordering.order_items i
          WHERE (i.order_id = o.id)) AS hang
   FROM (ordering.orders o
     JOIN identity.sellers s ON ((s.id = o.seller_id)))
  WHERE (o.customer_id IS NOT NULL);


ALTER VIEW screen.v_customer_history OWNER TO florabot;

--
-- Name: v_kiosk_catalog; Type: VIEW; Schema: screen; Owner: florabot
--

CREATE VIEW screen.v_kiosk_catalog AS
 SELECT k.id AS kiosk_id,
    k.code AS kiosk,
    sl.slot_code,
    b.id AS bouquet_id,
    fp.id AS product_id,
    fp.name,
    fp.tags,
    b.price_snapshot AS gia,
    b.sellable_until,
    s.shop_name
   FROM ((((kiosk_ops.slots sl
     JOIN kiosk_ops.kiosks k ON ((k.id = sl.kiosk_id)))
     JOIN kiosk_ops.bouquets b ON ((b.id = sl.bouquet_id)))
     JOIN catalog.flower_products fp ON ((fp.id = b.product_id)))
     JOIN identity.sellers s ON ((s.id = b.seller_id)))
  WHERE ((sl.status = 'STOCKED'::text) AND (b.status = 'STOCKED'::text) AND (b.sellable_until > public.app_now()) AND (s.status = 'ACTIVE'::text) AND (s.package_expires_at > (public.app_now())::date));


ALTER VIEW screen.v_kiosk_catalog OWNER TO florabot;

--
-- Name: v_platform_revenue; Type: VIEW; Schema: screen; Owner: florabot
--

CREATE VIEW screen.v_platform_revenue AS
 SELECT (date_trunc('month'::text, created_at))::date AS thang,
    (- sum(amount)) AS doanh_thu_phi_goi
   FROM payment.ledger_entries
  WHERE (account = 'PLATFORM_SUBSCRIPTION'::text)
  GROUP BY ((date_trunc('month'::text, created_at))::date);


ALTER VIEW screen.v_platform_revenue OWNER TO florabot;

--
-- Name: v_refund_queue; Type: VIEW; Schema: screen; Owner: florabot
--

CREATE VIEW screen.v_refund_queue AS
 SELECT p.id AS refund_id,
    p.created_at,
    p.amount,
    p.reason,
    p.purpose,
    o.order_code,
    (o.customer_id IS NOT NULL) AS khach_dang_nhap,
    s.shop_name,
    (o.refund_bank_name IS NOT NULL) AS da_co_stk,
    o.refund_bank_name,
    o.refund_bank_holder,
        CASE
            WHEN (p.purpose = 'SUBSCRIPTION'::text) THEN 'Hoàn seller (STK seller)'::text
            WHEN (o.refund_bank_name IS NOT NULL) THEN 'Sẵn sàng chi'::text
            ELSE 'Chờ khách khai STK trên e-receipt'::text
        END AS trang_thai,
    (EXTRACT(epoch FROM (public.app_now() - p.created_at)) / (3600)::numeric) AS cho_gio
   FROM ((payment.payments p
     LEFT JOIN ordering.orders o ON ((o.id = p.order_id)))
     LEFT JOIN identity.sellers s ON ((s.id = o.seller_id)))
  WHERE ((p.kind = 'REFUND'::text) AND (p.status = 'PENDING'::text));


ALTER VIEW screen.v_refund_queue OWNER TO florabot;

--
-- Name: v_revenue_daily; Type: VIEW; Schema: screen; Owner: florabot
--

CREATE VIEW screen.v_revenue_daily AS
 SELECT ((p.paid_at AT TIME ZONE 'Asia/Ho_Chi_Minh'::text))::date AS ngay,
    k.code AS kiosk,
    count(DISTINCT o.id) AS so_don,
    sum(o.total_amount) AS gmv,
    sum(o.discount_amount) AS giam_do_diem
   FROM ((payment.payments p
     JOIN ordering.orders o ON ((o.checkout_id = p.checkout_id)))
     JOIN kiosk_ops.kiosks k ON ((k.id = o.kiosk_id)))
  WHERE ((p.kind = 'CHARGE'::text) AND (p.purpose = 'ORDER_CHECKOUT'::text) AND (p.status = 'SUCCEEDED'::text) AND (o.status <> ALL (ARRAY['EXPIRED'::text, 'CANCELLED'::text])))
  GROUP BY (((p.paid_at AT TIME ZONE 'Asia/Ho_Chi_Minh'::text))::date), k.code;


ALTER VIEW screen.v_revenue_daily OWNER TO florabot;

--
-- Name: v_seller_wallet; Type: VIEW; Schema: screen; Owner: florabot
--

CREATE VIEW screen.v_seller_wallet AS
 SELECT s.shop_name,
    l.created_at,
    l.account,
    l.amount,
    l.ref_type,
    l.memo
   FROM (payment.ledger_entries l
     JOIN identity.sellers s ON ((s.id = l.seller_id)));


ALTER VIEW screen.v_seller_wallet OWNER TO florabot;

--
-- Name: v_web_catalog; Type: VIEW; Schema: screen; Owner: florabot
--

CREATE VIEW screen.v_web_catalog AS
 SELECT p.id AS product_id,
    p.seller_id,
    s.shop_name,
    p.name,
    p.description,
    p.price,
    k.id AS kiosk_id,
    k.name AS kiosk_name,
    k.address,
    ( SELECT a.file_url
           FROM notify.attachments a
          WHERE ((a.owner_service = 'catalog'::text) AND (a.owner_type = 'flower_product'::text) AND (a.owner_id = p.id))
          ORDER BY a.created_at
         LIMIT 1) AS photo,
    ARRAY( SELECT b.id
           FROM (kiosk_ops.bouquets b
             JOIN kiosk_ops.slots sl ON ((sl.bouquet_id = b.id)))
          WHERE ((b.product_id = p.id) AND (sl.kiosk_id = k.id) AND (sl.status = 'STOCKED'::text) AND (b.status = 'STOCKED'::text) AND (b.sellable_until > (public.app_now() + flow.cfg_min('hold_minutes'::text))) AND flow.slot_sellable(sl.id, s.id))
          ORDER BY b.sellable_until, b.id) AS bouquets
   FROM ((catalog.flower_products p
     JOIN identity.sellers s ON ((s.id = p.seller_id)))
     JOIN kiosk_ops.kiosks k ON ((k.status <> ALL (ARRAY['DISABLED'::text, 'MAINTENANCE'::text]))))
  WHERE ((p.status = 'ACTIVE'::text) AND (s.status = 'ACTIVE'::text) AND flow.package_valid(s.id) AND (EXISTS ( SELECT 1
           FROM kiosk_ops.slots sl
          WHERE ((sl.kiosk_id = k.id) AND (sl.current_seller_id = s.id) AND flow.slot_sellable(sl.id, s.id)))));


ALTER VIEW screen.v_web_catalog OWNER TO florabot;

--
-- Data for Name: gift_surveys; Type: TABLE DATA; Schema: ai; Owner: florabot
--

COPY ai.gift_surveys (id, session_id, customer_id, kiosk_id, recipient_type, age_group, occasion, message_tone, budget_max, results, model_version, source, latency_ms, selected_product_id, purchased_order_id, created_at, updated_at) FROM stdin;
\.


--
-- Data for Name: published; Type: TABLE DATA; Schema: cap; Owner: florabot
--

COPY cap.published ("Id", "Version", "Name", "Content", "Retries", "Added", "ExpiresAt", "StatusName") FROM stdin;
9009260044351062017	v1	florabot.seller.past-due.v1	{"Headers":{"cap-callback-name":null,"cap-msg-id":"9009260044351062017","cap-corr-id":"9009260044351062017","cap-corr-seq":"0","cap-msg-name":"florabot.seller.past-due.v1","cap-msg-type":"SellerPastDue","cap-senttime":"10/14/2026 12:57:18"},"Value":{"seller_id":"20000000-0000-0000-0000-000000000001","package_expires_at":"2026-11-13"}}	0	2026-10-14 12:57:18.279583	2026-10-15 12:57:18.354346	Succeeded
9009260047345295361	v1	florabot.seller.email-requested.v1	{"Headers":{"cap-callback-name":null,"cap-msg-id":"9009260047345295361","cap-corr-id":"9009260047345295361","cap-corr-seq":"0","cap-msg-name":"florabot.seller.email-requested.v1","cap-msg-type":"SellerEmailRequested","cap-senttime":"10/14/2026 13:53:36"},"Value":{"seller_id":"01a1391e-bcfb-7176-87a9-12d981d60264","kind":"approval","subscription_id":null,"expires_on":null}}	0	2026-10-14 13:53:36.055956	2026-10-15 13:53:36.164537	Succeeded
9009260063086874625	v1	florabot.seller.past-due.v1	{"Headers":{"cap-callback-name":null,"cap-msg-id":"9009260063086874625","cap-corr-id":"9009260063086874625","cap-corr-seq":"0","cap-msg-name":"florabot.seller.past-due.v1","cap-msg-type":"SellerPastDue","cap-senttime":"10/14/2026 14:13:32"},"Value":{"seller_id":"20000000-0000-0000-0000-000000000001","package_expires_at":"2026-11-13"}}	0	2026-10-14 14:13:32.848711	2026-10-15 14:13:32.889361	Succeeded
\.


--
-- Data for Name: received; Type: TABLE DATA; Schema: cap; Owner: florabot
--

COPY cap.received ("Id", "Version", "Name", "Group", "Content", "Retries", "Added", "ExpiresAt", "StatusName") FROM stdin;
9009260044351062018	v1	florabot.seller.past-due.v1	florabot.florabot.v1	{"Headers":{"cap-callback-name":null,"cap-corr-id":"9009260044351062017","cap-corr-seq":"0","cap-msg-id":"9009260044351062017","cap-msg-name":"florabot.seller.past-due.v1","cap-msg-type":"SellerPastDue","cap-senttime":"10/14/2026 12:57:18","cap-msg-group":"florabot.florabot.v1","cap-exec-instance-id":"DESKTOP-AV8GIN1"},"Value":{"seller_id":"20000000-0000-0000-0000-000000000001","package_expires_at":"2026-11-13"}}	0	2026-10-14 12:57:18.517407	2026-10-15 12:57:18.589346	Succeeded
9009260047345295362	v1	florabot.seller.email-requested.v1	florabot.florabot.v1	{"Headers":{"cap-callback-name":null,"cap-corr-id":"9009260047345295361","cap-corr-seq":"0","cap-msg-id":"9009260047345295361","cap-msg-name":"florabot.seller.email-requested.v1","cap-msg-type":"SellerEmailRequested","cap-senttime":"10/14/2026 13:53:36","cap-msg-group":"florabot.florabot.v1","cap-exec-instance-id":"DESKTOP-AV8GIN1"},"Value":{"seller_id":"01a1391e-bcfb-7176-87a9-12d981d60264","kind":"approval","subscription_id":null,"expires_on":null}}	0	2026-10-14 13:53:36.224499	2026-10-15 13:53:45.786959	Succeeded
9009260063086874626	v1	florabot.seller.past-due.v1	florabot.florabot.v1	{"Headers":{"cap-callback-name":null,"cap-corr-id":"9009260063086874625","cap-corr-seq":"0","cap-msg-id":"9009260063086874625","cap-msg-name":"florabot.seller.past-due.v1","cap-msg-type":"SellerPastDue","cap-senttime":"10/14/2026 14:13:32","cap-msg-group":"florabot.florabot.v1","cap-exec-instance-id":"DESKTOP-AV8GIN1"},"Value":{"seller_id":"20000000-0000-0000-0000-000000000001","package_expires_at":"2026-11-13"}}	0	2026-10-14 14:13:32.919378	2026-10-15 14:13:32.958452	Succeeded
\.


--
-- Data for Name: flower_products; Type: TABLE DATA; Schema: catalog; Owner: florabot
--

COPY catalog.flower_products (id, seller_id, name, description, price, tags, shelf_life_hours, status, created_at, updated_at, inventory_kind, length_cm, width_cm, height_cm) FROM stdin;
30000000-0000-0000-0000-000000000001	20000000-0000-0000-0000-000000000001	Hồng đỏ 10 bông	Hồng Đà Lạt bó giấy kraft — tình yêu trọn vẹn	350000	{LOVER,VALENTINE,ANNIVERSARY,ROMANTIC,RED}	72	ACTIVE	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00	LEGACY_FRESH	\N	\N	\N
30000000-0000-0000-0000-000000000002	20000000-0000-0000-0000-000000000001	Cúc họa mi	Bó nhỏ tông trắng — trong trẻo, biết ơn	250000	{FRIEND,TEACHER,TEACHERS_DAY,WARM,WHITE}	48	ACTIVE	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00	LEGACY_FRESH	\N	\N	\N
30000000-0000-0000-0000-000000000003	20000000-0000-0000-0000-000000000002	Hướng dương 3 bông	Hướng dương kèm baby — niềm tin và chúc mừng	300000	{FRIEND,CONGRATS,COLLEAGUE,BIRTHDAY,YELLOW}	72	ACTIVE	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00	LEGACY_FRESH	\N	\N	\N
30000000-0000-0000-0000-000000000004	20000000-0000-0000-0000-000000000002	Lan hồ điệp mini	Chậu lan 1 cành, sang trọng — kính trọng	600000	{BOSS,MOTHER,FORMAL,PURPLE}	240	ACTIVE	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00	LEGACY_FRESH	\N	\N	\N
30000000-0000-0000-0000-000000000005	20000000-0000-0000-0000-000000000002	Hồng pastel hộp	Hộp tròn 15 bông — dịu dàng cho mẹ	550000	{MOTHER,BIRTHDAY,MOTHERS_DAY,WARM,PINK}	72	DRAFT	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00	LEGACY_FRESH	\N	\N	\N
30000000-0000-0000-0000-000000000006	20000000-0000-0000-0000-000000000003	Tulip Hà Lan 5 cành	Tulip hồng gói giấy lụa — lời yêu nhẹ nhàng	280000	{LOVER,BIRTHDAY,WOMENS_DAY,PINK,WARM}	48	DRAFT	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00	LEGACY_FRESH	\N	\N	\N
30000000-0000-0000-0000-000000000007	20000000-0000-0000-0000-000000000003	Cẩm chướng tri ân	Bó cẩm chướng đỏ hồng — tri ân thầy cô, mẹ	220000	{TEACHER,MOTHER,TEACHERS_DAY,MOTHERS_DAY,WARM,RED}	72	DRAFT	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00	LEGACY_FRESH	\N	\N	\N
30000000-0000-0000-0000-000000000008	20000000-0000-0000-0000-000000000003	Baby trắng mini	Bó baby nhỏ xinh — xin lỗi dễ thương	150000	{FRIEND,APOLOGY,FUNNY,WHITE}	48	DRAFT	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00	LEGACY_FRESH	\N	\N	\N
\.


--
-- Data for Name: sellers; Type: TABLE DATA; Schema: identity; Owner: florabot
--

COPY identity.sellers (id, shop_name, phone, address, status, package_id, package_expires_at, brand_tone, bank_name, bank_account_no_enc, bank_holder, created_at, updated_at) FROM stdin;
20000000-0000-0000-0000-000000000003	Hoa Nhà Làm	0901000003	8 Phan Xích Long, Phú Nhuận	PENDING	\N	\N	\N	\N	\N	\N	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
20000000-0000-0000-0000-000000000002	Tiệm Hoa Mộc	0901000002	45 Võ Văn Ngân, Thủ Đức	APPROVED	20000000-0000-0000-0000-00000000000b	\N	Dễ thương, ấm áp kiểu Hàn, hay dùng câu ngắn	Techcombank	enc:v1:WFlaNzg5MDEy	TRAN THI BICH	2026-10-08 13:47:32.155922+00	2026-10-14 09:57:46.52136+00
01a1391e-bcfb-7176-87a9-12d981d60264	Shop Hoa Của Tâm	0889462565	12 Lê Lợi, Quận 1, TP.HCM	ACTIVE	20000000-0000-0000-0000-00000000000b	2026-11-14	\N	\N	\N	\N	2026-10-14 06:34:38.160068+00	2026-10-14 09:57:46.52136+00
20000000-0000-0000-0000-000000000001	Hoa Sài Gòn	0901000001	12 Lê Lợi, Q1, TP.HCM	PAST_DUE	20000000-0000-0000-0000-00000000000a	2026-11-13	Sang trọng, ít lời, như người bán hoa lâu năm ở Sài Gòn	Vietcombank	enc:v1:QUJDMTIzNDU2	NGUYEN VAN AN	2026-10-08 13:47:32.155922+00	2026-10-14 09:57:46.52136+00
\.


--
-- Data for Name: subscription_packages; Type: TABLE DATA; Schema: identity; Owner: florabot
--

COPY identity.subscription_packages (id, name, monthly_fee, max_slots, status, created_at, updated_at) FROM stdin;
20000000-0000-0000-0000-00000000000a	Cơ bản	400000	3	ACTIVE	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
20000000-0000-0000-0000-00000000000b	Chuyên nghiệp	1500000	10	ACTIVE	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
\.


--
-- Data for Name: subscriptions; Type: TABLE DATA; Schema: identity; Owner: florabot
--

COPY identity.subscriptions (id, seller_id, package_id, period_from, period_to, price, status, payment_id, created_at, updated_at) FROM stdin;
01a13943-ac2c-7755-bd94-8a89c0ed328e	01a1391e-bcfb-7176-87a9-12d981d60264	20000000-0000-0000-0000-00000000000b	2026-10-14	2026-11-14	1500000	ACTIVE	01a13943-ac49-7e1e-b980-dceb596e94e2	2026-10-14 07:14:58.704435+00	2026-10-14 07:19:06.659813+00
\.


--
-- Data for Name: users; Type: TABLE DATA; Schema: identity; Owner: florabot
--

COPY identity.users (id, email, phone, password_hash, full_name, role, seller_id, status, loyalty_points, created_at, updated_at) FROM stdin;
10000000-0000-0000-0000-0000000000ff	system@florabot.vn	\N	!disabled	Hệ thống FloraBot	SYSTEM	\N	DISABLED	0	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
10000000-0000-0000-0000-000000000002	ops@florabot.vn	0909000002	$2b$12$demoops	Điều phối vận hành (admin)	ADMIN	\N	ACTIVE	0	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
10000000-0000-0000-0000-000000000003	an@hoasaigon.vn	0909000003	$2b$12$demoseller1	Nguyễn Văn An	SELLER	20000000-0000-0000-0000-000000000001	ACTIVE	0	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
10000000-0000-0000-0000-000000000004	bich@hoamoc.vn	0909000004	$2b$12$demoseller2	Trần Thị Bích	SELLER	20000000-0000-0000-0000-000000000002	ACTIVE	0	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
10000000-0000-0000-0000-000000000005	cuong@hoamoc.vn	0909000005	$2b$12$demostaff	Lê Văn Cường	SELLER	20000000-0000-0000-0000-000000000002	ACTIVE	0	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
10000000-0000-0000-0000-000000000006	dung@hoasaigon.vn	0909000006	$2b$12$demostaff1	Phạm Thị Dung	SELLER	20000000-0000-0000-0000-000000000001	ACTIVE	0	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
10000000-0000-0000-0000-000000000007	em@florabot.vn	0909000007	$2b$12$demotech	Võ Văn Em (kỹ thuật, quyền admin)	ADMIN	\N	ACTIVE	0	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
10000000-0000-0000-0000-000000000008	giang@gmail.com	0912000008	\N	Đỗ Thu Giang	CUSTOMER	\N	ACTIVE	0	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
10000000-0000-0000-0000-000000000009	\N	0912000009	\N	Khách Huy (chỉ có SĐT)	CUSTOMER	\N	ACTIVE	0	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
10000000-0000-0000-0000-000000000010	hoa@hoamoc.vn	0909000010	$2b$12$demostaff2	Ngô Thị Hoa	SELLER	20000000-0000-0000-0000-000000000002	ACTIVE	0	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
10000000-0000-0000-0000-000000000011	khoa@gmail.com	0912000011	\N	Lý Minh Khoa	CUSTOMER	\N	ACTIVE	0	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
01a1360c-5aa9-7599-a1e3-ccb0664c270e	tamvcse184199@fpt.edu.vn	\N	$2a$12$JoL8VFJgG9nqu32pmAKP0et7KzPeASnd.vMS2KRRimWEYLq6VgLFu	Tam123	SELLER	01a1391e-bcfb-7176-87a9-12d981d60264	ACTIVE	0	2026-10-13 16:15:41.468841+00	2026-10-14 06:34:38.160068+00
10000000-0000-0000-0000-000000000001	admin@florabot.vn	0909000001	$2a$12$uFOA9MholdNW7pMINzowXeFdcE9D5jzDZBVa.gFZNL6IRLw5hnfma	Quản trị FloraBot	ADMIN	\N	ACTIVE	0	2026-10-08 13:47:32.155922+00	2026-10-14 06:49:41.159063+00
01a138f0-04ff-7383-a50b-1c38ccacaecb	vochitam112004@gmail.com	\N	$2a$12$Aht1/fZmcgeGS8Z/l2MH6eH4MpQvveinZo6UhlE2FvAZ8ZyrCJv62	Vo Chi Tam	CUSTOMER	\N	ACTIVE	0	2026-10-14 05:43:36.475704+00	2026-10-14 07:32:29.85102+00
\.


--
-- Data for Name: accessories; Type: TABLE DATA; Schema: kiosk_ops; Owner: florabot
--

COPY kiosk_ops.accessories (id, seller_id, kiosk_id, name, price, stock_quantity, status, created_at, updated_at) FROM stdin;
30000000-0000-0000-0001-000000000001	20000000-0000-0000-0000-000000000001	40000000-0000-0000-0000-000000000001	Thiệp chúc mừng	20000	30	ACTIVE	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
30000000-0000-0000-0001-000000000002	20000000-0000-0000-0000-000000000002	40000000-0000-0000-0000-000000000001	Gấu bông nhỏ	120000	5	ACTIVE	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
30000000-0000-0000-0001-000000000003	20000000-0000-0000-0000-000000000002	40000000-0000-0000-0000-000000000002	Thiệp chúc mừng	20000	40	ACTIVE	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
\.


--
-- Data for Name: bouquets; Type: TABLE DATA; Schema: kiosk_ops; Owner: florabot
--

COPY kiosk_ops.bouquets (id, product_id, seller_id, qr_code, price_snapshot, status, sellable_until, stocked_at, created_at, updated_at) FROM stdin;
\.


--
-- Data for Name: inventory_logs; Type: TABLE DATA; Schema: kiosk_ops; Owner: florabot
--

COPY kiosk_ops.inventory_logs (id, seq, bouquet_id, slot_id, movement_type, batch_id, order_id, performed_by, reason, created_at) FROM stdin;
\.


--
-- Data for Name: kiosks; Type: TABLE DATA; Schema: kiosk_ops; Owner: florabot
--

COPY kiosk_ops.kiosks (id, code, name, address, region, hardware_id, mqtt_client_id, status, last_heartbeat_at, api_key_hash, created_at, updated_at) FROM stdin;
40000000-0000-0000-0000-000000000001	K-Q1-01	Kiosk Nhà Văn hóa Thanh Niên	4 Phạm Ngọc Thạch, Q1	HCM	ESP32-A1B2C3	kiosk-q1-01	OFFLINE	2026-10-08 13:47:32.155922+00	fa442142df0b682690b5d1482a3ce018f4b6fc88e88e3a89415eb8823cba8f58	2026-10-08 13:47:32.155922+00	2026-10-08 13:50:43.628046+00
40000000-0000-0000-0000-000000000002	K-TD-01	Kiosk Đại học FPT	Lô E2a-7, Khu CNC, Thủ Đức	HCM	ESP32-D4E5F6	kiosk-td-01	OFFLINE	2026-10-08 13:47:32.155922+00	1bf497c303b9b5efd4040381277fcc542cda45f58309c399e74d568363dd62f2	2026-10-08 13:47:32.155922+00	2026-10-08 13:50:43.628046+00
\.


--
-- Data for Name: manager_kiosks; Type: TABLE DATA; Schema: kiosk_ops; Owner: florabot
--

COPY kiosk_ops.manager_kiosks (manager_id, kiosk_id, created_at) FROM stdin;
\.


--
-- Data for Name: mqtt_dispatches; Type: TABLE DATA; Schema: kiosk_ops; Owner: florabot
--

COPY kiosk_ops.mqtt_dispatches (cmd_id, delivery_id, attempted_at, next_attempt_at, pubacked_at) FROM stdin;
\.


--
-- Data for Name: mqtt_inbox; Type: TABLE DATA; Schema: kiosk_ops; Owner: florabot
--

COPY kiosk_ops.mqtt_inbox (event_id, kiosk_id, cmd_id, event, payload_hash, occurred_at, received_at, processed_at, disposition) FROM stdin;
\.


--
-- Data for Name: slot_assignments; Type: TABLE DATA; Schema: kiosk_ops; Owner: florabot
--

COPY kiosk_ops.slot_assignments (id, slot_id, seller_id, assigned_by, assigned_at, status, released_at, release_reason) FROM stdin;
\.


--
-- Data for Name: slots; Type: TABLE DATA; Schema: kiosk_ops; Owner: florabot
--

COPY kiosk_ops.slots (id, kiosk_id, slot_code, status, current_seller_id, bouquet_id, hold_until, hold_order_id, row_version, relay_channel, created_at, updated_at) FROM stdin;
40000000-0000-0000-0001-000000000001	40000000-0000-0000-0000-000000000001	A01	FREE	\N	\N	\N	\N	0	0	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
40000000-0000-0000-0001-000000000002	40000000-0000-0000-0000-000000000001	A02	FREE	\N	\N	\N	\N	0	1	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
40000000-0000-0000-0001-000000000003	40000000-0000-0000-0000-000000000001	A03	FREE	\N	\N	\N	\N	0	2	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
40000000-0000-0000-0001-000000000004	40000000-0000-0000-0000-000000000001	A04	FREE	\N	\N	\N	\N	0	3	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
40000000-0000-0000-0001-000000000005	40000000-0000-0000-0000-000000000001	A05	FREE	\N	\N	\N	\N	0	4	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
40000000-0000-0000-0001-000000000006	40000000-0000-0000-0000-000000000001	A06	FREE	\N	\N	\N	\N	0	5	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
40000000-0000-0000-0002-000000000001	40000000-0000-0000-0000-000000000002	B01	FREE	\N	\N	\N	\N	0	0	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
40000000-0000-0000-0002-000000000002	40000000-0000-0000-0000-000000000002	B02	FREE	\N	\N	\N	\N	0	1	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
40000000-0000-0000-0002-000000000003	40000000-0000-0000-0000-000000000002	B03	FREE	\N	\N	\N	\N	0	2	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
40000000-0000-0000-0002-000000000004	40000000-0000-0000-0000-000000000002	B04	FREE	\N	\N	\N	\N	0	3	2026-10-08 13:47:32.155922+00	2026-10-08 13:47:32.155922+00
\.


--
-- Data for Name: staff_incident_actions; Type: TABLE DATA; Schema: kiosk_ops; Owner: florabot
--

COPY kiosk_ops.staff_incident_actions (id, task_id, actor_id, incident_id, request_version, result_version, report, created_at) FROM stdin;
\.


--
-- Data for Name: staff_stock_actions; Type: TABLE DATA; Schema: kiosk_ops; Owner: florabot
--

COPY kiosk_ops.staff_stock_actions (id, task_id, actor_id, product_id, slot_id, qr_code, bouquet_id, created_at) FROM stdin;
\.


--
-- Data for Name: staff_tasks; Type: TABLE DATA; Schema: kiosk_ops; Owner: florabot
--

COPY kiosk_ops.staff_tasks (id, kind, assignee_id, assigned_by, kiosk_id, seller_id, incident_id, instructions, status, report, version, created_at, updated_at) FROM stdin;
\.


--
-- Data for Name: system_settings; Type: TABLE DATA; Schema: kiosk_ops; Owner: florabot
--

COPY kiosk_ops.system_settings (key, value, description, updated_by, updated_at) FROM stdin;
hold_minutes	7	Giữ ô chờ khách quét QR thanh toán (phút) — BRD: TTL 7 phút	\N	2026-10-08 13:47:32.155922+00
token_minutes	1	Hiệu lực token mở hộc (phút) — BRD: 60 giây	\N	2026-10-08 13:47:32.155922+00
kiosk_pickup_minutes	30	Sau khi trả tiền, giữ ô tối đa N phút cho khách mở lại hộc bằng mã e-receipt	\N	2026-10-08 13:47:32.155922+00
door_max_attempts	2	Số lần gửi lệnh mở cửa thất bại trước khi DISPENSE_FAILED — BRD: 2 lần	\N	2026-10-08 13:47:32.155922+00
dispute_window_hours	24	Thời hạn khiếu nại kể từ lúc lấy hàng (giờ); hết hạn mới đối soát cho seller	\N	2026-10-08 13:47:32.155922+00
withdraw_min	50000	Số tiền rút tối thiểu (đồng) — chờ GVHD chốt 50k hay 500k	\N	2026-10-08 13:47:32.155922+00
expiry_warn_hours	6	Bó hoa còn dưới N giờ hạn bán thì hiện cảnh báo cho seller	\N	2026-10-08 13:47:32.155922+00
points_rate_percent	1	Tích điểm = N% giá trị đơn (1 điểm = 1 đồng), cộng khi đơn COMPLETED	\N	2026-10-08 13:47:32.155922+00
points_max_redeem_percent	50	Đổi điểm tối đa N% giá trị hàng của một đơn	\N	2026-10-08 13:47:32.155922+00
otp_ttl_minutes	3	Hiệu lực OTP đăng nhập tại kiosk (OTP lưu Valkey, không lưu DB)	\N	2026-10-08 13:47:32.155922+00
heartbeat_offline_seconds	90	Không nhận heartbeat sau N giây thì coi kiosk OFFLINE	\N	2026-10-08 13:47:32.155922+00
subscription_payment_hours	24	Đăng ký gói chưa thanh toán sau N giờ thì hủy (mở lại kỳ cho lần đăng ký khác)	\N	2026-10-08 13:47:32.155922+00
past_due_grace_days	7	Gói hết hạn (PAST_DUE) quá N ngày không trả tiếp thì thu hồi toàn bộ ô đã gán	\N	2026-10-08 13:47:32.155922+00
return_deadline_hours	48	Seller phải lấy hoa quá hạn / hết hợp đồng về trong N giờ, quá hạn admin được thanh lý	\N	2026-10-08 13:47:32.155922+00
web_pickup_hours	2	Thoi gian nhan hoa dat web sau moc hen (gio)	\N	2026-10-08 13:47:35.962809+00
web_quote_hours	24	Hieu luc bao gia hoa dat rieng (gio)	\N	2026-10-08 13:47:35.962809+00
\.


--
-- Data for Name: unlock_tokens; Type: TABLE DATA; Schema: kiosk_ops; Owner: florabot
--

COPY kiosk_ops.unlock_tokens (id, kiosk_id, slot_id, purpose, order_id, issued_to_user_id, token_hash, cmd_id, status, attempts, issued_at, expires_at, door_opened_at, door_closed_at) FROM stdin;
\.


--
-- Data for Name: attachments; Type: TABLE DATA; Schema: notify; Owner: florabot
--

COPY notify.attachments (id, owner_service, owner_type, owner_id, file_url, mime_type, size_bytes, sha256, phase, uploaded_by, created_at) FROM stdin;
\.


--
-- Data for Name: audit_logs; Type: TABLE DATA; Schema: notify; Owner: florabot
--

COPY notify.audit_logs (id, actor_type, actor_id, action, entity_type, entity_id, payload, created_at) FROM stdin;
1	SYSTEM	\N	DAILY_RECONCILIATION	payments	\N	{"date": "2026-10-07", "rows": [{"muc": "Tiền vào VietQR (đơn + phí gói)", "lech": 0, "so_cai": 0, "ghi_chu": "Khoản thu SUCCEEDED trong ngày phải = nợ GATEWAY_CLEARING trong ngày", "chung_tu": 0}, {"muc": "Chi hoàn khách/seller (chuyển khoản tay)", "lech": 0, "so_cai": 0, "ghi_chu": "Lệnh hoàn đã chi trong ngày phải = có GATEWAY_CLEARING (REFUND/REFUND_PAYOUT) trong ngày", "chung_tu": 0}, {"muc": "Chi rút tiền seller", "lech": 0, "so_cai": 0, "ghi_chu": "Rút tiền PAID trong ngày phải = có GATEWAY_CLEARING (WITHDRAWAL) trong ngày", "chung_tu": 0}, {"muc": "Sổ cái cân (tổng mọi bút toán)", "lech": 0, "so_cai": 0, "ghi_chu": "Phải = 0", "chung_tu": 0}, {"muc": "Lệnh hoàn chờ chi quá 48 giờ (số lệnh)", "lech": 0, "so_cai": 0, "ghi_chu": "Cảnh báo SLA hoàn tiền: khách chưa khai STK hoặc admin chưa chi", "chung_tu": 0}, {"muc": "Tiền đang chờ hoàn (REFUND_CLEARING)", "lech": 0, "so_cai": 0, "ghi_chu": "Phải = tổng lệnh hoàn PENDING đã phân bổ; về 0 khi chi hết", "chung_tu": 0}]}	2026-10-08 13:50:44.207098+00
2	SYSTEM	\N	DAILY_RECONCILIATION	payments	\N	{"date": "2026-10-09", "rows": [{"muc": "Tiền vào VietQR (đơn + phí gói)", "lech": 0, "so_cai": 0, "ghi_chu": "Khoản thu SUCCEEDED trong ngày phải = nợ GATEWAY_CLEARING trong ngày", "chung_tu": 0}, {"muc": "Chi hoàn khách/seller (chuyển khoản tay)", "lech": 0, "so_cai": 0, "ghi_chu": "Lệnh hoàn đã chi trong ngày phải = có GATEWAY_CLEARING (REFUND/REFUND_PAYOUT) trong ngày", "chung_tu": 0}, {"muc": "Chi rút tiền seller", "lech": 0, "so_cai": 0, "ghi_chu": "Rút tiền PAID trong ngày phải = có GATEWAY_CLEARING (WITHDRAWAL) trong ngày", "chung_tu": 0}, {"muc": "Sổ cái cân (tổng mọi bút toán)", "lech": 0, "so_cai": 0, "ghi_chu": "Phải = 0", "chung_tu": 0}, {"muc": "Lệnh hoàn chờ chi quá 48 giờ (số lệnh)", "lech": 0, "so_cai": 0, "ghi_chu": "Cảnh báo SLA hoàn tiền: khách chưa khai STK hoặc admin chưa chi", "chung_tu": 0}, {"muc": "Tiền đang chờ hoàn (REFUND_CLEARING)", "lech": 0, "so_cai": 0, "ghi_chu": "Phải = tổng lệnh hoàn PENDING đã phân bổ; về 0 khi chi hết", "chung_tu": 0}]}	2026-10-10 11:59:31.110723+00
3	SYSTEM	\N	PORTAL_USER_PROVISIONED	users	10000000-0000-0000-0000-000000000001	{"role": "ADMIN", "channel": "operator_cli"}	2026-10-10 12:50:05.414853+00
4	SYSTEM	\N	DAILY_RECONCILIATION	payments	\N	{"date": "2026-10-11", "rows": [{"muc": "Tiền vào VietQR (đơn + phí gói)", "lech": 0, "so_cai": 0, "ghi_chu": "Khoản thu SUCCEEDED trong ngày phải = nợ GATEWAY_CLEARING trong ngày", "chung_tu": 0}, {"muc": "Chi hoàn khách/seller (chuyển khoản tay)", "lech": 0, "so_cai": 0, "ghi_chu": "Lệnh hoàn đã chi trong ngày phải = có GATEWAY_CLEARING (REFUND/REFUND_PAYOUT) trong ngày", "chung_tu": 0}, {"muc": "Chi rút tiền seller", "lech": 0, "so_cai": 0, "ghi_chu": "Rút tiền PAID trong ngày phải = có GATEWAY_CLEARING (WITHDRAWAL) trong ngày", "chung_tu": 0}, {"muc": "Sổ cái cân (tổng mọi bút toán)", "lech": 0, "so_cai": 0, "ghi_chu": "Phải = 0", "chung_tu": 0}, {"muc": "Lệnh hoàn chờ chi quá 48 giờ (số lệnh)", "lech": 0, "so_cai": 0, "ghi_chu": "Cảnh báo SLA hoàn tiền: khách chưa khai STK hoặc admin chưa chi", "chung_tu": 0}, {"muc": "Tiền đang chờ hoàn (REFUND_CLEARING)", "lech": 0, "so_cai": 0, "ghi_chu": "Phải = tổng lệnh hoàn PENDING đã phân bổ; về 0 khi chi hết", "chung_tu": 0}]}	2026-10-12 06:01:44.184727+00
5	SYSTEM	\N	DAILY_RECONCILIATION	payments	\N	{"date": "2026-10-12", "rows": [{"muc": "Tiền vào VietQR (đơn + phí gói)", "lech": 0, "so_cai": 0, "ghi_chu": "Khoản thu SUCCEEDED trong ngày phải = nợ GATEWAY_CLEARING trong ngày", "chung_tu": 0}, {"muc": "Chi hoàn khách/seller (chuyển khoản tay)", "lech": 0, "so_cai": 0, "ghi_chu": "Lệnh hoàn đã chi trong ngày phải = có GATEWAY_CLEARING (REFUND/REFUND_PAYOUT) trong ngày", "chung_tu": 0}, {"muc": "Chi rút tiền seller", "lech": 0, "so_cai": 0, "ghi_chu": "Rút tiền PAID trong ngày phải = có GATEWAY_CLEARING (WITHDRAWAL) trong ngày", "chung_tu": 0}, {"muc": "Sổ cái cân (tổng mọi bút toán)", "lech": 0, "so_cai": 0, "ghi_chu": "Phải = 0", "chung_tu": 0}, {"muc": "Lệnh hoàn chờ chi quá 48 giờ (số lệnh)", "lech": 0, "so_cai": 0, "ghi_chu": "Cảnh báo SLA hoàn tiền: khách chưa khai STK hoặc admin chưa chi", "chung_tu": 0}, {"muc": "Tiền đang chờ hoàn (REFUND_CLEARING)", "lech": 0, "so_cai": 0, "ghi_chu": "Phải = tổng lệnh hoàn PENDING đã phân bổ; về 0 khi chi hết", "chung_tu": 0}]}	2026-10-13 14:54:02.7405+00
6	SYSTEM	\N	DAILY_RECONCILIATION	payments	\N	{"date": "2026-10-13", "rows": [{"muc": "Tiền vào VietQR (đơn + phí gói)", "lech": 0, "so_cai": 0, "ghi_chu": "Khoản thu SUCCEEDED trong ngày phải = nợ GATEWAY_CLEARING trong ngày", "chung_tu": 0}, {"muc": "Chi hoàn khách/seller (chuyển khoản tay)", "lech": 0, "so_cai": 0, "ghi_chu": "Lệnh hoàn đã chi trong ngày phải = có GATEWAY_CLEARING (REFUND/REFUND_PAYOUT) trong ngày", "chung_tu": 0}, {"muc": "Chi rút tiền seller", "lech": 0, "so_cai": 0, "ghi_chu": "Rút tiền PAID trong ngày phải = có GATEWAY_CLEARING (WITHDRAWAL) trong ngày", "chung_tu": 0}, {"muc": "Sổ cái cân (tổng mọi bút toán)", "lech": 0, "so_cai": 0, "ghi_chu": "Phải = 0", "chung_tu": 0}, {"muc": "Lệnh hoàn chờ chi quá 48 giờ (số lệnh)", "lech": 0, "so_cai": 0, "ghi_chu": "Cảnh báo SLA hoàn tiền: khách chưa khai STK hoặc admin chưa chi", "chung_tu": 0}, {"muc": "Tiền đang chờ hoàn (REFUND_CLEARING)", "lech": 0, "so_cai": 0, "ghi_chu": "Phải = tổng lệnh hoàn PENDING đã phân bổ; về 0 khi chi hết", "chung_tu": 0}]}	2026-10-14 05:21:14.722976+00
7	SYSTEM	\N	SELLER_PAST_DUE	sellers	20000000-0000-0000-0000-000000000001	{"from": "ACTIVE", "expires": "2026-11-13"}	2026-10-14 05:57:18.19347+00
8	USER	01a1360c-5aa9-7599-a1e3-ccb0664c270e	MEMBER_SHOP_OPENED	sellers	01a1391e-bcfb-7176-87a9-12d981d60264	{"shop": "Shop Hoa Của Tâm"}	2026-10-14 06:34:38.238609+00
9	SYSTEM	\N	PORTAL_USER_PROVISIONED	users	10000000-0000-0000-0000-000000000001	{"role": "ADMIN", "channel": "operator_cli"}	2026-10-14 06:41:14.105811+00
10	SYSTEM	\N	PORTAL_USER_PROVISIONED	users	10000000-0000-0000-0000-000000000001	{"role": "ADMIN", "channel": "operator_cli"}	2026-10-14 06:43:01.417514+00
11	SYSTEM	\N	PORTAL_USER_PROVISIONED	users	10000000-0000-0000-0000-000000000001	{"role": "ADMIN", "channel": "operator_cli"}	2026-10-14 06:49:41.173955+00
12	USER	10000000-0000-0000-0000-000000000001	SELLER_APPROVED	sellers	01a1391e-bcfb-7176-87a9-12d981d60264	{"package": "20000000-0000-0000-0000-00000000000b"}	2026-10-14 06:53:36.005702+00
13	SYSTEM	\N	SELLER_PAST_DUE	sellers	20000000-0000-0000-0000-000000000001	{"from": "ACTIVE", "expires": "2026-11-13"}	2026-10-14 07:13:32.528191+00
14	USER	01a1360c-5aa9-7599-a1e3-ccb0664c270e	SUBSCRIPTION_REQUESTED	subscriptions	01a13943-ac2c-7755-bd94-8a89c0ed328e	{"to": "2026-11-14", "from": "2026-10-14", "price": 1500000, "package": "Chuyên nghiệp"}	2026-10-14 07:14:58.767656+00
15	SYSTEM	\N	SUBSCRIPTION_PAID	subscriptions	01a13943-ac2c-7755-bd94-8a89c0ed328e	{"to": "2026-11-14", "amount": 1500000}	2026-10-14 07:19:06.710169+00
16	SYSTEM	\N	SELLER_ACTIVATED	sellers	01a1391e-bcfb-7176-87a9-12d981d60264	{"from": "APPROVED", "expires": "2026-11-14"}	2026-10-14 07:19:06.715714+00
\.


--
-- Data for Name: customer_seller_chat_images; Type: TABLE DATA; Schema: ordering; Owner: florabot
--

COPY ordering.customer_seller_chat_images (id, message_id, chat_id, file_url, mime_type, size_bytes, sha256, uploaded_by, created_at) FROM stdin;
01a139b7-8174-71d3-8567-d599e1a77a9d	01a139b7-815d-7a87-ae9d-f9a141ad3b35	01a13972-79cb-727f-9754-425c6c708606	https://res.cloudinary.com/phoxww2x/image/authenticated/s--SH-RDbWs--/v1791969642/florabot-evidence/a1bc56cc1d71446fb79ebede05dad254.png	image/png	1976	f28ad793517af92d852d1bb1cebe7242e8b0a1e20d60ad77fdc35c819ad51eda	01a138f0-04ff-7383-a50b-1c38ccacaecb	2026-10-14 09:21:29.939726+00
\.


--
-- Data for Name: customer_seller_chat_messages; Type: TABLE DATA; Schema: ordering; Owner: florabot
--

COPY ordering.customer_seller_chat_messages (id, chat_id, sender_user_id, content, created_at) FROM stdin;
01a1390a-bea1-75c6-9820-087c0cd49b0e	01a13908-b06c-7900-91d0-bc7bb322cbe2	01a138f0-04ff-7383-a50b-1c38ccacaecb	Tam vừa gửi tin nhắn cho shop Hoa Sài Gòn	2026-10-14 06:12:47.898079+00
01a13979-e13c-7c6b-ac0e-f1675f652394	01a13972-79cb-727f-9754-425c6c708606	01a138f0-04ff-7383-a50b-1c38ccacaecb	tôi là customer gửi tin nhắn đến shop hoa	2026-10-14 08:14:11.245927+00
01a1397e-f3bf-7f72-8bfc-2d5fd268adfd	01a13972-79cb-727f-9754-425c6c708606	01a1360c-5aa9-7599-a1e3-ccb0664c270e	Seller vừa gửi tin nhắn đến Customer	2026-10-14 08:19:43.66915+00
01a13980-ba3a-7a31-b22a-165cda25c35d	01a13972-79cb-727f-9754-425c6c708606	01a138f0-04ff-7383-a50b-1c38ccacaecb	tôi muốn đặt hoa	2026-10-14 08:21:40.021796+00
01a13981-fe0b-7a44-98bd-b70fdc11c1fb	01a13972-79cb-727f-9754-425c6c708606	01a1360c-5aa9-7599-a1e3-ccb0664c270e	bạn muốn mua hoa gì	2026-10-14 08:23:02.920834+00
01a13982-5c82-76ed-9e30-23472929f60d	01a13972-79cb-727f-9754-425c6c708606	01a138f0-04ff-7383-a50b-1c38ccacaecb	tôi muốn cho sinh nhật mẹ	2026-10-14 08:23:27.103858+00
01a139b7-815d-7a87-ae9d-f9a141ad3b35	01a13972-79cb-727f-9754-425c6c708606	01a138f0-04ff-7383-a50b-1c38ccacaecb	bóa hoa có dạng như ảnh	2026-10-14 09:21:29.939726+00
01a139c1-2801-7eeb-9da1-91a8fa5614b2	01a13972-79cb-727f-9754-425c6c708606	01a1360c-5aa9-7599-a1e3-ccb0664c270e	ok tôi đã nhận được ảnh	2026-10-14 09:32:02.422774+00
\.


--
-- Data for Name: customer_seller_chat_products; Type: TABLE DATA; Schema: ordering; Owner: florabot
--

COPY ordering.customer_seller_chat_products (chat_id, product_id, product_name_snapshot, first_opened_at) FROM stdin;
\.


--
-- Data for Name: customer_seller_chats; Type: TABLE DATA; Schema: ordering; Owner: florabot
--

COPY ordering.customer_seller_chats (id, customer_id, seller_id, created_at, last_message_at) FROM stdin;
01a13908-b06c-7900-91d0-bc7bb322cbe2	01a138f0-04ff-7383-a50b-1c38ccacaecb	20000000-0000-0000-0000-000000000001	2026-10-14 06:10:33.177218+00	2026-10-14 06:12:47.898079+00
01a13972-79cb-727f-9754-425c6c708606	01a138f0-04ff-7383-a50b-1c38ccacaecb	01a1391e-bcfb-7176-87a9-12d981d60264	2026-10-14 08:06:05.996979+00	2026-10-14 09:32:02.422774+00
\.


--
-- Data for Name: disputes; Type: TABLE DATA; Schema: ordering; Owner: florabot
--

COPY ordering.disputes (id, kind, order_id, kiosk_id, slot_id, reason, reported_by, status, decision, refund_amount, decided_by, resolved_at, created_at, updated_at) FROM stdin;
\.


--
-- Data for Name: order_items; Type: TABLE DATA; Schema: ordering; Owner: florabot
--

COPY ordering.order_items (id, order_id, item_type, bouquet_id, accessory_id, slot_id, name_snapshot, quantity, unit_price, line_total, line_status, created_at) FROM stdin;
\.


--
-- Data for Name: orders; Type: TABLE DATA; Schema: ordering; Owner: florabot
--

COPY ordering.orders (id, order_code, checkout_id, customer_id, seller_id, kiosk_id, status, subtotal, discount_amount, total_amount, points_redeemed, points_earned, tracking_token, ecard_content, completed_at, refund_bank_name, refund_bank_account_enc, refund_bank_holder, created_at, updated_at) FROM stdin;
\.


--
-- Data for Name: web_requests; Type: TABLE DATA; Schema: ordering; Owner: florabot
--

COPY ordering.web_requests (id, customer_id, seller_id, product_id, kiosk_id, kind, instructions, pickup_at, pickup_before, quoted_price, quote_note, quote_expires_at, state, order_id, created_at, updated_at, request_payload, display_snapshot) FROM stdin;
\.


--
-- Data for Name: gateway_orders; Type: TABLE DATA; Schema: payment; Owner: florabot
--

COPY payment.gateway_orders (payment_id, order_code, checkout_url, created_at) FROM stdin;
\.


--
-- Data for Name: ledger_entries; Type: TABLE DATA; Schema: payment; Owner: florabot
--

COPY payment.ledger_entries (id, journal_id, account, seller_id, amount, ref_type, ref_id, memo, created_at) FROM stdin;
01a13947-74ca-7180-8fb3-b3d57533b427	01a13947-74b5-75b5-b073-58764dce1d89	GATEWAY_CLEARING	\N	1500000	SUBSCRIPTION	01a13943-ac2c-7755-bd94-8a89c0ed328e	Thu phí gói	2026-10-14 07:19:06.659813+00
01a13947-74d2-75ec-9fbb-bdd7ac0db04a	01a13947-74b5-75b5-b073-58764dce1d89	PLATFORM_SUBSCRIPTION	\N	-1500000	SUBSCRIPTION	01a13943-ac2c-7755-bd94-8a89c0ed328e	Doanh thu phí gói	2026-10-14 07:19:06.659813+00
\.


--
-- Data for Name: payments; Type: TABLE DATA; Schema: payment; Owner: florabot
--

COPY payment.payments (id, kind, purpose, checkout_id, order_id, subscription_id, parent_payment_id, gateway, gateway_txn_id, idempotency_key, amount, status, approved_by, reason, raw_payload, paid_at, created_at, updated_at, paid_by) FROM stdin;
01a13943-ac49-7e1e-b980-dceb596e94e2	CHARGE	SUBSCRIPTION	\N	\N	01a13943-ac2c-7755-bd94-8a89c0ed328e	\N	PAYOS	LOCAL-TEST-01a13943-ac2c-7755-bd94-8a89c0ed328e	sub-01a13943-ac2c-7755-bd94-8a89c0ed328e	1500000	SUCCEEDED	\N	\N	{"code": "00", "orderCode": "LOCAL-TEST-01a13943-ac2c-7755-bd94-8a89c0ed328e"}	2026-10-14 07:19:06.659813+00	2026-10-14 07:14:58.704435+00	2026-10-14 07:19:06.659813+00	\N
\.


--
-- Data for Name: withdrawal_requests; Type: TABLE DATA; Schema: payment; Owner: florabot
--

COPY payment.withdrawal_requests (id, seller_id, amount, status, bank_name, bank_account_no_enc, bank_holder, approved_by, reject_reason, paid_at, created_at, updated_at, paid_by, approved_at) FROM stdin;
\.


--
-- Name: inventory_logs_seq_seq; Type: SEQUENCE SET; Schema: kiosk_ops; Owner: florabot
--

SELECT pg_catalog.setval('kiosk_ops.inventory_logs_seq_seq', 1, false);


--
-- Name: audit_logs_id_seq; Type: SEQUENCE SET; Schema: notify; Owner: florabot
--

SELECT pg_catalog.setval('notify.audit_logs_id_seq', 16, true);


--
-- Name: gateway_order_code_seq; Type: SEQUENCE SET; Schema: payment; Owner: florabot
--

SELECT pg_catalog.setval('payment.gateway_order_code_seq', 1, true);


--
-- Name: gift_surveys gift_surveys_pkey; Type: CONSTRAINT; Schema: ai; Owner: florabot
--

ALTER TABLE ONLY ai.gift_surveys
    ADD CONSTRAINT gift_surveys_pkey PRIMARY KEY (id);


--
-- Name: published published_pkey; Type: CONSTRAINT; Schema: cap; Owner: florabot
--

ALTER TABLE ONLY cap.published
    ADD CONSTRAINT published_pkey PRIMARY KEY ("Id");


--
-- Name: received received_pkey; Type: CONSTRAINT; Schema: cap; Owner: florabot
--

ALTER TABLE ONLY cap.received
    ADD CONSTRAINT received_pkey PRIMARY KEY ("Id");


--
-- Name: flower_products flower_products_pkey; Type: CONSTRAINT; Schema: catalog; Owner: florabot
--

ALTER TABLE ONLY catalog.flower_products
    ADD CONSTRAINT flower_products_pkey PRIMARY KEY (id);


--
-- Name: sellers sellers_pkey; Type: CONSTRAINT; Schema: identity; Owner: florabot
--

ALTER TABLE ONLY identity.sellers
    ADD CONSTRAINT sellers_pkey PRIMARY KEY (id);


--
-- Name: subscription_packages subscription_packages_name_key; Type: CONSTRAINT; Schema: identity; Owner: florabot
--

ALTER TABLE ONLY identity.subscription_packages
    ADD CONSTRAINT subscription_packages_name_key UNIQUE (name);


--
-- Name: subscription_packages subscription_packages_pkey; Type: CONSTRAINT; Schema: identity; Owner: florabot
--

ALTER TABLE ONLY identity.subscription_packages
    ADD CONSTRAINT subscription_packages_pkey PRIMARY KEY (id);


--
-- Name: subscriptions subscriptions_pkey; Type: CONSTRAINT; Schema: identity; Owner: florabot
--

ALTER TABLE ONLY identity.subscriptions
    ADD CONSTRAINT subscriptions_pkey PRIMARY KEY (id);


--
-- Name: subscriptions subscriptions_seller_id_daterange_excl; Type: CONSTRAINT; Schema: identity; Owner: florabot
--

ALTER TABLE ONLY identity.subscriptions
    ADD CONSTRAINT subscriptions_seller_id_daterange_excl EXCLUDE USING gist (seller_id WITH =, daterange(period_from, period_to) WITH &&) WHERE ((status = ANY (ARRAY['PENDING_PAYMENT'::text, 'ACTIVE'::text])));


--
-- Name: users users_email_key; Type: CONSTRAINT; Schema: identity; Owner: florabot
--

ALTER TABLE ONLY identity.users
    ADD CONSTRAINT users_email_key UNIQUE (email);


--
-- Name: users users_phone_key; Type: CONSTRAINT; Schema: identity; Owner: florabot
--

ALTER TABLE ONLY identity.users
    ADD CONSTRAINT users_phone_key UNIQUE (phone);


--
-- Name: users users_pkey; Type: CONSTRAINT; Schema: identity; Owner: florabot
--

ALTER TABLE ONLY identity.users
    ADD CONSTRAINT users_pkey PRIMARY KEY (id);


--
-- Name: accessories accessories_kiosk_id_seller_id_name_key; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.accessories
    ADD CONSTRAINT accessories_kiosk_id_seller_id_name_key UNIQUE (kiosk_id, seller_id, name);


--
-- Name: accessories accessories_pkey; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.accessories
    ADD CONSTRAINT accessories_pkey PRIMARY KEY (id);


--
-- Name: bouquets bouquets_pkey; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.bouquets
    ADD CONSTRAINT bouquets_pkey PRIMARY KEY (id);


--
-- Name: bouquets bouquets_qr_code_key; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.bouquets
    ADD CONSTRAINT bouquets_qr_code_key UNIQUE (qr_code);


--
-- Name: inventory_logs inventory_logs_pkey; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.inventory_logs
    ADD CONSTRAINT inventory_logs_pkey PRIMARY KEY (id);


--
-- Name: inventory_logs inventory_logs_seq_key; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.inventory_logs
    ADD CONSTRAINT inventory_logs_seq_key UNIQUE (seq);


--
-- Name: kiosks kiosks_code_key; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.kiosks
    ADD CONSTRAINT kiosks_code_key UNIQUE (code);


--
-- Name: kiosks kiosks_hardware_id_key; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.kiosks
    ADD CONSTRAINT kiosks_hardware_id_key UNIQUE (hardware_id);


--
-- Name: kiosks kiosks_mqtt_client_id_key; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.kiosks
    ADD CONSTRAINT kiosks_mqtt_client_id_key UNIQUE (mqtt_client_id);


--
-- Name: kiosks kiosks_pkey; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.kiosks
    ADD CONSTRAINT kiosks_pkey PRIMARY KEY (id);


--
-- Name: manager_kiosks manager_kiosks_pkey; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.manager_kiosks
    ADD CONSTRAINT manager_kiosks_pkey PRIMARY KEY (manager_id, kiosk_id);


--
-- Name: mqtt_dispatches mqtt_dispatches_pkey; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.mqtt_dispatches
    ADD CONSTRAINT mqtt_dispatches_pkey PRIMARY KEY (cmd_id);


--
-- Name: mqtt_inbox mqtt_inbox_pkey; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.mqtt_inbox
    ADD CONSTRAINT mqtt_inbox_pkey PRIMARY KEY (event_id);


--
-- Name: slot_assignments slot_assignments_pkey; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.slot_assignments
    ADD CONSTRAINT slot_assignments_pkey PRIMARY KEY (id);


--
-- Name: slots slots_bouquet_id_key; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.slots
    ADD CONSTRAINT slots_bouquet_id_key UNIQUE (bouquet_id);


--
-- Name: slots slots_kiosk_id_relay_channel_key; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.slots
    ADD CONSTRAINT slots_kiosk_id_relay_channel_key UNIQUE (kiosk_id, relay_channel);


--
-- Name: slots slots_kiosk_id_slot_code_key; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.slots
    ADD CONSTRAINT slots_kiosk_id_slot_code_key UNIQUE (kiosk_id, slot_code);


--
-- Name: slots slots_pkey; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.slots
    ADD CONSTRAINT slots_pkey PRIMARY KEY (id);


--
-- Name: staff_incident_actions staff_incident_actions_pkey; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.staff_incident_actions
    ADD CONSTRAINT staff_incident_actions_pkey PRIMARY KEY (id);


--
-- Name: staff_incident_actions staff_incident_actions_task_id_key; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.staff_incident_actions
    ADD CONSTRAINT staff_incident_actions_task_id_key UNIQUE (task_id);


--
-- Name: staff_stock_actions staff_stock_actions_pkey; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.staff_stock_actions
    ADD CONSTRAINT staff_stock_actions_pkey PRIMARY KEY (id);


--
-- Name: staff_tasks staff_tasks_pkey; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.staff_tasks
    ADD CONSTRAINT staff_tasks_pkey PRIMARY KEY (id);


--
-- Name: system_settings system_settings_pkey; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.system_settings
    ADD CONSTRAINT system_settings_pkey PRIMARY KEY (key);


--
-- Name: unlock_tokens unlock_tokens_cmd_id_key; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.unlock_tokens
    ADD CONSTRAINT unlock_tokens_cmd_id_key UNIQUE (cmd_id);


--
-- Name: unlock_tokens unlock_tokens_pkey; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.unlock_tokens
    ADD CONSTRAINT unlock_tokens_pkey PRIMARY KEY (id);


--
-- Name: unlock_tokens unlock_tokens_token_hash_key; Type: CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.unlock_tokens
    ADD CONSTRAINT unlock_tokens_token_hash_key UNIQUE (token_hash);


--
-- Name: attachments attachments_pkey; Type: CONSTRAINT; Schema: notify; Owner: florabot
--

ALTER TABLE ONLY notify.attachments
    ADD CONSTRAINT attachments_pkey PRIMARY KEY (id);


--
-- Name: audit_logs audit_logs_pkey; Type: CONSTRAINT; Schema: notify; Owner: florabot
--

ALTER TABLE ONLY notify.audit_logs
    ADD CONSTRAINT audit_logs_pkey PRIMARY KEY (id);


--
-- Name: customer_seller_chat_images customer_seller_chat_images_file_url_key; Type: CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.customer_seller_chat_images
    ADD CONSTRAINT customer_seller_chat_images_file_url_key UNIQUE (file_url);


--
-- Name: customer_seller_chat_images customer_seller_chat_images_message_id_key; Type: CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.customer_seller_chat_images
    ADD CONSTRAINT customer_seller_chat_images_message_id_key UNIQUE (message_id);


--
-- Name: customer_seller_chat_images customer_seller_chat_images_pkey; Type: CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.customer_seller_chat_images
    ADD CONSTRAINT customer_seller_chat_images_pkey PRIMARY KEY (id);


--
-- Name: customer_seller_chat_messages customer_seller_chat_messages_id_chat_key; Type: CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.customer_seller_chat_messages
    ADD CONSTRAINT customer_seller_chat_messages_id_chat_key UNIQUE (id, chat_id);


--
-- Name: customer_seller_chat_messages customer_seller_chat_messages_pkey; Type: CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.customer_seller_chat_messages
    ADD CONSTRAINT customer_seller_chat_messages_pkey PRIMARY KEY (id);


--
-- Name: customer_seller_chat_products customer_seller_chat_products_pkey; Type: CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.customer_seller_chat_products
    ADD CONSTRAINT customer_seller_chat_products_pkey PRIMARY KEY (chat_id, product_id);


--
-- Name: customer_seller_chats customer_seller_chats_pair_key; Type: CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.customer_seller_chats
    ADD CONSTRAINT customer_seller_chats_pair_key UNIQUE (customer_id, seller_id);


--
-- Name: customer_seller_chats customer_seller_chats_pkey; Type: CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.customer_seller_chats
    ADD CONSTRAINT customer_seller_chats_pkey PRIMARY KEY (id);


--
-- Name: disputes disputes_pkey; Type: CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.disputes
    ADD CONSTRAINT disputes_pkey PRIMARY KEY (id);


--
-- Name: order_items order_items_pkey; Type: CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.order_items
    ADD CONSTRAINT order_items_pkey PRIMARY KEY (id);


--
-- Name: orders orders_order_code_key; Type: CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.orders
    ADD CONSTRAINT orders_order_code_key UNIQUE (order_code);


--
-- Name: orders orders_pkey; Type: CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.orders
    ADD CONSTRAINT orders_pkey PRIMARY KEY (id);


--
-- Name: orders orders_tracking_token_key; Type: CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.orders
    ADD CONSTRAINT orders_tracking_token_key UNIQUE (tracking_token);


--
-- Name: web_requests web_requests_order_id_key; Type: CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.web_requests
    ADD CONSTRAINT web_requests_order_id_key UNIQUE (order_id);


--
-- Name: web_requests web_requests_pkey; Type: CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.web_requests
    ADD CONSTRAINT web_requests_pkey PRIMARY KEY (id);


--
-- Name: gateway_orders gateway_orders_order_code_key; Type: CONSTRAINT; Schema: payment; Owner: florabot
--

ALTER TABLE ONLY payment.gateway_orders
    ADD CONSTRAINT gateway_orders_order_code_key UNIQUE (order_code);


--
-- Name: gateway_orders gateway_orders_pkey; Type: CONSTRAINT; Schema: payment; Owner: florabot
--

ALTER TABLE ONLY payment.gateway_orders
    ADD CONSTRAINT gateway_orders_pkey PRIMARY KEY (payment_id);


--
-- Name: ledger_entries ledger_entries_pkey; Type: CONSTRAINT; Schema: payment; Owner: florabot
--

ALTER TABLE ONLY payment.ledger_entries
    ADD CONSTRAINT ledger_entries_pkey PRIMARY KEY (id);


--
-- Name: payments payments_gateway_gateway_txn_id_key; Type: CONSTRAINT; Schema: payment; Owner: florabot
--

ALTER TABLE ONLY payment.payments
    ADD CONSTRAINT payments_gateway_gateway_txn_id_key UNIQUE (gateway, gateway_txn_id);


--
-- Name: payments payments_idempotency_key_key; Type: CONSTRAINT; Schema: payment; Owner: florabot
--

ALTER TABLE ONLY payment.payments
    ADD CONSTRAINT payments_idempotency_key_key UNIQUE (idempotency_key);


--
-- Name: payments payments_pkey; Type: CONSTRAINT; Schema: payment; Owner: florabot
--

ALTER TABLE ONLY payment.payments
    ADD CONSTRAINT payments_pkey PRIMARY KEY (id);


--
-- Name: withdrawal_requests withdrawal_requests_pkey; Type: CONSTRAINT; Schema: payment; Owner: florabot
--

ALTER TABLE ONLY payment.withdrawal_requests
    ADD CONSTRAINT withdrawal_requests_pkey PRIMARY KEY (id);


--
-- Name: gift_surveys_customer_id_idx; Type: INDEX; Schema: ai; Owner: florabot
--

CREATE INDEX gift_surveys_customer_id_idx ON ai.gift_surveys USING btree (customer_id) WHERE (customer_id IS NOT NULL);


--
-- Name: gift_surveys_kiosk_id_created_at_idx; Type: INDEX; Schema: ai; Owner: florabot
--

CREATE INDEX gift_surveys_kiosk_id_created_at_idx ON ai.gift_surveys USING btree (kiosk_id, created_at);


--
-- Name: idx_published_ExpiresAt_StatusName; Type: INDEX; Schema: cap; Owner: florabot
--

CREATE INDEX "idx_published_ExpiresAt_StatusName" ON cap.published USING btree ("ExpiresAt", "StatusName");


--
-- Name: idx_published_Version_ExpiresAt_StatusName; Type: INDEX; Schema: cap; Owner: florabot
--

CREATE INDEX "idx_published_Version_ExpiresAt_StatusName" ON cap.published USING btree ("Version", "ExpiresAt", "StatusName");


--
-- Name: idx_received_ExpiresAt_StatusName; Type: INDEX; Schema: cap; Owner: florabot
--

CREATE INDEX "idx_received_ExpiresAt_StatusName" ON cap.received USING btree ("ExpiresAt", "StatusName");


--
-- Name: idx_received_Version_ExpiresAt_StatusName; Type: INDEX; Schema: cap; Owner: florabot
--

CREATE INDEX "idx_received_Version_ExpiresAt_StatusName" ON cap.received USING btree ("Version", "ExpiresAt", "StatusName");


--
-- Name: flower_products_seller_id_status_idx; Type: INDEX; Schema: catalog; Owner: florabot
--

CREATE INDEX flower_products_seller_id_status_idx ON catalog.flower_products USING btree (seller_id, status);


--
-- Name: flower_products_tags_idx; Type: INDEX; Schema: catalog; Owner: florabot
--

CREATE INDEX flower_products_tags_idx ON catalog.flower_products USING gin (tags);


--
-- Name: bouquets_sellable_idx; Type: INDEX; Schema: kiosk_ops; Owner: florabot
--

CREATE INDEX bouquets_sellable_idx ON kiosk_ops.bouquets USING btree (sellable_until) WHERE (status = 'STOCKED'::text);


--
-- Name: inventory_logs_batch_id_idx; Type: INDEX; Schema: kiosk_ops; Owner: florabot
--

CREATE INDEX inventory_logs_batch_id_idx ON kiosk_ops.inventory_logs USING btree (batch_id);


--
-- Name: inventory_logs_bouquet_id_seq_idx; Type: INDEX; Schema: kiosk_ops; Owner: florabot
--

CREATE INDEX inventory_logs_bouquet_id_seq_idx ON kiosk_ops.inventory_logs USING btree (bouquet_id, seq);


--
-- Name: manager_kiosks_kiosk_idx; Type: INDEX; Schema: kiosk_ops; Owner: florabot
--

CREATE INDEX manager_kiosks_kiosk_idx ON kiosk_ops.manager_kiosks USING btree (kiosk_id);


--
-- Name: mqtt_inbox_pending; Type: INDEX; Schema: kiosk_ops; Owner: florabot
--

CREATE INDEX mqtt_inbox_pending ON kiosk_ops.mqtt_inbox USING btree (received_at, event_id) WHERE (disposition = 'PENDING'::text);


--
-- Name: slot_assignments_one_active; Type: INDEX; Schema: kiosk_ops; Owner: florabot
--

CREATE UNIQUE INDEX slot_assignments_one_active ON kiosk_ops.slot_assignments USING btree (slot_id) WHERE (status = 'ACTIVE'::text);


--
-- Name: slot_assignments_seller_id_idx; Type: INDEX; Schema: kiosk_ops; Owner: florabot
--

CREATE INDEX slot_assignments_seller_id_idx ON kiosk_ops.slot_assignments USING btree (seller_id) WHERE (status = 'ACTIVE'::text);


--
-- Name: slots_hold_idx; Type: INDEX; Schema: kiosk_ops; Owner: florabot
--

CREATE INDEX slots_hold_idx ON kiosk_ops.slots USING btree (hold_until) WHERE (status = 'HELD'::text);


--
-- Name: staff_tasks_assignee; Type: INDEX; Schema: kiosk_ops; Owner: florabot
--

CREATE INDEX staff_tasks_assignee ON kiosk_ops.staff_tasks USING btree (assignee_id, status, created_at);


--
-- Name: staff_tasks_open_incident; Type: INDEX; Schema: kiosk_ops; Owner: florabot
--

CREATE UNIQUE INDEX staff_tasks_open_incident ON kiosk_ops.staff_tasks USING btree (incident_id) WHERE ((incident_id IS NOT NULL) AND (status <> ALL (ARRAY['COMPLETED'::text, 'CANCELLED'::text])));


--
-- Name: unlock_live_expiry_idx; Type: INDEX; Schema: kiosk_ops; Owner: florabot
--

CREATE INDEX unlock_live_expiry_idx ON kiosk_ops.unlock_tokens USING btree (expires_at) WHERE (status = ANY (ARRAY['ISSUED'::text, 'SENT'::text, 'ACKED'::text]));


--
-- Name: unlock_one_live_per_slot; Type: INDEX; Schema: kiosk_ops; Owner: florabot
--

CREATE UNIQUE INDEX unlock_one_live_per_slot ON kiosk_ops.unlock_tokens USING btree (slot_id) WHERE (status = ANY (ARRAY['ISSUED'::text, 'SENT'::text, 'ACKED'::text, 'OPENED'::text]));


--
-- Name: unlock_tokens_order_id_idx; Type: INDEX; Schema: kiosk_ops; Owner: florabot
--

CREATE INDEX unlock_tokens_order_id_idx ON kiosk_ops.unlock_tokens USING btree (order_id);


--
-- Name: attachments_owner_type_owner_id_idx; Type: INDEX; Schema: notify; Owner: florabot
--

CREATE INDEX attachments_owner_type_owner_id_idx ON notify.attachments USING btree (owner_type, owner_id);


--
-- Name: audit_logs_entity_type_entity_id_id_idx; Type: INDEX; Schema: notify; Owner: florabot
--

CREATE INDEX audit_logs_entity_type_entity_id_id_idx ON notify.audit_logs USING btree (entity_type, entity_id, id);


--
-- Name: customer_seller_chat_images_chat; Type: INDEX; Schema: ordering; Owner: florabot
--

CREATE INDEX customer_seller_chat_images_chat ON ordering.customer_seller_chat_images USING btree (chat_id, created_at DESC);


--
-- Name: customer_seller_chat_messages_history; Type: INDEX; Schema: ordering; Owner: florabot
--

CREATE INDEX customer_seller_chat_messages_history ON ordering.customer_seller_chat_messages USING btree (chat_id, created_at DESC, id DESC);


--
-- Name: customer_seller_chat_products_recent; Type: INDEX; Schema: ordering; Owner: florabot
--

CREATE INDEX customer_seller_chat_products_recent ON ordering.customer_seller_chat_products USING btree (chat_id, first_opened_at DESC);


--
-- Name: customer_seller_chats_customer_recent; Type: INDEX; Schema: ordering; Owner: florabot
--

CREATE INDEX customer_seller_chats_customer_recent ON ordering.customer_seller_chats USING btree (customer_id, last_message_at DESC NULLS LAST, id DESC);


--
-- Name: customer_seller_chats_seller_recent; Type: INDEX; Schema: ordering; Owner: florabot
--

CREATE INDEX customer_seller_chats_seller_recent ON ordering.customer_seller_chats USING btree (seller_id, last_message_at DESC NULLS LAST, id DESC);


--
-- Name: disputes_kiosk_id_status_idx; Type: INDEX; Schema: ordering; Owner: florabot
--

CREATE INDEX disputes_kiosk_id_status_idx ON ordering.disputes USING btree (kiosk_id, status);


--
-- Name: disputes_one_complaint_per_order; Type: INDEX; Schema: ordering; Owner: florabot
--

CREATE UNIQUE INDEX disputes_one_complaint_per_order ON ordering.disputes USING btree (order_id) WHERE (kind = 'COMPLAINT'::text);


--
-- Name: order_items_bouquet_once; Type: INDEX; Schema: ordering; Owner: florabot
--

CREATE UNIQUE INDEX order_items_bouquet_once ON ordering.order_items USING btree (bouquet_id) WHERE ((bouquet_id IS NOT NULL) AND (line_status = 'ACTIVE'::text));


--
-- Name: orders_checkout_id_idx; Type: INDEX; Schema: ordering; Owner: florabot
--

CREATE INDEX orders_checkout_id_idx ON ordering.orders USING btree (checkout_id);


--
-- Name: orders_customer_id_created_at_idx; Type: INDEX; Schema: ordering; Owner: florabot
--

CREATE INDEX orders_customer_id_created_at_idx ON ordering.orders USING btree (customer_id, created_at);


--
-- Name: orders_seller_id_created_at_idx; Type: INDEX; Schema: ordering; Owner: florabot
--

CREATE INDEX orders_seller_id_created_at_idx ON ordering.orders USING btree (seller_id, created_at);


--
-- Name: web_requests_customer; Type: INDEX; Schema: ordering; Owner: florabot
--

CREATE INDEX web_requests_customer ON ordering.web_requests USING btree (customer_id, created_at DESC);


--
-- Name: web_requests_seller; Type: INDEX; Schema: ordering; Owner: florabot
--

CREATE INDEX web_requests_seller ON ordering.web_requests USING btree (seller_id, created_at DESC);


--
-- Name: ledger_entries_journal_id_idx; Type: INDEX; Schema: payment; Owner: florabot
--

CREATE INDEX ledger_entries_journal_id_idx ON payment.ledger_entries USING btree (journal_id);


--
-- Name: ledger_entries_ref_id_account_idx; Type: INDEX; Schema: payment; Owner: florabot
--

CREATE INDEX ledger_entries_ref_id_account_idx ON payment.ledger_entries USING btree (ref_id, account);


--
-- Name: ledger_entries_seller_id_account_idx; Type: INDEX; Schema: payment; Owner: florabot
--

CREATE INDEX ledger_entries_seller_id_account_idx ON payment.ledger_entries USING btree (seller_id, account);


--
-- Name: ledger_once; Type: INDEX; Schema: payment; Owner: florabot
--

CREATE UNIQUE INDEX ledger_once ON payment.ledger_entries USING btree (ref_type, ref_id, account, seller_id) NULLS NOT DISTINCT;


--
-- Name: payments_one_charge_per_checkout; Type: INDEX; Schema: payment; Owner: florabot
--

CREATE UNIQUE INDEX payments_one_charge_per_checkout ON payment.payments USING btree (checkout_id) WHERE (kind = 'CHARGE'::text);


--
-- Name: payments_order_id_idx; Type: INDEX; Schema: payment; Owner: florabot
--

CREATE INDEX payments_order_id_idx ON payment.payments USING btree (order_id);


--
-- Name: payments_refund_parent_idx; Type: INDEX; Schema: payment; Owner: florabot
--

CREATE INDEX payments_refund_parent_idx ON payment.payments USING btree (parent_payment_id) WHERE (kind = 'REFUND'::text);


--
-- Name: withdrawal_one_pending; Type: INDEX; Schema: payment; Owner: florabot
--

CREATE UNIQUE INDEX withdrawal_one_pending ON payment.withdrawal_requests USING btree (seller_id) WHERE (status = ANY (ARRAY['PENDING'::text, 'APPROVED'::text]));


--
-- Name: gift_surveys touch; Type: TRIGGER; Schema: ai; Owner: florabot
--

CREATE TRIGGER touch BEFORE UPDATE ON ai.gift_surveys FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();


--
-- Name: flower_products price_audit; Type: TRIGGER; Schema: catalog; Owner: florabot
--

CREATE TRIGGER price_audit AFTER UPDATE OF price ON catalog.flower_products FOR EACH ROW EXECUTE FUNCTION flow.trg_price_audit();


--
-- Name: flower_products touch; Type: TRIGGER; Schema: catalog; Owner: florabot
--

CREATE TRIGGER touch BEFORE UPDATE ON catalog.flower_products FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();


--
-- Name: sellers touch; Type: TRIGGER; Schema: identity; Owner: florabot
--

CREATE TRIGGER touch BEFORE UPDATE ON identity.sellers FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();


--
-- Name: subscription_packages touch; Type: TRIGGER; Schema: identity; Owner: florabot
--

CREATE TRIGGER touch BEFORE UPDATE ON identity.subscription_packages FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();


--
-- Name: subscriptions touch; Type: TRIGGER; Schema: identity; Owner: florabot
--

CREATE TRIGGER touch BEFORE UPDATE ON identity.subscriptions FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();


--
-- Name: users touch; Type: TRIGGER; Schema: identity; Owner: florabot
--

CREATE TRIGGER touch BEFORE UPDATE ON identity.users FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();


--
-- Name: bouquets bouquets_artificial_no_expiry_trg; Type: TRIGGER; Schema: kiosk_ops; Owner: florabot
--

CREATE TRIGGER bouquets_artificial_no_expiry_trg BEFORE INSERT OR UPDATE OF product_id ON kiosk_ops.bouquets FOR EACH ROW EXECUTE FUNCTION kiosk_ops.set_artificial_bouquet_no_expiry();


--
-- Name: unlock_tokens guard_status; Type: TRIGGER; Schema: kiosk_ops; Owner: florabot
--

CREATE TRIGGER guard_status BEFORE UPDATE OF status ON kiosk_ops.unlock_tokens FOR EACH ROW EXECUTE FUNCTION public.guard_transition();


--
-- Name: inventory_logs inventory_logs_append_only; Type: TRIGGER; Schema: kiosk_ops; Owner: florabot
--

CREATE TRIGGER inventory_logs_append_only BEFORE DELETE OR UPDATE ON kiosk_ops.inventory_logs FOR EACH ROW EXECUTE FUNCTION public.forbid_change();


--
-- Name: accessories touch; Type: TRIGGER; Schema: kiosk_ops; Owner: florabot
--

CREATE TRIGGER touch BEFORE UPDATE ON kiosk_ops.accessories FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();


--
-- Name: bouquets touch; Type: TRIGGER; Schema: kiosk_ops; Owner: florabot
--

CREATE TRIGGER touch BEFORE UPDATE ON kiosk_ops.bouquets FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();


--
-- Name: kiosks touch; Type: TRIGGER; Schema: kiosk_ops; Owner: florabot
--

CREATE TRIGGER touch BEFORE UPDATE ON kiosk_ops.kiosks FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();


--
-- Name: slots touch; Type: TRIGGER; Schema: kiosk_ops; Owner: florabot
--

CREATE TRIGGER touch BEFORE UPDATE ON kiosk_ops.slots FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();


--
-- Name: system_settings touch; Type: TRIGGER; Schema: kiosk_ops; Owner: florabot
--

CREATE TRIGGER touch BEFORE UPDATE ON kiosk_ops.system_settings FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();


--
-- Name: manager_kiosks trg_check_manager_kiosk; Type: TRIGGER; Schema: kiosk_ops; Owner: florabot
--

CREATE TRIGGER trg_check_manager_kiosk BEFORE INSERT OR UPDATE ON kiosk_ops.manager_kiosks FOR EACH ROW EXECUTE FUNCTION kiosk_ops.check_manager_kiosk();


--
-- Name: audit_logs audit_append_only; Type: TRIGGER; Schema: notify; Owner: florabot
--

CREATE TRIGGER audit_append_only BEFORE DELETE OR UPDATE ON notify.audit_logs FOR EACH ROW EXECUTE FUNCTION public.forbid_change();


--
-- Name: disputes guard_status; Type: TRIGGER; Schema: ordering; Owner: florabot
--

CREATE TRIGGER guard_status BEFORE UPDATE OF status ON ordering.disputes FOR EACH ROW EXECUTE FUNCTION public.guard_transition();


--
-- Name: orders guard_status; Type: TRIGGER; Schema: ordering; Owner: florabot
--

CREATE TRIGGER guard_status BEFORE UPDATE OF status ON ordering.orders FOR EACH ROW EXECUTE FUNCTION public.guard_transition();


--
-- Name: disputes touch; Type: TRIGGER; Schema: ordering; Owner: florabot
--

CREATE TRIGGER touch BEFORE UPDATE ON ordering.disputes FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();


--
-- Name: orders touch; Type: TRIGGER; Schema: ordering; Owner: florabot
--

CREATE TRIGGER touch BEFORE UPDATE ON ordering.orders FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();


--
-- Name: payments guard_status; Type: TRIGGER; Schema: payment; Owner: florabot
--

CREATE TRIGGER guard_status BEFORE UPDATE OF status ON payment.payments FOR EACH ROW EXECUTE FUNCTION public.guard_transition();


--
-- Name: withdrawal_requests guard_status; Type: TRIGGER; Schema: payment; Owner: florabot
--

CREATE TRIGGER guard_status BEFORE UPDATE OF status ON payment.withdrawal_requests FOR EACH ROW EXECUTE FUNCTION public.guard_transition();


--
-- Name: ledger_entries ledger_append_only; Type: TRIGGER; Schema: payment; Owner: florabot
--

CREATE TRIGGER ledger_append_only BEFORE DELETE OR UPDATE ON payment.ledger_entries FOR EACH ROW EXECUTE FUNCTION public.forbid_change();


--
-- Name: ledger_entries ledger_balanced; Type: TRIGGER; Schema: payment; Owner: florabot
--

CREATE CONSTRAINT TRIGGER ledger_balanced AFTER INSERT ON payment.ledger_entries DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION payment.check_journal_balanced();


--
-- Name: payments payments_refund_cap; Type: TRIGGER; Schema: payment; Owner: florabot
--

CREATE TRIGGER payments_refund_cap BEFORE INSERT OR UPDATE ON payment.payments FOR EACH ROW EXECUTE FUNCTION payment.check_refund_total();


--
-- Name: payments touch; Type: TRIGGER; Schema: payment; Owner: florabot
--

CREATE TRIGGER touch BEFORE UPDATE ON payment.payments FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();


--
-- Name: withdrawal_requests touch; Type: TRIGGER; Schema: payment; Owner: florabot
--

CREATE TRIGGER touch BEFORE UPDATE ON payment.withdrawal_requests FOR EACH ROW EXECUTE FUNCTION public.touch_updated_at();


--
-- Name: sellers sellers_package_id_fkey; Type: FK CONSTRAINT; Schema: identity; Owner: florabot
--

ALTER TABLE ONLY identity.sellers
    ADD CONSTRAINT sellers_package_id_fkey FOREIGN KEY (package_id) REFERENCES identity.subscription_packages(id);


--
-- Name: subscriptions subscriptions_package_id_fkey; Type: FK CONSTRAINT; Schema: identity; Owner: florabot
--

ALTER TABLE ONLY identity.subscriptions
    ADD CONSTRAINT subscriptions_package_id_fkey FOREIGN KEY (package_id) REFERENCES identity.subscription_packages(id);


--
-- Name: subscriptions subscriptions_seller_id_fkey; Type: FK CONSTRAINT; Schema: identity; Owner: florabot
--

ALTER TABLE ONLY identity.subscriptions
    ADD CONSTRAINT subscriptions_seller_id_fkey FOREIGN KEY (seller_id) REFERENCES identity.sellers(id);


--
-- Name: users users_seller_id_fkey; Type: FK CONSTRAINT; Schema: identity; Owner: florabot
--

ALTER TABLE ONLY identity.users
    ADD CONSTRAINT users_seller_id_fkey FOREIGN KEY (seller_id) REFERENCES identity.sellers(id);


--
-- Name: accessories accessories_kiosk_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.accessories
    ADD CONSTRAINT accessories_kiosk_id_fkey FOREIGN KEY (kiosk_id) REFERENCES kiosk_ops.kiosks(id);


--
-- Name: inventory_logs inventory_logs_bouquet_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.inventory_logs
    ADD CONSTRAINT inventory_logs_bouquet_id_fkey FOREIGN KEY (bouquet_id) REFERENCES kiosk_ops.bouquets(id);


--
-- Name: inventory_logs inventory_logs_slot_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.inventory_logs
    ADD CONSTRAINT inventory_logs_slot_id_fkey FOREIGN KEY (slot_id) REFERENCES kiosk_ops.slots(id);


--
-- Name: manager_kiosks manager_kiosks_kiosk_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.manager_kiosks
    ADD CONSTRAINT manager_kiosks_kiosk_id_fkey FOREIGN KEY (kiosk_id) REFERENCES kiosk_ops.kiosks(id);


--
-- Name: manager_kiosks manager_kiosks_manager_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.manager_kiosks
    ADD CONSTRAINT manager_kiosks_manager_id_fkey FOREIGN KEY (manager_id) REFERENCES identity.users(id);


--
-- Name: mqtt_dispatches mqtt_dispatches_cmd_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.mqtt_dispatches
    ADD CONSTRAINT mqtt_dispatches_cmd_id_fkey FOREIGN KEY (cmd_id) REFERENCES kiosk_ops.unlock_tokens(cmd_id);


--
-- Name: mqtt_inbox mqtt_inbox_cmd_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.mqtt_inbox
    ADD CONSTRAINT mqtt_inbox_cmd_id_fkey FOREIGN KEY (cmd_id) REFERENCES kiosk_ops.unlock_tokens(cmd_id);


--
-- Name: mqtt_inbox mqtt_inbox_kiosk_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.mqtt_inbox
    ADD CONSTRAINT mqtt_inbox_kiosk_id_fkey FOREIGN KEY (kiosk_id) REFERENCES kiosk_ops.kiosks(id);


--
-- Name: slot_assignments slot_assignments_slot_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.slot_assignments
    ADD CONSTRAINT slot_assignments_slot_id_fkey FOREIGN KEY (slot_id) REFERENCES kiosk_ops.slots(id);


--
-- Name: slots slots_bouquet_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.slots
    ADD CONSTRAINT slots_bouquet_id_fkey FOREIGN KEY (bouquet_id) REFERENCES kiosk_ops.bouquets(id);


--
-- Name: slots slots_kiosk_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.slots
    ADD CONSTRAINT slots_kiosk_id_fkey FOREIGN KEY (kiosk_id) REFERENCES kiosk_ops.kiosks(id);


--
-- Name: staff_incident_actions staff_incident_actions_actor_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.staff_incident_actions
    ADD CONSTRAINT staff_incident_actions_actor_id_fkey FOREIGN KEY (actor_id) REFERENCES identity.users(id);


--
-- Name: staff_incident_actions staff_incident_actions_incident_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.staff_incident_actions
    ADD CONSTRAINT staff_incident_actions_incident_id_fkey FOREIGN KEY (incident_id) REFERENCES ordering.disputes(id);


--
-- Name: staff_incident_actions staff_incident_actions_task_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.staff_incident_actions
    ADD CONSTRAINT staff_incident_actions_task_id_fkey FOREIGN KEY (task_id) REFERENCES kiosk_ops.staff_tasks(id);


--
-- Name: staff_stock_actions staff_stock_actions_actor_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.staff_stock_actions
    ADD CONSTRAINT staff_stock_actions_actor_id_fkey FOREIGN KEY (actor_id) REFERENCES identity.users(id);


--
-- Name: staff_stock_actions staff_stock_actions_bouquet_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.staff_stock_actions
    ADD CONSTRAINT staff_stock_actions_bouquet_id_fkey FOREIGN KEY (bouquet_id) REFERENCES kiosk_ops.bouquets(id);


--
-- Name: staff_stock_actions staff_stock_actions_product_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.staff_stock_actions
    ADD CONSTRAINT staff_stock_actions_product_id_fkey FOREIGN KEY (product_id) REFERENCES catalog.flower_products(id);


--
-- Name: staff_stock_actions staff_stock_actions_slot_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.staff_stock_actions
    ADD CONSTRAINT staff_stock_actions_slot_id_fkey FOREIGN KEY (slot_id) REFERENCES kiosk_ops.slots(id);


--
-- Name: staff_stock_actions staff_stock_actions_task_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.staff_stock_actions
    ADD CONSTRAINT staff_stock_actions_task_id_fkey FOREIGN KEY (task_id) REFERENCES kiosk_ops.staff_tasks(id);


--
-- Name: staff_tasks staff_tasks_assigned_by_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.staff_tasks
    ADD CONSTRAINT staff_tasks_assigned_by_fkey FOREIGN KEY (assigned_by) REFERENCES identity.users(id);


--
-- Name: staff_tasks staff_tasks_assignee_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.staff_tasks
    ADD CONSTRAINT staff_tasks_assignee_id_fkey FOREIGN KEY (assignee_id) REFERENCES identity.users(id);


--
-- Name: staff_tasks staff_tasks_incident_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.staff_tasks
    ADD CONSTRAINT staff_tasks_incident_id_fkey FOREIGN KEY (incident_id) REFERENCES ordering.disputes(id);


--
-- Name: staff_tasks staff_tasks_kiosk_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.staff_tasks
    ADD CONSTRAINT staff_tasks_kiosk_id_fkey FOREIGN KEY (kiosk_id) REFERENCES kiosk_ops.kiosks(id);


--
-- Name: staff_tasks staff_tasks_seller_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.staff_tasks
    ADD CONSTRAINT staff_tasks_seller_id_fkey FOREIGN KEY (seller_id) REFERENCES identity.sellers(id);


--
-- Name: unlock_tokens unlock_tokens_kiosk_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.unlock_tokens
    ADD CONSTRAINT unlock_tokens_kiosk_id_fkey FOREIGN KEY (kiosk_id) REFERENCES kiosk_ops.kiosks(id);


--
-- Name: unlock_tokens unlock_tokens_slot_id_fkey; Type: FK CONSTRAINT; Schema: kiosk_ops; Owner: florabot
--

ALTER TABLE ONLY kiosk_ops.unlock_tokens
    ADD CONSTRAINT unlock_tokens_slot_id_fkey FOREIGN KEY (slot_id) REFERENCES kiosk_ops.slots(id);


--
-- Name: customer_seller_chat_images customer_seller_chat_images_message_chat_fk; Type: FK CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.customer_seller_chat_images
    ADD CONSTRAINT customer_seller_chat_images_message_chat_fk FOREIGN KEY (message_id, chat_id) REFERENCES ordering.customer_seller_chat_messages(id, chat_id) ON DELETE CASCADE;


--
-- Name: customer_seller_chat_images customer_seller_chat_images_uploaded_by_fkey; Type: FK CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.customer_seller_chat_images
    ADD CONSTRAINT customer_seller_chat_images_uploaded_by_fkey FOREIGN KEY (uploaded_by) REFERENCES identity.users(id);


--
-- Name: customer_seller_chat_messages customer_seller_chat_messages_chat_id_fkey; Type: FK CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.customer_seller_chat_messages
    ADD CONSTRAINT customer_seller_chat_messages_chat_id_fkey FOREIGN KEY (chat_id) REFERENCES ordering.customer_seller_chats(id);


--
-- Name: customer_seller_chat_messages customer_seller_chat_messages_sender_user_id_fkey; Type: FK CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.customer_seller_chat_messages
    ADD CONSTRAINT customer_seller_chat_messages_sender_user_id_fkey FOREIGN KEY (sender_user_id) REFERENCES identity.users(id);


--
-- Name: customer_seller_chat_products customer_seller_chat_products_chat_id_fkey; Type: FK CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.customer_seller_chat_products
    ADD CONSTRAINT customer_seller_chat_products_chat_id_fkey FOREIGN KEY (chat_id) REFERENCES ordering.customer_seller_chats(id) ON DELETE CASCADE;


--
-- Name: customer_seller_chat_products customer_seller_chat_products_product_id_fkey; Type: FK CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.customer_seller_chat_products
    ADD CONSTRAINT customer_seller_chat_products_product_id_fkey FOREIGN KEY (product_id) REFERENCES catalog.flower_products(id);


--
-- Name: customer_seller_chats customer_seller_chats_customer_id_fkey; Type: FK CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.customer_seller_chats
    ADD CONSTRAINT customer_seller_chats_customer_id_fkey FOREIGN KEY (customer_id) REFERENCES identity.users(id);


--
-- Name: customer_seller_chats customer_seller_chats_seller_id_fkey; Type: FK CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.customer_seller_chats
    ADD CONSTRAINT customer_seller_chats_seller_id_fkey FOREIGN KEY (seller_id) REFERENCES identity.sellers(id);


--
-- Name: disputes disputes_order_id_fkey; Type: FK CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.disputes
    ADD CONSTRAINT disputes_order_id_fkey FOREIGN KEY (order_id) REFERENCES ordering.orders(id);


--
-- Name: order_items order_items_order_id_fkey; Type: FK CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.order_items
    ADD CONSTRAINT order_items_order_id_fkey FOREIGN KEY (order_id) REFERENCES ordering.orders(id);


--
-- Name: web_requests web_requests_order_id_fkey; Type: FK CONSTRAINT; Schema: ordering; Owner: florabot
--

ALTER TABLE ONLY ordering.web_requests
    ADD CONSTRAINT web_requests_order_id_fkey FOREIGN KEY (order_id) REFERENCES ordering.orders(id);


--
-- Name: gateway_orders gateway_orders_payment_id_fkey; Type: FK CONSTRAINT; Schema: payment; Owner: florabot
--

ALTER TABLE ONLY payment.gateway_orders
    ADD CONSTRAINT gateway_orders_payment_id_fkey FOREIGN KEY (payment_id) REFERENCES payment.payments(id);


--
-- Name: payments payments_parent_payment_id_fkey; Type: FK CONSTRAINT; Schema: payment; Owner: florabot
--

ALTER TABLE ONLY payment.payments
    ADD CONSTRAINT payments_parent_payment_id_fkey FOREIGN KEY (parent_payment_id) REFERENCES payment.payments(id);


--
-- PostgreSQL database dump complete
--

\unrestrict rX5zVB14TI04ajpbz6lflvgQX4fdRw5SkNupG4VXIJFEPnp3Mqvj4UJIv7wXY3Z


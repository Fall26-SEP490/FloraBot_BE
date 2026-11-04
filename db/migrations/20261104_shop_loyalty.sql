-- 20261104_shop_loyalty.sql
-- Migration: Nov 4 User Decision - Per-Shop Loyalty Balances, 1% Preorder Earn, Walk-In Zero, Debt Tracking on Earn Spent
-- D3: Explicit order provenance marker (loyalty_policy: LEGACY_GLOBAL vs SHOP_POLICY) replaces hardcoded dates.

BEGIN;

-- 0. Add explicit provenance marker to ordering.orders
-- Backfills LEGACY_GLOBAL for rows present at first migration (preserving SHOP_POLICY for rows already having snapshots).
-- Future INSERTs default to SHOP_POLICY; immune to created_at backdating. Reapplication does not reclassify rows.
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = 'ordering' AND table_name = 'orders' AND column_name = 'loyalty_policy'
  ) THEN
    ALTER TABLE ordering.orders ADD COLUMN loyalty_policy text;
    UPDATE ordering.orders SET loyalty_policy = 'LEGACY_GLOBAL';
    -- A fresh installation has no snapshot table yet. Candidate upgrades may
    -- already contain snapshots; retain those orders without requiring the table.
    IF to_regclass('ordering.order_loyalty_snapshots') IS NOT NULL THEN
      EXECUTE 'UPDATE ordering.orders o SET loyalty_policy = ''SHOP_POLICY''
               WHERE EXISTS (SELECT 1 FROM ordering.order_loyalty_snapshots s WHERE s.order_id = o.id)';
    END IF;
    ALTER TABLE ordering.orders ALTER COLUMN loyalty_policy SET NOT NULL;
    ALTER TABLE ordering.orders ALTER COLUMN loyalty_policy SET DEFAULT 'SHOP_POLICY';
    ALTER TABLE ordering.orders ADD CONSTRAINT orders_loyalty_policy_check
        CHECK (loyalty_policy IN ('LEGACY_GLOBAL', 'SHOP_POLICY'));
  END IF;
END $$;

-- 1. Seller loyalty settings: opt-in per seller, default OFF, fixed rate 1%
CREATE TABLE IF NOT EXISTS identity.seller_loyalty_settings (
    seller_id uuid PRIMARY KEY REFERENCES identity.sellers(id) ON DELETE CASCADE,
    enabled boolean NOT NULL DEFAULT false,
    earn_rate_percent numeric NOT NULL DEFAULT 1.00 CHECK (earn_rate_percent = 1.00),
    created_at timestamptz NOT NULL DEFAULT public.app_now(),
    updated_at timestamptz NOT NULL DEFAULT public.app_now()
);

-- 2. Customer balance per shop: isolated per (customer_id, seller_id), explicit debt tracking
CREATE TABLE IF NOT EXISTS identity.shop_loyalty_balances (
    id uuid PRIMARY KEY DEFAULT public.uuid_v7(),
    customer_id uuid NOT NULL REFERENCES identity.users(id) ON DELETE CASCADE,
    seller_id uuid NOT NULL REFERENCES identity.sellers(id) ON DELETE CASCADE,
    balance bigint NOT NULL DEFAULT 0 CHECK (balance >= 0),
    debt bigint NOT NULL DEFAULT 0 CHECK (debt >= 0),
    created_at timestamptz NOT NULL DEFAULT public.app_now(),
    updated_at timestamptz NOT NULL DEFAULT public.app_now(),
    CONSTRAINT uq_shop_loyalty_balances_customer_seller UNIQUE (customer_id, seller_id)
);

CREATE INDEX IF NOT EXISTS idx_shop_loyalty_balances_customer
    ON identity.shop_loyalty_balances(customer_id, updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_shop_loyalty_balances_seller
    ON identity.shop_loyalty_balances(seller_id);

-- 3. Shop loyalty events / immutable ledger
CREATE TABLE IF NOT EXISTS identity.shop_loyalty_events (
    id uuid PRIMARY KEY DEFAULT public.uuid_v7(),
    customer_id uuid NOT NULL REFERENCES identity.users(id) ON DELETE CASCADE,
    seller_id uuid NOT NULL REFERENCES identity.sellers(id) ON DELETE CASCADE,
    order_id uuid REFERENCES ordering.orders(id) ON DELETE SET NULL,
    event_type text NOT NULL CHECK (event_type IN (
        'POINTS_EARNED',
        'POINTS_REDEEMED',
        'POINTS_RESTORED',
        'POINTS_REVERSED',
        'DEBT_RECORDED',
        'DEBT_REPAID'
    )),
    points bigint NOT NULL CHECK (points >= 0),
    balance_after bigint NOT NULL CHECK (balance_after >= 0),
    debt_after bigint NOT NULL DEFAULT 0 CHECK (debt_after >= 0),
    note text,
    created_at timestamptz NOT NULL DEFAULT public.app_now()
);

CREATE INDEX IF NOT EXISTS idx_shop_loyalty_events_customer
    ON identity.shop_loyalty_events(customer_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_shop_loyalty_events_order
    ON identity.shop_loyalty_events(order_id);

-- 4. Order loyalty snapshots: immutable earn policy snapshot taken at order creation
CREATE TABLE IF NOT EXISTS ordering.order_loyalty_snapshots (
    order_id uuid PRIMARY KEY REFERENCES ordering.orders(id) ON DELETE CASCADE,
    seller_id uuid NOT NULL REFERENCES identity.sellers(id) ON DELETE CASCADE,
    customer_id uuid REFERENCES identity.users(id) ON DELETE SET NULL,
    order_type text NOT NULL CHECK (order_type IN ('PREORDER', 'WALK_IN')),
    policy_version text NOT NULL DEFAULT '20261104_shop_loyalty',
    seller_loyalty_enabled boolean NOT NULL DEFAULT false,
    earn_rate_percent numeric NOT NULL DEFAULT 1.00,
    expected_earn_points bigint NOT NULL DEFAULT 0 CHECK (expected_earn_points >= 0),
    actual_paid_amount bigint NOT NULL DEFAULT 0 CHECK (actual_paid_amount >= 0),
    is_self_order boolean NOT NULL DEFAULT false,
    points_awarded bigint NOT NULL DEFAULT 0 CHECK (points_awarded >= 0),
    awarded_at timestamptz,
    points_refunded_used bigint NOT NULL DEFAULT 0 CHECK (points_refunded_used >= 0),
    points_reversed_earned bigint NOT NULL DEFAULT 0 CHECK (points_reversed_earned >= 0),
    debt_created bigint NOT NULL DEFAULT 0 CHECK (debt_created >= 0),
    created_at timestamptz NOT NULL DEFAULT public.app_now(),
    updated_at timestamptz NOT NULL DEFAULT public.app_now()
);

CREATE INDEX IF NOT EXISTS idx_order_loyalty_snapshots_seller
    ON ordering.order_loyalty_snapshots(seller_id);
CREATE INDEX IF NOT EXISTS idx_order_loyalty_snapshots_customer
    ON ordering.order_loyalty_snapshots(customer_id);

-- 5. Helper function to snapshot order loyalty
CREATE OR REPLACE FUNCTION flow.snapshot_order_loyalty(p_order_id uuid)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    v_order ordering.orders;
    v_order_type text;
    v_is_self_order boolean := false;
    v_seller_enabled boolean := false;
    v_expected_earn bigint := 0;
    v_snap ordering.order_loyalty_snapshots;
BEGIN
    SELECT * INTO v_order FROM ordering.orders WHERE id = p_order_id FOR UPDATE;
    IF NOT FOUND THEN RETURN; END IF;

    -- Legacy orders explicitly marked LEGACY_GLOBAL are preserved and not snapshot
    IF v_order.loyalty_policy = 'LEGACY_GLOBAL' THEN
        RETURN;
    END IF;

    SELECT * INTO v_snap FROM ordering.order_loyalty_snapshots WHERE order_id = p_order_id FOR UPDATE;

    -- Check if web preorder exists
    IF EXISTS (SELECT 1 FROM ordering.web_requests WHERE order_id = p_order_id) THEN
        v_order_type := 'PREORDER';
    ELSE
        v_order_type := 'WALK_IN';
    END IF;

    -- If snapshot exists and order is already finalized or type matches, do not recreate
    IF FOUND THEN
        IF v_snap.order_type = 'WALK_IN' AND v_order_type = 'PREORDER'
           AND v_snap.points_awarded = 0
           AND v_order.loyalty_policy = 'SHOP_POLICY' THEN
            -- Upgrade from initial WALK_IN to PREORDER as web_request was linked before award
            v_seller_enabled := v_snap.seller_loyalty_enabled;
            v_is_self_order := v_snap.is_self_order;
            IF v_seller_enabled AND NOT v_is_self_order AND v_order.customer_id IS NOT NULL THEN
                v_expected_earn := floor(v_order.total_amount * 0.01);
            ELSE
                v_expected_earn := 0;
            END IF;
            UPDATE ordering.order_loyalty_snapshots
            SET order_type = 'PREORDER',
                expected_earn_points = v_expected_earn,
                actual_paid_amount = v_order.total_amount,
                updated_at = public.app_now()
            WHERE order_id = p_order_id;
            UPDATE ordering.orders SET points_earned = v_expected_earn WHERE id = p_order_id;
        END IF;
        RETURN;
    END IF;

    -- Check if self order (customer is seller or seller staff)
    IF v_order.customer_id IS NOT NULL THEN
        v_is_self_order := coalesce((
            SELECT (seller_id = v_order.seller_id)
            FROM identity.users WHERE id = v_order.customer_id
        ), false);
    ELSE
        v_is_self_order := false;
    END IF;

    -- Check seller loyalty setting
    v_seller_enabled := coalesce((
        SELECT enabled
        FROM identity.seller_loyalty_settings
        WHERE seller_id = v_order.seller_id
    ), false);

    -- ONLY PREORDER earns 1% of actual customer paid amount; WALK_IN earns ZERO
    IF v_order_type = 'PREORDER' AND v_seller_enabled AND NOT v_is_self_order AND v_order.customer_id IS NOT NULL THEN
        v_expected_earn := floor(v_order.total_amount * 0.01);
    ELSE
        v_expected_earn := 0;
    END IF;

    INSERT INTO ordering.order_loyalty_snapshots (
        order_id,
        seller_id,
        customer_id,
        order_type,
        policy_version,
        seller_loyalty_enabled,
        earn_rate_percent,
        expected_earn_points,
        actual_paid_amount,
        is_self_order,
        points_awarded,
        points_refunded_used,
        points_reversed_earned,
        debt_created
    ) VALUES (
        v_order.id,
        v_order.seller_id,
        v_order.customer_id,
        v_order_type,
        '20261104_shop_loyalty',
        v_seller_enabled,
        1.00,
        v_expected_earn,
        v_order.total_amount,
        v_is_self_order,
        0,
        0,
        0,
        0
    );

    UPDATE ordering.orders SET points_earned = v_expected_earn WHERE id = p_order_id;
END $$;

-- 6. Trigger on ordering.orders to auto-snapshot new orders
CREATE OR REPLACE FUNCTION ordering.trg_order_snapshot_loyalty_fn()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.loyalty_policy = 'SHOP_POLICY' THEN
        PERFORM flow.snapshot_order_loyalty(NEW.id);
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_order_snapshot_loyalty ON ordering.orders;
CREATE TRIGGER trg_order_snapshot_loyalty
AFTER INSERT ON ordering.orders
FOR EACH ROW EXECUTE FUNCTION ordering.trg_order_snapshot_loyalty_fn();

-- 7. Trigger on ordering.web_requests to update snapshot when preorder links order_id
CREATE OR REPLACE FUNCTION ordering.trg_web_request_order_loyalty_fn()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.order_id IS NOT NULL THEN
        PERFORM flow.snapshot_order_loyalty(NEW.order_id);
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_web_request_order_loyalty ON ordering.web_requests;
CREATE TRIGGER trg_web_request_order_loyalty
AFTER INSERT OR UPDATE OF order_id ON ordering.web_requests
FOR EACH ROW EXECUTE FUNCTION ordering.trg_web_request_order_loyalty_fn();

-- 8. Protect points_earned from being overwritten by legacy recalculations
CREATE OR REPLACE FUNCTION ordering.trg_guard_order_points_earned_fn()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_snap ordering.order_loyalty_snapshots;
BEGIN
    IF NEW.loyalty_policy = 'LEGACY_GLOBAL' THEN
        RETURN NEW;
    END IF;

    SELECT * INTO v_snap FROM ordering.order_loyalty_snapshots WHERE order_id = NEW.id;
    IF FOUND THEN
        NEW.points_earned := v_snap.expected_earn_points;
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_guard_order_points_earned ON ordering.orders;
CREATE TRIGGER trg_guard_order_points_earned
BEFORE UPDATE OF points_earned ON ordering.orders
FOR EACH ROW EXECUTE FUNCTION ordering.trg_guard_order_points_earned_fn();

-- 9. Narrow points function: redeem_points
-- Reuses existing checkout points param, redeems against own shop balance with row lock, no cross-shop/global fallback
CREATE OR REPLACE FUNCTION flow.redeem_points(p_customer uuid, p_points bigint, p_order uuid)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    v_order ordering.orders;
    v_bal bigint;
    v_debt bigint;
BEGIN
    IF p_points IS NULL OR p_points <= 0 THEN RETURN; END IF;

    SELECT * INTO v_order FROM ordering.orders WHERE id = p_order FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Đơn % không tồn tại', p_order;
    END IF;

    -- Explicit legacy classification: orders explicitly marked LEGACY_GLOBAL redeem from legacy global points
    IF v_order.loyalty_policy = 'LEGACY_GLOBAL' THEN
        SELECT loyalty_points INTO v_bal FROM identity.users WHERE id = p_customer AND role IN ('CUSTOMER','SELLER') FOR UPDATE;
        IF v_bal IS NULL THEN RAISE EXCEPTION 'Tài khoản khách % không hợp lệ', p_customer; END IF;
        IF p_points > v_bal THEN RAISE EXCEPTION 'Chỉ còn % điểm, không đủ %', v_bal, p_points; END IF;
        UPDATE identity.users SET loyalty_points = loyalty_points - p_points WHERE id = p_customer;
        PERFORM flow.audit(p_customer, 'POINTS_REDEEMED', 'orders', p_order, jsonb_build_object('points', p_points));
        RETURN;
    END IF;

    -- New Shop Loyalty: derive shop strictly from trusted order.seller_id; NO global fallback
    IF NOT EXISTS (SELECT 1 FROM identity.users WHERE id = p_customer AND role IN ('CUSTOMER','SELLER') AND status = 'ACTIVE') THEN
        RAISE EXCEPTION 'Tài khoản khách % không hợp lệ hoặc đã bị khóa', p_customer;
    END IF;

    SELECT balance, debt INTO v_bal, v_debt
    FROM identity.shop_loyalty_balances
    WHERE customer_id = p_customer AND seller_id = v_order.seller_id
    FOR UPDATE;

    IF NOT FOUND THEN
        v_bal := 0;
        v_debt := 0;
    END IF;

    -- Block redemption if debt exists
    IF v_debt > 0 THEN
        RAISE EXCEPTION 'Tài khoản đang có nợ % điểm tại shop này, không thể đổi điểm cho đến khi thanh toán hết nợ', v_debt;
    END IF;

    IF p_points > v_bal THEN
        RAISE EXCEPTION 'Chỉ còn % điểm tại shop này, không đủ %', v_bal, p_points;
    END IF;

    UPDATE identity.shop_loyalty_balances
    SET balance = balance - p_points,
        updated_at = public.app_now()
    WHERE customer_id = p_customer AND seller_id = v_order.seller_id;

    INSERT INTO identity.shop_loyalty_events (
        customer_id, seller_id, order_id, event_type, points, balance_after, debt_after, note
    ) VALUES (
        p_customer, v_order.seller_id, p_order, 'POINTS_REDEEMED', p_points, v_bal - p_points, v_debt,
        'Đổi điểm cho đơn ' || v_order.order_code
    );

    PERFORM flow.audit(p_customer, 'POINTS_REDEEMED', 'orders', p_order,
        jsonb_build_object('points', p_points, 'seller_id', v_order.seller_id, 'balance', v_bal - p_points));
END $$;

-- 10. Narrow points function: restore_points
-- Restores redeemed points on failed / expired / uncompleted orders atomically exactly once
CREATE OR REPLACE FUNCTION flow.restore_points(p_customer uuid, p_points bigint, p_order uuid)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    v_order ordering.orders;
    v_snap ordering.order_loyalty_snapshots;
    v_bal bigint;
    v_debt bigint;
    v_max_can_restore bigint;
    v_actual_restore bigint;
BEGIN
    IF p_points IS NULL OR p_points <= 0 THEN RETURN; END IF;

    SELECT * INTO v_order FROM ordering.orders WHERE id = p_order FOR UPDATE;
    IF NOT FOUND THEN RETURN; END IF;

    -- Explicit legacy classification: restores to legacy global points
    IF v_order.loyalty_policy = 'LEGACY_GLOBAL' THEN
        UPDATE identity.users SET loyalty_points = loyalty_points + p_points
        WHERE id = p_customer AND role IN ('CUSTOMER','SELLER') AND status = 'ACTIVE';
        IF FOUND THEN PERFORM flow.audit(p_customer, 'POINTS_RESTORED', 'orders', p_order, jsonb_build_object('points', p_points));
        ELSE PERFORM flow.audit(p_customer, 'POINTS_SKIPPED_INACTIVE', 'orders', p_order, jsonb_build_object('points', p_points, 'restore', true)); END IF;
        RETURN;
    END IF;

    -- Active user check
    IF NOT EXISTS (SELECT 1 FROM identity.users WHERE id = p_customer AND role IN ('CUSTOMER','SELLER') AND status = 'ACTIVE') THEN
        PERFORM flow.audit(p_customer, 'POINTS_SKIPPED_INACTIVE', 'orders', p_order, jsonb_build_object('points', p_points, 'restore', true));
        RETURN;
    END IF;

    -- Exactly-once guard on snapshot
    SELECT * INTO v_snap FROM ordering.order_loyalty_snapshots WHERE order_id = p_order FOR UPDATE;
    IF FOUND THEN
        v_max_can_restore := greatest(0, v_order.points_redeemed - v_snap.points_refunded_used);
        v_actual_restore := least(p_points, v_max_can_restore);
        IF v_actual_restore <= 0 THEN
            -- Already restored, avoid duplicate restore
            RETURN;
        END IF;
        UPDATE ordering.order_loyalty_snapshots
        SET points_refunded_used = points_refunded_used + v_actual_restore,
            updated_at = public.app_now()
        WHERE order_id = p_order;
    ELSE
        v_actual_restore := p_points;
    END IF;

    INSERT INTO identity.shop_loyalty_balances (customer_id, seller_id, balance, debt)
    VALUES (p_customer, v_order.seller_id, 0, 0)
    ON CONFLICT (customer_id, seller_id) DO NOTHING;

    SELECT balance, debt INTO v_bal, v_debt
    FROM identity.shop_loyalty_balances
    WHERE customer_id = p_customer AND seller_id = v_order.seller_id
    FOR UPDATE;

    v_bal := v_bal + v_actual_restore;

    UPDATE identity.shop_loyalty_balances
    SET balance = v_bal,
        updated_at = public.app_now()
    WHERE customer_id = p_customer AND seller_id = v_order.seller_id;

    INSERT INTO identity.shop_loyalty_events (
        customer_id, seller_id, order_id, event_type, points, balance_after, debt_after, note
    ) VALUES (
        p_customer, v_order.seller_id, p_order, 'POINTS_RESTORED', v_actual_restore, v_bal, v_debt,
        'Hoàn trả điểm đã đổi của đơn ' || v_order.order_code
    );

    PERFORM flow.audit(p_customer, 'POINTS_RESTORED', 'orders', p_order,
        jsonb_build_object('points', v_actual_restore, 'seller_id', v_order.seller_id, 'balance', v_bal));
END $$;

-- 11. Narrow points function: credit_points
-- Credits earned points upon COMPLETED order; prevents duplicate awards and retries; repays existing debt first
CREATE OR REPLACE FUNCTION flow.credit_points(p_customer uuid, p_points bigint, p_order uuid)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    v_order ordering.orders;
    v_snap ordering.order_loyalty_snapshots;
    v_bal bigint;
    v_debt bigint;
    v_repay bigint := 0;
    v_remaining bigint;
    v_to_award bigint;
BEGIN
    IF p_points IS NULL OR p_points <= 0 THEN RETURN; END IF;

    SELECT * INTO v_order FROM ordering.orders WHERE id = p_order FOR UPDATE;
    IF NOT FOUND THEN RETURN; END IF;

    -- Explicit legacy classification: awards to legacy global points
    IF v_order.loyalty_policy = 'LEGACY_GLOBAL' THEN
        UPDATE identity.users SET loyalty_points = loyalty_points + p_points
        WHERE id = p_customer AND role IN ('CUSTOMER','SELLER') AND status = 'ACTIVE';
        IF FOUND THEN PERFORM flow.audit(p_customer, 'POINTS_EARNED', 'orders', p_order, jsonb_build_object('points', p_points));
        ELSE PERFORM flow.audit(p_customer, 'POINTS_SKIPPED_INACTIVE', 'orders', p_order, jsonb_build_object('points', p_points)); END IF;
        RETURN;
    END IF;

    -- Unpaid, canceled, failed orders do not earn
    IF v_order.status <> 'COMPLETED' THEN
        RETURN;
    END IF;

    -- Active user check
    IF NOT EXISTS (SELECT 1 FROM identity.users WHERE id = p_customer AND role IN ('CUSTOMER','SELLER') AND status = 'ACTIVE') THEN
        PERFORM flow.audit(p_customer, 'POINTS_SKIPPED_INACTIVE', 'orders', p_order, jsonb_build_object('points', p_points));
        RETURN;
    END IF;

    SELECT * INTO v_snap FROM ordering.order_loyalty_snapshots WHERE order_id = p_order FOR UPDATE;
    IF NOT FOUND THEN
        -- Fallback to creating snapshot
        PERFORM flow.snapshot_order_loyalty(p_order);
        SELECT * INTO v_snap FROM ordering.order_loyalty_snapshots WHERE order_id = p_order FOR UPDATE;
    END IF;

    -- Completion / credit retries cannot double award
    IF v_snap.points_awarded > 0 OR v_snap.awarded_at IS NOT NULL THEN
        RETURN;
    END IF;

    -- Must not award if expected earn is 0 (WALK_IN, self-order, or loyalty disabled at creation)
    IF v_snap.expected_earn_points <= 0 THEN
        UPDATE ordering.order_loyalty_snapshots
        SET awarded_at = public.app_now(), updated_at = public.app_now()
        WHERE order_id = p_order;
        RETURN;
    END IF;

    v_to_award := v_snap.expected_earn_points;

    INSERT INTO identity.shop_loyalty_balances (customer_id, seller_id, balance, debt)
    VALUES (p_customer, v_order.seller_id, 0, 0)
    ON CONFLICT (customer_id, seller_id) DO NOTHING;

    SELECT balance, debt INTO v_bal, v_debt
    FROM identity.shop_loyalty_balances
    WHERE customer_id = p_customer AND seller_id = v_order.seller_id
    FOR UPDATE;

    -- Repay debt first if customer owes points
    IF v_debt > 0 THEN
        v_repay := least(v_debt, v_to_award);
        v_remaining := v_to_award - v_repay;
        v_debt := v_debt - v_repay;
        v_bal := v_bal + v_remaining;

        INSERT INTO identity.shop_loyalty_events (
            customer_id, seller_id, order_id, event_type, points, balance_after, debt_after, note
        ) VALUES (
            p_customer, v_order.seller_id, p_order, 'DEBT_REPAID', v_repay, v_bal, v_debt,
            'Khấu trừ nợ điểm từ đơn ' || v_order.order_code
        );
    ELSE
        v_bal := v_bal + v_to_award;
    END IF;

    UPDATE identity.shop_loyalty_balances
    SET balance = v_bal,
        debt = v_debt,
        updated_at = public.app_now()
    WHERE customer_id = p_customer AND seller_id = v_order.seller_id;

    UPDATE ordering.order_loyalty_snapshots
    SET points_awarded = v_to_award,
        awarded_at = public.app_now(),
        updated_at = public.app_now()
    WHERE order_id = p_order;

    INSERT INTO identity.shop_loyalty_events (
        customer_id, seller_id, order_id, event_type, points, balance_after, debt_after, note
    ) VALUES (
        p_customer, v_order.seller_id, p_order, 'POINTS_EARNED', v_to_award, v_bal, v_debt,
        'Tích điểm 1% từ đơn đặt trước ' || v_order.order_code
    );

    PERFORM flow.audit(p_customer, 'POINTS_EARNED', 'orders', p_order,
        jsonb_build_object('points', v_to_award, 'seller_id', v_order.seller_id, 'balance', v_bal, 'debt', v_debt));
END $$;

-- 12. Refund shop loyalty function
-- Reverses earned points, creates explicit debt if already spent, restores redeemed points once
CREATE OR REPLACE FUNCTION flow.refund_shop_loyalty(p_order_id uuid, p_refund_amount bigint)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    v_order ordering.orders;
    v_snap ordering.order_loyalty_snapshots;
    v_bal bigint;
    v_debt bigint;
    v_points_to_reverse bigint := 0;
    v_proportional_points bigint;
    v_max_can_reverse bigint;
    v_deficit bigint;
    v_from_bal bigint;
    v_to_restore bigint := 0;
    v_max_can_restore bigint := 0;
BEGIN
    SELECT * INTO v_order FROM ordering.orders WHERE id = p_order_id FOR UPDATE;
    IF NOT FOUND OR v_order.customer_id IS NULL THEN RETURN; END IF;

    -- Legacy orders are not handled by shop loyalty refunds
    IF v_order.loyalty_policy = 'LEGACY_GLOBAL' THEN RETURN; END IF;

    SELECT * INTO v_snap FROM ordering.order_loyalty_snapshots WHERE order_id = p_order_id FOR UPDATE;
    IF NOT FOUND THEN RETURN; END IF;

    -- A. Reverse earned points if points were awarded
    IF v_snap.points_awarded > 0 THEN
        v_max_can_reverse := greatest(0, v_snap.points_awarded - v_snap.points_reversed_earned);
        IF p_refund_amount >= v_order.total_amount THEN
            v_points_to_reverse := v_max_can_reverse;
        ELSE
            v_proportional_points := floor(p_refund_amount * v_snap.earn_rate_percent / 100);
            v_points_to_reverse := least(v_proportional_points, v_max_can_reverse);
        END IF;

        IF v_points_to_reverse > 0 THEN
            INSERT INTO identity.shop_loyalty_balances (customer_id, seller_id, balance, debt)
            VALUES (v_order.customer_id, v_order.seller_id, 0, 0)
            ON CONFLICT (customer_id, seller_id) DO NOTHING;

            SELECT balance, debt INTO v_bal, v_debt
            FROM identity.shop_loyalty_balances
            WHERE customer_id = v_order.customer_id AND seller_id = v_order.seller_id
            FOR UPDATE;

            IF v_bal >= v_points_to_reverse THEN
                v_bal := v_bal - v_points_to_reverse;
                INSERT INTO identity.shop_loyalty_events (
                    customer_id, seller_id, order_id, event_type, points, balance_after, debt_after, note
                ) VALUES (
                    v_order.customer_id, v_order.seller_id, p_order_id, 'POINTS_REVERSED',
                    v_points_to_reverse, v_bal, v_debt, 'Thu hồi điểm do hoàn tiền đơn ' || v_order.order_code
                );
            ELSE
                v_deficit := v_points_to_reverse - v_bal;
                v_from_bal := v_bal;
                v_bal := 0;
                v_debt := v_debt + v_deficit;

                IF v_from_bal > 0 THEN
                    INSERT INTO identity.shop_loyalty_events (
                        customer_id, seller_id, order_id, event_type, points, balance_after, debt_after, note
                    ) VALUES (
                        v_order.customer_id, v_order.seller_id, p_order_id, 'POINTS_REVERSED',
                        v_from_bal, 0, v_debt, 'Thu hồi một phần điểm do hoàn tiền đơn ' || v_order.order_code
                    );
                END IF;

                INSERT INTO identity.shop_loyalty_events (
                    customer_id, seller_id, order_id, event_type, points, balance_after, debt_after, note
                ) VALUES (
                    v_order.customer_id, v_order.seller_id, p_order_id, 'DEBT_RECORDED',
                    v_deficit, 0, v_debt, 'Ghi nợ điểm do điểm thưởng đã chi tiêu trước hoàn tiền'
                );

                UPDATE ordering.order_loyalty_snapshots
                SET debt_created = debt_created + v_deficit
                WHERE order_id = p_order_id;
            END IF;

            UPDATE identity.shop_loyalty_balances
            SET balance = v_bal, debt = v_debt, updated_at = public.app_now()
            WHERE customer_id = v_order.customer_id AND seller_id = v_order.seller_id;

            UPDATE ordering.order_loyalty_snapshots
            SET points_reversed_earned = points_reversed_earned + v_points_to_reverse,
                updated_at = public.app_now()
            WHERE order_id = p_order_id;

            PERFORM flow.audit(v_order.customer_id, 'POINTS_REVERSED', 'orders', p_order_id,
                jsonb_build_object('reversed', v_points_to_reverse, 'seller_id', v_order.seller_id, 'balance', v_bal, 'debt', v_debt));
        END IF;
    END IF;

    -- B. Return used points if unreturned points remain
    IF v_order.points_redeemed > 0 THEN
        v_max_can_restore := greatest(0, v_order.points_redeemed - v_snap.points_refunded_used);
        IF p_refund_amount >= v_order.total_amount THEN
            v_to_restore := v_max_can_restore;
        ELSE
            v_to_restore := least(floor(p_refund_amount * v_order.points_redeemed / greatest(v_order.total_amount, 1)), v_max_can_restore);
        END IF;

        IF v_to_restore > 0 THEN
            PERFORM flow.restore_points(v_order.customer_id, v_to_restore, p_order_id);
        END IF;
    END IF;
END $$;

-- 13. Trigger on payment.payments to automatically reverse points on successful refund
CREATE OR REPLACE FUNCTION payment.trg_refund_shop_loyalty_fn()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.kind = 'REFUND' AND NEW.status = 'SUCCEEDED' AND (OLD.status IS DISTINCT FROM 'SUCCEEDED') AND NEW.order_id IS NOT NULL THEN
        PERFORM flow.refund_shop_loyalty(NEW.order_id, NEW.amount);
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_payment_refund_shop_loyalty ON payment.payments;
CREATE TRIGGER trg_payment_refund_shop_loyalty
AFTER UPDATE OF status ON payment.payments
FOR EACH ROW EXECUTE FUNCTION payment.trg_refund_shop_loyalty_fn();

COMMIT;

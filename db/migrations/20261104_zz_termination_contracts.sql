-- 20261104_zz_termination_contracts.sql
-- Kiosk Termination typed SQL contracts (Worker B architectural repair)
-- Moves foreign identity and notify table references behind module/schema-owned typed contracts
-- Preserves lock semantics, ordering, pagination, and reader column types.

BEGIN;

-- 1. Identity lookup contract for active actor checks
CREATE OR REPLACE FUNCTION kiosk_ops.v2_term_get_user(p_user_id uuid)
RETURNS TABLE (
  status text,
  role text,
  seller_id uuid
) LANGUAGE sql STABLE SECURITY INVOKER AS $$
  SELECT u.status, u.role, u.seller_id
  FROM identity.users u
  WHERE u.id = p_user_id;
$$;

-- 2. Seller lock contract preserving FOR UPDATE inside caller transaction
CREATE OR REPLACE FUNCTION kiosk_ops.v2_term_lock_seller(p_seller_id uuid)
RETURNS void LANGUAGE plpgsql VOLATILE SECURITY INVOKER AS $$
BEGIN
  PERFORM 1 FROM identity.sellers WHERE id = p_seller_id FOR UPDATE;
END;
$$;

-- 3. Detailed termination request read contract (seller/manager)
CREATE OR REPLACE FUNCTION kiosk_ops.v2_term_get_request(p_id uuid)
RETURNS TABLE (
  id uuid,
  kiosk_id uuid,
  kiosk_code text,
  kiosk_name text,
  seller_id uuid,
  shop_name text,
  status text,
  reason text,
  requested_at timestamptz,
  requested_by uuid,
  review_action text,
  review_reason text,
  reviewed_at timestamptz,
  reviewed_by uuid,
  correlated_task_id uuid,
  completed_at timestamptz,
  completed_by uuid,
  completion_notes text,
  version integer,
  created_at timestamptz,
  updated_at timestamptz
) LANGUAGE sql STABLE SECURITY INVOKER AS $$
  SELECT
    t.id,
    t.kiosk_id,
    k.code AS kiosk_code,
    k.name AS kiosk_name,
    t.seller_id,
    s.shop_name,
    t.status,
    t.reason,
    t.requested_at,
    t.requested_by,
    t.review_action,
    t.review_reason,
    t.reviewed_at,
    t.reviewed_by,
    t.correlated_task_id,
    t.completed_at,
    t.completed_by,
    t.completion_notes,
    t.version,
    t.created_at,
    t.updated_at
  FROM kiosk_ops.kiosk_terminations t
  LEFT JOIN kiosk_ops.kiosks k ON k.id = t.kiosk_id
  LEFT JOIN identity.sellers s ON s.id = t.seller_id
  WHERE t.id = p_id;
$$;

-- 4. Paginated termination request listing contract for Operations Manager
CREATE OR REPLACE FUNCTION kiosk_ops.v2_term_list_requests(
  p_status text,
  p_take integer,
  p_skip integer
)
RETURNS TABLE (
  id uuid,
  kiosk_id uuid,
  kiosk_code text,
  kiosk_name text,
  seller_id uuid,
  shop_name text,
  status text,
  reason text,
  requested_at timestamptz,
  requested_by uuid,
  review_action text,
  review_reason text,
  reviewed_at timestamptz,
  reviewed_by uuid,
  correlated_task_id uuid,
  completed_at timestamptz,
  completed_by uuid,
  completion_notes text,
  version integer,
  created_at timestamptz,
  updated_at timestamptz
) LANGUAGE sql STABLE SECURITY INVOKER AS $$
  SELECT
    t.id,
    t.kiosk_id,
    k.code AS kiosk_code,
    k.name AS kiosk_name,
    t.seller_id,
    s.shop_name,
    t.status,
    t.reason,
    t.requested_at,
    t.requested_by,
    t.review_action,
    t.review_reason,
    t.reviewed_at,
    t.reviewed_by,
    t.correlated_task_id,
    t.completed_at,
    t.completed_by,
    t.completion_notes,
    t.version,
    t.created_at,
    t.updated_at
  FROM kiosk_ops.kiosk_terminations t
  JOIN kiosk_ops.kiosks k ON k.id = t.kiosk_id
  JOIN identity.sellers s ON s.id = t.seller_id
  WHERE (p_status IS NULL OR t.status = p_status)
  ORDER BY t.created_at DESC
  LIMIT p_take OFFSET p_skip;
$$;

-- 5. Notify-owned evidence attachment metadata contract
CREATE OR REPLACE FUNCTION notify.v2_term_attach_evidence(
  p_attachment_id uuid,
  p_termination_id uuid,
  p_file_url text,
  p_mime_type text,
  p_size_bytes bigint,
  p_sha256 text,
  p_actor uuid
) RETURNS void LANGUAGE plpgsql VOLATILE SECURITY INVOKER AS $$
BEGIN
  INSERT INTO notify.attachments (
    id, owner_service, owner_type, owner_id, file_url, mime_type, size_bytes, sha256, phase, uploaded_by
  ) VALUES (
    p_attachment_id, 'kiosk_ops', 'kiosk_termination', p_termination_id,
    p_file_url, p_mime_type, p_size_bytes, p_sha256, 'EVIDENCE', p_actor
  )
  ON CONFLICT (id) DO NOTHING;
END;
$$;

-- 6. Module-owned delegation to notify evidence contract
CREATE OR REPLACE FUNCTION kiosk_ops.v2_term_attach_evidence(
  p_attachment_id uuid,
  p_termination_id uuid,
  p_file_url text,
  p_mime_type text,
  p_size_bytes bigint,
  p_sha256 text,
  p_actor uuid
) RETURNS void LANGUAGE plpgsql VOLATILE SECURITY INVOKER AS $$
BEGIN
  PERFORM notify.v2_term_attach_evidence(
    p_attachment_id, p_termination_id, p_file_url, p_mime_type, p_size_bytes, p_sha256, p_actor
  );
END;
$$;

COMMIT;

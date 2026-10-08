-- Report rows and audit counts share one date/gateway snapshot. Original SQL stays immutable.
CREATE OR REPLACE FUNCTION flow.reconcile_gateway(p_date date, p_statement jsonb, p_admin uuid)
RETURNS TABLE (loai text, gateway_txn_id text, he_thong bigint, sao_ke bigint, ghi_chu text) LANGUAGE plpgsql AS $$
DECLARE n_match int; n_diff int;
BEGIN
  IF jsonb_typeof(p_statement) <> 'array' THEN RAISE EXCEPTION 'Sao kê phải là mảng JSON [{txn, amount}]'; END IF;
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
  SELECT 'MISSING_IN_DB', st.txn, NULL::bigint, st.amount, 'Ngân hàng có tiền vào nhưng hệ thống không có khoản thu (webhook rớt?) -> tra soát, tạo khoản thu tay hoặc hoàn'
    FROM _stmt st LEFT JOIN db ON db.txn = st.txn WHERE db.txn IS NULL
  UNION ALL
  SELECT 'MISSING_IN_STATEMENT', db.txn, db.amount, NULL::bigint, 'Hệ thống ghi SUCCEEDED nhưng sao kê không có -> webhook giả/ghi nhầm, khóa đơn, tra soát ngay'
    FROM db LEFT JOIN _stmt st ON st.txn = db.txn WHERE st.txn IS NULL
  UNION ALL
  SELECT 'AMOUNT_MISMATCH', db.txn, db.amount, st.amount, 'Số tiền lệch -> tra soát'
    FROM db JOIN _stmt st ON st.txn = db.txn WHERE st.amount <> db.amount;
  SELECT count(*) INTO n_match FROM _stmt st JOIN _reconcile_db db ON db.txn=st.txn AND db.amount=st.amount;
  SELECT count(*) INTO n_diff FROM _stmt st FULL JOIN _reconcile_db db ON db.txn=st.txn WHERE st.txn IS NULL OR db.amount IS NULL OR st.amount<>db.amount;
  PERFORM flow.audit(p_admin, 'RECONCILED', 'payments', NULL, jsonb_build_object('date', p_date, 'matched', n_match, 'diff', n_diff));
END $$;

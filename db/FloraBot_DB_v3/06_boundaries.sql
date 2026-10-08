-- Kiểm ranh giới service: FK thật chỉ trong cùng schema; mỗi hàm flow.* ghi trực tiếp tối đa 3 schema nghiệp vụ.
-- notify (audit/tệp) là dịch vụ dùng chung nhận sự kiện, không tính vào 3 schema.
\set ON_ERROR_STOP on
SET client_min_messages = notice;
CREATE TEMP VIEW fn_writes AS
SELECT p.proname AS ham,
       array_agg(DISTINCT m[2] ORDER BY m[2]) AS schema_ghi,
       count(DISTINCT m[2]) FILTER (WHERE m[2] <> 'notify') AS so_schema_nghiep_vu
FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace,
     LATERAL regexp_matches(p.prosrc, '(INSERT\s+INTO|UPDATE|DELETE\s+FROM)\s+(identity|catalog|kiosk_ops|ordering|payment|notify|ai)\.', 'gi') m
WHERE n.nspname = 'flow'
GROUP BY p.proname;
\o /dev/null
DO $$
BEGIN
  PERFORM flow.ok((SELECT count(*) FROM pg_constraint k
                   WHERE k.contype = 'f' AND (SELECT relnamespace FROM pg_class WHERE oid = k.conrelid) <> (SELECT relnamespace FROM pg_class WHERE oid = k.confrelid)) = 0,
                  'Mọi FK thật nằm trong cùng schema (cùng service)');
  PERFORM flow.ok((SELECT count(*) FROM information_schema.tables WHERE table_type = 'BASE TABLE'
                   AND table_schema IN ('identity','catalog','kiosk_ops','ordering','payment','notify','ai')) = 22, 'Đúng 22 bảng (MVP + gói thuê bao)');
  PERFORM flow.ok((SELECT count(*) FROM information_schema.columns c JOIN information_schema.tables t USING (table_schema, table_name)
                   WHERE t.table_type = 'BASE TABLE' AND c.table_schema IN ('identity','catalog','kiosk_ops','ordering','payment','notify','ai')) <= 260, 'Tổng số cột <= 260');
  PERFORM flow.ok((SELECT max(so_schema_nghiep_vu) FROM fn_writes) <= 3, 'Mỗi hàm flow.* ghi trực tiếp tối đa 3 schema nghiệp vụ');
END $$;
\o
\echo '=== HÀM flow.* VÀ CÁC SCHEMA GHI TRỰC TIẾP (đưa vào báo cáo) ==='
SELECT ham, schema_ghi, so_schema_nghiep_vu FROM fn_writes ORDER BY so_schema_nghiep_vu DESC, ham;

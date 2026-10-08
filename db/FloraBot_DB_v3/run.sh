#!/usr/bin/env bash
# Chạy lại toàn bộ: schema -> seed -> flows -> kịch bản -> lịch sử demo 30 ngày -> kiểm ranh giới service -> hồi quy. Cần PostgreSQL 16+.
set -e
cd "$(dirname "$0")"
DB=${DB:-florabot}; PSQL="psql ${PGARGS:--h /tmp -U postgres} -v ON_ERROR_STOP=1 -q"
# 04 không được đổi trạng thái bằng UPDATE tay (mọi thay đổi đi qua flow.*)
if grep -nE "UPDATE[^;]*SET[[:space:]]+status" 04_scenarios.sql; then echo "FAIL: 04_scenarios.sql còn UPDATE ... SET status"; exit 1; fi
$PSQL -d postgres -c "DROP DATABASE IF EXISTS $DB" -c "CREATE DATABASE $DB"
for f in 01_schema.sql 02_seed.sql 03_flows.sql 04_scenarios.sql 05_demo_history.sql 06_boundaries.sql 07_regression.sql; do
  [ -f "$f" ] || continue
  echo ">> $f"; $PSQL -d $DB -f $f
done

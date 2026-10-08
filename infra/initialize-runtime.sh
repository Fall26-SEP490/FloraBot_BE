#!/bin/sh
set -eu
initialized=$(psql -tAc "SELECT to_regclass('identity.sellers') IS NOT NULL")
if [ "$initialized" != "t" ]; then
  # Original source files own their transaction boundaries.
  psql -v ON_ERROR_STOP=1 -q -f /sql/01_schema.sql -f /sql/02_seed.sql -f /sql/03_flows.sql
fi
{
  printf "SELECT pg_advisory_xact_lock(hashtext('florabot-runtime-migrations'));\n"
  cat /migrations/*.sql
} | psql -v ON_ERROR_STOP=1 --single-transaction -q -f -

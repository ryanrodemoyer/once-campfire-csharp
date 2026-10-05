#!/usr/bin/env bash
# Runs campfire_db's differential tests against the campfire-reference image (built by
# `parity/bin/reference build`):
#
#   1. schema identity: the sqlite_master of a fresh reference `db:prepare`, of crates/db/src/schema.sql
#      and of a database the Rust crate created must be the same
#   2. fixtures_match_ruby_row_for_row against the reference's `db:fixtures:load`
#   3. scenario_matches_ruby against the reference after crates/db/ruby/scenario.rb
#   4. rollback: the reference boots on a database the Rust crate wrote (export_database_for_rails)
#      and reads, edits, searches and deletes through it (crates/db/ruby/rollback.rb)
#
# Databases land in OUT (default target/db-differential).
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
OUT=${OUT:-$ROOT/target/db-differential}
export CARGO_TARGET_DIR=${CARGO_TARGET_DIR:-$ROOT/target/db-differential/cargo}
rm -f "$OUT"/*.sqlite3 "$OUT"/*.sql; mkdir -p "$OUT"

# sqlite_master minus what SQLite derives on its own (see crates/db/src/schema.rs).
SCHEMA_QUERY="SELECT sql || ';' FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' AND name NOT LIKE 'message_search_index_%' ORDER BY rowid"

reference() {
  docker run --rm --entrypoint "" \
    --cpus "${PARITY_CPUS:-2}" \
    --user "$(id -u):$(id -g)" \
    --env-file "$ROOT/parity/.env.reference" \
    -e RAILS_ENV=test -e RAILS_LOG_LEVEL=warn -e SCHEMA_QUERY="$SCHEMA_QUERY" \
    -v "$ROOT/crates/db/ruby:/tools:ro" -v "$OUT:/out" \
    campfire-reference:latest sh -ec "$1" 2> >(grep -v -e VIPS -e '^$' >&2)
}

echo "== reference: db:prepare, db:fixtures:load, scenario.rb"
reference '
  db=storage/db/test.sqlite3
  bin/rails db:prepare >/dev/null
  sqlite3 $db "$SCHEMA_QUERY" > /out/schema_ruby.sql
  bin/rails db:fixtures:load
  sqlite3 $db "PRAGMA wal_checkpoint(TRUNCATE)" >/dev/null && cp $db /out/fixtures_ruby.sqlite3
  bin/rails runner /tools/scenario.rb
  sqlite3 $db "PRAGMA wal_checkpoint(TRUNCATE)" >/dev/null && cp $db /out/scenario_ruby.sqlite3'

echo "== campfire_db differential tests"
CAMPFIRE_RUBY_FIXTURES_DB=$OUT/fixtures_ruby.sqlite3 \
CAMPFIRE_RUBY_SCENARIO_DB=$OUT/scenario_ruby.sqlite3 \
CAMPFIRE_EXPORT_DB=$OUT/rust_export.sqlite3 \
  cargo test -p campfire_db -- --ignored --test-threads=2

echo "== schema identity"
sqlite3 "$OUT/rust_export.sqlite3" "$SCHEMA_QUERY" > "$OUT/schema_rust.sql"
diff -u "$ROOT/crates/db/src/schema.sql" "$OUT/schema_ruby.sql"
diff -u "$OUT/schema_ruby.sql" "$OUT/schema_rust.sql"
echo "schema.sql, reference db:prepare and Rust prepare agree"

echo "== reference on the Rust-written database"
reference '
  sqlite3 /out/rust_export.sqlite3 "PRAGMA wal_checkpoint(TRUNCATE)" >/dev/null
  cp /out/rust_export.sqlite3 storage/db/test.sqlite3
  bin/rails runner /tools/rollback.rb'

#!/usr/bin/env bash
# Imports the Rust port's reference-generated golden vectors and their generators from the
# reference-rust submodule, then rewrites vectors/MANIFEST with each vector's SHA-256 and source.
#
#   reference-tools/import.sh
#
# Vectors are copied byte for byte; never edit them by hand. The generator scripts under
# reference-tools/ are copied the same way but may be adapted to this repository afterwards
# (task V01), so a re-import only refreshes them when IMPORT_TOOLS=1.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
src="$root/reference-rust"
testdata=crates/campfire/src/integrations/testdata

# destination under vectors/   source under reference-rust/
vectors=(
  "campfire_routes.json                 vectors/campfire_routes.json"
  "campfire_sessions.json               vectors/campfire_sessions.json"
  "campfire_user_agents.json            vectors/campfire_user_agents.json"
  "rails_compat.json                    vectors/rails_compat.json"
  "ruby_core.json                       vectors/ruby_core.json"
  "storage.json                         vectors/storage.json"
  "rqrcode.json                         crates/campfire/src/controllers/qr_code/testdata/rqrcode.json"
  "richtext/inputs.yml                  crates/richtext/tests/corpus/inputs.yml"
  "richtext/expected.json               crates/richtext/tests/corpus/expected.json"
  "opengraph/cases.json                 $testdata/opengraph_cases.json"
  "opengraph/expected.json              $testdata/opengraph_expected.json"
  "webhook/cases.json                   $testdata/webhook_cases.json"
  "webhook/expected.json                $testdata/webhook_expected.json"
)
for f in "$src"/vectors/storage/*; do
  vectors+=("storage/$(basename "$f") vectors/storage/$(basename "$f")")
done

sha="$(git -C "$src" rev-parse HEAD)"
manifest="$root/vectors/MANIFEST"
mkdir -p "$root/vectors"
{
  echo "# Golden vectors imported by reference-tools/import.sh from basecamp/once-campfire-rust@$sha."
  echo "# sha256  path under vectors/  source path under reference-rust/"
} > "$manifest"

for entry in "${vectors[@]}"; do
  read -r dest from <<< "$entry"
  mkdir -p "$(dirname "$root/vectors/$dest")"
  cp "$src/$from" "$root/vectors/$dest"
  echo "$(sha256sum "$root/vectors/$dest" | cut -d' ' -f1)  $dest  $from" >> "$manifest"
done

if [ -n "${IMPORT_TOOLS:-}" ]; then
  cp -R "$src/reference-tools/." "$root/reference-tools/"
  mkdir -p "$root/reference-tools/oracle"
  cp "$src/$testdata"/oracle/{opengraph.rb,opengraph_cases.py,webhook.rb} "$root/reference-tools/oracle/"
fi

echo "Imported ${#vectors[@]} vectors from reference-rust@$sha"

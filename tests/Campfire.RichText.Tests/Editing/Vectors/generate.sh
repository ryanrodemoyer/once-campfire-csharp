#!/usr/bin/env bash
# The <lexxy-editor> value of every rich text corpus body at this repository's reference pin.
# vectors/richtext/expected.json comes byte for byte from reference-rust, so it records the value at
# the Rust port's older pin, Lexxy's render_custom_attachments_in. On Rails main, which edbc779 runs,
# rich_textarea_tag reads editable_body's to_editor_html through the editor adapter Lexxy registers
# instead (plans/upstream-sync.md, 32b4144). RichTextVectors reads the value from here.
#
#   tests/Campfire.RichText.Tests/Editing/Vectors/generate.sh
#
# Needs Docker and the reference image (parity/bin/reference build).
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/../../../.." && pwd)
OUT=tmp/vectors-editable

# regenerate exits non-zero because the corpus differs from vectors/ at this pin; the file is what's needed
OUT=$OUT "$ROOT/reference-tools/regenerate" richtext >/dev/null || true
test -s "$ROOT/$OUT/richtext/expected.json"

python3 - "$ROOT/$OUT/richtext/expected.json" "$ROOT/tests/Campfire.RichText.Tests/Editing/Vectors/editable.json" \
  "$(git -C "$ROOT/reference" rev-parse --short=7 HEAD)" <<'EOF'
import json, sys

source, target, pin = sys.argv[1:]
corpus = json.load(open(source, encoding="utf-8"))
editable = {
    "generated_by": "tests/Campfire.RichText.Tests/Editing/Vectors/generate.sh",
    "reference": pin,
    "cases": [{"name": case["name"], "editable": case["editable"]} for case in corpus["cases"]],
}
with open(target, "w", encoding="utf-8", newline="\n") as file:
    json.dump(editable, file, ensure_ascii=False, indent=2)
    file.write("\n")
print(f"Wrote {len(editable['cases'])} editor values at {pin} to {target}")
EOF

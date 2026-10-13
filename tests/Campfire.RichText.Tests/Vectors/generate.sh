#!/usr/bin/env bash
# Presentation, plain text, mentions and filtered HTML for every rich text corpus body at this
# repository's reference pin. vectors/richtext/expected.json comes byte for byte from reference-rust,
# so those outputs are the Rust port's older pin. RichTextVectors reads them from pin.json.
# The editor value stays in Editing/Vectors/editable.json.
#
#   tests/Campfire.RichText.Tests/Vectors/generate.sh
#
# Needs Docker and the reference image (parity/bin/reference build).
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/../../../.." && pwd)
OUT=${OUT:-tmp/vectors-pin}

# regenerate exits non-zero because the corpus differs from vectors/ at this pin; the file is what's needed
OUT=$OUT "$ROOT/reference-tools/regenerate" richtext >/dev/null || true
test -s "$ROOT/$OUT/richtext/expected.json"

python3 - "$ROOT/$OUT/richtext/expected.json" "$ROOT/tests/Campfire.RichText.Tests/Vectors/pin.json" \
  "$(git -C "$ROOT/reference" rev-parse --short=7 HEAD)" <<'EOF'
import json, sys

source, target, pin = sys.argv[1:]
corpus = json.load(open(source, encoding="utf-8"))
fields = (
    "presentation",
    "presentation_raised",
    "presentation_raised_message",
    "plain_text",
    "mentioned",
    "filtered",
)
pinned = {
    "generated_by": "tests/Campfire.RichText.Tests/Vectors/generate.sh",
    "reference": pin,
    "cases": [{ "name": case["name"], **{field: case[field] for field in fields} } for case in corpus["cases"]],
}
with open(target, "w", encoding="utf-8", newline="\n") as file:
    json.dump(pinned, file, ensure_ascii=False, indent=2)
    file.write("\n")
print(f"Wrote {len(pinned['cases'])} pin outputs at {pin} to {target}")
EOF

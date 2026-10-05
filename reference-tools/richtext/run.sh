#!/usr/bin/env bash
# Regenerates vectors/richtext/expected.json from inputs.yml by running the real Rails pipeline in
# the reference app (into tmp/vectors/richtext/, then checked against vectors/). See ../regenerate.
set -euo pipefail
exec "$(dirname "$0")/../regenerate" richtext

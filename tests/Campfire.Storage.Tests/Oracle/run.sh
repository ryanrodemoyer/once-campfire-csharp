#!/usr/bin/env bash
# Writes blobs with the C# port (OracleExportTests), then reads them with Active Storage at the
# reference's Rails revision (read.rb). Needs Ruby; run `bundle install` here first.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../../.." && pwd)"
dir="$(mktemp -d)"
trap 'rm -rf "$dir"' EXIT

CAMPFIRE_STORAGE_ORACLE_DIR="$dir" dotnet test "$root/tests/Campfire.Storage.Tests" -c Release --nologo \
  --filter "FullyQualifiedName~OracleExportTests" >/dev/null
cd "$here"
bundle exec ruby read.rb "$dir"

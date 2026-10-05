#!/usr/bin/env bash
# Boot Claude Code cloud sessions with the pinned .NET SDK, the references and restored packages,
# so bin/check works straight away. Local sessions run bin/setup themselves.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

cd "$CLAUDE_PROJECT_DIR"
bin/setup

if [ -x "$HOME/.dotnet/dotnet" ] && [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  echo "export PATH=\"$HOME/.dotnet:\$PATH\"" >> "$CLAUDE_ENV_FILE"
fi

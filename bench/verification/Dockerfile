# Orchestrator for basecamp/once-campfire-verification. The host has no Ruby or
# sqlite3 CLI. This image runs bin/benchmark, taskset and ffprobe. The app
# containers it starts are siblings on the host daemon, not children of this image.
FROM ruby:3.4-slim
RUN apt-get update && apt-get install --no-install-recommends -y \
      sqlite3 git util-linux ffmpeg ca-certificates \
    && rm -rf /var/lib/apt/lists/*

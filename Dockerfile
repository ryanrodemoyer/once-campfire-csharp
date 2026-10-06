# syntax = docker/dockerfile:1
#
# Production image for the C# port, laid out like the reference's (reference/Dockerfile): a
# non-root user with uid 1000, the app in /rails with its data under /rails/storage/{db,files,
# backups}, the same environment (RAILS_ENV, SECRET_KEY_BASE, VAPID_*, DISABLE_SSL, TLS_DOMAIN,
# APP_VERSION, GIT_REVISION), the ONCE backup/restore hooks, and bin/boot as the command.
#
#   docker build -t campfire-csharp --build-arg APP_VERSION=... --build-arg GIT_REVISION=... .
#   docker run -e SECRET_KEY_BASE=... -v campfire:/rails/storage -p 3000:3000 campfire-csharp
#
# bin/boot is the reference's bin/start-app: db:prepare, then the app on PORT (3000) on every
# interface. Thruster's public listener on 80 and 443 in front of it is task P03; the pinned media
# toolchain (libvips, ffmpeg) is task P02.
#
# The runtime is Debian trixie, as the reference's ruby:3.4-slim is, so P02 can add the same
# Debian media libraries. Microsoft ships no Debian image for .NET 10, so the app is published
# self-contained and needs only the libraries .NET itself loads.

ARG DOTNET_SDK_VERSION=10.0
ARG DEBIAN_RELEASE=trixie


# Restore, publish and build the frontend with the SDK, for the target architecture.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS build
ARG TARGETARCH
ARG SOURCE_DATE_EPOCH
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src src
RUN --mount=type=cache,target=/root/.nuget/packages \
    rid=linux-$([ "$TARGETARCH" = arm64 ] && echo arm64 || echo x64) && \
    dotnet publish src/Campfire.Server/Campfire.Server.csproj -c Release -r "$rid" --self-contained \
      -p:DebugType=none -o /out/app && \
    rm -f /out/app/*.xml

# bin/build-assets: assets:precompile without Ruby or Node (assets/README.md). SOURCE_DATE_EPOCH,
# when given, makes every file's last-modified reproducible.
COPY assets assets
COPY reference reference
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet run assets/build-assets.cs -- /src /out/app/assets


FROM docker.io/library/debian:${DEBIAN_RELEASE}-slim

# ca-certificates: the system CA store, for webhooks, unfurling and Web Push. The libraries the .NET
# runtime loads (libstdc++, libssl) are already in the slim image; ICU isn't needed, since
# globalization is invariant.
RUN apt-get update -qq && \
    apt-get install --no-install-recommends -y ca-certificates && \
    rm -rf /var/lib/apt/lists /var/cache/apt/archives

# Image metadata
ARG OCI_DESCRIPTION
LABEL org.opencontainers.image.description="${OCI_DESCRIPTION}"
ARG OCI_SOURCE
LABEL org.opencontainers.image.source="${OCI_SOURCE}"
LABEL org.opencontainers.image.licenses="MIT"

# Run and own only the runtime files as a non-root user, as the reference does.
RUN groupadd --system --gid 1000 rails && \
    useradd rails --uid 1000 --gid 1000 --create-home --shell /bin/bash

COPY --from=build /out/app /app
RUN ln -s /app/campfire /usr/local/bin/campfire

WORKDIR /rails

COPY --chmod=755 <<'EOF' /rails/bin/boot
#!/bin/sh
exec /usr/local/bin/campfire server
EOF

# Rails.root.join("storage"): storage/db/<env>.sqlite3, storage/files (Active Storage) and
# storage/backups (the ONCE hooks).
RUN mkdir -p /rails/storage/db /rails/storage/files /rails/storage/backups /rails/tmp && \
    chown -R 1000:1000 /rails

# ONCE backup/restore hooks. pre-backup is script/admin/prepare-backup (`campfire backup`, P04);
# post-restore is the reference's own script.
COPY --chmod=755 <<'EOF' /hooks/pre-backup
#!/bin/bash
cd /rails
exec /usr/local/bin/campfire backup
EOF
COPY --chmod=755 reference/hooks/post-restore /hooks/post-restore

USER 1000:1000

# Configure environment defaults. The HTTP_* timeouts are Thruster's, for P03's front server.
ENV RAILS_ENV="production" \
    CAMPFIRE_ASSETS_PATH="/app/assets" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV HTTP_IDLE_TIMEOUT=60
ENV HTTP_READ_TIMEOUT=300
ENV HTTP_WRITE_TIMEOUT=300

# Set version and revision
ARG APP_VERSION
ENV APP_VERSION=$APP_VERSION
ARG GIT_REVISION
ENV GIT_REVISION=$GIT_REVISION

# The app's own listener; P03 puts Thruster's 80 and 443 in front of it.
EXPOSE 3000

# Start the server by default, this can be overwritten at runtime
CMD ["bin/boot"]

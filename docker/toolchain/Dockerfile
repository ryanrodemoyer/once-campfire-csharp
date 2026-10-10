# syntax = docker/dockerfile:1
#
# The pinned media toolchain image: libvips 8.16.1 and ffmpeg 7.1.5, the exact Debian trixie
# packages the reference image installs (`apt-get install libvips ffmpeg` on ruby:3.4-slim, which
# is trixie — `libvips` is a virtual package that resolves to libvips42t64), pinned by version
# for amd64 and arm64. Being the same .debs as the reference image's is the strongest form of
# byte parity: the variants and preview images the media golden set compares
# (tests/Campfire.Storage.Tests/Media/MediaGoldenTests.cs, against vectors/storage.json) come
# out byte for byte the same as the reference's, on both architectures.
#
#   docker build -t campfire-csharp-toolchain docker/toolchain
#   docker/toolchain/media-tests amd64
#   docker/toolchain/media-tests arm64
#
# The gate: the byte comparisons only run when the local tools are the versions that made the
# vectors (vectors/storage.json `versions`: libvips 8.16.1, ffmpeg 7.1.5-0+deb13u1), and the
# toolchain stage sets CAMPFIRE_REQUIRE_MEDIA_VECTORS=1, so any other version fails the tests
# instead of skipping. If Debian trixie moves on to newer packages, the pinned install below
# fails this build — regenerate the vectors then (a rebuilt reference image would drift the
# same way; see docker/toolchain/README.md).
#
# NetVips loads libvips.so.42 from the system (src/Campfire.Storage/Media/LibVips.cs); it is
# never NetVips.Native. ffmpeg and ffprobe come from PATH.
#
# The production image (the root Dockerfile, P01's) takes the same two pinned packages in its
# runtime stage; docker/toolchain/README.md has the lines, and the `media` stage below is that
# runtime set on its own.

ARG DOTNET_SDK_VERSION=10.0
ARG DEBIAN_RELEASE=trixie
ARG LIBVIPS_PACKAGE_VERSION=8.16.1-1+deb13u1
ARG FFMPEG_PACKAGE_VERSION=7:7.1.5-0+deb13u1
# vectors/storage.json versions.ffmpeg: the first `ffmpeg -version` line, which the tests compare.
ARG FFMPEG_VERSION_LINE="ffmpeg version 7.1.5-0+deb13u1 Copyright (c) 2000-2026 the FFmpeg developers"


# The .NET SDK, for the stage below: Microsoft ships no Debian .NET 10 SDK image (only Ubuntu,
# Azure Linux and CBL-Mariner), so this Ubuntu image's /usr/share/dotnet comes over whole. It
# resolves for the build's target platform. An SDK that no longer satisfies global.json makes
# every `dotnet` command in the image fail with its version rule, so the image stays pinned to
# the repo's SDK while that holds.
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS dotnet-sdk


# The pinned media packages on their own: what a runtime image carries. The exact versions are
# the ones the vectors were made with; apt's signed package indexes verify the .debs, and
# installing `ffmpeg` from the same source pins its libav* libraries too (they are built and
# published together, and the version line check below fails the build if they drift).
FROM docker.io/library/debian:${DEBIAN_RELEASE}-slim AS media
ARG LIBVIPS_PACKAGE_VERSION
ARG FFMPEG_PACKAGE_VERSION
ARG FFMPEG_VERSION_LINE
RUN apt-get update -qq && \
    apt-get install --no-install-recommends -y \
      libvips42t64="${LIBVIPS_PACKAGE_VERSION}" ffmpeg="${FFMPEG_PACKAGE_VERSION}" && \
    ffmpeg -version | head -1 | grep -qxF "${FFMPEG_VERSION_LINE}" && \
    rm -rf /var/lib/apt/lists /var/cache/apt/archives


# The toolchain the media golden tests run in, with the source bind-mounted: the media packages
# above plus the .NET SDK and the strictness flag.
FROM media AS toolchain
COPY --from=dotnet-sdk /usr/share/dotnet /usr/share/dotnet
# The SDK's own host needs ICU (the app and the tests don't: InvariantGlobalization, see
# Directory.Build.props). The SDK image's Ubuntu libicu74 doesn't come over with /usr/share/dotnet.
# ca-certificates: the system CA store, for NuGet restore (trixie-slim has none), as the
# production runtime installs too.
RUN apt-get update -qq && \
    apt-get install --no-install-recommends -y ca-certificates libicu76 && \
    rm -rf /var/lib/apt/lists /var/cache/apt/archives && \
    ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    CAMPFIRE_REQUIRE_MEDIA_VECTORS=1
WORKDIR /src

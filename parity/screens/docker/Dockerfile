# syntax = docker/dockerfile:1
#
# The parity candidate image for the C# port: the unmodified C# production image (built from
# /Dockerfile as campfire-csharp:app) plus what the harness needs around it, mirroring
# reference-rust/parity/docker/candidate/Dockerfile for the Rust candidate and parity/docker/Dockerfile
# for the reference. App code is untouched.
#
#   * libfaketime (built exactly as for the reference image), so the server clock can start at a
#     chosen instant and tick (FAKETIME, see entrypoint); --freeze also sets CAMPFIRE_FROZEN_TIME
#   * sqlite3 and procps, for inspecting databases and processes
#
# Built by parity/screens/candidate build.

ARG BASE_IMAGE=campfire-csharp:app

FROM docker.io/library/debian:trixie-slim AS faketime
RUN apt-get update -qq && apt-get install --no-install-recommends -y build-essential ca-certificates curl
ARG FAKETIME_VERSION=0.9.10
ARG FAKETIME_SHA256=729ad33b9c750a50d9c68e97b90499680a74afd1568d859c574c0fe56fe7947f
# Without FAKE_PTHREAD, as for the reference (parity/docker/Dockerfile).
RUN curl -fsSL -o /tmp/libfaketime.tar.gz https://github.com/wolfcw/libfaketime/archive/refs/tags/v${FAKETIME_VERSION}.tar.gz && \
    echo "${FAKETIME_SHA256}  /tmp/libfaketime.tar.gz" | sha256sum -c - && \
    tar xzf /tmp/libfaketime.tar.gz -C /tmp && \
    sed -i 's/-Werror //' /tmp/libfaketime-${FAKETIME_VERSION}/src/Makefile && \
    make -C /tmp/libfaketime-${FAKETIME_VERSION}/src FAKETIME_COMPILE_CFLAGS="-UFAKE_PTHREAD" libfaketime.so.1 && \
    install -D -m 644 /tmp/libfaketime-${FAKETIME_VERSION}/src/libfaketime.so.1 /usr/local/lib/faketime/libfaketime.so.1

FROM ${BASE_IMAGE}

USER root

RUN apt-get update -qq && \
    apt-get install --no-install-recommends -y sqlite3 procps && \
    rm -rf /var/lib/apt/lists /var/cache/apt/archives

COPY --from=faketime /usr/local/lib/faketime/libfaketime.so.1 /usr/local/lib/faketime/libfaketime.so.1
COPY --chmod=755 entrypoint /usr/local/bin/parity-entrypoint

# Instances run as the host user (so seed copies stay writable on the host), which may not be
# uid 1000: let any uid write the storage directories.
RUN chmod -R a+rwX /rails/storage

USER 1000:1000

ENV TZ=UTC

ENTRYPOINT ["/usr/local/bin/parity-entrypoint"]
CMD ["bin/boot"]
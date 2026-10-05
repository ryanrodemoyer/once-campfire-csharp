# syntax = docker/dockerfile:1
#
# The parity reference image: the unmodified reference app image (built from reference/Dockerfile as
# campfire-reference:app) plus what the harness needs around it. App code is untouched; the one
# addition to its config is an initializer pinning Action Cable's worker pool (below).
#
#   * libfaketime, so the server clock can start at a chosen instant (FAKETIME, see entrypoint)
#   * sqlite3 and procps, for inspecting seeds, state dumps and processes
#   * a one-worker resque-pool.yml (the reference sizes it at half the CPU count, per instance)
#   * config/initializers/parity_action_cable.rb: Action Cable handles each connection's commands
#     in order (worker_pool_size = 1), as the candidate's cable server must; see that file
#   * test/fixtures, which reference/.dockerignore keeps out of the app image; the seed builder
#     loads them. Passed in as a named build context:
#       docker build --build-context fixtures=reference/test/fixtures -f parity/docker/Dockerfile parity/docker
#
# Built by `parity/bin/reference build`.

ARG BASE_IMAGE=campfire-reference:app

# libfaketime built without FAKE_PTHREAD. With it (as Debian builds it), every timed wait in Ruby
# (ConditionVariable#wait, Queue#pop(timeout:)) returns immediately and loops, so Puma and Resque
# spin at 100% CPU under any FAKETIME.
FROM ${BASE_IMAGE} AS faketime
USER root
RUN apt-get update -qq && apt-get install --no-install-recommends -y build-essential ca-certificates curl
ARG FAKETIME_VERSION=0.9.10
ARG FAKETIME_SHA256=729ad33b9c750a50d9c68e97b90499680a74afd1568d859c574c0fe56fe7947f
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

COPY --from=fixtures --chown=1000:1000 . /rails/test/fixtures
COPY --chmod=755 entrypoint /usr/local/bin/parity-entrypoint
COPY --chmod=755 redis-server /usr/local/bin/redis-server
COPY --chown=1000:1000 resque-pool.yml /rails/config/resque-pool.yml
COPY --chown=1000:1000 parity_action_cable.rb /rails/config/initializers/parity_action_cable.rb

# Instances run as the host user (so seed copies stay writable on the host), which may not be
# uid 1000: let any uid write the runtime scratch directories.
RUN mkdir -p /rails/storage/db /rails/storage/files && \
    chmod -R a+rwX /rails/tmp /rails/log /rails/storage

USER 1000:1000

ENV TZ=UTC

ENTRYPOINT ["/usr/local/bin/parity-entrypoint"]
CMD ["bin/boot"]

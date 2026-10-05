# Parity-only (baked into the campfire-reference image by parity/docker/Dockerfile, not part of
# reference/): Action Cable runs each connection's commands on a single worker thread.
#
# Action Cable hands every incoming command (subscribe, unsubscribe, message) to a worker pool of
# `worker_pool_size` threads (4 by default), so two commands a client sends back to back can run in
# either order. Every room page sends exactly such a pair: when the sidebar turbo-frame replaces its
# turbo-cable-stream-source elements, the old one unsubscribes and the new one subscribes with the
# same identifier. When the subscribe wins the race the server drops it as a duplicate and never
# confirms it, so the page renders differently from run to run. A port's cable server handles a
# connection's commands in order; one worker makes the oracle do the same, deterministically.
#
# PARITY_CABLE_WORKER_POOL_SIZE overrides it (for experiments only).
pool_size = Integer(ENV.fetch("PARITY_CABLE_WORKER_POOL_SIZE", "1"))

Rails.application.config.action_cable.worker_pool_size = pool_size

# The engine copies config.action_cable onto the server config when ActionCable::Server::Base
# loads; set it there too in case that already happened. The pool itself is created lazily, on
# the first connection.
ActiveSupport.on_load(:action_cable) { self.worker_pool_size = pool_size }

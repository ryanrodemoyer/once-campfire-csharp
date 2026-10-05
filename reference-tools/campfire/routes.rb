# Route table and path recognition as the reference's router sees them, for
# crates/campfire/src/controllers (route coverage and Rails' first-match precedence).
#
#   parity/bin/reference runner reference-tools/campfire/routes.rb > vectors/campfire_routes.json
require "json"

routes = Rails.application.routes.routes.filter_map do |route|
  next if route.verb.blank? || route.internal
  { verb: route.verb, path: route.path.spec.to_s, endpoint: "#{route.defaults[:controller]}##{route.defaults[:action]}",
    defaults: route.defaults.except(:controller, :action).transform_values(&:to_s) }
end

samples = [
  %w[GET /], %w[HEAD /], %w[GET /rooms], %w[GET /rooms/], %w[GET //rooms//1//], %w[GET /rooms/1], %w[GET /rooms/1.json],
  %w[GET /rooms/1.turbo_stream], %w[GET /rooms/1.2.3], %w[GET /rooms/opens], %w[GET /rooms/closeds], %w[GET /rooms/directs],
  %w[GET /rooms/opens/new], %w[POST /rooms/opens], %w[GET /rooms/opens/5], %w[PATCH /rooms/opens/5], %w[PUT /rooms/directs/5],
  %w[GET /rooms/new], %w[GET /rooms/1/edit], %w[GET /rooms/1/@42], %w[GET /rooms/1/@42.json], %w[GET /rooms/1/messages],
  %w[GET /rooms/1/messages.json], %w[POST /rooms/1/messages], %w[GET /rooms/1/messages/new], %w[GET /rooms/1/messages/2],
  %w[DELETE /rooms/1/messages/2], %w[GET /rooms/1/1-abc/messages], %w[POST /rooms/1/1-abc/messages], %w[POST /rooms/1/1-abc/messages.txt],
  %w[PUT /rooms/1/1-abc/messages/7], %w[POST /rooms/1/1-abc/messages/7/boosts], %w[DELETE /rooms/1/1-abc/messages/7/boosts/3],
  %w[GET /rooms/1/refresh], %w[GET /rooms/1/settings], %w[GET /rooms/1/involvement], %w[PATCH /rooms/1/involvement],
  %w[GET /rooms/1/involvement.turbo_stream], %w[GET /messages/1/boosts], %w[GET /messages/1/boosts/new], %w[POST /messages/1/boosts],
  %w[DELETE /messages/1/boosts/9], %w[GET /messages/new], %w[GET /messages/5], %w[GET /session/new], %w[POST /session], %w[DELETE /session],
  %w[GET /session/transfers/abc-def], %w[PUT /session/transfers/abc], %w[GET /first_run], %w[POST /first_run], %w[GET /account/edit],
  %w[GET /account/users], %w[PATCH /account/users/3], %w[GET /account/bots/new], %w[PATCH /account/bots/3/key], %w[POST /account/join_code],
  %w[GET /account/logo], %w[GET /account/logo.png], %w[DELETE /account/logo], %w[GET /account/custom_styles/edit], %w[PATCH /account/custom_styles],
  %w[GET /join/abc-def-ghi], %w[POST /join/abc-def-ghi], %w[GET /qr_code/aGVsbG8], %w[GET /users/3], %w[GET /users/3/avatar], %w[GET /users/me/avatar.svg],
  %w[DELETE /users/3/avatar], %w[POST /users/3/ban], %w[DELETE /users/3/ban], %w[GET /users/me/sidebar], %w[GET /users/me/profile],
  %w[PATCH /users/me/profile], %w[GET /users/me/push_subscriptions], %w[POST /users/me/push_subscriptions], %w[DELETE /users/me/push_subscriptions/4],
  %w[POST /users/me/push_subscriptions/4/test_notifications], %w[GET /autocompletable/users], %w[GET /autocompletable/users.json],
  %w[GET /searches], %w[POST /searches], %w[DELETE /searches/clear], %w[GET /searches/clear], %w[POST /unfurl_link], %w[GET /webmanifest],
  %w[GET /webmanifest.json], %w[GET /service-worker], %w[GET /service-worker.js], %w[GET /up], %w[GET /up.json], %w[GET /recede_historical_location],
  %w[GET /rails/active_storage/blobs/redirect/abc--def/photo.jpg], %w[GET /rails/active_storage/blobs/redirect/abc--def/dir/photo.tar.gz],
  %w[GET /rails/active_storage/blobs/proxy/abc--def/photo.jpg], %w[GET /rails/active_storage/blobs/abc--def/photo],
  %w[GET /rails/active_storage/representations/redirect/abc--def/var--x/photo.png], %w[GET /rails/active_storage/representations/abc--def/var--x/photo.png],
  %w[GET /rails/active_storage/disk/key--x/photo.jpg], %w[PUT /rails/active_storage/disk/token--x], %w[POST /rails/active_storage/direct_uploads],
  %w[POST /rails/action_mailbox/postmark/inbound_emails], %w[GET /rails/action_mailbox/mandrill/inbound_emails], %w[GET /rails/conductor/action_mailbox/inbound_emails],
  %w[GET /nope], %w[PATCH /rooms], %w[GET /ROOMS/1], %w[GET /rooms/%31], %w[GET /rooms/a%2Fb], %w[GET /rooms/caf%C3%A9], %w[GET /cable]
]

recognitions = samples.map do |verb, path|
  params = Rails.application.routes.recognize_path(path, method: verb.downcase.to_sym)
  { verb: verb, path: path, endpoint: "#{params.delete(:controller)}##{params.delete(:action)}", params: params.transform_values(&:to_s) }
rescue ActionController::RoutingError
  { verb: verb, path: path, endpoint: nil, params: {} }
end

puts JSON.pretty_generate(routes: routes, recognitions: recognitions)

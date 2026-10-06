# Regenerates messages.json: every message in the default parity seed, rendered by the reference
# app's own partials (reference/app/views/messages), for MessageRenderingTests.
#
#   PARITY_RUNTIME=native parity/bin/seed build default    # or Docker, the canonical runtime
#   parity/bin/reference runner --seed default \
#     tests/Campfire.Web.Tests/Helpers/Messages/Vectors/generate.rb \
#     > tests/Campfire.Web.Tests/Helpers/Messages/Vectors/messages.json
#
# Each message is rendered as rooms/show, messages/index and the turbo streams render it:
# `messages/_message` (with `_actions`, `_presentation` and `messages/boosts/_boosts`) and the JSON
# partial the bot API returns. Requests come from http://campfire.test, which is also
# Current.request_host. Fragment caching is off; a cached fragment holds the same bytes.
require "json"

# message_tag logs the messages it can't render; keep stdout for the JSON.
Rails.logger = ActiveSupport::Logger.new($stderr)
ActionController::Base.perform_caching = false

HOST = "campfire.test"
env = Rack::MockRequest.env_for("http://#{HOST}/", "HTTP_HOST" => HOST)
Current.request = ActionDispatch::Request.new(env)
renderer = ApplicationController.renderer.new(http_host: HOST, https: false)

render = ->(**options) { renderer.render(**options) }
html = ->(partial, **locals) { render.(partial: partial, locals: locals, formats: [ :html ]) }
json = ->(partial, **locals) { render.(partial: partial, locals: locals, formats: [ :json ]) }

messages = Message.with_presentation.order(:id).map do |message|
  {
    id: message.id,
    client_message_id: message.client_message_id,
    room_id: message.room_id,
    plain_text_body: message.plain_text_body,
    content_type: message.content_type.to_s,
    html: html.("messages/message", message: message),
    json: json.("messages/message", message: message)
  }
end

boosts = Boost.order(:id).map do |boost|
  {
    id: boost.id,
    html: html.("messages/boosts/boost", boost: boost),
    json: json.("messages/boosts/boost", boost: boost)
  }
end

templates = User.order(:id).map do |user|
  Current.set(user: user) do
    { user_id: user.id, html: html.("messages/template") }
  end
end

# Every sound /play can show, as message_sound_presentation renders it for a message playing it.
sounds = Sound::BUILTIN.map do |sound|
  playing = Struct.new(:sound).new(sound)
  { name: sound.name, html: render.(inline: "<%= send(:message_sound_presentation, message) %>", locals: { message: playing }) }
end

# States the seed doesn't have, made on this throwaway copy of it after the renders above: a
# booster whose user is gone, a direct room with no members left, and a video without dimensions.
labels = JSON.parse(File.read(File.join(ENV.fetch("PARITY_WORK"), "parity/.seed/default/labels.json")))
gone_booster = Message.find(labels.fetch("messages.boosted_many")).boosts.order(:created_at).first
gone_booster.update_columns(booster_id: 999_998)
memberless_room = Room.find(labels.fetch("rooms.group_direct"))
memberless_room.memberships.delete_all
video = Message.find(labels.fetch("messages.video")).attachment.blob
video.update_columns(metadata: video.metadata.except("width", "height"))

scenarios = {
  gone_booster: { message: gone_booster.message, boost_id: gone_booster.id },
  memberless_direct_room: { message: Message.find(labels.fetch("messages.group_direct_first")), room_id: memberless_room.id },
  video_without_dimensions: { message: Message.find(labels.fetch("messages.video")), blob_id: video.id }
}.map do |name, scenario|
  message = Message.with_presentation.find(scenario.delete(:message).id)
  { name: name, message_id: message.id, **scenario, html: html.("messages/message", message: message), json: json.("messages/message", message: message) }
end

puts JSON.pretty_generate(
  host: HOST,
  messages: messages,
  boosts: boosts,
  templates: templates,
  unrenderable: html.("messages/unrenderable"),
  sounds: sounds,
  sound_names: Sound.names,
  scenarios: scenarios
)

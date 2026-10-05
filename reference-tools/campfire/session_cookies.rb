# Session cookies as Rails issues them (`cookies.signed.permanent[:session_token]`, Authentication
# #set_authentication_cookie) for sessions in the default seed, plus a blob redirect path, for
# crates/campfire/src/app/tests.rs.
#
#   parity/bin/reference runner --seed default reference-tools/campfire/session_cookies.rb > vectors/campfire_sessions.json
require "json"

def issue(token)
  request = ActionDispatch::Request.new(Rails.application.env_config.merge("HTTP_HOST" => "campfire.test", "rack.input" => StringIO.new))
  request.cookie_jar.signed.permanent[:session_token] = { value: token, httponly: true, same_site: :lax }
  raw = request.cookie_jar[:session_token]
  { cookie_value: raw, cookie_header: "session_token=#{Rack::Utils.escape(raw)}" }
end

sessions = Session.includes(:user).order(:id).limit(5).map do |session|
  { session_id: session.id, user_id: session.user_id, user_name: session.user.name, token: session.token, **issue(session.token) }
end

blob = ActiveStorage::Blob.joins(:attachments).where(active_storage_attachments: { record_type: "Message" }).order(:id).first
blobs = blob ? [ { blob_id: blob.id, signed_id: blob.signed_id, redirect_path: Rails.application.routes.url_helpers.rails_blob_path(blob, only_path: true) } ] : []

puts JSON.pretty_generate(sessions: sessions, blobs: blobs, forged: issue("not-a-session-token"))

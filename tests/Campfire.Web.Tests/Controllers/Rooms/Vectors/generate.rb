# What the reference answers for rooms#index, rooms#show and the /rooms/:room_id/@:message_id
# deep link: each response's status, headers, cookies and body. Writes pages.json, which
# RoomsControllerTests replays through the C# router and controllers on the same database.
#
# The database is D02's tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 (the default parity
# seed). Run this script in the reference app on a copy of it, with the clock frozen:
#
#   mkdir -p tmp/m03/db tmp/m03/storage
#   cp tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 tmp/m03/db/production.sqlite3
#   parity/bin/reference runner --storage tmp/m03 --time 2026-03-02T16:00:00Z --freeze \
#     tests/Campfire.Web.Tests/Controllers/Rooms/Vectors/generate.rb > tests/Campfire.Web.Tests/Controllers/Rooms/Vectors/pages.json
#
# Requests go straight to Rails.application (the stack Puma runs), in order, on one database.
# The fixtures below are applied first, and the C# test applies the same SQL to its copy.
require "json"

HOST = "campfire.test"
NOW = Time.now.utc

FIXTURES = [
  # Kevin, a member, signed in and active just now (no activity refresh).
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (900001, 712064548, 'KevinSessionToken0000001', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')"
]
FIXTURES.each { |sql| ActiveRecord::Base.connection.execute(sql) }

DAVID = "AxJs94fteQ5Autv2VrKsH68c" # administrator; last active 14:00, so the first request refreshes it
KEVIN = "KevinSessionToken0000001"

ALL_PETS = 104393281     # open; Room.original, no messages (the invitation); Kevin isn't in it
HQ = 201306877           # open; no messages
ALL_TALK = 486777696     # closed; 131 messages, so paged
DESIGNERS = 654632876    # closed; attachments, mentions, embeds and boosts
DAVID_JASON = 186869642  # direct
BROKEN = 699448328       # closed; a message with a broken body
KEVIN_BENDER = 340026324 # direct, with a bot

AGENTS = {
  "Chrome on macOS" => "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
  "Chrome on Windows" => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
  "Chrome on Android" => "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36",
  "Chrome on iOS" => "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/140.0.0.0 Mobile/15E148 Safari/604.1",
  "Safari on macOS" => "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15",
  "Safari on iOS" => "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1",
  "Firefox on macOS" => "Mozilla/5.0 (Macintosh; Intel Mac OS X 14.0; rv:131.0) Gecko/20100101 Firefox/131.0",
  "Firefox on Windows" => "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:131.0) Gecko/20100101 Firefox/131.0",
  "Firefox on Android" => "Mozilla/5.0 (Android 14; Mobile; rv:131.0) Gecko/131.0 Firefox/131.0",
  "Edge on Windows" => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0",
  "Edge on macOS" => "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0",
  "Opera on Linux" => "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 OPR/115.0.0.0"
}

PAGE = "text/html, application/xhtml+xml"

def jar
  ActionDispatch::Request.new(Rails.application.env_config.merge("HTTP_HOST" => HOST, "rack.input" => StringIO.new)).cookie_jar
end

def session_token(token)
  cookies = jar
  cookies.signed.permanent[:session_token] = { value: token, httponly: true, same_site: :lax }
  "session_token=#{Rack::Utils.escape(cookies[:session_token])}"
end

def signed_in(token, agent = "Chrome on macOS", extra = {})
  { "Cookie" => session_token(token), "User-Agent" => AGENTS.fetch(agent), "Accept" => PAGE }.merge(extra)
end

# [name, path, headers]
CASES = [
  [ "administrator's rooms", "/rooms", signed_in(DAVID) ],
  [ "member's rooms", "/rooms", signed_in(KEVIN) ],

  [ "the original room", "/rooms/#{ALL_PETS}", signed_in(DAVID) ],
  [ "an open room", "/rooms/#{HQ}", signed_in(KEVIN) ],
  [ "a paged room", "/rooms/#{ALL_TALK}", signed_in(DAVID) ],
  [ "a room with attachments, mentions and boosts", "/rooms/#{DESIGNERS}", signed_in(DAVID) ],
  [ "a member's view of attachments, mentions and boosts", "/rooms/#{DESIGNERS}", signed_in(KEVIN) ],
  [ "a direct room", "/rooms/#{DAVID_JASON}", signed_in(DAVID) ],
  [ "a direct room with a bot", "/rooms/#{KEVIN_BENDER}", signed_in(KEVIN) ],
  [ "a room with a broken message", "/rooms/#{BROKEN}", signed_in(DAVID) ],
  [ "a room the last visit remembered", "/rooms/#{HQ}", signed_in(KEVIN, "Chrome on macOS", "Cookie" => "#{session_token(KEVIN)}; last_room=#{DESIGNERS}") ],

  [ "a deep link", "/rooms/#{ALL_TALK}/@933434560", signed_in(DAVID) ],
  [ "a deep link to the first message", "/rooms/#{ALL_TALK}/@933434510", signed_in(DAVID) ],
  [ "a deep link to the last message", "/rooms/#{ALL_TALK}/@136976342", signed_in(DAVID) ],
  [ "a deep link to another room's message", "/rooms/#{ALL_TALK}/@309456473", signed_in(DAVID) ],
  [ "a deep link to a missing message", "/rooms/#{ALL_TALK}/@1", signed_in(DAVID) ],
  [ "a deep link that isn't a number", "/rooms/#{ALL_TALK}/@latest", signed_in(DAVID) ],

  [ "a room the member isn't in", "/rooms/#{ALL_PETS}", signed_in(KEVIN) ],
  [ "a missing room", "/rooms/1", signed_in(KEVIN) ],
  [ "a room id that isn't a number", "/rooms/hq", signed_in(KEVIN) ],
  [ "a deep link into a room the member isn't in", "/rooms/#{ALL_PETS}/@1", signed_in(KEVIN) ],
  [ "a room as JSON", "/rooms/#{HQ}.json", signed_in(KEVIN) ],
  [ "a room in a Turbo frame", "/rooms/#{HQ}", signed_in(KEVIN, "Chrome on macOS", "Turbo-Frame" => "room") ],
  [ "a room, signed out", "/rooms/#{HQ}", { "User-Agent" => AGENTS.fetch("Chrome on macOS"), "Accept" => PAGE } ]
] + AGENTS.keys.drop(1).map { |agent| [ "an open room in #{agent}", "/rooms/#{HQ}", signed_in(KEVIN, agent) ] }

RESPONSE_HEADERS = %w[ content-type location vary cache-control etag set-cookie x-frame-options ]

results = CASES.map do |name, path, headers|
  env = Rack::MockRequest.env_for("http://#{HOST}#{path}", method: "GET")
  env["REMOTE_ADDR"] = "198.51.100.7"
  headers.each { |key, value| env["HTTP_#{key.upcase.tr("-", "_")}"] = value }

  status, response_headers, response_body = Rails.application.call(env)
  text = +""
  response_body.each { |chunk| text << chunk }
  response_body.close if response_body.respond_to?(:close)

  {
    "name" => name,
    "request" => { "method" => "GET", "path" => path, "headers" => headers },
    "response" => {
      "status" => status,
      "headers" => response_headers.to_h.transform_keys(&:downcase).slice(*RESPONSE_HEADERS),
      "body" => text.force_encoding("UTF-8")
    }
  }
end

puts JSON.pretty_generate("now" => NOW.iso8601, "fixtures" => FIXTURES, "cases" => results)

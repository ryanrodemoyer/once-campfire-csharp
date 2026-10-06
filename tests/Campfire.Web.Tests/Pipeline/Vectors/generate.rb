# What the reference answers at the edges of ApplicationController's callback chain: no cookie,
# expired, unknown and tampered sessions, bot keys on bot and non-bot routes, bad and missing
# CSRF tokens, a foreign Origin, a banned IP, a non-administrator, require_unauthenticated_access
# and allow_browser. Writes auth_matrix.json, which AuthMatrixTests replays through the C#
# pipeline on the same database.
#
# The database is D02's tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 (the default parity
# seed). Run the reference on a copy of it, with the clock frozen, and this script inside it:
#
#   mkdir -p parity/.seed/w02/db parity/.seed/w02/storage
#   cp tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 parity/.seed/w02/db/production.sqlite3
#   parity/bin/reference up --seed w02 --port 3100 --time 2026-03-02T16:00:00Z --freeze
#   parity/bin/reference runner --port 3100 --time 2026-03-02T16:00:00Z --freeze -e APP_PORT=13100 \
#     tests/Campfire.Web.Tests/Pipeline/Vectors/generate.rb > tests/Campfire.Web.Tests/Pipeline/Vectors/auth_matrix.json
#   parity/bin/reference down --port 3100
#
# APP_PORT is Puma's port (the instance's port + 10000), so Thruster's headers stay out of it.
# Requests run in order on one database; the fixtures below are applied first, and the C# test
# applies the same SQL to its copy.
require "net/http"
require "digest"
require "zlib"
require "json"
require "action_controller/test_case"

HOST = "campfire.test"
APP_PORT = Integer(ENV.fetch("APP_PORT"))
NOW = Time.now.utc

FIXTURES = [
  # Kevin, a member, signed in and active just now (no activity refresh).
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (900001, 712064548, 'KevinSessionToken0000001', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')"
]
FIXTURES.each { |sql| ActiveRecord::Base.connection.execute(sql) }

DAVID = "AxJs94fteQ5Autv2VrKsH68c" # administrator; last active 14:00, so the first request refreshes it
KEVIN = "KevinSessionToken0000001"
BENDER = "394959859-BenderBot123"
OLD_BOT = "773523957-OldBot789abc" # deactivated
CSRF = "w02MatrixCsrfToken0w02MatrixCsrfToken0w02MA" # the session's _csrf_token (urlsafe base64, 32 bytes)
SESSION_ID = "0123456789abcdef0123456789abcdef"
MODERN = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
OLD_CHROME = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/100.0.4896.127 Safari/537.36"

def jar
  ActionDispatch::Request.new(Rails.application.env_config.merge("HTTP_HOST" => HOST, "rack.input" => StringIO.new)).cookie_jar
end

def cookie(name, raw) = "#{name}=#{Rack::Utils.escape(raw)}"

def session_token(token, expires: nil)
  cookies = jar
  if expires
    cookies.signed[:session_token] = { value: token, expires: expires }
  else
    cookies.signed.permanent[:session_token] = { value: token, httponly: true, same_site: :lax }
  end
  cookie("session_token", cookies[:session_token])
end

def rails_session(data)
  cookies = jar
  cookies.encrypted[:_campfire_session] = { value: data }
  cookie("_campfire_session", cookies[:_campfire_session])
end

def form_token(action: nil, method: nil, path: "/")
  request = ActionDispatch::TestRequest.create("PATH_INFO" => path, "HTTP_HOST" => HOST)
  request.session = ActionController::TestSession.new("_csrf_token" => CSRF)
  controller = ApplicationController.new
  controller.set_request!(request)
  controller.send(:form_authenticity_token, form_options: { action: action, method: method })
end

def tampered(cookie) = cookie.sub(/.(%3D%3D)?\z/) { |tail| (tail[0] == "A" ? "B" : "A") + tail[1..] }

CSRF_SESSION = rails_session("session_id" => SESSION_ID, "_csrf_token" => CSRF)
DAVID_COOKIE = session_token(DAVID)
KEVIN_COOKIE = session_token(KEVIN)
JOIN_CODE_TOKEN = form_token(action: "/account/join_code", method: "post", path: "/account/edit")
GLOBAL_TOKEN = form_token
BOT_MESSAGES = "/rooms/486777696/#{BENDER}/messages"

def cookies(*values) = values.join("; ")

CASES = [
  # require_authentication
  ["no cookie", "GET", "/", {}],
  ["no cookie, HEAD", "HEAD", "/", {}],
  ["no cookie, JSON", "GET", "/", { "Accept" => "application/json" }],
  ["no cookie, query kept for return_to", "GET", "/rooms/201306877?message_id=5&x=%20y", {}],
  ["no cookie behind a TLS proxy", "GET", "/", { "X-Forwarded-Proto" => "https", "X-Forwarded-Host" => "chat.example.com" }],
  ["no cookie, Host with a port", "GET", "/", { "Host" => "campfire.test:8080" }],
  ["expired session_token", "GET", "/", { "Cookie" => session_token(DAVID, expires: NOW - 3600) }],
  ["unknown session token", "GET", "/", { "Cookie" => session_token("NoSuchSessionToken000000") }],
  ["tampered session_token", "GET", "/", { "Cookie" => tampered(DAVID_COOKIE) }],
  ["unsigned session_token", "GET", "/", { "Cookie" => "session_token=#{DAVID}" }],
  ["administrator, first request refreshes the session", "GET", "/", { "Cookie" => DAVID_COOKIE }],
  ["administrator, last room cookie", "GET", "/", { "Cookie" => cookies(DAVID_COOKIE, "last_room=486777696") }],
  ["administrator, last room cookie for a room they're not in", "GET", "/", { "Cookie" => cookies(DAVID_COOKIE, "last_room=999") }],
  ["administrator, last room cookie that isn't a number", "GET", "/", { "Cookie" => cookies(DAVID_COOKIE, "last_room=486777696abc") }],
  ["member", "GET", "/", { "Cookie" => KEVIN_COOKIE }],
  ["member with a session cookie", "GET", "/", { "Cookie" => cookies(KEVIN_COOKIE, CSRF_SESSION) }],

  # deny_bots and bot_authentication
  ["bot key on a non-bot route", "GET", "/rooms/201306877?bot_key=#{BENDER}", {}],
  ["bot key with spaces", "GET", "/rooms/201306877?bot_key=%20#{BENDER}%20", {}],
  ["wrong bot key", "GET", "/rooms/201306877?bot_key=394959859-wrong", {}],
  ["blank bot key", "GET", "/rooms/201306877?bot_key=%20", {}],
  ["deactivated bot's key", "GET", "/rooms/201306877?bot_key=#{OLD_BOT}", {}],
  ["bot key on a non-bot write", "POST", "/account/join_code?bot_key=#{BENDER}", {}],
  ["bot key on a bot route, no body", "POST", BOT_MESSAGES, {}],
  ["bot key on a bot route, room the bot isn't in", "POST", "/rooms/104393281/#{BENDER}/messages", {}],
  ["wrong bot key on a bot route", "POST", "/rooms/201306877/394959859-wrong/messages", {}],
  ["deactivated bot on a bot route", "POST", "/rooms/201306877/#{OLD_BOT}/messages", {}],
  ["user cookie on a bot route, no CSRF token", "POST", "/rooms/201306877/nokey/messages", { "Cookie" => DAVID_COOKIE }],

  # verify_authenticity_token
  ["write, no cookie", "POST", "/account/join_code", {}],
  ["write, no token", "POST", "/account/join_code", { "Cookie" => cookies(DAVID_COOKIE, CSRF_SESSION) }],
  ["write, no token and no session", "POST", "/account/join_code", { "Cookie" => DAVID_COOKIE }],
  ["write, bad token", "POST", "/account/join_code", { "Cookie" => cookies(DAVID_COOKIE, CSRF_SESSION) }, "authenticity_token=bad"],
  ["write, token for another form", "POST", "/account/join_code", { "Cookie" => cookies(DAVID_COOKIE, CSRF_SESSION) }, "authenticity_token=#{CGI.escape(form_token(action: "/account/logo", method: "delete"))}"],
  ["write, foreign Origin", "POST", "/account/join_code", { "Cookie" => cookies(DAVID_COOKIE, CSRF_SESSION), "Origin" => "http://evil.test" }, "authenticity_token=#{CGI.escape(JOIN_CODE_TOKEN)}"],
  ["write, null Origin", "POST", "/account/join_code", { "Cookie" => cookies(DAVID_COOKIE, CSRF_SESSION), "Origin" => "null" }, "authenticity_token=#{CGI.escape(JOIN_CODE_TOKEN)}"],
  ["write, form token", "POST", "/account/join_code", { "Cookie" => cookies(DAVID_COOKIE, CSRF_SESSION), "Origin" => "http://#{HOST}" }, "authenticity_token=#{CGI.escape(JOIN_CODE_TOKEN)}"],
  ["write, header token", "POST", "/account/join_code", { "Cookie" => cookies(DAVID_COOKIE, CSRF_SESSION), "X-CSRF-Token" => GLOBAL_TOKEN }],

  # ensure_can_administer
  ["member write", "POST", "/account/join_code", { "Cookie" => cookies(KEVIN_COOKIE, CSRF_SESSION) }, "authenticity_token=#{CGI.escape(JOIN_CODE_TOKEN)}"],

  # RoomScoped
  ["member, room they're not in", "POST", "/rooms/104393281/involvement", { "Cookie" => cookies(KEVIN_COOKIE, CSRF_SESSION), "X-CSRF-Token" => GLOBAL_TOKEN }, "_method=patch&involvement=everything"],
  ["member, room id that isn't a number", "POST", "/rooms/abc/involvement", { "Cookie" => cookies(KEVIN_COOKIE, CSRF_SESSION), "X-CSRF-Token" => GLOBAL_TOKEN }, "_method=patch&involvement=everything"],
  ["member, PATCH with a token for POST", "POST", "/rooms/104393281/involvement", { "Cookie" => cookies(KEVIN_COOKIE, CSRF_SESSION) }, "_method=patch&authenticity_token=#{CGI.escape(form_token(action: "/rooms/104393281/involvement", method: "post"))}"],

  # reject_banned_ip
  ["banned IP write", "POST", "/account/join_code", { "X-Forwarded-For" => "203.0.113.9" }],
  ["banned IP write, behind proxies", "POST", "/account/join_code", { "X-Forwarded-For" => "203.0.113.9, 10.0.0.2" }],
  ["banned IP write by an administrator", "POST", "/account/join_code", { "Cookie" => cookies(DAVID_COOKIE, CSRF_SESSION), "X-Forwarded-For" => "203.0.113.9" }, "authenticity_token=#{CGI.escape(JOIN_CODE_TOKEN)}"],
  ["banned IP read", "GET", "/", { "X-Forwarded-For" => "203.0.113.9" }],
  ["banned IP in Client-Ip only", "POST", "/account/join_code", { "Client-Ip" => "203.0.113.9" }],
  ["banned IP behind an untrusted hop", "POST", "/account/join_code", { "X-Forwarded-For" => "203.0.113.9, 198.51.100.20" }],
  ["banned IP spoofing check", "POST", "/account/join_code", { "Client-Ip" => "203.0.113.9", "X-Forwarded-For" => "198.51.100.20" }],

  # require_unauthenticated_access and allow_unauthenticated_access
  ["signed in, join link", "GET", "/join/WRONG", { "Cookie" => DAVID_COOKIE }],
  ["signed in, right join code", "GET", "/join/CRMu-l8Ge-KB9B", { "Cookie" => KEVIN_COOKIE }],
  ["signed out, wrong join code", "GET", "/join/WRONG", {}],
  ["signed out, first run after the first run", "GET", "/first_run", {}],
  ["signed out, first run as JSON", "GET", "/first_run", { "Accept" => "application/json" }],

  # allow_browser
  ["old browser", "GET", "/first_run", { "User-Agent" => OLD_CHROME }],
  ["old browser, signed out on a protected page", "GET", "/", { "User-Agent" => OLD_CHROME }],
  ["old browser, bot key", "GET", "/rooms/201306877?bot_key=#{BENDER}", { "User-Agent" => OLD_CHROME }],

  # Rack::Deflater
  ["identity only", "GET", "/", { "Accept-Encoding" => "identity" }],
  ["no Accept-Encoding", "GET", "/", { "Accept-Encoding" => "" }],
  ["gzip refused", "GET", "/", { "Accept-Encoding" => "gzip;q=0, identity" }],
  ["nothing acceptable", "GET", "/", { "Accept-Encoding" => "identity;q=0" }],
  ["gzipped error page", "POST", "/account/join_code", { "Cookie" => cookies(DAVID_COOKIE, CSRF_SESSION) }],
  ["error page, HEAD", "HEAD", "/rooms/abc/involvement", { "Cookie" => KEVIN_COOKIE }]
]

HEADERS = %w[location content-type content-encoding cache-control vary x-version x-rev x-frame-options x-xss-protection
  x-content-type-options x-permitted-cross-domain-policies referrer-policy etag last-modified]

# Set by hand, so Net::HTTP leaves gzipped bodies as they came (Rack::Deflater, config.ru).
ACCEPT_ENCODING = "gzip, deflate, br, zstd"

def decoded(response)
  body = response.body || ""
  response["content-encoding"] == "gzip" && !body.empty? ? Zlib.gunzip(body) : body
end

def perform(method, path, headers, body)
  http = Net::HTTP.new("127.0.0.1", APP_PORT)
  request = Net::HTTPGenericRequest.new(method, !body.nil? || method == "POST", method != "HEAD", path)
  request["Host"] = HOST
  request["User-Agent"] = MODERN
  request["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
  request["Accept-Encoding"] = ACCEPT_ENCODING
  headers.each { |name, value| request[name] = value }
  if method == "POST"
    request["Content-Type"] = "application/x-www-form-urlencoded"
    request.body = body || ""
  end
  http.request(request)
end

results = CASES.map do |name, method, path, headers, body|
  response = perform(method, path, headers, body)
  sent = { "Host" => HOST, "User-Agent" => MODERN, "Accept" => "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", "Accept-Encoding" => ACCEPT_ENCODING }.merge(headers)
  sent["Content-Type"] = "application/x-www-form-urlencoded" if method == "POST"
  {
    name: name,
    request: { method: method, path: path, headers: sent, body: method == "POST" ? (body || "") : nil },
    response: {
      status: response.code.to_i,
      headers: HEADERS.to_h { |header| [header, response[header]] }.compact,
      set_cookies: response.get_fields("set-cookie") || [],
      body_sha256: Digest::SHA256.hexdigest(decoded(response)),
      body_length: decoded(response).bytesize
    }
  }
end

puts JSON.pretty_generate(now: NOW.iso8601, fixtures: FIXTURES, csrf_token: CSRF, cases: results)

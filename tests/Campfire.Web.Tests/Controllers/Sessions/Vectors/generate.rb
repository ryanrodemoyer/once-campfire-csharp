# What the reference does when people sign in and out, land on the root, and set up a new install
# through SessionsController, WelcomeController and FirstRunsController: each response, the Action
# Cable broadcasts it made, and every row it changed. Writes sessions.json, which
# SessionsControllerTests replays through the C# router and controllers on the same database.
#
# The database is D02's tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 (the default parity
# seed, where everyone's password is "secret123456"). Run this script in the reference app on a
# copy of it, with the clock frozen:
#
#   mkdir -p tmp/a01/db tmp/a01/storage
#   cp tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 tmp/a01/db/production.sqlite3
#   parity/bin/reference runner --storage tmp/a01 --time 2026-03-02T16:00:00Z --freeze \
#     tests/Campfire.Web.Tests/Controllers/Sessions/Vectors/generate.rb > tests/Campfire.Web.Tests/Controllers/Sessions/Vectors/sessions.json
#
# Requests go straight to Rails.application (the stack Puma runs), in order, on one database, with
# CSRF protection on. The fixtures are applied first; the "empty the database" case runs RESET,
# after which the first-run cases see a new install. The C# test applies the same SQL.
require "json"
require "action_controller/test_case"

HOST = "campfire.test"
NOW = Time.now.utc

FIXTURES = [
  # Kevin, a member, signed in and active just now (no activity refresh).
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (900001, 712064548, 'KevinSessionToken0000001', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
  # Lou, who is in no rooms.
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (900002, 773523958, 'LouSessionToken000000002', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
  "DELETE FROM memberships WHERE user_id = 773523958"
]
FIXTURES.each { |sql| ActiveRecord::Base.connection.execute(sql) }

# Everything a new install doesn't have.
RESET = %w[ sessions push_subscriptions searches bans webhooks boosts memberships action_text_rich_texts
  active_storage_variant_records active_storage_attachments active_storage_blobs messages rooms users accounts ]
  .map { |table| "DELETE FROM #{table}" } + [ "DELETE FROM message_search_index" ]

KEVIN = "KevinSessionToken0000001"
LOU = "LouSessionToken000000002"
CSRF = "a01SessionsCsrfToken0a01SessionsCsrfToken0A" # the session's _csrf_token (urlsafe base64, 32 bytes)
SESSION_ID = "0123456789abcdef0123456789abcdef"
MODERN = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
OLD_CHROME = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/100.0.0.0 Safari/537.36"
PAGE = "text/html, application/xhtml+xml"
TURBO = "text/vnd.turbo-stream.html, text/html, application/xhtml+xml"
PASSWORD = "secret123456"
LIMITED_IP = "192.0.2.44"
BANNED_IP = "203.0.113.9" # banned in the seed

def jar
  ActionDispatch::Request.new(Rails.application.env_config.merge("HTTP_HOST" => HOST, "rack.input" => StringIO.new)).cookie_jar
end

def cookie(name, raw) = "#{name}=#{Rack::Utils.escape(raw)}"

def session_token(token)
  cookies = jar
  cookies.signed.permanent[:session_token] = { value: token, httponly: true, same_site: :lax }
  cookie("session_token", cookies[:session_token])
end

def rails_session(data)
  cookies = jar
  cookies.encrypted[:_campfire_session] = { value: data }
  cookie("_campfire_session", cookies[:_campfire_session])
end

def global_token
  request = ActionDispatch::TestRequest.create("PATH_INFO" => "/", "HTTP_HOST" => HOST)
  request.session = ActionController::TestSession.new("_csrf_token" => CSRF)
  controller = ApplicationController.new
  controller.set_request!(request)
  controller.send(:form_authenticity_token)
end

CSRF_SESSION = rails_session("session_id" => SESSION_ID, "_csrf_token" => CSRF)
RETURN_TO_SESSION = rails_session("session_id" => SESSION_ID, "_csrf_token" => CSRF, "return_to_after_authenticating" => "http://#{HOST}/rooms/201306877")
TOKEN = global_token

def browser(extra = {}) = { "Cookie" => CSRF_SESSION, "User-Agent" => MODERN, "Accept" => PAGE }.merge(extra)

def signed_in(token, extra = {}) = browser("Cookie" => "#{session_token(token)}; #{CSRF_SESSION}").merge(extra)

# A form Turbo submits: the page's token in the body.
def submit(params) = URI.encode_www_form({ "authenticity_token" => TOKEN }.merge(params))

def sign_in(email, password = PASSWORD) = submit("email_address" => email, "password" => password)

FORM = "application/x-www-form-urlencoded"

# [name, method, path, headers, body, remote address]
CASES = [
  ["sign-in page", "GET", "/session/new", browser],
  ["sign-in page, JSON", "GET", "/session/new", browser("Accept" => "application/json")],
  ["sign-in page with an email address", "GET", "/session/new?email_address=kevin%4037signals.com", browser],
  ["sign-in page, signed in", "GET", "/session/new", signed_in(KEVIN)],
  ["sign-in page, unsupported browser", "GET", "/session/new", browser("User-Agent" => OLD_CHROME)],
  ["sign in", "POST", "/session", browser("Accept" => TURBO), sign_in("david@37signals.com")],
  ["sign in, wrong password", "POST", "/session", browser("Accept" => TURBO), sign_in("david@37signals.com", "wrong")],
  ["sign in, unknown address", "POST", "/session", browser("Accept" => TURBO), sign_in("nobody@37signals.com")],
  ["sign in, deactivated", "POST", "/session", browser("Accept" => TURBO),
    sign_in("rita-deactivated-00000000-0000-4000-8000-000000000000@37signals.com")],
  ["sign in, banned", "POST", "/session", browser("Accept" => TURBO), sign_in("mallory@example.com")],
  ["sign in, blank password", "POST", "/session", browser("Accept" => TURBO), sign_in("kevin@37signals.com", "")],
  ["sign in, no params", "POST", "/session", browser("Accept" => TURBO), submit({})],
  ["sign in, back to where they were", "POST", "/session", browser("Accept" => TURBO, "Cookie" => RETURN_TO_SESSION), sign_in("kevin@37signals.com")],
  ["sign in, no CSRF token", "POST", "/session", browser("Accept" => TURBO), URI.encode_www_form("email_address" => "kevin@37signals.com", "password" => PASSWORD)],

  ["root, signed out", "GET", "/", browser],
  ["root, signed in", "GET", "/", signed_in(KEVIN)],
  ["root, last room visited", "GET", "/", signed_in(KEVIN, "Cookie" => "#{session_token(KEVIN)}; #{CSRF_SESSION}; last_room=699448327")],
  ["root, no rooms", "GET", "/", signed_in(LOU)],
  ["root, no rooms, JSON", "GET", "/", signed_in(LOU, "Accept" => "application/json")],
  ["first run, already set up", "GET", "/first_run", browser],
  ["first run create, already set up", "POST", "/first_run", browser,
    submit("user[name]" => "Mallory", "user[email_address]" => "mallory2@example.com", "user[password]" => PASSWORD)],
  ["session show", "GET", "/session", signed_in(KEVIN)],
  ["first run edit", "GET", "/first_run/edit", browser],

  ["sign out", "DELETE", "/session", signed_in(KEVIN, "Accept" => TURBO),
    submit("push_subscription_endpoint" => "https://fcm.googleapis.com/fcm/send/789")],
  ["sign out, signed out", "DELETE", "/session", browser("Accept" => TURBO), submit({})],
  ["sign in from a banned address", "POST", "/session", browser("Accept" => TURBO), sign_in("jz@37signals.com"), BANNED_IP],
] + (1..11).map do |attempt|
  ["rate limit, attempt #{attempt}", "POST", "/session", browser("Accept" => TURBO), sign_in("jz@37signals.com", "wrong#{attempt}"), LIMITED_IP]
end + [
  ["rate limit, right password", "POST", "/session", browser("Accept" => TURBO), sign_in("jz@37signals.com"), LIMITED_IP],

  ["empty the database", nil],
  ["root, new install", "GET", "/", browser],
  ["sign-in page, new install", "GET", "/session/new", browser],
  ["first run page", "GET", "/first_run", browser],
  ["first run page, JSON", "GET", "/first_run.json", browser],
  ["first run, no user", "POST", "/first_run", browser, submit("name" => "x")],
  ["first run", "POST", "/first_run", browser,
    submit("user[name]" => "Ada", "user[email_address]" => "ada@example.com", "user[password]" => PASSWORD, "user[role]" => "member")],
  ["first run, again", "GET", "/first_run", browser],
  ["sign in after the first run", "POST", "/session", browser("Accept" => TURBO), sign_in("ada@example.com")]
]

TABLES = %w[ accounts users rooms memberships sessions push_subscriptions ]

def snapshot
  connection = ActiveRecord::Base.connection
  TABLES.to_h do |table|
    [ table, connection.select_all("SELECT * FROM #{table}").rows.to_h { |row| [ row.first, row ] } ]
  end
end

def columns(table) = ActiveRecord::Base.connection.select_all("SELECT * FROM #{table} LIMIT 0").columns

def changes(before, after)
  before.keys.filter_map do |table|
    ids = (before[table].keys | after[table].keys).sort
    rows = ids.filter_map do |id|
      old, new = before[table][id], after[table][id]
      next if old == new
      { "id" => id, "row" => new && columns(table).zip(new).to_h }
    end
    [ table, rows ] if rows.any?
  end.to_h
end

events = []
ActiveSupport::Notifications.subscribe("broadcast.action_cable") do |*, payload|
  message = payload[:coder] ? payload[:coder].encode(payload[:message]) : payload[:message]
  events << { "broadcast" => payload[:broadcasting], "payload" => message }
end

RESPONSE_HEADERS = %w[ content-type location vary cache-control set-cookie x-frame-options ]

results = CASES.map do |name, method, path, headers, body, remote_addr|
  if method.nil?
    RESET.each { |sql| ActiveRecord::Base.connection.execute(sql) }
    next { "name" => name, "reset" => RESET }
  end

  headers = headers.dup
  content_type = body ? FORM : nil
  env = Rack::MockRequest.env_for("http://#{HOST}#{path}", method: method, input: body || "")
  env["CONTENT_TYPE"] = content_type if content_type
  env["REMOTE_ADDR"] = remote_addr || "198.51.100.7"
  headers.each { |key, value| env["HTTP_#{key.upcase.tr("-", "_")}"] = value }

  events.clear
  before = snapshot
  status, response_headers, response_body = Rails.application.call(env)
  text = +""
  response_body.each { |chunk| text << chunk }
  response_body.close if response_body.respond_to?(:close)
  after = snapshot

  {
    "name" => name,
    "request" => { "method" => method, "path" => path, "headers" => headers, "content_type" => content_type, "body" => body, "remote_addr" => env["REMOTE_ADDR"] },
    "response" => {
      "status" => status,
      "headers" => response_headers.to_h.transform_keys(&:downcase).slice(*RESPONSE_HEADERS),
      "body" => text.force_encoding("UTF-8")
    },
    "events" => events.dup,
    "changes" => changes(before, after)
  }
end

puts JSON.pretty_generate("now" => NOW.iso8601, "fixtures" => FIXTURES, "cases" => results)

# What the reference answers for Users::PushSubscriptionsController and
# Users::PushSubscriptions::TestNotificationsController.
# Writes push_subscriptions.json, which PushSubscriptionsControllerTests replays.
#
# The database is D02's tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3. Run this script
# in the reference app on a copy of it, with the clock frozen:
#
#   mkdir -p tmp/a07/db tmp/a07/storage
#   cp tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 tmp/a07/db/production.sqlite3
#   parity/bin/reference runner --storage tmp/a07 --time 2026-03-02T16:00:00Z --freeze \
#     tests/Campfire.Web.Tests/Controllers/PushSubscriptions/Vectors/generate.rb
#
# DNS is stubbed (Surfguard calls Resolv.getaddresses) so a permitted endpoint can be made to
# resolve publicly, privately, or not at all. WebPush.payload_send is stubbed so a test
# notification never opens a socket; the C# replay uses a private answer for those cases, which
# makes deliver return without posting, and a separate test dials a local push endpoint.
require "json"
require "resolv"
require "action_controller/test_case"

HOST = "campfire.test"
NOW = Time.now.utc
WORK = ENV.fetch("PARITY_WORK")
OUTPUT = ARGV.fetch(0, "tests/Campfire.Web.Tests/Controllers/PushSubscriptions/Vectors/push_subscriptions.json")
PUBLIC_IP = "142.250.185.206"
PRIVATE_IP = "169.254.169.254"

module A07Dns
  class << self
    attr_accessor :ips, :fail
  end

  def getaddresses(*)
    raise Resolv::ResolvError, "no addresses for the stubbed host" if A07Dns.fail

    A07Dns.ips
  end
end
A07Dns.ips = [ PUBLIC_IP ]
A07Dns.fail = false
Resolv.singleton_class.prepend(A07Dns)

module A07PayloadSend
  def payload_send(...)
    nil
  end
end
WebPush.singleton_class.prepend(A07PayloadSend)

FIXTURES = [
  # David, an administrator, and Kevin, a member, signed in and active just now.
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (900001, 127326141, 'DavidSessionToken0000001', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (900002, 712064548, 'KevinSessionToken0000002', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
  # A row that predates endpoint validation (saved without it). Re-posting its keys must 422.
  "INSERT INTO push_subscriptions (id, auth_key, created_at, endpoint, p256dh_key, updated_at, user_agent, user_id) " \
    "VALUES (900003, '456', '2026-01-01 00:00:00', 'https://attacker.example.com/steal', '123', '2026-01-01 00:00:00', NULL, 127326141)"
]
FIXTURES.each { |sql| ActiveRecord::Base.connection.execute(sql) }

DAVID = "DavidSessionToken0000001"
KEVIN = "KevinSessionToken0000002"
# SecureRandom.urlsafe_base64(32): the session token both apps decode to 32 bytes before the per-form HMAC.
CSRF = "QTA3UHVzaFN1YnNjcmlwdGlvbnNDc3JmVG9rZW4wMDA"
SESSION_ID = "0123456789abcdef0123456789abcdef"
MODERN = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
PAGE = "text/html, application/xhtml+xml"
TURBO = "text/vnd.turbo-stream.html, text/html, application/xhtml+xml"
CHROME_ENDPOINT = "https://fcm.googleapis.com/fcm/send/123"
CHROME_P256DH = "123-RIXcMgkdjhRnFZaYjjGvo00dydRQbCpQTuXFjLaCPSE7ofxi19awgGc3Doqa1RmYQqsbQDfQTifFZgc"
CHROME_AUTH = "xxx2DtgvmLkevKRwoyahJl0efg"

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
TOKEN = global_token

def browser(extra = {}) = { "Cookie" => CSRF_SESSION, "User-Agent" => MODERN, "Accept" => PAGE }.merge(extra)
def signed_in(token, extra = {}) = browser("Cookie" => "#{session_token(token)}; #{CSRF_SESSION}").merge(extra)
def david(extra = {}) = signed_in(DAVID, extra)
def kevin(extra = {}) = signed_in(KEVIN, extra)

def form(fields = {}) = URI.encode_www_form({ "authenticity_token" => TOKEN }.merge(fields))

def subscription_form(endpoint, p256dh, auth, token: true)
  fields = {
    "push_subscription[endpoint]" => endpoint,
    "push_subscription[p256dh_key]" => p256dh,
    "push_subscription[auth_key]" => auth
  }
  token ? form(fields) : URI.encode_www_form(fields)
end

def apply_dns(dns)
  A07Dns.fail = dns == "unresolvable"
  A07Dns.ips = dns == "unresolvable" ? [] : [ dns ]
end

# name, method, path, headers, body, content_type, dns
CASES = [
  ["index", "GET", "/users/me/push_subscriptions", david, nil, nil, PUBLIC_IP],
  ["index with last room visited", "GET", "/users/me/push_subscriptions", david("Cookie" => "#{session_token(DAVID)}; #{CSRF_SESSION}; last_room=201306877"), nil, nil, PUBLIC_IP],
  ["index signed out", "GET", "/users/me/push_subscriptions", browser, nil, nil, PUBLIC_IP],
  ["index format json", "GET", "/users/me/push_subscriptions.json", david("Accept" => "application/json"), nil, nil, PUBLIC_IP],
  ["index turbo stream only", "GET", "/users/me/push_subscriptions", david("Accept" => "text/vnd.turbo-stream.html"), nil, nil, PUBLIC_IP],
  ["index turbo stream and html", "GET", "/users/me/push_subscriptions", david("Accept" => TURBO), nil, nil, PUBLIC_IP],
  ["index turbo frame", "GET", "/users/me/push_subscriptions", david("Turbo-Frame" => "push_subscriptions"), nil, nil, PUBLIC_IP],
  ["index as kevin", "GET", "/users/me/push_subscriptions", kevin, nil, nil, PUBLIC_IP],
  ["index ignores the user id in the path", "GET", "/users/149087659/push_subscriptions", david, nil, nil, PUBLIC_IP],

  ["create new subscription", "POST", "/users/me/push_subscriptions", david("Accept" => TURBO, "User-Agent" => "Mozilla/5.0"),
    subscription_form("https://fcm.googleapis.com/fcm/send/abc123", "123", "456"), nil, PUBLIC_IP],
  ["create json subscription", "POST", "/users/me/push_subscriptions", david("Accept" => TURBO, "X-CSRF-Token" => TOKEN),
    JSON.generate("push_subscription" => { "endpoint" => "https://updates.push.services.mozilla.com/wpush/v2/fresh", "p256dh_key" => "123", "auth_key" => "456" }),
    "application/json", PUBLIC_IP],
  ["touch chrome subscription", "POST", "/users/me/push_subscriptions", david("Accept" => TURBO),
    subscription_form(CHROME_ENDPOINT, CHROME_P256DH, CHROME_AUTH), nil, PUBLIC_IP],
  ["reject non-permitted endpoint", "POST", "/users/me/push_subscriptions", david("Accept" => TURBO),
    subscription_form("https://attacker.example.com/steal", "999", "999"), nil, PUBLIC_IP],
  ["re-register legacy invalid subscription", "POST", "/users/me/push_subscriptions", david("Accept" => TURBO),
    subscription_form("https://attacker.example.com/steal", "123", "456"), nil, PUBLIC_IP],
  ["reject private ip", "POST", "/users/me/push_subscriptions", david("Accept" => TURBO),
    subscription_form("https://fcm.googleapis.com/fcm/send/private", "123", "456"), nil, PRIVATE_IP],
  ["reject unresolvable endpoint", "POST", "/users/me/push_subscriptions", david("Accept" => TURBO),
    subscription_form("https://fcm.googleapis.com/fcm/send/missing", "123", "456"), nil, "unresolvable"],
  ["reject blank endpoint", "POST", "/users/me/push_subscriptions", david("Accept" => TURBO),
    subscription_form("", "123", "456"), nil, PUBLIC_IP],
  ["create missing push subscription param", "POST", "/users/me/push_subscriptions", david("Accept" => TURBO),
    form, nil, PUBLIC_IP],
  ["create empty push subscription", "POST", "/users/me/push_subscriptions", david("Accept" => TURBO, "X-CSRF-Token" => TOKEN),
    JSON.generate("push_subscription" => {}), "application/json", PUBLIC_IP],
  ["create without csrf", "POST", "/users/me/push_subscriptions", david("Accept" => TURBO),
    subscription_form("https://fcm.googleapis.com/fcm/send/forged", "123", "456", token: false), nil, PUBLIC_IP],

  # Private DNS: deliver returns without posting. The body of a real push is not in this response.
  ["test notification", "POST", "/users/me/push_subscriptions/782661004/test_notifications", david("Accept" => TURBO),
    form, nil, PRIVATE_IP],
  ["test notification missing", "POST", "/users/me/push_subscriptions/999999999/test_notifications", david("Accept" => TURBO),
    form, nil, PRIVATE_IP],
  ["test notification of another user", "POST", "/users/me/push_subscriptions/283769004/test_notifications", david("Accept" => TURBO),
    form, nil, PRIVATE_IP],
  ["test notification non-numeric id", "POST", "/users/me/push_subscriptions/abc/test_notifications", david("Accept" => TURBO),
    form, nil, PRIVATE_IP],
  ["test notification without csrf", "POST", "/users/me/push_subscriptions/782661004/test_notifications", david("Accept" => TURBO),
    "", nil, PRIVATE_IP],

  ["destroy chrome", "DELETE", "/users/me/push_subscriptions/56887440", david("Accept" => TURBO), form, nil, PUBLIC_IP],
  ["destroy another user's subscription", "DELETE", "/users/me/push_subscriptions/283769004", david("Accept" => TURBO), form, nil, PUBLIC_IP],
  ["destroy missing", "DELETE", "/users/me/push_subscriptions/999999999", david("Accept" => TURBO), form, nil, PUBLIC_IP],
  ["destroy non-numeric id", "DELETE", "/users/me/push_subscriptions/abc", david("Accept" => TURBO), form, nil, PUBLIC_IP],
  ["destroy via to_i prefix", "DELETE", "/users/me/push_subscriptions/782661004abc", david("Accept" => TURBO), form, nil, PUBLIC_IP],
  ["destroy via button post", "POST", "/users/me/push_subscriptions/900003", david("Accept" => TURBO), form("_method" => "delete"), nil, PUBLIC_IP],
  ["destroy without csrf", "DELETE", "/users/me/push_subscriptions/257765087", david("Accept" => TURBO), "", nil, PUBLIC_IP],

  ["new action", "GET", "/users/me/push_subscriptions/new", david, nil, nil, PUBLIC_IP],
  ["show action", "GET", "/users/me/push_subscriptions/56887440", david, nil, nil, PUBLIC_IP],
  ["edit action", "GET", "/users/me/push_subscriptions/56887440/edit", david, nil, nil, PUBLIC_IP],
  ["update action", "PATCH", "/users/me/push_subscriptions/56887440", david("Accept" => TURBO), form, nil, PUBLIC_IP],
  ["create signed out", "POST", "/users/me/push_subscriptions", browser("Accept" => TURBO),
    subscription_form("https://fcm.googleapis.com/fcm/send/anon", "123", "456"), nil, PUBLIC_IP],

  ["index after changes", "GET", "/users/me/push_subscriptions", david, nil, nil, PUBLIC_IP]
]

TABLES = %w[ push_subscriptions sessions ]

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

RESPONSE_HEADERS = %w[ content-type location vary cache-control set-cookie x-frame-options etag ]

results = CASES.map do |name, method, path, headers, body, content_type, dns|
  apply_dns(dns)
  content_type = "application/x-www-form-urlencoded" if body && content_type.nil?
  input = body || ""
  env = Rack::MockRequest.env_for("http://#{HOST}#{path}", method: method, input: input)
  env["CONTENT_TYPE"] = content_type if content_type
  env["REMOTE_ADDR"] = "198.51.100.7"
  headers.each { |key, value| env["HTTP_#{key.upcase.tr("-", "_")}"] = value }

  before = snapshot
  status, response_headers, response_body = Rails.application.call(env)
  text = +""
  if response_body.respond_to?(:to_path)
    text << File.binread(response_body.to_path)
  else
    response_body.each { |chunk| text << chunk }
  end
  response_body.close if response_body.respond_to?(:close)
  after = snapshot

  response_headers = response_headers.to_h.transform_keys(&:downcase)
  {
    "name" => name,
    "dns" => dns,
    "request" => { "method" => method, "path" => path, "headers" => headers, "content_type" => content_type,
      "body" => body, "remote_addr" => "198.51.100.7" },
    "response" => {
      "status" => status,
      "headers" => response_headers.slice(*RESPONSE_HEADERS),
      "body" => text.force_encoding("UTF-8")
    },
    "changes" => changes(before, after)
  }
end

File.write(File.join(WORK, OUTPUT), JSON.pretty_generate("now" => NOW.iso8601, "fixtures" => FIXTURES, "cases" => results) + "\n")

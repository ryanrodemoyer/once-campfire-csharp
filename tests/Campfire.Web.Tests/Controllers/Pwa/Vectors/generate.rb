# What the reference answers for PwaController, QrCodeController, Rails::HealthController
# and Autocompletable::UsersController. Writes pwa.json, which PwaControllerTests replays
# through the C# router and controllers on the same database.
#
# The database is D02's tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3. Run this script
# in the reference app on a copy of it, with the clock frozen:
#
#   mkdir -p /tmp/a08/db
#   cp tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 /tmp/a08/db/production.sqlite3
#   parity/bin/reference runner --storage /tmp/a08 --time 2026-03-02T16:00:00Z --freeze \
#     tests/Campfire.Web.Tests/Controllers/Pwa/Vectors/generate.rb
#
require "json"
require "base64"
require "action_controller/test_case"

HOST = "campfire.test"
NOW = Time.now.utc
WORK = ENV.fetch("PARITY_WORK")
OUTPUT = ARGV.fetch(0, "tests/Campfire.Web.Tests/Controllers/Pwa/Vectors/pwa.json")

FIXTURES = [
  # David, an administrator, and Kevin, a member, signed in and active just now.
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (900001, 127326141, 'DavidSessionToken0000001', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (900002, 712064548, 'KevinSessionToken0000002', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')"
]

# A second page of active users, plus one whose name has to be HTML-escaped in JSON.
(1..20).each do |n|
  FIXTURES << "INSERT INTO users (id, name, email_address, role, status, created_at, updated_at) " \
    "VALUES (#{910000 + n}, 'Page User #{format("%02d", n)}', 'page#{format("%02d", n)}@example.com', 0, 0, '2026-01-01 16:00:00', '2026-01-01 16:00:00')"
end
FIXTURES << %q{INSERT INTO users (id, name, email_address, role, status, created_at, updated_at) VALUES (910021, 'Tom & "Jerry" <script>''x''', 'tom@example.com', 0, 0, '2026-01-01 16:00:00', '2026-01-01 16:00:00')}
FIXTURES.each { |sql| ActiveRecord::Base.connection.execute(sql) }

DAVID = "DavidSessionToken0000001"
KEVIN = "KevinSessionToken0000002"
# Urlsafe Base64 of 32 bytes, the length a session CSRF token has.
CSRF = "bTA4U2VhcmNoZXNDc3JmVG9rZW4wMG0wOFNlYXJjaGU"
SESSION_ID = "0123456789abcdef0123456789abcdef"
MODERN = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
OLD = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/100.0.0.0 Safari/537.36"
BROWSER = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
BENDER = "394959859-BenderBot123"

# Rooms from the parity seed. Kevin is a member of HQ and not of All Pets.
HQ = "201306877"
ALL_PETS = "104393281"

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

CSRF_SESSION = rails_session("session_id" => SESSION_ID, "_csrf_token" => CSRF)

def browser(extra = {}) = { "Cookie" => CSRF_SESSION, "User-Agent" => MODERN, "Accept" => BROWSER }.merge(extra)
def signed_in(token, extra = {}) = browser("Cookie" => "#{session_token(token)}; #{CSRF_SESSION}").merge(extra)
def david(extra = {}) = signed_in(DAVID, extra)
def kevin(extra = {}) = signed_in(KEVIN, extra)
def accept(type, extra = {}) = browser("Accept" => type).merge(extra)

EXAMPLE = Base64.urlsafe_encode64("http://example.com")
JOIN = Base64.urlsafe_encode64("http://campfire.test/join/CRMu-l8Ge-KB9B")
SHORT = Base64.urlsafe_encode64("http://campfire.test")
PADDED = Base64.urlsafe_encode64("a")
UNPADDED = PADDED.delete("=")
LONG = Base64.urlsafe_encode64("x" * 3000)

CASES = [
  # Manifest: the extension wins over a browser Accept; without one, the Accept does.
  ["manifest json", "GET", "/webmanifest.json", browser],
  ["manifest browser accept", "GET", "/webmanifest", browser],
  ["manifest accept json", "GET", "/webmanifest", accept("application/json")],
  ["manifest accept any", "GET", "/webmanifest", accept("*/*")],
  ["manifest signed in", "GET", "/webmanifest.json", david],
  ["manifest old chrome", "GET", "/webmanifest.json", browser("User-Agent" => OLD)],
  ["manifest head", "HEAD", "/webmanifest.json", browser],
  ["manifest html format", "GET", "/webmanifest.html", browser],
  ["manifest no cookies", "GET", "/webmanifest.json", { "User-Agent" => MODERN, "Accept" => BROWSER }],

  # Service worker is JavaScript, so forgery protection must be skipped or a script tag 422s.
  ["service worker js", "GET", "/service-worker.js", browser],
  ["service worker browser accept", "GET", "/service-worker", browser],
  ["service worker accept js", "GET", "/service-worker", accept("text/javascript")],
  ["service worker accept any", "GET", "/service-worker", accept("*/*")],
  ["service worker xhr browser", "GET", "/service-worker", browser("X-Requested-With" => "XMLHttpRequest")],
  ["service worker xhr js", "GET", "/service-worker", accept("text/javascript", "X-Requested-With" => "XMLHttpRequest")],
  ["service worker old chrome", "GET", "/service-worker.js", browser("User-Agent" => OLD)],
  ["service worker head", "HEAD", "/service-worker.js", browser],

  # Rails::HealthController is ActionController::Base: no browser block, no version headers.
  ["up", "GET", "/up", browser],
  ["up json", "GET", "/up.json", browser],
  ["up accept json", "GET", "/up", accept("application/json")],
  ["up accept text", "GET", "/up", accept("text/plain")],
  ["up accept any", "GET", "/up", accept("*/*")],
  ["up old chrome", "GET", "/up", browser("User-Agent" => OLD)],
  ["up head", "HEAD", "/up", browser],
  ["up json accept html", "GET", "/up.json", accept("text/html")],
  ["up no cookies", "GET", "/up", { "User-Agent" => MODERN, "Accept" => BROWSER }],

  # QR codes. Invalid base64 and data past version 40 are 500s. A year is 31556952 seconds.
  ["qr example", "GET", "/qr_code/#{EXAMPLE}", browser],
  ["qr join", "GET", "/qr_code/#{JOIN}", browser],
  ["qr short", "GET", "/qr_code/#{SHORT}", browser],
  ["qr invalid", "GET", "/qr_code/!!", browser],
  ["qr unpadded", "GET", "/qr_code/#{UNPADDED}", browser],
  ["qr partial padding", "GET", "/qr_code/ab=", browser],
  ["qr short alphabet", "GET", "/qr_code/a", browser],
  ["qr padded", "GET", "/qr_code/#{PADDED}", browser],
  ["qr too long", "GET", "/qr_code/#{LONG}", browser],
  ["qr svg format", "GET", "/qr_code/#{EXAMPLE}.svg", browser],
  ["qr json format", "GET", "/qr_code/#{EXAMPLE}.json", accept("application/json")],
  ["qr signed in", "GET", "/qr_code/#{EXAMPLE}", david],
  ["qr accept svg", "GET", "/qr_code/#{EXAMPLE}", accept("image/svg+xml")],
  ["qr head", "HEAD", "/qr_code/#{EXAMPLE}", browser],
  ["qr old chrome", "GET", "/qr_code/#{EXAMPLE}", browser("User-Agent" => OLD)],

  # Autocompletable users: HTML mentions markup and JSON, 20 per page.
  ["users json da", "GET", "/autocompletable/users.json?query=da", david],
  ["users html da", "GET", "/autocompletable/users?query=da", david],
  ["users json escape", "GET", "/autocompletable/users.json?query=tom", david],
  ["users html escape", "GET", "/autocompletable/users?query=tom", david],
  ["users json hq", "GET", "/autocompletable/users.json?room_id=#{HQ}&query=da", david],
  ["users json pets", "GET", "/autocompletable/users.json?room_id=#{ALL_PETS}&query=da", david],
  ["users json pets kevin", "GET", "/autocompletable/users.json?room_id=#{ALL_PETS}&query=da", kevin],
  ["users html pets kevin", "GET", "/autocompletable/users?room_id=#{ALL_PETS}&query=da", kevin],
  ["users signed out", "GET", "/autocompletable/users.json?query=da", browser],
  ["users json page 1", "GET", "/autocompletable/users.json", david],
  ["users json page 2", "GET", "/autocompletable/users.json?page=2", david],
  ["users json page 3", "GET", "/autocompletable/users.json?page=3", david],
  ["users json page 0", "GET", "/autocompletable/users.json?page=0", david],
  ["users json page abc", "GET", "/autocompletable/users.json?page=abc", david],
  ["users json page array", "GET", "/autocompletable/users.json?page[]=2", david],
  ["users json filter wins", "GET", "/autocompletable/users.json?filter=kevin&query=da", david],
  ["users json blank filter", "GET", "/autocompletable/users.json?filter=%20&query=da", david],
  ["users json blank query", "GET", "/autocompletable/users.json?query=%20%20", david],
  ["users json query case", "GET", "/autocompletable/users.json?query=DA", david],
  ["users json query percent", "GET", "/autocompletable/users.json?query=%25", david],
  ["users json query underscore", "GET", "/autocompletable/users.json?query=_", david],
  ["users accept any", "GET", "/autocompletable/users?query=da", david("Accept" => "*/*")],
  ["users json browser accept", "GET", "/autocompletable/users.json?query=da", david],
  ["users room blank", "GET", "/autocompletable/users.json?room_id=&query=da", david],
  ["users room junk", "GET", "/autocompletable/users.json?room_id=#{HQ}xyz&query=da", david],
  ["users room abc", "GET", "/autocompletable/users.json?room_id=abc&query=da", david],
  ["users room missing", "GET", "/autocompletable/users.json?room_id=999999999&query=da", david],
  ["users bot json", "GET", "/autocompletable/users.json?bot_key=#{BENDER}", accept("application/json")],
  ["users bot html", "GET", "/autocompletable/users?bot_key=#{BENDER}", browser],
  ["users messy link", "GET", "/autocompletable/users.json?query=da&z=1&a=2", david],
  ["users json query page", "GET", "/autocompletable/users.json?query=page", david],
  ["users head", "HEAD", "/autocompletable/users.json?query=da", david],
  ["users html page 2", "GET", "/autocompletable/users?page=2", david],
  ["users filter array", "GET", "/autocompletable/users.json?filter[]=da", david],
  ["users room array", "GET", "/autocompletable/users.json?room_id[]=1", david],
  ["users old chrome", "GET", "/autocompletable/users.json?query=da", david("User-Agent" => OLD)]
]

TABLES = %w[ users sessions ]

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

RESPONSE_HEADERS = %w[ content-type location vary cache-control set-cookie x-frame-options etag link x-total-count x-version x-rev date ]

results = CASES.map do |name, method, path, headers, body, remote_addr|
  content_type = body ? "application/x-www-form-urlencoded" : nil
  input = body || ""
  env = Rack::MockRequest.env_for("http://#{HOST}#{path}", method: method, input: input)
  env["CONTENT_TYPE"] = content_type if content_type
  env["REMOTE_ADDR"] = remote_addr || "198.51.100.7"
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
    "request" => { "method" => method, "path" => path, "headers" => headers, "content_type" => content_type,
      "body" => body, "remote_addr" => env["REMOTE_ADDR"] },
    "response" => {
      "status" => status,
      "headers" => response_headers.slice(*RESPONSE_HEADERS),
      "body" => text.force_encoding("UTF-8")
    },
    "changes" => changes(before, after)
  }
end

File.write(File.join(WORK, OUTPUT), JSON.pretty_generate("now" => NOW.iso8601, "fixtures" => FIXTURES, "cases" => results) + "\n")

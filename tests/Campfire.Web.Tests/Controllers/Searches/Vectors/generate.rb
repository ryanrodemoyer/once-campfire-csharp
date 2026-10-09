# What the reference answers for SearchesController:
# index, create and clear, result rendering, recent searches, last room visited link, etc.
# Writes searches.json, which SearchesControllerTests replays through the C# router and
# controllers on the same database.
#
# The database is D02's tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 (the default parity
# seed). Run this script in the reference app on a copy of it, with the clock frozen:
#
#   mkdir -p tmp/m08/db tmp/m08/storage
#   cp tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 tmp/m08/db/production.sqlite3
#   parity/bin/reference runner --storage tmp/m08 --time 2026-03-02T16:00:00Z --freeze \
#     tests/Campfire.Web.Tests/Controllers/Searches/Vectors/generate.rb
#
require "json"
require "digest"
require "action_controller/test_case"

HOST = "campfire.test"
NOW = Time.now.utc
WORK = ENV.fetch("PARITY_WORK")
OUTPUT = ARGV.fetch(0, "tests/Campfire.Web.Tests/Controllers/Searches/Vectors/searches.json")

FIXTURES = [
  # David, an administrator, and Kevin, a member, signed in and active just now.
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (900001, 127326141, 'DavidSessionToken0000001', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (900002, 712064548, 'KevinSessionToken0000002', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')"
]
FIXTURES.each { |sql| ActiveRecord::Base.connection.execute(sql) }

DAVID = "DavidSessionToken0000001"
KEVIN = "KevinSessionToken0000002"
CSRF = "bTA4U2VhcmNoZXNDc3JmVG9rZW4wMG0wOFNlYXJjaGU"
SESSION_ID = "0123456789abcdef0123456789abcdef"
MODERN = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
PAGE = "text/html, application/xhtml+xml"
TURBO = "text/vnd.turbo-stream.html, text/html, application/xhtml+xml"

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

def submit(params) = URI.encode_www_form({ "authenticity_token" => TOKEN }.merge(params))

CASES = [
  # 1. Acceptance criterion: /searches?q=coffee
  ["searches with query coffee", "GET", "/searches?q=coffee", david],

  # 2. Initial view without query
  ["searches initial view", "GET", "/searches", david],

  # 3. Blank query (all spaces)
  ["searches blank query", "GET", "/searches?q=%20%20%20", david],

  # 4. Search query with non-word characters
  ["searches query with punctuation", "GET", "/searches?q=coffee%21%23%24", david],

  # 5. Member's search
  ["searches member query coffee", "GET", "/searches?q=coffee", kevin],

  # 6. Last room visited cookie remembered
  ["searches with last room visited", "GET", "/searches?q=coffee", david("Cookie" => "#{session_token(DAVID)}; #{CSRF_SESSION}; last_room=201306877")],

  # 7. Signed out
  ["searches signed out", "GET", "/searches?q=coffee", browser],

  # 8. Format JSON -> 406
  ["searches format json", "GET", "/searches.json?q=coffee", david("Accept" => "application/json")],

  # 9. Create search term
  ["create search", "POST", "/searches", david("Accept" => TURBO), submit("q" => "coffee")],

  # 10. Index after recording a search (recent searches shown)
  ["searches after create", "GET", "/searches?q=coffee", david],

  # 11. Create another search term with punctuation
  ["create search with punctuation", "POST", "/searches", david("Accept" => TURBO), submit("q" => "hello world!")],

  # 12. Create search without CSRF token -> 422
  ["create search without CSRF", "POST", "/searches", david("Accept" => TURBO), URI.encode_www_form("q" => "forged")],

  # 13. Clear searches without CSRF token -> 422
  ["clear searches without CSRF", "DELETE", "/searches/clear", david("Accept" => TURBO), ""],

  # 14. Clear searches
  ["clear searches", "DELETE", "/searches/clear", david("Accept" => TURBO), submit({})],

  # 15. Index after clear
  ["searches after clear", "GET", "/searches", david]
]

TABLES = %w[ searches sessions ]

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

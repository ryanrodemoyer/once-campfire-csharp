# What the reference does through Messages::BoostsController: each response, the Action Cable
# broadcasts it made, and every row it changed. Writes boosts.json, which BoostsControllerTests
# replays through the C# router and controller on the same database.
#
# The database is D02's tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 (the default parity
# seed). Run this script in the reference app on a copy of it, with the clock frozen:
#
#   mkdir -p tmp/m06/db tmp/m06/storage
#   cp tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 tmp/m06/db/production.sqlite3
#   parity/bin/reference runner --storage tmp/m06 --time 2026-03-02T16:00:00Z --freeze \
#     tests/Campfire.Web.Tests/Controllers/Messages/Vectors/boosts.rb > tests/Campfire.Web.Tests/Controllers/Messages/Vectors/boosts.json
#
# Requests go straight to Rails.application (the stack Puma runs), in order, on one database.
# The fixtures below are applied first, and the C# test applies the same SQL to its copy.
require "json"
require "action_controller/test_case"

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
CSRF = "m06BoostsCsrfToken00m06BoostsCsrfToken00m0A" # the session's _csrf_token (urlsafe base64, 32 bytes)
SESSION_ID = "0123456789abcdef0123456789abcdef"
MODERN = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"

LAUNCH = 933434507       # Kevin's, in Designers (closed; David, Kevin, ...), with six boosts
LAUNCH_ID = "4f384e0a-1ad0-57d7-8c1b-b1ee6797e852"
PIZZA = 933434508        # Jason's, in Designers, with JZ's boost
QUIET = 933434482        # Jason's, in Designers, without boosts
ALL_TALK = 136976342     # Jason's, in All Talk (closed), which Kevin isn't in
KEVIN_HEART = 329428239  # Kevin's boost of LAUNCH
DAVID_PLUS_ONE = 329428241 # David's boost of LAUNCH

TURBO_STREAM = "text/vnd.turbo-stream.html, text/html, application/xhtml+xml"
TURBO_FRAME = "text/html, application/xhtml+xml"

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

def signed_in(token, extra = {})
  { "Cookie" => "#{session_token(token)}; #{CSRF_SESSION}", "User-Agent" => MODERN }.merge(extra)
end

def writer(token, accept, extra = {})
  signed_in(token, { "Accept" => accept, "X-CSRF-Token" => TOKEN, "Origin" => "http://#{HOST}" }.merge(extra))
end

def form(params) = URI.encode_www_form(params)

# Boosts made below, by content.
def boosted(content) = Boost.find_by!(content: content).id

FORM = "application/x-www-form-urlencoded"

# [name, method, path (or a proc for one), headers, body]
CASES = [
  # index
  ["index in its frame", "GET", "/messages/#{LAUNCH}/boosts", signed_in(KEVIN, "Accept" => TURBO_FRAME, "Turbo-Frame" => "boosting_message_#{LAUNCH_ID}")],
  ["index, full page", "GET", "/messages/#{LAUNCH}/boosts", signed_in(KEVIN, "Accept" => TURBO_FRAME)],
  ["index of a message without boosts", "GET", "/messages/#{QUIET}/boosts", signed_in(KEVIN, "Accept" => TURBO_FRAME, "Turbo-Frame" => "x")],
  ["index, JSON", "GET", "/messages/#{LAUNCH}/boosts.json", signed_in(KEVIN)],
  ["index of a message in a room the member isn't in", "GET", "/messages/#{ALL_TALK}/boosts", signed_in(KEVIN, "Accept" => TURBO_FRAME)],
  ["index of a missing message", "GET", "/messages/1/boosts", signed_in(KEVIN, "Accept" => TURBO_FRAME)],
  ["index, signed out", "GET", "/messages/#{LAUNCH}/boosts", { "Accept" => TURBO_FRAME, "User-Agent" => MODERN }],

  # new
  ["new in its frame", "GET", "/messages/#{PIZZA}/boosts/new", signed_in(KEVIN, "Accept" => TURBO_FRAME, "Turbo-Frame" => "new_boost_message_9e72b61d-e884-5b22-b5c9-3e5d0809134a")],
  ["new, full page", "GET", "/messages/#{PIZZA}/boosts/new", signed_in(DAVID, "Accept" => TURBO_FRAME)],
  ["new for a message in a room the member isn't in", "GET", "/messages/#{ALL_TALK}/boosts/new", signed_in(KEVIN, "Accept" => TURBO_FRAME)],

  # create
  ["create from the form", "POST", "/messages/#{PIZZA}/boosts", writer(KEVIN, TURBO_STREAM, "Turbo-Frame" => "boosting_message_9e72b61d-e884-5b22-b5c9-3e5d0809134a"),
    form("boost[content]" => "Yum!")],
  ["index after a create", "GET", "/messages/#{PIZZA}/boosts", signed_in(KEVIN, "Accept" => TURBO_FRAME, "Turbo-Frame" => "boosting_message_9e72b61d-e884-5b22-b5c9-3e5d0809134a")],
  ["create emoji", "POST", "/messages/#{QUIET}/boosts", writer(DAVID, TURBO_STREAM), form("boost[content]" => "🔥🔥")],
  ["create, HTML", "POST", "/messages/#{QUIET}/boosts", writer(KEVIN, "text/html"), form("boost[content]" => "<b>bold</b>")],
  ["create, JSON", "POST", "/messages/#{QUIET}/boosts", writer(KEVIN, "application/json"), form("boost[content]" => "as json")],
  ["create longer than the column", "POST", "/messages/#{QUIET}/boosts", writer(KEVIN, TURBO_STREAM), form("boost[content]" => "This is far longer than sixteen")],
  ["create a blank boost", "POST", "/messages/#{QUIET}/boosts", writer(KEVIN, TURBO_STREAM), form("boost[content]" => "")],
  ["create with unpermitted params", "POST", "/messages/#{QUIET}/boosts", writer(KEVIN, TURBO_STREAM),
    form("boost[content]" => "sneaky", "boost[booster_id]" => "127326141", "boost[message_id]" => "#{LAUNCH}")],
  ["create without content", "POST", "/messages/#{QUIET}/boosts", writer(KEVIN, TURBO_STREAM), form("boost[other]" => "x")],
  ["create without boost params", "POST", "/messages/#{QUIET}/boosts", writer(KEVIN, TURBO_STREAM), form("content" => "x")],
  ["create on a message in a room the member isn't in", "POST", "/messages/#{ALL_TALK}/boosts", writer(KEVIN, TURBO_STREAM), form("boost[content]" => "hi")],
  ["create without a CSRF token", "POST", "/messages/#{QUIET}/boosts", signed_in(KEVIN, "Accept" => TURBO_STREAM), form("boost[content]" => "no token")],

  # destroy
  ["destroy from the boost's button", "POST", "/messages/#{LAUNCH}/boosts/#{KEVIN_HEART}",
    writer(KEVIN, TURBO_STREAM, "Turbo-Frame" => "boosting_message_#{LAUNCH_ID}"), form("_method" => "delete")],
  ["destroy a boost made here", "DELETE", -> { "/messages/#{PIZZA}/boosts/#{boosted("Yum!")}" }, writer(KEVIN, TURBO_STREAM)],
  ["destroy, HTML", "DELETE", -> { "/messages/#{QUIET}/boosts/#{boosted("🔥🔥")}" }, writer(DAVID, "text/html")],
  ["destroy someone else's boost", "DELETE", "/messages/#{LAUNCH}/boosts/#{DAVID_PLUS_ONE}", writer(KEVIN, TURBO_STREAM)],
  ["administrator destroys someone else's boost", "DELETE", "/messages/#{PIZZA}/boosts/329428242", writer(DAVID, TURBO_STREAM)],
  ["destroy through another message", "DELETE", "/messages/#{PIZZA}/boosts/#{DAVID_PLUS_ONE}", writer(DAVID, TURBO_STREAM)],
  ["destroy a missing boost", "DELETE", "/messages/#{LAUNCH}/boosts/1", writer(KEVIN, TURBO_STREAM)],

  # Routes the controller has no action for
  ["show", "GET", "/messages/#{LAUNCH}/boosts/#{DAVID_PLUS_ONE}", signed_in(KEVIN, "Accept" => TURBO_FRAME)],
  ["edit", "GET", "/messages/#{LAUNCH}/boosts/#{DAVID_PLUS_ONE}/edit", signed_in(DAVID, "Accept" => TURBO_FRAME)],
  ["update", "PATCH", "/messages/#{LAUNCH}/boosts/#{DAVID_PLUS_ONE}", writer(DAVID, TURBO_STREAM), form("boost[content]" => "+2")]
]

# Every row of the tables a message write touches, keyed by id.
TABLES = %w[ messages rooms memberships boosts sessions ]

def snapshot
  connection = ActiveRecord::Base.connection
  state = TABLES.to_h do |table|
    [ table, connection.select_all("SELECT * FROM #{table}").rows.zip.map(&:first).to_h { |row| [ row.first, row ] } ]
  end
  state["message_search_index"] = connection.select_rows("SELECT rowid, body FROM message_search_index").to_h { |row| [ row.first, row ] }
  state
end

def columns(table)
  return %w[ rowid body ] if table == "message_search_index"
  ActiveRecord::Base.connection.select_all("SELECT * FROM #{table} LIMIT 0").columns
end

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
ActiveSupport::Notifications.subscribe("enqueue.active_job") do |*, payload|
  job = payload[:job]
  events << { "job" => job.class.name, "arguments" => job.arguments.map { |argument| argument.respond_to?(:to_global_id) ? argument.to_global_id.to_s : argument } }
end

RESPONSE_HEADERS = %w[ content-type location vary cache-control etag set-cookie x-frame-options ]

results = CASES.map do |name, method, path, headers, body|
  path = path.call if path.respond_to?(:call)
  body = body.call if body.respond_to?(:call)
  headers = headers.dup
  content_type = headers.delete("Content-Type") || (body ? FORM : nil)

  env = Rack::MockRequest.env_for("http://#{HOST}#{path}", method: method, input: body || "")
  env["CONTENT_TYPE"] = content_type if content_type
  env["REMOTE_ADDR"] = "198.51.100.7"
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
    "request" => { "method" => method, "path" => path, "headers" => headers, "content_type" => content_type, "body" => body },
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

# What the reference does through Rooms::OpensController, Rooms::ClosedsController,
# Rooms::DirectsController, Rooms::InvolvementsController and rooms#destroy: each response, the
# Action Cable broadcasts it made, and every row it changed. Writes settings.json, which
# RoomSettingsReplayTests replays through the C# router and controllers on the same database.
#
# The database is D02's tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 (the default parity
# seed). Run this script in the reference app on a copy of it, with the clock frozen:
#
#   mkdir -p tmp/m07/db tmp/m07/storage
#   cp tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 tmp/m07/db/production.sqlite3
#   parity/bin/reference runner -e RAILS_LOG_LEVEL=fatal --storage tmp/m07 --time 2026-03-02T16:00:00Z --freeze \
#     tests/Campfire.Web.Tests/Controllers/Rooms/Vectors/settings.rb > tests/Campfire.Web.Tests/Controllers/Rooms/Vectors/settings.json
#
# Requests go straight to Rails.application (the stack Puma runs), in order, on one database.
# The fixtures below are applied first, and the C# test applies the same SQL to its copy. A case
# may run SQL of its own first ("sql"), which the C# test runs too.
require "json"
require "action_controller/test_case"

HOST = "campfire.test"
NOW = Time.now.utc

FIXTURES = [
  # Kevin, a member, signed in and active just now (no activity refresh).
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (900001, 712064548, 'KevinSessionToken0000001', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
  # JZ, a member who created nothing, signed in too.
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (900002, 773523953, 'JzSessionToken0000000001', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')"
]
FIXTURES.each { |sql| ActiveRecord::Base.connection.execute(sql) }

DAVID = "AxJs94fteQ5Autv2VrKsH68c" # administrator; last active 14:00, so the first request refreshes it
KEVIN = "KevinSessionToken0000001"
JZ = "JzSessionToken0000000001"
CSRF = "m07RoomsCsrfToken000m07RoomsCsrfToken000m0A" # the session's _csrf_token (urlsafe base64, 32 bytes)
SESSION_ID = "0123456789abcdef0123456789abcdef"
MODERN = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"

DAVID_ID = 127326141   # administrator
JASON_ID = 149087659   # administrator
KEVIN_ID = 712064548   # member
JZ_ID = 773523953      # member
RITA_ID = 773523954    # deactivated
LOU_ID = 773523958     # member, in no rooms

ALL_PETS = 104393281      # open; David, Jason, Mallory, Deploy Bot
DAVID_JASON = 186869642   # direct; three messages
HQ = 201306877            # open; David's (nothing), Kevin, JZ, ...
KEVIN_BENDER = 340026324  # direct; Kevin and Bender Bot, one message
ALL_TALK = 486777696      # closed; David, Jason, Bender Bot; 131 messages
DESIGNERS = 654632876     # closed; David, Jason, Kevin, JZ, Mallory, Deploy Bot
DAVID_KEVIN = 699448325   # direct
QUIET = 699448326         # closed; Kevin's; David and Kevin; no messages
ARCHIVE = 699448327       # open; David's is invisible
BROKEN = 699448328        # closed; David only; one broken message
GROUP = 699448329         # direct; David, Jason, Kevin, JZ

PAGE = "text/html, application/xhtml+xml"
TURBO_STREAM = "text/vnd.turbo-stream.html, text/html, application/xhtml+xml"

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
  { "Cookie" => "#{session_token(token)}; #{CSRF_SESSION}", "User-Agent" => MODERN, "Accept" => PAGE }.merge(extra)
end

def writer(token, extra = {})
  signed_in(token, { "Accept" => TURBO_STREAM, "X-CSRF-Token" => TOKEN, "Origin" => "http://#{HOST}" }.merge(extra))
end

def form(params) = URI.encode_www_form(params)

# Rooms made below, by name.
def made(name) = Room.find_by!(name: name).id
def newest_room = Room.maximum(:id)

RESTRICT = "UPDATE accounts SET settings = '{\"restrict_room_creation_to_administrators\":true}'"
UNRESTRICT = "UPDATE accounts SET settings = NULL"

# [name, method, path (or a proc for one), headers, body, sql run first]
CASES = [
  # new
  [ "new open room", "GET", "/rooms/opens/new", signed_in(DAVID) ],
  [ "new open room, member", "GET", "/rooms/opens/new", signed_in(KEVIN) ],
  [ "new closed room", "GET", "/rooms/closeds/new", signed_in(DAVID) ],
  [ "new closed room, member", "GET", "/rooms/closeds/new", signed_in(JZ) ],
  [ "new direct room in its frame", "GET", "/rooms/directs/new", signed_in(DAVID, "Turbo-Frame" => "direct_rooms_control") ],
  [ "new direct room, full page", "GET", "/rooms/directs/new", signed_in(KEVIN) ],
  [ "new open room in a Turbo frame", "GET", "/rooms/opens/new", signed_in(DAVID, "Turbo-Frame" => "x") ],
  [ "new open room, JSON", "GET", "/rooms/opens/new.json", signed_in(DAVID) ],
  [ "new open room, signed out", "GET", "/rooms/opens/new", { "User-Agent" => MODERN, "Accept" => PAGE } ],

  # edit
  [ "edit an open room", "GET", "/rooms/opens/#{HQ}/edit", signed_in(DAVID) ],
  [ "edit an open room, member", "GET", "/rooms/opens/#{HQ}/edit", signed_in(KEVIN) ],
  [ "edit a closed room", "GET", "/rooms/closeds/#{DESIGNERS}/edit", signed_in(DAVID) ],
  [ "edit a closed room, member", "GET", "/rooms/closeds/#{DESIGNERS}/edit", signed_in(KEVIN) ],
  [ "edit a closed room, its creator", "GET", "/rooms/closeds/#{QUIET}/edit", signed_in(KEVIN) ],
  [ "edit a closed room as open", "GET", "/rooms/opens/#{ALL_TALK}/edit", signed_in(DAVID) ],
  [ "edit an open room as closed", "GET", "/rooms/closeds/#{HQ}/edit", signed_in(DAVID) ],
  [ "edit a direct room", "GET", "/rooms/directs/#{DAVID_JASON}/edit", signed_in(DAVID) ],
  [ "edit a group direct room", "GET", "/rooms/directs/#{GROUP}/edit", signed_in(KEVIN) ],
  [ "edit a direct room with a bot", "GET", "/rooms/directs/#{KEVIN_BENDER}/edit", signed_in(KEVIN) ],
  [ "edit a direct room as open", "GET", "/rooms/opens/#{DAVID_JASON}/edit", signed_in(DAVID) ],
  [ "edit a direct room as closed", "GET", "/rooms/closeds/#{DAVID_JASON}/edit", signed_in(DAVID) ],
  [ "edit a closed room as direct", "GET", "/rooms/directs/#{DESIGNERS}/edit", signed_in(DAVID) ],
  [ "edit a room the member isn't in", "GET", "/rooms/opens/#{ALL_PETS}/edit", signed_in(KEVIN) ],
  [ "edit a missing room", "GET", "/rooms/closeds/1/edit", signed_in(KEVIN) ],
  [ "edit with a last room cookie", "GET", "/rooms/opens/#{HQ}/edit",
    signed_in(KEVIN, "Cookie" => "#{session_token(KEVIN)}; #{CSRF_SESSION}; last_room=#{DESIGNERS}") ],
  [ "edit in a Turbo frame", "GET", "/rooms/closeds/#{DESIGNERS}/edit", signed_in(DAVID, "Turbo-Frame" => "x") ],

  # show and index
  [ "show an open room", "GET", "/rooms/opens/#{HQ}", signed_in(KEVIN) ],
  [ "show a closed room", "GET", "/rooms/closeds/#{DESIGNERS}", signed_in(KEVIN) ],
  [ "show a closed room as open", "GET", "/rooms/opens/#{DESIGNERS}", signed_in(KEVIN) ],
  [ "show a direct room as open", "GET", "/rooms/opens/#{DAVID_KEVIN}", signed_in(KEVIN) ],
  [ "show a direct room", "GET", "/rooms/directs/#{DAVID_KEVIN}", signed_in(KEVIN) ],
  [ "index of open rooms", "GET", "/rooms/opens", signed_in(KEVIN) ],
  [ "index of direct rooms", "GET", "/rooms/directs", signed_in(DAVID) ],

  # create
  [ "create an open room", "POST", "/rooms/opens", writer(DAVID), form("room[name]" => "Lobby") ],
  [ "create an open room, member", "POST", "/rooms/opens", writer(KEVIN, "Accept" => PAGE), form("room[name]" => "Kevin's <b>Place</b>") ],
  [ "create an open room with unpermitted params", "POST", "/rooms/opens", writer(JZ),
    form("room[name]" => "Sneaky", "room[type]" => "Rooms::Direct", "room[creator_id]" => DAVID_ID.to_s) ],
  [ "create an open room with a blank name", "POST", "/rooms/opens", writer(DAVID), form("room[name]" => "") ],
  [ "create an open room without room params", "POST", "/rooms/opens", writer(DAVID), form("name" => "x") ],
  [ "create an open room without a CSRF token", "POST", "/rooms/opens", signed_in(DAVID, "Accept" => TURBO_STREAM), form("room[name]" => "No token") ],
  [ "create a closed room", "POST", "/rooms/closeds", writer(DAVID),
    form([ [ "room[name]", "Inner Circle" ], [ "user_ids[]", DAVID_ID ], [ "user_ids[]", KEVIN_ID ], [ "user_ids[]", JASON_ID ] ]) ],
  [ "create a closed room, member", "POST", "/rooms/closeds", writer(KEVIN),
    form([ [ "room[name]", "Kevin and Lou" ], [ "user_ids[]", KEVIN_ID ], [ "user_ids[]", LOU_ID ], [ "user_ids[]", RITA_ID ], [ "user_ids[]", "999" ] ]) ],
  [ "create a closed room without users", "POST", "/rooms/closeds", writer(DAVID), form("room[name]" => "Nobody") ],
  [ "create a direct room", "POST", "/rooms/directs", writer(DAVID), form([ [ "user_ids[]", JZ_ID ] ]) ],
  [ "create the same direct room", "POST", "/rooms/directs", writer(DAVID), form([ [ "user_ids[]", JZ_ID ] ]) ],
  [ "create an existing direct room", "POST", "/rooms/directs", writer(JZ, "Accept" => PAGE),
    form([ [ "user_ids[]", DAVID_ID ], [ "user_ids[]", JASON_ID ], [ "user_ids[]", KEVIN_ID ] ]) ],
  [ "create a direct room with several", "POST", "/rooms/directs", writer(KEVIN),
    form([ [ "user_ids[]", JZ_ID ], [ "user_ids[]", LOU_ID ] ]) ],
  [ "create a direct room with yourself", "POST", "/rooms/directs", writer(KEVIN), form("user_ids_input" => "") ],

  # Restricted room creation
  [ "new open room, restricted, member", "GET", "/rooms/opens/new", signed_in(KEVIN), nil, RESTRICT ],
  [ "new closed room, restricted, member", "GET", "/rooms/closeds/new", signed_in(KEVIN) ],
  [ "create an open room, restricted, member", "POST", "/rooms/opens", writer(KEVIN), form("room[name]" => "Not allowed") ],
  [ "create a closed room, restricted, member", "POST", "/rooms/closeds", writer(KEVIN), form([ [ "room[name]", "Not allowed" ], [ "user_ids[]", KEVIN_ID ] ]) ],
  [ "create a direct room, restricted, member", "POST", "/rooms/directs", writer(KEVIN), form([ [ "user_ids[]", DAVID_ID ] ]) ],
  [ "create an open room, restricted, administrator", "POST", "/rooms/opens", writer(DAVID), form("room[name]" => "Allowed") ],
  [ "new closed room, unrestricted again", "GET", "/rooms/closeds/new", signed_in(KEVIN), nil, UNRESTRICT ],

  # update
  [ "update an open room", "PATCH", "/rooms/opens/#{HQ}", writer(DAVID), form("room[name]" => "Headquarters") ],
  [ "update an open room from the form", "POST", "/rooms/opens/#{HQ}", writer(DAVID, "Accept" => PAGE),
    form("_method" => "patch", "room[name]" => "HQ") ],
  [ "update an open room, member", "PUT", "/rooms/opens/#{HQ}", writer(KEVIN), form("room[name]" => "Kevin's HQ") ],
  [ "update a room, its creator", "PATCH", "/rooms/closeds/#{QUIET}", writer(KEVIN),
    form([ [ "room[name]", "Quieter Corner" ], [ "user_ids[]", KEVIN_ID ], [ "user_ids[]", DAVID_ID ], [ "user_ids[]", JZ_ID ] ]) ],
  [ "update a closed room to be open", "PATCH", "/rooms/opens/#{ALL_TALK}", writer(DAVID), form("room[name]" => "All Talk") ],
  [ "update with membership revisions", "PATCH", "/rooms/closeds/#{DESIGNERS}", writer(DAVID),
    form([ [ "room[name]", "Design" ], [ "user_ids[]", DAVID_ID ], [ "user_ids[]", KEVIN_ID ], [ "user_ids[]", JZ_ID ], [ "user_ids[]", LOU_ID ] ]) ],
  [ "update an open room to be closed", "PATCH", "/rooms/closeds/#{ALL_PETS}", writer(DAVID),
    form([ [ "room[name]", "All Pets" ], [ "user_ids[]", DAVID_ID ], [ "user_ids[]", JASON_ID ] ]) ],
  [ "update a closed room, member", "PATCH", "/rooms/closeds/#{DESIGNERS}", writer(KEVIN),
    form([ [ "room[name]", "Kevin's" ], [ "user_ids[]", KEVIN_ID ] ]) ],
  [ "update a closed room without users", "PATCH", -> { "/rooms/closeds/#{made("Inner Circle")}" }, writer(DAVID), form("room[name]" => "Outer Circle") ],
  [ "remove yourself", "PATCH", "/rooms/closeds/#{ALL_PETS}", writer(DAVID), form([ [ "room[name]", "Pets" ], [ "user_ids[]", JASON_ID ] ]) ],
  [ "the room you left", "GET", "/rooms/#{ALL_PETS}", signed_in(DAVID) ],
  [ "update a direct room as open", "PATCH", "/rooms/opens/#{KEVIN_BENDER}", writer(KEVIN), form("room[name]" => "Watercooler") ],
  [ "update a direct room as closed", "PATCH", "/rooms/closeds/#{DAVID_KEVIN}", writer(DAVID),
    form([ [ "room[name]", "Watercooler" ], [ "user_ids[]", DAVID_ID ], [ "user_ids[]", JZ_ID ] ]) ],
  [ "update a direct room", "PATCH", "/rooms/directs/#{DAVID_KEVIN}", writer(DAVID), form("room[name]" => "Watercooler") ],
  [ "update without room params", "PATCH", "/rooms/opens/#{HQ}", writer(DAVID), form("name" => "x") ],
  [ "update a room the member isn't in", "PATCH", "/rooms/opens/#{ALL_TALK}", writer(JZ), form("room[name]" => "Mine") ],

  # involvement
  [ "involvement", "GET", "/rooms/#{HQ}/involvement", signed_in(DAVID, "Turbo-Frame" => "involvement_rooms_open_#{HQ}") ],
  [ "involvement, full page", "GET", "/rooms/#{DESIGNERS}/involvement", signed_in(KEVIN) ],
  [ "involvement in a direct room", "GET", "/rooms/#{GROUP}/involvement", signed_in(KEVIN, "Turbo-Frame" => "involvement_rooms_direct_#{GROUP}") ],
  [ "involvement in a room the member isn't in", "GET", "/rooms/#{ALL_TALK}/involvement", signed_in(JZ) ],
  [ "involvement in a missing room", "GET", "/rooms/1/involvement", signed_in(JZ) ],
  [ "involvement becomes invisible", "PUT", "/rooms/#{HQ}/involvement?involvement=invisible", writer(DAVID, "Turbo-Frame" => "involvement_rooms_open_#{HQ}"), "" ],
  [ "involvement becomes visible", "PUT", "/rooms/#{ARCHIVE}/involvement?involvement=everything", writer(DAVID), "" ],
  [ "involvement changes while visible", "PUT", "/rooms/#{DESIGNERS}/involvement", writer(KEVIN), form("involvement" => "everything") ],
  [ "involvement from the button", "POST", "/rooms/#{DESIGNERS}/involvement?involvement=nothing", writer(JZ, "Accept" => PAGE), form("_method" => "put") ],
  [ "involvement in a direct room changes", "PUT", "/rooms/#{GROUP}/involvement?involvement=nothing", writer(KEVIN), "" ],
  [ "involvement in a direct room becomes invisible", "PUT", "/rooms/#{DAVID_KEVIN}/involvement?involvement=invisible", writer(KEVIN), "" ],
  [ "involvement that isn't one", "PUT", "/rooms/#{HQ}/involvement?involvement=loud", writer(KEVIN), "" ],
  [ "involvement missing", "PUT", "/rooms/#{HQ}/involvement", writer(KEVIN), "" ],
  [ "involvement after it went missing", "GET", "/rooms/#{HQ}/involvement", signed_in(KEVIN, "Turbo-Frame" => "involvement_rooms_open_#{HQ}") ],
  [ "involvement in a room the member isn't in, update", "PUT", "/rooms/#{ALL_TALK}/involvement?involvement=everything", writer(JZ), "" ],

  # destroy
  [ "destroy a room, member", "DELETE", "/rooms/#{HQ}", writer(KEVIN) ],
  [ "destroy a room, its creator", "POST", "/rooms/#{QUIET}", writer(KEVIN, "Accept" => PAGE), form("_method" => "delete") ],
  [ "destroy a room with a message", "DELETE", "/rooms/#{BROKEN}", writer(DAVID) ],
  [ "destroy a room the member isn't in", "DELETE", "/rooms/#{ALL_TALK}", writer(JZ) ],
  [ "destroy a missing room", "DELETE", "/rooms/1", writer(DAVID) ],
  [ "destroy a direct room", "DELETE", "/rooms/directs/#{KEVIN_BENDER}", writer(KEVIN) ],
  [ "destroy a direct room with messages", "POST", "/rooms/directs/#{DAVID_JASON}", writer(DAVID, "Accept" => PAGE), form("_method" => "delete") ],
  [ "destroy a closed room as direct", "DELETE", "/rooms/directs/#{DESIGNERS}", writer(KEVIN) ],
  [ "destroy an open room as direct", "DELETE", "/rooms/directs/#{ARCHIVE}", writer(DAVID) ],
  [ "destroy a direct room the member isn't in", "DELETE", "/rooms/directs/#{DAVID_KEVIN}", writer(JZ) ],
  [ "destroy an open room through opens", "DELETE", "/rooms/opens/#{ARCHIVE}", writer(DAVID) ],
  [ "destroy a closed room through closeds", "DELETE", "/rooms/closeds/#{DESIGNERS}", writer(DAVID) ],
  [ "destroy a room without a CSRF token", "DELETE", "/rooms/#{ARCHIVE}", signed_in(DAVID, "Accept" => TURBO_STREAM) ],
  [ "destroy a room made here", "DELETE", -> { "/rooms/#{made("Lobby")}" }, writer(DAVID) ]
]

# Every row of the tables a room write touches, keyed by id.
TABLES = %w[ accounts rooms memberships messages boosts action_text_rich_texts sessions ]

def snapshot
  connection = ActiveRecord::Base.connection
  state = TABLES.to_h do |table|
    [ table, connection.select_all("SELECT * FROM #{table}").rows.to_h { |row| [ row.first, row ] } ]
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

results = CASES.map do |name, method, path, headers, body, sql|
  ActiveRecord::Base.connection.execute(sql) if sql
  path = path.call if path.respond_to?(:call)
  body = body.call if body.respond_to?(:call)
  headers = headers.dup
  content_type = headers.delete("Content-Type") || (body ? "application/x-www-form-urlencoded" : nil)

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
    "sql" => sql,
    "request" => { "method" => method, "path" => path, "headers" => headers, "content_type" => content_type, "body" => body },
    "response" => {
      "status" => status,
      "headers" => response_headers.to_h.transform_keys(&:downcase).slice(*RESPONSE_HEADERS),
      "body" => text.force_encoding("UTF-8")
    },
    "events" => events.dup,
    "changes" => changes(before, after)
  }.compact
end

puts JSON.pretty_generate("now" => NOW.iso8601, "fixtures" => FIXTURES, "cases" => results)

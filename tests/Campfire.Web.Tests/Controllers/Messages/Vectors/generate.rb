# What the reference does when messages are created, shown, edited, updated and destroyed
# through MessagesController: each response, the Action Cable broadcasts and Active Job enqueues
# it made, and every row it changed. Writes writes.json, which WriteControllerTests replays
# through the C# router and controllers on the same database.
#
# The database is D02's tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 (the default parity
# seed). Run this script in the reference app on a copy of it, with the clock frozen:
#
#   mkdir -p tmp/m05/db tmp/m05/storage
#   cp tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 tmp/m05/db/production.sqlite3
#   parity/bin/reference runner --storage tmp/m05 --time 2026-03-02T16:00:00Z --freeze \
#     tests/Campfire.Web.Tests/Controllers/Messages/Vectors/generate.rb > tests/Campfire.Web.Tests/Controllers/Messages/Vectors/writes.json
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
CSRF = "m05WritesCsrfToken00m05WritesCsrfToken00m0A" # the session's _csrf_token (urlsafe base64, 32 bytes)
SESSION_ID = "0123456789abcdef0123456789abcdef"
MODERN = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"

HQ = 201306877           # open; David, Kevin, Deploy Bot (no webhook), ...
ARCHIVE = 699448327      # open; David, Kevin, Bender (webhook), Deploy Bot
KEVIN_BENDER = 340026324 # direct; Kevin and Bender
DESIGNERS = 654632876    # closed; David, Kevin, ...
ALL_PETS = 104393281     # open; Kevin isn't in it

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

def mention(user)
  sgid = user.attachable_sgid
  content = ApplicationController.render(partial: "users/mention", locals: { user: user })
  %(<action-text-attachment sgid="#{sgid}" content-type="application/vnd.campfire.mention" content="#{ERB::Util.html_escape(content.to_str)}"></action-text-attachment>)
end

BENDER = User.find(394959859)
DEPLOY_BOT = User.find(773523956)

# Messages made below, by client_message_id.
def created(client_message_id) = Message.find_by!(client_message_id: client_message_id).id

FORM = "application/x-www-form-urlencoded"

# [name, method, path (or a proc for one), headers, body]
CASES = [
  # create
  ["member creates in an open room", "POST", "/rooms/#{HQ}/messages", writer(KEVIN, TURBO_STREAM),
    form("message[body]" => "<p>Hello <strong>HQ</strong> &amp; https://example.com/hq</p>", "message[client_message_id]" => "m05-hq-hello")],
  ["member creates in a direct room with a bot", "POST", "/rooms/#{KEVIN_BENDER}/messages", writer(KEVIN, TURBO_STREAM),
    form("message[body]" => "<p>Bender, status?</p>", "message[client_message_id]" => "m05-direct-bender")],
  ["administrator mentions bots", "POST", "/rooms/#{ARCHIVE}/messages", writer(DAVID, TURBO_STREAM),
    form("message[body]" => "<p>Ping #{mention(BENDER)} and #{mention(DEPLOY_BOT)} and #{mention(BENDER)}</p>", "message[client_message_id]" => "m05-archive-mentions")],
  ["member plays a sound", "POST", "/rooms/#{HQ}/messages", writer(KEVIN, TURBO_STREAM),
    form("message[body]" => "<p>/play 56k</p>", "message[client_message_id]" => "m05-hq-sound")],
  ["member sends emoji", "POST", "/rooms/#{HQ}/messages", writer(KEVIN, TURBO_STREAM),
    form("message[body]" => "<p>🔥🔥</p>", "message[client_message_id]" => "m05-hq-emoji")],
  ["member sends a blank body", "POST", "/rooms/#{HQ}/messages", writer(KEVIN, TURBO_STREAM),
    form("message[body]" => "", "message[client_message_id]" => "m05-hq-blank")],
  ["member sends no body", "POST", "/rooms/#{HQ}/messages", writer(KEVIN, TURBO_STREAM),
    form("message[client_message_id]" => "m05-hq-nobody")],
  ["create without a client message id", "POST", "/rooms/#{HQ}/messages", writer(KEVIN, TURBO_STREAM),
    form("message[body]" => "<p>No id</p>")],
  ["create, XHR without Accept", "POST", "/rooms/#{HQ}/messages", writer(KEVIN, "*/*"),
    form("message[body]" => "<p>From XHR</p>", "message[client_message_id]" => "m05-hq-xhr")],
  ["create, HTML", "POST", "/rooms/#{HQ}/messages", writer(KEVIN, "text/html"),
    form("message[body]" => "<p>As HTML</p>", "message[client_message_id]" => "m05-hq-html")],
  ["create, JSON", "POST", "/rooms/#{HQ}/messages", writer(KEVIN, "application/json"),
    form("message[body]" => "<p>As JSON</p>", "message[client_message_id]" => "m05-hq-json")],
  ["create, multipart form", "POST", "/rooms/#{HQ}/messages",
    writer(KEVIN, TURBO_STREAM, "Content-Type" => "multipart/form-data; boundary=m05boundary"),
    "--m05boundary\r\nContent-Disposition: form-data; name=\"message[body]\"\r\n\r\n<p>Multipart</p>\r\n" \
    "--m05boundary\r\nContent-Disposition: form-data; name=\"message[client_message_id]\"\r\n\r\nm05-hq-multipart\r\n--m05boundary--\r\n"],
  ["create with unpermitted params", "POST", "/rooms/#{HQ}/messages", writer(KEVIN, TURBO_STREAM),
    form("message[body]" => "<p>Sneaky</p>", "message[client_message_id]" => "m05-hq-sneaky", "message[creator_id]" => "127326141", "message[room_id]" => "#{ARCHIVE}")],
  ["create in a room the member isn't in", "POST", "/rooms/#{ALL_PETS}/messages", writer(KEVIN, TURBO_STREAM),
    form("message[body]" => "<p>Let me in</p>", "message[client_message_id]" => "m05-pets")],
  ["create without a room", "POST", "/messages", writer(KEVIN, TURBO_STREAM),
    form("message[body]" => "<p>Nowhere</p>", "message[client_message_id]" => "m05-nowhere")],
  ["create without message params", "POST", "/rooms/#{HQ}/messages", writer(KEVIN, TURBO_STREAM), form("body" => "x")],
  ["create without a CSRF token", "POST", "/rooms/#{HQ}/messages", signed_in(KEVIN, "Accept" => TURBO_STREAM),
    form("message[body]" => "<p>No token</p>", "message[client_message_id]" => "m05-no-token")],

  # show
  ["show", "GET", -> { "/rooms/#{HQ}/messages/#{created("m05-hq-hello")}" }, signed_in(KEVIN, "Accept" => TURBO_FRAME)],
  ["show in a frame", "GET", -> { "/rooms/#{HQ}/messages/#{created("m05-hq-hello")}" },
    signed_in(KEVIN, "Accept" => TURBO_FRAME, "Turbo-Frame" => "edit_message_m05-hq-hello")],
  ["show an attachment", "GET", "/rooms/#{DESIGNERS}/messages/933434498", signed_in(KEVIN, "Accept" => TURBO_FRAME, "Turbo-Frame" => "x")],
  ["show, JSON", "GET", -> { "/rooms/#{HQ}/messages/#{created("m05-hq-hello")}.json" }, signed_in(KEVIN)],
  ["show a message from another room", "GET", -> { "/rooms/#{HQ}/messages/#{created("m05-archive-mentions")}" }, signed_in(KEVIN)],
  ["show a missing message", "GET", "/rooms/#{HQ}/messages/1", signed_in(KEVIN)],
  ["show in a room the member isn't in", "GET", "/rooms/#{ALL_PETS}/messages/1", signed_in(KEVIN)],

  # edit
  ["edit own message", "GET", -> { "/rooms/#{HQ}/messages/#{created("m05-hq-hello")}/edit" },
    signed_in(KEVIN, "Accept" => TURBO_FRAME, "Turbo-Frame" => "edit_message_m05-hq-hello")],
  ["edit own message, full page", "GET", -> { "/rooms/#{HQ}/messages/#{created("m05-hq-hello")}/edit" }, signed_in(KEVIN, "Accept" => TURBO_FRAME)],
  ["edit a message with mentions", "GET", -> { "/rooms/#{ARCHIVE}/messages/#{created("m05-archive-mentions")}/edit" },
    signed_in(DAVID, "Accept" => TURBO_FRAME, "Turbo-Frame" => "edit_message_m05-archive-mentions")],
  ["edit a seed message with a mention", "GET", "/rooms/#{DESIGNERS}/messages/933434490/edit", signed_in(DAVID, "Accept" => TURBO_FRAME, "Turbo-Frame" => "x")],
  ["edit a seed message with an embed", "GET", "/rooms/#{DESIGNERS}/messages/933434494/edit", signed_in(DAVID, "Accept" => TURBO_FRAME, "Turbo-Frame" => "x")],
  ["edit an attachment", "GET", "/rooms/#{DESIGNERS}/messages/933434498/edit", signed_in(KEVIN, "Accept" => TURBO_FRAME, "Turbo-Frame" => "x")],
  ["edit a blank message", "GET", -> { "/rooms/#{HQ}/messages/#{created("m05-hq-blank")}/edit" }, signed_in(KEVIN, "Accept" => TURBO_FRAME, "Turbo-Frame" => "x")],
  ["administrator edits a member's message", "GET", -> { "/rooms/#{HQ}/messages/#{created("m05-hq-hello")}/edit" },
    signed_in(DAVID, "Accept" => TURBO_FRAME, "Turbo-Frame" => "x")],
  ["member edits someone else's message", "GET", -> { "/rooms/#{ARCHIVE}/messages/#{created("m05-archive-mentions")}/edit" },
    signed_in(KEVIN, "Accept" => TURBO_FRAME, "Turbo-Frame" => "x")],

  # update
  ["update from the editor", "POST", -> { "/rooms/#{HQ}/messages/#{created("m05-hq-hello")}" },
    writer(KEVIN, TURBO_STREAM, "Turbo-Frame" => "edit_message_m05-hq-hello"),
    form("_method" => "patch", "message[body]" => "<p>Hello <em>edited</em> #{mention(DEPLOY_BOT)}</p>")],
  ["update with the same body", "PATCH", -> { "/rooms/#{HQ}/messages/#{created("m05-hq-hello")}" }, writer(KEVIN, "text/html"),
    -> { form("message[body]" => Message.find_by!(client_message_id: "m05-hq-hello").body.body.to_html) }],
  ["update a message without a body", "PATCH", -> { "/rooms/#{HQ}/messages/#{created("m05-hq-nobody")}" }, writer(KEVIN, "text/html"),
    form("message[body]" => "<p>Now with a body</p>")],
  ["update, JSON", "PATCH", -> { "/rooms/#{HQ}/messages/#{created("m05-hq-sound")}" }, writer(KEVIN, "application/json"),
    form("message[body]" => "<p>/play bell</p>")],
  ["update the client message id", "PATCH", -> { "/rooms/#{HQ}/messages/#{created("m05-hq-emoji")}" }, writer(KEVIN, "text/html"),
    form("message[client_message_id]" => "m05-hq-emoji-renamed")],
  ["administrator updates a member's message", "PUT", -> { "/rooms/#{HQ}/messages/#{created("m05-hq-xhr")}" }, writer(DAVID, TURBO_STREAM),
    form("message[body]" => "<p>Moderated</p>")],
  ["member updates someone else's message", "PATCH", -> { "/rooms/#{ARCHIVE}/messages/#{created("m05-archive-mentions")}" }, writer(KEVIN, TURBO_STREAM),
    form("message[body]" => "<p>Hijacked</p>")],
  ["update without message params", "PATCH", -> { "/rooms/#{HQ}/messages/#{created("m05-hq-html")}" }, writer(KEVIN, TURBO_STREAM), form("x" => "y")],

  # destroy
  ["destroy from the editor", "POST", -> { "/rooms/#{KEVIN_BENDER}/messages/#{created("m05-direct-bender")}" },
    writer(KEVIN, TURBO_STREAM, "Turbo-Frame" => "edit_message_m05-direct-bender"), form("_method" => "delete")],
  ["destroy, HTML", "DELETE", -> { "/rooms/#{HQ}/messages/#{created("m05-hq-json")}" }, writer(KEVIN, "text/html")],
  ["member destroys someone else's message", "DELETE", -> { "/rooms/#{ARCHIVE}/messages/#{created("m05-archive-mentions")}" }, writer(KEVIN, TURBO_STREAM)],
  ["administrator destroys a member's message", "DELETE", -> { "/rooms/#{HQ}/messages/#{created("m05-hq-multipart")}" }, writer(DAVID, TURBO_STREAM)],
  ["destroy a message with boosts", "DELETE", "/rooms/#{DESIGNERS}/messages/933434490", writer(DAVID, TURBO_STREAM)],
  ["destroy a missing message", "DELETE", "/rooms/#{HQ}/messages/1", writer(KEVIN, TURBO_STREAM)]
]

# Every row of the tables a message write touches, keyed by id.
TABLES = %w[ messages action_text_rich_texts rooms memberships boosts sessions active_storage_attachments ]

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

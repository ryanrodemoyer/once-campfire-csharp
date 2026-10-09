# What the reference does when administrators manage chat bots through
# Accounts::BotsController and Accounts::Bots::KeysController: each response, the
# Action Cable broadcasts it made, and every row it changed. Writes bots.json, which
# AccountBotsReplayTests replays through the C# router and controllers on the same database.
#
# The database is D02's tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 (the default parity
# seed). Run this script in the reference app on a copy of it, with the clock frozen:
#
#   mkdir -p tmp/a05/db tmp/a05/storage
#   cp tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 tmp/a05/db/production.sqlite3
#   parity/bin/reference runner --storage tmp/a05 --time 2026-03-02T16:00:00Z --freeze \
#     tests/Campfire.Web.Tests/Controllers/AccountBots/Vectors/generate.rb
#
require "json"
require "action_controller/test_case"

HOST = "campfire.test"
NOW = Time.now.utc
WORK = ENV.fetch("PARITY_WORK")
OUTPUT = ARGV.fetch(0, "tests/Campfire.Web.Tests/Controllers/AccountBots/Vectors/bots.json")

def session_row(id, user_id, token, ip_address)
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (#{id}, #{user_id}, '#{token}', #{ip_address ? "'#{ip_address}'" : "NULL"}, 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')"
end

DAVID_ID = 127326141 # administrator
KEVIN_ID = 712064548 # member
BENDER_ID = 394959859 # active bot
OLD_BOT_ID = 773523957 # deactivated bot

FIXTURES = [
  session_row(900001, DAVID_ID, "DavidSessionToken0000001", "198.51.100.7"),
  session_row(900002, KEVIN_ID, "KevinSessionToken0000002", "198.51.100.8")
]
FIXTURES.each { |sql| ActiveRecord::Base.connection.execute(sql) }

DAVID = "DavidSessionToken0000001"
KEVIN = "KevinSessionToken0000002"
CSRF = "a05BotsCsrfToken0000a05BotsCsrfToken0000a0A"
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
  # Index
  ["bots index", "GET", "/account/bots", david],
  ["bots index, signed out", "GET", "/account/bots", browser],
  ["bots index, member", "GET", "/account/bots", kevin],

  # New
  ["bots new", "GET", "/account/bots/new", david],
  ["bots new, member", "GET", "/account/bots/new", kevin],

  # Create bot without webhook
  ["bots create without webhook", "POST", "/account/bots", david, submit("user[name]" => "Calculon")],

  # Create bot with webhook
  ["bots create with webhook", "POST", "/account/bots", david, submit("user[name]" => "Robot Devil", "user[webhook_url]" => "https://example.com/devil")],

  # Create bot as member (forbidden)
  ["bots create, member", "POST", "/account/bots", kevin, submit("user[name]" => "Flexo")],

  # Show is not an action -> 404
  ["bots show", "GET", "/account/bots/#{BENDER_ID}", david],

  # Edit
  ["bots edit Bender", "GET", "/account/bots/#{BENDER_ID}/edit", david],
  ["bots edit, member", "GET", "/account/bots/#{BENDER_ID}/edit", kevin],
  ["bots edit, missing bot", "GET", "/account/bots/999999/edit", david],
  ["bots edit, deactivated bot", "GET", "/account/bots/#{OLD_BOT_ID}/edit", david],

  # Update bot name
  ["bots update name", "PUT", "/account/bots/#{BENDER_ID}", david, submit("user[name]" => "Bender Bending Rodriguez")],

  # Update bot webhook
  ["bots update webhook", "PATCH", "/account/bots/#{BENDER_ID}", david, submit("user[name]" => "Bender Bending Rodriguez", "user[webhook_url]" => "https://example.com/bender-hook")],

  # Remove webhook (empty webhook_url)
  ["bots remove webhook", "PUT", "/account/bots/#{BENDER_ID}", david, submit("user[name]" => "Bender Bending Rodriguez", "user[webhook_url]" => "")],

  # Update as member (forbidden)
  ["bots update, member", "PUT", "/account/bots/#{BENDER_ID}", kevin, submit("user[name]" => "Evil Bender")],

  # Regenerate key
  ["bots regenerate key", "PUT", "/account/bots/#{BENDER_ID}/key", david, submit({})],
  ["bots regenerate key, member", "PUT", "/account/bots/#{BENDER_ID}/key", kevin, submit({})],
  ["bots regenerate key, deactivated bot", "PUT", "/account/bots/#{OLD_BOT_ID}/key", david, submit({})],

  # Destroy / deactivate
  ["bots destroy, member", "DELETE", "/account/bots/#{BENDER_ID}", kevin, submit({})],
  ["bots destroy", "DELETE", "/account/bots/#{BENDER_ID}", david, submit({})],
  ["bots destroy already deactivated", "DELETE", "/account/bots/#{BENDER_ID}", david, submit({})],

  # Index after deactivating Bender
  ["bots index after destroy", "GET", "/account/bots", david]
]

TABLES = %w[ users webhooks memberships sessions ]

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
ActiveSupport::Notifications.subscribe("enqueue.active_job") do |*, payload|
  job = payload[:job]
  events << { "job" => job.class.name, "arguments" => job.arguments.map { |argument| argument.respond_to?(:to_global_id) ? argument.to_global_id.to_s : argument } }
end

RESPONSE_HEADERS = %w[ content-type location vary cache-control set-cookie x-frame-options etag ]

results = CASES.map do |name, method, path, headers, body, remote_addr, sql|
  ActiveRecord::Base.connection.execute(sql) if sql
  content_type = body ? "application/x-www-form-urlencoded" : nil
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

  response_headers = response_headers.to_h.transform_keys(&:downcase)
  {
    "name" => name,
    "sql" => sql,
    "request" => { "method" => method, "path" => path, "headers" => headers, "content_type" => content_type, "body" => body, "remote_addr" => env["REMOTE_ADDR"] },
    "response" => { "status" => status, "headers" => response_headers.slice(*RESPONSE_HEADERS), "body" => text.force_encoding("UTF-8") },
    "events" => events.dup,
    "changes" => changes(before, after)
  }.compact
end

File.write(File.join(WORK, OUTPUT), JSON.pretty_generate("now" => NOW.iso8601, "fixtures" => FIXTURES, "cases" => results) + "\n")

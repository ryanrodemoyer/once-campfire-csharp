# What the reference does when people page through the account's users, and when an administrator
# changes someone's role, deactivates them, bans or unbans them, through Accounts::UsersController
# and Users::BansController: each response, the Action Cable broadcasts and jobs it made, and every
# row it changed. Writes users.json, which AccountUsersReplayTests replays through the C# router and
# controllers on the same database.
#
# The database is D02's tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 (the default parity
# seed). Run this script in the reference app on a copy of it, with the clock frozen:
#
#   mkdir -p tmp/a04/db tmp/a04/storage
#   cp tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 tmp/a04/db/production.sqlite3
#   parity/bin/reference runner --storage tmp/a04 --time 2026-03-02T16:00:00Z --freeze \
#     tests/Campfire.Web.Tests/Controllers/AccountUsers/Vectors/generate.rb
#
# It writes the vectors itself (an optional argument names the file, relative to the repository),
# since the production logger shares stdout with the runner.
#
# Requests go straight to Rails.application (the stack Puma runs), in order, on one database, with
# CSRF protection on. The fixtures below are applied first, and the C# test applies the same SQL to
# its copy. A case may run SQL of its own first ("sql"), which the C# test runs too.
require "json"
require "action_controller/test_case"

HOST = "campfire.test"
NOW = Time.now.utc
WORK = ENV.fetch("PARITY_WORK")
OUTPUT = ARGV.fetch(0, "tests/Campfire.Web.Tests/Controllers/AccountUsers/Vectors/users.json")

def session_row(id, user_id, token, ip_address)
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (#{id}, #{user_id}, '#{token}', #{ip_address ? "'#{ip_address}'" : "NULL"}, 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')"
end

DAVID_ID = 127326141 # administrator
JASON_ID = 149087659 # administrator
BENDER_ID = 394959859 # an active bot
KEVIN_ID = 712064548 # member, banned and unbanned below
JZ_ID = 773523953 # member, deactivated below
RITA_ID = 773523954 # deactivated
MALLORY_ID = 773523955 # banned (from 203.0.113.9)
LOU_ID = 773523958 # member

FIXTURES = [
  # Everyone signed in is active just now, so no request refreshes a session. Kevin has two
  # sessions from public addresses and one with none; JZ one from a private address.
  session_row(900001, DAVID_ID, "DavidSessionToken0000001", "198.51.100.7"),
  session_row(900002, JASON_ID, "JasonSessionToken0000002", "198.51.100.8"),
  session_row(900003, LOU_ID, "LouSessionToken000000003", "198.51.100.9"),
  session_row(900004, KEVIN_ID, "KevinSessionToken0000004", "203.0.113.1"),
  session_row(900005, KEVIN_ID, "KevinSessionToken0000005", "203.0.113.2"),
  session_row(900006, KEVIN_ID, "KevinSessionToken0000006", "203.0.113.1"),
  session_row(900007, KEVIN_ID, "KevinSessionToken0000007", nil),
  session_row(900008, JZ_ID, "JzSessionToken0000000008", "10.0.0.5"),
  # A search and a push subscription of JZ's, which deactivating removes.
  "INSERT INTO searches (id, user_id, query, created_at, updated_at) VALUES (900001, #{JZ_ID}, 'cats', '2026-03-01 09:00:00', '2026-03-01 09:00:00')",
  "INSERT INTO push_subscriptions (id, user_id, endpoint, p256dh_key, auth_key, user_agent, created_at, updated_at) " \
    "VALUES (900001, #{JZ_ID}, 'https://push.example.com/jz', 'p256dh', 'auth', 'curl/8.0', '2026-03-01 09:00:00', '2026-03-01 09:00:00')"
]
FIXTURES.each { |sql| ActiveRecord::Base.connection.execute(sql) }

DAVID = "DavidSessionToken0000001"
JASON = "JasonSessionToken0000002"
LOU = "LouSessionToken000000003"
CSRF = "a04UsersCsrfToken000a04UsersCsrfToken000a0A" # the session's _csrf_token (urlsafe base64, 32 bytes)
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

def jason(extra = {}) = signed_in(JASON, extra)

def lou(extra = {}) = signed_in(LOU, extra)

# The lazy next-page frame's request: `src` carries format=turbo_stream.
def frame(who) = who.merge("Turbo-Frame" => "next_page_container")

# A form Turbo submits: the page's token in the body.
def submit(params) = URI.encode_www_form({ "authenticity_token" => TOKEN }.merge(params))

def account_user(id) = "/account/users/#{id}"

def ban(id) = "/users/#{id}/ban"

# Five hundred more people, so the users span two pages of 500.
MANY_PEOPLE = "INSERT INTO users (name, email_address, role, status, created_at, updated_at) " \
  "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 500) " \
  "SELECT printf('Person %03d', i), printf('person%03d@37signals.com', i), 0, 0, '2026-03-01 09:00:00', '2026-03-01 09:00:00' FROM n"

# [name, method, path, headers, body, remote address, sql]
CASES = [
  # accounts/users#index: geared pagination of `User.active.ordered.without_bots`, 500 a page,
  # as a turbo stream.
  ["users", "GET", "/account/users?format=turbo_stream", frame(david)],
  ["users, turbo stream accepted", "GET", "/account/users", david("Accept" => TURBO)],
  ["users, .turbo_stream", "GET", "/account/users.turbo_stream", david],
  ["users, page 2", "GET", "/account/users?page=2&format=turbo_stream", frame(david)],
  ["users, page 0", "GET", "/account/users?page=0&format=turbo_stream", frame(david)],
  ["users, page abc", "GET", "/account/users?page=abc&format=turbo_stream", frame(david)],
  ["users, page 2abc", "GET", "/account/users?page=2abc&format=turbo_stream", frame(david)],
  ["users, page array", "GET", "/account/users?page[]=2&format=turbo_stream", frame(david)],
  ["users, member", "GET", "/account/users?format=turbo_stream", frame(lou)],
  ["users, HTML", "GET", "/account/users", david],
  ["users, JSON", "GET", "/account/users", david("Accept" => "application/json")],
  ["users, .json", "GET", "/account/users.json", david],
  ["users, any", "GET", "/account/users", david("Accept" => "*/*")],
  ["users, JSON first", "GET", "/account/users", david("Accept" => "application/json, text/vnd.turbo-stream.html")],
  ["users, JSON first, page 2", "GET", "/account/users?z=last&page=2&a=b+c%20d&flag&=empty&e%5B%5D=%E2%9C%93",
    david("Accept" => "application/json, text/vnd.turbo-stream.html")],
  ["users, signed out", "GET", "/account/users?format=turbo_stream", browser],

  # The resource routes Accounts::UsersController has no action for.
  ["account user show", "GET", account_user(KEVIN_ID), david],
  ["account user new", "GET", "/account/users/new", david],
  ["account user edit", "GET", "#{account_user(KEVIN_ID)}/edit", david],
  ["account user create", "POST", "/account/users", david("Accept" => TURBO), submit("user[name]" => "x")],

  # accounts/users#update: the role.
  ["make an administrator, member", "PATCH", account_user(KEVIN_ID), lou("Accept" => TURBO), submit("user[role]" => "administrator")],
  ["make an administrator", "PATCH", account_user(KEVIN_ID), david("Accept" => TURBO), submit("user[role]" => "administrator")],
  ["make an administrator again", "PUT", account_user(KEVIN_ID), david("Accept" => TURBO), submit("user[role]" => "administrator")],
  ["make a member, check box", "PATCH", account_user(KEVIN_ID), david("Accept" => TURBO), submit("user[role]" => "member")],
  ["check box checked", "PATCH", account_user(KEVIN_ID), david("Accept" => TURBO),
    URI.encode_www_form([ [ "authenticity_token", TOKEN ], [ "user[role]", "member" ], [ "user[role]", "administrator" ] ])],
  ["make a member", "PATCH", account_user(KEVIN_ID), david("Accept" => TURBO), submit("user[role]" => "member")],
  ["role bot", "PATCH", account_user(LOU_ID), david("Accept" => TURBO), submit("user[role]" => "bot")],
  ["role blank", "PATCH", account_user(KEVIN_ID), david("Accept" => TURBO), submit("user[role]" => "")],
  ["role array", "PATCH", account_user(KEVIN_ID), david("Accept" => TURBO), submit("user[role][]" => "administrator")],
  ["no role", "PATCH", account_user(KEVIN_ID), david("Accept" => TURBO), submit("user[name]" => "Kev")],
  ["no user", "PATCH", account_user(KEVIN_ID), david("Accept" => TURBO), submit("role" => "administrator")],
  ["user is a string", "PATCH", account_user(KEVIN_ID), david("Accept" => TURBO), submit("user" => "administrator")],
  ["role of a bot", "PATCH", account_user(BENDER_ID), david("Accept" => TURBO), submit("user[role]" => "administrator")],
  ["role of a deactivated person", "PATCH", account_user(RITA_ID), david("Accept" => TURBO), submit("user[role]" => "administrator")],
  ["role of a banned person", "PATCH", account_user(MALLORY_ID), david("Accept" => TURBO), submit("user[role]" => "administrator")],
  ["role of a missing person", "PATCH", account_user(1), david("Accept" => TURBO), submit("user[role]" => "administrator")],
  ["role through user_id", "PATCH", "#{account_user(KEVIN_ID)}?user_id=#{JZ_ID}", david("Accept" => TURBO), submit("user[role]" => "administrator")],
  ["role, no CSRF token", "PATCH", account_user(KEVIN_ID), david("Accept" => TURBO), URI.encode_www_form("user[role]" => "administrator")],
  ["role, signed out", "PATCH", account_user(KEVIN_ID), browser("Accept" => TURBO), submit("user[role]" => "administrator")],

  # users/bans#create and #destroy
  ["ban, member", "POST", ban(KEVIN_ID), lou("Accept" => TURBO), submit({})],
  ["ban from a private address", "POST", ban(JZ_ID), david("Accept" => TURBO), submit({})],
  ["ban", "POST", ban(KEVIN_ID), david("Accept" => TURBO), submit({})],
  ["users after a ban", "GET", "/account/users?format=turbo_stream", frame(david)],
  ["a write from a banned address", "POST", ban(KEVIN_ID), david("Accept" => TURBO), submit({}), "203.0.113.2"],
  ["a read from a banned address", "GET", "/account/users?format=turbo_stream", frame(david), nil, "203.0.113.2"],
  ["ban again", "POST", ban(KEVIN_ID), david("Accept" => TURBO), submit({})],
  ["unban, member", "DELETE", ban(KEVIN_ID), lou("Accept" => TURBO), submit({})],
  ["unban", "DELETE", ban(KEVIN_ID), david("Accept" => TURBO), submit({})],
  ["unban again", "DELETE", ban(KEVIN_ID), david("Accept" => TURBO), submit({})],
  ["unban someone banned before", "DELETE", ban(MALLORY_ID), david("Accept" => TURBO), submit({})],
  ["ban a deactivated person", "POST", ban(RITA_ID), david("Accept" => TURBO), submit({})],
  ["ban a bot", "POST", ban(BENDER_ID), david("Accept" => TURBO), submit({})],
  ["ban a missing person", "POST", ban(1), david("Accept" => TURBO), submit({})],
  ["ban, JSON", "POST", ban(RITA_ID), david("Accept" => "application/json"), submit({})],
  ["unban, JSON", "DELETE", ban(RITA_ID), david("Accept" => "application/json"), submit({})],
  ["ban, no CSRF token", "POST", ban(KEVIN_ID), david("Accept" => TURBO), ""],
  ["ban, signed out", "POST", ban(KEVIN_ID), browser("Accept" => TURBO), submit({})],

  # accounts/users#destroy: deactivating.
  ["deactivate, member", "DELETE", account_user(JZ_ID), lou("Accept" => TURBO), submit({})],
  ["deactivate", "DELETE", account_user(JZ_ID), david("Accept" => TURBO), submit({})],
  ["deactivate again", "DELETE", account_user(JZ_ID), david("Accept" => TURBO), submit({})],
  ["deactivate an unbanned person", "DELETE", account_user(RITA_ID), david("Accept" => TURBO), submit({})],
  ["deactivate a banned bot", "DELETE", account_user(BENDER_ID), david("Accept" => TURBO), submit({})],
  ["deactivate through user_id", "DELETE", "#{account_user(KEVIN_ID)}?user_id=#{LOU_ID}", david("Accept" => TURBO), submit({})],
  ["users after deactivating", "GET", "/account/users?format=turbo_stream", frame(david)],
  ["deactivate yourself", "DELETE", account_user(JASON_ID), jason("Accept" => TURBO), submit({})],
  ["after deactivating yourself", "GET", "/account/users?format=turbo_stream", frame(jason)],

  # Two pages of people.
  ["users, two pages", "GET", "/account/users?format=turbo_stream&page=2", frame(david), nil, nil, MANY_PEOPLE],
  ["users, two pages, JSON first", "GET", "/account/users?page=2", david("Accept" => "application/json, text/vnd.turbo-stream.html")],
  ["users, past the last page", "GET", "/account/users?format=turbo_stream&page=3", frame(david)]
]

TABLES = %w[ users sessions bans memberships searches push_subscriptions ]

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

RESPONSE_HEADERS = %w[ content-type location vary cache-control set-cookie x-frame-options etag link x-total-count ]

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

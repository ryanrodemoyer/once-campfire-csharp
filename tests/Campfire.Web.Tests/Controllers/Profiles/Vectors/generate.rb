# What the reference does when people follow a sign-in (transfer) link, and open and change their
# own profile, through Sessions::TransfersController and Users::ProfilesController: each response,
# the Action Cable broadcasts it made, and every row it changed. Also the transfer ids the
# reference mints at the frozen time, which the C# test mints too. Writes profiles.json, which
# ProfilesReplayTests replays through the C# router and controllers on the same database.
#
# The database is D02's tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 (the default parity
# seed). Run this script in the reference app on a copy of it, with the clock frozen:
#
#   mkdir -p tmp/a02/db tmp/a02/storage
#   cp tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 tmp/a02/db/production.sqlite3
#   parity/bin/reference runner --storage tmp/a02 --time 2026-03-02T16:00:00Z --freeze \
#     tests/Campfire.Web.Tests/Controllers/Profiles/Vectors/generate.rb
#
# It writes the vectors itself (an optional argument names the file, relative to the repository),
# since the production logger shares stdout with the runner.
#
# Requests go straight to Rails.application (the stack Puma runs), in order, on one database, with
# CSRF protection on. The fixtures below are applied first, and the C# test applies the same SQL to
# its copy. Uploads are multipart bodies built from the parts each case lists (fixture files from
# reference/test/fixtures/files); the C# test builds the same bytes.
require "json"
require "action_controller/test_case"

HOST = "campfire.test"
NOW = Time.now.utc
WORK = ENV.fetch("PARITY_WORK")
OUTPUT = ARGV.fetch(0, "tests/Campfire.Web.Tests/Controllers/Profiles/Vectors/profiles.json")

DAVID_ID = 127326141 # administrator
JASON_ID = 149087659 # administrator with an avatar
BENDER_ID = 394959859 # an active bot
KEVIN_ID = 712064548 # member
RITA_ID = 773523954 # deactivated
MALLORY_ID = 773523955 # banned
LOU_ID = 773523958 # member in no rooms

def session_row(id, user_id, token)
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (#{id}, #{user_id}, '#{token}', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')"
end

FIXTURES = [
  # Everyone signed in is active just now, so no request refreshes a session.
  session_row(900001, DAVID_ID, "DavidSessionToken0000001"),
  session_row(900002, KEVIN_ID, "KevinSessionToken0000002"),
  session_row(900003, JASON_ID, "JasonSessionToken0000003"),
  session_row(900004, LOU_ID, "LouSessionToken000000004")
]
FIXTURES.each { |sql| ActiveRecord::Base.connection.execute(sql) }

DAVID = "DavidSessionToken0000001"
KEVIN = "KevinSessionToken0000002"
JASON = "JasonSessionToken0000003"
LOU = "LouSessionToken000000004"
CSRF = "a02ProfileCsrfToken00a02ProfileCsrfToken00A" # the session's _csrf_token (urlsafe base64, 32 bytes)
SESSION_ID = "0123456789abcdef0123456789abcdef"
MODERN = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
SAFARI = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15"
PAGE = "text/html, application/xhtml+xml"
TURBO = "text/vnd.turbo-stream.html, text/html, application/xhtml+xml"
BOUNDARY = "----a02FormBoundary7MA4YWxkTrZu0gW"
FILES = "reference/test/fixtures/files"

# The transfer ids the reference mints now (`user.transfer_id`), which the C# test mints too.
TRANSFER_IDS = [ DAVID_ID, KEVIN_ID, JASON_ID, LOU_ID, BENDER_ID ].to_h { |id| [ id.to_s, User.find(id).transfer_id ] }

def transfer_id(id) = TRANSFER_IDS.fetch(id.to_s)

# A transfer id expiring at another time, or for a user who isn't there (a persisted stand-in).
def signed_transfer_id(id, expires_at)
  User.new(id: id).tap { |user| user.instance_variable_set(:@new_record, false) }.signed_id(purpose: :transfer, expires_at: expires_at)
end

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
RETURNING_SESSION = rails_session("session_id" => SESSION_ID, "_csrf_token" => CSRF,
  "return_to_after_authenticating" => "http://#{HOST}/rooms/201306877")
TOKEN = global_token

def browser(extra = {}) = { "Cookie" => CSRF_SESSION, "User-Agent" => MODERN, "Accept" => PAGE }.merge(extra)

def signed_in(token, extra = {}) = browser("Cookie" => "#{session_token(token)}; #{CSRF_SESSION}").merge(extra)

def david(extra = {}) = signed_in(DAVID, extra)

def kevin(extra = {}) = signed_in(KEVIN, extra)

def jason(extra = {}) = signed_in(JASON, extra)

def lou(extra = {}) = signed_in(LOU, extra)

# A form Turbo submits: the page's token in the body.
def submit(params) = URI.encode_www_form({ "authenticity_token" => TOKEN }.merge(params))

def profile_with(params) = submit(params.transform_keys { |key| "user[#{key}]" })

# A multipart form: its token, then the fields and files in order.
def upload(parts) = { "boundary" => BOUNDARY, "parts" => [ { "name" => "authenticity_token", "value" => TOKEN } ] + parts }

def file_part(name, filename, content_type) = { "name" => name, "filename" => filename, "content_type" => content_type, "file" => "#{FILES}/#{filename}" }

def multipart_body(form)
  body = form["parts"].map do |part|
    if part["file"]
      "--#{form["boundary"]}\r\nContent-Disposition: form-data; name=\"#{part["name"]}\"; filename=\"#{part["filename"]}\"\r\n" \
        "Content-Type: #{part["content_type"]}\r\n\r\n".b + File.binread(File.join(WORK, part["file"])) + "\r\n".b
    else
      "--#{form["boundary"]}\r\nContent-Disposition: form-data; name=\"#{part["name"]}\"\r\n\r\n#{part["value"]}\r\n".b
    end
  end.join.b
  body + "--#{form["boundary"]}--\r\n".b
end

def transfer(id) = "/session/transfers/#{id}"

PROFILE = "/users/me/profile"

# What the profile form sends when only the name and bio change: the password field is blank.
KEVINS_FORM = { "name" => "Kevin McConnell", "email_address" => "kevin@37signals.com", "password" => "", "bio" => "Ships things." }

# [name, method, path, headers, body (a string or an upload), remote address]
CASES = [
  # sessions/transfers#show: the page that puts the link back to itself.
  ["transfer page", "GET", transfer(transfer_id(KEVIN_ID)), browser],
  ["transfer page, signed in", "GET", transfer(transfer_id(KEVIN_ID)), david],
  ["transfer page, bad id", "GET", transfer("not-a-transfer-id"), browser],
  ["transfer page, JSON", "GET", transfer(transfer_id(KEVIN_ID)), browser("Accept" => "application/json")],
  ["transfer page, .json", "GET", "#{transfer(transfer_id(KEVIN_ID))}.json", browser],

  # sessions/transfers#update: `User.active.find_by_transfer_id`, signed in as them.
  ["transfer, no CSRF token", "PUT", transfer(transfer_id(KEVIN_ID)), browser("Accept" => TURBO), ""],
  ["transfer", "PUT", transfer(transfer_id(KEVIN_ID)), browser("Accept" => TURBO), submit({})],
  ["transfer again", "PUT", transfer(transfer_id(KEVIN_ID)), browser("Accept" => TURBO), submit({})],
  ["transfer, PATCH", "PATCH", transfer(transfer_id(LOU_ID)), browser("Accept" => TURBO), submit({})],
  ["transfer, returning", "PUT", transfer(transfer_id(KEVIN_ID)),
    browser("Cookie" => RETURNING_SESSION, "Accept" => TURBO), submit({})],
  ["transfer, signed in as someone else", "PUT", transfer(transfer_id(KEVIN_ID)), david("Accept" => TURBO), submit({})],
  ["transfer, JSON", "PUT", transfer(transfer_id(KEVIN_ID)), browser("Accept" => "application/json"), submit({})],
  ["transfer, an administrator", "PUT", transfer(transfer_id(JASON_ID)), browser("Accept" => TURBO), submit({})],
  ["transfer, a bot", "PUT", transfer(transfer_id(BENDER_ID)), browser("Accept" => TURBO), submit({})],
  ["transfer, about to expire", "PUT", transfer(signed_transfer_id(KEVIN_ID, NOW + 1.second)), browser("Accept" => TURBO), submit({})],
  ["transfer, expired", "PUT", transfer(signed_transfer_id(KEVIN_ID, NOW - 1.second)), browser("Accept" => TURBO), submit({})],
  ["transfer, tampered", "PUT", transfer(transfer_id(KEVIN_ID).sub(/.\z/) { |c| c == "A" ? "B" : "A" }), browser("Accept" => TURBO), submit({})],
  ["transfer, bad id", "PUT", transfer("not-a-transfer-id"), browser("Accept" => TURBO), submit({})],
  ["transfer, avatar token", "PUT", transfer(User.find(KEVIN_ID).avatar_token), browser("Accept" => TURBO), submit({})],
  ["transfer, deactivated person", "PUT", transfer(signed_transfer_id(RITA_ID, NOW + 4.hours)), browser("Accept" => TURBO), submit({})],
  ["transfer, banned person", "PUT", transfer(signed_transfer_id(MALLORY_ID, NOW + 4.hours)), browser("Accept" => TURBO), submit({})],
  ["transfer, missing person", "PUT", transfer(signed_transfer_id(1, NOW + 4.hours)), browser("Accept" => TURBO), submit({})],

  # users/profiles#show: the signed-in person's own, whatever id the URL names.
  ["profile, administrator", "GET", PROFILE, david],
  ["profile, member", "GET", PROFILE, kevin],
  ["profile, in no rooms", "GET", PROFILE, lou],
  ["profile with an avatar", "GET", PROFILE, jason],
  ["profile through another id", "GET", "/users/#{KEVIN_ID}/profile", david],
  ["profile from another page", "GET", PROFILE, kevin("Referer" => "http://#{HOST}/rooms/201306877")],
  ["profile from itself", "GET", PROFILE, kevin("Referer" => "http://#{HOST}#{PROFILE}")],
  ["profile, Safari", "GET", PROFILE, kevin("User-Agent" => SAFARI)],
  ["profile, JSON", "GET", PROFILE, kevin("Accept" => "application/json")],
  ["profile, signed out", "GET", PROFILE, browser],

  # The resource routes Users::ProfilesController has no action for.
  ["new profile", "GET", "#{PROFILE}/new", kevin],
  ["edit profile", "GET", "#{PROFILE}/edit", kevin],
  ["create profile", "POST", PROFILE, kevin("Accept" => TURBO), profile_with("name" => "x")],
  ["delete profile", "DELETE", PROFILE, kevin("Accept" => TURBO), submit({})],

  # users/profiles#update
  ["update name and bio", "PATCH", PROFILE, kevin("Accept" => TURBO), profile_with(KEVINS_FORM)],
  ["profile after an update", "GET", PROFILE, kevin],
  ["update, nothing changed", "PUT", PROFILE, kevin("Accept" => TURBO), profile_with(KEVINS_FORM)],
  ["update, blank bio", "PATCH", PROFILE, kevin("Accept" => TURBO), profile_with(KEVINS_FORM.merge("bio" => ""))],
  ["update password", "PATCH", PROFILE, kevin("Accept" => TURBO), profile_with(KEVINS_FORM.merge("password" => "new-secret-123"))],
  ["update email address", "PATCH", PROFILE, kevin("Accept" => TURBO), profile_with("email_address" => "kevin@example.com")],
  ["update, address taken", "PATCH", PROFILE, kevin("Accept" => TURBO), profile_with("email_address" => "david@37signals.com")],
  ["update, role ignored", "PATCH", PROFILE, kevin("Accept" => TURBO), profile_with("role" => "administrator", "name" => "Kev")],
  ["update through another id", "PATCH", "/users/#{DAVID_ID}/profile", kevin("Accept" => TURBO), profile_with("name" => "Kevin")],
  ["update, JSON", "PATCH", PROFILE, kevin("Accept" => "application/json"), profile_with("bio" => "JSON")],
  ["update, blank name", "PATCH", PROFILE, lou("Accept" => TURBO), profile_with("name" => "")],
  ["update, no user", "PATCH", PROFILE, kevin("Accept" => TURBO), submit("name" => "x")],
  ["update, user is a string", "PATCH", PROFILE, kevin("Accept" => TURBO), submit("user" => "x")],
  ["update, name is an array", "PATCH", PROFILE, kevin("Accept" => TURBO), submit("user[name][]" => "x")],
  ["update, no CSRF token", "PATCH", PROFILE, kevin("Accept" => TURBO), URI.encode_www_form("user[name]" => "Forged")],
  ["update, signed out", "PATCH", PROFILE, browser("Accept" => TURBO), profile_with("name" => "Nobody")],

  # The avatar: an upload, a blank (removing it), or a blob's signed id.
  ["upload an avatar", "PATCH", PROFILE, kevin("Accept" => TURBO),
    upload([ file_part("user[avatar]", "moon.jpg", "image/jpeg") ])],
  ["profile after an avatar", "GET", PROFILE, kevin],
  ["replace the avatar", "PATCH", PROFILE, kevin("Accept" => TURBO),
    upload([ { "name" => "user[name]", "value" => "Kevin" }, file_part("user[avatar]", "black_hole.jpg", "image/jpeg") ])],
  ["remove the avatar", "PATCH", PROFILE, kevin("Accept" => TURBO), profile_with("avatar" => "")],
  ["remove no avatar", "PATCH", PROFILE, kevin("Accept" => TURBO), profile_with("avatar" => "")],
  ["avatar, bad signed id", "PATCH", PROFILE, kevin("Accept" => TURBO), profile_with("avatar" => "not-a-signed-id", "name" => "Bad")],
  ["avatar, a blob's signed id", "PATCH", PROFILE, kevin("Accept" => TURBO), profile_with("avatar" => ActiveStorage::Blob.find(1).signed_id)],
  ["profile after a blob's signed id", "GET", PROFILE, kevin]
]

TABLES = %w[ users sessions active_storage_blobs active_storage_attachments ]

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
  events << { "job" => job.class.name, "arguments" => job.arguments.map { |argument| argument.respond_to?(:to_global_id) ? argument.to_global_id.to_s : argument.to_s } }
end

RESPONSE_HEADERS = %w[ content-type location vary cache-control set-cookie x-frame-options etag ]

results = CASES.map do |name, method, path, headers, body, remote_addr|
  if body.is_a?(Hash)
    content_type = "multipart/form-data; boundary=#{body["boundary"]}"
    input = multipart_body(body)
  else
    content_type = body ? "application/x-www-form-urlencoded" : nil
    input = body || ""
  end
  env = Rack::MockRequest.env_for("http://#{HOST}#{path}", method: method, input: input)
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
    "request" => { "method" => method, "path" => path, "headers" => headers, "content_type" => content_type,
      "body" => body.is_a?(Hash) ? nil : body, "multipart" => body.is_a?(Hash) ? body : nil, "remote_addr" => env["REMOTE_ADDR"] },
    "response" => { "status" => status, "headers" => response_headers.slice(*RESPONSE_HEADERS), "body" => text.force_encoding("UTF-8") },
    "events" => events.dup,
    "changes" => changes(before, after)
  }
end

File.write(File.join(WORK, OUTPUT), JSON.pretty_generate("now" => NOW.iso8601, "fixtures" => FIXTURES,
  "transfer_ids" => TRANSFER_IDS, "cases" => results) + "\n")

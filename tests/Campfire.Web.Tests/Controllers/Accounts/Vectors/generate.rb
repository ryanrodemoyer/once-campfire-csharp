# What the reference does when people join through the account's link, open a person's page, and
# administer the account (its settings, name, logo, join code and custom styles) through
# UsersController, AccountsController and Accounts::{JoinCodes,Logos,CustomStyles}Controller:
# each response, the Action Cable broadcasts it made, and every row it changed. Writes
# accounts.json, which AccountsControllerTests replays through the C# router and controllers on
# the same database.
#
# The database is D02's tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 (the default parity
# seed, where everyone's password is "secret123456"). Run this script in the reference app on a
# copy of it, with the clock frozen:
#
#   mkdir -p tmp/a03/db tmp/a03/storage
#   cp tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 tmp/a03/db/production.sqlite3
#   parity/bin/reference runner --storage tmp/a03 --time 2026-03-02T16:00:00Z --freeze \
#     tests/Campfire.Web.Tests/Controllers/Accounts/Vectors/generate.rb
#
# It writes the vectors itself (an optional argument names the file, relative to the repository),
# since the production logger shares stdout with the runner.
#
# Requests go straight to Rails.application (the stack Puma runs), in order, on one database, with
# CSRF protection on. Uploads are multipart bodies built from the parts each case lists (fixture
# files from reference/test/fixtures/files); the C# test builds the same bytes. Binary response
# bodies (logos) are recorded as their SHA-256 and size, with the libvips version that made them.
require "json"
require "digest"
require "action_controller/test_case"

HOST = "campfire.test"
NOW = Time.now.utc
WORK = ENV.fetch("PARITY_WORK")
OUTPUT = ARGV.fetch(0, "tests/Campfire.Web.Tests/Controllers/Accounts/Vectors/accounts.json")

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
JOIN_CODE = Account.first.join_code
CSRF = "a03AccountsCsrfToken0a03AccountsCsrfToken0A" # the session's _csrf_token (urlsafe base64, 32 bytes)
SESSION_ID = "0123456789abcdef0123456789abcdef"
MODERN = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
PAGE = "text/html, application/xhtml+xml"
TURBO = "text/vnd.turbo-stream.html, text/html, application/xhtml+xml"
IMAGE = "image/avif,image/webp,image/png,image/svg+xml,image/*;q=0.8,*/*;q=0.5"
PASSWORD = "secret123456"
BOUNDARY = "----a03FormBoundary7MA4YWxkTrZu0gW"
FILES = "reference/test/fixtures/files"

DAVID_ID = 127326141
JASON_ID = 149087659
KEVIN_ID = 712064548
RITA_ID = 773523954 # deactivated
MALLORY_ID = 773523955 # banned
BENDER_ID = 394959859 # an active bot
OLD_BOT_ID = 773523957 # a deactivated bot

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

# A form Turbo submits: the page's token in the body.
def submit(params) = URI.encode_www_form({ "authenticity_token" => TOKEN }.merge(params))

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

def join_with(params) = submit(params.transform_keys { |key| "user[#{key}]" })

USER_PATH = ->(id) { "/users/#{id}" }

# [name, method, path, headers, body (a string or an upload), remote address]
CASES = [
  # users#new and #create: the join link.
  ["join page", "GET", "/join/#{JOIN_CODE}", browser],
  ["join page, JSON", "GET", "/join/#{JOIN_CODE}", browser("Accept" => "application/json")],
  ["join page, wrong code", "GET", "/join/not-the-code", browser],
  ["join page, signed in", "GET", "/join/#{JOIN_CODE}", kevin],
  ["join", "POST", "/join/#{JOIN_CODE}", browser("Accept" => TURBO),
    join_with("name" => "New Person", "email_address" => "new@37signals.com", "password" => PASSWORD, "role" => "administrator")],
  ["join, address taken", "POST", "/join/#{JOIN_CODE}", browser("Accept" => TURBO),
    join_with("name" => "Another David", "email_address" => "david@37signals.com", "password" => PASSWORD)],
  ["join, wrong code", "POST", "/join/not-the-code", browser("Accept" => TURBO),
    join_with("name" => "Nobody", "email_address" => "nobody@37signals.com", "password" => PASSWORD)],
  ["join, no user", "POST", "/join/#{JOIN_CODE}", browser("Accept" => TURBO), submit("name" => "x")],
  ["join, blank password", "POST", "/join/#{JOIN_CODE}", browser("Accept" => TURBO),
    join_with("name" => "No Password", "email_address" => "nopassword@37signals.com", "password" => "")],
  ["join, no CSRF token", "POST", "/join/#{JOIN_CODE}", browser("Accept" => TURBO),
    URI.encode_www_form("user[name]" => "Forged", "user[email_address]" => "forged@37signals.com", "user[password]" => PASSWORD)],
  ["join with an avatar", "POST", "/join/#{JOIN_CODE}", browser("Accept" => TURBO),
    upload([ { "name" => "user[name]", "value" => "Ava Tar" }, { "name" => "user[email_address]", "value" => "ava@37signals.com" },
      { "name" => "user[password]", "value" => PASSWORD }, file_part("user[avatar]", "moon.jpg", "image/jpeg") ])],

  # users#show
  ["own page, administrator", "GET", USER_PATH.(DAVID_ID), david],
  ["member's page, administrator", "GET", USER_PATH.(KEVIN_ID), david],
  ["administrator's page, administrator", "GET", USER_PATH.(JASON_ID), david],
  ["own page, member", "GET", USER_PATH.(KEVIN_ID), kevin],
  ["administrator's page, member", "GET", USER_PATH.(DAVID_ID), kevin],
  ["deactivated person's page", "GET", USER_PATH.(RITA_ID), david],
  ["banned person's page, administrator", "GET", USER_PATH.(MALLORY_ID), david],
  ["banned person's page, member", "GET", USER_PATH.(MALLORY_ID), kevin],
  ["bot's page", "GET", USER_PATH.(BENDER_ID), kevin],
  ["deactivated bot's page", "GET", USER_PATH.(OLD_BOT_ID), david],
  ["a page from another page", "GET", USER_PATH.(KEVIN_ID), kevin("Referer" => "http://#{HOST}/rooms/201306877")],
  ["missing person's page", "GET", "/users/1", david],
  ["person's page, JSON", "GET", USER_PATH.(KEVIN_ID), david("Accept" => "application/json")],
  ["person's page, signed out", "GET", USER_PATH.(KEVIN_ID), browser],

  # accounts#edit and #update
  ["settings, administrator", "GET", "/account/edit", david],
  ["settings, member", "GET", "/account/edit", kevin],
  ["settings, last room visited", "GET", "/account/edit", david("Cookie" => "#{session_token(DAVID)}; #{CSRF_SESSION}; last_room=699448327")],
  ["settings, page 2", "GET", "/account/edit?page=2", david],
  ["settings, JSON", "GET", "/account/edit", david("Accept" => "application/json")],
  ["settings, signed out", "GET", "/account/edit", browser],
  ["account show", "GET", "/account", david],
  ["new account", "GET", "/account/new", david],
  ["rename, member", "PUT", "/account", kevin("Accept" => TURBO), submit("account[name]" => "Different")],
  ["rename", "PATCH", "/account", david("Accept" => TURBO), submit("account[name]" => "Different")],
  ["rename to the same name", "PATCH", "/account", david("Accept" => TURBO), submit("account[name]" => "Different")],
  ["update, no account", "PATCH", "/account", david("Accept" => TURBO), submit("name" => "x")],
  ["restrict room creation", "PUT", "/account", david("Accept" => TURBO),
    submit("account[settings][restrict_room_creation_to_administrators]" => "true")],
  ["settings, restricted", "GET", "/account/edit", david],
  ["allow room creation", "PUT", "/account", david("Accept" => TURBO),
    submit("account[settings][restrict_room_creation_to_administrators]" => "false")],

  # accounts/logos
  ["stock logo", "GET", "/account/logo", browser("Accept" => IMAGE)],
  ["stock logo, small", "GET", "/account/logo?size=small", browser("Accept" => IMAGE)],
  ["stock logo, cached", "GET", "/account/logo", browser("Accept" => IMAGE, "If-None-Match" => :etag_of_stock_logo)],
  ["upload a logo", "PATCH", "/account", david("Accept" => TURBO), upload([ file_part("account[logo]", "black_hole.jpg", "image/jpeg") ])],
  ["logo", "GET", "/account/logo?v=20260302160000", david("Accept" => IMAGE)],
  ["logo, small", "GET", "/account/logo?size=small", browser("Accept" => IMAGE)],
  ["logo, again", "GET", "/account/logo", browser("Accept" => IMAGE)],
  ["settings with a logo", "GET", "/account/edit", david],
  ["settings with a logo, member", "GET", "/account/edit", kevin],
  ["sign-in page with a logo", "GET", "/session/new", browser],
  ["delete the logo, member", "DELETE", "/account/logo", kevin("Accept" => TURBO), submit({})],
  ["delete the logo", "DELETE", "/account/logo", david("Accept" => TURBO), submit({})],
  ["logo, deleted", "GET", "/account/logo", browser("Accept" => IMAGE)],
  ["upload a logo that can't be resized", "PATCH", "/account", david("Accept" => TURBO), upload([ file_part("account[logo]", "pixel.bmp", "image/bmp") ])],
  ["logo that can't be resized", "GET", "/account/logo", browser("Accept" => IMAGE)],
  ["blank logo", "PATCH", "/account", david("Accept" => TURBO), submit("account[logo]" => "")],

  # accounts/join_codes
  ["new join code, member", "POST", "/account/join_code", kevin("Accept" => TURBO), submit({})],
  ["new join code", "POST", "/account/join_code", david("Accept" => TURBO), submit({})],
  ["join page, old code", "GET", "/join/#{JOIN_CODE}", browser],

  # accounts/custom_styles
  ["custom styles", "GET", "/account/custom_styles/edit", david],
  ["custom styles, member", "GET", "/account/custom_styles/edit", kevin],
  ["save custom styles, member", "PUT", "/account/custom_styles", kevin("Accept" => TURBO),
    submit("account[custom_styles]" => ":root { --color-text: red; }")],
  ["save custom styles", "PUT", "/account/custom_styles", david("Accept" => TURBO),
    submit("account[custom_styles]" => ":root { --color-text: red; }", "account[name]" => "Ignored")],
  ["custom styles, saved", "GET", "/account/custom_styles/edit", david],
  ["a page with custom styles", "GET", USER_PATH.(KEVIN_ID), kevin],
  ["clear custom styles", "PATCH", "/account/custom_styles", david("Accept" => TURBO), submit("account[custom_styles]" => "")]
]

TABLES = %w[ accounts users memberships sessions active_storage_blobs active_storage_attachments active_storage_variant_records ]

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

RESPONSE_HEADERS = %w[ content-type location vary cache-control set-cookie x-frame-options etag content-disposition content-transfer-encoding ]

etags = {}

results = CASES.map do |name, method, path, headers, body, remote_addr|
  headers = headers.transform_values { |value| value.is_a?(Symbol) ? etags.fetch(value) : value }
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
  if response_body.respond_to?(:to_path)
    text << File.binread(response_body.to_path)
  else
    response_body.each { |chunk| text << chunk }
  end
  response_body.close if response_body.respond_to?(:close)
  after = snapshot

  response_headers = response_headers.to_h.transform_keys(&:downcase)
  etags[:etag_of_stock_logo] = response_headers["etag"] if name == "stock logo"
  binary = response_headers["content-type"].to_s.start_with?("image/")
  {
    "name" => name,
    "request" => { "method" => method, "path" => path, "headers" => headers, "content_type" => content_type,
      "body" => body.is_a?(Hash) ? nil : body, "multipart" => body.is_a?(Hash) ? body : nil, "remote_addr" => env["REMOTE_ADDR"] },
    "response" => {
      "status" => status,
      "headers" => response_headers.slice(*RESPONSE_HEADERS),
      "body" => binary ? nil : text.force_encoding("UTF-8"),
      "body_sha256" => binary ? Digest::SHA256.hexdigest(text) : nil,
      "body_size" => binary ? text.bytesize : nil
    },
    "events" => events.dup,
    "changes" => changes(before, after)
  }
end

File.write(File.join(WORK, OUTPUT), JSON.pretty_generate("now" => NOW.iso8601, "libvips" => Vips.version_string, "fixtures" => FIXTURES, "cases" => results) + "\n")

# What the reference does when browsers fetch people's avatars and people remove their own through
# Users::AvatarsController: each response (status, headers, and the body, or the SHA-256 and size of
# an image's), and every row it changed. Writes avatars.json, which AvatarsControllerTests replays
# through the C# router and controller on the same database and storage.
#
# The database is D02's tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 (the default parity
# seed, where everyone's password is "secret123456"). Run this script in the reference app on a
# copy of it, with the clock frozen:
#
#   mkdir -p tmp/a06/db tmp/a06/storage
#   cp tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 tmp/a06/db/production.sqlite3
#   parity/bin/reference runner --storage tmp/a06 --time 2026-03-02T16:00:00Z --freeze \
#     tests/Campfire.Web.Tests/Controllers/Avatars/Vectors/generate.rb
#
# The seed's database names Jason's and Deploy Bot's avatars (moon.jpg) and their :square variants,
# but its storage isn't committed: STORAGE lists the files this script (and the C# test) puts under
# each blob's key first, the fixture and the variant Rails made of it in the seed build
# (reference-rust/vectors/storage/moon-avatar.webp, whose checksum the seed records).
#
# Requests go straight to Rails.application (the stack Puma runs), in order, on one database, with
# CSRF protection on. A case's path or header may be a symbol, filled in from an earlier response
# or row when the case runs. Avatars made here by libvips (someone joining with moon.jpg) are
# recorded with the libvips version that made them.
require "json"
require "digest"
require "fileutils"
require "action_controller/test_case"

HOST = "campfire.test"
NOW = Time.now.utc
WORK = ENV.fetch("PARITY_WORK")
OUTPUT = ARGV.fetch(0, "tests/Campfire.Web.Tests/Controllers/Avatars/Vectors/avatars.json")

FIXTURES = [
  # David, an administrator, Kevin, a member, and Jason, an administrator with an avatar, signed in
  # and active just now.
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (900001, 127326141, 'DavidSessionToken0000001', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (900002, 712064548, 'KevinSessionToken0000002', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (900003, 149087659, 'JasonSessionToken0000003', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')",
  # Names whose initials are long, or skip letters Ruby's \w doesn't know.
  "INSERT INTO users (id, name, role, status, created_at, updated_at) " \
    "VALUES (900101, 'Mary Jane Watson', 0, 0, '2026-02-10 10:00:00', '2026-02-10 10:00:00')",
  "INSERT INTO users (id, name, role, status, created_at, updated_at) " \
    "VALUES (900102, 'Émile Zola', 0, 0, '2026-02-10 10:00:00', '2026-02-10 10:00:00')",
  "INSERT INTO users (id, name, role, status, created_at, updated_at) " \
    "VALUES (900103, 'O''Brien & <Sons>', 0, 0, '2026-02-10 10:00:00', '2026-02-10 10:00:00')"
]
FIXTURES.each { |sql| ActiveRecord::Base.connection.execute(sql) }

# [blob id, file under the repository]
STORAGE = [
  [ 1, "reference/test/fixtures/files/moon.jpg" ],
  [ 2, "reference-rust/vectors/storage/moon-avatar.webp" ],
  [ 3, "reference/test/fixtures/files/moon.jpg" ],
  [ 4, "reference-rust/vectors/storage/moon-avatar.webp" ]
]
STORAGE.each do |id, file|
  path = ActiveStorage::Blob.service.path_for(ActiveStorage::Blob.find(id).key)
  FileUtils.mkdir_p(File.dirname(path))
  FileUtils.cp(File.join(WORK, file), path)
end

DAVID = "DavidSessionToken0000001"
KEVIN = "KevinSessionToken0000002"
JASON = "JasonSessionToken0000003"
JOIN_CODE = Account.first.join_code
CSRF = "a06AvatarsCsrfToken00a06AvatarsCsrfToken00A" # the session's _csrf_token (urlsafe base64, 32 bytes)
SESSION_ID = "0123456789abcdef0123456789abcdef"
MODERN = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
TURBO = "text/vnd.turbo-stream.html, text/html, application/xhtml+xml"
IMAGE = "image/avif,image/webp,image/png,image/svg+xml,image/*;q=0.8,*/*;q=0.5"
PASSWORD = "secret123456"
BOUNDARY = "----a06FormBoundary7MA4YWxkTrZu0gW"
FILES = "reference/test/fixtures/files"

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

def browser(extra = {}) = { "Cookie" => CSRF_SESSION, "User-Agent" => MODERN, "Accept" => IMAGE }.merge(extra)

def signed_in(token, extra = {}) = browser("Cookie" => "#{session_token(token)}; #{CSRF_SESSION}").merge(extra)

def david(extra = {}) = signed_in(DAVID, extra)

def kevin(extra = {}) = signed_in(KEVIN, extra)

def jason(extra = {}) = signed_in(JASON, extra)

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

def join_with(name, email, file, content_type)
  upload([ { "name" => "user[name]", "value" => name }, { "name" => "user[email_address]", "value" => email },
    { "name" => "user[password]", "value" => PASSWORD }, file_part("user[avatar]", file, content_type) ])
end

def avatar(id, suffix = "") = "/users/#{User.find(id).avatar_token}/avatar#{suffix}"

DAVID_ID = 127326141
JASON_ID = 149087659
BENDER_ID = 394959859 # an active bot
KEVIN_ID = 712064548
JZ_ID = 773523953
RITA_ID = 773523954 # deactivated
MALLORY_ID = 773523955 # banned
DEPLOY_BOT_ID = 773523956 # a bot with an avatar
OLD_BOT_ID = 773523957 # a deactivated bot
LOU_ID = 773523958
MARY_JANE_ID = 900101
EMILE_ID = 900102
OBRIEN_ID = 900103

# An avatar token for a user that doesn't exist: a good signature on a missing id.
MISSING = User.signed_id_verifier.generate(1, purpose: User.combine_signed_id_purposes(:avatar))

# Path builders run when their case does, after the requests before them.
JOINED = ->(email) { -> { avatar(User.find_by!(email_address: email).id) } }

# [name, method, path, headers, body (a string or an upload)]
CASES = [
  # Initials, on a colour from the user's id.
  [ "David's initials", "GET", avatar(DAVID_ID), kevin ],
  [ "Kevin's own initials", "GET", avatar(KEVIN_ID), kevin ],
  [ "JZ's initials", "GET", avatar(JZ_ID), david ],
  [ "a deactivated person's initials", "GET", avatar(RITA_ID), david ],
  [ "a banned person's initials", "GET", avatar(MALLORY_ID), david ],
  [ "Lonely Lou's initials", "GET", avatar(LOU_ID), david ],
  [ "three initials", "GET", avatar(MARY_JANE_ID), david ],
  [ "initials Ruby's \\w skips", "GET", avatar(EMILE_ID), david ],
  [ "initials from punctuation", "GET", avatar(OBRIEN_ID), david ],
  [ "initials, cached", "GET", avatar(DAVID_ID), kevin("If-None-Match" => :etag_of_initials) ],
  [ "initials, cached etag of another user", "GET", avatar(KEVIN_ID), kevin("If-None-Match" => :etag_of_initials) ],
  [ "initials, SVG accepted", "GET", avatar(DAVID_ID), kevin("Accept" => "image/svg+xml") ],
  [ "initials, SVG accepted and cached", "GET", avatar(DAVID_ID), kevin("Accept" => "image/svg+xml", "If-None-Match" => :etag_of_svg_initials) ],
  [ "initials, anything accepted", "GET", avatar(DAVID_ID), kevin("Accept" => "*/*") ],
  [ "initials, JSON accepted", "GET", avatar(DAVID_ID), kevin("Accept" => "application/json") ],
  [ "initials, HTML accepted", "GET", avatar(DAVID_ID), kevin("Accept" => "text/html") ],
  [ "initials, no Accept", "GET", avatar(DAVID_ID), kevin.except("Accept") ],
  [ "initials, .svg", "GET", avatar(DAVID_ID, ".svg"), kevin ],
  [ "initials, .png", "GET", avatar(DAVID_ID, ".png"), kevin ],
  [ "initials with a version", "GET", "#{avatar(DAVID_ID)}?v=20260102160000", kevin ],
  [ "initials, HEAD", "HEAD", avatar(DAVID_ID), kevin ],

  # The stock bot avatar.
  [ "Bender's avatar", "GET", avatar(BENDER_ID), david ],
  [ "a deactivated bot's avatar", "GET", avatar(OLD_BOT_ID), david ],
  [ "bot avatar, cached", "GET", avatar(BENDER_ID), david("If-None-Match" => :etag_of_bot) ],

  # Uploaded avatars: the seed's :square variants.
  [ "Jason's avatar", "GET", avatar(JASON_ID), kevin ],
  [ "Deploy Bot's avatar", "GET", avatar(DEPLOY_BOT_ID), kevin ],
  [ "Jason's own avatar", "GET", avatar(JASON_ID), jason ],
  [ "uploaded avatar, cached", "GET", avatar(JASON_ID), kevin("If-None-Match" => :etag_of_upload) ],
  [ "uploaded avatar, SVG accepted", "GET", avatar(JASON_ID), kevin("Accept" => "image/svg+xml") ],

  # Bad tokens.
  [ "a token that isn't one", "GET", "/users/not-a-valid-token/avatar", david ],
  [ "a user id for a token", "GET", "/users/#{DAVID_ID}/avatar", david ],
  [ "a tampered token", "GET", avatar(DAVID_ID).sub("--", "x--"), david ],
  [ "a token for nobody", "GET", "/users/#{MISSING}/avatar", david ],
  [ "a token for the user's page", "GET", "/users/#{User.find(DAVID_ID).signed_id}/avatar", david ],

  # Signed out.
  [ "signed out", "GET", avatar(DAVID_ID), browser ],
  [ "signed out, uploaded avatar", "GET", avatar(JASON_ID), browser ],

  # Avatars uploaded while joining.
  [ "join with an avatar", "POST", "/join/#{JOIN_CODE}", browser("Accept" => TURBO), join_with("Ava Tar", "ava@37signals.com", "moon.jpg", "image/jpeg") ],
  [ "a new avatar", "GET", JOINED.("ava@37signals.com"), david ],
  [ "a new avatar, again", "GET", JOINED.("ava@37signals.com"), kevin ],
  [ "join with an avatar that can't be resized", "POST", "/join/#{JOIN_CODE}", browser("Accept" => TURBO),
    join_with("Pix El", "pixel@37signals.com", "pixel.bmp", "image/bmp") ],
  [ "initials when the avatar can't be resized", "GET", JOINED.("pixel@37signals.com"), david ],

  # users/avatars#destroy: always the signed-in person's own.
  [ "remove, no CSRF token", "DELETE", avatar(JASON_ID), jason("Accept" => TURBO), URI.encode_www_form("x" => "y") ],
  [ "remove, signed out", "DELETE", avatar(JASON_ID), browser("Accept" => TURBO), submit({}) ],
  [ "remove another's avatar", "DELETE", avatar(JASON_ID), kevin("Accept" => TURBO), submit({}) ],
  [ "remove, with a bad token", "DELETE", "/users/not-a-valid-token/avatar", kevin("Accept" => TURBO), submit({}) ],
  [ "Jason's avatar, still there", "GET", avatar(JASON_ID), kevin ],
  [ "remove own avatar", "DELETE", avatar(JASON_ID), jason("Accept" => TURBO), submit({}) ],
  [ "Jason's initials", "GET", avatar(JASON_ID), kevin ],
  [ "Jason's initials, old etag", "GET", avatar(JASON_ID), kevin("If-None-Match" => :etag_of_upload) ],
  [ "remove own avatar again", "DELETE", avatar(JASON_ID), jason("Accept" => TURBO), submit({}) ]
]

ETAG_SOURCES = {
  "David's initials" => :etag_of_initials,
  "initials, SVG accepted" => :etag_of_svg_initials,
  "Bender's avatar" => :etag_of_bot,
  "Jason's avatar" => :etag_of_upload
}

TABLES = %w[ users sessions active_storage_blobs active_storage_attachments active_storage_variant_records ]

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

RESPONSE_HEADERS = %w[ content-type location vary cache-control set-cookie x-frame-options etag last-modified content-disposition content-transfer-encoding ]

etags = {}

results = CASES.map do |name, method, path, headers, body|
  path = path.call if path.respond_to?(:call)
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
  etags[ETAG_SOURCES[name]] = response_headers["etag"] if ETAG_SOURCES[name]
  binary = response_headers["content-type"].to_s.match?(%r{\Aimage/(?!svg)})
  {
    "name" => name,
    "request" => { "method" => method, "path" => path, "headers" => headers, "content_type" => content_type,
      "body" => body.is_a?(Hash) ? nil : body, "multipart" => body.is_a?(Hash) ? body : nil },
    "response" => {
      "status" => status,
      "headers" => response_headers.slice(*RESPONSE_HEADERS),
      "body" => binary ? nil : text.force_encoding("UTF-8"),
      "body_sha256" => binary ? Digest::SHA256.hexdigest(text) : nil,
      "body_size" => binary ? text.bytesize : nil
    },
    "changes" => changes(before, after)
  }
end

File.write(File.join(WORK, OUTPUT), JSON.pretty_generate("now" => NOW.iso8601, "libvips" => Vips.version_string,
  "fixtures" => FIXTURES, "storage" => STORAGE, "cases" => results) + "\n")

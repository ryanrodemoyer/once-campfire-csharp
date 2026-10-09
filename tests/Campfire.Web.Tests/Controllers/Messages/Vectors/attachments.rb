# What the reference does when the composer's file uploader uploads an image, a video and a
# plain file (app/javascript/models/file_uploader.js: a multipart XHR POST of
# message[attachment] and message[client_message_id], with the CSRF token in a header), and when
# each of those messages is destroyed, through MessagesController: each response, the Action
# Cable broadcasts and Active Job enqueues it made, and every row it changed. Writes
# attachments.json, which AttachmentsTests replays through the C# router and controllers on the
# same database.
#
# The database is D02's tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 (the default parity
# seed). Run this script in the reference app on a copy of it, with the clock frozen (it writes
# the file itself, since the production logger shares stdout with the runner):
#
#   mkdir -p tmp/m10/db tmp/m10/storage
#   cp tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 tmp/m10/db/production.sqlite3
#   parity/bin/reference runner --storage tmp/m10 --time 2026-03-02T16:00:00Z --freeze \
#     tests/Campfire.Web.Tests/Controllers/Messages/Vectors/attachments.rb
#
# Uploads are multipart bodies built from the parts each case lists (fixture files from
# reference/test/fixtures/files); the C# test builds the same bytes. The media the request
# generates (a thumbnail, a video preview and its webp variant) depends on the toolchain, so the
# versions are recorded and the blobs that came out of it are listed per case: on a toolchain
# that doesn't match, the replay compares their bytes as placeholders, like S03's goldens.
#
# Requests go straight to Rails.application (the stack Puma runs), in order, on one database.
# The fixtures below are applied first, and the C# test applies the same SQL to its copy.
require "json"
require "action_controller/test_case"

HOST = "campfire.test"
NOW = Time.now.utc
WORK = ENV.fetch("PARITY_WORK")
OUTPUT = ARGV.fetch(0, "tests/Campfire.Web.Tests/Controllers/Messages/Vectors/attachments.json")

FIXTURES = [
  # Kevin, a member, signed in and active just now (no activity refresh).
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (900001, 712064548, 'KevinSessionToken0000001', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')"
]
FIXTURES.each { |sql| ActiveRecord::Base.connection.execute(sql) }

DAVID = "AxJs94fteQ5Autv2VrKsH68c" # administrator; last active 14:00, so the first request refreshes it
KEVIN = "KevinSessionToken0000001"
CSRF = "m10UploadsCsrfToken000000000000000000000m0A" # the session's _csrf_token (urlsafe base64, 32 bytes)
SESSION_ID = "0123456789abcdef0123456789abcdef"
MODERN = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"

HQ = 201306877           # open; David, Kevin, Deploy Bot (no webhook), ...
KEVIN_BENDER = 340026324 # direct; Kevin and Bender (webhook)

# The file uploader sends no Accept header, so the browser's XHR default answers.
XHR = "*/*"
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

# The uploader's XHR: the CSRF token in a header, like every signed-in browser request.
def signed_in(token, extra = {})
  { "Cookie" => "#{session_token(token)}; #{CSRF_SESSION}", "User-Agent" => MODERN }.merge(extra)
end

def uploader(token, accept = XHR, extra = {})
  signed_in(token, { "Accept" => accept, "X-CSRF-Token" => TOKEN, "Origin" => "http://#{HOST}" }.merge(extra))
end

BOUNDARY = "----m10UploadBoundary7MA4YWxkTrZu0gW"
FILES = "reference/test/fixtures/files"

# The FormData the uploader builds: the file, then the client message id.
def upload(filename, content_type, client_message_id = nil)
  parts = [ { "name" => "message[attachment]", "filename" => filename, "content_type" => content_type, "file" => "#{FILES}/#{filename}" } ]
  parts << { "name" => "message[client_message_id]", "value" => client_message_id } if client_message_id
  { "boundary" => BOUNDARY, "parts" => parts }
end

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

def attachment_value(value) = { "boundary" => BOUNDARY, "parts" => [
  { "name" => "message[attachment]", "value" => value },
  { "name" => "message[client_message_id]", "value" => "m10-hq-junk" }
] }

# Messages made below, by client_message_id.
def created(client_message_id) = Message.find_by!(client_message_id: client_message_id).id

# [name, method, path (or a proc for one), headers, body (a string or an upload form)]
CASES = [
  # create_with_attachment! (messages_controller.rb create): the blob and attachment rows in the
  # message's transaction, the upload, then process_attachment's analysis and thumbnail.
  ["member uploads an image", "POST", "/rooms/#{HQ}/messages", uploader(KEVIN), upload("moon.jpg", "image/jpeg", "m10-hq-moon")],
  ["member uploads a video", "POST", "/rooms/#{HQ}/messages", uploader(KEVIN), upload("alpha-centuri.mov", "video/quicktime", "m10-hq-mov")],
  ["member uploads a plain file", "POST", "/rooms/#{HQ}/messages", uploader(KEVIN), upload("pixel.bmp", "image/bmp", "m10-hq-bmp")],
  ["member uploads a plain file in a direct room with a bot", "POST", "/rooms/#{KEVIN_BENDER}/messages", uploader(KEVIN),
    upload("pixel.bmp", "image/bmp", "m10-direct-bmp")],
  ["administrator uploads an image with a body", "POST", "/rooms/#{HQ}/messages", uploader(DAVID, TURBO_STREAM),
    { "boundary" => BOUNDARY, "parts" => [
      { "name" => "message[body]", "value" => "<p>The black hole, with a body</p>" },
      { "name" => "message[client_message_id]", "value" => "m10-hq-black-hole" },
      { "name" => "message[attachment]", "filename" => "black_hole.jpg", "content_type" => "image/jpeg", "file" => "#{FILES}/black_hole.jpg" }
    ] }],
  ["upload without a client message id", "POST", "/rooms/#{HQ}/messages", uploader(KEVIN), upload("moon.jpg", "image/jpeg")],
  # Any other string is a blob's signed id (`Blob.find_signed!`): the uploaded image again.
  ["attach the uploaded image's blob by signed id", "POST", "/rooms/#{HQ}/messages", uploader(KEVIN),
    -> { { "boundary" => BOUNDARY, "parts" => [
      { "name" => "message[attachment]", "value" => Message.find_by!(client_message_id: "m10-hq-moon").attachment.blob.signed_id },
      { "name" => "message[client_message_id]", "value" => "m10-hq-again" }
    ] } }],
  ["upload with a bad signed id", "POST", "/rooms/#{HQ}/messages", uploader(KEVIN), attachment_value("junk")],

  # destroy of a message with a file: the attachment goes in the message's transaction, and the
  # blob is purged later (ActiveStorage::PurgeJob).
  ["member destroys the image message", "DELETE", -> { "/rooms/#{HQ}/messages/#{created("m10-hq-moon")}" }, uploader(KEVIN, TURBO_STREAM), nil],
  ["member destroys someone else's message", "DELETE", -> { "/rooms/#{HQ}/messages/#{created("m10-hq-black-hole")}" }, uploader(KEVIN, TURBO_STREAM), nil],
  ["administrator destroys the video message", "DELETE", -> { "/rooms/#{HQ}/messages/#{created("m10-hq-mov")}" }, uploader(DAVID, TURBO_STREAM), nil],
  ["member destroys the plain file message", "DELETE", -> { "/rooms/#{HQ}/messages/#{created("m10-hq-bmp")}" }, uploader(KEVIN, TURBO_STREAM), nil],
  ["member destroys the message in the direct room", "DELETE", -> { "/rooms/#{KEVIN_BENDER}/messages/#{created("m10-direct-bmp")}" },
    uploader(KEVIN, TURBO_STREAM), nil]
]

# Every row of the tables a message write touches, keyed by id.
TABLES = %w[ messages action_text_rich_texts rooms memberships boosts sessions
            active_storage_attachments active_storage_blobs active_storage_variant_records ]

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

# The blobs the request generated (a thumbnail, a preview image or a webp variant): their bytes
# are the toolchain's, not the database's, so the replay gates them on the recorded versions.
def media_blob_ids(before, after)
  (after["active_storage_blobs"].keys - before["active_storage_blobs"].keys).select do |id|
    ActiveStorage::Attachment.where(blob_id: id, record_type: "Message", name: "attachment").empty?
  end
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
  multipart = body.is_a?(Hash) ? body : nil
  content_type = multipart ? "multipart/form-data; boundary=#{multipart["boundary"]}" : nil

  env = Rack::MockRequest.env_for("http://#{HOST}#{path}", method: method, input: multipart ? multipart_body(multipart) : (body || ""))
  env["CONTENT_TYPE"] = content_type if content_type
  env["REMOTE_ADDR"] = "198.51.100.7"
  headers.each { |key, value| env["HTTP_#{key.upcase.tr("-", "_")}"] = value }

  events.clear
  before = snapshot
  status, response_headers, response_body = Rails.application.call(env)
  warn "#{name}: #{status}"
  text = +""
  response_body.each { |chunk| text << chunk }
  response_body.close if response_body.respond_to?(:close)
  after = snapshot

  {
    "name" => name,
    "request" => { "method" => method, "path" => path, "headers" => headers, "multipart" => multipart },
    "response" => {
      "status" => status,
      "headers" => response_headers.to_h.transform_keys(&:downcase).slice(*RESPONSE_HEADERS),
      "body" => text.force_encoding("UTF-8")
    },
    "events" => events.dup,
    "changes" => changes(before, after),
    "media_blob_ids" => media_blob_ids(before, after)
  }
end

# The toolchain that generated the media blobs, as storage.json records it.
require "vips"
versions = {
  "libvips" => Vips.version_string, "ruby_vips" => Vips::VERSION,
  "ffmpeg" => `ffmpeg -version`.lines.first.strip, "ffprobe" => `ffprobe -version`.lines.first.strip,
  "rails" => Rails.version, "ruby" => RUBY_VERSION
}

File.write(File.join(WORK, OUTPUT), JSON.pretty_generate(
  "now" => NOW.iso8601, "fixtures" => FIXTURES, "versions" => versions, "cases" => results
) + "\n")
puts "wrote #{OUTPUT} (#{results.size} cases)"

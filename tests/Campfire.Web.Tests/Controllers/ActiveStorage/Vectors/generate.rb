# What the reference does for Active Storage's HTTP endpoints: blob and representation redirect
# and proxy, the disk service's GET (ranges, HEAD, conditional) and PUT, and direct uploads.
# Writes JSON to stdout. ActiveStorageControllerTests replays it on the same database and files.
#
#   mkdir -p tmp/s05/db tmp/s05/storage
#   cp tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 tmp/s05/db/production.sqlite3
#   parity/bin/reference runner --storage tmp/s05 --time 2026-03-02T16:00:00Z --freeze \
#     tests/Campfire.Web.Tests/Controllers/ActiveStorage/Vectors/generate.rb \
#     > tests/Campfire.Web.Tests/Controllers/ActiveStorage/Vectors/endpoints.json
#
# --storage uses tmp/s05 in place and does not copy a seed, so the sqlite file has to be there first.
# Requests go to Rails.application (CSRF on), with the clock frozen. Files are copied onto the
# seed blobs' keys and touched to the frozen instant, so Last-Modified is that instant.
require "json"
require "digest"
require "fileutils"
require "action_controller/test_case"
require "base64"

# RecordNotFound and the forgery error log to stdout in production. The vectors are the JSON only.
Rails.logger = ActiveSupport::Logger.new($stderr)

HOST = "campfire.test"
NOW = Time.now.utc
WORK = ENV.fetch("PARITY_WORK")
MTIME = Time.utc(2026, 3, 2, 16, 0, 0)
NOTES = "Launch notes\n\n1. Copy\n2. Video\n3. Ship\n"

# [blob key, repo file or nil when the bytes are NOTES]
STORAGE = [
  ["2k7n5s996jb5k5xwhx14f5oetpj4", "reference/test/fixtures/files/moon.jpg"],
  ["5nlq52kvmcweobxncp0zy1zvu9bg", "reference-rust/vectors/storage/moon-avatar.webp"],
  ["5yn8o54bcl3ruybz88hlc6blvenf", "reference/test/fixtures/files/moon.jpg"],
  ["6btcnksw2px5m61m8ttuft8qq5sg", "reference-rust/vectors/storage/moon-avatar.webp"],
  ["5j2ce1yctlrqocxcrb749gk74d70", "reference/test/fixtures/files/moon.jpg"],
  ["1la4o27xxpq9ksfz8queu37rsz24", "reference-rust/vectors/storage/moon-thumb.jpg"],
  ["2yenb2ld8ninydzf9y2cpqyrbjqx", "reference/test/fixtures/files/black_hole.jpg"],
  ["4lihdellmqlan3dkoguy17k7b3u9", "reference-rust/vectors/storage/black_hole-thumb.jpg"],
  ["353m578luh88ii4tp6d8yuyva55v", "reference/test/fixtures/files/alpha-centuri.mov"],
  ["359ws0k2x3iscwqju6x1z5fotuj9", "reference-rust/vectors/storage/alpha-centuri-preview_image.jpg"],
  ["3o8eizy6lzq4d0bh82x5ddncbtdn", "reference-rust/vectors/storage/alpha-centuri-preview-webp.webp"],
  ["ex0ujd9i7haorvtq2hku4qo22hd8", "reference-rust/vectors/storage/alpha-centuri-poster.webp"],
  ["5dadz19ls6axuhw3ago9wumgc26k", nil],
  ["raodqbbpbhm7xp5hfgb3tu8on8vi", "reference/test/fixtures/files/pixel.bmp"]
]

def stage(key, file)
  path = ActiveStorage::Blob.service.path_for(key)
  FileUtils.mkdir_p(File.dirname(path))
  if file
    FileUtils.cp(File.join(WORK, file), path)
  else
    File.binwrite(path, NOTES)
  end
  File.utime(MTIME, MTIME, path)
  path
end

def stage_bytes(key, bytes)
  path = ActiveStorage::Blob.service.path_for(key)
  FileUtils.mkdir_p(File.dirname(path))
  File.binwrite(path, bytes)
  File.utime(MTIME, MTIME, path)
  path
end

STORAGE.each { |key, file| stage(key, file) }

FIXTURES = [
  "INSERT OR REPLACE INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (900001, 127326141, 'DavidSessionToken0000001', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')"
]
FIXTURES.each { |sql| ActiveRecord::Base.connection.execute(sql) }

SEED_BLOB = ActiveRecord::Base.connection.select_value("SELECT MAX(id) FROM active_storage_blobs").to_i
SEED_VARIANT = ActiveRecord::Base.connection.select_value("SELECT MAX(id) FROM active_storage_variant_records").to_i
SEED_ATTACHMENT = ActiveRecord::Base.connection.select_value("SELECT MAX(id) FROM active_storage_attachments").to_i

ActiveStorage::Current.url_options = { protocol: "http", host: HOST }
Rails.application.routes.default_url_options = { protocol: "http", host: HOST }
# Rails.application.call resets CurrentAttributes, and afterwards the attributes store itself is
# gone, so a later blob.url cannot set them again. The disk service reads this instead.
ActiveStorage::Blob.service.define_singleton_method(:url_options) { { protocol: "http", host: HOST } }
HELPERS = Rails.application.routes.url_helpers
MOON = ActiveStorage::Blob.find(5)
VIDEO = ActiveStorage::Blob.find(9)
NOTES_BLOB = ActiveStorage::Blob.find(13)
BMP = ActiveStorage::Blob.find(14)

# The variation key the representation URL carries. Processed through that key, so the variant
# record's digest is the decoded key's, which is what the controller looks up.
THUMB_KEY = ActiveStorage::Variation.new(resize_to_limit: [1200, 800]).key
POSTER_KEY = ActiveStorage::Variation.new({ "format" => "webp", "resize_to_limit" => [1200, 800] }).key

# processed opens the source blob and checks its checksum. The committed preview image
# (reference-rust/vectors/storage) is not the bytes the seed's checksum names, and serving never
# checks. Match the checksum to the staged file for the transform, then put the seed's back, so
# extra_sql does not rewrite a seed row.
def processed_from_staged(blob, key)
  sources = [blob]
  sources << blob.preview_image.blob if blob.preview_image.attached?
  saved = {}
  sources.each do |item|
    path = ActiveStorage::Blob.service.path_for(item.key)
    next unless File.file?(path)
    digest = Digest::MD5.base64digest(File.binread(path))
    next if digest == item.checksum
    saved[item.id] = item.checksum
    item.update_columns(checksum: digest)
    item.checksum = digest
  end
  blob.representation(key).processed
ensure
  saved&.each do |id, checksum|
    ActiveStorage::Blob.find(id).update_columns(checksum: checksum)
  end
end

thumb_image = processed_from_staged(MOON, THUMB_KEY).image
poster_image = processed_from_staged(VIDEO, POSTER_KEY).image

LODZ_KEY = "lodzfilename0000000000000001"
lodz_bytes = "jpeg"
LODZ = ActiveStorage::Blob.create!(
  key: LODZ_KEY, filename: "Łódź.jpg", content_type: "image/jpeg",
  byte_size: lodz_bytes.bytesize, checksum: Digest::MD5.base64digest(lodz_bytes),
  service_name: "local", metadata: nil)
stage_bytes(LODZ_KEY, lodz_bytes)

def insert_sql(table, columns, id_floor)
  connection = ActiveRecord::Base.connection
  rows = connection.select_all("SELECT #{columns.join(', ')} FROM #{table} WHERE id > #{id_floor}")
  rows.map do |row|
    vals = columns.map { |column| connection.quote(row[column]) }
    "INSERT INTO #{table} (#{columns.join(', ')}) VALUES (#{vals.join(', ')})"
  end
end

EXTRA_SQL = insert_sql("active_storage_blobs",
  %w[id key filename content_type metadata service_name byte_size checksum created_at], SEED_BLOB) +
  insert_sql("active_storage_variant_records", %w[id blob_id variation_digest], SEED_VARIANT) +
  insert_sql("active_storage_attachments",
    %w[id name record_type record_id blob_id created_at], SEED_ATTACHMENT)

known_keys = STORAGE.map(&:first)
EXTRA_FILES = ActiveStorage::Blob.where("id > ?", SEED_BLOB).filter_map do |blob|
  next if known_keys.include?(blob.key)
  path = ActiveStorage::Blob.service.path_for(blob.key)
  next unless File.file?(path)
  { "key" => blob.key, "base64" => Base64.strict_encode64(File.binread(path)) }
end

def path_of(url) = URI.parse(url).request_uri

DIRECT_KEY = "directuploadkey000000000000"
DIRECT_BODY = "hello!"
DIRECT_CHECKSUM = Digest::MD5.base64digest(DIRECT_BODY)
direct_upload_path = ActiveStorage::Blob.service.url_for_direct_upload(
  DIRECT_KEY, expires_in: 5.minutes, content_type: "application/octet-stream",
  content_length: DIRECT_BODY.bytesize, checksum: DIRECT_CHECKSUM)

SIGNED = {
  "moon_redirect" => HELPERS.rails_blob_path(MOON),
  "moon_redirect_attachment" => HELPERS.rails_blob_path(MOON, disposition: "attachment"),
  "moon_proxy" => HELPERS.rails_storage_proxy_path(MOON),
  "moon_disk" => path_of(MOON.url),
  "moon_disk_attachment" => path_of(MOON.url(disposition: :attachment)),
  "moon_disk_no_expiry" => path_of(MOON.url(expires_in: nil)),
  "video_disk_inline" => path_of(VIDEO.url(disposition: :inline)),
  "thumb_redirect" => HELPERS.rails_storage_redirect_path(MOON.representation(THUMB_KEY)),
  "thumb_proxy" => HELPERS.rails_storage_proxy_path(MOON.representation(THUMB_KEY)),
  "thumb_disk" => path_of(thumb_image.url),
  "poster_redirect" => HELPERS.rails_storage_redirect_path(VIDEO.representation(POSTER_KEY)),
  "poster_proxy" => HELPERS.rails_storage_proxy_path(VIDEO.representation(POSTER_KEY)),
  "poster_disk" => path_of(poster_image.url),
  "lodz_proxy" => HELPERS.rails_storage_proxy_path(LODZ),
  "notes_proxy" => HELPERS.rails_storage_proxy_path(NOTES_BLOB),
  # BMP isn't representable, so Blob#representation raises before a path exists. The URL is the
  # thumb's variation segment on the BMP blob, which is what a client would request.
  "bmp_representation" => nil,
  "direct_upload" => path_of("http://#{HOST}#{direct_upload_path.start_with?('/') ? '' : '/'}#{direct_upload_path}"),
  "direct_upload_key" => DIRECT_KEY,
  "direct_upload_checksum" => DIRECT_CHECKSUM,
  "direct_upload_byte_size" => DIRECT_BODY.bytesize,
  "direct_upload_content_type" => "application/octet-stream",
  "thumb_image_id" => thumb_image.id,
  "poster_image_id" => poster_image.id,
  "lodz_id" => LODZ.id
}
# url_for_direct_upload returns a path. Normalize SIGNED["direct_upload"] below after seeing it.

DAVID = "DavidSessionToken0000001"
CSRF = "s05StorageCsrfTokenXXs05StorageCsrfTokenXXA"
raise "csrf token must be 43 urlsafe-base64 chars" unless CSRF.bytesize == 43
SESSION_ID = "0123456789abcdef0123456789abcdef"

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
SIGNED_IN = "#{session_token(DAVID)}; #{CSRF_SESSION}"

def browser(extra = {}) = { "Cookie" => CSRF_SESSION, "Accept" => "text/html" }.merge(extra)

def signed_in(extra = {}) = browser("Cookie" => SIGNED_IN).merge(extra)

JSON_TYPE = "application/json"
UPLOAD_TYPE = "application/octet-stream"

def blob_json(byte_size: DIRECT_BODY.bytesize, content_type: UPLOAD_TYPE, filename: "quota.bin", checksum: DIRECT_CHECKSUM, metadata: nil)
  blob = { "filename" => filename, "byte_size" => byte_size, "checksum" => checksum, "content_type" => content_type }
  blob["metadata"] = metadata unless metadata.nil?
  JSON.generate("blob" => blob)
end

missing_sid = ActiveStorage.verifier.generate(999_999_999, purpose: :blob_id)
MISSING = HELPERS.rails_service_blob_path(missing_sid, "missing.jpg")
BAD_SIG = "/rails/active_storage/blobs/redirect/not-a-signed-id/moon.jpg"
BAD_DISK = "/rails/active_storage/disk/not-a-signed-key/moon.jpg"
# A signed disk key for a file that was never written.
MISSING_FILE = path_of(ActiveStorage::Blob.service.url(
  "nosuchkey000000000000000000", expires_in: 5.minutes,
  filename: ActiveStorage::Filename.new("missing.bin"), disposition: :inline,
  content_type: "application/octet-stream"))

thumb_parts = SIGNED["thumb_redirect"].split("/")
variation_segment = thumb_parts[-2]
SIGNED["bmp_representation"] = "/rails/active_storage/representations/redirect/#{BMP.signed_id}/#{variation_segment}/pixel.bmp"
# A tampered URL: the moon redirect's signed id plus a variation segment that does not verify.
bad_variation = "/rails/active_storage/representations/redirect/#{MOON.signed_id}/not-a-variation-key/moon.jpg"

TABLES = %w[active_storage_blobs active_storage_attachments active_storage_variant_records]
RESPONSE_HEADERS = %w[content-type content-length content-range content-disposition content-transfer-encoding
  cache-control etag last-modified accept-ranges location vary date allow
  x-frame-options x-xss-protection x-content-type-options x-permitted-cross-domain-policies referrer-policy]

def snapshot
  connection = ActiveRecord::Base.connection
  TABLES.to_h do |table|
    [table, connection.select_all("SELECT * FROM #{table}").rows.to_h { |row| [row.first, row] }]
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
    [table, rows] if rows.any?
  end.to_h
end

def call(method, path, headers, body, content_type)
  env = Rack::MockRequest.env_for("http://#{HOST}#{path}", method: method, input: body || "")
  env["CONTENT_TYPE"] = content_type if content_type
  env["REMOTE_ADDR"] = "198.51.100.7"
  headers.each { |key, value| env["HTTP_#{key.upcase.tr('-', '_')}"] = value }
  status, response_headers, response_body = Rails.application.call(env)
  bytes = +""
  bytes.force_encoding(Encoding::ASCII_8BIT)
  response_body.each { |chunk| bytes << chunk.to_s.b }
  response_body.close if response_body.respond_to?(:close)
  [status, response_headers, bytes]
end

def normalize_upload(text)
  text.sub(%r{(/rails/active_storage/disk/)[^"]+}, "\\1<token>")
    .sub(/"key":"[^"]+"/, '"key":"<key>"')
    .sub(/"signed_id":"[^"]+"/, '"signed_id":"<signed_id>"')
end

def record(name, method, path, headers, body = nil, content_type = nil, normalize: nil)
  before = snapshot
  status, response_headers, bytes = call(method, path, headers, body, content_type)
  text = bytes.dup.force_encoding(Encoding::UTF_8)
  text = nil unless text.valid_encoding?
  shown = response_headers.to_h.transform_keys { |key| key.to_s.downcase }.slice(*RESPONSE_HEADERS)
  # Rack joins repeated headers with newlines. Keep one string.
  shown.transform_values! { |value| value.is_a?(Array) ? value.join("\n") : value.to_s }
  response = {
    "status" => status,
    "headers" => shown,
    "body_sha256" => Digest::SHA256.hexdigest(bytes),
    "body_size" => bytes.bytesize
  }
  response["body"] = text if text && bytes.bytesize < 64_000 && (shown["content-type"].to_s =~ /json|text|html|xml/ || bytes.bytesize < 256)
  response["body_normalized"] = normalize.call(text) if normalize && text
  {
    "name" => name,
    "request" => {
      "method" => method, "path" => path, "headers" => headers,
      "content_type" => content_type, "body_base64" => body && Base64.strict_encode64(body)
    },
    "response" => response,
    "changes" => changes(before, snapshot)
  }
end

results = []
disk = SIGNED["moon_disk"]
proxy = SIGNED["moon_proxy"]
results << record("disk show", "GET", disk, {})
last_modified = results.last["response"]["headers"]["last-modified"]
results << record("disk show, attachment url", "GET", SIGNED["moon_disk_attachment"], {})
results << record("disk show, no expiry", "GET", SIGNED["moon_disk_no_expiry"], {})
results << record("disk show, video forced attachment", "GET", SIGNED["video_disk_inline"], { "Range" => "bytes=0-15" })
results << record("disk head", "HEAD", disk, {})
results << record("disk range", "GET", disk, { "Range" => "bytes=0-3" })
results << record("disk range suffix", "GET", disk, { "Range" => "bytes=-4" })
results << record("disk range multi", "GET", disk, { "Range" => "bytes=0-1,4-5" })
results << record("disk range unsatisfiable", "GET", disk, { "Range" => "bytes=999999-999999" })
results << record("disk range head", "HEAD", disk, { "Range" => "bytes=0-3" })
results << record("disk not modified", "GET", disk, { "If-Modified-Since" => last_modified })
results << record("disk modified since earlier", "GET", disk, { "If-Modified-Since" => "Thu, 01 Jan 2015 00:00:00 GMT" })
results << record("disk bad key", "GET", BAD_DISK, {})
results << record("disk missing file", "GET", MISSING_FILE, {})

results << record("blob redirect", "GET", SIGNED["moon_redirect"], {})
results << record("blob redirect attachment", "GET", SIGNED["moon_redirect_attachment"], {})
results << record("blob redirect bad signature", "GET", BAD_SIG, {})
results << record("blob redirect missing", "GET", MISSING, {})
results << record("blob proxy", "GET", proxy, {})
proxy_etag = results.last["response"]["headers"]["etag"]
results << record("blob proxy head", "HEAD", proxy, {})
results << record("blob proxy not modified", "GET", proxy, { "If-None-Match" => proxy_etag })
results << record("blob proxy range", "GET", proxy, { "Range" => "bytes=0-5" })
results << record("blob proxy range multi", "GET", proxy, { "Range" => "bytes=0-1,3-4" })
results << record("blob proxy range unsatisfiable", "GET", proxy, { "Range" => "bytes=999999-999999" })
results << record("blob proxy range head", "HEAD", proxy, { "Range" => "bytes=0-5" })
results << record("lodz proxy", "GET", SIGNED["lodz_proxy"], {})
results << record("notes proxy", "GET", SIGNED["notes_proxy"], {})
results << record("thumb redirect", "GET", SIGNED["thumb_redirect"], {})
results << record("thumb proxy", "GET", SIGNED["thumb_proxy"], {})
thumb_etag = results.last["response"]["headers"]["etag"]
results << record("thumb proxy not modified", "GET", SIGNED["thumb_proxy"], { "If-None-Match" => thumb_etag })
results << record("poster redirect", "GET", SIGNED["poster_redirect"], {})
results << record("poster proxy", "GET", SIGNED["poster_proxy"], { "Range" => "bytes=0-3" })
results << record("bmp representation", "GET", SIGNED["bmp_representation"], {})
results << record("representation bad key", "GET", bad_variation, {})

upload_headers = signed_in("Origin" => "http://#{HOST}", "X-CSRF-Token" => TOKEN, "Content-Type" => JSON_TYPE)
upload_body = blob_json
results << record("direct upload anonymous", "POST", "/rails/active_storage/direct_uploads",
  browser("Origin" => "http://#{HOST}", "X-CSRF-Token" => TOKEN, "Content-Type" => JSON_TYPE),
  upload_body, JSON_TYPE)
results << record("direct upload no csrf", "POST", "/rails/active_storage/direct_uploads",
  browser("Origin" => "http://#{HOST}", "Content-Type" => JSON_TYPE),
  upload_body, JSON_TYPE)
results << record("direct upload", "POST", "/rails/active_storage/direct_uploads", upload_headers, upload_body, JSON_TYPE,
  normalize: ->(text) { normalize_upload(text) })

uploaded = JSON.parse(results.last["response"]["body"])
upload_url = URI.parse(uploaded.dig("direct_upload", "url")).request_uri
results << record("disk put anonymous", "PUT", upload_url, { "Content-Type" => UPLOAD_TYPE, "Content-Length" => DIRECT_BODY.bytesize.to_s },
  DIRECT_BODY, UPLOAD_TYPE)
results << record("disk put bad token", "PUT", "/rails/active_storage/disk/not-a-token",
  signed_in("Content-Type" => UPLOAD_TYPE, "Content-Length" => DIRECT_BODY.bytesize.to_s),
  DIRECT_BODY, UPLOAD_TYPE)
results << record("disk put wrong type", "PUT", upload_url,
  signed_in("Content-Type" => "text/plain", "Content-Length" => DIRECT_BODY.bytesize.to_s),
  DIRECT_BODY, "text/plain")
results << record("disk put bad checksum", "PUT", upload_url,
  signed_in("Content-Type" => UPLOAD_TYPE, "Content-Length" => DIRECT_BODY.bytesize.to_s),
  "hello?", UPLOAD_TYPE)
results << record("disk put", "PUT", upload_url,
  signed_in("Content-Type" => UPLOAD_TYPE, "Content-Length" => DIRECT_BODY.bytesize.to_s),
  DIRECT_BODY, UPLOAD_TYPE)

stored = ActiveStorage::Blob.find(uploaded["id"])
results << record("disk show uploaded", "GET", path_of(stored.url), {})

# direct_upload path from the service is already a path. Record it without a doubled host.
SIGNED["direct_upload"] = direct_upload_path.start_with?("http") ? path_of(direct_upload_path) : direct_upload_path

puts JSON.pretty_generate(
  "now" => NOW.iso8601,
  "fixtures" => FIXTURES,
  "storage" => STORAGE.map { |key, file| file ? { "key" => key, "file" => file } : { "key" => key, "text" => NOTES } },
  "extra_sql" => EXTRA_SQL,
  "extra_files" => EXTRA_FILES,
  "signed" => SIGNED,
  "cases" => results.compact)

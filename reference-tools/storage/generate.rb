# Golden vectors for campfire_storage, produced by the reference app itself.
#
# Runs under `bin/rails runner` in production mode with the fixed parity SECRET_KEY_BASE (see
# reference-tools/storage/run.sh). It drives the real models (Message::Attachment, User::Avatar,
# Account logo) through Active Storage and records:
#
# * Marshal dumps, variation digests and signed variation keys for every transformation hash the
#   app produces, both as Ruby builds them (symbols) and as a URL-decoded variation key yields them
#   (strings), since the two digest differently.
# * Marcel identification, filename sanitization and Content-Disposition cases.
# * Every blob row, attachment row and variant record the upload paths write, with their paths
#   (rails_blob_path, representation paths) and the processed files themselves, so the Rust port
#   can check its thumbnails, avatars, logos and video posters byte for byte.
#
# Output goes to $OUT (default: vectors/): storage.json plus storage/<name> files.
require "json"
require "fileutils"

OUT = Pathname.new(ENV.fetch("OUT", Rails.root.join("../vectors").to_s))
FILES_OUT = OUT.join("storage")
FIXTURES = Rails.root.join("test/fixtures/files")
FileUtils.mkdir_p FILES_OUT

ActiveJob::Base.queue_adapter = :inline
ActiveStorage::Current.url_options = { host: "campfire.test", protocol: "http" }
Rails.application.routes.default_url_options = { host: "campfire.test", protocol: "http" }
include Rails.application.routes.url_helpers

def hex(string) = string.b.unpack1("H*")

# Ruby values with symbols and strings kept apart: {"sym": "webp"}, {"str": "jpg"}, {"hash": [[k, v]]}.
def typed(value)
  case value
  when Symbol then { sym: value.to_s }
  when String then { str: value }
  when Array then value.map { typed(_1) }
  when Hash then { hash: value.map { |k, v| [ k.to_s, typed(v) ] } }
  else value
  end
end

def variation_vector(transformations)
  variation = ActiveStorage::Variation.new(transformations)
  decoded = ActiveStorage::Variation.decode(variation.key)
  {
    inspect: transformations.inspect,
    typed: typed(variation.transformations),
    decoded_typed: typed(decoded.transformations),
    marshal_hex: hex(Marshal.dump(variation.transformations)),
    digest: variation.digest,
    key: variation.key,
    decoded_inspect: decoded.transformations.inspect,
    decoded_marshal_hex: hex(Marshal.dump(decoded.transformations)),
    decoded_digest: decoded.digest
  }
end

variations = [
  { format: :webp, resize_to_limit: [ 512, 512 ] },
  { format: :png, resize_to_limit: [ 512, 512 ] },
  { format: :png, resize_to_limit: [ 192, 192 ] },
  { format: "jpg", resize_to_limit: [ 1200, 800 ] },
  { format: "png", resize_to_limit: [ 1200, 800 ] },
  { format: "gif", resize_to_limit: [ 1200, 800 ] },
  { format: :png, resize_to_limit: [ 1200, 800 ] },
  { format: :webp },
  { format: :webp, resize_to_limit: [ 1200, 800 ] },
  { format: "webp", resize_to_limit: [ 1200, 800 ] },
  { resize_to_limit: [ 100, nil ], saver: { quality: 80, strip: true }, rotate: -90, format: "jpeg" },
  { resize_to_limit: [ 0, 1, 121, 122, 123, 255, 256, 65535, 65536, 16777216, 1073741823, 1073741824, -1, -123, -124, -256, -257, -65536, -65537 ], format: :png }
].map { variation_vector(_1) }

MARCEL_SAMPLES = Dir.children(FIXTURES).sort.flat_map do |name|
  data = FIXTURES.join(name).binread
  [
    [ name, data, nil, name ], [ name, data, "application/octet-stream", name ], [ "renamed.txt", data, nil, name ],
    [ "noext", data, nil, name ], [ name, data, "image/png", name ], [ name, data, "video/mp4", name ]
  ]
end + [
  [ "notes.txt", "hello world", "text/plain" ],
  [ "page.html", "<!DOCTYPE html><html></html>", nil ],
  [ "image.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>", "image/svg+xml" ],
  [ "doc.pdf", "%PDF-1.4\n", nil ],
  [ "archive.zip", "PK\x03\x04rest", nil ],
  [ "report.docx", "PK\x03\x04rest", nil ],
  [ "clip.mp4", "\x00\x00\x00\x18ftypisom\x00\x00\x02\x00", nil ],
  [ "clip.webm", "\x1aE\xdf\xa3\x9fB\x86\x81\x01B\xf7\x81\x01B\xf2\x81\x04B\xf3\x81\x08B\x82\x84webm", nil ],
  [ "photo.heic", "\x00\x00\x00\x18ftypheic", nil ],
  [ "sound.mp3", "ID3\x04\x00\x00", nil ],
  [ "empty.bin", "", nil ],
  [ "UPPER.JPG", "", nil ],
  [ "data.json", "{}", "application/json; charset=utf-8" ],
  [ "file", "plain", "Text/Plain" ],
  [ "gif.gif", "GIF89a", "image/jpeg" ]
]

marcel = MARCEL_SAMPLES.map do |name, data, declared, fixture|
  { name: name, data_hex: fixture ? nil : hex(data), fixture: fixture,
    declared_type: declared, content_type: Marcel::MimeType.for(StringIO.new(data), name: name, declared_type: declared) }
end

FILENAMES = [
  "moon.jpg", "  spaced name .png ", "a/b\\c:d;e|f%g$h<i>j?k*l\"m\tn\ro\n.txt", "‮evil.exe", "résumé.pdf",
  ".bashrc", "archive.tar.gz", "noext", "trailing.", "..", "日本語.png", "emoji 😀.gif", "Ωmega ß.txt",
  "with'quote & (parens) #1+~.txt", "Æther œuvre.jpg", "bad\xFFbyte.jpg".dup.force_encoding("UTF-8")
]

filenames = FILENAMES.map do |name|
  filename = ActiveStorage::Filename.new(name)
  { input_hex: hex(name), sanitized: filename.sanitized, base_hex: hex(filename.base), extension_hex: hex(filename.extension),
    inline: ActionDispatch::Http::ContentDisposition.format(disposition: "inline", filename: filename.sanitized),
    attachment: ActionDispatch::Http::ContentDisposition.format(disposition: "attachment", filename: filename.sanitized),
    escaped_path: ActionDispatch::Journey::Router::Utils.escape_path(filename.sanitized) }
end

# --- Driving the app ------------------------------------------------------------------------------

def upload(name, content_type)
  tempfile = Tempfile.new([ "RackMultipart", File.extname(name) ], binmode: true)
  tempfile.write FIXTURES.join(name).binread
  tempfile.rewind
  ActionDispatch::Http::UploadedFile.new(tempfile: tempfile, filename: name, type: content_type)
end

def blob_row(blob)
  blob.reload
  attrs = blob.attributes_before_type_cast.slice("id", "key", "filename", "content_type", "metadata", "service_name", "byte_size", "checksum")
  attrs.merge("path" => ActiveStorage::Blob.service.path_for(blob.key).delete_prefix(ActiveStorage::Blob.service.root + "/"))
end

def save_file(name, blob)
  File.binwrite FILES_OUT.join(name), blob.download
  name
end

def variant_vector(label, variant, source_blob)
  variant = variant.processed
  image = variant.respond_to?(:image) ? variant.image : variant
  {
    label: label,
    source_blob_id: source_blob.id,
    transformations_inspect: variant.variation.transformations.inspect,
    transformations_typed: typed(variant.variation.transformations),
    variation_digest: variant.variation.digest,
    variant_record: ActiveStorage::VariantRecord.where(blob_id: source_blob.id, variation_digest: variant.variation.digest).pick(:id, :blob_id, :variation_digest),
    blob: blob_row(image.blob),
    attachment: image.attachment.attributes.slice("name", "record_type", "record_id", "blob_id"),
    file: save_file("#{label}.#{variant.variation.format}", image.blob)
  }
end

Account.create!(name: "Campfire") unless Account.any?
account = Account.first
user = User.create!(name: "Vector Victor", email_address: "victor-#{SecureRandom.hex(4)}@example.com", password: "secret123456")
room = Rooms::Open.create!(name: "Storage vectors", creator: user)
Current.user = user

uploads = {
  "moon.jpg" => "image/jpeg", "earth.png" => "image/png", "black_hole.jpg" => "image/jpeg",
  "alpha-centuri.mov" => "video/quicktime", "pixel.bmp" => "image/bmp"
}

messages = uploads.map do |name, content_type|
  message = room.messages.create_with_attachment!(attachment: upload(name, content_type), creator: user)
  attachment = message.attachment
  blob = attachment.blob
  vector = {
    fixture: name, declared_type: content_type, blob: blob_row(blob),
    attachment: attachment.attributes.slice("name", "record_type", "record_id", "blob_id"),
    variable: blob.variable?, previewable: blob.previewable?,
    rails_blob_path: rails_blob_path(attachment, only_path: true),
    rails_blob_download_path: rails_blob_path(attachment, disposition: "attachment", only_path: true),
    rails_blob_proxy_path: rails_storage_proxy_path(attachment, only_path: true),
    service_url: blob.url(expires_in: nil),
    service_url_attachment: blob.url(expires_in: nil, disposition: :attachment)
  }

  if blob.video?
    preview = attachment.preview(format: :webp).processed
    preview_image = blob.reload.preview_image
    poster = attachment.preview(format: :webp, resize_to_limit: [ Message::THUMBNAIL_MAX_WIDTH, Message::THUMBNAIL_MAX_HEIGHT ])
    vector[:preview_image] = { blob: blob_row(preview_image.blob), attachment: preview_image.attachment.attributes.slice("name", "record_type", "record_id", "blob_id"),
                               file: save_file("#{File.basename(name, ".*")}-preview_image.jpg", preview_image.blob) }
    vector[:variants] = [
      variant_vector("#{File.basename(name, ".*")}-preview-webp", preview_image.variant(format: :webp), preview_image.blob),
      variant_vector("#{File.basename(name, ".*")}-poster", preview_image.variant(format: :webp, resize_to_limit: [ 1200, 800 ]), preview_image.blob),
      variant_vector("#{File.basename(name, ".*")}-poster-from-key", preview_image.variant(ActiveStorage::Variation.decode(poster.variation.key).transformations), preview_image.blob)
    ]
    vector[:poster_path] = url_for(poster).then { URI(_1).path }
    vector[:poster_proxy_path] = rails_storage_proxy_path(poster, only_path: true)
  elsif blob.variable?
    thumb = attachment.representation(:thumb)
    vector[:thumb_path] = url_for(thumb).then { URI(_1).path }
    vector[:thumb_proxy_path] = rails_storage_proxy_path(thumb, only_path: true)
    vector[:variants] = [ variant_vector("#{File.basename(name, ".*")}-thumb", thumb, blob) ]
  end

  vector
end

user.avatar.attach(upload("earth.png", "image/png"))
avatar_variant = user.avatar.variant(:square)
avatar = { fixture: "earth.png", blob: blob_row(user.avatar.blob), variants: [ variant_vector("earth-avatar", avatar_variant, user.avatar.blob) ],
           path: url_for(avatar_variant).then { URI(_1).path } }

user.avatar.attach(upload("moon.jpg", "image/jpeg"))
avatar_jpg = { fixture: "moon.jpg", blob: blob_row(user.avatar.blob), variants: [ variant_vector("moon-avatar", user.avatar.variant(:square), user.avatar.blob) ] }

account.logo.attach(upload("black_hole.jpg", "image/jpeg"))
logo = { fixture: "black_hole.jpg", blob: blob_row(account.logo.blob), variants: %i[ large small ].map { variant_vector("black_hole-logo-#{_1}", account.logo.variant(_1), account.logo.blob) } }

weird = ActiveStorage::Filename.new("weird & <name> ünï.png")
verifier_vectors = {
  expiring: ActiveStorage.verifier.generate("x", expires_at: Time.utc(2030, 1, 2, 3, 4, Rational(5678, 1000)), purpose: "p"),
  disk_url_path: URI(ActiveStorage::Blob.service.url("abcdefghijklmnopqrstuvwxyz12", expires_in: nil, filename: weird, content_type: "image/png", disposition: :inline)).path,
  disk_url_path_nil_type: URI(ActiveStorage::Blob.service.url("abcdefghijklmnopqrstuvwxyz12", expires_in: nil, filename: weird, content_type: nil, disposition: :attachment)).path,
  direct_upload_path: URI(ActiveStorage::Blob.service.url_for_direct_upload("abcdefghijklmnopqrstuvwxyz12", expires_in: 1.minute, content_type: "image/png", content_length: 42, checksum: "abc==")).path
}

vectors = {
  verifier: verifier_vectors,
  generated_by: "reference-tools/storage/generate.rb",
  versions: {
    libvips: Vips.version_string, ruby_vips: Vips::VERSION, image_processing: ImageProcessing::VERSION, marcel: Marcel::VERSION,
    ffmpeg: `ffmpeg -version`.lines.first.strip, ffprobe: `ffprobe -version`.lines.first.strip, rails: Rails.version, ruby: RUBY_VERSION
  },
  video_preview_arguments: ActiveStorage.video_preview_arguments,
  secret_key_base_prefix: Rails.application.secret_key_base[0, 16],
  variations: variations, marcel: marcel, filenames: filenames,
  messages: messages, avatars: [ avatar, avatar_jpg ], logos: [ logo ]
}

File.write OUT.join("storage.json"), JSON.pretty_generate(vectors) + "\n"
puts "wrote #{OUT.join("storage.json")} and #{Dir.children(FILES_OUT).size} files in #{FILES_OUT}"

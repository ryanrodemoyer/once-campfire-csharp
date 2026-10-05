# Reads what OracleExportTests wrote with Active Storage itself, at the Rails revision
# reference/Gemfile.lock pins: a bare Rails app on the C#-written database and storage directory,
# with the parity secret_key_base and Campfire's `local` Disk service. For each blob it checks
# that Rails finds it by the C# signed id, that `blob.open` passes Active Storage's checksum
# verification, that key, metadata and columns read back as the reference writes them, and that
# Rails generates the same paths and disk URLs. Prints a report and exits non-zero on any mismatch.
#
#   bundle exec ruby read.rb <directory>
require "bundler/setup"
require "json"
require "logger"
require "rails"
require "active_record/railtie"
require "active_storage/engine"

DIR = File.expand_path(ARGV.fetch(0))
FIXTURES = File.expand_path("../../../reference/test/fixtures/files", __dir__)
ENV["DATABASE_URL"] = "sqlite3:#{File.join(DIR, "production.sqlite3")}"

class OracleApp < Rails::Application
  config.load_defaults 8.2
  config.eager_load = false
  config.logger = Logger.new(nil)
  config.secret_key_base = File.read(File.expand_path("../../../parity/.env.reference", __dir__))[/^SECRET_KEY_BASE=(\h+)$/, 1]
  config.active_storage.service = :local
  config.active_storage.service_configurations = { local: { service: "Disk", root: File.join(DIR, "storage", "files") } }
  # reference/config/initializers/vips.rb
  config.active_storage.variable_content_types -= %w[ image/bmp image/vnd.microsoft.icon image/vnd.adobe.photoshop ]
end
Rails.application.initialize!
ActiveStorage::Current.url_options = { host: "campfire.test", protocol: "http" }
include Rails.application.routes.url_helpers

manifest = JSON.parse(File.read(File.join(DIR, "manifest.json")))
failures = []
check = ->(label, expected, actual) do
  ok = expected == actual
  failures << "#{label}: expected #{expected.inspect}, got #{actual.inspect}" unless ok
  puts "#{ok ? "ok  " : "FAIL"} #{label}"
end

manifest.fetch("blobs").each do |c|
  name = "blob #{c["id"]} (#{c["fixture"]})"
  blob = ActiveStorage::Blob.find_signed!(c["signed_id"])
  check.("#{name} find_signed!", c["id"], blob.id)
  check.("#{name} key", c["key"], blob.key)
  check.("#{name} metadata", { "identified" => true }, blob.metadata)
  check.("#{name} identified?", true, blob.identified?)
  check.("#{name} filename", c["filename"], blob[:filename])
  check.("#{name} content_type", c["content_type"], blob.content_type)
  check.("#{name} byte_size", c["byte_size"], blob.byte_size)
  check.("#{name} checksum", c["checksum"], blob.checksum)
  check.("#{name} service_name", "local", blob.service_name)
  check.("#{name} created_at", c["created_at"], blob.created_at.utc.iso8601(6).sub("+00:00", "Z"))
  check.("#{name} path_for", File.join(blob.service.root, c["path"]), blob.service.path_for(blob.key))
  opened = blob.open { |file| file.read } # raises ActiveStorage::IntegrityError on a checksum mismatch
  check.("#{name} open (checksum verified)", File.binread(File.join(FIXTURES, c["fixture"])), opened)
  check.("#{name} blob valid?", true, blob.valid?)

  attachment = ActiveStorage::Attachment.find(c["attachment"]["id"])
  check.("#{name} attachment", c["attachment"].merge("blob_id" => blob.id),
    attachment.attributes.slice("id", "name", "record_type", "record_id", "blob_id"))

  check.("#{name} rails_blob_path", c["rails_blob_path"], rails_blob_path(blob, only_path: true))
  check.("#{name} rails_blob_path disposition", c["rails_blob_download_path"], rails_blob_path(blob, disposition: "attachment", only_path: true))
  check.("#{name} rails_storage_proxy_path", c["rails_blob_proxy_path"], rails_storage_proxy_path(blob, only_path: true))
  check.("#{name} url", c["service_url"], blob.url(expires_in: nil))
  check.("#{name} url attachment", c["service_url_attachment"], blob.url(expires_in: nil, disposition: :attachment))
end

direct = manifest.fetch("direct_upload")
expected = URI(ActiveStorage::Blob.service.url_for_direct_upload("abcdefghijklmnopqrstuvwxyz12",
  expires_in: Time.iso8601(direct["expires_at"]) - Time.now, content_type: "image/png", content_length: 42, checksum: "abc==")).path
token = URI.decode_uri_component(direct["path"].split("/").last)
check.("direct upload token verifies", { "key" => "abcdefghijklmnopqrstuvwxyz12", "content_type" => "image/png",
  "content_length" => 42, "checksum" => "abc==", "service_name" => "local" }, ActiveStorage.verifier.verified(token, purpose: :blob_token)&.stringify_keys)
check.("direct upload path shape", expected.split("/")[0..-2], direct["path"].split("/")[0..-2])

if failures.empty?
  puts "Active Storage #{ActiveStorage.gem_version} read #{manifest["blobs"].size} C#-written blobs: all checks passed"
else
  puts failures
  exit 1
end

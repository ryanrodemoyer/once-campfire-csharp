# Compares regenerated vectors with the committed ones, for reference-tools/regenerate:
#
#   ruby compare.rb VECTORS_DIR GENERATED_DIR RUNTIME
#
# Prints one line per file in VECTORS_DIR/MANIFEST and exits 1 if any differs. A file is
#   same      byte-identical
#   varies    equal except for values that change on every run: drawn at random, or derived from the
#             wall clock (listed below)
#   media     equal except for libvips/ffmpeg output, which only the Docker image makes
#             canonically (native runtime only; under Docker these count as DIFFERS)
#   DIFFERS   anything else
#   missing   not generated (a family that wasn't run or was skipped)
require "json"

vectors, generated, runtime = ARGV

# JSON paths (keys joined with ".", arrays as "[]") whose values change on every run.
VARYING = {
  # AES-256-GCM cookies get a random IV; CSRF tokens a random one-time pad; session ids and
  # has_secure_token keys are SecureRandom; bcrypt digests a random salt.
  "rails_compat.json" => %w[
    .encrypted_cookies.verify[].raw .encrypted_cookies.generate[].raw .encrypted_cookies.generate[].set_cookie
    .csrf.global_tokens[] .csrf.form_tokens[].token .csrf.generated_session_token_example .csrf.validity[].token
    .session.session_cookie_raw .session.session_token_raw .session.session_after_login.session_id
    .session.session_after_login._csrf_token .session.csrf_meta_token .session.session_form_token
    .session.session.session_id .session.session._csrf_token .session.set_cookie .session.session_token_value
    .session.session_after_login_raw .session.session_token_set_cookie
    .passwords.checks[].digest .passwords.seeded_user_digest .passwords.digests[].digest
  ],
  # Blob keys (and the paths and service URLs built from them) are has_secure_token; the direct
  # upload token expires relative to the real clock.
  "storage.json" => %w[
    .avatars[].blob.key .avatars[].blob.path .avatars[].variants[].blob.key .avatars[].variants[].blob.path
    .logos[].blob.key .logos[].blob.path .logos[].variants[].blob.key .logos[].variants[].blob.path
    .messages[].blob.key .messages[].blob.path .messages[].variants[].blob.key .messages[].variants[].blob.path
    .messages[].preview_image.blob.key .messages[].preview_image.blob.path
    .messages[].service_url .messages[].service_url_attachment .verifier.direct_upload_path
  ],
  # Permanent cookies expire 20 years from the real clock.
  "campfire_sessions.json" => %w[
    .sessions[].cookie_value .sessions[].cookie_header .forged.cookie_value .forged.cookie_header
  ]
}

# Values that change wherever they appear: the oracles' local servers listen on any free port.
VARYING_VALUES = { "webhook/expected.json" => /\A127\.0\.0\.1:\d+\z/, "opengraph/expected.json" => /\A127\.0\.0\.1:\d+\z/ }

MEDIA = {
  "storage.json" => %w[.versions.libvips .versions.ffmpeg .versions.ffprobe .messages[].preview_image.blob.checksum]
}
MEDIA_FILES = %r{\Astorage/}

# Walks two parsed documents in step, yielding [path, expected, actual] for every leaf that differs
# (or every node whose shape differs).
def differences(expected, actual, path = "", &block)
  if expected.is_a?(Hash) && actual.is_a?(Hash) && expected.keys.sort == actual.keys.sort
    expected.each { |key, value| differences(value, actual[key], "#{path}.#{key}", &block) }
  elsif expected.is_a?(Array) && actual.is_a?(Array) && expected.size == actual.size
    expected.zip(actual) { |e, a| differences(e, a, "#{path}[]", &block) }
  elsif expected != actual
    yield path, expected, actual
  end
end

def classify(name, expected_bytes, actual_bytes, runtime)
  return "same" if expected_bytes == actual_bytes
  return (runtime == "native" ? "media" : "DIFFERS") if name.match?(MEDIA_FILES)
  return "DIFFERS" unless name.end_with?(".json")

  kinds = []
  differences(JSON.parse(expected_bytes), JSON.parse(actual_bytes)) do |path, e, a|
    pattern = VARYING_VALUES[name]
    kinds << if VARYING.fetch(name, []).include?(path) || (pattern && e.to_s.match?(pattern) && a.to_s.match?(pattern))
      "varies"
    elsif MEDIA.fetch(name, []).include?(path) && runtime == "native"
      "media"
    else
      "DIFFERS"
    end
  end
  %w[DIFFERS media varies].find { |kind| kinds.include?(kind) } || "DIFFERS"
end

differs = false
File.readlines(File.join(vectors, "MANIFEST"), chomp: true).each do |line|
  next if line.start_with?("#")
  name = line.split[1]
  next if name.end_with?("/inputs.yml", "/cases.json") # generator inputs, not outputs

  actual = File.join(generated, name)
  status = File.exist?(actual) ? classify(name, File.binread(File.join(vectors, name)), File.binread(actual), runtime) : "missing"
  differs ||= status == "DIFFERS"
  puts format("%-8s %s", status, name)
end

exit(differs ? 1 : 0)

# What the reference answers for messages#index and rooms/refreshes#show: each response's status,
# headers, cookies and body. Writes pages.json, which IndexControllerTests replays through the C#
# router and controllers on the same database.
#
# The database is D02's tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 (the default parity
# seed). Run this script in the reference app on a copy of it, with the clock frozen:
#
#   mkdir -p tmp/m04/db tmp/m04/storage
#   cp tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 tmp/m04/db/production.sqlite3
#   parity/bin/reference runner --storage tmp/m04 --time 2026-03-02T16:00:00Z --freeze \
#     tests/Campfire.Web.Tests/Controllers/Messages/Vectors/index.rb
# The script writes pages.json itself (production logs share stdout). M05's generator stays
# generate.rb.
#
# Requests go straight to Rails.application (the stack Puma runs), in order, on one database.
# The fixtures below are applied first, and the C# test applies the same SQL to its copy.
require "json"

# RecordNotFound is logged while the missing-message cases run, and production logs to stdout.
Rails.logger = ActiveSupport::Logger.new(File::NULL)
ActiveRecord::Base.logger = nil if defined?(ActiveRecord)

HOST = "campfire.test"
NOW = Time.now.utc

FIXTURES = [
  # Kevin, a member, signed in and active just now (no activity refresh).
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (900001, 712064548, 'KevinSessionToken0000001', '198.51.100.7', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')"
]
FIXTURES.each { |sql| ActiveRecord::Base.connection.execute(sql) }

DAVID = "AxJs94fteQ5Autv2VrKsH68c" # administrator; last active 14:00, so the first request refreshes it
KEVIN = "KevinSessionToken0000001"

HQ = 201306877           # open; no messages; Kevin is in it
ALL_TALK = 486777696     # closed; the busy room (watercooler)
DESIGNERS = 654632876    # closed; attachments, mentions and boosts; an edited message
DAVID_JASON = 186869642  # direct
BUSY_060 = 933434569

AGENTS = {
  "Chrome on macOS" => "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
}
PAGE = "text/html, application/xhtml+xml"
TURBO = "text/vnd.turbo-stream.html, text/html, application/xhtml+xml"

def jar
  ActionDispatch::Request.new(Rails.application.env_config.merge("HTTP_HOST" => HOST, "rack.input" => StringIO.new)).cookie_jar
end

def session_token(token)
  cookies = jar
  cookies.signed.permanent[:session_token] = { value: token, httponly: true, same_site: :lax }
  "session_token=#{Rack::Utils.escape(cookies[:session_token])}"
end

def signed_in(token, extra = {})
  { "Cookie" => session_token(token), "User-Agent" => AGENTS.fetch("Chrome on macOS"), "Accept" => PAGE }.merge(extra)
end

# messages#index's fresh_when etag, captured while the acceptance request runs, so the constant
# the port uses is the reference's digest rather than a guess.
module IndexEtagProbe
  def index
    if $index_etag_probe.nil? && params[:before].to_s == BUSY_060.to_s
      $index_etag_probe = probe_index_etag
    end
    super
  end

  def probe_index_etag
    messages = send(:find_paged_messages)
    template = lookup_context.find_all("index", _prefixes).first
    digest = template && ActionView::Digestor.digest(name: template.virtual_path, finder: lookup_context)
    digest_html = template && ActionView::Digestor.digest(name: template.virtual_path, format: :html, finder: lookup_context)
    hex = ->(parts) { ActiveSupport::Digest.hexdigest(ActiveSupport::Cache.expand_cache_key(parts)) }
    {
      "virtual_path" => template&.virtual_path,
      "formats" => lookup_context.formats.map(&:to_s),
      "digest" => digest,
      "digest_html" => digest_html,
      "count" => messages.size,
      "class" => messages.class.name,
      "respond_to_maximum" => messages.respond_to?(:maximum),
      "respond_to_updated_at" => messages.respond_to?(:updated_at),
      "ids" => messages.map(&:id),
      "hex_messages" => hex.call([ messages ]),
      "hex_messages_digest" => hex.call([ messages, digest ]),
      "hex_messages_digest_html" => hex.call([ messages, digest_html ]),
      "key_head" => ActiveSupport::Cache.expand_cache_key([ messages, digest ])[0, 180]
    }
  rescue => error
    { "error" => "#{error.class}: #{error.message}", "backtrace" => error.backtrace.first(8) }
  end
end
MessagesController.prepend(IndexEtagProbe)

talk = Message.where(room_id: ALL_TALK).order(:created_at)
FIRST_ID = talk.first.id
LAST_ID = talk.last.id
OTHER_ID = Message.where(room_id: DESIGNERS).order(:created_at).first.id
DESIGNERS_SINCE = Time.utc(2026, 3, 1, 12, 45, 0).to_fs(:epoch)
BUSY_SINCE = Message.find(BUSY_060).created_at.to_fs(:epoch)
NOW_SINCE = NOW.to_fs(:epoch)

ACCEPTANCE = "/rooms/#{ALL_TALK}/messages?before=#{BUSY_060}"

# [name, path, headers]
CASES = [
  [ "page before busy_060", ACCEPTANCE, signed_in(DAVID) ],
  [ "page before busy_060, no Accept", ACCEPTANCE, { "Cookie" => session_token(DAVID), "User-Agent" => AGENTS.fetch("Chrome on macOS") } ],
  [ "page before busy_060, any Accept", ACCEPTANCE, signed_in(DAVID, "Accept" => "*/*") ],
  [ "page before busy_060, paginator", ACCEPTANCE, signed_in(DAVID, "X-Requested-With" => "XMLHttpRequest") ],
  [ "the last page", "/rooms/#{ALL_TALK}/messages", signed_in(DAVID) ],
  [ "a page after busy_060", "/rooms/#{ALL_TALK}/messages?after=#{BUSY_060}", signed_in(DAVID) ],
  [ "before and after, so before wins", "/rooms/#{ALL_TALK}/messages?before=#{BUSY_060}&after=#{BUSY_060}", signed_in(DAVID) ],
  [ "a blank before, so the last page", "/rooms/#{ALL_TALK}/messages?before=", signed_in(DAVID) ],
  [ "a page before the first message", "/rooms/#{ALL_TALK}/messages?before=#{FIRST_ID}", signed_in(DAVID) ],
  [ "a page after the last message", "/rooms/#{ALL_TALK}/messages?after=#{LAST_ID}", signed_in(DAVID) ],
  [ "before a message from another room", "/rooms/#{ALL_TALK}/messages?before=#{OTHER_ID}", signed_in(DAVID) ],
  [ "before a missing message", "/rooms/#{ALL_TALK}/messages?before=1", signed_in(DAVID) ],
  [ "before a message id that isn't a number", "/rooms/#{ALL_TALK}/messages?before=latest", signed_in(DAVID) ],
  [ "an empty room", "/rooms/#{HQ}/messages", signed_in(KEVIN) ],
  [ "an empty room as JSON", "/rooms/#{HQ}/messages.json", signed_in(KEVIN) ],
  [ "a room the member isn't in", "/rooms/#{ALL_TALK}/messages", signed_in(KEVIN) ],
  [ "messages, signed out", "/rooms/#{ALL_TALK}/messages", { "User-Agent" => AGENTS.fetch("Chrome on macOS"), "Accept" => PAGE } ],
  [ "messages as JSON", "/rooms/#{ALL_TALK}/messages.json", signed_in(DAVID) ],
  [ "messages in a Turbo frame", ACCEPTANCE, signed_in(DAVID, "Turbo-Frame" => "messages") ],
  [ "messages with no room", "/messages", signed_in(DAVID) ],
  [ "a direct room's last page", "/rooms/#{DAVID_JASON}/messages", signed_in(DAVID) ],
  [ "a room with attachments, mentions and boosts", "/rooms/#{DESIGNERS}/messages", signed_in(DAVID) ],
  [ "a member's view of attachments, mentions and boosts", "/rooms/#{DESIGNERS}/messages", signed_in(KEVIN) ],

  [ "refresh with new and updated messages", "/rooms/#{DESIGNERS}/refresh?since=#{DESIGNERS_SINCE}", signed_in(DAVID, "Accept" => TURBO) ],
  [ "refresh as turbo_stream", "/rooms/#{DESIGNERS}/refresh.turbo_stream?since=#{DESIGNERS_SINCE}", signed_in(DAVID) ],
  [ "refresh since the epoch", "/rooms/#{DESIGNERS}/refresh?since=0", signed_in(DAVID, "Accept" => TURBO) ],
  [ "refresh since now", "/rooms/#{DESIGNERS}/refresh?since=#{NOW_SINCE}", signed_in(DAVID, "Accept" => TURBO) ],
  [ "refresh with no since", "/rooms/#{DESIGNERS}/refresh", signed_in(DAVID, "Accept" => TURBO) ],
  [ "refresh with a blank since", "/rooms/#{DESIGNERS}/refresh?since=", signed_in(DAVID, "Accept" => TURBO) ],
  [ "refresh with a since that isn't a number", "/rooms/#{DESIGNERS}/refresh?since=later", signed_in(DAVID, "Accept" => TURBO) ],
  [ "refresh as HTML", "/rooms/#{DESIGNERS}/refresh?since=#{DESIGNERS_SINCE}", signed_in(DAVID) ],
  [ "refresh of a room the member isn't in", "/rooms/#{ALL_TALK}/refresh?since=0", signed_in(KEVIN, "Accept" => TURBO) ],
  [ "refresh, signed out", "/rooms/#{DESIGNERS}/refresh?since=0", { "User-Agent" => AGENTS.fetch("Chrome on macOS"), "Accept" => TURBO } ],
  [ "refresh of an empty room", "/rooms/#{HQ}/refresh?since=0", signed_in(KEVIN, "Accept" => TURBO) ],
  [ "refresh the way the room asks", "/rooms/#{DESIGNERS}/refresh?since=#{DESIGNERS_SINCE}", signed_in(DAVID, "Accept" => TURBO, "X-Requested-With" => "XMLHttpRequest") ],
  [ "refresh for anything", "/rooms/#{DESIGNERS}/refresh?since=#{DESIGNERS_SINCE}", signed_in(DAVID, "Accept" => "*/*") ],
  [ "refresh of messages since busy_060", "/rooms/#{ALL_TALK}/refresh?since=#{BUSY_SINCE}", signed_in(DAVID, "Accept" => TURBO) ]
]

RESPONSE_HEADERS = %w[ content-type location vary cache-control etag set-cookie x-frame-options ]

def perform(name, path, headers)
  env = Rack::MockRequest.env_for("http://#{HOST}#{path}", method: "GET")
  env["REMOTE_ADDR"] = "198.51.100.7"
  headers.each { |key, value| env["HTTP_#{key.upcase.tr("-", "_")}"] = value }

  status, response_headers, response_body = Rails.application.call(env)
  text = +""
  response_body.each { |chunk| text << chunk }
  response_body.close if response_body.respond_to?(:close)

  {
    "name" => name,
    "request" => { "method" => "GET", "path" => path, "headers" => headers },
    "response" => {
      "status" => status,
      "headers" => response_headers.to_h.transform_keys(&:downcase).slice(*RESPONSE_HEADERS),
      "body" => text.force_encoding("UTF-8")
    }
  }
end

results = CASES.map { |name, path, headers| perform(name, path, headers) }

acceptance = results.find { |result| result["name"] == "page before busy_060" }
etag = acceptance["response"]["headers"]["etag"]
results << perform("page before busy_060, fresh", ACCEPTANCE, signed_in(DAVID, "If-None-Match" => etag))
results << perform("page before busy_060, stale", ACCEPTANCE, signed_in(DAVID, "If-None-Match" => 'W/"00000000000000000000000000000000"'))

if $index_etag_probe
  $index_etag_probe["response_etag"] = etag
  $index_etag_probe["last_modified"] = acceptance["response"]["headers"]["last-modified"]
  $index_etag_probe["cache_control"] = acceptance["response"]["headers"]["cache-control"]
  %w[ hex_messages hex_messages_digest hex_messages_digest_html ].each do |key|
    $index_etag_probe["match_#{key}"] = etag == %(W/"#{$index_etag_probe[key]}")
  end
end

def seams(body)
  {
    "bytes" => body.bytesize,
    "head" => body[0, 160].inspect,
    "tail" => (body[-80, 80] || body).inspect,
    "between" => body.scan(/<\/turbo-stream>(.*?)<turbo-stream/m).map { |seam| seam[0].inspect },
    "template" => body[/<template>(.{0,60})/m, 1]&.inspect
  }
end

notes = {
  "first_id" => FIRST_ID,
  "last_id" => LAST_ID,
  "other_id" => OTHER_ID,
  "designers_since" => DESIGNERS_SINCE,
  "busy_since" => BUSY_SINCE,
  "now_since" => NOW_SINCE,
  "index_etag" => $index_etag_probe,
  "streams" => results.select { |result| result["name"].start_with?("refresh") }.to_h { |result| [ result["name"], seams(result["response"]["body"]).merge("status" => result["response"]["status"], "etag" => result["response"]["headers"]["etag"], "content_type" => result["response"]["headers"]["content-type"], "cache_control" => result["response"]["headers"]["cache-control"]) ] }
}
root = ENV.fetch("PARITY_WORK", ".")
File.write(File.join(root, "tmp/m04/notes.json"), JSON.pretty_generate(notes))
File.write(File.join(root, "tests/Campfire.Web.Tests/Controllers/Messages/Vectors/pages.json"), JSON.pretty_generate("now" => NOW.iso8601, "fixtures" => FIXTURES, "cases" => results))

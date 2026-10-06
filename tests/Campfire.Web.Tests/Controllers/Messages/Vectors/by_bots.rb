# What the reference does through the bot API, Messages::ByBotsController and
# Messages::Boosts::ByBotsController: each response, the Action Cable broadcasts and jobs it made,
# and every row it changed. Writes by_bots.json, which ByBotsReplayTests replays through the C#
# router and controllers on the same database.
#
# The database is D02's tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 (the default parity
# seed). Run this script in the reference app on a copy of it, with the clock frozen:
#
#   mkdir -p tmp/i05/db tmp/i05/storage
#   cp tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 tmp/i05/db/production.sqlite3
#   parity/bin/reference runner -e RAILS_LOG_LEVEL=fatal --storage tmp/i05 --time 2026-03-02T16:00:00Z --freeze \
#     tests/Campfire.Web.Tests/Controllers/Messages/Vectors/by_bots.rb > tests/Campfire.Web.Tests/Controllers/Messages/Vectors/by_bots.json
#
# Requests go straight to Rails.application (the stack Puma runs), in order, on one database, as a
# bot's client sends them: no cookies, curl's form Content-Type unless another is given.
require "json"

HOST = "campfire.test"
NOW = Time.now.utc

BENDER = "394959859-BenderBot123"   # in All Talk (131 messages), Archive (none) and a direct room with Kevin; has a webhook
DEPLOY = "773523956-DeployBot456"   # in Designers, Archive, All Pets and HQ; no webhook
OLD_BOT = "773523957-OldBot789abc"  # deactivated
KEVIN_AS_BOT = "712064548-"         # a member's id with an empty token

ALL_TALK = 486777696
DESIGNERS = 654632876
ARCHIVE = 699448327
BENDER_AND_KEVIN = 340026324

BENDERS_MESSAGE = 933434630   # Bender's, in All Talk
JZS_MESSAGE = 136976342       # JZ's, in All Talk (the newest there)
OLDEST_IN_ALL_TALK = 933434510
DESIGNERS_MESSAGE = 309456473 # Jason's, in Designers
BENDERS_BOOST = 329428235     # on 933434481, in All Talk
JASONS_BOOST = 136976342      # on 136976342, in All Talk

CURL = { "User-Agent" => "curl/8.5.0", "Accept" => "*/*" }

def mention(user_id) = ActionText::Attachment.from_attachable(User.find(user_id)).to_html

# Bender's newest message in All Talk, made below.
def benders_latest = Message.where(creator_id: 394959859, room_id: ALL_TALK).order(:id).last.id

# The first message of a page, which the Link header pages back from.
def page_start(page) = page.to_a.first.id

# Bender's boosts made below, by content.
def boosted(content) = Boost.where(booster_id: 394959859, content: content).order(:id).last.id

def bot_messages(room, key, query = nil) = "/rooms/#{room}/#{key}/messages#{query && "?#{query}"}"

# [name, method, path (or a proc for one), headers, body]
CASES = [
  # index
  ["index, the last page", "GET", bot_messages(ALL_TALK, BENDER), CURL],
  ["index, the page before", "GET", -> { bot_messages(ALL_TALK, BENDER, "before=#{page_start(Room.find(ALL_TALK).messages.last_page)}") }, CURL],
  ["index, the first page", "GET", -> { bot_messages(ALL_TALK, BENDER, "before=#{Room.find(ALL_TALK).messages.ordered.second.id}") }, CURL],
  ["index after a message", "GET", bot_messages(ALL_TALK, BENDER, "after=#{OLDEST_IN_ALL_TALK}"), CURL],
  ["index after the newest message", "GET", bot_messages(ALL_TALK, BENDER, "after=#{JZS_MESSAGE}"), CURL],
  ["index, blank before", "GET", bot_messages(ALL_TALK, BENDER, "before="), CURL],
  ["index before a message in another room", "GET", bot_messages(ALL_TALK, BENDER, "before=#{DESIGNERS_MESSAGE}"), CURL],
  ["index before a missing message", "GET", bot_messages(ALL_TALK, BENDER, "before=1"), CURL],
  ["index of a direct room", "GET", bot_messages(BENDER_AND_KEVIN, BENDER), CURL],
  ["index of a room with no messages", "GET", bot_messages(ARCHIVE, BENDER), CURL],
  ["index of a room the bot isn't in", "GET", bot_messages(DESIGNERS, BENDER), CURL],
  ["index of a missing room", "GET", bot_messages(1, BENDER), CURL],
  ["index from a browser", "GET", bot_messages(ARCHIVE, BENDER), { "Accept" => "text/html,application/xhtml+xml", "User-Agent" => "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36" }],
  ["index of a room with no messages as HTML", "GET", "/rooms/#{ARCHIVE}/#{BENDER}/messages.html", CURL],
  ["index as HTML", "GET", "/rooms/#{BENDER_AND_KEVIN}/#{BENDER}/messages.html", CURL],
  ["index with an invalid bot key", "GET", bot_messages(ALL_TALK, "invalid-bot-key"), CURL],
  ["index with a deactivated bot's key", "GET", bot_messages(ALL_TALK, OLD_BOT), CURL],
  ["index with a member's id as the key", "GET", bot_messages(BENDER_AND_KEVIN, KEVIN_AS_BOT), CURL],

  # create
  ["create", "POST", bot_messages(ALL_TALK, BENDER), CURL, "Hello Bot World!"],
  ["create with UTF-8", "POST", bot_messages(ALL_TALK, BENDER), CURL, "Hello 👋!"],
  ["create as text/plain", "POST", bot_messages(ARCHIVE, BENDER), CURL.merge("Content-Type" => "text/plain"), "Plain & simple <3"],
  ["create as JSON", "POST", bot_messages(ARCHIVE, BENDER), CURL.merge("Content-Type" => "application/json"), %({"text":"Hi"})],
  ["create with HTML", "POST", bot_messages(ARCHIVE, BENDER), CURL, "<div>Deployed <b>v2</b> <script>alert(1)</script><a href=\"javascript:alert(1)\" onclick=\"x()\">here</a></div>"],
  ["create with what parses as a form", "POST", bot_messages(ARCHIVE, BENDER), CURL, "status=ok&count=2"],
  ["create with an empty body", "POST", bot_messages(ALL_TALK, BENDER), CURL, ""],
  ["create with a blank body", "POST", bot_messages(ALL_TALK, BENDER), CURL, "   \n"],
  ["create with a Unicode-blank body", "POST", bot_messages(ALL_TALK, BENDER), CURL, " 　"],
  ["create in a direct room", "POST", bot_messages(BENDER_AND_KEVIN, BENDER), CURL, "Talking to myself again!"],
  ["create mentioning a bot with a webhook", "POST", bot_messages(ARCHIVE, DEPLOY), CURL, -> { "<div>Hey #{mention(394959859)}, deploy?</div>" }],
  ["create mentioning itself", "POST", bot_messages(ARCHIVE, BENDER), CURL, -> { "<div>I am #{mention(394959859)}</div>" }],
  ["create in a room the bot isn't in", "POST", bot_messages(DESIGNERS, BENDER), CURL, "Hello!"],
  ["create with an invalid bot key", "POST", bot_messages(ALL_TALK, "invalid-bot-key"), CURL, "Hello!"],
  ["create with a member's id as the key", "POST", bot_messages(BENDER_AND_KEVIN, KEVIN_AS_BOT), CURL, "Hello 👋!"],
  ["index after creating", "GET", bot_messages(ALL_TALK, BENDER), CURL],

  # update
  ["update", "PATCH", -> { "#{bot_messages(ALL_TALK, BENDER)}/#{BENDERS_MESSAGE}" }, CURL, "Deployed."],
  ["update with UTF-8", "PUT", -> { "#{bot_messages(ALL_TALK, BENDER)}/#{BENDERS_MESSAGE}" }, CURL, "Deployed 🚀!"],
  ["update to the same body", "PATCH", -> { "#{bot_messages(ALL_TALK, BENDER)}/#{BENDERS_MESSAGE}" }, CURL, "Deployed 🚀!"],
  ["update as HTML", "PATCH", -> { "/rooms/#{ALL_TALK}/#{BENDER}/messages/#{benders_latest}.html" }, CURL, "Redirected"],
  ["update someone else's message", "PATCH", "#{bot_messages(ALL_TALK, BENDER)}/#{JZS_MESSAGE}", CURL, "Hijacked!"],
  ["update in a room the bot isn't in", "PATCH", "#{bot_messages(DESIGNERS, BENDER)}/#{DESIGNERS_MESSAGE}", CURL, "Hijacked!"],
  ["update a message of another room", "PATCH", "#{bot_messages(ALL_TALK, BENDER)}/#{DESIGNERS_MESSAGE}", CURL, "Hijacked!"],
  ["update a missing message", "PATCH", "#{bot_messages(ALL_TALK, BENDER)}/1", CURL, "Hijacked!"],
  ["update with a member's id as the key", "PATCH", "#{bot_messages(ALL_TALK, "773523953-")}/#{JZS_MESSAGE}", CURL, "Hijacked!"],

  # destroy
  ["destroy", "DELETE", -> { "#{bot_messages(ALL_TALK, BENDER)}/#{benders_latest}" }, CURL],
  ["destroy someone else's message", "DELETE", "#{bot_messages(ALL_TALK, BENDER)}/#{JZS_MESSAGE}", CURL],
  ["destroy a missing message", "DELETE", "#{bot_messages(ALL_TALK, BENDER)}/1", CURL],

  # boosts
  ["boost", "POST", "#{bot_messages(ALL_TALK, BENDER)}/#{JZS_MESSAGE}/boosts", CURL, "👀"],
  ["boost with text", "POST", "#{bot_messages(ALL_TALK, BENDER)}/#{JZS_MESSAGE}/boosts", CURL.merge("Content-Type" => "text/plain"), "<b>Nice!</b>"],
  ["boost with an empty body", "POST", "#{bot_messages(ALL_TALK, BENDER)}/#{JZS_MESSAGE}/boosts", CURL, ""],
  ["boost with a blank body", "POST", "#{bot_messages(ALL_TALK, BENDER)}/#{JZS_MESSAGE}/boosts", CURL, "   "],
  ["boost in a room the bot isn't in", "POST", "#{bot_messages(DESIGNERS, BENDER)}/#{DESIGNERS_MESSAGE}/boosts", CURL, "👀"],
  ["boost a message outside the room", "POST", "#{bot_messages(ALL_TALK, BENDER)}/#{DESIGNERS_MESSAGE}/boosts", CURL, "👀"],
  ["boost with an invalid bot key", "POST", "#{bot_messages(ALL_TALK, "invalid-bot-key")}/#{JZS_MESSAGE}/boosts", CURL, "👀"],
  ["boost with a member's id as the key", "POST", "#{bot_messages(BENDER_AND_KEVIN, KEVIN_AS_BOT)}/#{JZS_MESSAGE}/boosts", CURL, "👀"],
  ["unboost", "DELETE", -> { "#{bot_messages(ALL_TALK, BENDER)}/#{JZS_MESSAGE}/boosts/#{boosted("👀")}" }, CURL],
  ["unboost a seeded boost", "DELETE", "#{bot_messages(ALL_TALK, BENDER)}/933434481/boosts/#{BENDERS_BOOST}", CURL],
  ["unboost someone else's boost", "DELETE", "#{bot_messages(ALL_TALK, BENDER)}/#{JZS_MESSAGE}/boosts/#{JASONS_BOOST}", CURL],
  ["unboost a missing boost", "DELETE", "#{bot_messages(ALL_TALK, BENDER)}/#{JZS_MESSAGE}/boosts/1", CURL],
  ["unboost with an invalid bot key", "DELETE", -> { "#{bot_messages(ALL_TALK, "invalid-bot-key")}/#{JZS_MESSAGE}/boosts/#{boosted("<b>Nice!</b>")}" }, CURL],

  # A bot key doesn't open the rest of the app
  ["the rooms page with a bot key", "GET", "/rooms/#{ALL_TALK}?bot_key=#{BENDER}", CURL]
]

FORM = "application/x-www-form-urlencoded"

# Every row of the tables a message write touches, keyed by id.
TABLES = %w[ messages rooms memberships boosts action_text_rich_texts ]

def snapshot
  connection = ActiveRecord::Base.connection
  state = TABLES.to_h do |table|
    [ table, connection.select_all("SELECT * FROM #{table}").rows.to_h { |row| [ row.first, row ] } ]
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

events = []
ActiveSupport::Notifications.subscribe("broadcast.action_cable") do |*, payload|
  message = payload[:coder] ? payload[:coder].encode(payload[:message]) : payload[:message]
  events << { "broadcast" => payload[:broadcasting], "payload" => message }
end
ActiveSupport::Notifications.subscribe("enqueue.active_job") do |*, payload|
  job = payload[:job]
  events << { "job" => job.class.name, "arguments" => job.arguments.map { |argument| argument.respond_to?(:to_global_id) ? argument.to_global_id.to_s : argument } }
end

RESPONSE_HEADERS = %w[ content-type location vary cache-control etag set-cookie x-frame-options x-total-count link ]

results = CASES.map do |name, method, path, headers, body|
  path = path.call if path.respond_to?(:call)
  body = body.call if body.respond_to?(:call)
  headers = headers.dup
  content_type = headers.delete("Content-Type") || (body ? FORM : nil)

  env = Rack::MockRequest.env_for("http://#{HOST}#{path}", method: method, input: body || "")
  env["CONTENT_TYPE"] = content_type if content_type
  env["REMOTE_ADDR"] = "198.51.100.7"
  headers.each { |key, value| env["HTTP_#{key.upcase.tr("-", "_")}"] = value }

  # The clock is frozen, so a message updated twice keeps its cache key and `json.cache!` would
  # serve the first update's JSON. Fragment caching isn't modeled (M02's gap): start each request cold.
  Rails.cache.clear
  events.clear
  before = snapshot
  status, response_headers, response_body = Rails.application.call(env)
  text = +""
  response_body.each { |chunk| text << chunk }
  response_body.close if response_body.respond_to?(:close)
  after = snapshot

  {
    "name" => name,
    "request" => { "method" => method, "path" => path, "headers" => headers, "content_type" => content_type, "body" => body },
    "response" => {
      "status" => status,
      "headers" => response_headers.to_h.transform_keys(&:downcase).slice(*RESPONSE_HEADERS),
      "body" => text.force_encoding("UTF-8")
    },
    "events" => events.dup,
    "changes" => changes(before, after)
  }
end

puts JSON.pretty_generate("now" => NOW.iso8601, "cases" => results)

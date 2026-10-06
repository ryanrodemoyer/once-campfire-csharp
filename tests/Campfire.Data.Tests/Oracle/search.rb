# Runs the reference's own search code with Active Record at the reference's pinned Rails revision
# and writes what it does, for SearchOracleTests to compare MessageSearch, SearchQuery and
# RecentSearches with:
#
#   search.json  { "queries": [{ "q", "query", "present" }],
#                  "searches": [{ "user", "q", "result" | "error" }],
#                  "crowded": { "messages": [...], "searches": [...] },
#                  "recent": [{ "step", "user", "q", "at", "searches" | "error" }] }
#
#   - queries: SearchesController#query (`params[:q]&.gsub(/[^[:word:]]/, " ")`) and `present?`
#     for every q in a seeded random corpus: seed words, FTS5 operators and syntax, punctuation,
#     mixed scripts, combining marks, emoji, and code points from every plane.
#   - searches: `set_messages`, `user.reachable_messages.search(query).last_page_of(100)` (ids),
#     for every user in the parity seed and every present query; or the SQLite error it raises.
#   - crowded: 150 more messages, some created at the same second, and searches over them, for the
#     100 limit and `created_at` ties the seed doesn't have.
#   - recent: Search.record, Search.ordered and `searches.destroy_all`, step by step on a copy of
#     rails-fixtures.sqlite3, at each step's (whole-second, as travel_to freezes it) time; the
#     user's searches after each step.
#
#   cd tests/Campfire.Data.Tests/Oracle && bundle install && bundle exec ruby search.rb
#
# It only reads parity-seed.sqlite3 and rails-fixtures.sqlite3. It needs the reference's Ruby
# (reference/.ruby-version), whose Onigmo decides what `[[:word:]]` is.
Encoding.default_external = Encoding::UTF_8
Encoding.default_internal = Encoding::UTF_8
ENV["TZ"] = "UTC"

require "json"
require "tmpdir"
require "fileutils"
require "active_record"
require "active_record/connection_adapters/sqlite3_adapter"
require "active_support/testing/time_helpers"

HERE = __dir__
REFERENCE = File.expand_path("../../../reference", HERE)
RUBY = File.read(File.join(REFERENCE, ".ruby-version")).strip
abort "Run with Ruby #{RUBY} (reference/.ruby-version), not #{RUBY_VERSION}" unless RUBY_VERSION == RUBY

# reference/app/controllers/searches_controller.rb, whose private `query` this copies.
CONTROLLER = File.read(File.join(REFERENCE, "app/controllers/searches_controller.rb"))
abort "SearchesController#query changed" unless CONTROLLER.include?('params[:q]&.gsub(/[^[:word:]]/, " ")')
def query(q) = q&.gsub(/[^[:word:]]/, " ")

ActiveRecord.default_timezone = :utc

class ApplicationRecord < ActiveRecord::Base
  self.abstract_class = true
end

class Room < ApplicationRecord
  self.inheritance_column = nil
  has_many :messages
end

class Membership < ApplicationRecord
  belongs_to :room
  belongs_to :user
end

# reference/app/models/message.rb, as far as searching needs it.
class Message < ApplicationRecord
  belongs_to :room
  scope :ordered, -> { order(:created_at) }

  # Rich text's `to_plain_text`, which the crowded messages below give directly.
  attr_accessor :plain_text_body
end
require File.join(REFERENCE, "app/models/message/pagination")
require File.join(REFERENCE, "app/models/message/searchable")
Message.include Message::Pagination, Message::Searchable

# reference/app/models/user.rb's associations searching uses.
class User < ApplicationRecord
  has_many :memberships, dependent: :delete_all
  has_many :rooms, through: :memberships
  has_many :reachable_messages, through: :rooms, source: :messages
  has_many :searches, dependent: :delete_all
end
require File.join(REFERENCE, "app/models/search")

# The corpus. A fixed seed, so a regenerated file only changes when the reference does.
random = Random.new(40)
ActiveRecord::Base.establish_connection(adapter: "sqlite3", database: File.join(HERE, "parity-seed.sqlite3"), readonly: true)
words = ActiveRecord::Base.connection.select_values("SELECT body FROM message_search_index ORDER BY rowid")
  .flat_map { |body| body.scan(/[[:word:]]+/) }.uniq.sort

pieces = {
  word: -> { words.sample(random: random) },
  prefix: -> { word = words.sample(random: random); word[0, random.rand(1..word.length)] },
  shouted: -> { words.sample(random: random).upcase },
  operator: -> { %w[ AND OR NOT NEAR and or not near NEAR/2 ].sample(random: random) },
  syntax: -> { %w[ * ^ ( ) : + - " ' { } , . ! ? / @ # $ % & = ~ ` | \\ ; < > \[ \] ].sample(random: random) },
  script: -> { [ "日本語", "東京", "Привет", "мир", "café", "naïve", "é", "ﬁle", "Ⅻ", "Ⓐ", "٣", "مرحبا", "שלום", "Ελλάδα", "ß", "İstanbul",
                 "한국어", "ไทย", "हिन्दी", "a‍b", "a‌b", "‿", "_", "²", "½", "２", "❤️", "👍🏽", "👨‍👩‍👧", " ", "　", "€", "\u0000" ].sample(random: random) },
  codepoint: -> {
    plane = [ 0x0000..0x007F, 0x0080..0x07FF, 0x0800..0xD7FF, 0xE000..0xFFFD, 0x10000..0x1FFFF, 0x20000..0x3FFFF, 0xE0000..0xE01EF ].sample(random: random)
    [ random.rand(plane) ].pack("U")
  }
}
separators = [ " ", " ", " ", "", "  ", "\t", "\n", "-", " " ]

fixed = [ nil, "", " ", "   ", "\t\n", "hello", "Hello world!", "the", "love", "LOVE", "lov", "happiness", "pain", "001", "busy", "_", "__", "a_b", "AND", "OR", "NOT", "NEAR",
  "love AND", "AND love", "love OR", "OR love", "NOT love", "love NOT", "love NOT pain", "love AND pain", "love OR pain", "love NEAR pain", "NEAR(love pain)", "\"love pain\"",
  "love*", "lov*", "^love", "body:love", "love -pain", "love + pain", "(love)", "love)", "\"unclosed", "a AND OR b", "AND AND", "OR OR OR", "a NOT", "x NEAR",
  "日本語", "café", "cafe", "naïve", "é", "Ⅻ", "Ⓐ", "a‍b", "²", "½", "❤️", "👍🏽", "Привет мир", "love 日本語", "\u0000", " love " ]

generated = Array.new(600) do
  Array.new(random.rand(1..5)) { pieces.values.sample(random: random).call }.zip(Array.new(5) { separators.sample(random: random) }).flatten.compact.take(random.rand(1..9)).join
end
corpus = (fixed + generated).uniq

queries = corpus.map { |q| { q: q, query: query(q), present: query(q).present? } }

users = User.order(:id).to_a
searches = []
corpus.each do |q|
  next unless query(q).present?
  users.each do |user|
    result = begin
      { result: user.reachable_messages.search(query(q)).last_page_of(100).map(&:id) }
    rescue ActiveRecord::StatementInvalid => error
      { error: error.cause.message }
    end
    searches << { user: user.id, q: q }.merge(result)
  end
end

# The seed's rooms are too quiet to reach `last_page_of(100)` and none of its messages share a
# `created_at`, so on a writable copy of the seed, the busiest room gets 150 more messages, in
# threes created at the same second, indexed by Message::Searchable's own `create_in_index`.
crowded = { messages: [], searches: [] }
Dir.mktmpdir do |dir|
  database = File.join(dir, "seed.sqlite3")
  FileUtils.cp(File.join(HERE, "parity-seed.sqlite3"), database)
  ActiveRecord::Base.establish_connection(adapter: "sqlite3", database: database)

  room = Room.all.max_by { |room| [ Membership.where(room: room).count, -room.id ] }
  creator_id = Membership.where(room: room).order(:user_id).first.user_id
  150.times do |i|
    created_at = Time.utc(2026, 4, 1, 12, 0, 0) + (i / 3)
    body = "Crowded #{format("%03d", i)} #{%w[ coffee tea water ][i % 3]}"
    message = Message.create!(room: room, creator_id: creator_id, client_message_id: "crowded-#{i}", created_at: created_at, updated_at: created_at, plain_text_body: body)
    crowded[:messages] << { id: message.id, room: room.id, creator: creator_id, client_message_id: message.client_message_id, at: created_at.utc.iso8601(6), body: body }
  end

  [ "crowded", "coffee", "crowded coffee", "crowded OR coffee", "Crowded tea", "007", "crowded NOT water", "crowd*", "crowd" ].each do |q|
    users.each do |user|
      crowded[:searches] << { user: user.id, q: q, result: user.reachable_messages.search(query(q)).last_page_of(100).map(&:id) }
    end
  end
end

# Recent searches, on a writable copy of the fixtures database.
include ActiveSupport::Testing::TimeHelpers
recent = []
Dir.mktmpdir do |dir|
  database = File.join(dir, "fixtures.sqlite3")
  FileUtils.cp(File.join(HERE, "rails-fixtures.sqlite3"), database)
  ActiveRecord::Base.establish_connection(adapter: "sqlite3", database: database)

  david = User.find_by!(name: "David")
  jason = User.find_by!(name: "Jason")
  time = Time.utc(2026, 3, 3, 9, 0, 0)
  steps = [ [ david, "first" ], [ david, "second" ], [ david, "first" ], [ jason, "first" ], [ david, "hello  world" ], [ david, "" ], [ david, nil ] ] +
    (1..12).map { |i| [ david, "query #{i}" ] } + [ [ david, "query 3" ], [ david, "first" ], [ david, "Query 3" ], [ jason, "second" ], [ david, :clear ], [ david, "after clear" ], [ jason, "third" ]]
  steps.each_with_index do |(user, q), step|
    time += [ 1, 1, 60, 0.5 ].sample(random: random)
    travel_to(time) do
      result = begin
        if q == :clear
          user.searches.destroy_all
        else
          user.searches.record(q)
        end
        { searches: user.searches.ordered.map { |search| [ search.query, search.created_at.utc.iso8601(6), search.updated_at.utc.iso8601(6) ] } }
      rescue ActiveRecord::ActiveRecordError => error
        { error: error.class.name }
      end
      recent << { step: step, user: user.id, q: q == :clear ? nil : q, clear: q == :clear, at: Time.now.utc.iso8601(6) }.merge(result)
    end
  end
end

# One case per line, so a regenerated file diffs by case.
lines = ->(rows) { rows.map { |row| JSON.generate(row) }.join(",\n") }
File.write(File.join(HERE, "search.json"), <<~JSON)
  {
  "queries": [
  #{lines[queries]}
  ],
  "searches": [
  #{lines[searches]}
  ],
  "crowded": {
  "messages": [
  #{lines[crowded[:messages]]}
  ],
  "searches": [
  #{lines[crowded[:searches]]}
  ]
  },
  "recent": [
  #{lines[recent]}
  ]
  }
JSON
puts "#{queries.size} queries, #{searches.size} searches (#{searches.count { |s| s[:error] }} errors), #{recent.size} recent steps"

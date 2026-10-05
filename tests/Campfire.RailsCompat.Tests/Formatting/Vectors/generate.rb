# Regenerates formatting.json from the Rails revision reference/Gemfile.lock pins (rails/rails
# 1a02651, 8.2.0.alpha): Active Support's time formats, Active Record's SQLite timestamp text both
# ways, String#truncate, Array#to_sentence, String#capitalize and the app's String#all_emoji?.
#
#   git clone https://github.com/rails/rails && git -C rails checkout 1a02651ac37fb64b4de2a2b73461d86acf9c98fb
#   gem install i18n:1.14.7 tzinfo:2.0.6 concurrent-ruby:1.3.7 connection_pool:2.5.5 minitest:5.26.2 \
#     base64:0.3.0 drb:2.2.3 bigdecimal:3.3.1 logger:1.7.0 securerandom:0.4.1 uri:1.1.1
#   LANG=C.UTF-8 ruby -I rails/activesupport/lib -I rails/activemodel/lib -I rails/activerecord/lib \
#     tests/Campfire.RailsCompat.Tests/Formatting/Vectors/generate.rb
require "active_support/all"
require "active_model"
require "active_record"
require "json"

raise "rails 8.2.0.alpha expected" unless ActiveSupport.version.to_s == "8.2.0.alpha"
raise "Unicode 15.0.0 expected (Ruby 3.4's)" unless RbConfig::CONFIG["UNICODE_VERSION"] == "15.0.0"

require_relative "../../../../reference/config/initializers/time_formats"
require_relative "../../../../reference/lib/rails_ext/string"

# The app runs in UTC (no config.time_zone), with Active Record's default_timezone :utc.
Time.zone_default = Time.find_zone!("UTC")
Time.zone = "UTC"

# What a datetime(6) column is on SQLite, and how Active Record quotes what it serializes.
DATETIME = ActiveRecord::Type::DateTime.new(precision: 6)
QUOTING = Object.new.extend(ActiveRecord::ConnectionAdapters::Quoting)
def QUOTING.default_timezone = :utc

def to_db(time)
  QUOTING.quoted_date(DATETIME.serialize(time))
end

# Whole nanoseconds since the epoch, so the input reads back exactly.
def exact(time)
  time.to_i * 1_000_000_000 + time.nsec
end

def time_case(time)
  time = time.in_time_zone
  {
    "nanoseconds" => exact(time).to_s,
    "db" => to_db(time),
    "number" => time.to_fs(:number),
    "epoch" => time.to_fs(:epoch),
    "iso8601" => time.iso8601,
    "usec" => time.to_fs(:usec),
    "fs_db" => time.to_fs(:db),
    "as_json" => time.as_json
  }
end

rng = Random.new(20261005)

# Times as the app holds them (100ns steps, which .NET's ticks hold exactly), including the ones
# where Float can't hold the milliseconds and to_fs(:epoch) rounds down.
TIMES = [
  Time.utc(2026, 1, 1, 12), Time.utc(1970, 1, 1), Time.utc(1969, 12, 31, 23, 59, 59, Rational(999_999)),
  Time.utc(1969, 12, 31, 23, 59, 59, Rational(1, 10)), Time.utc(1900, 1, 1), Time.utc(9999, 12, 31, 23, 59, 59, Rational(9_999_999, 10)),
  Time.utc(2026, 9, 26, 12, 23, 46, Rational(483_521)), Time.utc(2026, 9, 26, 11, 23, 46),
  Time.utc(2024, 6, 1, 12, 0, 0, Rational(123)), Time.utc(2024, 2, 29, 0, 0, 0, Rational(1)),
  Time.utc(2026, 1, 1, 0, 0, 0, Rational(1, 10)), Time.utc(2026, 1, 1, 0, 0, 0, Rational(9999, 10)),
  Time.utc(2026, 1, 1, 0, 0, 0, Rational(120_000))
] + Array.new(400) do
  seconds = rng.rand(-2_208_988_800..4_102_444_800)
  Time.at(seconds, rng.rand(0...10_000_000) * 100, :nsec).utc
end + Array.new(100) do
  # Whole milliseconds, where the float round trip loses one most often.
  milliseconds = rng.rand(1_500_000_000_000..1_900_000_000_000)
  Time.at(milliseconds / 1000, milliseconds % 1000, :millisecond).utc
end

# Text in datetime columns: what Rails writes, what SQLite's own functions write (insert_all uses
# STRFTIME('%Y-%m-%d %H:%M:%f', 'NOW'); CURRENT_TIMESTAMP has no fraction), and what Time.new
# makes of the spellings around them.
DB_TEXTS = [
  "2026-01-01 12:00:00", "2026-01-01 12:00:00.000001", "2026-01-01 12:00:00.123456", "2026-01-01 12:00:00.120000",
  "2026-09-26 12:25:26.826", "2026-01-01 12:00:00.5", "2026-01-01 12:00:00.1234567", "2026-01-01 12:00:00.123456789",
  "2026-01-01 12:00:00.1234567891", "2026-01-01 12:00:00.000000", "2026-01-01 12:00:00.0000001",
  "2026-01-01T12:00:00", "2026-01-01T12:00:00.5", "2026-01-01 12:00:00Z", "2026-01-01 12:00:00.5Z", "2026-01-01 12:00:00 Z",
  "2026-01-01 12:00:00 UTC", "2026-01-01 12:00:00 utc", "2026-01-01 12:00:00+01:00", "2026-01-01 12:00:00-05:30",
  "2026-01-01 12:00:00 +0100", "2026-01-01 12:00:00+0100", "2026-01-01 12:00:00.25+01:00", "2026-01-01 00:30:00+01:00",
  "2026-02-28 23:59:59.999999", "2024-02-29 00:00:00", "2026-02-29 00:00:00", "2026-02-30 00:00:00", "2026-04-31 00:00:00",
  "2026-12-31 23:59:59", "2026-12-31 24:00:00", "2026-12-31 23:59:60", "1969-12-31 23:59:59.999999", "1970-01-01 00:00:00",
  "1900-01-01 00:00:00", "9999-12-31 23:59:59.999999", "0001-01-01 00:00:00",
  "2026-13-01 00:00:00", "2026-00-01 00:00:00", "2026-01-32 00:00:00", "2026-01-00 00:00:00", "2026-01-01 25:00:00",
  "2026-01-01 12:60:00", "2026-01-01 12:00:61", "2026-01-01 24:00:01", "2026-01-01 12:00:00.", "2026-01-01 12:00:00.x",
  "2026-01-01 12:00", "yesterday", "", "1:2:3", "2026-01-01 12:00:00 EST", "2026-01-01  12:00:00",
  "2026-01-01 12:00:00 X", "2026-01-01 12:00:00 A", "2026-01-01 12:00:00M", "2026-01-01 12:00:00 J", "2026-01-01 12:00:00 x",
  "2026-01-01 12:00:00+01", "2026-01-01 12:00:00 UTC+1", "2026-01-01 12:00:00 Utc", "2026-01-01 24:00:00.5",
  "2026-01-01 12:00:60.5", "2026-01-01 12:00:00+24:00", "2026-01-01 12:00:00+23:59", "2026-01-01 12:00:00+01:60",
  "2026-01-01 12:00:00 -00:00", "+2026-01-01 12:00:00", "202-01-01 12:00:00", "02026-01-01 12:00:00", "2026-1-01 12:00:00",
  "2026-01-01 1:00:00", "2026-01-01t12:00:00", " 2026-01-01 12:00:00", "2026-01-01 12:00:00 ", "2026-01-01 12:00:00\n",
  "2026-01-01 12:00:00.5 UTC", "2026-01-01 12:00:00.-5", "2026-01-01 12:00:00z", "2026-01-01 12:00:00+1:00",
  "2026-01-01 12:00:00 +01:00", "2026-01-01 12:00:00  +01:00", "2026-01-01 12:00:00GMT", "2026-01-01 12:00:00 utc ",
  "2026-01-01 12:00:00.000000000001", "２０２６-01-01 12:00:00",
] + TIMES.map { |time| to_db(time) }

# strict: whether Time.new(text, in: "UTC") read it. Where it raises, Rails falls back to
# Date._parse, which the port doesn't (neither Rails nor SQLite writes those spellings).
def db_text_case(text)
  time = DATETIME.deserialize(text)
  strict = begin
    Time.new(text, in: "UTC")
    true
  rescue ArgumentError
    false
  end
  { "input" => text, "strict" => strict, "nanoseconds" => time && exact(time).to_s, "db" => time && to_db(time) }
end

TRUNCATIONS = [
  [ "abcdef", 4, "…", nil ], [ "abcd", 4, "…", nil ], [ "abcde", 4, "…", nil ], [ "", 0, "…", nil ], [ "abc", 0, "…", nil ],
  [ "abc", 1, "...", nil ], [ "abcdef", 5, nil, nil ], [ "Hello World", 8, nil, " " ], [ "Hello World", 8, "…", " " ],
  [ "Hello Big World", 12, "…", " " ], [ "Helloworld", 8, "…", " " ], [ "日本語のテキスト", 5, "…", nil ],
  [ "😀😀😀😀😀😀", 4, "…", nil ], [ "ééé", 3, "…", nil ], [ "<b>bold</b> & more", 10, "…", nil ],
  [ "a" * 300, 280, "…", nil ], [ "a" * 280, 280, "…", nil ], [ "a" * 281, 280, "…", nil ], [ "x" * 600, 560, "…", nil ],
  [ "ab", 1, "", nil ], [ "abcdef", 3, "......", nil ]
]

def truncate_case((text, length, omission, separator))
  options = { omission: omission, separator: separator }.compact
  { "text" => text, "length" => length, "omission" => omission, "separator" => separator, "result" => text.truncate(length, options) }
end

SENTENCES = [ [], [ "A" ], [ "A", "B" ], [ "A", "B", "C" ], [ "A", "B", "C", "D" ], [ "", "" ] ]
CONNECTORS = [ nil, "+" ]

def sentence_case(items, two_words_connector)
  options = { two_words_connector: two_words_connector }.compact
  { "items" => items, "two_words_connector" => two_words_connector, "result" => items.to_sentence(options) }
end

CODEPOINTS = (0..0x10FFFF).reject { |c| c.between?(0xD800, 0xDFFF) }.map { |c| [ c ].pack("U") }

# Only the codepoints a case mapping changes; every other one maps to itself.
def changed(&mapping)
  CODEPOINTS.filter_map { |c| (mapped = mapping.call(c)) == c ? nil : [ c.ord, mapped ] }.to_h
end

def ranges(codepoints)
  codepoints.slice_when { |a, b| b != a + 1 }.map { |run| [ run.first, run.last ] }
end

CAPITALIZE_STRINGS = [ "", "a", "ab", "AB", "aBc DeF", "élan", "ÉLAN", "ßa", "ǆemal", "ǅEMAL", "ﬁx", "ΣΑΣ", "ὈΔΥΣΣΕΎΣ", "İstanbul", "ŉ", "1abc", " abc" ]

EMOJI_STRINGS = [
  "", "👍", "❤️", "❤", "hi 👍", "👍👍", "👨‍👩‍👧", "🇺🇸", "1️⃣", "#", "©", "©️", "™", "☺", "☺️", "\u{FE0F}", "👍🏽", "a", " 👍",
  "👍 ", "😀\n", "🫠", "\u{1FAE8}", "\u{1F7F0}", "\u{200D}", "🏳️‍🌈", "↔", "↔️", "⌚", "‼"
]

def cases(list)
  "[\n" + list.map { |c| "    " + JSON.generate(c) }.join(",\n") + "\n  ]"
end

sections = {
  "times" => cases(TIMES.map { |time| time_case(time) }),
  "db_texts" => cases(DB_TEXTS.uniq.map { |text| db_text_case(text) }),
  "truncate" => cases(TRUNCATIONS.map { |c| truncate_case(c) }),
  "to_sentence" => cases(SENTENCES.product(CONNECTORS).map { |items, connector| sentence_case(items, connector) }),
  "capitalize" => cases(CAPITALIZE_STRINGS.map { |s| { "input" => s, "result" => s.capitalize } }),
  "capitalize_codepoints" => JSON.generate(changed(&:capitalize)),
  "downcase_codepoints" => JSON.generate(changed(&:downcase)),
  "emoji_ranges" => JSON.generate(ranges(CODEPOINTS.select(&:all_emoji?).map(&:ord))),
  "all_emoji" => cases(EMOJI_STRINGS.map { |s| { "input" => s, "result" => s.all_emoji? } })
}
versions = { "ruby" => RUBY_VERSION, "rails" => ActiveSupport.version.to_s, "unicode" => RbConfig::CONFIG["UNICODE_VERSION"] }

json = "{\n  \"versions\": #{JSON.generate(versions)},\n" +
  sections.map { |name, value| "  #{JSON.generate(name)}: #{value}" }.join(",\n") + "\n}\n"
File.write(File.join(__dir__, "formatting.json"), json)

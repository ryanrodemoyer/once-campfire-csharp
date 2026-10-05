# Generates vectors/ruby_core.json: what Ruby, Rack, Active Record and Addressable make of the
# inputs crates/ruby (ruby_compat) handles, for its golden test (crates/ruby/tests/ruby_core.rs).
#
#   reference-tools/run.sh reference-tools/ruby_core.rb
#
# The strings are every character from U+0000 to U+00FF on its own (neither JSON nor a Rust `&str`
# can hold a lone byte past 0x7F), then edge cases for the parsers. An integer is written as a
# decimal string, since Ruby integers go past 64 bits; a float as its `Float#to_s` (or, as an
# input, its IEEE 754 bits in hex); and an exception as its class name.
require "cgi"
require "erb"
require "json"

SINGLE_CHARACTERS = (0..255).map { |codepoint| [ codepoint ].pack("U") }

EDGE_STRINGS = [
  # String#to_i, and Active Record's integer binding
  "", " ", "0", "5", "-5", "+5", "--5", "+-5", "-+5", "-", "+", "- 5", "12abc", " 12", "\t\n\v\f\r 7",
  "\v5", "\0 5", "5\0", " 5", "　5", "\u{2028}5", "５", "5_6", "5__6", "_5", "5_", "0_5",
  "0__5", "1_000", "1_000_000", "0d12", "0D12", "-0d5", "+0d5", "0d", "0d_5", "0d0_5", "0d-5",
  "00d5", "0x5", "0X5", "0b1", "0o7", "07", "0_7", "0 5", "3000000000", "2147483648",
  "9223372036854775807", "9223372036854775808", "-9223372036854775808", "-9223372036854775809",
  "99999999999999999999", "-99999999999999999999", "1" * 50, "9" * 40 + "x",
  # String#to_f
  "0.5", "0.5.1", "1.5.5e2", "1e2", "1E2", "1.2e-1", "1.5e+2", "1.e5", "1e2.5", "1e0_1", "1e",
  "1e+", "0.5e-", "1e_2", "1_0.5", "1_2_3.4_5", "0.5_5", "0_0.5", "1__0", "1_e2", "1._5", "1_.5",
  ".5", "+.5", "-.5", "5.", "00.5", "  -1.5x", "1,5", "0x1A", "-0x1A", "0x1p3", ".e5", "e5", ".",
  "1e-400", "1e400", "-1e400", "1e308", "1.7976931348623157e308", "4.9e-324", "2.5e-324", "0.1",
  "-0.0", "-0", "Infinity", "NaN", "0.1e-3_0", "123456789012345678901234567890.5",
  "0." + "0" * 30 + "1", "1" + "0" * 70 + "x", "1_" + "0" * 70, "0" * 70 + "1.5",
  "1." + "3" * 70, "9007199254740993", "9007199254740993x", "2.2250738585072011e-308",
  "1" + "0" * 65 + "e5x", "12_34.5_6e1_0x", "1.5e3_", "1e5e3", "+_1", "1 ", "1 x", "1\t2", "1\0" + "2",
  "0" * 64 + "1_0", "-" + "0" * 64 + "1_0",
  # Ruby's strtod reads a fraction's digits only while it has 60 significant ones or fewer. Zeros
  # are counted when a digit follows them, so the 1 here is read; in the 74 digits halfway between
  # two doubles (2**-30 * (1 + 3 * 2**-53)) the last ones aren't, and it rounds down, not to even.
  "1.00000000000000011102230246251565404236316680908203125" + "0" * 10 + "1",
  "0.00000000093132257461547882581772970738537807677825952623607008717954158782958984375",
  # Hexadecimal is read only after a sign
  "+0x1A", "-0x1p3", "-0x1.8", "-0x.8", "-0x", "-0xg", "-0x1p", "-0x1_A", "-0x1Ag", " -0x10", "-0x1p-2",
  "-0x0", "-0x00.1", "-0x1P+4", "-0x" + "f" * 20, "+0x1e3", "-0x1p99999", "-0x1.fffffffffffff8p0",
  "+0x0.0000000000000000000000001p0", "-0x1p-1074", "-0x1p-1075", "-0x1.8p-1074", "+0x_1", "+0x1__2",
  "-0x.", "+0x.", "0x.", "-0x.p1", "-0x.g", "-0x._1", "-0x1_.", "-0x1.p1", "-0xp1", "-0x_1",
  # String#strip
  "  \t", "\0\0", "\0x\0", " x ", " x ", "　x　", "\u{85}x\u{85}", "x\n\n",
  # The escapers
  "é", "a b", "a*~ b-._", "<&>\"'", "x\u{2028}y", "日本", "😀", "%41", "a+b/c?d=e&f#g", "@:!$&'()*,;=",
]

def rescued
  yield
rescue => e
  e.class.name
end

# What `find`, `find_by(id:)` and `where` bind a string id param as: the SQLite adapter's 8-byte
# integer type, as `Room.type_for_attribute(:id)` is. (`ActiveModel::Type::Integer.new` is 4 bytes.)
INTEGER = ActiveRecord::ConnectionAdapters::SQLite3Adapter::SQLite3Integer.new

def string_case(s)
  {
    "input" => s,
    "to_i" => s.to_i.to_s,
    "integer_cast" => rescued { INTEGER.serialize(s)&.to_s },
    "to_f" => s.to_f.to_s,
    "strip" => s.strip,
    "html_escape" => ERB::Util.html_escape(s).to_str,
    "cgi_escape" => CGI.escape(s),
    "url_encode" => ERB::Util.url_encode(s),
    # `uri.query_values=` (Addressable 2.9), as geared_pagination's next-page links use it
    "addressable_unreserved" => Addressable::URI.encode_component(s, Addressable::URI::CharacterClassesRegexps::UNRESERVED),
    # Rack's cookie value escaping, for rails_compat::cookies::escape
    "rack_escape" => Rack::Utils.escape(s)
  }
end

rng = Random.new(20260930)

FLOATS = [
  0.0, -0.0, 1.0, -1.0, 0.1, 0.5, 100.0, 600.0, 16.0 / 9.0, 1.0 / 3.0, 0.1 + 0.2, 0.0001, 0.00012345,
  9.999999999999999e-5, 1e-5, 1.5e-7, 5e-324, 2.2250738585072014e-308, 1e14, 123456789012345.6,
  999999999999999.0, 999999999999999.9, -999999999999999.0, 1e15, -1e15, 1.5e15, 1234567890123456.0,
  9007199254740992.0, 1000000000000001.0, 1963684456584958.8, 1000000000000000.1, 1000000000000000.2,
  2251799813685248.5, -2551800308696183.5, 9999999999999998.0, 1e16, 1.5e16, 1e20, 1e21, 1e100,
  Float::MAX, -Float::MAX, Float::MIN, Float::EPSILON, Float::INFINITY, -Float::INFINITY, Float::NAN,
  # Two shortest forms equally close: Ruby takes the even one while it reads back
  667020902720176.0 + 0.25, 667020902720176.0 + 0.75, 1125899906842624.0 + 0.25,
  -(2074704973491874.0 + 0.25), 24603114260468.0 + 0.0625, 210745403561986.0 + 0.125, 2.0**-24, 2.0**-25
] + Array.new(300) { rng.bytes(8).unpack1("G") } + Array.new(200) { rng.rand * 10.0**rng.rand(-8..22) }

def float_case(f)
  { "bits" => [ f ].pack("G").unpack1("H*"), "to_s" => f.to_s }
end

RANGE_HEADERS = [
  nil, "bytes=0-4", "bytes=5-", "bytes=-3", "bytes=-30", "bytes=0-99999999999999999999",
  "bytes=99999999999999999999-", "bytes=-99999999999999999999", "bytes=0-1,", "bytes= -5", "bytes=0-1, 3-4",
  "bytes=0-1,\t 3-4", "bytes=3-5,1-2", "bytes=+1-2", "bytes=1-+2", "bytes=1_0-2_0", "bytes=0d5-0d9",
  "bytes= 1-2", "bytes=a-b", "bytes=0-0x5", "bytes=0-1 ", "bytes=0 -1", "bytes=1-2-3", "bytes=9-9",
  "bytes=10-", "bytes=10-12", "bytes=-0", "bytes=--5", "bytes=5--6", "bytes=0-4,5-9,0-0", "bytes=;bytes=2-3",
  "bytes=0-1;bytes=2-3", "xbytes=0-1", "bytes=;0-1", "bytes=", "bytes=-", "bytes=5", "bytes=1-0",
  "bytes=0-1,,2-3", "bytes=0-1, ", "bytes=0-0,-1", "bytes=0-", "bytes=-1", "bytes=0-0", "items=0-1",
  "Bytes=0-1", "bytes=\v1-2", "bytes=0-1\n", "bytes=0-1," + "0-0," * 98, "bytes=0-1," + "0-0," * 99
]

def byte_range_case(header, size)
  ranges = Rack::Utils.get_byte_ranges(header, size)
  { "header" => header, "size" => size, "ranges" => ranges&.map { |range| [ range.begin, range.end ] } }
end

# One case per line.
def cases(list)
  "[\n" + list.map { |c| "    " + JSON.generate(c) }.join(",\n") + "\n  ]"
end

sections = {
  "strings" => (SINGLE_CHARACTERS + EDGE_STRINGS).uniq.map { |s| string_case(s) },
  "floats" => FLOATS.map { |f| float_case(f) },
  "byte_ranges" => RANGE_HEADERS.product([ 0, 1, 10, 100 ]).map { |header, size| byte_range_case(header, size) }
}
versions = { "ruby" => RUBY_VERSION, "rack" => Rack.release, "rails" => Rails.version, "addressable" => Addressable::VERSION::STRING }

json = "{\n  \"versions\": #{JSON.generate(versions)},\n" +
  sections.map { |name, list| "  #{JSON.generate(name)}: #{cases(list)}" }.join(",\n") + "\n}\n"
File.write(File.join(ENV.fetch("VECTORS_DIR"), "ruby_core.json"), json)

# Regenerates json_parse.json: what JSON.parse (json 2.21.2 in the reference image) makes of byte
# strings, as attachable_from_possibly_expired_sgid parses a decoded SGID. For a failure it records
# whether the exception's message is valid UTF-8: the message quotes the bytes at the parser's
# cursor for some errors only, and an invalid one can't be logged. Run in the reference:
#
#   parity/bin/reference runner -e RAILS_LOG_LEVEL=fatal \
#     tests/Campfire.RichText.Tests/Attachments/Vectors/generate_json.rb \
#     > tests/Campfire.RichText.Tests/Attachments/Vectors/json_parse.json
require "json"
require "base64"

bad = "\xFB" # Invalid UTF-8 in a UTF-8 string, so it interpolates anywhere
inputs = [
  # Objects: keys, colons, commas
  %({"_rails";#{bad}"data":1}), %({"a":1,"b";#{bad}}), %({#{bad}}), %({"a":1,#{bad}}), %({"a":1#{bad}}),
  %({"a":1 #{bad}}), %({ #{bad}}), %({"a" #{bad}}), %({"a"), %({"a":), %({"a":1), %({"a":1,), %({), %({"a":1,"b"),
  %({"a":1,"a":2}), %({"a":{"b":[1,2,{"c":null}]}}), %({"a":1,}), %({,}), %({"a"::1}), %({'a':1}),
  # Arrays and values
  %([1#{bad}]), %([1,#{bad}]), %([#{bad}]), %([1,]), %([), %([1), %([1,), %([]), %([ ]), %([1 2]),
  bad, %( #{bad}), %(\n#{bad}), "x#{bad}", "\xC3\xA9", "\xC3", "a\xC3\xA9", "é#{bad}", "",
  # Keywords
  "nul#{bad}", "nul", "null#{bad}", "nulx", "true", "tru#{bad}", "falsy#{bad}", "f", "N#{bad}", "NaN",
  "I#{bad}", "Infinity", "-Infinity", "[NaN]",
  # Numbers
  "-#{bad}", "-", "01#{bad}", "01", "-01", "1.#{bad}", "1.", "1e#{bad}", "1e", "1e+", "1E+5", "-0", "1.5e-3",
  "12345678901234567890123", "1x", "1 x", "[01]", "[-]", "[1.]", "{\"a\":-#{bad}}",
  # End of stream
  "1 #{bad}", "1 x#{bad}", "1 x\xC3\xA9", "1 x\xC3", "1 \x00#{bad}", "1\n#{bad}", "{} #{bad}", "{}}",
  "1 #{"a" * 31}#{bad}", "1 #{"a" * 30}#{bad}a", "1 #{"a" * 32}#{bad}", "1 #{"a" * 31}\xC3\xA9", "1 #{"a" * 30}\xC3\xA9\xC3",
  "1 \xA9\xA9", "1 \xC3\xA9\xA9", "1 \xE2\x82", "1 \xE2\x82\xAC", "1 \xF0\x9F\x98\x80", "1 \xF0\x9F\x98",
  # Strings
  %("abc), %("abc\\), %("a\\#{bad}"), %("a\\#{bad}), %("a\\u12#{bad}4"), %("a\\u12"), %("a\\u12), %("a\\uzzzz"),
  %("\\ud800#{bad}"), %("x\\ud800#{bad}"), %("\\ud800\\u0041#{bad}"), %("y\\n\\ud800\\u0041#{bad}"), %("\\ud800\\udc00"),
  %("\\udc00"), %("\\ud800"), %("\\u0000"), "\"#{bad}\x01\"", "\"a\x01#{bad}\"", "\"a\\n\x01#{bad}\"", "\"a\\\n#{bad}\"",
  "\"a\\\x01#{bad}\"", "\"a\\\x00#{bad}\"", "\"a\x00#{bad}\"", "\"\t\"", "\"a\\q\"", "\"#{bad}\"", "{\"#{bad}\":1}",
  %("\\/\\"\\\\\\b\\f\\n\\r\\t"), %("#{"\\n" * 20}\\q#{bad}"), %("#{"\\n" * 20}\\ud800#{bad}"), "\"a\\\\\x01\"",
  "{\"a\":\"\x7F\"}",
  # Comments
  "/x#{bad}", "/", "/*#{bad}", "/* x *", "// #{bad}", "//x\n1", "/*c*/{\"a\":1}", "{/*c*/\"a\"/*d*/:/*e*/1/*f*/}",
  "[1/*c*/,2]", "1 /#{bad}", "1 //x", "{\"a\":1/#{bad}}", "[/#{bad}]",
  # Nesting
  "[" * 100 + "]" * 100, "[" * 101 + "]" * 101, "[" * 102 + "]" * 102, "[" * 101 + "1" + "]" * 101,
  "{\"a\":" * 100 + "1" + "}" * 100, "{\"a\":" * 101 + "1" + "}" * 101, "{\"a\":" * 101 + "{}" + "}" * 101,
  # Base64 SGID payloads the fuzzer found
  Base64.decode64("eyJfcmFpbHMiO/siZGF0YSI6ImdpZDovL2NhbXBmaXJlL1VzZXIvMj9leHBpcmVzX2luIiwicHVyIjoiYXR0YWNoYWJsZSJ9fQ=="),
  %({"_rails":{"data":"gid://campfire/User/1","pur":"attachable"}}), %({"_rails":null}), %({"_rails":{"message":"x"}})
].map(&:b)

def outcome(input)
  value = JSON.parse(input.dup.force_encoding("BINARY"))
  begin
    { "ok" => JSON.generate(value, max_nesting: false) }
  rescue JSON::GeneratorError, Encoding::UndefinedConversionError, ArgumentError
    { "ok" => nil } # A string that isn't valid UTF-8
  end
rescue JSON::NestingError => e
  { "error" => "JSON::NestingError", "loggable" => e.message.valid_encoding?, "message" => e.message }
rescue JSON::ParserError => e
  { "error" => "JSON::ParserError", "loggable" => e.message.valid_encoding?, "message" => e.message.scrub }
end

cases = inputs.uniq.map { |input| { "input" => Base64.strict_encode64(input) }.merge(outcome(input)) }
$stderr.reopen(File::NULL) # JSON.parse warns about comments and duplicate keys
puts JSON.pretty_generate({ "json" => JSON::VERSION, "cases" => cases })

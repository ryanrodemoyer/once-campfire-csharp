# Regenerates allowed_uri.json: Loofah::HTML5::Scrub.allowed_uri? (loofah 2.25.2), which decides
# whether a URI attribute survives sanitizing, on hand-picked URIs and on random ones built from
# fragments, many of them character references CGI.unescapeHTML may or may not decode. Run in the
# reference:
#
#   parity/bin/reference runner -e RAILS_LOG_LEVEL=fatal \
#     tests/Campfire.RichText.Tests/Sanitize/Vectors/generate_allowed_uri.rb \
#     > tests/Campfire.RichText.Tests/Sanitize/Vectors/allowed_uri.json
require "json"

uris = [
  "https://example.com/", "http://x", "mailto:a@b.c", "javascript:alert(1)", "JavaScript:x", " javascript:x", "java\tscript:x",
  "java\u0000script:x", "jav&#x09;ascript:x", "javascript&colon;x", "javascript&#58;x", "javascript&#58x", "javascript&#058;x",
  "javascript&#x3a;x", "javascript&#X3A;x", "javascript&#x003a;x", "javascript%3Ax", "javascript&#37;3Ax", "&#106;avascript:x",
  "&#x6A;avascript:x", "&#106avascript:x", "ja&#\v118;ascript&#+58;al ;e\tr\v�6;('&+#88\n;SS')", "javascript&#+58;x",
  "javascript&#-58;x", "javascript&# 58;x", "javascript&#x 3a;x", "javascript&#x+3a;x", "javascript&#5_8;x", "javascript&#58 ;x",
  "javascript&#\t58;x", "javascript&Tab;:x", "javascript&NewLine;:x", "data:image/png;base64,AA", "data:text/html;base64,AA",
  "data:text/html:text/html;base64,AA", "data:,x", "data:image/svg+xml,x", "vbscript:x", "ftp://x", "tel:1", "sms:1", "x:y",
  "/relative", "#frag", "?q", "", "&amp;", "&#1114111;", "&#1114112;", "&#0;javascript:x", "javascript&#0;:x", "&lt;javascript:x"
]

pieces = [
  "javascript", "java", "script", "data", "http", "https", "mailto", "x", ":", ":", "&#58;", "&#58", "&#058;", "&#x3a;", "&#x3A",
  "&#X3a;", "&#+58;", "&#-58;", "&# 58;", "&#x 3a;", "&#x+3a;", "&#5_8;", "&#106;", "&#x6a;", "&#106", "&#0;", "&#;", "&#x;",
  "&#99999999;", "&#1114111;", "&colon;", "&Tab;", "&NewLine;", "&amp;", "&lt;", "&quot;", "&apos;", "&nbsp;", "%3A", "&#37;3A",
  "%3a", "&", "#", ";", " ", "\t", "\n", "\v", "\u0000", " ", "ā", "Ă", "`", "/", "//", "text/html", "image/png",
  ",", "base64", "é", "😀", "+", "-", ".", "A", "0"
]
random = Random.new(3)
3000.times do
  uris << Array.new(random.rand(1..8)) { pieces[random.rand(pieces.size)] }.join
end

cases = uris.uniq.map { |uri| { "uri" => uri, "allowed" => Loofah::HTML5::Scrub.allowed_uri?(uri) } }
puts JSON.pretty_generate({ "loofah" => Loofah::VERSION, "cases" => cases })

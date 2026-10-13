# Regenerates css_scrub.json: Loofah::HTML5::Scrub.scrub_css (loofah 2.25.2, crass 1.0.7, with the
# "var" function Lexxy allows) on hand-picked styles and on random ones built from CSS fragments.
# Run in the reference, where Lexxy's initializer has run:
#
#   parity/bin/reference runner -e RAILS_LOG_LEVEL=fatal \
#     tests/Campfire.RichText.Tests/Sanitize/Vectors/generate_css.rb \
#     > tests/Campfire.RichText.Tests/Sanitize/Vectors/css_scrub.json
require "json"

ActionText::Content # Lexxy adds "var" when Action Text's content loads
raise "Lexxy's var function expected" unless Loofah::HTML5::SafeList::ALLOWED_CSS_FUNCTIONS.include?("var")

styles = [
  "color: red", "color: red;", "COLOR: RED", "color:red;background-color:blue", "color: red !important",
  "color: red ! important", "color: red !IMPORTANT", "color: !important", "color: red !important x",
  "color: var(--highlight-1); background-color: var(--highlight-bg-2);", "color: height=", "color: height=\"20<select>\"",
  "width: expreth: );", "width: expression(alert(1))", "width: expre S=", "white-space: pre-old", "white-space: pre-wrap",
  "background: url(javascript:x)", "background: red url(x)", "background-image: url(x)", "color: url(x)",
  "color: red; background: url(javascript:x)", "margin: 0 auto", "margin: 10px foo", "padding: 1em 2em", "border: 1px solid red",
  "border-left: 1px dotted bogus", "font-family: 'Helvetica'", "font-family: \"a'b\"", "font-family: \"\"", "font-family: \"abc",
  "font-family: 'abc\n'", "font-family: \"a\\\"b\"", "color: rgb(1,2,3)", "color: RGB(1,2,3)", "color: rgb(1,2", "color: hsl(0 0% 0%)",
  "color: rgba(url(x))", "color: calc(1px + 2px)", "color: expression(1)", "color: #fff", "color: #", "color: \\72 ed",
  "c\\6flor: red", "color : red", "color\n:\tred", "color: red\r\nfont-size: 2px", "color:red;;;font-size:2px", ";color:red",
  "@media x { color: red } color: blue", "@x; color: blue", "{color: red}", "[a] color: red; width: 1px", "color: [a] red",
  "color: (a) red", "color: {a} red", "color: red]", "color: red)", "color: red}", "color: /* c */ red", "color: red /* c",
  "/* c */ color: red", "color: /[a]*", "color: a/**/b", "color: <!-- red -->", "color: U+26 u+1? u+0-7F", "color: 1e3px 50% -.5",
  "color: +.5em", "color: -webkit-x", "color: --x", "color: \\", "color: \\\n red", "color: 'a\\\nb'", "color: red\\", "color: é😀",
  "color: \u0000red", "color: \u000bred", "color: \u007f", "color: red\u000b", "font-size: 12pX", "font-size: 12Q", "width: 1.",
  "width: .5", "width: 10vmax", "margin: -1.5em", "margin: 1,2", "margin: rgb(1,2,3)", "margin: inherit", "margin: #abc",
  "margin: bogus", "margin-left: auto", "-margin: 0", "fill: red", "stroke-width: 2", "position: fixed", "display: none",
  "display: block;position:absolute", "x: y", "color", "color red", ":red", "color:", "color: ;", "color: \"\";",
  "color: rgb(" + "rgb(" * 30 + "1" + ")" * 31, "color: " + "(" * 30 + "red" + ")" * 30, "color: var(" * 26 + "x",
  "color: url(  x  )", "color: url( 'x' )", "color: url(\"x\")", "color: url(x y)", "color: url(x\"y)", "color: url(x\\)y)",
  "color: url(", "background: URL(x)", "color: red, blue", "font: 12px/1.5 'a', serif", "color: attr(data-x)",
  "color: a\fb", "color:red;color:blue", "COLOR:Red", "Background-Color: Blue", "color: Red !Important",
  "font-family: \"a;b\"; color: red", "color: rgb(1;2); width: 1px", "color: 'a;b' red"
]

pieces = [
  "color", "Color", "background-color", "font-size", "margin", "border-top", "padding", "white-space", "width", "x", "c\\6flor",
  "-moz-x", "--var", ":", ":", ";", ";", " ", " ", "\n", "\t", "\r", "\f", "red", "RED", "auto", "pre", "height", "important",
  "!", "!important", "1", "-1.5e3", "10px", "50%", ".5", "+2", "#fff", "#", "'a'", "\"b\"", "\"a'b\"", "\"open", "'x\n", "rgb(",
  "var(", "url(", "url(x)", "url( x )", "url('x')", "expression(", "calc(", "attr(", ")", "(", "[", "]", "{", "}", "/*", "*/",
  "\\", "\\61", "\\\n", "@media", "<!--", "-->", "U+26", "u+1?", "~=", "|=", "||", "^=", "$=", "*=", ",", ".", "+", "-", "=",
  "é", "😀", "\u0000", "\u000b", "\u007f", "javascript:", "/", "*", "\"", "'", "Q", "vw", "em"
]
random = Random.new(12)
3000.times do
  styles << Array.new(random.rand(1..14)) { pieces[random.rand(pieces.size)] }.join
end

cases = styles.uniq.map { |style| { "style" => style, "scrubbed" => Loofah::HTML5::Scrub.scrub_css(style) } }
puts JSON.pretty_generate({ "loofah" => Loofah::VERSION, "cases" => cases })

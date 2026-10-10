# What Nokogiri does with a NUL in an attribute name or value, for the R06 check in
# HtmlElement.SetAttribute (issue #161). Records node[name] = value (Nokogiri::XML::Node#[]=,
# which calls the C `set`, nokogiri 1.19.4 xml_node.c) and the parse-time NUL handling of
# Nokogiri::HTML5, which must stay as it is.
#
#   parity/bin/reference runner tests/Campfire.RichText.Tests/NullByte/generate.rb \
#     > tests/Campfire.RichText.Tests/NullByte/null_byte.json

require "json"

def outcome
  { "ok" => yield }
rescue Exception => e
  { "error" => e.class.name, "message" => e.message.to_s.scrub[0, 300] }
end

ASSIGNMENTS = [
  { "name" => "new attribute, value with NUL in the middle", "attribute" => "title", "value" => "a\u0000b" },
  { "name" => "new attribute, value that is only NUL", "attribute" => "title", "value" => "\u0000" },
  { "name" => "new attribute, value with NUL at the end", "attribute" => "title", "value" => "ab\u0000" },
  { "name" => "existing attribute, value with NUL", "attribute" => "class", "value" => "x\u0000y" },
  { "name" => "style attribute, value with NUL", "attribute" => "style", "value" => "color: red\u0000" },
  { "name" => "name with NUL", "attribute" => "da\u0000ta", "value" => "x" },
  { "name" => "value with the escaped text &#0;", "attribute" => "title", "value" => "a&#0;b" },
  { "name" => "value with other control characters", "attribute" => "title", "value" => "a\u0001\u001fb\u007f" },
  { "name" => "empty value", "attribute" => "title", "value" => "" },
  { "name" => "value with unicode", "attribute" => "title", "value" => "é 😀 שלום" },
]

assignments = ASSIGNMENTS.map do |c|
  node = Nokogiri::HTML5.fragment('<p class="a">x</p>').at("p")
  result = outcome { node[c["attribute"]] = c["value"]; nil }
  c.merge(
    "result" => result,
    "attributes_after" => node.attribute_nodes.map { |a| [a.name, a.value] }
  )
end

PARSE_CASES = [
  { "name" => "NUL in an attribute value", "html" => "<p title=\"a\u0000b\">x</p>" },
  { "name" => "NUL in an attribute name", "html" => "<p da\u0000ta=\"x\">x</p>" },
  { "name" => "NUL in text", "html" => "<p>a\u0000b</p>" },
  { "name" => "NUL-only attribute value", "html" => "<p title=\"\u0000\">x</p>" },
  { "name" => "escaped NUL reference", "html" => "<p title=\"a&#0;b\">x</p>" },
]

parse = PARSE_CASES.map do |c|
  fragment = Nokogiri::HTML5.fragment(c["html"])
  c.merge(
    "to_html" => fragment.to_html,
    "attributes" => fragment.css("p").first.attribute_nodes.map { |a| [a.name, a.value] }
  )
end

puts JSON.pretty_generate({ "assignments" => assignments, "parse" => parse })

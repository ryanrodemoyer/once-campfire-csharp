# Regenerates parse.json: what URI::GID.parse (globalid 1.4.0 on Ruby 4.0's RFC 3986 parser) makes
# of GID strings, as GlobalID.parse sees them before its Base64 fallback. Run in the reference:
#
#   parity/bin/reference runner tests/Campfire.RailsCompat.Tests/GlobalId/Vectors/generate.rb \
#     > tests/Campfire.RailsCompat.Tests/GlobalId/Vectors/parse.json
require "json"

hosts = [
  "campfire", "Campfire", "camp-fire", "camp.fire", "camp_fire", "camp~fire", "camp&ire", "camp!fire",
  "camp$fire", "camp'fire", "camp(fire)", "camp*fire", "camp+fire", "camp,fire", "camp;fire", "camp=fire",
  "camp%41fire", "camp%4", "camp%zzfire", "camp fire", "camp\"fire", "camp<fire", "camp>fire", "camp\\fire",
  "camp^fire", "camp`fire", "camp{fire}", "camp|fire", "camp[fire]", "", "1.2.3.4", "999.1.1.1",
  "[::1]", "[2001:db8::1]", "[v1.x]", "[::1", "[fire]", "user@campfire", "u:p@campfire", "@campfire",
  "us er@campfire", "u%40@campfire", "a@b@campfire", "campfire:", "campfire:80", "campfire:abc", "campfire: 80",
  ":80", "camp:fire:80", "campfire\n", "\tcampfire"
]
paths = [
  "/User/1", "/User/1/2", "/User//1", "/User/1/", "/User/", "/User", "/", "", "//User/1",
  "/Rooms::Open/1", "/User/1%2F2", "/User/a+b", "/User/%E2%9C%93", "/User/%zz", "/Us@er/1", "/User/1;x",
  "/User/1:2", "/User/[1]", "/User/1 2", "/User/1\"", "/User/1%", "/User/(1)", "/User/1!$&'*+,=",
  "/User/1|2", "/User/1^2", "/User/1`", "/User/1{}", "/User/1\\2", "/User/1<2>", "/U%73er/1", "/User/~1",
  "/User/1/2/3/4/5/6/7/8/9/10/11/12/13/14/15/16/17/18/19/20", "/User/1/2/3/4/5/6/7/8/9/10/11/12/13/14/15/16/17/18/19/20/21",
  "/ /1", "/User/1\n", "/User//", "/User///", "/User/%FF", "/User/%C3%A9", "/User/1%00"
]
suffixes = [
  "", "?expires_in", "?a=b&c=d", "?a b", "?\x7Fx", "?<>", "?#", "#", "#frag", "#fr ag", "#a#b", "#a?b/c",
  "#<x>", "?x#y", "?x?y", "#%41", "#%zz", "?%zz", "?%4z", "?%z4", "?%4", "?%", "?a=%", "?a=%2", "?a=%ZZ&b",
  "?&&", "?=", "?a=b;c=d", "?%FF", "?a\tb", "\n", " "
]
others = [
  "GID://campfire/User/1", "Gid://campfire/User/1", "gid:campfire/User/1", "gid:/campfire/User/1",
  "gid:///User/1", "gid://campfire", "gid://campfire/", "http://campfire/User/1", "gid://cämpfire/User/1",
  "gid://campfire/User/ü", " gid://campfire/User/1", "gid:// campfire/User/1", "gid://campfire/User/1 ",
  "gid://campfire/User/1\u0000", "gid://campfire/User/1\r\n", "gid://campfire/User/0x1", "gid://campfire/User/-1",
  "gid://campfire/User/1e3", "gid://campfire/::User/1", "gid://campfire/User::/1", "gid://campfire/user/1",
  "gid://campfire/User/1?expires_at=2099-01-01+00%3A00%3A00+UTC", "gid://campfire/User/%2F", "gid://campfire/User/+",
  "gid://campfire/User/%20", "gid://campfire/User/1%2F", "gid://campfire/User/%2F1", "gid", "gid://", ""
]

gids = hosts.map { |h| "gid://#{h}/User/1" } +
  paths.map { |p| "gid://campfire#{p}" } +
  suffixes.map { |s| "gid://campfire/User/1#{s}" } +
  others

# A model id that isn't valid UTF-8 is written as its bytes
def text(value)
  value.valid_encoding? ? value : { "bytes" => value.bytes }
end

def parse(gid)
  uri = URI::GID.parse(gid)
  model_id = uri.model_id.is_a?(Array) ? uri.model_id.map { |id| text(id) } : text(uri.model_id)
  { "app" => uri.app, "model_name" => uri.model_name, "model_id" => model_id }
rescue URI::Error => e
  { "error" => e.class.name }
rescue => e
  { "raises" => e.class.name }
end

cases = gids.uniq.map { |gid| { "gid" => gid }.merge(parse(gid)) }
puts JSON.pretty_generate({ "versions" => { "ruby" => RUBY_VERSION, "globalid" => GlobalID::VERSION, "uri" => URI::VERSION }, "cases" => cases })

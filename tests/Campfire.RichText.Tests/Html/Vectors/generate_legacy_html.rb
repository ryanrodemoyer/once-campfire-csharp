# Regenerates legacy_html.json: Nokogiri::HTML.fragment (Nokogiri::HTML4, libxml2's legacy HTML
# parser) on hand-picked markup and on random markup built from fragments, as the tree it builds
# and the OpenGraph embed attributes_from_content reads from it
# (reference/lib/rails_ext/actiontext_opengraph_embeds.rb). Run in the reference:
#
#   parity/bin/reference runner -e RAILS_LOG_LEVEL=fatal \
#     tests/Campfire.RichText.Tests/Html/Vectors/generate_legacy_html.rb \
#     > tests/Campfire.RichText.Tests/Html/Vectors/legacy_html.json
require "json"

# Elements as [name, [[attribute, value]...], children...], text and CDATA as strings (adjacent
# ones joined), comments and processing instructions left out
def tree(node)
  children = node.children.each_with_object([]) do |child, nodes|
    if child.text? || child.cdata?
      nodes.last.is_a?(String) ? nodes[-1] += child.content : nodes << child.content
    elsif child.element?
      nodes << tree(child)
    end
  end
  node.element? ? [node.name, node.attribute_nodes.map { |a| [a.name, a.value] }, *children] : children
end

def embed(fragment)
  title = fragment.at_css(".og-embed__title")
  link = title&.at_css("a")
  {
    "href" => link&.[]("href"),
    "image" => fragment.at_css(".og-embed__image img")&.[]("src"),
    "title" => (link || title)&.text&.strip,
    "description" => fragment.at_css(".og-embed__description")&.text&.strip
  }
end

inputs = [
  "", "text", "<p>a</p>", "<div class=\"og-embed__title\"><a href=\"https://example.com/\">Title</a></div>",
  "<actiontext-opengraph-embed><div class=\"og-embed gap\"><div class=\"og-embed__content\"><div class=\"og-embed__title\"><a href=\"https://basecamp.com/\">Basecamp</a></div><div class=\"og-embed__description\">Project management</div></div><div class=\"og-embed__image\"><img src=\"https://basecamp.com/og.png\" class=\"image center\" alt=\"\"></div></div></actiontext-opengraph-embed>",
  # From the fuzz run in plans/fuzz-run-2.md
  "<div class=\"og-embed__title\"><a href=\"https://example.com/page\">&lb&gt;Title&lt;/b&gt;</a&g(t;</div><div class=\"og-embed__description\">&lt;img src=x onerror=alert(1)&g\u0000t;</div>",
  "<actiontext-opengraph-embed><div class=\"og-embed gap\"><div class=\"og-embed__content\"><div </class=\"og-embed__title\"><a href=\"https://twitter.catus/1\">Tweet</a></div><div class=\"og-embed__description\">desc</div></div><div class=\"og-embed__image\"><img src=\"https://pbs.twimg.com/profile_im\u0000ages/x.jpg\" class=\"image center\" alt=\"\" /></div></div></actiontext-open/actiontext-opengraph-embed>",
  "<actiontext-opengraph-embed><div class=\"og-embed gap\"><<div>;div class=\"og-embed__content\"><div class=\"og-embed__title\"><a href=\"https://basecamp.com/\">Basecamp</a>lt;/div><div class=\"og-embed__description\">Project management</div></div><div class=\"og-embed__image\"><img src=\"https://<![CDATA[basecamp.com/og.png\" class=\"image center\" alt=\"\"></div></div></actiontext-opengraph-embed>",
  "<div class=\"og-embed__title\"><a href=&href=\"https://example.com/\">AAAA<td>AAAA\rAAAA</a></div><div class=\"og-embed__descriot;>  spaced   </div>",
  "<actiontext-opengraph-embed><div class=\"og-embed gap\"><div class=\"og-embed_ntent\"><div class=\"og-embed__title\"><a href=\"https://basecamp.com/\">Basecamp</a&guot;>Basecamp</a></div><div class=\"og-embed__description\">Project management</div></div></div></actiontext-opengraph-embed>",
  # Recovery
  "<a href=x>1<a href=y>2</a>3", "<p>1<p>2<div>3</div>4", "<table><tr><td>1<td>2</table>", "<ul><li>1<li>2</ul>",
  "<dl><dt>1<dd>2<dt>3</dl>", "<b>1<p>2</b>3</p>", "<div>1</span>2</div>3", "<td>1</td>", "<tr><td>1</tr>", "</p>x", "</div>x",
  "<div><table><tr><td><div>1</div></td></tr></table></div>", "<div>a</td>b</div>c", "<span><div>x</div></span>",
  "<font><center>x</center></font>", "<h1>a<h2>b</h1>c", "<form><form>x</form></form>", "<select><option>1<option>2</select>",
  "<head>x</head>y", "<title>t</title>x", "<meta charset=latin1>é", "<script>a</div>b</script>c", "<script></div>x</script>y",
  "<style>a<b>c</style>d", "<script>a</scriptx>b</script>c", "<textarea><b>x</b></textarea>", "<xmp><b>x</b></xmp>",
  "</body>x<div>y</div>", "</html>x", "<body class=x>y", "x<body>y</body>z", "<html><body>x", "a</body><body>b",
  "<body/>x", "<div/>x", "<a/>x", "<br>x<br/>y</br>z", "<img src=a>x</img>y", "<input checked value=1>",
  "<a href>x</a>", "<a href=\"\">x</a>", "<a href='x' href='y'>z</a>", "<a HREF=X>y</A>", "<A Href='x'>y</a>",
  "<a href=a\"b>x</a>", "<a href=\"a>b\">x</a>", "<a href=\"x>y", "<a href=\"x", "<a href=x", "<a", "<", "<div", "</", "</div",
  "</ div>x", "</div x>y", "<div>x</div x>y", "< div>x", "<_a>x</_a>", "<:a>x", "<.a>x", "<a.b>x</a.b>", "<a-b>x</a-b>",
  "<x-y class=og-embed__title>t</x-y>", "<a b c=d e>x</a>", "<a =x>y</a>", "<a \"x\">y</a>", "<a/ b>x</a>",
  "<!-- c -->x", "<!-->x", "<!--->x", "<!-- a -- b -->x", "<!-- a --!>x", "<!-- a", "<!x>y", "<!>y", "<!DOCTYPE html>x",
  "<!doctype html public \"a>b\" \"c\">x", "<?pi x?>y", "<?>x", "<? x>y", "<?pi", "<![CDATA[x]]>y",
  "&amp;&lt;&gt;&quot;&apos;&nbsp;&eacute;&hellip;", "&amp", "&ampx;", "&bogus;", "& x", "&#106;&#x6A;&#X6a;", "&#;", "&#x;",
  "&#+58;", "&#0;", "&#1;", "&#55296;", "&#1114112;", "&#99999999999;", "&#65;x", "&#x41", "a&b", "&é;", "&amp\u0000x",
  "<a href=\"&#106;ava&#0;script\">x</a>", "<a href=\"&amp;&lt&bogus;&#x;\">x</a>", "<a href=\"&#+58;x\">y</a>",
  "a\u0000b", "<div>a\u0000b</div>", "\u0000a", "<a href=\"x\u0000y\">z</a>", "<div\u0000>x", "\u000b\u0001x\u007f",
  "a\r\nb", "<div> </div>", "<div>\n<b>x</b>\n<i>y</i>\n</div>", " <div>x</div> ", "\n<body>x", "x\n  <BODY>y",
  "😀<div title=\"😀\">é</div>", "<div class=\"og-embed__title\">  plain title  </div>",
  "<div class=\"a og-embed__title\tb\"><a>t</a></div>", "<div class=\"og-embed__TITLE\">t</div>",
  "<div class=\"og-embed__image\"><p><img src=\"https://e.com/1.png\"></p></div><img src=\"https://e.com/2.png\">",
  "<div class=\"og-embed__description\"><b>d</b>e<!-- c -->f<script>g</script></div>",
  "<div class=\"og-embed__title\"><span><a href=\"https://a.com/\">1</a></span><a href=\"https://b.com/\">2</a></div>",
  "<div class=\"og-embed__title\"></div><div class=\"og-embed__title\"><a href=\"https://b.com/\">2</a></div>",
  "<" + "a" * 120 + ">x</" + "a" * 120 + ">", "<div " + "b" * 120 + "=1>x</div>"
]

pieces = [
  "<div class=\"og-embed__title\">", "<div class=\"og-embed__description\">", "<div class=\"og-embed__image\">",
  "<div class=og-embed__title>", "<DIV CLASS=\"og-embed__description\">", "<actiontext-opengraph-embed>",
  "</actiontext-opengraph-embed>", "<a href=\"https://example.com/\">", "<a href=https://x.com/>", "<a>", "</a>", "</A>",
  "<div>", "</div>", "<span>", "</span>", "<p>", "</p>", "<b>", "</b>", "<i>", "</i>", "<em>", "<td>", "</td>", "<tr>", "<th>",
  "<table>", "</table>", "<tbody>", "<li>", "<ul>", "</ul>", "<ol>", "<dl>", "<dt>", "<dd>", "<h1>", "</h1>", "<h3>", "<pre>",
  "<center>", "<font>", "<form>", "</form>", "<fieldset>", "<select>", "<option>", "<textarea>", "</textarea>", "<iframe>",
  "<noscript>", "<object>", "<address>", "<blockquote>", "<caption>", "<col>", "<colgroup>", "<head>", "<title>", "</title>",
  "<meta>", "<link>", "<base>", "<img src=\"https://example.com/i.png\">", "<img src=x alt>", "<br>", "<hr/>", "<input checked>",
  "<script>", "</script>", "<style>", "</style>", "<body>", "</body>", "<html>", "</html>", "<body/>", "<div/>", "<x-y>", "<_a>",
  "<!--", "-->", "--!>", "<!-- c -->", "<!DOCTYPE html>", "<!x>", "<?pi x?>", "<?", "<![CDATA[", "]]>", "&amp;", "&lt", "&#106;",
  "&#+58;", "&#x6A;", "&#0;", "&bogus;", "&nbsp;", "&", "\u0000", "\u000b", "<a href=\"", "<a href=x", "<div class=\"", "\"", "'",
  "=", " ", " ", "\n", "\r", "\t", "<", ">", "/>", "/", "</", "</ div>", "text", "Title", "é", "😀", "AAAA"
]
random = Random.new(2)
3000.times do
  inputs << Array.new(random.rand(1..16)) { pieces[random.rand(pieces.size)] }.join
end

cases = inputs.uniq.map do |input|
  fragment = Nokogiri::HTML.fragment(input)
  { "input" => input, "tree" => tree(fragment), "embed" => embed(fragment) }
end
# One case per line
puts "{\"nokogiri\": #{Nokogiri::VERSION.to_json}, \"libxml2\": #{Nokogiri::VERSION_INFO["libxml"]["loaded"].to_json}, \"cases\": ["
puts cases.map { |c| JSON.generate(c) }.join(",\n")
puts "]}"

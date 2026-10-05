using System.Buffers;
using System.Text;

namespace Campfire.Templates.Tests;

// Mirrors ActionView::OutputBuffer (reference: actionview/lib/action_view/buffers.rb) and
// ERB::Util.html_escape. Expected strings are what Rails prints for the same values.
public sealed class HtmlWriterTests
{
    [Fact]
    public void Append_escapes_the_five_html_characters_like_ERB_Util()
    {
        Assert.Equal("&lt;b&gt;&quot;Tom&quot; &amp; &#39;Jerry&#39;&lt;/b&gt;", Render(w => w.Append("<b>\"Tom\" & 'Jerry'</b>")));
    }

    [Fact]
    public void Append_leaves_other_characters_alone()
    {
        Assert.Equal("café / 日本 🔥 `=", Render(w => w.Append("café / 日本 🔥 `=")));
    }

    [Fact]
    public void Append_writes_safe_content_unescaped()
    {
        Assert.Equal("<i>x</i><b>y</b>", Render(w =>
        {
            w.Append(new SafeString("<i>x</i>"));
            w.Append(new Bold("y"));
        }));
    }

    [Fact]
    public void AppendRaw_never_escapes()
    {
        Assert.Equal("<i>&</i>", Render(w => w.AppendRaw("<i>&</i>")));
    }

    [Fact]
    public void Null_and_default_append_nothing_like_nil()
    {
        Assert.Equal("", Render(w =>
        {
            w.Append((string?)null);
            w.AppendRaw((string?)null);
            w.Append((IHtml?)null);
            w.Append(default(SafeString));
        }));
    }

    [Fact]
    public void Numbers_and_booleans_render_like_Ruby_to_s()
    {
        Assert.Equal("0 -42 9223372036854775807 -9223372036854775808 true false", Render(w =>
        {
            w.Append(0);
            w.WriteLiteral(" "u8);
            w.Append(-42);
            w.WriteLiteral(" "u8);
            w.Append(long.MaxValue);
            w.WriteLiteral(" "u8);
            w.AppendRaw(long.MinValue);
            w.WriteLiteral(" "u8);
            w.Append(true);
            w.WriteLiteral(" "u8);
            w.AppendRaw(false);
        }));
    }

    [Fact]
    public void Long_strings_keep_surrogate_pairs_whole_across_chunks()
    {
        var text = new string('a', 4095) + "🔥<" + new string('b', 9000);

        Assert.Equal(text.Replace("<", "&lt;", StringComparison.Ordinal), Render(w => w.Append(text)));
    }

    [Fact]
    public void Capture_diverts_output_and_returns_it_as_safe()
    {
        var output = Render(w =>
        {
            w.WriteLiteral("a"u8);
            var captured = w.Capture(() => w.Append("<x>"));
            w.WriteLiteral("b"u8);
            w.Append(captured);
            w.Append(captured.Value);
        });

        Assert.Equal("ab&lt;x&gt;&amp;lt;x&amp;gt;", output);
    }

    [Fact]
    public void Capture_restores_the_output_when_the_body_throws()
    {
        var output = Render(w =>
        {
            Assert.Throws<InvalidOperationException>(() => w.Capture(() => throw new InvalidOperationException()));
            w.WriteLiteral("after"u8);
        });

        Assert.Equal("after", output);
    }

    [Fact]
    public void SafeString_escape_matches_html_escape()
    {
        Assert.Equal("&amp;&lt;&gt;&quot;&#39;", SafeString.Escape("&<>\"'").Value);
        Assert.Equal("", SafeString.Escape(null).Value);
        Assert.Equal(new SafeString("a"), new SafeString("a"));
        Assert.Equal(SafeString.Empty, new SafeString(""));
    }

    static string Render(Action<HtmlWriter> render)
    {
        var buffer = new ArrayBufferWriter<byte>();
        render(new HtmlWriter(buffer));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    sealed class Bold(string text) : IHtml
    {
        public void WriteTo(HtmlWriter writer)
        {
            writer.WriteLiteral("<b>"u8);
            writer.Append(text);
            writer.WriteLiteral("</b>"u8);
        }
    }
}

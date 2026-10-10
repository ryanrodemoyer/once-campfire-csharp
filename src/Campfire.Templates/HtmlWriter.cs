using System.Buffers;
using System.Globalization;
using System.Text;

namespace Campfire.Templates;

/// <summary>
/// The output buffer compiled templates write to, mirroring <c>ActionView::OutputBuffer</c>
/// (reference: actionview/lib/action_view/buffers.rb). Template text goes through
/// <see cref="WriteLiteral"/> (<c>safe_append=</c>), <c>&lt;%= %&gt;</c> through
/// <see cref="Append(string)"/> (<c>append=</c>: escaped unless HTML-safe) and <c>&lt;%== %&gt;</c>
/// through <see cref="AppendRaw(string)"/> (<c>safe_expr_append=</c>: never escaped). Null appends
/// nothing, as Ruby's nil does.
/// </summary>
public sealed class HtmlWriter(IBufferWriter<byte> output)
{
    IBufferWriter<byte> output = output;

    public void WriteLiteral(ReadOnlySpan<byte> utf8) => output.Write(utf8);

    public void Append(string? value)
    {
        if (value is not null)
        {
            HtmlEscaper.WriteEscaped(output, value);
        }
    }

    public void Append(SafeString value) => AppendRaw(value.Value);

    public void Append(IHtml? value) => value?.WriteTo(this);

    public void Append(int value) => WriteFormatted(value);

    public void Append(long value) => WriteFormatted(value);

    public void Append(bool value) => WriteLiteral(value ? "true"u8 : "false"u8);

    public void AppendRaw(string? value)
    {
        if (value is not null)
        {
            HtmlEscaper.WriteUtf8(output, value);
        }
    }

    public void AppendRaw(SafeString value) => AppendRaw(value.Value);

    public void AppendRaw(IHtml? value) => value?.WriteTo(this);

    public void AppendRaw(int value) => WriteFormatted(value);

    public void AppendRaw(long value) => WriteFormatted(value);

    public void AppendRaw(bool value) => Append(value);

    /// <summary>
    /// Runs <paramref name="body"/> with its output diverted and returns that output as a
    /// <see cref="SafeString"/>, like <c>OutputBuffer#capture</c>. This is the buffer primitive;
    /// <c>capture</c>/<c>content_for</c> helper rules belong to the helpers built on it.
    /// </summary>
    public SafeString Capture(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var previous = output;
        var captured = new ArrayBufferWriter<byte>();
        output = captured;
        try
        {
            body();
        }
        finally
        {
            output = previous;
        }
        return new SafeString(Encoding.UTF8.GetString(captured.WrittenSpan));
    }

    /// <summary>
    /// Like <see cref="Capture"/> but returns the raw UTF-8 bytes, avoiding the decode/re-encode
    /// round trip when the result is cached as bytes.
    /// </summary>
    public byte[] CaptureBytes(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var previous = output;
        var captured = new ArrayBufferWriter<byte>();
        output = captured;
        try
        {
            body();
        }
        finally
        {
            output = previous;
        }
        return captured.WrittenSpan.ToArray();
    }

    void WriteFormatted<T>(T value) where T : IUtf8SpanFormattable
    {
        var span = output.GetSpan(20);
        value.TryFormat(span, out var written, default, CultureInfo.InvariantCulture);
        output.Advance(written);
    }
}

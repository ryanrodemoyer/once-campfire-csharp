namespace Campfire.Templates.Generator.Tests;

public sealed class TemplateEmitterTests
{
    [Theory]
    [InlineData("plain", "\"plain\"u8")]
    [InlineData("a\"b\\c", "\"a\\\"b\\\\c\"u8")]
    [InlineData("\n\r\t\0\u007f", "\"\\n\\r\\t\\u0000\\u007f\"u8")]
    [InlineData("\u0085\u2028\u2029", "\"\\u0085\\u2028\\u2029\"u8")]
    [InlineData("café 日本", "\"café 日本\"u8")]
    [InlineData("🔥", "\"\\ud83d\\udd25\"u8")]
    public void Writes_text_as_an_exact_utf8_literal(string text, string literal)
    {
        Assert.Equal(literal, TemplateEmitter.Utf8Literal(text));
    }
}

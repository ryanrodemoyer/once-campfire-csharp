using System.Text.Json;
using Campfire.RichText.Sanitize;

namespace Campfire.RichText.Tests.Sanitize;

public class RichTextSanitizeVectorTests
{
    private static readonly string ExpectedJsonPath = Path.Combine(AppContext.BaseDirectory, "../../../../../vectors/richtext/expected.json");

    [Fact]
    public void WebUrlVectorsPass()
    {
        var json = File.ReadAllText(ExpectedJsonPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var webUrls = root.GetProperty("web_urls").EnumerateArray();
        var tested = 0;

        foreach (var item in webUrls)
        {
            var value = item.GetProperty("value").GetString()!;
            var host = item.GetProperty("host").GetString()!;
            var resultProp = item.GetProperty("result");

            if (resultProp.TryGetProperty("ok", out var okProp))
            {
                var expected = okProp.ValueKind == JsonValueKind.Null ? null : okProp.GetString();
                string? actual = null;
                try
                {
                    actual = OpengraphEmbedUrl.WebUrl(value, host);
                }
                catch (Exception ex)
                {
                    Assert.Fail($"Expected ok={expected}, but threw {ex.GetType().Name}: {ex.Message} for value={value}");
                }
                Assert.Equal(expected, actual);
            }
            else if (resultProp.TryGetProperty("error", out var errorProp))
            {
                var errorType = errorProp.GetString()!;
                Assert.ThrowsAny<Exception>(() => OpengraphEmbedUrl.WebUrl(value, host));
            }

            tested++;
        }

        Assert.Equal(54, tested);
    }

    [Fact]
    public void FilteredCorpusVectorsPass()
    {
        var json = File.ReadAllText(ExpectedJsonPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var cases = root.GetProperty("cases").EnumerateArray();
        var tested = 0;
        var passed = 0;

        var failures = new List<string>();

        foreach (var c in cases)
        {
            var name = c.GetProperty("name").GetString()!;
            var host = c.GetProperty("host").GetString()!;
            var body = c.GetProperty("body").GetString()!;
            var filteredProp = c.GetProperty("filtered");

            if (filteredProp.TryGetProperty("ok", out var okProp))
            {
                var expected = okProp.GetString()!;
                try
                {
                    var actual = ContentFilters.ApplyTextMessagePresentationFilters(body, host);
                    if (actual != expected)
                    {
                        failures.Add($"[{name}] expected:\n{expected}\nbut got:\n{actual}");
                    }
                    else
                    {
                        passed++;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"[{name}] threw {ex.GetType().Name}: {ex.Message}");
                }

                tested++;
            }
        }

        if (failures.Count > 0)
        {
            Assert.Fail($"Failed {failures.Count} of {tested} cases:\n" + string.Join("\n---\n", failures));
        }

        Assert.Equal(641, tested);
        Assert.Equal(tested, passed);
    }
}

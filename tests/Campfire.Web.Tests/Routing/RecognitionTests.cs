using Campfire.Vectors;
using Campfire.Web.Routing;

namespace Campfire.Web.Tests.Routing;

public class RecognitionTests
{
    static RouteTable Table => Routes.Table;

    [Fact]
    public void The_table_is_bin_rails_routes_in_order()
    {
        var rails = CampfireVectors.RoutesFile.Routes;
        Assert.Equal(rails.Count, Table.Routes.Count);
        for (var i = 0; i < rails.Count; i++)
        {
            var ours = Table.Routes[i];
            Assert.Equal(
                (rails[i].Verb, rails[i].Path, rails[i].Endpoint, Sorted(rails[i].Defaults)),
                (ours.Verb, ours.Spec.Spec, ours.Endpoint, Sorted(ours.Defaults)));
        }
    }

    [Theory]
    [MemberData(nameof(CampfireVectors.Recognitions), MemberType = typeof(CampfireVectors))]
    public void Recognizes_paths_like_rails(RecognitionCase sample)
    {
        var match = Table.Recognize(sample.Verb, PathEscaping.NormalizePath(sample.Path));
        if (sample.Endpoint is null)
        {
            // recognize_path also raises for a route whose controller doesn't exist.
            Assert.True(match is null || RoutesSource.MissingControllers.Contains(match.Route.Controller), match?.Route.ToString());
            return;
        }
        Assert.NotNull(match);
        Assert.Equal(sample.Endpoint, match.Route.Endpoint);
        var parameters = match.Parameters.Where(pair => pair.Key is not ("controller" or "action"));
        Assert.Equal(Sorted(sample.Params), Sorted(parameters));
    }

    [Fact]
    public void Path_parameters_are_defaults_then_controller_and_action_then_captures()
    {
        var match = Table.Recognize("POST", "/rooms/1/abc/messages/7/boosts")!;
        Assert.Equal(
            ["format=json", "controller=messages/boosts/by_bots", "action=create", "room_id=1", "bot_key=abc", "message_id=7"],
            match.Parameters.Select(pair => $"{pair.Key}={pair.Value}"));

        var sidebar = Table.Recognize("GET", "/users/5/sidebar")!;
        Assert.Equal(["user_id=5", "controller=users/sidebars", "action=show"], sidebar.Parameters.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    [Fact]
    public void Unknown_verbs_are_405_where_a_pattern_matches_and_404_elsewhere()
    {
        Assert.Throws<UnknownHttpMethodException>(() => Table.Recognize("FOO", "/rooms/1"));
        Assert.Null(Table.Recognize("FOO", "/nope"));
        Assert.Null(Table.Recognize("OPTIONS", "/rooms/1"));
        Assert.Null(Table.Recognize("PROPFIND", "/rooms/1"));
    }

    [Fact]
    public void A_parameter_that_isnt_utf8_once_decoded_is_a_bad_request()
    {
        var error = Assert.Throws<BadRequestException>(() => Table.Recognize("GET", "/rooms/%FF"));
        Assert.Equal(400, HttpErrors.StatusFor(error));
        Assert.Equal("a+b", Table.Recognize("GET", "/rooms/a+b")!.Parameters["id"]);
        Assert.Equal("%zz", Table.Recognize("GET", "/rooms/%zz")!.Parameters["id"]);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("foo", "/foo")]
    [InlineData("/foo/", "/foo")]
    [InlineData("/%ab", "/%AB")]
    [InlineData("//rooms//1//", "/rooms/1")]
    [InlineData("/a%2fb%aF%Fa", "/a%2Fb%aF%Fa")]
    [InlineData("/rooms/1%2f/", "/rooms/1%2F")]
    public void Normalizes_paths_like_journey(string? path, string expected) =>
        Assert.Equal(expected, PathEscaping.NormalizePath(path));

    [Fact]
    public void Recognizes_like_a_first_match_scan()
    {
        var verbs = new[] { "GET", "HEAD", "POST", "PATCH", "PUT", "DELETE", "OPTIONS" };
        var matchedRows = new HashSet<int>();
        var (missed, failed) = (0, 0);
        foreach (var verb in verbs)
        {
            foreach (var path in Corpus())
            {
                var scanned = RecognizeByScanning(verb, path);
                try
                {
                    Assert.Equal(scanned?.Index, Table.Recognize(verb, path)?.Route.Index);
                }
                catch (BadRequestException)
                {
                    // A matched route whose capture isn't UTF-8 once decoded.
                    Assert.NotNull(scanned);
                    failed++;
                    continue;
                }
                if (scanned is null)
                {
                    missed++;
                }
                else
                {
                    matchedRows.Add(scanned.Index);
                }
            }
        }

        // Every row answers some path but the three `GET /rooms/:id`, drawn first, shadows.
        var unmatched = Table.Routes.Where(route => !matchedRows.Contains(route.Index)).Select(route => $"{route.Verb} {route.Spec}");
        Assert.Equal(["GET /rooms/opens(.:format)", "GET /rooms/closeds(.:format)", "GET /rooms/directs(.:format)"], unmatched);
        Assert.True(missed > 10_000 && failed > 100, $"{missed} paths missed, {failed} failed");
    }

    // The straightforward recognizer: the verb's routes in table order, the first whose regex matches.
    static Route? RecognizeByScanning(string verb, string path) =>
        Table.Routes.FirstOrDefault(route => route.Verb == (verb == "HEAD" ? "GET" : verb) && route.Spec.Regex.IsMatch(path));

    // Every recognition sample and every route filled in, as given and normalized, with near misses:
    // other formats, trailing and doubled slashes, a character or segment more or less, other case,
    // escapes that aren't UTF-8, raw UTF-8.
    static SortedSet<string> Corpus()
    {
        var seeds = CampfireVectors.RoutesFile.Recognitions.Select(sample => sample.Path)
            .Concat(CampfireVectors.RoutesFile.Routes.SelectMany(route => FilledIn(route.Path)));
        var corpus = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var seed in seeds)
        {
            foreach (var path in (string[])[seed, PathEscaping.NormalizePath(seed)])
            {
                foreach (var variant in Variants(path))
                {
                    corpus.Add(PathEscaping.NormalizePath(variant));
                }
            }
        }
        return corpus;
    }

    static IEnumerable<string> FilledIn(string pattern)
    {
        foreach (var format in (string[])["", ".:format"])
        {
            var spec = pattern.Replace("(.:format)", format, StringComparison.Ordinal);
            foreach (var (param, glob) in ((string, string)[])[("1", "photo"), ("opens", "dir/photo.tar.gz"), ("caf%C3%A9", "a%2Fb/c%20d"), ("café", "😀/dir/é.tar")])
            {
                yield return System.Text.RegularExpressions.Regex.Replace(spec, @"([:*])(\w+)", match =>
                    match.Groups[1].Value == "*" ? glob : match.Groups[2].Value == "format" ? "json" : param);
            }
        }
    }

    static IEnumerable<string> Variants(string path)
    {
        yield return path;
        yield return path.ToUpperInvariant();
        yield return path.TrimStart('/');
        foreach (var suffix in (string[])[".json", ".turbo_stream", ".1.2", ".", "/", "x", "/x", "/new", "/edit", "%FF", "%2F", "é", "😀.json"])
        {
            yield return path + suffix;
        }
        if (path.Length > 1)
        {
            yield return path[..^1];
        }
        var slash = path.LastIndexOf('/');
        if (slash > 0)
        {
            var parent = path[..slash];
            yield return parent;
            yield return parent + "/%E9";
            yield return parent + "/@42";
            yield return parent + "/opens";
        }
    }

    static string Sorted(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Join(",", pairs.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
}

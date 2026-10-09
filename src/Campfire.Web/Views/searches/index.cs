using Campfire.Data.Records;

namespace Campfire.Web.Helpers;

// reference/app/views/searches/index.html.erb and searches_helper.rb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary><c>searches/index</c>: search results and recent searches.</summary>
    [ErbTemplate("searches/index.html.erb.cs")]
    public partial void SearchesIndex(HtmlWriter w, SearchPage page);

    /// <summary>
    /// <c>search_results_tag</c> (reference/app/helpers/searches_helper.rb).
    /// </summary>
    public static IHtml SearchResultsTag(HtmlWriter w, Action body) =>
        Tag.Div(new()
        {
            { "id", "search-results" },
            { "class", "messages searches__results" },
            {
                "data", new HtmlOptions
                {
                    { "controller", "search-results" },
                    { "search_results_target", "messages" },
                    { "search_results_me_class", "message--me" },
                    { "search_results_threaded_class", "message--threaded" },
                    { "search_results_mentioned_class", "message--mentioned" },
                    { "search_results_formatted_class", "message--formatted" },
                }
            },
        }, () => CaptureOrLastText(w, "\n", body));
}
#pragma warning restore IDE0060

/// <summary>What <c>searches/index</c> shows.</summary>
/// <param name="Query">The sanitized query if present, else null (<c>@query</c>).</param>
/// <param name="RawQuery">The raw query param, for the search input value (<c>params[:q]</c>).</param>
/// <param name="RecentSearches">The user's recent searches (<c>@recent_searches</c>).</param>
/// <param name="ReturnToRoom">The room to return to (<c>@return_to_room</c>).</param>
/// <param name="Messages">The matching messages (<c>@messages</c>).</param>
public sealed record SearchPage(
    string? Query,
    string? RawQuery,
    IReadOnlyList<Search> RecentSearches,
    Room? ReturnToRoom,
    IReadOnlyList<MessageView> Messages);

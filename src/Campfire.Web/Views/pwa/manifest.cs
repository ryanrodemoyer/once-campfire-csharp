using Campfire.RailsCompat.Formatting;

namespace Campfire.Web.Helpers;

// reference/app/views/pwa/manifest.json.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary>
    /// <c>pwa/manifest.json</c>: the installable app's name, icons and screenshots.
    /// <paramref name="accountName"/> is <c>Current.account&amp;.name</c> (only nil becomes "Campfire").
    /// </summary>
    [ErbTemplate("pwa/manifest.json.erb.cs")]
    public partial void PwaManifest(HtmlWriter w, string? accountName, DateTimeOffset? accountUpdatedAt);

    /// <summary>
    /// <c>fresh_account_logo_path</c> as this template emits it. <c>route_for</c> writes
    /// <c>size</c> before <c>v</c>; <see cref="Routes.FreshAccountLogoPath"/> writes <c>v</c> first.
    /// </summary>
    public static string ManifestAccountLogo(DateTimeOffset? updatedAt, string? size = null)
    {
        var pairs = new List<string>();
        if (size is not null)
        {
            pairs.Add($"size={size}");
        }

        if (updatedAt is { } at)
        {
            pairs.Add($"v={TimeFormats.ToFsNumber(at)}");
        }

        var path = Routes.AccountLogoPath();
        return pairs.Count == 0 ? path : $"{path}?{string.Join('&', pairs)}";
    }
}
#pragma warning restore IDE0060

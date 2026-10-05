using System.Text;
using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;
using Campfire.RichText.Html;
using Campfire.RichText.Sanitize;

namespace Campfire.Jobs.OpenGraph;

/// <summary>
/// An exception the Rails code raises out of <c>Opengraph::Metadata.from_url</c> or
/// <c>valid?</c>, so <c>UnfurlLinksController#create</c> answers 500. <see cref="RubyClass"/>
/// names what Ruby raised.
/// </summary>
public sealed class OpenGraphRaisedException(string rubyClass, Exception? innerException = null)
    : Exception($"raised {rubyClass}", innerException)
{
    public string RubyClass { get; } = rubyClass;
}

/// <summary>
/// <c>Opengraph::Metadata</c> and its <c>Fetching</c> concern
/// (reference/app/models/opengraph/metadata.rb, reference/app/models/opengraph/metadata/fetching.rb).
/// </summary>
public sealed class OpenGraphMetadata
{
    static readonly string[] TwitterHosts = ["twitter.com", "www.twitter.com", "x.com", "www.x.com"];
    const string fxTwitterHost = "fxtwitter.com";
    static readonly string[] AllowedImageContentTypes = ["image/jpeg", "image/png", "image/gif", "image/webp"];

    // The model's attributes as instance variables, in the order they were first assigned, which
    // is the order `render json:` emits them.
    readonly List<KeyValuePair<string, string?>> attributes;

    OpenGraphMetadata(List<KeyValuePair<string, string?>> attributes)
    {
        this.attributes = attributes;
    }

    public string? Title => this["title"];
    public string? Url => this["url"];
    public string? Image => this["image"];
    public string? Description => this["description"];

    public string? this[string key] => attributes.FirstOrDefault(a => a.Key == key).Value;

    /// <summary>
    /// <c>UnfurlLinksController#create</c> after <c>params.require(:url)</c>: the JSON
    /// <c>render json: opengraph</c> sends, or null for <c>head :no_content</c>. Throws
    /// <see cref="OpenGraphRaisedException"/> where the action raises.
    /// </summary>
    public static async Task<string?> UnfurlAsync(OpenGraphFetch fetch, string url, CancellationToken cancellationToken = default)
    {
        var opengraph = await FromUrlAsync(fetch, url, cancellationToken).ConfigureAwait(false);
        return await opengraph.ValidateAsync(fetch, cancellationToken).ConfigureAwait(false) ? opengraph.ToJson() : null;
    }

    /// <summary><c>Metadata.from_url(url)</c></summary>
    public static async Task<OpenGraphMetadata> FromUrlAsync(OpenGraphFetch fetch, string url, CancellationToken cancellationToken = default)
    {
        var body = await FetchDocumentAsync(fetch, url, cancellationToken).ConfigureAwait(false);
        var found = OpenGraphDocument.OpenGraphAttributes(body);
        string? Og(string key) => found.FirstOrDefault(a => a.Key == key).Value;

        var canonicalUrl = await ValidCanonicalUrlAsync(fetch, Og("url"), url, cancellationToken).ConfigureAwait(false);
        var image = await ValidImageContentTypeAsync(fetch, Og("image"), cancellationToken).ConfigureAwait(false);

        var metadata = new OpenGraphMetadata([.. found.Select(a => KeyValuePair.Create(a.Key, (string?)a.Value))]);
        metadata.Assign("url", canonicalUrl);
        metadata.Assign("image", image);
        return metadata;
    }

    /// <summary>
    /// <c>valid?</c>: sanitizes the title and description first (<c>before_validation</c>), then
    /// checks presence and, when there's an image, that it's a valid location.
    /// </summary>
    public async Task<bool> ValidateAsync(OpenGraphFetch fetch, CancellationToken cancellationToken = default)
    {
        foreach (var key in (string[])["title", "description"])
        {
            Assign(key, this[key] is { } value ? Sanitize(StripTags(value)) : null);
        }
        var valid = !OpenGraphDocument.IsBlank(Title) && !OpenGraphDocument.IsBlank(Url) && !OpenGraphDocument.IsBlank(Description);
        if (!OpenGraphDocument.IsBlank(Image))
        {
            valid &= await new OpenGraphLocation(fetch, Image).IsValidAsync(cancellationToken).ConfigureAwait(false);
        }
        return valid;
    }

    /// <summary>
    /// <c>render json: opengraph</c> after <c>valid?</c>: <c>instance_values</c>, which by then
    /// include the validation context and the (empty) errors.
    /// </summary>
    public string ToJson()
    {
        var json = new JsonObject();
        foreach (var (key, value) in attributes)
        {
            json[key] = value;
        }
        json["context_for_validation"] = new JsonObject { ["context"] = null };
        json["errors"] = new JsonObject();
        return RailsJson.Encode(json);
    }

    void Assign(string key, string? value)
    {
        var index = attributes.FindIndex(a => a.Key == key);
        if (index < 0)
        {
            attributes.Add(KeyValuePair.Create(key, value));
        }
        else
        {
            attributes[index] = KeyValuePair.Create(key, value);
        }
    }

    /// <summary>
    /// <c>fetch_document(untrusted_url)</c>: tweets are read through fxtwitter.com. A tweet whose
    /// fxtwitter page can't be read raises (<c>nil.force_encoding</c>).
    /// </summary>
    static async Task<byte[]?> FetchDocumentAsync(OpenGraphFetch fetch, string url, CancellationToken cancellationToken)
    {
        if (!IsTweetUrl(url))
        {
            return await new OpenGraphLocation(fetch, url).ReadHtmlAsync(cancellationToken).ConfigureAwait(false);
        }
        var fxtwitterUrl = ReplaceTwitterDomainForOpenGraphSupport(url);
        return await new OpenGraphLocation(fetch, fxtwitterUrl).ReadHtmlAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new OpenGraphRaisedException("NoMethodError");
    }

    /// <summary><c>tweet_url?</c>: <c>URI::InvalidURIError</c> is rescued, <c>URI::InvalidComponentError</c> isn't.</summary>
    static bool IsTweetUrl(string url)
    {
        try
        {
            var uri = RubyUri.Parse(url);
            return TwitterHosts.Contains(uri.Host) && !OpenGraphDocument.IsBlank(uri.Path) && uri.Path != "/";
        }
        catch (RubyUriInvalidComponentException e)
        {
            throw new OpenGraphRaisedException("URI::InvalidComponentError", e);
        }
        catch (RubyUriException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>replace_twitter_domain_for_opengraph_support</c>: Twitter and X don't serve Open Graph
    /// tags, fxtwitter.com does.
    /// </summary>
    static string? ReplaceTwitterDomainForOpenGraphSupport(string url)
    {
        try
        {
            var uri = RubyUri.Parse(url);
            if (TwitterHosts.Contains(uri.Host))
            {
                uri.Host = fxTwitterHost;
            }
            return uri.ToUriString();
        }
        catch (RubyUriException e) when (e is not RubyUriInvalidComponentException)
        {
            return null;
        }
    }

    /// <summary><c>valid_canonical_url(url, fallback)</c></summary>
    static async Task<string> ValidCanonicalUrlAsync(OpenGraphFetch fetch, string? url, string fallback, CancellationToken cancellationToken) =>
        url is not null && await new OpenGraphLocation(fetch, url).IsValidAsync(cancellationToken).ConfigureAwait(false) ? url : fallback;

    /// <summary><c>valid_image_content_type(image)</c>: kept only when a HEAD says it's a JPEG, PNG, GIF or WebP.</summary>
    static async Task<string?> ValidImageContentTypeAsync(OpenGraphFetch fetch, string? image, CancellationToken cancellationToken)
    {
        if (OpenGraphDocument.IsBlank(image))
        {
            return null;
        }
        try
        {
            RubyUri.Parse(image!);
        }
        catch (RubyUriException e)
        {
            fetch.Warn?.Invoke($"Failed to fetch image content tpye: {image} ({e.Message})");
            return null;
        }
        var contentType = await new OpenGraphLocation(fetch, image).FetchContentTypeAsync(cancellationToken).ConfigureAwait(false);
        return contentType is not null && AllowedImageContentTypes.Contains(contentType.ToLowerInvariant()) ? image : null;
    }

    /// <summary>
    /// <c>strip_tags</c> (<c>Rails::HTML5::FullSanitizer</c>): the HTML5 fragment's text nodes,
    /// in order (<c>TextOnlyScrubber</c>, bottom up), serialized.
    /// </summary>
    internal static string StripTags(string html)
    {
        if (html.Length == 0)
        {
            return html;
        }
        var text = new StringBuilder();
        foreach (var node in Parse(html).Descendants().OfType<HtmlText>())
        {
            text.Append(node.Data);
        }
        var fragment = new HtmlFragment();
        if (text.Length > 0)
        {
            fragment.AppendChild(new HtmlText(text.ToString()));
        }
        return fragment.ToHtml();
    }

    /// <summary><c>sanitize</c> (<c>Rails::HTML5::SafeListSanitizer</c> with its default allowlist).</summary>
    internal static string Sanitize(string html)
    {
        try
        {
            return SafeListSanitizer.Sanitize(html, SafeList.Defaults);
        }
        catch (HtmlParseException e)
        {
            throw new OpenGraphRaisedException("ArgumentError", e);
        }
    }

    /// <summary><c>Loofah.html5_fragment</c>, which raises <c>ArgumentError</c> past Gumbo's limits.</summary>
    static HtmlFragment Parse(string html)
    {
        try
        {
            return HtmlParser.ParseFragment(html);
        }
        catch (HtmlParseException e)
        {
            throw new OpenGraphRaisedException("ArgumentError", e);
        }
    }
}

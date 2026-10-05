namespace Campfire.RichText.Sanitize;

/// <summary>
/// Opengraph embed web URL constraints relative to the request host.
/// Port of ActionText::Attachment::OpengraphEmbed.web_url (reference/lib/rails_ext/actiontext_opengraph_embeds.rb).
/// </summary>
public static class OpengraphEmbedUrl
{
    public static string? WebUrl(string? value, string? requestHost)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var uri = RubyUri.Parse(value);
            if (uri.IsHttp && Elsewhere(uri.Host, requestHost))
            {
                return value;
            }
            return null;
        }
        catch (RubyUriInvalidComponentException)
        {
            throw;
        }
        catch (RubyUriException)
        {
            return null;
        }
    }

    public static bool Elsewhere(string? host, string? requestHost)
    {
        if (host is null || !NamedHost(host))
        {
            return false;
        }

        return !CanonicalHost(host).Equals(CanonicalHost(requestHost ?? string.Empty), StringComparison.Ordinal);
    }

    public static bool NamedHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Contains('%') || !host.Contains('.'))
        {
            return false;
        }

        var trimmed = host.TrimEnd('.');
        if (trimmed.Length == 0)
        {
            // In Ruby: host.split(".").last is nil -> nil.match? raises NoMethodError
            throw new InvalidOperationException("NoMethodError: undefined method 'match?' for nil");
        }

        var lastDot = trimmed.LastIndexOf('.');
        var label = lastDot >= 0 ? trimmed[(lastDot + 1)..] : trimmed;

        return label.Any(char.IsAsciiLetter) && !label.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
    }

    public static string CanonicalHost(string host)
    {
        var lower = host.ToLowerInvariant();
        return lower.EndsWith('.') ? lower[..^1] : lower;
    }
}

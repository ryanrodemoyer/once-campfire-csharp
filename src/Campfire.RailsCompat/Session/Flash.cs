using System.Text.Json.Nodes;

namespace Campfire.RailsCompat.Session;

/// <summary>
/// Rails <c>ActionDispatch::Flash::FlashHash</c>, stored in the session under <c>"flash"</c>
/// as <c>{ "discard" =&gt; [], "flashes" =&gt; { ... } }</c>.
/// </summary>
public sealed class Flash
{
    readonly Dictionary<string, JsonNode?> flashes = new(StringComparer.Ordinal);
    readonly HashSet<string> discard = new(StringComparer.Ordinal);

    /// <summary>
    /// Loads flash from session storage. Everything loaded is marked for discard at the end of this request,
    /// minus what the previous request had already discarded.
    /// </summary>
    public static Flash FromSessionValue(JsonNode? value)
    {
        var flash = new Flash();
        if (value is not JsonObject stored)
        {
            return flash;
        }

        var discarded = new HashSet<string>(StringComparer.Ordinal);
        if (stored["discard"] is JsonArray discardArray)
        {
            foreach (var item in discardArray)
            {
                if (item is JsonValue jv && jv.TryGetValue<string>(out var s))
                {
                    discarded.Add(s);
                }
            }
        }

        if (stored["flashes"] is JsonObject flashesObj)
        {
            foreach (var (k, v) in flashesObj)
            {
                if (!discarded.Contains(k) && v is not null)
                {
                    flash.flashes[k] = v.DeepClone();
                    flash.discard.Add(k);
                }
            }
        }

        return flash;
    }

    /// <summary>
    /// Converts flash to session representation. Returns <c>null</c> if nothing survives.
    /// </summary>
    public JsonObject? ToSessionValue()
    {
        var keep = new JsonObject();
        foreach (var (k, v) in flashes)
        {
            if (!discard.Contains(k) && v is not null)
            {
                keep[k] = v.DeepClone();
            }
        }

        if (keep.Count == 0)
        {
            return null;
        }

        return new JsonObject
        {
            ["discard"] = new JsonArray(),
            ["flashes"] = keep
        };
    }

    public string? Notice
    {
        get => Get("notice");
        set => Set("notice", value);
    }

    public string? Alert
    {
        get => Get("alert");
        set => Set("alert", value);
    }

    public string? this[string key]
    {
        get => Get(key);
        set => Set(key, value);
    }

    public string? Get(string key)
    {
        if (flashes.TryGetValue(key, out var node))
        {
            return node switch
            {
                null => null,
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                _ => node.ToString()
            };
        }
        return null;
    }

    public JsonNode? GetNode(string key) =>
        flashes.TryGetValue(key, out var node) ? node : null;

    public void Set(string key, string? value)
    {
        if (value is null)
        {
            Delete(key);
            return;
        }
        Set(key, JsonValue.Create(value));
    }

    public void Set(string key, JsonNode? value)
    {
        if (value is null)
        {
            Delete(key);
            return;
        }
        discard.Remove(key);
        flashes[key] = value.DeepClone();
    }

    FlashNow? now;
    public FlashNow Now => now ??= new(this);

    public void Keep(string? key = null)
    {
        if (key is not null)
        {
            discard.Remove(key);
        }
        else
        {
            discard.Clear();
        }
    }

    public void Discard(string? key = null)
    {
        if (key is not null)
        {
            discard.Add(key);
        }
        else
        {
            foreach (var k in flashes.Keys)
            {
                discard.Add(k);
            }
        }
    }

    public void Delete(string key)
    {
        discard.Remove(key);
        flashes.Remove(key);
    }

    public bool IsEmpty => flashes.Count == 0;

    public IEnumerable<string> Keys => flashes.Keys;
}

public sealed class FlashNow
{
    readonly Flash flash;

    internal FlashNow(Flash flash) => this.flash = flash;

    public string? this[string key]
    {
        get => flash.Get(key);
        set
        {
            flash.Set(key, value);
            if (key is not null) flash.Discard(key);
        }
    }

    public string? Notice
    {
        get => flash.Notice;
        set => this["notice"] = value;
    }

    public string? Alert
    {
        get => flash.Alert;
        set => this["alert"] = value;
    }

    public void Set(string key, string? value) => this[key] = value;

    public void Set(string key, JsonNode? value)
    {
        flash.Set(key, value);
        if (key is not null) flash.Discard(key);
    }
}

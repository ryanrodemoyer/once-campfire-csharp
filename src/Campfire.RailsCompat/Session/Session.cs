using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Campfire.RailsCompat.Cookies;

namespace Campfire.RailsCompat.Session;

/// <summary>
/// Encrypted cookie store session matching <c>ActionDispatch::Session::CookieStore</c>.
/// Holds session ID, CSRF secret, return-to URL, flash data, and arbitrary keys.
/// </summary>
public sealed class Session
{
    readonly SessionConfig config;
    readonly CookieJar jar;
    JsonObject data;
    bool loaded;
    bool changed;
    Flash? flash;

    public Session(CookieJar jar, SessionConfig? config = null)
    {
        this.jar = jar;
        this.config = config ?? new SessionConfig();
        data = new JsonObject();
    }

    public bool IsLoaded => loaded;
    public bool IsChanged => changed;

    public void Load()
    {
        if (loaded) return;

        var encryptedNode = jar.Encrypted.Get(config.Key);
        if (encryptedNode is JsonObject obj)
        {
            data = (JsonObject)obj.DeepClone();
        }
        else
        {
            data = new JsonObject();
        }

        if (!data.ContainsKey("session_id") || data["session_id"] is null)
        {
            data["session_id"] = GenerateSessionId();
        }

        loaded = true;
    }

    public string? Id
    {
        get
        {
            Load();
            return GetString("session_id");
        }
    }

    public Flash Flash
    {
        get
        {
            Load();
            if (flash is null)
            {
                flash = Flash.FromSessionValue(data["flash"]);
            }
            return flash;
        }
    }

    public string? CsrfToken
    {
        get => GetString("_csrf_token");
        set => SetString("_csrf_token", value);
    }

    public string? ReturnToAfterAuthenticating
    {
        get => GetString("return_to_after_authenticating");
        set => SetString("return_to_after_authenticating", value);
    }

    public string? this[string key]
    {
        get => GetString(key);
        set => SetString(key, value);
    }

    public string? GetString(string key)
    {
        Load();
        if (data.TryGetPropertyValue(key, out var node) && node is not null)
        {
            return node switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                _ => node.ToString()
            };
        }
        return null;
    }

    public void SetString(string key, string? value)
    {
        Load();
        if (value is null)
        {
            Remove(key);
            return;
        }
        SetNode(key, JsonValue.Create(value));
    }

    public JsonNode? GetNode(string key)
    {
        Load();
        return data.TryGetPropertyValue(key, out var node) ? node : null;
    }

    public void SetNode(string key, JsonNode? value)
    {
        Load();
        if (value is null)
        {
            Remove(key);
            return;
        }

        if (!data.TryGetPropertyValue(key, out var existing) || !JsonNode.DeepEquals(existing, value))
        {
            data[key] = value.DeepClone();
            changed = true;
        }
    }

    public bool Remove(string key)
    {
        Load();
        if (data.Remove(key))
        {
            changed = true;
            return true;
        }
        return false;
    }

    public bool ContainsKey(string key)
    {
        Load();
        return data.ContainsKey(key) && data[key] is not null;
    }

    /// <summary>
    /// Resets the session (matching Rails <c>reset_session</c>): generates a new session id,
    /// clears all stored data and flash, and marks changed.
    /// </summary>
    public void Reset()
    {
        data = new JsonObject
        {
            ["session_id"] = GenerateSessionId()
        };
        flash = new Flash();
        loaded = true;
        changed = true;
    }

    /// <summary>
    /// Commits changes to the cookie jar. If data changed, updates or deletes the session cookie.
    /// </summary>
    public void Commit(DateTimeOffset? now = null)
    {
        if (flash is not null)
        {
            var flashVal = flash.ToSessionValue();
            if (flashVal is not null)
            {
                SetNode("flash", flashVal);
            }
            else if (data.ContainsKey("flash"))
            {
                Remove("flash");
            }
        }

        if (!changed)
        {
            return;
        }

        var nonNullKeys = data.Where(kvp => kvp.Value is not null).Select(kvp => kvp.Key).ToList();
        if (nonNullKeys.All(k => k == "session_id"))
        {
            jar.Delete(config.Key);
            return;
        }

        var time = now ?? jar.Now;
        var options = new CookieOptions
        {
            HttpOnly = config.HttpOnly,
            SameSite = config.SameSite,
            Expires = time.AddYears(config.ExpireAfterYears)
        };

        jar.Encrypted.Set(config.Key, data, options);
    }

    public static string GenerateSessionId() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}

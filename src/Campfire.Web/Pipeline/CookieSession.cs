using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Campfire.RailsCompat.Cookies;
using Campfire.RailsCompat.Session;

namespace Campfire.Web.Pipeline;

/// <summary>
/// <c>session</c>: the <c>_campfire_session</c> cookie store as Rails runs it
/// (<c>ActionDispatch::Request::Session</c> over <c>ActionDispatch::Session::CookieStore</c> and
/// rack-session's <c>Persisted#commit_session</c>). It loads lazily, and <see cref="Commit"/>
/// writes the cookie whenever the session was loaded, or isn't empty: with <c>expire_after</c>
/// set, Rails re-sends a non-empty session on every response to renew its expiry, even one the
/// action never touched. A session that is only a <c>session_id</c> is written too.
/// </summary>
public sealed class CookieSession
{
    const string SessionIdKey = "session_id";

    readonly CookieJar cookies;
    readonly SessionConfig config;
    JsonObject data = [];
    JsonObject? cookieData;
    bool cookieRead;
    bool loaded;

    public CookieSession(CookieJar cookies, SessionConfig config)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        ArgumentNullException.ThrowIfNull(config);
        this.cookies = cookies;
        this.config = config;
    }

    /// <summary><c>session.loaded?</c></summary>
    public bool IsLoaded => loaded;

    /// <summary><c>session.exists?</c>: the request's cookie holds a session with an id.</summary>
    public bool Exists => CookieData()?[SessionIdKey] is not null;

    /// <summary><c>session.id</c> (its public id), loading the session.</summary>
    public string Id
    {
        get
        {
            LoadForWrite();
            return data[SessionIdKey]!.GetValue<string>();
        }
    }

    /// <summary><c>session.empty?</c>; reads the cookie when there is one.</summary>
    public bool IsEmpty
    {
        get
        {
            LoadForRead();
            return data.Count == 0;
        }
    }

    /// <summary><c>session[key]</c> as a string (a non-string value as its JSON).</summary>
    public string? this[string key]
    {
        get => GetNode(key) switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            var other => other.ToJsonString(),
        };
        set => SetNode(key, value is null ? null : JsonValue.Create(value));
    }

    /// <summary><c>session[key]</c></summary>
    public JsonNode? GetNode(string key)
    {
        LoadForRead();
        return data.TryGetPropertyValue(key, out var value) ? value : null;
    }

    /// <summary><c>session[key] = value</c>. A null value is kept as a key until the session is written.</summary>
    public void SetNode(string key, JsonNode? value)
    {
        LoadForWrite();
        data[key] = value?.DeepClone();
    }

    /// <summary><c>session.key?(key)</c>, true for a key whose value is nil too.</summary>
    public bool ContainsKey(string key)
    {
        LoadForRead();
        return data.ContainsKey(key);
    }

    /// <summary><c>session.delete(key)</c>: the removed value.</summary>
    public JsonNode? Remove(string key)
    {
        LoadForWrite();
        return data.Remove(key, out var value) ? value : null;
    }

    /// <summary>
    /// <c>session.destroy</c> (from <c>reset_session</c>): no data, and a new id, which the next
    /// write sends as a session of its own.
    /// </summary>
    public void Reset()
    {
        cookieData = new JsonObject { [SessionIdKey] = GenerateSessionId() };
        cookieRead = true;
        loaded = false;
        LoadForWrite();
    }

    /// <summary>
    /// The session store's commit (<c>commit_session</c>): when the session was loaded, or holds
    /// anything, write all of it, nils dropped, with the id, into the encrypted cookie with
    /// <c>expire_after</c>'s expiry.
    /// </summary>
    public void Commit(DateTimeOffset now)
    {
        if (!loaded && IsEmpty)
        {
            return;
        }
        LoadForWrite();
        var value = new JsonObject();
        foreach (var (key, node) in data)
        {
            if (node is not null)
            {
                value[key] = node.DeepClone();
            }
        }
        value[SessionIdKey] = data[SessionIdKey]!.DeepClone();
        cookies.Encrypted.Set(config.Key, value, new CookieOptions
        {
            Expires = now.AddYears(config.ExpireAfterYears),
            HttpOnly = config.HttpOnly,
            SameSite = config.SameSite,
        });
    }

    // load_for_read!: only a session the cookie carries is loaded to be read.
    void LoadForRead()
    {
        if (!loaded && Exists)
        {
            Load();
        }
    }

    void LoadForWrite()
    {
        if (!loaded)
        {
            Load();
        }
    }

    // CookieStore#load_session: the cookie's hash, given an id if it has none.
    void Load()
    {
        data = CookieData() is { } stored ? (JsonObject)stored.DeepClone() : [];
        if (data[SessionIdKey] is null)
        {
            data[SessionIdKey] = GenerateSessionId();
        }
        loaded = true;
    }

    JsonObject? CookieData()
    {
        if (!cookieRead)
        {
            cookieData = cookies.Encrypted.Get(config.Key) as JsonObject;
            cookieRead = true;
        }
        return cookieData;
    }

    // AbstractSecureStore#generate_sid: SecureRandom.hex(16).
    static string GenerateSessionId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}

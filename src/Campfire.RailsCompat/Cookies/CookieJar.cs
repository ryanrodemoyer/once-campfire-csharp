using System.Text;
using System.Text.Json.Nodes;
using Campfire.RailsCompat.Crypto;

namespace Campfire.RailsCompat.Cookies;

/// <summary>
/// Rails-compatible cookie jar with plain, signed, encrypted, and permanent sub-jars,
/// matching <c>ActionDispatch::Cookies::CookieJar</c>.
/// </summary>
public sealed class CookieJar
{
    public const string SignedCookieSalt = "signed cookie";
    public const string EncryptedCookieSalt = "authenticated encrypted cookie";

    readonly Dictionary<string, string> cookies = new(StringComparer.Ordinal);
    readonly Dictionary<string, CookieOptions> setCookies = new(StringComparer.Ordinal);
    readonly Dictionary<string, CookieOptions> deletedCookies = new(StringComparer.Ordinal);
    readonly Func<DateTimeOffset> clock;
    readonly MessageVerifier signedVerifier;
    readonly MessageEncryptor encryptor;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "Matches Rails cookies.signed API")]
    public SignedCookieJar Signed { get; }
    public EncryptedCookieJar Encrypted { get; }
    public PermanentCookieJar Permanent { get; }

    public DateTimeOffset Now => clock();

    public CookieJar(KeyGenerator keys, Func<DateTimeOffset>? clock = null)
    {
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        var signedKey = keys.GenerateKey(SignedCookieSalt, 64);
        var encryptedKey = keys.GenerateKey(EncryptedCookieSalt, 32);

        signedVerifier = new MessageVerifier(signedKey, MessageDigest.Sha1, MessageEncoding.Strict, MessageSerializer.Null);
        encryptor = new MessageEncryptor(encryptedKey, MessageSerializer.Null);

        Signed = new SignedCookieJar(this);
        Encrypted = new EncryptedCookieJar(this);
        Permanent = new PermanentCookieJar(this);
    }

    public CookieJar(string secretKeyBase, Func<DateTimeOffset>? clock = null)
        : this(new KeyGenerator(secretKeyBase), clock)
    {
    }

    public static CookieJar FromHeader(string? header, KeyGenerator keys, Func<DateTimeOffset>? clock = null)
    {
        var jar = new CookieJar(keys, clock);
        foreach (var pair in CookieEncoding.ParseCookieHeader(header))
        {
            jar.cookies.TryAdd(pair.Key, pair.Value);
        }
        return jar;
    }

    public static CookieJar FromHeader(string? header, string secretKeyBase, Func<DateTimeOffset>? clock = null) =>
        FromHeader(header, new KeyGenerator(secretKeyBase), clock);

    public static CookieJar FromHeaders(IEnumerable<string>? headers, KeyGenerator keys, Func<DateTimeOffset>? clock = null)
    {
        var jar = new CookieJar(keys, clock);
        foreach (var pair in CookieEncoding.ParseCookieHeaders(headers))
        {
            jar.cookies.TryAdd(pair.Key, pair.Value);
        }
        return jar;
    }

    public static CookieJar FromHeaders(IEnumerable<string>? headers, string secretKeyBase, Func<DateTimeOffset>? clock = null) =>
        FromHeaders(headers, new KeyGenerator(secretKeyBase), clock);

    public string? this[string name]
    {
        get => Get(name);
        set => Set(name, value!);
    }

    public string? Get(string name) => cookies.TryGetValue(name, out var val) ? val : null;

    public bool Contains(string name) => cookies.ContainsKey(name);

    public void Set(string name, string value, CookieOptions? options = null)
    {
        CookieEncoding.CheckOverflow(name, value);
        var opt = (options?.Clone() ?? new CookieOptions()).ResolveExpiry(Now);
        WriteValue(name, value, opt);
    }

    public void Delete(string name, CookieOptions? options = null)
    {
        if (!cookies.ContainsKey(name) && !setCookies.ContainsKey(name))
        {
            return;
        }

        cookies.Remove(name);
        setCookies.Remove(name);
        deletedCookies[name] = options?.Clone() ?? new CookieOptions();
    }

    public bool IsDeleted(string name) => deletedCookies.ContainsKey(name);

    public IReadOnlyList<string> ToSetCookieHeaders(bool ssl = false, string host = "")
    {
        var headers = new List<string>();
        foreach (var (name, opt) in setCookies)
        {
            if (ssl || !opt.Secure || host.EndsWith(".onion", StringComparison.OrdinalIgnoreCase))
            {
                headers.Add(CookieEncoding.FormatSetCookie(name, opt.Value ?? "", opt, Now));
            }
        }
        foreach (var (name, opt) in deletedCookies)
        {
            headers.Add(CookieEncoding.FormatDeleteSetCookie(name, opt));
        }
        return headers;
    }

    public void SetAuthenticationCookie(string sessionToken)
    {
        Signed.Permanent.Set("session_token", sessionToken, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax
        });
    }

    public void RemoveAuthenticationCookie() => Delete("session_token");

    void WriteValue(string name, string rawValue, CookieOptions options)
    {
        var current = Get(name);
        if (current != rawValue || options.Expires.HasValue || options.Permanent)
        {
            cookies[name] = rawValue;
            options.Value = rawValue;
            setCookies[name] = options;
            deletedCookies.Remove(name);
        }
    }

    public sealed class SignedCookieJar
    {
        readonly CookieJar jar;

        public PermanentSignedCookieJar Permanent { get; }

        internal SignedCookieJar(CookieJar jar)
        {
            this.jar = jar;
            Permanent = new PermanentSignedCookieJar(this);
        }

        public string? this[string name]
        {
            get => Get(name);
            set => Set(name, value!);
        }

        public string? Get(string name)
        {
            var node = GetJson(name);
            return node switch
            {
                null => null,
                JsonValue val when val.TryGetValue<string>(out var str) => str,
                _ => node.ToString()
            };
        }

        public JsonNode? GetJson(string name)
        {
            var raw = jar.Get(name);
            if (raw is null) return null;

            var result = jar.signedVerifier.VerifyRaw(raw, $"cookie.{name}", jar.Now);
            if (!result.IsValid)
            {
                result = jar.signedVerifier.VerifyRaw(raw, null, jar.Now);
            }
            if (!result.IsValid || result.Value is null)
            {
                return null;
            }

            var loaded = MessageSerializer.JsonWithFallback.Load(Encoding.UTF8.GetBytes(result.Value));
            return loaded.IsValid ? loaded.Value : null;
        }

        public void Set(string name, string value, CookieOptions? options = null)
        {
            var opt = (options?.Clone() ?? new CookieOptions()).ResolveExpiry(jar.Now);
            var dumped = RailsJson.Encode(JsonValue.Create(value));
            var raw = jar.signedVerifier.GenerateRaw(dumped, $"cookie.{name}", opt.Expires);
            CookieEncoding.CheckOverflow(name, raw);
            jar.WriteValue(name, raw, opt);
        }
    }

    public sealed class PermanentSignedCookieJar
    {
        readonly SignedCookieJar signed;

        internal PermanentSignedCookieJar(SignedCookieJar signed) => this.signed = signed;

        public string? this[string name]
        {
            set => Set(name, value!);
        }

        public void Set(string name, string value, CookieOptions? options = null)
        {
            var opt = options?.Clone() ?? new CookieOptions();
            opt.Permanent = true;
            signed.Set(name, value, opt);
        }
    }

    public sealed class EncryptedCookieJar
    {
        readonly CookieJar jar;

        public PermanentEncryptedCookieJar Permanent { get; }

        internal EncryptedCookieJar(CookieJar jar)
        {
            this.jar = jar;
            Permanent = new PermanentEncryptedCookieJar(this);
        }

        public JsonNode? this[string name]
        {
            get => Get(name);
            set => Set(name, value!);
        }

        public JsonNode? Get(string name)
        {
            var raw = jar.Get(name);
            if (raw is null) return null;

            var result = jar.encryptor.DecryptAndVerify(raw, $"cookie.{name}", jar.Now);
            if (!result.IsValid)
            {
                result = jar.encryptor.DecryptAndVerify(raw, null, jar.Now);
            }
            if (!result.IsValid || result.Value is null)
            {
                return null;
            }

            var dumped = result.Value.GetValue<string>();
            var loaded = MessageSerializer.JsonWithFallback.Load(Encoding.UTF8.GetBytes(dumped));
            return loaded.IsValid ? loaded.Value : null;
        }

        public string? GetString(string name)
        {
            var node = Get(name);
            return node switch
            {
                null => null,
                JsonValue val when val.TryGetValue<string>(out var str) => str,
                _ => node.ToString()
            };
        }

        public void Set(string name, JsonNode value, CookieOptions? options = null)
        {
            var opt = (options?.Clone() ?? new CookieOptions()).ResolveExpiry(jar.Now);
            var dumped = RailsJson.Encode(value);
            var raw = jar.encryptor.EncryptAndSignRaw(dumped, $"cookie.{name}", opt.Expires);
            CookieEncoding.CheckOverflow(name, raw);
            jar.WriteValue(name, raw, opt);
        }

        public void Set(string name, string value, CookieOptions? options = null) =>
            Set(name, JsonValue.Create(value), options);
    }

    public sealed class PermanentEncryptedCookieJar
    {
        readonly EncryptedCookieJar encrypted;

        internal PermanentEncryptedCookieJar(EncryptedCookieJar encrypted) => this.encrypted = encrypted;

        public JsonNode? this[string name]
        {
            set => Set(name, value!);
        }

        public void Set(string name, JsonNode value, CookieOptions? options = null)
        {
            var opt = options?.Clone() ?? new CookieOptions();
            opt.Permanent = true;
            encrypted.Set(name, value, opt);
        }

        public void Set(string name, string value, CookieOptions? options = null) =>
            Set(name, JsonValue.Create(value), options);
    }

    public sealed class PermanentCookieJar
    {
        readonly CookieJar jar;

        internal PermanentCookieJar(CookieJar jar) => this.jar = jar;

        public string? this[string name]
        {
            set => Set(name, value!);
        }

        public void Set(string name, string value, CookieOptions? options = null)
        {
            var opt = options?.Clone() ?? new CookieOptions();
            opt.Permanent = true;
            jar.Set(name, value, opt);
        }
    }
}

static class CookieOptionsExtensions
{
    public static CookieOptions ResolveExpiry(this CookieOptions options, DateTimeOffset now)
    {
        if (options.Permanent && options.Expires is null)
        {
            options.Expires = now.AddYears(20);
        }
        return options;
    }
}

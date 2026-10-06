using System.Security.Cryptography;
using Campfire.RailsCompat.Crypto;

namespace Campfire.Storage.Variants;

/// <summary>
/// <c>ActiveStorage::Variation</c>: a set of transformations, the digest that names its variant
/// record (<c>active_storage_variant_records.variation_digest</c>) and the signed key that carries it
/// in representation URLs.
/// </summary>
public sealed class Variation
{
    public const string KeyPurpose = "variation";

    readonly Lazy<string> digest;

    public Variation(Transformations transformations)
    {
        ArgumentNullException.ThrowIfNull(transformations);
        Transformations = transformations;
        digest = new(() => Convert.ToBase64String(Sha1(Marshal())));
    }

    public Transformations Transformations { get; }

    /// <summary><c>Marshal.dump(transformations)</c>.</summary>
    public byte[] Marshal() => RubyMarshalWriter.Dump(Transformations);

    /// <summary><c>OpenSSL::Digest::SHA1.base64digest Marshal.dump(transformations)</c>.</summary>
    public string Digest => digest.Value;

    /// <summary><c>default_to(defaults)</c>: <c>transformations.reverse_merge(defaults)</c>.</summary>
    public Variation DefaultTo(Transformations defaults) => new(Transformations.ReverseMerge(defaults));

    /// <summary>
    /// <c>format</c>: <c>transformations.fetch(:format, :png)</c> as a string, which must be an
    /// extension Marcel knows (<c>ArgumentError</c> otherwise, as here).
    /// </summary>
    public string Format
    {
        get
        {
            var format = Transformations.TryGetValue("format", out var value) ? value : new RubySymbol("png");
            // format.to_s
            var name = format switch
            {
                string s => s,
                RubySymbol symbol => symbol.Name,
                long n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                bool b => b ? "true" : "false",
                null => "",
                _ => RubyInspect(format),
            };
            if (MarcelExtensions.TypeFor(name) is null)
            {
                throw new ArgumentException($"Invalid variant format ({RubyInspect(format)})");
            }
            return name;
        }
    }

    /// <summary><c>content_type</c>: <c>Marcel::MimeType.for(extension: format.to_s)</c>.</summary>
    public string ContentType => MarcelExtensions.MimeTypeFor(Format);

    /// <summary><c>key</c>: <c>ActiveStorage.verifier.generate(transformations, purpose: :variation)</c>.</summary>
    public string Key(MessageVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        return verifier.Generate(Transformations.ToJson(), KeyPurpose);
    }

    /// <summary>
    /// <c>Variation.decode(key)</c>, or null when the key doesn't verify (Rails raises
    /// <c>InvalidSignature</c>). Its values are strings where the encoded hash had symbols.
    /// </summary>
    public static Variation? Decode(MessageVerifier verifier, string key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(key);
        var verified = verifier.Verify(key, KeyPurpose, now);
        return verified.IsValid && Transformations.FromJson(verified.Value) is { } transformations ? new Variation(transformations) : null;
    }

    public override string ToString() => Transformations.Inspect();

    // The digest names variant records Rails wrote; it isn't a security boundary.
#pragma warning disable CA5350
    static byte[] Sha1(byte[] data) => SHA1.HashData(data);
#pragma warning restore CA5350

    static string RubyInspect(object? value) => new Transformations(("format", value)).Inspect()["{format: ".Length..^1];
}

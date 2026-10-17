using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Campfire.Jobs.WebPush;
using Campfire.RailsCompat.Crypto;
using Campfire.Server.Cli;

namespace Campfire.Server.Tests.Cli;

/// <summary>
/// reference/script/admin/generate-secrets prints <c>SecureRandom.hex(64)</c> and the
/// <c>WebPush::VapidKey</c> pair, encoded by <c>Base64.urlsafe_encode64</c> with its padding.
/// </summary>
public sealed partial class GenerateSecretsTests : IDisposable
{
    readonly TestRoot root = new();
    readonly StringWriter output = new();
    readonly StringWriter error = new();

    public void Dispose()
    {
        output.Dispose();
        error.Dispose();
        root.Dispose();
    }

    [Fact]
    public async Task Prints_the_three_lines_the_reference_script_prints()
    {
        Assert.Equal(0, await Commands.RunAsync(["generate-secrets"], root.Settings(), output, error));

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Matches(SecretKeyBaseLine(), lines[0]);
        Assert.Matches(PrivateKeyLine(), lines[1]);
        Assert.Matches(PublicKeyLine(), lines[2]);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task Refuses_arguments()
    {
        Assert.Equal(64, await Commands.RunAsync(["generate-secrets", "extra"], root.Settings(), output, error));

        Assert.Equal(Commands.Usage, error.ToString().TrimEnd());
    }

    [Fact]
    public void Each_run_makes_new_secrets()
    {
        Assert.NotEqual(GenerateSecrets.Lines().ToArray(), GenerateSecrets.Lines().ToArray());
    }

    [Fact]
    public void The_public_key_is_the_uncompressed_point_of_the_private_key()
    {
        var (privateKey, publicKey) = GenerateSecrets.VapidKeyPair();

        var point = Decode64(publicKey);
        Assert.Equal(65, point.Length);
        Assert.Equal(0x04, point[0]);
        var scalar = Decode64(privateKey);
        Assert.InRange(scalar.Length, 1, 32);
        Assert.NotEqual(0, scalar[0]);

        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = [.. new byte[32 - scalar.Length], .. scalar],
        });
        var derived = key.ExportParameters(includePrivateParameters: false).Q;
        Assert.Equal(point, [0x04, .. derived.X!, .. derived.Y!]);
    }

    [Fact]
    public void The_keys_sign_a_vapid_header_that_verifies_with_the_public_key()
    {
        var (privateKey, publicKey) = GenerateSecrets.VapidKeyPair();

        var header = new VapidIdentification(publicKey, privateKey)
            .Authorization("https://fcm.googleapis.com", DateTimeOffset.UnixEpoch);

        var match = VapidHeader().Match(header);
        Assert.True(match.Success, header);
        Assert.Equal(publicKey.TrimEnd('='), match.Groups["k"].Value);
        var point = Decode64(publicKey);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..] },
        });
        Assert.True(verifier.VerifyData(
            Encoding.ASCII.GetBytes(match.Groups["input"].Value),
            Decode64(match.Groups["signature"].Value),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    static byte[] Decode64(string value) => RubyBase64.UrlSafeDecode(value)!;

    [GeneratedRegex("^SECRET_KEY_BASE=[0-9a-f]{128}$")]
    private static partial Regex SecretKeyBaseLine();

    // 32 bytes is 43 characters and one "="; a scalar with leading zero bytes (BN#to_s(2)) is shorter.
    [GeneratedRegex("^VAPID_PRIVATE_KEY=[A-Za-z0-9_-]{40,43}={0,2}$")]
    private static partial Regex PrivateKeyLine();

    // 65 bytes is 87 characters and one "=", and 0x04 encodes as "B".
    [GeneratedRegex("^VAPID_PUBLIC_KEY=B[A-Za-z0-9_-]{86}=$")]
    private static partial Regex PublicKeyLine();

    [GeneratedRegex(@"^vapid t=(?<input>[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+)\.(?<signature>[A-Za-z0-9_-]+),k=(?<k>[A-Za-z0-9_-]+)$")]
    private static partial Regex VapidHeader();
}

using System.Text.Json;
using Campfire.Vectors;

namespace Campfire.Jobs.Tests.WebPush;

/// <summary>
/// What the reference's web-push gem produced, recorded by the Rust port's oracle
/// (reference-rust/crates/campfire/src/integrations/testdata/oracle/web_push.rb, run in the
/// reference image): a receiver key pair and auth secret, a notification's encoded message, its
/// ciphertext, and the request headers with the clock frozen at <see cref="Now"/>.
/// </summary>
static class ReferenceVector
{
    static readonly JsonElement Root = JsonDocument.Parse(File.ReadAllText(Path.Combine(
        VectorFiles.Root, "reference-rust", "crates", "campfire", "src", "integrations", "testdata", "web_push_expected.json"))).RootElement;

    /// <summary>The <c>VAPID_PUBLIC_KEY</c> and <c>VAPID_PRIVATE_KEY</c> of reference-rust/parity/.env.reference.</summary>
    public const string VapidPublicKey = "BEYXTBB5_jNhNzXDmx5KEU55Vbbd-u--Lk9rM5OFQvUkPIBwZJ9QzAq0zdEzFw6yTV8cTriz_qYBVicY02_VxTQ=";
    public const string VapidPrivateKey = "qfXLHghuG1rSHZUVo9SscNRI-0EIHRbIrfeGCqbAwak=";

    // The notification the oracle built.
    public const string Title = "Designers <&> \"quotes\" é 😀";
    // U+2028 stays raw: JSON.generate doesn't escape it.
    public const string Body = "Kevin: line\nbreak\ttab \u2028 \u001f / \\ ";
    public const string RoomPath = "/rooms/1";
    public const long Badge = 3;
    public const string Endpoint = "https://fcm.googleapis.com/fcm/send/abc";

    public static string String(string name) => Root.GetProperty(name).GetString()!;

    public static string ReceiverPrivateKey => String("receiver_private_key");
    public static string P256dh => String("p256dh");
    public static string Auth => String("auth");
    public static string Message => String("message");
    public static string Ciphertext => String("ciphertext");
    public static DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(Root.GetProperty("now").GetInt64());

    public static List<(string Name, string Value)> Headers =>
        [.. Root.GetProperty("headers").EnumerateArray().Select(pair => (pair[0].GetString()!, pair[1].GetString()!))];

    public static WebPushReceiver Receiver => WebPushReceiver.FromPrivateKey(ReceiverPrivateKey, Auth);
}

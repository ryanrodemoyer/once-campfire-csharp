using System.Net;
using Campfire.Jobs.WebPush;

namespace Campfire.Jobs.Tests.WebPush;

public sealed class WebPushNotificationTests
{
    static WebPushNotification Notification => new(
        ReferenceVector.Title, ReferenceVector.Body, ReferenceVector.RoomPath, ReferenceVector.Badge,
        ReferenceVector.Endpoint, ReferenceVector.P256dh, ReferenceVector.Auth);

    [Fact]
    public void Encodes_the_message_as_the_reference_does()
    {
        Assert.Equal(ReferenceVector.Message, Notification.EncodedMessage());
    }

    [Fact]
    public void A_nameless_room_is_a_null_title()
    {
        Assert.StartsWith("{\"title\":null,", (Notification with { Title = null }).EncodedMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Sends_the_headers_the_gem_sends()
    {
        var payload = WebPushEncryption.Encrypt(ReferenceVector.Message, ReferenceVector.P256dh, ReferenceVector.Auth);

        var headers = WebPushClient.Headers(payload, "vapid t=…", WebPushNotification.Urgency);

        Assert.Equal([.. ReferenceVector.Headers, ("Authorization", "vapid t=…")], headers);
    }

    [Fact]
    public void An_empty_message_sends_no_content_coding()
    {
        Assert.Equal(["Content-Type", "Ttl", "Urgency", "Authorization"], WebPushClient.Headers(null, "a", "high").Select(header => header.Name));
    }

    [Theory]
    [InlineData(200, null, null)]
    [InlineData(201, null, null)]
    [InlineData(410, null, "WebPush::ExpiredSubscription")]
    [InlineData(404, null, "WebPush::InvalidSubscription")]
    [InlineData(401, null, "WebPush::Unauthorized")]
    [InlineData(403, null, "WebPush::Unauthorized")]
    [InlineData(400, "UnauthorizedRegistration", "WebPush::Unauthorized")]
    [InlineData(400, "Bad Request", "WebPush::ResponseError")]
    [InlineData(413, null, "WebPush::PayloadTooLarge")]
    [InlineData(429, null, "WebPush::TooManyRequests")]
    [InlineData(500, null, "WebPush::PushServiceError")]
    [InlineData(503, null, "WebPush::PushServiceError")]
    [InlineData(301, null, "WebPush::ResponseError")]
    public void Verifies_the_response_as_the_gem_does(int status, string? reason, string? rubyClass)
    {
        if (rubyClass is null)
        {
            Assert.Equal((HttpStatusCode)status, WebPushClient.VerifyResponse((HttpStatusCode)status, reason, "fcm.googleapis.com"));
            return;
        }
        var error = Assert.ThrowsAny<WebPushResponseException>(() => WebPushClient.VerifyResponse((HttpStatusCode)status, reason, "fcm.googleapis.com"));
        Assert.Equal(rubyClass, error.RubyClass);
        Assert.Equal(status == 410, error is WebPushExpiredSubscriptionException);
    }

    [Fact]
    public void The_audience_keeps_the_hosts_case()
    {
        Assert.Equal("https://FCM.googleapis.com", WebPushClient.Audience(PushEndpoint.Parse("HTTPS://FCM.googleapis.com:443/fcm/send/x")!));
    }

    [Fact]
    public void Ttl_is_four_weeks()
    {
        Assert.Equal("2419200", ReferenceVector.Headers.Single(header => header.Name == "Ttl").Value);
        Assert.Equal(2419200, WebPushClient.Ttl);
    }
}

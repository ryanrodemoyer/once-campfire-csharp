using System.Net.Sockets;
using Campfire.Jobs.Webhooks;
using Campfire.Vectors;
using WebhookReply = Campfire.Vectors.WebhookReply;
using WebhookRequest = Campfire.Vectors.WebhookRequest;

namespace Campfire.Jobs.Tests.Webhooks;

/// <summary>
/// Replays vectors/webhook: each scripted reply in cases.json, and what <c>Webhook#deliver</c> did
/// with it in the reference (expected.json, from reference-tools/oracle/webhook.rb, which stubs
/// out the payload and the reply messages): the request it sent, the status, and the text or
/// attachment it replied with, or the error it raised.
/// </summary>
public sealed class WebhookVectorTests
{
    const string payload = """{"message":"hi"}""";

    static readonly Lazy<Task<Dictionary<string, (object Outcome, IReadOnlyList<WebhookRequest> Requests)>>> Runs = new(RunAllAsync);

    [Theory]
    [MemberData(nameof(IntegrationVectors.Webhook), MemberType = typeof(IntegrationVectors))]
    public async Task Delivers_like_the_reference(WebhookExpectedCase expected)
    {
        var (outcome, requests) = (await Runs.Value)[expected.Name];

        switch (outcome)
        {
            case WebhookDelivery delivery:
                Assert.Null(expected.Error);
                Assert.Equal(expected.Status, delivery.Status);
                AssertReply(RepliedAtEdbc779(expected), delivery.Reply);
                break;
            case Exception error:
                Assert.Equal(expected.Error, RubyClass(error));
                Assert.Null(expected.Reply);
                break;
        }

        Assert.Equal(expected.Requests.Count, requests.Count);
        foreach (var (want, have) in expected.Requests.Zip(requests))
        {
            Assert.Equal(want.RequestLine, have.RequestLine);
            Assert.Equal(want.Body, have.Body);
            // The same headers, the Host naming this run's port. Their order is .NET's.
            var port = have.Headers.Single(header => header[0] == "Host")[1].Split(':')[1];
            Assert.Equal(
                want.Headers.Select(header => header[0] == "Host" ? $"Host: 127.0.0.1:{port}" : $"{header[0]}: {header[1]}").Order(),
                have.Headers.Select(header => $"{header[0]}: {header[1]}").Order());
        }
    }

    [Fact]
    public void The_vectors_cover_every_case()
    {
        Assert.Equal(19, IntegrationVectors.WebhookExpectedFile.Count);
        Assert.Equal(IntegrationVectors.WebhookCasesFile.Select(c => c.Name), IntegrationVectors.WebhookExpectedFile.Select(c => c.Name));
    }

    // The vectors come from the Rust port's older reference. At edbc779 an answer other than a 200
    // is no reply at all, attachment or not (d2155e8, reference/app/models/webhook.rb): running
    // reference-tools/oracle/webhook.rb in it changes only error-html (a 500) and created-text (a
    // 201), to no reply. A timeout (no status) still replies with its text.
    static WebhookReply? RepliedAtEdbc779(WebhookExpectedCase expected) => expected.Status is null or 200 ? expected.Reply : null;

    static void AssertReply(WebhookReply? expected, Jobs.Webhooks.WebhookReply? actual)
    {
        switch (expected)
        {
            case null:
                Assert.Null(actual);
                break;
            case { Attachment: { } attachment }:
                var reply = Assert.IsType<WebhookAttachmentReply>(actual);
                Assert.Equal(attachment.Filename, reply.Filename);
                Assert.Equal(attachment.ContentType, reply.ContentType);
                Assert.Equal(Convert.FromBase64String(attachment.BodyB64), reply.Data);
                break;
            default:
                var text = Assert.IsType<WebhookTextReply>(actual);
                Assert.Equal(Convert.FromBase64String(expected.TextB64!), text.Bytes);
                break;
        }
    }

    // The cases run side by side, as the slow one takes the whole timeout.
    static async Task<Dictionary<string, (object, IReadOnlyList<WebhookRequest>)>> RunAllAsync()
    {
        var cases = IntegrationVectors.WebhookCasesFile;
        await using var server = new WebhookServer(cases);
        using var client = new WebhookClient();
        var outcomes = await Task.WhenAll(cases.Select(async c =>
        {
            try
            {
                return (object)await client.DeliverAsync(c.Url ?? server.Url(c.Name), payload);
            }
            catch (Exception error)
            {
                return error;
            }
        }));
        var requests = server.Requests;
        return cases.Zip(outcomes).ToDictionary(
            run => run.First.Name,
            run => (run.Second, (IReadOnlyList<WebhookRequest>)[.. requests.Where(request => request.RequestLine.Split(' ')[1] == $"/{run.First.Name}")]));
    }

    static string RubyClass(Exception error) => error switch
    {
        InvalidMimeTypeException => "Mime::Type::InvalidMimeType",
        HttpRequestException { InnerException: SocketException { SocketErrorCode: SocketError.ConnectionRefused } } => "Errno::ECONNREFUSED",
        _ => error.ToString(),
    };
}

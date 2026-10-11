using System.Text.Json.Nodes;
using Campfire.Data.Records;
using Campfire.RailsCompat.GlobalId;
using Campfire.RichText.Attachments;
using Campfire.Storage.Blobs;
using Campfire.Storage.Variants;
using Campfire.Web.Helpers;
using Campfire.Web.Routing;
using Campfire.Web.Tests.Assets;
using Campfire.Web.Tests.Controllers.Messages;

namespace Campfire.Web.Tests.Upstream.U06;

public sealed class AttachmentPresentationTests : IDisposable
{
    static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    readonly AttachmentsApp app;
    readonly View view;
    readonly MessageRoom room;
    readonly MessageUser user;

    public AttachmentPresentationTests()
    {
        app = new AttachmentsApp(new DateTimeOffset(2026, 3, 2, 16, 0, 0, TimeSpan.Zero));
        view = new View
        {
            Assets = ReferenceAssets.Bundle,
            Origin = new UrlBase("http", MessagesApp.Host),
            Storage = app.App.Storage
        };
        room = new MessageRoom(1, "Pets");
        user = new MessageUser(1, "Jason", "Jason – Bio", UserRole.Member, "signed-avatar", app.Now);
    }

    Blob CreateBlob(string filename, string contentType, long byteSize, int width, int height)
    {
        var metadata = new JsonObject
        {
            ["identified"] = true,
            ["width"] = width,
            ["height"] = height,
            ["analyzed"] = true
        };
        return new Blob(101, "test-key-101", new Filename(filename), contentType, metadata, "local", byteSize, "checksum", app.Now);
    }

    MessageView AttachmentMessage(Blob blob, bool isThumbProcessed, bool isPosterProcessed) =>
        new(
            Id: 999,
            ClientMessageId: "0015",
            CreatedAt: app.Now,
            UpdatedAt: app.Now,
            CreatorId: user.Id,
            Room: room,
            Creator: user,
            Body: null,
            PlainTextBody: blob.Filename.ToString(),
            Attachment: blob,
            Boosts: [],
            PlainTextFailed: false,
            IsThumbProcessed: isThumbProcessed,
            IsPosterProcessed: isPosterProcessed);

    [Fact]
    public void Message_presentation_shows_an_images_thumbnail_made_when_it_was_posted()
    {
        var moon = CreateBlob("moon.jpg", "image/jpeg", 13036, 640, 640);
        var message = AttachmentMessage(moon, isThumbProcessed: true, isPosterProcessed: false);

        var presentation = view.MessagePresentationHtml(message).ToString();

        Assert.Matches(@"<img[^>]+src=""[^""]*/representations/[^""]*moon\.jpg""", presentation);
    }

    [Fact]
    public void Message_presentation_links_an_image_whose_thumbnail_was_not_made_rather_than_making_it_on_view()
    {
        var moon = CreateBlob("moon.jpg", "image/jpeg", 13036, 640, 640);
        var message = AttachmentMessage(moon, isThumbProcessed: false, isPosterProcessed: false);

        var presentation = view.MessagePresentationHtml(message).ToString();

        Assert.DoesNotContain("/representations/", presentation);
        Assert.Contains("<span>moon.jpg</span>", presentation);
    }

    [Fact]
    public void Message_presentation_gives_a_video_the_poster_made_when_it_was_posted()
    {
        var video = CreateBlob("alpha-centuri.mov", "video/quicktime", 114840, 320, 180);
        var message = AttachmentMessage(video, isThumbProcessed: false, isPosterProcessed: true);

        var presentation = view.MessagePresentationHtml(message).ToString();

        Assert.Matches(@"<video[^>]+poster=""[^""]*/representations/[^""]*alpha-centuri", presentation);
    }

    [Fact]
    public void Message_presentation_shows_a_video_whose_poster_was_not_made_without_one_rather_than_making_it_on_view()
    {
        var video = CreateBlob("alpha-centuri.mov", "video/quicktime", 114840, 320, 180);
        var message = AttachmentMessage(video, isThumbProcessed: false, isPosterProcessed: false);

        var presentation = view.MessagePresentationHtml(message).ToString();

        Assert.Matches(@"<video[^>]+src=""[^""]*alpha-centuri\.mov""", presentation);
        Assert.DoesNotContain("poster=", presentation);
        Assert.DoesNotContain("/representations/", presentation);
    }

    [Fact]
    public void Message_presentation_shows_a_video_whose_frame_was_drawn_but_whose_poster_was_not_made_without_one()
    {
        var video = CreateBlob("alpha-centuri.mov", "video/quicktime", 114840, 320, 180);
        // Frame was drawn (so preview image exists), but poster variant was not made
        var message = AttachmentMessage(video, isThumbProcessed: false, isPosterProcessed: false);

        var presentation = view.MessagePresentationHtml(message).ToString();

        Assert.Matches(@"<video[^>]+src=""[^""]*alpha-centuri\.mov""", presentation);
        Assert.DoesNotContain("poster=", presentation);
        Assert.DoesNotContain("/representations/", presentation);
    }

    [Fact]
    public async Task Message_presentation_shows_an_image_in_the_rich_text_as_a_file_rather_than_making_its_preview_on_view()
    {
        var blob = await app.App.Database.WriteAsync(tx =>
        {
            return BlobRecords.InsertBlob(tx.Session, new NewBlob("rt-moon-key", new Filename("moon.jpg"), "image/jpeg", [], "local", 13036, "checksum"), app.Now);
        }, Cancellation);

        var sgid = SignedGlobalId.AttachableSgid(app.Keys, GlobalId.Create("ActiveStorage::Blob", blob.Id));
        var body = $"<div>Here: <action-text-attachment sgid=\"{sgid}\"></action-text-attachment></div>";

        var message = new MessageView(
            Id: 998,
            ClientMessageId: "0016",
            CreatedAt: app.Now,
            UpdatedAt: app.Now,
            CreatorId: user.Id,
            Room: room,
            Creator: user,
            Body: body,
            PlainTextBody: "Here: moon.jpg",
            Attachment: null,
            Boosts: []);

        await app.App.Database.ReadAsync(session =>
        {
            var v = new View
            {
                Assets = ReferenceAssets.Bundle,
                Origin = new UrlBase("http", MessagesApp.Host),
                Storage = app.App.Storage,
                RichTextContext = new RenderContext(new DatabaseAttachables(session, app.Keys, app.Now), MessagesApp.Host)
            };
            var presentation = v.MessagePresentationHtml(message).ToString();

            Assert.DoesNotContain("/representations/", presentation);
            Assert.Contains("<span class=\"attachment__name\">moon.jpg</span>", presentation);
            Assert.Contains("<span class=\"attachment__size\">12.7 KB</span>", presentation);
            return true;
        }, Cancellation);
    }

    [Fact]
    public async Task Message_presentation_shows_an_image_in_the_rich_text_as_a_file_even_when_its_preview_was_made()
    {
        var blob = await app.App.Database.WriteAsync(tx =>
        {
            var b = BlobRecords.InsertBlob(tx.Session, new NewBlob("rt-moon-key2", new Filename("moon.jpg"), "image/jpeg", [], "local", 13036, "checksum"), app.Now);
            // Simulate a variant record existing
            var variation = new Variation(new Transformations(("resize_to_limit", new[] { 1024L, 768L })));
            VariantRecords.CreateOrFind(tx, b.Id, variation.Digest, app.App.RequireStorage().Stage(new MemoryStream([1, 2, 3]), new Filename("thumb.jpg"), "image/jpeg"), app.Now);
            return b;
        }, Cancellation);

        var sgid = SignedGlobalId.AttachableSgid(app.Keys, GlobalId.Create("ActiveStorage::Blob", blob.Id));
        var body = $"<div>Here: <action-text-attachment sgid=\"{sgid}\"></action-text-attachment></div>";

        var message = new MessageView(
            Id: 997,
            ClientMessageId: "0017",
            CreatedAt: app.Now,
            UpdatedAt: app.Now,
            CreatorId: user.Id,
            Room: room,
            Creator: user,
            Body: body,
            PlainTextBody: "Here: moon.jpg",
            Attachment: null,
            Boosts: []);

        await app.App.Database.ReadAsync(session =>
        {
            var v = new View
            {
                Assets = ReferenceAssets.Bundle,
                Origin = new UrlBase("http", MessagesApp.Host),
                Storage = app.App.Storage,
                RichTextContext = new RenderContext(new DatabaseAttachables(session, app.Keys, app.Now), MessagesApp.Host)
            };
            var presentation = v.MessagePresentationHtml(message).ToString();

            Assert.DoesNotContain("/representations/", presentation);
            Assert.Contains("<span class=\"attachment__name\">moon.jpg</span>", presentation);
            Assert.Contains("<span class=\"attachment__size\">12.7 KB</span>", presentation);
            return true;
        }, Cancellation);
    }

    [Fact]
    public async Task Message_presentation_shows_a_video_in_the_rich_text_as_a_file()
    {
        var blob = await app.App.Database.WriteAsync(tx =>
        {
            return BlobRecords.InsertBlob(tx.Session, new NewBlob("rt-vid-key", new Filename("alpha-centuri.mov"), "video/quicktime", [], "local", 114840, "checksum"), app.Now);
        }, Cancellation);

        var sgid = SignedGlobalId.AttachableSgid(app.Keys, GlobalId.Create("ActiveStorage::Blob", blob.Id));
        var body = $"<div>Here: <action-text-attachment sgid=\"{sgid}\"></action-text-attachment></div>";

        var message = new MessageView(
            Id: 996,
            ClientMessageId: "0018",
            CreatedAt: app.Now,
            UpdatedAt: app.Now,
            CreatorId: user.Id,
            Room: room,
            Creator: user,
            Body: body,
            PlainTextBody: "Here: alpha-centuri.mov",
            Attachment: null,
            Boosts: []);

        await app.App.Database.ReadAsync(session =>
        {
            var v = new View
            {
                Assets = ReferenceAssets.Bundle,
                Origin = new UrlBase("http", MessagesApp.Host),
                Storage = app.App.Storage,
                RichTextContext = new RenderContext(new DatabaseAttachables(session, app.Keys, app.Now), MessagesApp.Host)
            };
            var presentation = v.MessagePresentationHtml(message).ToString();

            Assert.DoesNotContain("/representations/", presentation);
            Assert.Contains("<span class=\"attachment__name\">alpha-centuri.mov</span>", presentation);
            Assert.Contains("<span class=\"attachment__size\">112 KB</span>", presentation);
            return true;
        }, Cancellation);
    }

    public void Dispose()
    {
        app.Dispose();
    }
}

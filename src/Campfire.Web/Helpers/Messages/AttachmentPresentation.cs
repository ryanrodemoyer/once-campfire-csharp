using System.Text.Json;
using System.Text.Json.Nodes;
using Campfire.RailsCompat.Ruby;
using Campfire.Storage.Blobs;
using Campfire.Storage.Variants;

namespace Campfire.Web.Helpers;

/// <summary>
/// <c>Messages::AttachmentPresentation</c> (reference/app/helpers/messages/attachment_presentation.rb):
/// a video player, a lightboxed thumbnail, or a file link with download and share buttons.
/// </summary>
sealed class AttachmentPresentation(View context, Blob attachment)
{
    public SafeString Render() =>
        Representable.IsPreviewable(attachment) || attachment.IsVariable ? RenderPreview() : RenderLink();

    SafeString RenderPreview() => attachment.IsVideo ? VideoPreviewTag() : LightboxedImagePreviewTag();

    SafeString VideoPreviewTag()
    {
        var (width, height) = PreviewDimensions();
        var poster = context.RepresentationUrlsForAttachments.RedirectPath(Representable.Preview(attachment, NamedVariants.VideoPoster));
        return InlineMediaDimensionConstraints(width, height, Tag.Element("video", options: new()
        {
            { "src", BlobPath() },
            { "poster", poster },
            { "controls", true },
            { "preload", "none" },
            { "width", "100%" },
            { "height", "100%" },
            { "class", "message__attachment" },
        }));
    }

    SafeString LightboxedImagePreviewTag()
    {
        var (width, height) = PreviewDimensions();
        var thumb = context.RepresentationUrlsForAttachments.RedirectPath(Representable.Representation(attachment, NamedVariants.MessageThumb));
        return InlineMediaDimensionConstraints(width, height, LightboxLink(context.BroadcastImageTag(thumb, new()
        {
            { "width", width?.Value },
            { "height", height?.Value },
            { "class", "message__attachment" },
            { "loading", "lazy" },
        })));
    }

    static SafeString InlineMediaDimensionConstraints(RubyNumber? width, RubyNumber? height, SafeString content)
    {
        if (width is { } w && height is { } h)
        {
            var aspectRatio = w.ToDouble() / h.ToDouble();
            return Tag.Div(content, new()
            {
                { "class", "max-inline-size center flex overflow-clip" },
                { "style", $"width: {w.Half()}px; aspect-ratio: {RubyFloat.ToS(aspectRatio)};" },
            });
        }
        return Tag.Div(content, new() { { "class", "max-inline-size center overflow-clip" } });
    }

    (RubyNumber? Width, RubyNumber? Height) PreviewDimensions()
    {
        var width = RubyNumber.From(attachment.Metadata["width"]);
        var height = RubyNumber.From(attachment.Metadata["height"]);

        if (width is not { } w || height is not { } h)
        {
            return (null, null);
        }
        if (w.ToDouble() <= NamedVariants.ThumbnailMaxWidth && h.ToDouble() <= NamedVariants.ThumbnailMaxHeight)
        {
            return (w, h);
        }
        var widthFactor = (double)NamedVariants.ThumbnailMaxWidth / w.ToDouble();
        var heightFactor = (double)NamedVariants.ThumbnailMaxHeight / h.ToDouble();
        var scaleFactor = Math.Min(widthFactor, heightFactor);
        return (new RubyNumber(w.ToDouble() * scaleFactor), new RubyNumber(h.ToDouble() * scaleFactor));
    }

    SafeString RenderLink() =>
        Tag.Div(
            OutputSafety.Concat(
                context.BroadcastImageTag("common-file-text.svg", new() { { "size", 22 }, { "class", "colorize--black" }, { "aria", new HtmlOptions { { "hidden", "true" } } } }),
                Tag.Span(Filename),
                DownloadLink(),
                ShareButton()),
            new() { { "class", "flex-inline align-center gap-half" } });

    SafeString LightboxLink(SafeString content) =>
        View.LinkTo(content, BlobPath(), new()
        {
            { "class", "flex" },
            { "data", new HtmlOptions { { "lightbox_target", "image" }, { "action", "lightbox#open" }, { "lightbox_url_value", DownloadUrl() } } },
        });

    SafeString DownloadLink() =>
        View.LinkTo(
            OutputSafety.Concat(
                context.BroadcastImageTag("download.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }),
                Tag.Span($"Download {Filename}", new() { { "class", "for-screen-reader" } })),
            DownloadUrl(),
            new() { { "class", "btn message__action-btn hide-in-ios-pwa" }, { "style", "--width: auto;" } });

    SafeString ShareButton() =>
        Tag.Button(
            OutputSafety.Concat(
                context.BroadcastImageTag("share.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }),
                Tag.Span($"Share {Filename}", new() { { "class", "for-screen-reader" } })),
            new()
            {
                { "class", "btn message__action-btn" },
                { "style", "--width: auto;" },
                { "data", new HtmlOptions { { "controller", "web-share" }, { "action", "web-share#share" }, { "web_share_files_value", DownloadUrl() } } },
            });

    string Filename => attachment.Filename.ToString();

    // rails_blob_path(message.attachment)
    string BlobPath() => context.BlobUrlsForAttachments.BlobRedirectPath(attachment);

    // rails_blob_path(message.attachment, disposition: "attachment", only_path: true)
    string DownloadUrl() => context.BlobUrlsForAttachments.BlobRedirectPath(attachment, "attachment");
}

/// <summary>
/// A number from blob metadata as Ruby holds it: an Integer stays one through <c>/ 2</c> and
/// prints without a decimal point, a Float prints with Ruby's <c>Float#to_s</c>.
/// </summary>
readonly record struct RubyNumber(object Value)
{
    public static RubyNumber? From(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }
        return value.TryGetValue<long>(out var integer) ? new RubyNumber(integer) : new RubyNumber(value.GetValue<double>());
    }

    public double ToDouble() => Value is long integer ? integer : (double)Value;

    // `width / 2`: Integer division floors.
    public string Half() => Value is long integer ? Math.Floor(integer / 2.0).ToString(System.Globalization.CultureInfo.InvariantCulture) : RubyFloat.ToS((double)Value / 2);
}

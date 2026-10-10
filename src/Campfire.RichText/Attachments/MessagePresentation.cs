using Campfire.RichText.Html;
using Campfire.RichText.Sanitize;

namespace Campfire.RichText.Attachments;

/// <summary>How <c>messages/_message.html.erb</c> shows a text message's body.</summary>
public abstract record Presentation
{
    private Presentation() { }

    public sealed record Html(string Value) : Presentation;

    /// <summary><c>message_tag</c> rescued an exception: render <c>messages/_unrenderable.html.erb</c> instead.</summary>
    public sealed record Unrenderable : Presentation;
}

/// <summary>The text branch of <c>MessagesHelper#message_presentation</c> (<c>reference/app/helpers/messages_helper.rb</c>).</summary>
public static class MessagePresentation
{
    /// <summary>
    /// <c>auto_link h(ContentFilters::TextMessagePresentationFilters.apply(message.body.body)), html: { target: "_blank" }, ...</c>
    /// </summary>
    /// <exception cref="RichTextRaisedException">Rails raises rendering this body.</exception>
    /// <exception cref="HtmlParseException">The body exceeds Gumbo's limits.</exception>
    public static string Render(string body, RenderContext context)
    {
        var filtered = ContentFilters.ApplyTextMessagePresentationFilters(body, context.RequestHost, context);
        var rendered = RichTextRenderer.RenderWithLayout(RichTextRenderer.Wrap(filtered), context);
        return RailsAutoLink.Apply(rendered, SafeList.AutoLink);
    }

    /// <summary>
    /// <c>message_presentation</c> with its rescue: an exception renders as an empty string, unless
    /// logging it raises too, in which case the whole message is unrenderable.
    /// </summary>
    public static Presentation Present(string body, RenderContext context)
    {
        try
        {
            return new Presentation.Html(Render(body, context));
        }
        catch (RichTextRaisedException e) when (e.Unloggable)
        {
            return new Presentation.Unrenderable();
        }
#pragma warning disable CA1031 // message_presentation rescues Exception (reference/app/helpers/messages_helper.rb).
        catch (Exception)
#pragma warning restore CA1031
        {
            return new Presentation.Html("");
        }
    }
}

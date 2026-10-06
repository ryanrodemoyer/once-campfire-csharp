namespace Campfire.Web.Helpers;

// reference/app/views/layouts/
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary>
    /// The application layout around a rendered page. Render the page first, so its
    /// <c>content_for</c> calls are in place when the layout yields them, as Rails does.
    /// </summary>
    [ErbTemplate("layouts/application.html.erb.cs")]
    public partial void ApplicationLayout(HtmlWriter w, SafeString body);

    /// <summary><c>layouts/_lightbox</c>.</summary>
    [ErbTemplate("layouts/_lightbox.html.erb.cs")]
    public partial void Lightbox(HtmlWriter w);

    /// <summary>
    /// <c>layouts/action_text/contents/_content</c>: the wrapper Action Text renders rich text
    /// content in. <paramref name="content"/> is Action Text's own <c>contents/_content</c>
    /// partial, which ends in a newline.
    /// </summary>
    [ErbTemplate("layouts/action_text/contents/_content.html.erb.cs")]
    public partial void ActionTextContentLayout(HtmlWriter w, SafeString content);

    /// <summary>The mailer layout. The app sends no mail; it is here because the reference has it.</summary>
    [ErbTemplate("layouts/mailer.html.erb.cs")]
    public partial void MailerLayout(HtmlWriter w, SafeString body);
}
#pragma warning restore IDE0060

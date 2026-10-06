namespace Campfire.Web.Helpers;

// turbo-rails' app/views/layouts/turbo_rails/frame.html.erb (turbo-rails@30cd8fc, the revision
// reference/Gemfile.lock pins).
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary>
    /// <c>layouts/turbo_rails/frame</c>: the minimal layout <c>Turbo::Frames::FrameRequest</c>
    /// renders a page in when a Turbo frame asked for it. Render the page first, so its
    /// <c>content_for :head</c> is in place.
    /// </summary>
    [ErbTemplate("layouts/turbo_rails/frame.html.erb.cs")]
    public partial void TurboRailsFrameLayout(HtmlWriter w, SafeString body);
}
#pragma warning restore IDE0060

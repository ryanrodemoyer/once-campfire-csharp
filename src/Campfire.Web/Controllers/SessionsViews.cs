using Campfire.Web.Controllers;

namespace Campfire.Web.Helpers;

// reference/app/views/sessions/{new,incompatible_browser}, first_runs/show, welcome/show and
// accounts/_help_contact (which A02 owns; sessions/new is its only page before A02).
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary>
    /// <c>AllowBrowser::VERSIONS</c> (reference/app/controllers/concerns/allow_browser.rb), each
    /// minimum as Ruby prints it; null for <c>false</c>.
    /// </summary>
    internal static readonly (string Browser, string? Version)[] SupportedBrowsers =
        [("safari", "17.2"), ("chrome", "120"), ("firefox", "121"), ("opera", "104"), ("ie", null)];

    /// <summary><c>sessions/new</c>: the sign-in form.</summary>
    [ErbTemplate("sessions/new.html.erb.cs")]
    public partial void SessionsNew(HtmlWriter w, string accountName, object? emailAddress, HelpContact? helpContact);

    /// <summary><c>accounts/_help_contact</c>: the administrator to e-mail, if there is one.</summary>
    [ErbTemplate("sessions/_help_contact.html.erb.cs")]
    public partial void AccountsHelpContact(HtmlWriter w, HelpContact? helpContact);

    /// <summary><c>sessions/incompatible_browser</c>: the supported browsers.</summary>
    [ErbTemplate("sessions/incompatible_browser.html.erb.cs")]
    public partial void SessionsIncompatibleBrowser(HtmlWriter w, bool isAppleMessages);

    /// <summary><c>first_runs/show</c>: the administrator's sign-up form.</summary>
    [ErbTemplate("first_runs/show.html.erb.cs")]
    public partial void FirstRunsShow(HtmlWriter w);

    /// <summary><c>welcome/show</c>: no rooms yet.</summary>
    [ErbTemplate("welcome/show.html.erb.cs")]
    public partial void WelcomeShow(HtmlWriter w, string userName);

    /// <summary>
    /// turbo-rails' <c>turbo_page_requires_reload</c>:
    /// <c>provide :head, tag.meta(name: "turbo-visit-control", content: "reload")</c>.
    /// </summary>
    internal void TurboPageRequiresReload() =>
        ContentFor("head", Tag.Meta(new() { { "name", "turbo-visit-control" }, { "content", "reload" } }));
}
#pragma warning restore IDE0060

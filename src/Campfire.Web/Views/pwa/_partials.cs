using Campfire.RailsCompat.Ruby;
using Campfire.RailsCompat.UserAgent;

namespace Campfire.Web.Helpers;

// reference/app/views/pwa/_*.html.erb: help for turning on notifications, by platform.
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary><c>pwa/_browser_settings</c>.</summary>
    [ErbTemplate("pwa/_browser_settings.html.erb.cs")]
    public partial void PwaBrowserSettings(HtmlWriter w, ApplicationPlatform platform);

    /// <summary><c>pwa/_system_settings</c>.</summary>
    [ErbTemplate("pwa/_system_settings.html.erb.cs")]
    public partial void PwaSystemSettings(HtmlWriter w, ApplicationPlatform platform);

    /// <summary><c>pwa/_install_instructions</c>.</summary>
    [ErbTemplate("pwa/_install_instructions.html.erb.cs")]
    public partial void PwaInstallInstructions(HtmlWriter w, ApplicationPlatform platform);

    /// <summary><c>platform.browser.capitalize</c>, which raises when the browser is unknown.</summary>
    static string CapitalizedBrowser(ApplicationPlatform platform) =>
        RubyCase.Capitalize(platform.Browser() ?? throw new InvalidOperationException("undefined method 'capitalize' for nil"));
}
#pragma warning restore IDE0060

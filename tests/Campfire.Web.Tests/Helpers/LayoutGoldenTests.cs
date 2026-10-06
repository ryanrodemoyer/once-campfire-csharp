using System.Text.Json;
using System.Text.RegularExpressions;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Session;
using Campfire.Templates;
using Campfire.Web.Helpers;
using Campfire.Web.Routing;
using Campfire.Web.Tests.Assets;

namespace Campfire.Web.Tests.Helpers;

// The application layout around an empty page body, against what generate.rb rendered through the
// reference's own layouts/application.html.erb and layouts/_lightbox.html.erb.
public sealed partial class LayoutGoldenTests
{
    static readonly JsonElement Golden = HelperGoldenTests.LoadGolden();

    static readonly DateTimeOffset Later = new(2026, 9, 27, 8, 1, 2, TimeSpan.Zero);

    static readonly string[] Layouts = ["layout_signed_out", "layout_signed_in", "layout_alert", "layout_vapid"];

    public static TheoryData<string> LayoutNames => [.. Layouts];

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void The_layout_renders_an_empty_page_like_the_reference(string name)
    {
        var expected = Golden.GetProperty("layouts").GetProperty(name);
        var view = LayoutView(name);

        var html = HelperGoldenTests.Render(w => view.ApplicationLayout(w, SafeString.Empty));

        Assert.Equal(expected.GetProperty("html").GetString(), html);
        Assert.Equal(expected.GetProperty("link").GetString(), view.LinkHeader);
    }

    [Fact]
    public void Every_golden_layout_is_checked()
    {
        var golden = Golden.GetProperty("layouts").EnumerateObject().Select(property => property.Name);
        Assert.Equal(golden.Order(StringComparer.Ordinal), Layouts.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_page_body_goes_in_main_unescaped()
    {
        var view = LayoutView("layout_signed_out");

        var html = HelperGoldenTests.Render(w => view.ApplicationLayout(w, new SafeString("<p>page</p>")));

        Assert.Contains("<main id=\"main-content\">\n      <p>page</p>\n\n      <footer id=\"footer\">", html, StringComparison.Ordinal);
    }

    // Independent of generate.rb's stand-ins: the reference app itself served sessions/new
    // (reference-rust's views golden, captured from the running app), so the layout around that
    // page must match it outside the page body, given the same title, VAPID key and account.
    [Fact]
    public void The_layout_matches_a_page_the_reference_app_served()
    {
        var served = ServedGolden("sessions_new.html");
        var token = CsrfToken().Match(served).Groups[1].Value;
        var view = new View
        {
            Assets = ReferenceAssets.Bundle,
            Origin = new UrlBase("http", "campfire.test"),
            FormAuthenticityToken = (_, _) => token,
            CurrentAccount = new CurrentAccount(null, LogoAttached: false, new DateTimeOffset(2026, 9, 26, 13, 0, 20, TimeSpan.Zero)),
            VapidPublicKey = "BEYXTBB5_jNhNzXDmx5KEU55Vbbd-u--Lk9rM5OFQvUkPIBwZJ9QzAq0zdEzFw6yTV8cTriz_qYBVicY02_VxTQ=",
            PageTitle = "Sign in",
        };
        view.ContentFor("head", new SafeString("<meta name=\"turbo-visit-control\" content=\"reload\">"));

        var html = HelperGoldenTests.Render(w => view.ApplicationLayout(w, SafeString.Empty));

        Assert.Equal(served[..served.IndexOf("<main", StringComparison.Ordinal)], html[..html.IndexOf("<main", StringComparison.Ordinal)]);
        Assert.Equal(served[served.IndexOf("</main>", StringComparison.Ordinal)..], html[html.IndexOf("</main>", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void The_action_text_content_layout_wraps_content_like_the_reference()
    {
        var view = LayoutView("layout_signed_out");

        var html = HelperGoldenTests.Render(w => view.ActionTextContentLayout(w, new SafeString("<p>Hi <strong>there</strong></p>\n")));

        Assert.Equal(ServedGolden("action_text_content.html"), html);
    }

    [Fact]
    public void The_mailer_layout_renders_like_the_reference()
    {
        var view = LayoutView("layout_signed_out");

        var html = HelperGoldenTests.Render(w => view.MailerLayout(w, new SafeString("<p>Mail</p>")));

        Assert.Equal(ServedGolden("mailer_layout.html"), html);
    }

    // What reference-tools/views/a/render.rb captured from the reference app.
    static string ServedGolden(string name) =>
        File.ReadAllText(Path.Combine(ReferenceAssets.RepositoryRoot, "reference-rust", "crates", "views", "tests", "golden", "a", name));

    [GeneratedRegex("<meta name=\"csrf-token\" content=\"([^\"]+)\" />")]
    private static partial Regex CsrfToken();

    static View LayoutView(string name)
    {
        var flash = Flash.FromSessionValue(null);
        View view = name switch
        {
            "layout_signed_in" => NewView(flash, new CurrentUser(3, "Kevin <K>", CanAdminister: true), new CurrentAccount(":root { --x: 1 }", LogoAttached: true, Later)),
            "layout_vapid" => NewView(flash, vapidPublicKey: "BKey-_123=", appVersion: "abc123"),
            _ => NewView(flash),
        };

        switch (name)
        {
            case "layout_signed_in":
                view.PageTitle = "Lobby & more";
                view.BodyClass = "sidebar";
                flash.Notice = "Saved <ok>";
                view.ContentFor("head", new SafeString("<meta name=\"x\">"));
                view.ContentFor("nav", new SafeString("<b>nav</b>"));
                view.ContentFor("footer", "foot & er");
                view.ContentFor("sidebar", new SafeString("<aside-content>"));
                break;
            case "layout_alert":
                flash.Notice = "Ignored";
                flash.Alert = "Failed!";
                break;
        }
        return view;
    }

    static View NewView(Flash flash, CurrentUser? user = null, CurrentAccount? account = null, string? vapidPublicKey = null, string appVersion = "0") => new()
    {
        Assets = ReferenceAssets.Bundle,
        Origin = new UrlBase("https", "campfire.test"),
        RequestPath = "/session/transfers/abc",
        RequestUrl = "https://campfire.test/session/transfers/abc",
        FormAuthenticityToken = (action, method) => $"token({action}|{method})",
        StreamKeys = new KeyGenerator("unused"),
        Flash = flash,
        CurrentUser = user,
        CurrentAccount = account,
        VapidPublicKey = vapidPublicKey,
        AppVersion = appVersion,
    };
}

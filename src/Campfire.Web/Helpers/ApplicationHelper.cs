namespace Campfire.Web.Helpers;

// reference/app/helpers/application_helper.rb, accounts_helper.rb, cable_helper.rb and
// version_helper.rb: what the layout and the account pages read.
public partial class View
{
    /// <summary><c>page_title_tag</c>.</summary>
    public SafeString PageTitleTag() => Tag.Title(PageTitle ?? "Campfire");

    /// <summary><c>current_user_meta_tags</c>: the signed-in user's id and name, for the frontend.</summary>
    public SafeString CurrentUserMetaTags()
    {
        if (CurrentUser is not { } user)
        {
            return SafeString.Empty;
        }

        return OutputSafety.SafeJoin(
        [
            TagHelper.Tag("meta", new() { { "name", "current-user-id" }, { "content", user.Id } }),
            TagHelper.Tag("meta", new() { { "name", "current-user-name" }, { "content", user.Name } }),
        ]);
    }

    /// <summary><c>custom_styles_tag</c>: the account's CSS, unescaped, when it has any.</summary>
    public SafeString CustomStylesTag() =>
        CurrentAccount?.CustomStyles is { } customStyles
            ? Tag.Style(new SafeString(customStyles), new() { { "data", new HtmlOptions { { "turbo_track", "reload" } } } })
            : SafeString.Empty;

    /// <summary><c>body_classes</c>: <c>@body_class</c>, "admin" and "account-has-logo".</summary>
    public string BodyClasses()
    {
        string?[] classes =
        [
            BodyClass,
            CurrentUser?.CanAdminister == true ? "admin" : null,
            CurrentAccount?.LogoAttached == true ? "account-has-logo" : null,
        ];
        return string.Join(" ", classes.Where(name => name is not null));
    }

    /// <summary><c>link_back</c>: back to the referrer, or home when there is none or it is this page.</summary>
    public SafeString LinkBack()
    {
        var backUrl = Referrer;
        if (backUrl is null || backUrl == RequestUrl)
        {
            backUrl = Routes.RootPath();
        }
        return LinkBackTo(backUrl);
    }

    /// <summary><c>link_back_to(destination)</c>.</summary>
    public SafeString LinkBackTo(string destination) =>
        LinkTo(
            OutputSafety.Concat(
                ImageTag("arrow-left.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }),
                Tag.Span("Go Back", new() { { "class", "for-screen-reader" } })),
            destination,
            new() { { "class", "btn" } });

    /// <summary><c>account_logo_tag(style:)</c>.</summary>
    public SafeString AccountLogoTag(string? style = null) =>
        Tag.Figure(
            ImageTag(Routes.FreshAccountLogoPath(CurrentAccount?.UpdatedAt), new() { { "alt", "Account logo" }, { "size", 300 } }),
            new() { { "class", $"account-logo avatar {style}" } });

    /// <summary>
    /// <c>script_aware_action_cable_meta_tag</c>: <c>Pathname(script_name) + Pathname("/cable")</c>,
    /// which is "/cable" whatever the script name, since the mount path is absolute.
    /// </summary>
    public static SafeString ScriptAwareActionCableMetaTag() =>
        Tag.Meta(new() { { "name", "action-cable-url" }, { "content", "/cable" } });

    /// <summary><c>version_badge</c>.</summary>
    public SafeString VersionBadge() => Tag.Span(AppVersion, new() { { "class", "version-badge" } });

    /// <summary>
    /// <c>broadcast_image_tag(image, options)</c>: an image from an asset name or a path (an
    /// attachment's <c>polymorphic_url(only_path: true)</c>).
    /// </summary>
    public SafeString BroadcastImageTag(string image, HtmlOptions? options = null) => ImageTag(BroadcastImagePath(image), options);

    /// <summary><c>broadcast_image_path(image)</c>.</summary>
    public string BroadcastImagePath(string image) => ImagePath(image);
}

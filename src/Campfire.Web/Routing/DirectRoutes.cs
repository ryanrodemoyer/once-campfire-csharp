using Campfire.RailsCompat.Formatting;
using Campfire.Web.Routing;

namespace Campfire.Web;

// The direct routes in reference/config/routes.rb. A direct route's _path is its _url with the
// scheme and host cut off (ActionDispatch::Routing::RouteSet::CustomUrlHelper#call).
public static partial class Routes
{
    /// <summary>
    /// <c>fresh_account_logo_path(size:)</c>: <c>route_for :account_logo, v:
    /// Current.account&amp;.updated_at&amp;.to_fs(:number), size: options[:size]</c>.
    /// </summary>
    public static string FreshAccountLogoPath(DateTimeOffset? accountUpdatedAt, object? size = null) =>
        AccountLogoPath(FreshAccountLogoOptions(accountUpdatedAt, size));

    /// <summary><c>fresh_account_logo_url(size:)</c>.</summary>
    public static string FreshAccountLogoUrl(UrlBase origin, DateTimeOffset? accountUpdatedAt, object? size = null) =>
        AccountLogoUrl(origin, FreshAccountLogoOptions(accountUpdatedAt, size));

    /// <summary>
    /// <c>fresh_user_avatar_path(user)</c>: <c>route_for :user_avatar, user.avatar_token, v:
    /// user.updated_at.to_fs(:number)</c>.
    /// </summary>
    public static string FreshUserAvatarPath(object? avatarToken, DateTimeOffset userUpdatedAt) =>
        UserAvatarPath(avatarToken, new RouteOptions { { "v", TimeFormats.ToFsNumber(userUpdatedAt) } });

    /// <summary><c>fresh_user_avatar_url(user)</c>.</summary>
    public static string FreshUserAvatarUrl(UrlBase origin, object? avatarToken, DateTimeOffset userUpdatedAt) =>
        UserAvatarUrl(origin, avatarToken, new RouteOptions { { "v", TimeFormats.ToFsNumber(userUpdatedAt) } });

    static RouteOptions FreshAccountLogoOptions(DateTimeOffset? accountUpdatedAt, object? size) => new()
    {
        { "v", accountUpdatedAt is { } updatedAt ? TimeFormats.ToFsNumber(updatedAt) : null },
        { "size", size },
    };
}

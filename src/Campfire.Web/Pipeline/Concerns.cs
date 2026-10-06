using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.RailsCompat.Ruby;
using Campfire.RailsCompat.UserAgent;
using Campfire.Web.Routing;

namespace Campfire.Web.Pipeline;

// The small concerns ApplicationController includes, each citing its file in
// reference/app/controllers/concerns.
public partial class ApplicationController
{
    ApplicationPlatform? platform;

    // version_headers.rb
    /// <summary><c>set_version_headers</c>: <c>X-Version</c> and <c>X-Rev</c> (left out when <c>GIT_REVISION</c> isn't set).</summary>
    public void SetVersionHeaders()
    {
        Headers["X-Version"] = App.AppVersion;
        Headers["X-Rev"] = App.GitRevision;
    }

    // set_current_request.rb: `Current.request = request` has nothing to do, since
    // Current.Request is this controller already.
    /// <summary><c>default_url_options</c>: the request's protocol, host and port, for <c>_url</c> helpers.</summary>
    public UrlBase UrlBase => RequestUrl.UrlBase;

    // block_banned_requests.rb
    /// <summary><c>reject_banned_ip</c>: 429 for a banned <c>remote_ip</c>.</summary>
    public async ValueTask RejectBannedIpAsync()
    {
        var ipAddress = RemoteIp;
        if (await ReadAsync(session => Bans.IsBanned(session, ipAddress)).ConfigureAwait(false))
        {
            Head(429);
        }
    }

    /// <summary><c>safe_request?</c>: GET or HEAD, which bans don't block.</summary>
    public bool IsSafeRequest => Request.Method is "GET" or "HEAD";

    // authorization.rb
    /// <summary><c>ensure_can_administer</c>: 403 unless <c>Current.user.can_administer?</c>.</summary>
    public void EnsureCanAdminister()
    {
        var user = Current.User ?? throw new InvalidOperationException("undefined method 'can_administer?' for nil");
        if (!user.CanAdminister())
        {
            Head(403);
        }
    }

    // set_platform.rb
    /// <summary><c>platform</c> (a helper method): the request's <c>ApplicationPlatform</c>.</summary>
    public ApplicationPlatform Platform => platform ??= new ApplicationPlatform(UserAgent);

    // allow_browser.rb
    /// <summary>
    /// <c>allow_browser versions: AllowBrowser::VERSIONS, block: -&gt; { render template:
    /// "sessions/incompatible_browser" }</c>.
    /// </summary>
    public async ValueTask AllowBrowserAsync()
    {
        if (AllowBrowser.IsBlocked(UserAgent))
        {
            await RenderIncompatibleBrowserAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>render template: "sessions/incompatible_browser"</c>, which A01 provides by implementing
    /// <see cref="IncompatibleBrowserPage"/> in its own file. Until it does, a blocked browser
    /// gets a 501.
    /// </summary>
    protected virtual ValueTask RenderIncompatibleBrowserAsync()
    {
        Func<ApplicationController, ValueTask>? render = null;
        IncompatibleBrowserPage(ref render);
        return render is null
            ? throw new NotImplementedRouteException("sessions/incompatible_browser isn't ported yet")
            : render(this);
    }

    /// <summary>The hook for the <c>sessions/incompatible_browser</c> page (A01's).</summary>
    static partial void IncompatibleBrowserPage(ref Func<ApplicationController, ValueTask>? render);

    // tracked_room_visit.rb
    /// <summary><c>remember_last_room_visited</c>: <c>cookies.permanent[:last_room] = @room.id</c>.</summary>
    public void RememberLastRoomVisited(long roomId) =>
        Cookies.Permanent.Set("last_room", roomId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// <c>last_room_visited</c> (a helper method): the <c>last_room</c> cookie's room if the user is
    /// in it, else <c>Current.user.rooms.original</c>.
    /// </summary>
    public async ValueTask<Room?> LastRoomVisitedAsync()
    {
        var user = Current.User ?? throw new InvalidOperationException("undefined method 'rooms' for nil");
        var lastRoom = Cookies["last_room"] is { } cookie ? ActiveModelInteger.Cast(cookie) : null;
        return await ReadAsync(session =>
            (lastRoom is { } roomId ? Rooms.FindForUser(session, user.Id, roomId) : null) ?? Rooms.OriginalForUser(session, user.Id)).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>RoomScoped</c> (reference/app/controllers/concerns/room_scoped.rb): a controller that
/// includes it declares <c>.Before("set_room", c =&gt; c.SetRoomAsync())</c> with
/// <see cref="FindRoomAsync"/> filling its <c>@membership</c> and <c>@room</c>.
/// </summary>
public static class RoomScoped
{
    /// <summary>
    /// <c>set_room</c>: <c>Current.user.memberships.find_by!(room_id: params[:room_id])</c> and its
    /// room; <see cref="RecordNotFoundException"/> (404) when the user isn't in the room.
    /// </summary>
    public static async ValueTask<(Membership Membership, Room Room)> FindRoomAsync(ApplicationController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        var user = controller.Current.User ?? throw new InvalidOperationException("undefined method 'memberships' for nil");
        var roomId = controller.Params["room_id"] is string param ? ActiveModelInteger.Cast(param) : null;
        var found = await controller.ReadAsync(session =>
        {
            var membership = roomId is { } id ? Memberships.FindFor(session, user.Id, id) : null;
            return membership is null ? default : (membership, Rooms.Find(session, membership.RoomId));
        }).ConfigureAwait(false);
        if (found.membership is null)
        {
            throw new RecordNotFoundException("Couldn't find Membership");
        }
        // Memberships have no foreign key to rooms: a membership of a deleted room has no room,
        // and the action then fails on it (a 500).
        return (found.membership, found.Item2 ?? throw new InvalidOperationException("The membership's room is gone"));
    }
}

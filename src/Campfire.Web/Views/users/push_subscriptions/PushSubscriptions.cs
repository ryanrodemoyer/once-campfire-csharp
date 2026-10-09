using Campfire.Data.Records;

namespace Campfire.Web.Helpers;

// reference/app/views/users/push_subscriptions/index.html.erb and _push_subscription.html.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary><c>users/push_subscriptions/index</c>: the signed-in person's push subscriptions.</summary>
    [ErbTemplate("users/push_subscriptions/index.html.erb.cs")]
    public partial void UsersPushSubscriptionsIndex(HtmlWriter w, PushSubscriptionsPage page);

    /// <summary><c>users/push_subscriptions/_push_subscription</c>: one subscription, its browser and its buttons.</summary>
    [ErbTemplate("users/push_subscriptions/_push_subscription.html.erb.cs")]
    public partial void UsersPushSubscriptionsPushSubscription(HtmlWriter w, PushSubscription pushSubscription);
}
#pragma warning restore IDE0060

/// <summary>What <c>users/push_subscriptions/index</c> shows.</summary>
/// <param name="Subscriptions"><c>Current.user.push_subscriptions</c>.</param>
/// <param name="LastRoomId"><c>last_room_visited</c>'s id, for the back link.</param>
public sealed record PushSubscriptionsPage(IReadOnlyList<PushSubscription> Subscriptions, long? LastRoomId);

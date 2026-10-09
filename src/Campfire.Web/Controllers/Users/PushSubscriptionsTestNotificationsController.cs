using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Jobs.WebPush;
using Campfire.RailsCompat.Ruby;
using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Users::PushSubscriptions::TestNotificationsController</c>
/// (reference/app/controllers/users/push_subscriptions/test_notifications_controller.rb):
/// delivers one "Campfire Test" notification, then redirects to the subscription list.
/// </summary>
public sealed class UsersPushSubscriptionsTestNotificationsController : ApplicationController
{
    static readonly ControllerCallbacks<UsersPushSubscriptionsTestNotificationsController> Chain =
        Callbacks.For<UsersPushSubscriptionsTestNotificationsController>();

    /// <summary><c>POST /users/:user_id/push_subscriptions/:push_subscription_id/test_notifications</c>.</summary>
    public static readonly RequestDelegate Create = Action(Chain, c => c.CreateAsync());

    User User => Current.User ?? throw new InvalidOperationException("undefined method 'push_subscriptions' for nil");

    // find on the association (404 when the id is missing, not numeric, or another person's),
    // then notification(...).deliver. A refused push propagates; an endpoint that doesn't resolve
    // to a public address delivers nothing, and either way the action redirects.
    async ValueTask CreateAsync()
    {
        var user = User;
        var param = Params["push_subscription_id"];
        var id = param is string text ? ActiveModelInteger.Cast(text) : param as long?;
        var subscription = id is { } subscriptionId
            ? await ReadAsync(session => PushSubscriptions.FindForUser(session, user.Id, subscriptionId)).ConfigureAwait(false)
            : null;
        if (subscription is null)
        {
            throw new RecordNotFoundException($"Couldn't find Push::Subscription with 'id'={param} [WHERE \"push_subscriptions\".\"user_id\" = ?]");
        }

        var path = Routes.UserPushSubscriptionsUrl(UrlBase);
        var notification = await ReadAsync(session => WebPushNotification.For(
            session, subscription, new PushPayload("Campfire Test", Guid.NewGuid().ToString(), path))).ConfigureAwait(false);
        using var owned = App.WebPush is null ? App.OpenPushClient() : null;
        await notification.DeliverAsync(App.WebPush ?? owned!, RequestAborted).ConfigureAwait(false);
        RedirectTo(path);
    }
}

using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.Jobs.WebPush;
using Campfire.RailsCompat.Params;
using Campfire.RailsCompat.Ruby;
using Campfire.Storage.Blobs;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Users::PushSubscriptionsController</c>
/// (reference/app/controllers/users/push_subscriptions_controller.rb): the signed-in person's
/// push subscriptions, registering or touching one, and removing one.
/// </summary>
public sealed class UsersPushSubscriptionsController : ApplicationController
{
    static readonly ControllerCallbacks<UsersPushSubscriptionsController> Chain = Callbacks.For<UsersPushSubscriptionsController>();

    static readonly PermitFilter[] SubscriptionParams = ["endpoint", "p256dh_key", "auth_key"];

    /// <summary><c>GET /users/:user_id/push_subscriptions</c>. The URL's user is ignored; this is <c>Current.user</c>.</summary>
    public static readonly RequestDelegate Index = Action(Chain, c => c.IndexAsync());

    /// <summary><c>POST /users/:user_id/push_subscriptions</c>: touch a matching row, or create one.</summary>
    public static readonly RequestDelegate Create = Action(Chain, c => c.CreateAsync());

    /// <summary><c>DELETE /users/:user_id/push_subscriptions/:id</c>, then the index.</summary>
    public static readonly RequestDelegate Destroy = Action(Chain, c => c.DestroyAsync());

    User User => Current.User ?? throw new InvalidOperationException("undefined method 'push_subscriptions' for nil");

    async ValueTask IndexAsync()
    {
        RespondTo(MimeType.Html);
        var user = User;
        var lastRoomId = (await LastRoomVisitedAsync().ConfigureAwait(false))?.Id;
        var html = await ReadAsync(session =>
        {
            var page = new PushSubscriptionsPage(PushSubscriptions.ForUser(session, user.Id), lastRoomId);
            var view = NewView(session);
            var body = RenderString(w => view.UsersPushSubscriptionsIndex(w, page));
            return RenderLayout(view, body, Request.IsTurboFrameRequest);
        }).ConfigureAwait(false);
        Render(html, MimeType.Html.Value);
    }

    // find_by the permitted keys that were sent (an empty set is the user's first row). An
    // existing row is touched only when its stored endpoint still validates; otherwise, and when
    // a new row fails validation, head :unprocessable_entity. Nothing is written in those cases.
    async ValueTask CreateAsync()
    {
        var permitted = Params.RequireHash("push_subscription").Permit(SubscriptionParams);
        var user = User;
        var existing = await ReadAsync(session => FindBy(session, user.Id, permitted)).ConfigureAwait(false);
        var guard = App.PushGuard;
        if (existing is not null)
        {
            if ((await PushEndpoint.ValidationErrorsAsync(guard, existing.Endpoint, RequestAborted).ConfigureAwait(false)).Count > 0)
            {
                Head(422);
                return;
            }

            var id = existing.Id;
            await WriteAsync(tx => PushSubscriptions.Touch(tx.Session, id, Now)).ConfigureAwait(false);
            Head(200);
            return;
        }

        var endpoint = Column(permitted, "endpoint");
        if ((await PushEndpoint.ValidationErrorsAsync(guard, endpoint, RequestAborted).ConfigureAwait(false)).Count > 0)
        {
            Head(422);
            return;
        }

        var (p256dh, auth) = (Column(permitted, "p256dh_key"), Column(permitted, "auth_key"));
        await WriteAsync(tx => PushSubscriptions.Create(tx.Session, user.Id, endpoint, p256dh, auth, UserAgent, Now)).ConfigureAwait(false);
        Head(200);
    }

    // destroy_by(id:): only this user's row, and a miss still redirects. The id is cast the way
    // Active Record casts an integer column (`"56887440abc"` is 56887440; `"abc"` matches nothing).
    async ValueTask DestroyAsync()
    {
        var user = User;
        if (CastId(Params["id"]) is { } id)
        {
            await WriteAsync(tx =>
            {
                if (PushSubscriptions.FindForUser(tx.Session, user.Id, id) is not null)
                {
                    PushSubscriptions.Delete(tx.Session, id);
                }
            }).ConfigureAwait(false);
        }

        RedirectTo(Routes.UserPushSubscriptionsUrl(UrlBase));
    }

    // `find_by` on the association: only keys present in the hash, `IS NULL` for nil, and the
    // first row when the hash is empty. String columns are compared as Active Record serializes them.
    static PushSubscription? FindBy(SqliteSession session, long userId, ParamHash keys)
    {
        foreach (var row in PushSubscriptions.ForUser(session, userId))
        {
            if (Matches(row, keys))
            {
                return row;
            }
        }

        return null;
    }

    static bool Matches(PushSubscription row, ParamHash keys)
    {
        foreach (var (key, value) in keys)
        {
            var column = Attribute(row, key);
            if (value is null)
            {
                if (column is not null)
                {
                    return false;
                }
            }
            else if (column != SerializeString(value))
            {
                return false;
            }
        }

        return true;
    }

    static string? Attribute(PushSubscription row, string key) => key switch
    {
        "endpoint" => row.Endpoint,
        "p256dh_key" => row.P256dhKey,
        "auth_key" => row.AuthKey,
        _ => throw new InvalidOperationException($"unknown attribute '{key}' for Push::Subscription."),
    };

    // A key absent from the permitted hash is not assigned (the column stays NULL).
    static string? Column(ParamHash hash, string key) =>
        hash.TryGetValue(key, out var value) && value is not null ? SerializeString(value) : null;

    // ActiveModel::Type::String#serialize for the scalars permit keeps.
    static string SerializeString(object value) => value switch
    {
        string text => text,
        true => "t",
        false => "f",
        long number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString(CultureInfo.InvariantCulture),
        BigInteger number => number.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    static long? CastId(object? id) => id is string text ? ActiveModelInteger.Cast(text) : id as long?;

    View NewView(SqliteSession session)
    {
        var account = Accounts.First(session);
        var user = User;
        return new View
        {
            Assets = App.RequireAssets(),
            Origin = UrlBase,
            RequestPath = Request.Path,
            RequestUrl = RequestUrl.Url,
            Referrer = Referer,
            FormAuthenticityToken = (action, method) => FormAuthenticityToken(action, method),
            StreamKeys = App.Keys,
            Flash = Flash,
            CurrentUser = new CurrentUser(user.Id, user.Name, user.CanAdminister()),
            CurrentAccount = account is null ? null : new CurrentAccount(account.CustomStyles, BlobRecords.FindAttachedBlob(session, "Account", account.Id, "logo") is not null, account.UpdatedAt),
            VapidPublicKey = App.VapidPublicKey,
            AppVersion = App.AppVersion,
            Storage = App.RequireStorage(),
        };
    }

    static ReadOnlyMemory<byte> RenderLayout(View view, string page, bool turboFrame)
    {
        var buffer = new ArrayBufferWriter<byte>();
        if (turboFrame)
        {
            view.TurboRailsFrameLayout(new HtmlWriter(buffer), new SafeString(page));
        }
        else
        {
            view.ApplicationLayout(new HtmlWriter(buffer), new SafeString(page));
        }

        return buffer.WrittenMemory;
    }

    static string RenderString(Action<HtmlWriter> template)
    {
        var buffer = new ArrayBufferWriter<byte>();
        template(new HtmlWriter(buffer));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.RailsCompat.Params;
using Campfire.RailsCompat.Ruby;
using Campfire.RailsCompat.Signing;
using Campfire.Storage.Blobs;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Accounts::BotsController</c> (reference/app/controllers/accounts/bots_controller.rb): listing
/// the account's active bots with their curl commands, creating new bots, editing them, and
/// deactivating them.
/// </summary>
public sealed class AccountsBotsController : ApplicationController
{
    static readonly ControllerCallbacks<AccountsBotsController> Chain = Callbacks.For<AccountsBotsController>()
        .EnsureCanAdminister()
        .Before("set_bot", c => c.SetBotAsync(), only: ["edit", "update", "destroy"]);

    static readonly PermitFilter[] BotParams =
    [
        PermitFilter.Key("name"),
        PermitFilter.Key("avatar"),
        PermitFilter.Key("webhook_url"),
    ];

    User? bot;

    User CurrentBot => bot ?? throw new InvalidOperationException("set_bot hasn't run");

    public static readonly RequestDelegate Index = Action(Chain, c => c.IndexAsync());
    public static readonly RequestDelegate New = Action(Chain, c => c.NewAsync());
    public static readonly RequestDelegate Create = Action(Chain, c => c.CreateAsync());
    public static readonly RequestDelegate Edit = Action(Chain, c => c.EditAsync());
    public static readonly RequestDelegate Update = Action(Chain, c => c.UpdateAsync());
    public static readonly RequestDelegate Destroy = Action(Chain, c => c.DestroyAsync());

    async ValueTask IndexAsync()
    {
        await this.RenderActionAsync((view, session, w) =>
        {
            var bots = Users.ActiveBotsOrdered(session);
            var rows = bots.Select(b => new BotRow(
                b,
                new AvatarUser(b.Id, b.Title, TransferableUser.GenerateAvatarSignedId(App.Keys, b.Id), b.UpdatedAt),
                Rooms.ForUserWithoutDirectsOrdered(session, b.Id)
            )).ToList();
            view.AccountsBotsIndex(w, rows);
        }).ConfigureAwait(false);
    }

    async ValueTask NewAsync()
    {
        await this.RenderActionAsync((view, _, w) =>
        {
            view.AccountsBotsNew(w, FormModel.New(User.ModelName));
        }).ConfigureAwait(false);
    }

    async ValueTask CreateAsync()
    {
        var attributes = Params.RequireHash("user").Permit(BotParams);
        var name = attributes["name"] is { } nameParam ? RubyValues.ToS(nameParam) : null;
        var hasWebhook = attributes.ContainsKey("webhook_url");
        var webhookUrl = hasWebhook && attributes["webhook_url"] is { } urlParam ? RubyValues.ToS(urlParam) : null;
        var avatar = Avatar(attributes["avatar"]);
        var now = Now;

        using var staged = avatar is null ? null : App.RequireStorage().Stage(avatar);
        await WriteAsync(tx =>
        {
            var created = UserLifecycle.Create(tx, name!, emailAddress: null, passwordDigest: null, now, role: UserRole.Bot, bio: null, botToken: User.GenerateBotToken());
            if (staged is not null)
            {
                BlobStorage.AttachOne(tx, staged, User.ModelName, created.Id, AttachmentNames.Avatar, now);
            }
            if (hasWebhook && webhookUrl is not null)
            {
                Webhooks.Create(tx.Session, created.Id, webhookUrl, now);
            }
            return created;
        }).ConfigureAwait(false);

        RedirectTo(Routes.AccountBotsUrl(UrlBase));
    }

    async ValueTask EditAsync()
    {
        var (b, urlBase) = (CurrentBot, UrlBase);
        await this.RenderActionAsync((view, session, w) =>
        {
            var webhook = Webhooks.ForUser(session, b.Id);
            var avatarBlob = BlobRecords.FindAttachedBlob(session, User.ModelName, b.Id, AttachmentNames.Avatar);
            var avatarUrl = avatarBlob is not null
                ? App.Storage?.Urls.BlobRedirectPath(avatarBlob) is { } path ? UrlGenerator.HostUrl(urlBase) + path : null
                : null;

            var formModel = new FormModel(
                new(User.ModelName, b.Id),
                isPersisted: true,
                new Dictionary<string, object?>
                {
                    ["name"] = b.Name,
                    ["webhook_url"] = webhook?.Url,
                });

            view.AccountsBotsEdit(w, new BotEditModel(b, formModel, new BotFormModel(avatarUrl)));
        }).ConfigureAwait(false);
    }

    async ValueTask UpdateAsync()
    {
        var attributes = Params.RequireHash("user").Permit(BotParams);
        var name = attributes["name"] is { } nameParam ? RubyValues.ToS(nameParam) : null;
        var hasWebhook = attributes.ContainsKey("webhook_url");
        var webhookUrl = hasWebhook && attributes["webhook_url"] is { } urlParam ? RubyValues.ToS(urlParam) : null;
        var avatar = Avatar(attributes["avatar"]);
        var (b, now) = (CurrentBot, Now);

        using var staged = avatar is null ? null : App.RequireStorage().Stage(avatar);
        await WriteAsync(tx =>
        {
            if (!string.IsNullOrWhiteSpace(webhookUrl))
            {
                if (Webhooks.ForUser(tx.Session, b.Id) is { } existing)
                {
                    Webhooks.UpdateUrl(tx.Session, existing, webhookUrl, now);
                }
                else
                {
                    Webhooks.Create(tx.Session, b.Id, webhookUrl, now);
                }
            }
            else
            {
                Webhooks.DeleteForUser(tx.Session, b.Id);
            }

            var updated = Users.Update(tx.Session, b, now, name: name);
            if (staged is not null)
            {
                BlobStorage.AttachOne(tx, staged, User.ModelName, b.Id, AttachmentNames.Avatar, now);
            }
            return updated;
        }).ConfigureAwait(false);

        RedirectTo(Routes.AccountBotsUrl(UrlBase));
    }

    async ValueTask DestroyAsync()
    {
        var (b, now, seams) = (CurrentBot, Now, App.RequireSeams());
        await WriteAsync(tx => UserLifecycle.Deactivate(tx, seams, b, now)).ConfigureAwait(false);
        RedirectTo(Routes.AccountBotsUrl(UrlBase));
    }

    async ValueTask SetBotAsync()
    {
        var param = Params["id"];
        var id = param is string text ? ActiveModelInteger.Cast(text) : null;
        bot = (id is { } botId ? await ReadAsync(session => Users.FindActiveBot(session, botId)).ConfigureAwait(false) : null)
            ?? throw new RecordNotFoundException($"Couldn't find User with 'id'={param}");
    }

    static UploadedFile? Avatar(object? value) => value switch
    {
        null or "" => null,
        UploadedFile file => file,
        _ => throw new InvalidOperationException("Could not find or build blob: expected attachable"),
    };
}

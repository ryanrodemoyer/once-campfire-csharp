using Campfire.Data.Events;
using Campfire.Data.MessageAttachments;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Params;
using Campfire.RailsCompat.Signing;
using Campfire.Storage.Blobs;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>AccountsController</c> (reference/app/controllers/accounts_controller.rb): the account
/// settings page, and saving its name, logo and settings.
/// </summary>
public sealed class AccountsController : ApplicationController
{
    /// <summary><c>set_page_and_extract_portion_from users, per_page: 500</c></summary>
    public const int UsersPerPage = 500;

    static readonly ControllerCallbacks<AccountsController> Chain = Callbacks.For<AccountsController>()
        .EnsureCanAdminister(only: ["update"]);

    static readonly PermitFilter[] AccountParams = [PermitFilter.Key("name"), PermitFilter.Key("logo"), PermitFilter.AnyHash("settings")];

    public static readonly RequestDelegate Edit = Action(Chain, c => c.EditAsync());

    public static readonly RequestDelegate Update = Action(Chain, c => c.UpdateAsync());

    // `edit`: every administrator `User.visible_to(Current.user)` sees, then one page of members
    // (`set_page_and_extract_portion_from users.member, per_page: 500`).
    async ValueTask EditAsync()
    {
        var viewer = Current.User ?? throw new InvalidOperationException("undefined method 'can_administer?' for nil");
        var lastRoomId = (await LastRoomVisitedAsync().ConfigureAwait(false))?.Id;
        var pageNumber = GearedPage.NumberFrom(Params["page"]);
        await this.RenderActionAsync((view, session, w) =>
        {
            var account = Accounts.First(session) ?? throw new InvalidOperationException("undefined method 'name' for nil");
            var administrators = Users.VisibleToOrderedWithRole(session, viewer, UserRole.Administrator);
            var members = Users.VisibleToOrderedWithRole(session, viewer, UserRole.Member);
            var page = new GearedPage(pageNumber, members.Count, UsersPerPage);
            view.AccountsEdit(w, new AccountEditPage(
                View.AccountFormModel(account),
                Routes.AccountPath(new() { { "format", account.Id } }),
                account.Name,
                account.JoinCode,
                account.SettingsData.RestrictRoomCreationToAdministrators,
                viewer.IsAdministrator,
                lastRoomId,
                [.. administrators.Select(Row)],
                [.. page.Portion(members).Select(Row)],
                page.IsLast ? null : page.NextParam));
        }).ConfigureAwait(false);
    }

    AccountUserRow Row(User user) => new(user, TransferableUser.GenerateAvatarSignedId(App.Keys, user.Id));

    // `@account.update!(account_params)`, then back to the settings with a "✓".
    async ValueTask UpdateAsync()
    {
        var attributes = Params.RequireHash("account").Permit(AccountParams);
        await UpdateAccountAsync(App, attributes, Now, RequestAborted).ConfigureAwait(false);
        RedirectTo(Routes.EditAccountUrl(UrlBase), notice: "✓");
    }

    /// <summary>
    /// <c>Current.account.update!(attributes)</c> of <c>name</c>, <c>custom_styles</c>,
    /// <c>settings</c> and <c>logo</c>: the row is saved first, then the logo attached (or, for
    /// nil or "", detached). The account isn't touched: its <c>updated_at</c> (and so the logo's
    /// ETag and <c>?v=</c>) stays as it was, as in the reference.
    /// </summary>
    internal static async Task UpdateAccountAsync(WebApp app, ParamHash attributes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var logo = attributes.TryGetValue("logo", out var logoParam) ? Logo(logoParam) : null;
        using var staged = logo?.Upload is { } upload ? app.RequireStorage().Stage(upload) : null;
        // The attachment's `before_validation` analysis (analyze: :immediately).
        staged?.AnalyzeWithoutSaving();
        var jobs = app.RequireSeams().Jobs;
        await app.Database.WriteAsync(tx =>
        {
            var account = Accounts.First(tx.Session) ?? throw new InvalidOperationException("undefined method 'update!' for nil");
            account = Accounts.Update(
                tx.Session,
                account,
                now,
                name: attributes["name"] is { } name ? RubyValues.ToS(name) : null,
                customStyles: attributes.TryGetValue("custom_styles", out var styles) ? new Change<string?>(styles is null ? null : RubyValues.ToS(styles)) : null,
                settings: attributes["settings"] is ParamHash settings ? account.SettingsData.Assign(settings.Select(pair => KeyValuePair.Create(pair.Key, ParamValues.ToJson(pair.Value)))) : null);
            var accountId = account.Id;
            if (staged is not null)
            {
                var attached = BlobStorage.AttachOne(tx, staged, nameof(Account), accountId, AttachmentNames.Logo, now);
                ActiveStoragePurge.Later(tx, jobs, attached.ReplacedBlobId);
                if (!attached.Blob.IsAnalyzed)
                {
                    tx.AfterCommit(_ => jobs.Enqueue(new AnalyzeBlobJob(attached.Blob.Id)));
                }
            }
            else if (logo is not null)
            {
                DetachLogo(tx, jobs, accountId);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>Current.account.logo.destroy</c>: the attachment goes and its blob is purged later
    /// (<c>has_one_attached :logo, dependent: :purge_later</c>).
    /// </summary>
    internal static void DetachLogo(WriteTransaction tx, IJobQueue jobs, long accountId)
    {
        if (BlobRecords.FindAttachment(tx.Session, nameof(Account), accountId, AttachmentNames.Logo) is { } attachment)
        {
            BlobRecords.DeleteAttachment(tx.Session, attachment.Id);
            ActiveStoragePurge.Later(tx, jobs, attachment.BlobId);
        }
    }

    // `logo = value`: an upload attaches; nil and "" detach (Attached::Changes::DeleteOne).
    static LogoChange Logo(object? value) => value switch
    {
        null or "" => new LogoChange(null),
        UploadedFile file => new LogoChange(file),
        _ => throw new InvalidOperationException("Could not find or build blob: expected attachable"),
    };

    sealed record LogoChange(UploadedFile? Upload);
}

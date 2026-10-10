using System.Globalization;
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

    async ValueTask EditAsync()
    {
        var viewer = Current.User ?? throw new InvalidOperationException("undefined method 'can_administer?' for nil");
        var lastRoomId = (await LastRoomVisitedAsync().ConfigureAwait(false))?.Id;
        var pageNumber = PageNumber(Params["page"]);
        await this.RenderActionAsync((view, session, w) =>
        {
            var account = Accounts.First(session) ?? throw new InvalidOperationException("undefined method 'name' for nil");
            // `account_users.ordered.without_bots`, split into administrators and members.
            var users = AccountUsers(session, viewer).Select(user => new AccountUserRow(user, TransferableUser.GenerateAvatarSignedId(App.Keys, user.Id))).ToList();
            var page = new AccountEditPage(
                View.AccountFormModel(account),
                Routes.AccountPath(new() { { "format", account.Id } }),
                account.Name,
                account.JoinCode,
                account.SettingsData.RestrictRoomCreationToAdministrators,
                viewer.IsAdministrator,
                lastRoomId,
                [.. users.Where(row => row.User.IsAdministrator)],
                [.. users.Where(row => !row.User.IsAdministrator)],
                pageNumber == PageCount(users.Count) ? null : pageNumber + 1);
            view.AccountsEdit(w, page);
        }).ConfigureAwait(false);
    }

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
                if (!attached.Blob.IsAnalyzed)
                {
                    tx.AfterCommit(_ => app.RequireSeams().Jobs.Enqueue(new AnalyzeBlobJob(attached.Blob.Id)));
                }
            }
            else if (logo is not null)
            {
                DetachLogo(tx, accountId);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>Current.account.logo.destroy</c>: the attachment goes; its blob is left for
    /// <c>ActiveStorage::PurgeJob</c>.
    /// </summary>
    internal static void DetachLogo(WriteTransaction tx, long accountId)
    {
        if (BlobRecords.FindAttachment(tx.Session, nameof(Account), accountId, AttachmentNames.Logo) is { } attachment)
        {
            BlobRecords.DeleteAttachment(tx.Session, attachment.Id);
        }
    }

    // `account_users`: administrators also see banned people.
    static List<User> AccountUsers(SqliteSession session, User viewer) => viewer.CanAdminister()
        ? Users.WithStatusesOrderedWithoutBots(session, UserStatus.Active, UserStatus.Banned)
        : Users.ActiveOrderedWithoutBots(session);

    // GearedPagination::Recordset#page_count with one ratio of 500.
    static long PageCount(int records) => Math.Max(1, (records + UsersPerPage - 1) / UsersPerPage);

    // GearedPagination's `page_number_from(params[:page])`: `param.to_i`, else page 1.
    static long PageNumber(object? param)
    {
        var number = param switch
        {
            null => 0,
            string text => StringToI(text),
            _ => throw new InvalidOperationException($"undefined method 'to_i' for an instance of {ParamValues.RubyClassName(param)}"),
        };
        return number > 0 ? number : 1;
    }

    // Ruby's String#to_i: leading whitespace, a sign, then digits (underscores between them).
    static long StringToI(string text)
    {
        var span = text.AsSpan().TrimStart();
        var digits = new System.Text.StringBuilder();
        var at = 0;
        if (at < span.Length && span[at] is '+' or '-')
        {
            digits.Append(span[at++]);
        }
        for (; at < span.Length; at++)
        {
            if (char.IsAsciiDigit(span[at]))
            {
                digits.Append(span[at]);
            }
            else if (span[at] != '_' || at + 1 >= span.Length || !char.IsAsciiDigit(span[at + 1]) || at == 0 || !char.IsAsciiDigit(span[at - 1]))
            {
                break;
            }
        }
        return long.TryParse(digits.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : 0;
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

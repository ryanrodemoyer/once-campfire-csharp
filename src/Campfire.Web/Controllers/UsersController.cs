using Campfire.Data.Lifecycle;
using Campfire.Data.MessageAttachments;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Params;
using Campfire.RailsCompat.Ruby;
using Campfire.RailsCompat.Signing;
using Campfire.Storage.Blobs;
using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>UsersController</c> (reference/app/controllers/users_controller.rb): signing up through the
/// account's join link, and a person's page.
/// </summary>
public sealed class UsersController : ApplicationController
{
    static readonly ControllerCallbacks<UsersController> Chain = Callbacks.For<UsersController>()
        .RequireUnauthenticatedAccess(only: ["new", "create"])
        .Before("set_user", c => c.SetUserAsync(), only: ["show"])
        .Before("verify_join_code", c => c.VerifyJoinCodeAsync(), only: ["new", "create"]);

    static readonly PermitFilter[] UserParams =
        [PermitFilter.Key("name"), PermitFilter.Key("avatar"), PermitFilter.Key("email_address"), PermitFilter.Key("password")];

    User? user;

    /// <summary><c>@user = User.new</c>, then <c>users/new</c>.</summary>
    public static readonly RequestDelegate New = Action(Chain, c => c.RenderActionAsync(c.RenderNew));

    public static readonly RequestDelegate Create = Action(Chain, c => c.CreateAsync());

    /// <summary><c>users/show</c> for <c>@user</c>.</summary>
    public static readonly RequestDelegate Show = Action(Chain, c => c.ShowAsync());

    // `User.create!(user_params)`, signed in as the new user; an address that's taken goes to the
    // sign-in page with it filled in.
    async ValueTask CreateAsync()
    {
        var attributes = Params.RequireHash("user").Permit(UserParams);
        User created;
        try
        {
            created = await CreateUserAsync(attributes).ConfigureAwait(false);
        }
        catch (SqliteException error) when (IsRecordNotUnique(error))
        {
            RedirectTo(Routes.NewSessionUrl(UrlBase, new() { { "email_address", attributes["email_address"] } }));
            return;
        }
        await StartNewSessionForAsync(created).ConfigureAwait(false);
        RedirectTo(Routes.RootUrl(UrlBase));
    }

    async ValueTask ShowAsync()
    {
        var shown = user ?? throw new InvalidOperationException("set_user didn't run");
        var viewer = Current.User ?? throw new InvalidOperationException("undefined method 'can_administer?' for nil");
        var avatarToken = TransferableUser.GenerateAvatarSignedId(App.Keys, shown.Id);
        // users/profiles/_transfer, which administrators see on an active person's page.
        var transferUrl = viewer.CanAdminister() && shown.IsActive && !shown.IsBot
            ? Routes.SessionTransferUrl(UrlBase, TransferableUser.GenerateTransferId(App.Keys, shown.Id, Now))
            : null;
        await this.RenderActionAsync((view, _, w) => view.UsersShow(w, shown, avatarToken, transferUrl)).ConfigureAwait(false);
    }

    // set_user: `User.find(params[:id])`.
    async ValueTask SetUserAsync()
    {
        var id = Params["id"] is string param ? ActiveModelInteger.Cast(param) : null;
        user = (id is { } userId ? await ReadAsync(session => Users.Find(session, userId)).ConfigureAwait(false) : null)
            ?? throw new RecordNotFoundException($"Couldn't find User with 'id'={Params["id"]}");
    }

    // verify_join_code: `head :not_found if Current.account.join_code != params[:join_code]`.
    async ValueTask VerifyJoinCodeAsync()
    {
        var account = await Current.AccountAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("undefined method 'join_code' for nil");
        if (account.JoinCode != Params["join_code"] as string)
        {
            Head(404);
        }
    }

    void RenderNew(Helpers.View view, Data.Sqlite.SqliteSession session, Campfire.Templates.HtmlWriter w)
    {
        var account = Accounts.First(session) ?? throw new InvalidOperationException("undefined method 'name' for nil");
        view.UsersNew(w, (string)Params["join_code"]!, account.Name, SessionsPages.AdministratorContact(session));
    }

    // `User.create!`: the user (whose commit grants the open rooms) with its avatar attached in the
    // same transaction, the file kept once it commits.
    async ValueTask<User> CreateUserAsync(ParamHash attributes)
    {
        var name = attributes["name"] is { } nameParam ? RubyValues.ToS(nameParam) : null;
        var emailAddress = attributes["email_address"] is { } emailParam ? RubyValues.ToS(emailParam) : null;
        var passwordDigest = PasswordDigest(attributes["password"]);
        var avatar = Avatar(attributes["avatar"]);
        var now = Now;

        using var staged = avatar is null ? null : App.RequireStorage().Stage(avatar);
        return await WriteAsync(tx =>
        {
            // users.name is NOT NULL: a missing name fails here, as ActiveRecord::NotNullViolation does.
            var created = UserLifecycle.Create(tx, name!, emailAddress, passwordDigest, now);
            if (staged is not null)
            {
                var attached = BlobStorage.AttachOne(tx, staged, User.ModelName, created.Id, "avatar", now);
                if (!attached.Blob.IsAnalyzed)
                {
                    tx.AfterCommit(_ => App.RequireSeams().Jobs.Enqueue(new AnalyzeBlobJob(attached.Blob.Id)));
                }
            }
            return created;
        }).ConfigureAwait(false);
    }

    // has_secure_password's `password=`: nil clears the digest, an empty password leaves it unset.
    static string? PasswordDigest(object? password) => password switch
    {
        null => null,
        _ when RubyValues.ToS(password) is { Length: > 0 } secret => SecurePassword.Digest(secret),
        _ => null,
    };

    // `avatar = value`: an upload is attached; nil and "" attach nothing.
    static UploadedFile? Avatar(object? value) => value switch
    {
        null or "" => null,
        UploadedFile file => file,
        _ => throw new InvalidOperationException("Could not find or build blob: expected attachable"),
    };

    // ActiveRecord::RecordNotUnique: SQLITE_CONSTRAINT_UNIQUE (and _PRIMARYKEY).
    static bool IsRecordNotUnique(SqliteException error) =>
        error.SqliteErrorCode == 19 && error.SqliteExtendedErrorCode is 2067 or 1555;
}

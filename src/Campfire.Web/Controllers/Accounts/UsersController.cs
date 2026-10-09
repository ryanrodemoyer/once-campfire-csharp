using System.Buffers;
using System.Text;
using Campfire.Data.Lifecycle;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
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
/// <c>Accounts::UsersController</c> (reference/app/controllers/accounts/users_controller.rb): the
/// account's people, a page at a time, for the settings page's lazy frame; and an administrator
/// changing someone's role or deactivating them.
/// </summary>
public sealed class AccountsUsersController : ApplicationController
{
    static readonly ControllerCallbacks<AccountsUsersController> Chain = Callbacks.For<AccountsUsersController>()
        .EnsureCanAdminister(only: ["update", "destroy"])
        .Before("set_user", c => c.SetUserAsync(), only: ["update", "destroy"]);

    static readonly string[] Roles = ["member", "administrator"];

    User? user;

    User CurrentUser => user ?? throw new InvalidOperationException("set_user hasn't run");

    public static readonly RequestDelegate Index = Action(Chain, c => c.IndexAsync());

    public static readonly RequestDelegate Update = Action(Chain, c => c.UpdateAsync());

    public static readonly RequestDelegate Destroy = Action(Chain, c => c.DestroyAsync());

    // `set_page_and_extract_portion_from User.active.ordered.without_bots, per_page: 500`, then
    // the implicit render of index.turbo_stream (its only template, so any other format is a 406)
    // and GearedPagination::Controller's after_action `set_paginated_headers`.
    async ValueTask IndexAsync()
    {
        var pageNumber = GearedPage.NumberFrom(Params["page"]);
        RespondTo(MimeType.TurboStream);
        var (html, page) = await ReadAsync(session =>
        {
            var users = Users.ActiveOrderedWithoutBots(session);
            var page = new GearedPage(pageNumber, users.Count, AccountsController.UsersPerPage);
            var rows = page.Portion(users).Select(shown => new AccountUserRow(shown, TransferableUser.GenerateAvatarSignedId(App.Keys, shown.Id))).ToList();
            var view = NewView(session);
            return (RenderString(w => view.AccountsUsersIndex(w, new AccountUsersPage(rows, page.IsLast ? null : page.NextParam))), page);
        }).ConfigureAwait(false);
        Render(Encoding.UTF8.GetBytes(html), MimeType.TurboStream.Value);
        SetPaginatedHeaders(page);
    }

    // `@user.update(role_params)`, then back to the settings.
    async ValueTask UpdateAsync()
    {
        var role = RoleParam();
        var (shown, now) = (CurrentUser, Now);
        await WriteAsync(tx => Users.Update(tx.Session, shown, now, role: role)).ConfigureAwait(false);
        RedirectTo(Routes.EditAccountUrl(UrlBase));
    }

    // `@user.deactivate`, then back to the settings.
    async ValueTask DestroyAsync()
    {
        var (shown, now, seams) = (CurrentUser, Now, App.RequireSeams());
        await WriteAsync(tx => UserLifecycle.Deactivate(tx, seams, shown, now)).ConfigureAwait(false);
        RedirectTo(Routes.EditAccountUrl(UrlBase));
    }

    // set_user: `User.active.find(params[:user_id] || params[:id])`.
    async ValueTask SetUserAsync()
    {
        var param = Params["user_id"] ?? Params["id"];
        var id = param is string text ? ActiveModelInteger.Cast(text) : null;
        user = (id is { } userId ? await ReadAsync(session => Users.FindActive(session, userId)).ConfigureAwait(false) : null)
            ?? throw new RecordNotFoundException($"Couldn't find User with 'id'={param}");
    }

    // role_params: `params.require(:user)[:role].presence_in(%w[ member administrator ]) || "member"`.
    UserRole RoleParam()
    {
        var attributes = Params.Require("user") as ParamHash
            ?? throw new InvalidOperationException("no implicit conversion of Symbol into Integer");
        return attributes["role"] is string role && Roles.Contains(role) ? Enum.Parse<UserRole>(role, ignoreCase: true) : UserRole.Member;
    }

    // GearedPagination::Headers#apply, for JSON requests only: the total, and the next page's URL.
    void SetPaginatedHeaders(GearedPage page)
    {
        if (Request.Format != MimeType.Json)
        {
            return;
        }
        Headers["X-Total-Count"] = page.RecordsCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!page.IsLast)
        {
            Headers["Link"] = $"<{GearedPage.UrlWithPage(RequestUrl.Url, page.NextParam)}>; rel=\"next\"";
        }
    }

    // The view the stream renders in: the request, the current user and account, and the app's assets.
    View NewView(SqliteSession session)
    {
        var account = Accounts.First(session);
        var viewer = Current.User ?? throw new InvalidOperationException("undefined method 'can_administer?' for nil");
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
            CurrentUser = new CurrentUser(viewer.Id, viewer.Name, viewer.CanAdminister()),
            CurrentAccount = account is null ? null : new CurrentAccount(account.CustomStyles, BlobRecords.FindAttachedBlob(session, "Account", account.Id, "logo") is not null, account.UpdatedAt),
            VapidPublicKey = App.VapidPublicKey,
            AppVersion = App.AppVersion,
            Storage = App.Storage,
        };
    }

    static string RenderString(Action<HtmlWriter> template)
    {
        var buffer = new ArrayBufferWriter<byte>();
        template(new HtmlWriter(buffer));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>
/// A page of geared_pagination's offset portions with one ratio
/// (<c>GearedPagination::Recordset</c>, <c>Page</c> and <c>PortionAtOffset</c>).
/// </summary>
sealed record GearedPage(long Number, long RecordsCount, int PerPage)
{
    /// <summary><c>page_count</c>: at least one page, even with no records.</summary>
    public long PageCount => Math.Max(1, (RecordsCount + PerPage - 1) / PerPage);

    /// <summary><c>last?</c>: the page is the last one, so a page past it isn't.</summary>
    public bool IsLast => Number == PageCount;

    /// <summary><c>next_param</c></summary>
    public long NextParam => Number + 1;

    /// <summary><c>scope.limit(per_page).offset((number - 1) * per_page)</c> of the ordered records.</summary>
    public IEnumerable<T> Portion<T>(IEnumerable<T> records)
    {
        var offset = (Number - 1) * PerPage;
        return offset >= RecordsCount ? [] : records.Skip((int)offset).Take(PerPage);
    }

    /// <summary><c>page_number_from(params[:page])</c>: <c>param.to_i</c>, else page 1.</summary>
    public static long NumberFrom(object? param)
    {
        var number = param switch
        {
            null => 0,
            string text => RubyString.ToI(text),
            _ => throw new InvalidOperationException($"undefined method 'to_i' for an instance of {ParamValues.RubyClassName(param)}"),
        };
        return number > 0 ? number : 1;
    }

    /// <summary>
    /// GearedPagination::Headers#uri: <c>Addressable::URI.parse(request.url)</c> with
    /// <c>query_values</c> merged with the page. Addressable decodes each pair (a key without
    /// <c>=</c> has no value, and <c>+</c> in a value is a space), sorts them by key, and
    /// percent-encodes every byte but the unreserved ones.
    /// </summary>
    public static string UrlWithPage(string url, long page)
    {
        ArgumentNullException.ThrowIfNull(url);
        var at = url.IndexOf('?', StringComparison.Ordinal);
        var values = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        if (at >= 0)
        {
            foreach (var pair in url[(at + 1)..].Split('&').Where(pair => pair.Length > 0))
            {
                var equals = pair.IndexOf('=', StringComparison.Ordinal);
                var key = Unencode(equals < 0 ? pair : pair[..equals]);
                values[Latin1(key)] = equals < 0 ? null : Unencode(pair[(equals + 1)..].Replace('+', ' '));
            }
        }
        values["page"] = Encoding.ASCII.GetBytes(page.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var query = values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value is null ? Encode(Encoding.Latin1.GetBytes(pair.Key)) : $"{Encode(Encoding.Latin1.GetBytes(pair.Key))}={Encode(pair.Value)}");
        return $"{(at < 0 ? url : url[..at])}?{string.Join('&', query)}";
    }

    // Keys compare as Ruby compares strings, byte by byte: as Latin-1, one char per byte.
    static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    // URI.unencode_component: each %XX becomes its byte.
    static byte[] Unencode(string component)
    {
        var bytes = Encoding.UTF8.GetBytes(component);
        var result = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == '%' && i + 2 < bytes.Length && Uri.IsHexDigit((char)bytes[i + 1]) && Uri.IsHexDigit((char)bytes[i + 2]))
            {
                result.Add((byte)Convert.ToInt32(Encoding.ASCII.GetString(bytes, i + 1, 2), 16));
                i += 2;
            }
            else
            {
                result.Add(bytes[i]);
            }
        }
        return [.. result];
    }

    // URI.encode_component(component, UNRESERVED): every byte but A-Z a-z 0-9 - . _ ~ as %XX.
    static string Encode(byte[] bytes)
    {
        var result = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            if (char.IsAsciiLetterOrDigit((char)b) || b is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~')
            {
                result.Append((char)b);
            }
            else
            {
                result.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return result.ToString();
    }
}

using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;
using Campfire.Data.Queries;
using Campfire.Data.Records;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.GlobalId;
using Campfire.RailsCompat.Params;
using Campfire.RailsCompat.Ruby;
using Campfire.RailsCompat.Signing;
using Campfire.Web.Helpers;
using Campfire.Web.Pipeline;
using Campfire.Web.Routing;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Autocompletable::UsersController</c> (reference/app/controllers/autocompletable/users_controller.rb):
/// people the mention prompt and autocomplete inputs can pick. HTML is the prompt items; JSON is
/// the same page for the autocomplete field. Twenty per page.
/// </summary>
public sealed class AutocompletableUsersController : ApplicationController
{
    const int perPage = 20;

    static readonly ControllerCallbacks<AutocompletableUsersController> Chain = Callbacks.For<AutocompletableUsersController>();

    /// <summary><c>GET /autocompletable/users</c>.</summary>
    public static readonly RequestDelegate Index = Action(Chain, controller => controller.IndexAsync());

    // The block runs before formats are narrowed, so a .json request still gets the HTML page.
    protected override ValueTask RenderIncompatibleBrowserAsync() => PwaIncompatibleBrowser.RenderAsync(this);

    async ValueTask IndexAsync()
    {
        // set_page_and_extract_portion_from runs before respond_to, so a bad page fails first.
        var query = QueryText();
        var roomId = RoomId();
        var number = GearedPage.NumberFrom(Params["page"]);
        var users = await ReadAsync(session => Find(session, query, roomId)).ConfigureAwait(false);
        var format = RespondTo(MimeType.Html, MimeType.Json);
        var page = new GearedPage(number, users.Count, perPage);
        var portion = page.Portion(users).ToList();
        SetPaginatedHeaders(page);

        if (format == MimeType.Html)
        {
            var rows = portion.Select(Row).ToList();
            Render(RenderString(writer => NewView().AutocompletableUsersIndex(writer, rows)), MimeType.Html.Value);
            return;
        }

        Render(ToJson(portion), MimeType.Json.Value);
    }

    // find_autocompletable_users, then with_attached_avatar (a preload; nobody here has one) and ordered.
    List<User> Find(SqliteSession session, string? query, long? roomId)
    {
        var users = query is null ? Users.ActiveOrdered(session) : Users.ActiveFilteredByOrdered(session, query);
        if (roomId is not { } id)
        {
            return users;
        }

        var user = Current.User ?? throw new InvalidOperationException("undefined method 'rooms' for nil");
        if (Rooms.FindForUser(session, user.Id, id) is null)
        {
            throw new RecordNotFoundException($"Couldn't find Room with 'id'={Params["room_id"]}");
        }

        var members = Users.IdsInRoom(session, id).ToHashSet();
        return users.Where(candidate => members.Contains(candidate.Id)).ToList();
    }

    // filter, or else query. Blank is absent. A present non-string has no string conversion.
    string? QueryText()
    {
        foreach (var key in new[] { "filter", "query" })
        {
            var value = Params[key];
            if (!ParamValues.IsPresent(value))
            {
                continue;
            }

            // An array is interpolated with Array#to_s (`["da"]`), which matches no names.
            return value switch
            {
                string text => text,
                List<object?> list => "[" + string.Join(", ", list.Select(RubyInspect)) + "]",
                _ => throw new InvalidOperationException($"no implicit conversion of {ParamValues.RubyClassName(value)} into String"),
            };
        }

        return null;
    }

    // Current.user.rooms.find(room_id) when room_id is present. find flattens a one-element
    // array (`room_id[]=1`); several ids would return an array, which has no users.
    long? RoomId()
    {
        var param = Params["room_id"];
        if (!ParamValues.IsPresent(param))
        {
            return null;
        }

        // find(["1"]) flattens a one-element array. Several ids would return an array, which has no users.
        if (param is List<object?> list)
        {
            if (list.Count != 1)
            {
                throw new InvalidOperationException("undefined method 'users' for an instance of Array");
            }

            param = list[0];
        }

        if (param is not string text)
        {
            throw new InvalidOperationException($"undefined method 'to_i' for an instance of {ParamValues.RubyClassName(param)}");
        }

        return ActiveModelInteger.Cast(text) ?? throw new RecordNotFoundException($"Couldn't find Room with 'id'={text}");
    }

    // Ruby's Array#inspect for the values a query string can hold.
    static string RubyInspect(object? value) => value switch
    {
        null => "nil",
        string text => $"\"{text}\"",
        _ => value.ToString() ?? "nil",
    };

    // GearedPagination::Headers, JSON only. A page past the end is not the last, so it still links.
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

    // _user.json.jbuilder: h(name) first, then ActiveSupport::JSON.encode escapes the entities again.
    string ToJson(List<User> users)
    {
        var array = new JsonArray();
        foreach (var user in users)
        {
            array.Add(new JsonObject
            {
                ["name"] = RubyEscape.HtmlEscape(user.Name),
                ["value"] = user.Id,
                ["avatar_url"] = Routes.FreshUserAvatarUrl(UrlBase, TransferableUser.GenerateAvatarSignedId(App.Keys, user.Id), user.UpdatedAt),
                ["sgid"] = SignedGlobalId.AttachableSgid(App.Keys, GlobalId.Create(User.ModelName, user.Id)),
            });
        }

        return RailsJson.Encode(array);
    }

    AutocompletableUser Row(User user) => new(
        user.Id,
        user.Name,
        user.Title,
        SignedGlobalId.AttachableSgid(App.Keys, GlobalId.Create(User.ModelName, user.Id)),
        TransferableUser.GenerateAvatarSignedId(App.Keys, user.Id),
        user.UpdatedAt);

    View NewView() => new()
    {
        Assets = App.RequireAssets(),
        Origin = UrlBase,
    };

    static string RenderString(Action<HtmlWriter> template)
    {
        var buffer = new ArrayBufferWriter<byte>();
        template(new HtmlWriter(buffer));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

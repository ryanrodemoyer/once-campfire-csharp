using System.Buffers;
using System.Text;
using System.Text.Json;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Session;
using Campfire.Templates;
using Campfire.Web.Helpers;
using Campfire.Web.Helpers.Rails;
using Campfire.Web.Routing;
using Campfire.Web.Tests.Assets;

namespace Campfire.Web.Tests.Helpers;

// Each case renders what Vectors/generate.rb rendered through the reference's helpers, from the
// same inputs, and must produce the same bytes. A case is the C# translation of the ERB snippet
// with the same name in generate.rb.
public sealed class HelperGoldenTests
{
    const string secretKeyBase = "c20a0c5bc9bbba6b6c4d3e9b8e1ed7b4d3e1c1a9c8a5d1e0f9e8d7c6b5a4f3e2d1c0b9a8f7e6d5c4b3a2f1e0d9c8b7a6";

    static readonly DateTimeOffset Time = new DateTimeOffset(2026, 9, 26, 12, 23, 46, TimeSpan.Zero).AddTicks(4_835_210);
    static readonly DateTimeOffset Later = new(2026, 9, 27, 8, 1, 2, TimeSpan.Zero);

    static readonly RecordKey Room = new("Rooms::Open", 7);
    static readonly RecordKey Direct = new("Rooms::Direct", 9);
    static readonly RecordKey User = new("User", 3);
    static readonly RecordKey Message = new("Message", "a1b2-c3");
    static readonly RecordKey NewMessage = RecordKey.New("Message");

    static readonly AvatarUser AvatarUser = new(3, "Kevin & Co – Hi <there>", "eyJfcmFpbHMiOnsiZGF0YSI6M319--abc", Time);

    static readonly FormModel UserModel = new(User, isPersisted: true, new Dictionary<string, object?>
    {
        ["name"] = "Kevin & Co",
        ["email_address"] = "kevin@example.com",
        ["bio"] = "Hi <there>",
        ["role"] = "administrator",
        ["id"] = 3,
    });

    static readonly JsonElement Golden = LoadGolden();

    static readonly Dictionary<string, Action<View, HtmlWriter>> Cases = new()
    {
        // TagHelper
        ["tag_div_text"] = (v, w) => w.Append(Tag.Div("a & <b>")),
        ["tag_div_nil"] = (v, w) => w.Append(Tag.Div()),
        ["tag_div_safe"] = (v, w) => w.Append(Tag.Div(new SafeString("<b>bold</b>"), new() { { "id", "x" } })),
        ["tag_class_array"] = (v, w) => w.Append(Tag.Span("x", new() { { "class", new object?[] { "a", null, "b", new HtmlOptions { { "c", true }, { "d", false } } } } })),
        ["tag_data_values"] = (v, w) => w.Append(Tag.Div(options: new()
        {
            {
                "data", new HtmlOptions
                {
                    { "a", "s" }, { "b", 1 }, { "c", true }, { "d", false }, { "e", null },
                    { "f", new HtmlOptions { { "x", 1 }, { "y", "<>" } } }, { "g", new object[] { 1, "a" } },
                    { "h", "<>&\"'" }, { "i_j", "v" }, { "k", new SafeString("->") }, { "l", 1.5 },
                }
            },
        })),
        ["tag_aria_values"] = (v, w) => w.Append(Tag.Div(options: new()
        {
            {
                "aria", new HtmlOptions
                {
                    { "hidden", true }, { "label", "Close <x>" }, { "describedby", new[] { "a", "b" } },
                    { "empty", Array.Empty<object>() }, { "flags", new HtmlOptions { { "g", true }, { "h", false } } }, { "n", null },
                }
            },
        })),
        ["tag_boolean_attributes"] = (v, w) => w.Append(Tag.Input(new()
        {
            { "type", "checkbox" }, { "hidden", true }, { "disabled", false }, { "checked", "checked" }, { "required", null }, { "autofocus", "" },
        })),
        ["tag_attribute_escaping"] = (v, w) => w.Append(Tag.A("x", new()
        {
            { "title", "a\"b'<c>&" }, { "href", "/?a=1&b=2" }, { "data", new HtmlOptions { { "safe", new SafeString("&amp;\"") } } },
        })),
        ["tag_numbers"] = (v, w) => w.Append(Tag.Summary("s", new() { { "tabindex", -1 }, { "width", 1.5 }, { "height", 10 } })),
        ["tag_key_escaping"] = (v, w) => w.Append(Tag.Div("x", new() { { "bad key", "v" }, { "1st", "w" }, { "ok:name", "z" } })),
        ["tag_unescaped"] = (v, w) => w.Append(Tag.Div("<b>", new() { { "title", "<i>" } }, escape: false)),
        ["tag_void_elements"] = (v, w) =>
        {
            w.Append(Tag.Meta(new() { { "name", "a" }, { "content", "b" } }));
            w.Append(Tag.Link(new() { { "rel", "icon" }, { "href", "/x" } }));
            w.Append(Tag.Input(new() { { "type", "text" } }));
            w.Append(Tag.Img(new() { { "src", "/a.png" } }));
            w.Append(Tag.Br());
        },
        ["tag_dasherized"] = (v, w) =>
        {
            w.Append(Tag.Element("turbo_frame", null, new HtmlOptions { { "id", "f" } }));
            w.Append(Tag.Element("lexxy_prompt", "p", new HtmlOptions { { "trigger", "@" } }));
        },
        ["tag_textarea"] = (v, w) => w.Append(Tag.Element("textarea", "a\nb", new HtmlOptions { { "name", "t" } })),
        ["tag_block"] = (v, w) => w.Append(Tag.Div(new() { { "class", "outer" } }, () =>
        {
            w.WriteLiteral("<p>inner "u8);
            w.Append("<x>");
            w.WriteLiteral("</p>"u8);
        })),
        ["tag_legacy"] = (v, w) =>
        {
            w.Append(TagHelper.Tag("meta", new() { { "name", "a" }, { "content", "b" } }));
            w.WriteLiteral("|"u8);
            w.Append(TagHelper.Tag("input", new() { { "type", "text" } }, open: true));
            w.WriteLiteral("|"u8);
            w.Append(TagHelper.Tag("br"));
        },
        ["content_tag"] = (v, w) =>
        {
            w.Append(TagHelper.ContentTag("p", "a<b", new() { { "class", "y" } }));
            w.Append(TagHelper.ContentTag("section", new() { { "id", "s" } }, () => w.WriteLiteral("in"u8)));
        },
        ["token_list"] = (v, w) => w.Append(TagHelper.TokenList("a b", "a", null, new HtmlOptions { { "c", true }, { "d", false } }, new object[] { "e", new[] { "f" } })),
        ["safe_join"] = (v, w) =>
        {
            w.Append(OutputSafety.SafeJoin(["<a>", new SafeString("<b>"), new object?[] { "c", null }], "<br>"));
            w.WriteLiteral("|"u8);
            w.Append(OutputSafety.SafeJoin(["x", "y"], new SafeString("<hr>")));
        },
        ["raw"] = (v, w) => w.Append(OutputSafety.Raw("<b>")),

        // CaptureHelper
        ["content_for"] = (v, w) =>
        {
            v.ContentFor("x", "a<b");
            v.ContentFor(w, "x", () => w.WriteLiteral("<b>c</b>"u8));
            v.ContentFor("x", new SafeString("<i>"));
            w.Append(v.ContentFor("x") ?? default);
            w.WriteLiteral("|"u8);
            w.Append(v.HasContentFor("x"));
            w.WriteLiteral("|"u8);
            w.Append(v.HasContentFor("y"));
            w.WriteLiteral("|"u8);
            w.Append(v.ContentFor("y") is null ? "nil" : "present");
        },
        ["content_for_flush"] = (v, w) =>
        {
            v.ContentFor("x", "a");
            v.ContentFor("x", "b", flush: true);
            w.Append(v.ContentFor("x") ?? default);
        },

        // RecordIdentifier
        ["dom_id"] = (v, w) => w.Append(string.Join("|",
            RecordIdentifier.DomId(Room), RecordIdentifier.DomId(Room, "messages"), RecordIdentifier.DomId(NewMessage),
            RecordIdentifier.DomId(NewMessage, "form"), RecordIdentifier.DomId(Message), RecordIdentifier.DomId(User, "role"),
            RecordIdentifier.DomClass(Direct), RecordIdentifier.DomClass(Room, "edit"))),

        // UrlHelper
        ["link_to_text"] = (v, w) => w.Append(View.LinkTo("Home & away", "/")),
        ["link_to_nil_name"] = (v, w) => w.Append(View.LinkTo(null, "/x?a=1&b=2")),
        ["link_to_options"] = (v, w) => w.Append(View.LinkTo("<b>", "/r", new() { { "class", "c" }, { "title", "t" }, { "data", new HtmlOptions { { "turbo_frame", "_top" } } } })),
        ["link_to_method"] = (v, w) =>
        {
            w.Append(View.LinkTo("Delete", "/r/1", new() { { "method", "delete" } }));
            w.WriteLiteral("|"u8);
            w.Append(View.LinkTo("Get", "/r", new() { { "method", "get" }, { "rel", "me" } }));
            w.WriteLiteral("|"u8);
            w.Append(View.LinkTo("Put", "/r", new() { { "method", "put" }, { "rel", "me" } }));
            w.WriteLiteral("|"u8);
            w.Append(View.LinkTo("Already", "/r", new() { { "method", "post" }, { "rel", "nofollow" } }));
        },
        ["link_to_remote"] = (v, w) => w.Append(View.LinkTo("R", "/r", new() { { "remote", true } })),
        ["link_to_href_override"] = (v, w) => w.Append(View.LinkTo("H", "/r", new() { { "href", "#top" } })),
        ["link_to_block"] = (v, w) => w.Append(View.LinkTo("/rooms/1", new() { { "class", "btn" }, { "id", "go" } }, () => w.WriteLiteral("<span>Go</span>"u8))),
        ["button_to_text"] = (v, w) => w.Append(v.ButtonTo("Delete", "/rooms/1", new() { { "method", "delete" }, { "class", "btn" } })),
        ["button_to_block"] = (v, w) => w.Append(v.ButtonTo("/rooms/1/involvement", new()
        {
            { "method", "put" }, { "class", "btn" }, { "aria", new HtmlOptions { { "label", "Change" } } }, { "data", new HtmlOptions { { "turbo_confirm", "Sure?" } } },
        }, () => w.WriteLiteral("<img src=\"/x.svg\">"u8))),
        ["button_to_post"] = (v, w) =>
        {
            w.Append(v.ButtonTo("Go", "/go"));
            w.WriteLiteral("|"u8);
            w.Append(v.ButtonTo("Post", "/go", new() { { "method", "post" } }));
            w.WriteLiteral("|"u8);
            w.Append(v.ButtonTo("Patch", "/go", new() { { "method", "patch" } }));
        },
        ["button_to_get"] = (v, w) => w.Append(v.ButtonTo("Search", "/search", new() { { "method", "get" } })),
        ["button_to_form_options"] = (v, w) =>
        {
            w.Append(v.ButtonTo("A", "/a", new() { { "form", new HtmlOptions { { "class", "f" }, { "data", new HtmlOptions { { "x", 1 } } } } } }));
            w.WriteLiteral("|"u8);
            w.Append(v.ButtonTo("B", "/b", new() { { "form_class", "fc" } }));
        },
        ["button_to_params"] = (v, w) => w.Append(v.ButtonTo("P", "/p", new()
        {
            { "params", new HtmlOptions { { "b", 2 }, { "a", new object[] { 1, "x" } }, { "c", new HtmlOptions { { "d", "e" } } } } },
        })),
        ["button_to_token_options"] = (v, w) =>
        {
            w.Append(v.ButtonTo("N", "/n", new() { { "authenticity_token", false } }));
            w.WriteLiteral("|"u8);
            w.Append(v.ButtonTo("S", "/s", new() { { "authenticity_token", "given" } }));
        },
        ["button_to_remote"] = (v, w) => w.Append(v.ButtonTo("R", "/r", new() { { "remote", true } })),
        ["mail_to"] = (v, w) =>
        {
            w.Append(View.MailTo("kevin@example.com"));
            w.WriteLiteral("|"u8);
            w.Append(View.MailTo("a+b@x.com", "Write <me>", new() { { "class", "c" } }));
            w.WriteLiteral("|"u8);
            w.Append(View.MailTo("a@x.com", null, new() { { "subject", "Hi there & bye" }, { "body", "x y" }, { "cc", "c@x.com" } }));
        },

        // AssetTagHelper and friends
        ["image_tag"] = (v, w) =>
        {
            w.Append(v.ImageTag("check.svg"));
            w.WriteLiteral("|"u8);
            w.Append(v.ImageTag("check.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }));
            w.WriteLiteral("|"u8);
            w.Append(v.ImageTag("campfire-icon.png", new() { { "alt", "Campfire logo" }, { "width", 256 }, { "height", 216 } }));
            w.WriteLiteral("|"u8);
            w.Append(v.ImageTag("remove.svg", new() { { "size", "20x30" }, { "class", "x" } }));
            w.WriteLiteral("|"u8);
            w.Append(v.ImageTag("external/switch.svg", new() { { "alt", "the switch" }, { "size", 22 } }));
        },
        ["image_tag_paths"] = (v, w) =>
        {
            w.Append(v.ImageTag("/rails/active_storage/x.png", new() { { "loading", "lazy" } }));
            w.WriteLiteral("|"u8);
            w.Append(v.ImageTag("https://example.com/a.png"));
            w.WriteLiteral("|"u8);
            w.Append(v.ImageTag("check.svg?v=1#f"));
            w.WriteLiteral("|"u8);
            w.Append(v.ImageTag("check.svg", new() { { "size", "big" } }));
        },
        ["image_path"] = (v, w) => w.Append(string.Join("|", v.ImagePath("add.svg"), v.ImageUrl("screenshots/android-chat.png"), v.AssetPath("check.svg"), v.ImagePath(""))),
        ["stylesheet_link_tag_all"] = (v, w) => w.Append(v.StylesheetLinkTagAll(new() { { "data-turbo-track", "reload" } })),
        ["csrf_meta_tags"] = (v, w) => w.Append(v.CsrfMetaTags()),

        // FormTagHelper
        ["hidden_field_tag"] = (v, w) =>
        {
            w.Append(View.HiddenFieldTag("push_subscription_endpoint", null, new() { { "data", new HtmlOptions { { "sessions_target", "pushSubscriptionEndpoint" } } } }));
            w.WriteLiteral("|"u8);
            w.Append(View.HiddenFieldTag("user_ids[]", 5, new() { { "id", null } }));
            w.WriteLiteral("|"u8);
            w.Append(View.HiddenFieldTag("boost[content]", "👍"));
        },
        ["check_box_tag"] = (v, w) =>
        {
            w.Append(View.CheckBoxTag("user_ids[]", 5, true, new() { { "class", "switch__input" }, { "id", null } }));
            w.WriteLiteral("|"u8);
            w.Append(View.CheckBoxTag("user_ids[]", 6, false, new() { { "class", "switch__input" }, { "id", null } }));
            w.WriteLiteral("|"u8);
            w.Append(View.CheckBoxTag("agree"));
        },
        ["text_field_tag"] = (v, w) => w.Append(View.TextFieldTag("q", "a&b", new() { { "class", "i" } })),
        ["button_tag"] = (v, w) =>
        {
            w.Append(View.ButtonTag("Go"));
            w.WriteLiteral("|"u8);
            w.Append(View.ButtonTag(new HtmlOptions { { "class", "btn" }, { "type", "submit" } }, () => w.WriteLiteral("<i>x</i>"u8)));
            w.WriteLiteral("|"u8);
            w.Append(View.ButtonTag(new HtmlOptions { { "type", "submit" }, { "form", "f" }, { "name", "n" } }, () => w.WriteLiteral("y"u8)));
        },

        // FormHelper
        ["form_with_url_block"] = (v, w) => w.Append(v.FormWith(null, "/session", new()
        {
            { "method", "delete" }, { "data", new HtmlOptions { { "controller", "sessions" } } },
        }, form => w.Append(View.HiddenFieldTag("push_subscription_endpoint")))),
        ["form_with_model"] = (v, w) => w.Append(v.FormWith(UserModel, "/users/3", new() { { "class", "center" } }, form =>
        {
            w.WriteLiteral("\n"u8);
            w.Append(form.TextField("name", new() { { "class", "input" }, { "autocomplete", "name" }, { "placeholder", "Name" }, { "autofocus", true }, { "required", true }, { "maxlength", 20 } }));
            w.WriteLiteral("\n"u8);
            w.Append(form.EmailField("email_address", new() { { "class", "input" }, { "value", "other@example.com" } }));
            w.WriteLiteral("\n"u8);
            w.Append(form.PasswordField("password", new() { { "class", "input" }, { "required", true }, { "maxlength", 72 } }));
            w.WriteLiteral("\n"u8);
            w.Append(form.TextArea("bio", new() { { "class", "input" }, { "maxlength", 200 } }));
            w.WriteLiteral("\n"u8);
            w.Append(form.TextArea("bio", new() { { "size", "20x3" }, { "value", "x" } }));
            w.WriteLiteral("\n"u8);
            w.Append(form.UrlField("webhook_url", new() { { "class", "input" } }));
            w.WriteLiteral("\n"u8);
            w.Append(form.HiddenField("id"));
            w.WriteLiteral("\n"u8);
            w.Append(form.CheckBox("role", new() { { "data", new HtmlOptions { { "action", "form#submit" } } }, { "hidden", true }, { "id", "role_3" }, { "disabled", false } }, "administrator", "member"));
            w.WriteLiteral("\n"u8);
            w.Append(form.CheckBox("role", new(), "member", "administrator"));
            w.WriteLiteral("\n"u8);
            w.Append(form.TextField("name", new() { { "name", "room[name]" }, { "id", "room_name" } }));
            w.WriteLiteral("\n"u8);
            w.Append(form.TextField("missing", new() { { "id", null } }));
            w.WriteLiteral("\n"u8);
            w.Append(form.Button(new HtmlOptions { { "class", "btn" }, { "type", "submit" } }, () => w.WriteLiteral("Save"u8)));
            w.WriteLiteral("\n"u8);
            w.Append(form.Button("Plain"));
            w.WriteLiteral("\n"u8);
            w.Append(form.Button());
            w.WriteLiteral("\n"u8);
        })),
        ["form_with_file_field"] = (v, w) => w.Append(v.FormWith(UserModel, "/users/3/profile", new()
        {
            { "method", "patch" }, { "data", new HtmlOptions { { "controller", "form" } } },
        }, form =>
        {
            w.Append(form.FileField("avatar", new() { { "id", "file" }, { "class", "input" }, { "accept", "image/*" } }));
            w.Append(form.FileField("photos", new() { { "multiple", true } }));
        })),
        ["form_with_new_model"] = (v, w) => w.Append(v.FormWith(FormModel.New("Message"), "/rooms/7/messages", new()
        {
            { "id", "composer" }, { "data", new HtmlOptions { { "controller", "composer" } } },
        }, form =>
        {
            w.Append(form.HiddenField("client_message_id", new() { { "data", new HtmlOptions { { "composer_target", "clientid" } } } }));
            w.Append(form.Button(new HtmlOptions { { "name", "send" }, { "type", "submit" }, { "data", new HtmlOptions { { "action", "composer#submit" } } } }, () => w.WriteLiteral("Send"u8)));
            w.Append(form.Button());
        })),
        ["form_with_fields_for"] = (v, w) => w.Append(v.FormWith(new FormModel(new("Account", 1), isPersisted: true), "/account", new()
        {
            { "method", "put" }, { "data", new HtmlOptions { { "controller", "form" } } }, { "class", "flex" },
        }, form => w.Append(form.FieldsFor("settings", null, settings =>
            w.Append(settings.HiddenField("restrict_room_creation_to_administrators", new() { { "value", false } })))))),
        ["form_with_scope"] = (v, w) => w.Append(v.FormWith(null, "/searches", new()
        {
            { "class", "s" }, { "html", new HtmlOptions { { "role", "search" } } },
        }, form => w.Append(form.TextField("q", new() { { "value", "a b" }, { "role", "searchbox" }, { "aria", new HtmlOptions { { "label", "search" } } } })), scope: "search")),
        ["form_with_get"] = (v, w) => w.Append(v.FormWith(null, "/searches", new() { { "method", "get" } }, form => w.Append(form.TextField("q")))),
        ["form_with_no_block"] = (v, w) => w.Append(v.FormWith(null, "/rooms/7/messages/11", new()
        {
            { "method", "delete" }, { "id", "delete_form" }, { "data", new HtmlOptions { { "turbo_frame", "edit" } } },
        })),
        ["form_with_current_url"] = (v, w) => w.Append(v.FormWith(options: new() { { "method", "put" } })),
        ["form_with_options"] = (v, w) =>
        {
            w.Append(v.FormWith(null, "/x", new() { { "authenticity_token", false }, { "local", false }, { "multipart", true }, { "skip_enforcing_utf8", false } }, form => w.WriteLiteral("x"u8)));
            w.WriteLiteral("|"u8);
            w.Append(v.FormWith(null, "/y", new() { { "authenticity_token", "given" }, { "aria", new HtmlOptions { { "label", "dropped" } } } }, form => w.WriteLiteral("y"u8)));
        },
        ["form_with_persisted_method_override"] = (v, w) => w.Append(v.FormWith(
            new FormModel(Room, isPersisted: true, new Dictionary<string, object?> { ["name"] = "Lobby <1>" }), "/rooms/opens/7", new() { { "method", "post" } },
            form => w.Append(form.TextField("name")))),

        // turbo-rails
        ["turbo_frame_tag"] = (v, w) =>
        {
            w.Append(View.TurboFrameTag("x"));
            w.WriteLiteral("|"u8);
            w.Append(View.TurboFrameTag("next_page_container", new() { { "loading", "lazy" } }, "/accounts/users?page=2", "_top"));
            w.WriteLiteral("|"u8);
            w.Append(View.TurboFrameTag("f", src: ""));
        },
        ["turbo_frame_tag_record"] = (v, w) =>
        {
            w.Append(View.TurboFrameTag(RecordIdentifier.DomId(Message, "boosting"), null, () => w.WriteLiteral("b"u8)));
            w.WriteLiteral("|"u8);
            w.Append(View.TurboFrameTag(RecordIdentifier.DomId(Room, "involvement"), new()
            {
                { "id", "ignored" }, { "data", new HtmlOptions { { "controller", "turbo-frame" } } },
            }, () => w.WriteLiteral("i"u8)));
        },
        ["turbo_stream_from"] = (v, w) =>
        {
            w.Append(v.TurboStreamFrom(["rooms"]));
            w.WriteLiteral("|"u8);
            w.Append(v.TurboStreamFrom([RecordIdentifier.GidParam("User", 3), "rooms"]));
            w.WriteLiteral("|"u8);
            w.Append(v.TurboStreamFrom([RecordIdentifier.GidParam("Rooms::Open", 7), "messages"], "RoomMessagesChannel"));
            w.WriteLiteral("|"u8);
            w.Append(v.TurboStreamFrom(["a", ""], attributes: new() { { "data", new HtmlOptions { { "x", 1 } } } }));
        },
        ["turbo_stream_actions"] = (v, w) =>
        {
            w.Append(TurboStreamTags.Append("messages", "<b>raw</b>"));
            w.WriteLiteral("|"u8);
            w.Append(TurboStreamTags.Prepend("m", new SafeString("<i>")));
            w.WriteLiteral("|"u8);
            w.Append(TurboStreamTags.Remove("message_a1"));
            w.WriteLiteral("|"u8);
            w.Append(TurboStreamTags.Update("u", "x", "morph"));
            w.WriteLiteral("|"u8);
            w.Append(TurboStreamTags.Replace("r", "y"));
            w.WriteLiteral("|"u8);
            w.Append(TurboStreamTags.Before("b", "1"));
            w.WriteLiteral("|"u8);
            w.Append(TurboStreamTags.After("a", "2"));
        },
        ["turbo_stream_block"] = (v, w) =>
        {
            w.Append(TurboStreamTags.Append(RecordIdentifier.DomId(Room, "messages"), () => w.WriteLiteral("<div>new</div>"u8)));
            w.Append(TurboStreamTags.Replace("r", () => w.WriteLiteral("r"u8), "morph"));
        },
        ["turbo_stream_record_target"] = (v, w) => w.Append(TurboStreamTags.Remove(RecordIdentifier.DomId(Message))),

        // The app's helpers
        ["page_title_tag"] = (v, w) =>
        {
            w.Append(v.PageTitleTag());
            w.WriteLiteral("|"u8);
            v.PageTitle = "Rooms & <more>";
            w.Append(v.PageTitleTag());
        },
        ["current_user_meta_tags"] = (v, w) =>
        {
            w.Append(Nil(v.CurrentUserMetaTags()));
            w.WriteLiteral("|"u8);
            w.Append(With(v, user: new CurrentUser(3, "Kevin & Co", true)).CurrentUserMetaTags());
        },
        ["custom_styles_tag"] = (v, w) =>
        {
            w.Append(Nil(v.CustomStylesTag()));
            w.WriteLiteral("|"u8);
            w.Append(With(v, account: new CurrentAccount("body { color: red } </style><x>", false, null)).CustomStylesTag());
            w.WriteLiteral("|"u8);
            w.Append(With(v, account: new CurrentAccount("", false, null)).CustomStylesTag());
        },
        ["body_classes"] = (v, w) =>
        {
            w.WriteLiteral("["u8);
            w.Append(v.BodyClasses());
            w.WriteLiteral("]|"u8);
            var admin = With(v, new CurrentUser(1, "", CanAdminister: true), new CurrentAccount(null, LogoAttached: true, null));
            admin.BodyClass = "sidebar";
            w.WriteLiteral("["u8);
            w.Append(admin.BodyClasses());
            w.WriteLiteral("]"u8);
            var member = With(v, new CurrentUser(2, "", CanAdminister: false), new CurrentAccount(null, LogoAttached: false, null));
            member.BodyClass = "sidebar";
            w.WriteLiteral("["u8);
            w.Append(member.BodyClasses());
            w.WriteLiteral("]"u8);
        },
        ["link_back_to"] = (v, w) => w.Append(v.LinkBackTo("/rooms/7")),
        ["account_logo_tag"] = (v, w) =>
        {
            var withAccount = With(v, account: new CurrentAccount(null, false, Later));
            w.Append(withAccount.AccountLogoTag());
            w.WriteLiteral("|"u8);
            w.Append(withAccount.AccountLogoTag("avatar--large"));
            w.WriteLiteral("|"u8);
            w.Append(v.AccountLogoTag());
        },
        ["script_aware_action_cable_meta_tag"] = (v, w) => w.Append(View.ScriptAwareActionCableMetaTag()),
        ["version_badge"] = (v, w) => w.Append(v.VersionBadge()),
        ["broadcast_image_tag"] = (v, w) =>
        {
            w.Append(v.BroadcastImageTag("common-file-text.svg", new() { { "size", 22 }, { "class", "colorize--black" }, { "aria", new HtmlOptions { { "hidden", "true" } } } }));
            w.WriteLiteral("|"u8);
            w.Append(v.BroadcastImageTag("/rails/active_storage/representations/x.png", new() { { "width", 10 }, { "height", 5 }, { "class", "message__attachment" }, { "loading", "lazy" } }));
        },
        ["button_to_copy_to_clipboard"] = (v, w) => w.Append(View.ButtonToCopyToClipboard("https://campfire.test/join/a&b", () => w.WriteLiteral("Copy"u8))),
        ["drop_target_actions"] = (v, w) => w.Append(View.DropTargetActions()),
        ["auto_submit_form_with"] = (v, w) =>
        {
            w.Append(v.AutoSubmitFormWith(new HtmlOptions { { "method", "put" } }));
            w.WriteLiteral("|"u8);
            w.Append(v.AutoSubmitFormWith(new HtmlOptions { { "data", new HtmlOptions { { "controller", "other" }, { "y", 1 } } } }, form => w.WriteLiteral("x"u8), url: "/x"));
        },
        ["link_to_zoom_qr_code"] = (v, w) => w.Append(View.LinkToZoomQrCode("https://campfire.test/join/abc?x=1", () => w.WriteLiteral("Zoom"u8))),
        ["rich_text_data_actions"] = (v, w) => w.Append(View.RichTextDataActions()),
        ["mention_prompt_tag"] = (v, w) => w.Append(View.MentionPromptTag(7)),
        ["search_results_tag"] = (v, w) => w.Append(View.SearchResultsTag(() => w.WriteLiteral("r"u8))),
        ["local_datetime_tag"] = (v, w) =>
        {
            w.Append(View.LocalDatetimeTag(Time));
            w.WriteLiteral("|"u8);
            w.Append(View.LocalDatetimeTag(Time, "datetime", new() { { "class", "x" } }));
            w.WriteLiteral("|"u8);
            w.Append(View.MessageTimestamp(Time, new() { { "class", "message__timestamp" } }));
        },
        ["translations_for"] = (v, w) => w.Append(View.TranslationsFor("email_address")),
        ["translation_button"] = (v, w) => w.Append(v.TranslationButton("invite_message")),
        ["avatar_tag"] = (v, w) =>
        {
            w.Append(v.AvatarTag(AvatarUser));
            w.WriteLiteral("|"u8);
            w.Append(v.AvatarTag(AvatarUser, new() { { "size", 20 }, { "class", "avatar--small" }, { "loading", "lazy" } }));
        },
        ["button_to_direct_room_with"] = (v, w) => w.Append(v.ButtonToDirectRoomWith(3)),
        ["user_filter_menu_tag"] = (v, w) => w.Append(View.UserFilterMenuTag(() => w.WriteLiteral("m"u8))),
        ["user_filter_search_tag"] = (v, w) => w.Append(View.UserFilterSearchTag()),
        ["profile_form_with"] = (v, w) => w.Append(v.ProfileFormWith(UserModel, new() { { "class", "txt-medium" } }, form => w.Append(form.TextField("name")))),
        ["profile_form_submit_button"] = (v, w) => w.Append(v.ProfileFormSubmitButton()),
        ["web_share_session_button"] = (v, w) => w.Append(View.WebShareSessionButton("https://campfire.test/session/transfers/x", "Link & go", "Text <t>", () => w.WriteLiteral("Share"u8))),
        ["sidebar_turbo_frame_tag"] = (v, w) =>
        {
            w.Append(View.SidebarTurboFrameTag("/users/me/sidebar"));
            w.WriteLiteral("|"u8);
            w.Append(View.SidebarTurboFrameTag(null, () => w.WriteLiteral("s"u8)));
        },
        ["link_to_room"] = (v, w) =>
        {
            w.Append(View.LinkToRoom(7, null, () => w.WriteLiteral("r"u8)));
            w.WriteLiteral("|"u8);
            w.Append(View.LinkToRoom(9, new()
            {
                { "class", new object[] { "direct", new HtmlOptions { { "unread", true } } } },
                { "id", "list_rooms_direct_9" },
                { "data", new HtmlOptions { { "extra", "x" }, { "room_id", 99 } } },
            }, () => w.WriteLiteral("d"u8)));
        },
        ["link_to_edit_room"] = (v, w) => w.Append(View.LinkToEditRoom(7, Routes.EditRoomsOpenPath(7), () => w.WriteLiteral("e"u8))),
        ["link_back_to_last_room_visited"] = (v, w) =>
        {
            w.Append(v.LinkBackToLastRoomVisited(null));
            w.WriteLiteral("|"u8);
            w.Append(v.LinkBackToLastRoomVisited(7));
        },
        ["button_to_delete_room"] = (v, w) =>
        {
            w.Append(v.ButtonToDeleteRoom(7, "Lobby <1>", "Lobby <1>"));
            w.WriteLiteral("|"u8);
            w.Append(v.ButtonToDeleteRoom(9, "Ping", "Ping", "/rooms/directs/9"));
        },
        ["button_to_jump_to_newest_message"] = (v, w) => w.Append(v.ButtonToJumpToNewestMessage()),
        ["submit_room_button_tag"] = (v, w) => w.Append(v.SubmitRoomButtonTag()),
        ["composer_form_tag"] = (v, w) => w.Append(v.ComposerFormTag(7, form => w.Append(form.HiddenField("client_message_id")))),
        ["turbo_frame_for_involvement_tag"] = (v, w) => w.Append(View.TurboFrameForInvolvementTag(Room, 7, () => w.WriteLiteral("i"u8))),
        ["button_to_change_involvement"] = (v, w) =>
        {
            w.Append(v.ButtonToChangeInvolvement(Room, 7, false, "mentions"));
            w.WriteLiteral("|"u8);
            w.Append(v.ButtonToChangeInvolvement(Room, 7, false, "invisible"));
            w.WriteLiteral("|"u8);
            w.Append(v.ButtonToChangeInvolvement(Direct, 9, true, "everything"));
            w.WriteLiteral("|"u8);
            w.Append(v.ButtonToChangeInvolvement(Direct, 9, true, "nothing"));
        },
        ["message_area_tag"] = (v, w) => w.Append(v.MessageAreaTag(7, () => w.WriteLiteral("a"u8))),
        ["messages_tag"] = (v, w) => w.Append(v.MessagesTag(Room, 7, Time, () => w.WriteLiteral("m"u8))),
        ["message_tag"] = (v, w) =>
        {
            w.Append(View.MessageTag(new MessageTagInfo(11, "a1b2-c3", 3, Time, Later, AllEmoji: false), () => w.WriteLiteral("m"u8)));
            w.WriteLiteral("|"u8);
            w.Append(View.MessageTag(new MessageTagInfo(11, "a1b2-c3", 3, Time, Later, AllEmoji: true), () => w.WriteLiteral("e"u8)));
        },
        ["link_back_referrer"] = (v, w) => w.Append(With(v, referrer: "https://campfire.test/rooms/1").LinkBack()),
        ["link_back_same_page"] = (v, w) => w.Append(With(v, referrer: "https://campfire.test/session/transfers/abc").LinkBack()),
        ["link_back_no_referrer"] = (v, w) => w.Append(v.LinkBack()),
    };

    public static TheoryData<string> CaseNames => [.. Cases.Keys];

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Helpers_render_what_the_reference_renders(string name)
    {
        var expected = Golden.GetProperty("cases").GetProperty(name).GetString();
        var view = NewView();
        Assert.Equal(expected, Render(w => Cases[name](view, w)));
    }

    [Fact]
    public void Every_golden_case_has_a_csharp_case()
    {
        var golden = Golden.GetProperty("cases").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal);
        Assert.Equal(golden, Cases.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Reactions_are_the_references()
    {
        var expected = Golden.GetProperty("reactions").EnumerateArray().Select(pair => KeyValuePair.Create(pair[0].GetString()!, pair[1].GetString()!));
        Assert.Equal(expected, View.Reactions);
    }

    [Fact]
    public void Translations_are_the_references()
    {
        var golden = Golden.GetProperty("translations");
        Assert.Equal(golden.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal), View.Translations.Keys.Order(StringComparer.Ordinal));
        foreach (var property in golden.EnumerateObject())
        {
            var expected = property.Value.EnumerateArray().Select(pair => KeyValuePair.Create(pair[0].GetString()!, pair[1].GetString()!));
            Assert.Equal(expected, View.Translations[property.Name]);
        }
    }

    [Fact]
    public void Avatar_colors_are_the_references()
    {
        foreach (var property in Golden.GetProperty("avatar_colors").EnumerateObject())
        {
            Assert.Equal(property.Value.GetString(), View.AvatarBackgroundColor(property.Name));
        }
    }

    internal static View NewView(string? referrer = null) => new()
    {
        Assets = ReferenceAssets.Bundle,
        Origin = new UrlBase("https", "campfire.test"),
        RequestPath = "/session/transfers/abc",
        RequestUrl = "https://campfire.test/session/transfers/abc",
        CurrentPath = "/session/transfers/abc",
        Referrer = referrer,
        // The same stand-in generate.rb gives Rails for the random masked token.
        FormAuthenticityToken = (action, method) => $"token({action}|{method})",
        StreamKeys = new KeyGenerator(secretKeyBase),
    };

    internal static string Render(Action<HtmlWriter> render)
    {
        var buffer = new ArrayBufferWriter<byte>();
        render(new HtmlWriter(buffer));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static JsonElement LoadGolden()
    {
        var path = Path.Combine(ReferenceAssets.RepositoryRoot, "tests", "Campfire.Web.Tests", "Helpers", "Vectors", "helpers.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    // A view like this one with the given Current and request state.
    static View With(View view, CurrentUser? user = null, CurrentAccount? account = null, string? referrer = null) => new()
    {
        Assets = view.Assets,
        Origin = view.Origin,
        RequestPath = view.RequestPath,
        RequestUrl = view.RequestUrl,
        CurrentPath = view.CurrentPath,
        Referrer = referrer ?? view.Referrer,
        FormAuthenticityToken = view.FormAuthenticityToken,
        StreamKeys = view.StreamKeys,
        CurrentUser = user,
        CurrentAccount = account,
        Flash = Flash.FromSessionValue(null),
    };

    // Ruby's nil.inspect for a helper that returned nothing.
    static SafeString Nil(SafeString value) => value.Value.Length == 0 ? new SafeString("nil") : value;
}

# Helpers

Rails' view helpers and the app's own (`reference/app/helpers`), checked byte for byte against
what the reference renders (`tests/Campfire.Web.Tests/Helpers/`).

## The view

`View` is the per-request view context, like the `ActionView::Base` instance Rails builds. Helpers
are its methods, and templates are partial instance methods declared in your own file:

```csharp
namespace Campfire.Web.Helpers;

public partial class View
{
    [ErbTemplate("rooms/show.html.erb.cs")]
    public partial void RoomsShow(HtmlWriter w, Room room);
}
```

A page, its partials and the layout then share one `content_for` store. Render the page first,
then `ApplicationLayout(w, page)` with the page's output. The controller builds the `View` with
the request state the helpers read: `Assets`, `Origin`, `RequestPath`, `RequestUrl`, `Referrer`,
`CurrentPath` (`url_for({})`), `FormAuthenticityToken` (`CsrfTokens.For(session token, request
path)`), `StreamKeys`, `Flash`, `CurrentUser`, `CurrentAccount`, `VapidPublicKey` and `AppVersion`.
After rendering, set `View.LinkHeader` (the stylesheet preload links) as the `link` response header.

`Campfire.Templates` and `Campfire.Web.Helpers.Rails` are global usings in this project.

## Translating helper calls

| Ruby                                                  | C#                                                                 |
| ----------------------------------------------------- | ------------------------------------------------------------------ |
| `class: "btn", data: { turbo_frame: "_top" }`          | `new() { { "class", "btn" }, { "data", new HtmlOptions { { "turbo_frame", "_top" } } } }` |
| `tag.div "x", class: "c"`                             | `Tag.Div("x", new() { { "class", "c" } })`                          |
| `tag.div class: "c"` (no content)                     | `Tag.Div(options: new() { { "class", "c" } })`                      |
| `tag.div class: "c" do %>…<% end`                     | `Tag.Div(new() { { "class", "c" } }, () => { %>…<% })`              |
| `tag.turbo_frame`, `tag.lexxy_prompt`                 | `Tag.Element("turbo_frame", …)`                                    |
| `tag(:meta, …)` (legacy, ends in ` />`)               | `TagHelper.Tag("meta", …)`                                         |
| `link_to name, url, …` / with a block                 | `LinkTo(name, url, …)` / `LinkTo(url, options, () => { … })`       |
| `button_to name, url, method: :delete`                | `ButtonTo(name, url, new() { { "method", "delete" } })`            |
| `image_tag "x.svg", size: 20`                         | `ImageTag("x.svg", new() { { "size", 20 } })`                      |
| `form_with model: @user, url: x do \|form\|`          | `FormWith(new FormModel(new("User", id), true, attributes), x, options, form => { … })` |
| `form.text_field :name, …`                            | `form.TextField("name", …)`                                        |
| `turbo_frame_tag dom_id(room, :x)`                    | `TurboFrameTag(RecordIdentifier.DomId(new("Rooms::Open", id), "x"))` |
| `turbo_stream_from @room, :messages`                  | `TurboStreamFrom([RecordIdentifier.GidParam("Rooms::Open", id), "messages"])` |
| `turbo_stream.append target, content`                 | `TurboStreamTags.Append(target, content)`                          |
| `content_for :nav do %>…<% end`                       | `ContentFor(w, "nav", () => { %>…<% })`                            |
| `render "layouts/lightbox"`                           | `Render(Lightbox)`                                                 |

Option values are what Ruby would pass: `string` is escaped, `SafeString` (`html_safe`) isn't,
`bool`, `int`, `long`, `double`, nested `HtmlOptions` and arrays. Anything else throws rather than
render differently from Ruby. Keys keep their order, which is the attribute order Rails writes.

Helpers that read a record take what they read (`RecordKey` for `dom_id`, `FormModel` for forms,
`AvatarUser`, `MessageTagInfo`, ids), so they work with any record type. URLs are strings from the
typed `Routes` helpers; `polymorphic_path` isn't modeled, so pass the URL a model form posts to.

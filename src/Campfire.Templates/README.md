# Campfire.Templates

ERB templates with C# code, compiled at build time by `Campfire.Templates.Generator` with
Erubi's exact trim rules, so a translated view renders the same bytes as the Rails one.

## Translating a view

Copy `reference/app/views/<path>.html.erb` to `Views/<path>.html.erb.cs` and change only the code
inside the tags. The markup, indentation and line endings stay as they are; the corpus tests
compare a translation's text against its source the same way.

| Ruby ERB                                   | C# ERB                                          |
| ------------------------------------------ | ----------------------------------------------- |
| `<%= x %>` (escaped unless `html_safe`)    | `<%= x %>`: `string` is escaped, `SafeString` and `IHtml` are not |
| `<%== x %>` / `<%= raw x %>`               | `<%== x %>`                                     |
| `<% if a %>` … `<% else %>` … `<% end %>`  | `<% if (a) { %>` … `<% } else { %>` … `<% } %>` |
| `<% xs.each do \|x\| %>` … `<% end %>`     | `<% foreach (var x in xs) { %>` … `<% } %>`     |
| `<%= helper(a) do %>` … `<% end %>`        | `<%= Helper(a, () => { %>` … `<% }) %>`         |
| `<%= helper do \|f\| %>` … `<% end %>`     | `<%= Helper(f => { %>` … `<% }) %>`             |
| `<%# comment %>`, `<%%`, `-%>`, `<%-`      | unchanged                                       |

An expression tag that leaves a bracket open starts a block expression; the code tag that closes it
again ends it. Block helpers take an `Action` (or `Action<T>`) and return an `IHtml` that writes
its markup around the block, or call `HtmlWriter.Capture` when they need the block's output as a
`SafeString`. `nil` is `null` and appends nothing. `<%= %>` accepts `string`, `SafeString`,
`IHtml`, `int`, `long` and `bool`; format anything else explicitly the way Ruby's `to_s` does.

## Declaring the render method

```csharp
using Campfire.Templates;

static partial class RoomViews
{
    [ErbTemplate("rooms/show.html.erb.cs")]
    public static partial void Show(HtmlWriter w, Room room, User user);
}
```

The method must be `partial`, return `void` and take exactly one `HtmlWriter`. Its other
parameters, the members of its class and the `using` directives of its file are in scope for the
template's code. The path matches the end of an `AdditionalFiles` path on whole directory names.
Errors in template code are reported at the template's line.

The project compiling templates needs:

```xml
<ItemGroup>
  <Compile Remove="Views/**/*.erb.cs" />
  <AdditionalFiles Include="Views/**/*.erb.cs" />
  <ProjectReference Include="../Campfire.Templates/Campfire.Templates.csproj" />
  <ProjectReference Include="../Campfire.Templates.Generator/Campfire.Templates.Generator.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
```

## Rails behaviors kept on purpose

- Trimming is `Erubi::Engine` 1.13.1 with `trim: true`, as ActionView configures it: a line holding
  only a `<% %>` or `<%# %>` tag disappears with its indentation and newline; `<%= %>` keeps both,
  and `-%>`/`=%>` on an expression drop the newline after it.
- CRLF in template text renders as LF, because Ruby's parser reads CRLF in the compiled string
  literals as LF. A lone CR is kept.
- A leading `<%# encoding: ... %>` comment is stripped, as `ActionView::Template::Handlers::ERB`
  does.

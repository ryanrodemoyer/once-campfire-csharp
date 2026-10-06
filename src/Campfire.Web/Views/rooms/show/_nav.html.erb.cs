<% ContentFor(w, "nav", () => { %>
  <%= CurrentAccount!.LogoAttached ? AccountLogoTag() : SafeString.Empty %>

  <%= Tag.Span(new() { { "class", "btn btn--reversed btn--faux room--current" } }, () => { %>
    <h1 class="room__contents txt-medium overflow-ellipsis">
      <% if (page.Room.IsDirect) { %>
        <span class="for-screen-reader">Ping with</span>
      <% } %>

      <%= page.DisplayName %>
    </h1>
  <% }) %>

  <%= LinkToEditRoom(page.Room.Id, page.EditPath, () => { %>
    <%= ImageTag("menu-dots-horizontal.svg", new() { { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
    <span class="for-screen-reader">Settings for this <%= page.Room.IsDirect ? "Ping" : "room" %></span>
  <% }) %>

  <%= Render(o => RoomsInvolvementsBell(o, page.Room, page.Platform)) %>
<% }); %>

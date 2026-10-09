<% PageTitle = "Search"; %>
<% BodyClass = "sidebar searches"; %>

<% ContentFor(w, "nav", () => { %>
  <% if (page.Query is not null) { %>
    <div class="searches__query flex align-center gap pad-block-start-half">
      <%= Tag.Div(new() { { "class", "btn btn--reversed btn--faux align-center gap txt-nowrap" } }, () => { %>
        <span class="overflow-ellipsis">“<%= page.Query %>”</span>
        <span class="flex-item-no-shrink"><%= page.Messages.Count %></span>
      <% }) %>
    </div>
  <% } %>

  <div class="searches__recents align-center gap pad-block-half overflow-y overflow-hide-scrollbar">
    <% foreach (var search in page.RecentSearches) { %>
      <%= LinkTo(Routes.SearchesPath(new() { { "q", search.Query } }), new() { { "class", "align-center gap room btn txt-nowrap" } }, () => { %>
        <span class="overflow-ellipsis">“<%= search.Query %>”</span>
      <% }) %>
    <% } %>

    <% if (page.RecentSearches.Count > 0) { %>
      <%= ButtonTo(Routes.ClearSearchesUrl(Origin), new() { { "method", "delete" }, { "class", "btn searches__btn" },
              { "data", new HtmlOptions { { "turbo_confirm", "Are you sure you want to clear your recent searches?" } } } }, () => { %>
        <%= ImageTag("broom.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
        <span class="for-screen-reader">Clear recent searches</span>
      <% }) %>
    <% } %>
  </div>
<% }); %>

<% ContentFor(w, "sidebar", () => { %>
  <div class="rooms position-relative flex flex-column gap overflow-y overflow-hide-scrollbar">
    <% foreach (var search in page.RecentSearches) { %>
      <%= LinkTo(Routes.SearchesPath(new() { { "q", search.Query } }), new() { { "class", "align-center gap room btn txt-nowrap" } }, () => { %>
        <span class="overflow-ellipsis">“<%= search.Query %>”</span>
      <% }) %>
    <% } %>

    <% if (page.RecentSearches.Count > 0) { %>
      <%= ButtonTo(Routes.ClearSearchesUrl(Origin), new() { { "method", "delete" }, { "class", "btn searches__btn" },
              { "data", new HtmlOptions { { "turbo_confirm", "Are you sure you want to clear your recent searches?" } } } }, () => { %>
        <%= ImageTag("broom.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
        <span class="for-screen-reader">Clear recent searches</span>
      <% }) %>
    <% } %>
  </div>
<% }); %>

<div id="message-area" class="message-area">
  <div class="message-area--empty min-width center">
    <figure class="center pad">
      <%= ImageTag("search.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "class", "colorize--black translucent" } }) %>
    </figure>
  </div>

  <%= SearchResultsTag(w, () => { %>
    <%= Render(o => { foreach (var message in page.Messages) { MessagesMessage(o, message); } }) %>
  <% }) %>
</div>

<% ContentFor(w, "footer", () => { %>
  <div class="composer flex align-end gap">
    <%= LinkTo(page.ReturnToRoom is { } returnToRoom ? Routes.RoomPath(returnToRoom.Id) : Routes.RootPath(),
            new() { { "class", "btn flex-item-no-shrink margin-block-end" }, { "style", "view-transition-name: input-switcher; --btn-border-radius: 0.5em" } }, () => { %>
      <%= ImageTag("arrow-left.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
      <span class="for-screen-reader">Exit search </span>
    <% }) %>

    <%= FormWith(null, Routes.SearchesPath(), new() { { "class", "margin-block flex-item-grow contain flex align-center gap" },
          { "data", new HtmlOptions { { "controller", "form" }, { "action", "keydown.esc->form#cancel" } } } }, form => { %>
      <div class="composer__input flex align-center flex-item-grow gap full-width input input--actor min-width">
        <%= ImageTag("search.svg", new() { { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } }, { "class", "composer__input-hint colorize--black" }, { "style", "view-transition-name: input-btn;" } }) %>

        <%= form.TextField("q", new() { { "value", page.RawQuery }, { "class", "searches__input input flex-item-grow" }, { "role", "searchbox" }, { "aria", new HtmlOptions { { "label", "search" } } }, { "autofocus", true }, { "required", true } }) %>

        <%= LinkTo(Routes.SearchesPath(), new() { { "data", new HtmlOptions { { "form_target", "cancel" } } }, { "role", "button" }, { "class", "searches__reset" } }, () => { %>
          <%= ImageTag("remove.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 14 }, { "class", "colorize--black" } }) %>
          <span class="for-screen-reader">Clear search field</span>
        <% }) %>

        <%= form.Button(new() { { "type", "submit" }, { "class", "btn btn--reversed flex-item-no-shrink txt-small" }, { "style", "--btn-border-radius: 0.5em" } }, () => { %>
          <%= ImageTag("arrow-up.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
          <span class="for-screen-reader">Search</span>
        <% }) %>
      </div>
    <% }) %>
  </div>
<% }); %>

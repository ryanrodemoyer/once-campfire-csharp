<% PageTitle = $"Edit settings for {room.DisplayName}"; %>

<% ContentFor(w, "nav", () => { %>
  <div class="flex-item-justify-start">
    <%= LinkBackToLastRoomVisited(room.LastRoomId) %>
  </div>
<% }); %>

<div class="panel txt-align-center">
  <section class="directs--edit margin-block-end">
    <% var users = room.Users; %>
    <% foreach (var user in users) { %>
      <div class="member flex flex-column gap fill-shade pad border-radius">
        <figure class="avatar center" style="--avatar-border-radius: 10ch; --avatar-size: 10ch;" >
          <%= AvatarTag(user.Avatar, new() { { "loading", "lazy" } }) %>
        </figure>

        <strong><%= user.Name %></strong>
      </div>
    <% } %>
  </section>

  <%= ButtonTo(Routes.RoomsDirectUrl(Origin, room.Id), new() { { "method", "delete" }, { "class", "btn btn--negative center" }, { "aria", new HtmlOptions { { "label", "Delete Ping" } } },
      { "data", new HtmlOptions { { "turbo_confirm", "Are you sure you want to delete this ping and all messages in it? This can’t be undone." } } } }, () => { %>
    <%= ImageTag("trash.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
    Ping
  <% }) %>
</div>

<li class="flex align-center gap margin-none" data-value="<%= user.Name.ToLowerInvariant() %>">
  <figure class="avatar flex-item-no-shrink" style="--avatar-size: 4ch;">
    <%= AvatarTag(user.Avatar, new() { { "loading", "lazy" } }) %>
  </figure>

  <div class="min-width">
    <div class="overflow-ellipsis fill-shade"><strong><%= user.Name %></strong></div>
  </div>

  <hr class="separator" aria-hidden="true">

  <% if (room.CanAdminister) { %>
    <%= ImageTag("check.svg", new() { { "size", 20 }, { "class", "colorize--black flex-item-no-shrink" }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
  <% } %>
</li>

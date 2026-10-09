<%= ButtonTo(Routes.RoomsDirectsPath(new() { { "user_ids", new[] { user.Id } } }), new() { { "class", "direct borderless fill-transparent unpad" } }, () => { %>
  <span class="avatar">
    <%= ImageTag(user.AvatarPath, new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
  </span>

  <span class="direct__author flex align-center gap max-width min-width border-radius txt-small">
    <span class="txt-nowrap overflow-ellipsis">
      <span class="for-screen-reader">Start a ping with</span>
      <%= FirstName(user.Name) %>
    </span>
  </span>
<% }) %>

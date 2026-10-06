<% if (user.IsActive) { %>
  <%= ButtonTo(Routes.UserBanPath(user.Id), new() { { "method", "post" },
        { "class", "btn full-width" },
        { "data", new HtmlOptions { { "turbo_confirm", "Are you sure you want to ban this user? This will log them out, delete their messages, and block their IP addresses." } } } }, () => { %>
    <%= ImageTag("cancel.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" }, { "label", $"Ban {user.Name}" } } } }) %>
    <span>Ban <%= user.Name %></span>
  <% }) %>
<% } else { %>
  <%= ButtonTo(Routes.UserBanPath(user.Id), new() { { "method", "delete" },
        { "class", "btn btn--negative full-width" },
        { "data", new HtmlOptions { { "turbo_confirm", "Are you sure you want to remove the ban on this user?" } } } }, () => { %>
    <%= ImageTag("cancel.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" }, { "label", $"Remove Ban {user.Name}" } } } }) %>
    <span>Remove ban</span>
  <% }) %>
<% } %>

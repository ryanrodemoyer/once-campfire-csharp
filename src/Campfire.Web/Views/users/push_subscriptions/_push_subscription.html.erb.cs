<li class="flex flex-column margin-none membership-item">
  <span class="overflow-ellipsis txt-primary txt-undecorated">
    <% var agent = Campfire.RailsCompat.UserAgent.ParsedUserAgent.Parse(pushSubscription.UserAgent); %>
    <strong><%= agent.Browser() %> <%= agent.Version()?.Text %> on <%= agent.Platform() %></strong><br>
  </span>

  <span class="flex align-start gap txt-small">
    <span><%= pushSubscription.Endpoint %></span>

    <span class="flex align-center gap">
      <%= ButtonTo(Routes.UserPushSubscriptionTestNotificationsPath(pushSubscription.Id), new() { { "class", "btn btn--reversed" } }, () => { %>
        <%= ImageTag("notification-bell-everything.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
        <span class="for-screen-reader">Send test notification</span>
      <% }) %>

      <%= ButtonTo(Routes.UserPushSubscriptionPath(pushSubscription.Id), new() { { "method", "delete" }, { "class", "btn btn--negative" } }, () => { %>
        <%= ImageTag("minus.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
        <span class="for-screen-reader">Delete subscription</span>
      <% }) %>
    </span>
  </span>
</li>

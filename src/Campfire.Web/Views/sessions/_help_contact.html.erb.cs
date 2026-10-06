<% if (helpContact is { } owner) { %>
  <div class="txt-align-center margin-block-double full-width">
    <%= LinkTo($"mailto:\"{owner.Name}\" <{owner.EmailAddress}>", new() { { "class", "btn center" }, { "title", $"Email {owner.Name}" } }, () => { %>
      <%= ImageTag("lifebuoy.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
      <span><%= owner.EmailAddress %></span>
    <% }) %>

    <div class="txt-align-center center margin-block txt-subtle">Campfire&trade; version <%= VersionBadge() %></div>
  </div>
<% } %>

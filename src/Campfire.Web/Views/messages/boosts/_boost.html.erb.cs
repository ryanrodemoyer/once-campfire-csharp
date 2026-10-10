<% FragmentCache(w, boost.Id, boost.CreatedAt, () => { %>
  <div id="<%= RecordIdentifier.DomId(boost.Record) %>"
      class="boost boost-item flex-inline postion--relative max-width align-center fill-white gap"
      data-controller="boost-delete" data-boost-delete-perform-class="boost--deleting" data-boost-delete-reveal-class="expanded" data-boost-delete-booster-id-value="<%= boost.Booster!.Id %>">
    <figure class="avatar boost__avatar flex-item-no-shrink">
      <%= AvatarTag(boost.Booster!.Avatar, new() { { "aria", new HtmlOptions { { "label", $"{boost.Booster!.Name} boosted {boost.Content}" } } } }) %>
    </figure>

    <%= Tag.Span(boost.Content, new() { { "role", "button" },
          { "class", new object?[] { "txt-small", new HtmlOptions { { "txt-medium", Campfire.RailsCompat.Formatting.StringExtensions.AllEmoji(boost.Content) } } } },
          { "data", new HtmlOptions { { "action", "click->boost-delete#reveal keydown.enter->boost-delete#reveal:prevent" }, { "boost_delete_target", "content" } } } }) %>

    <%= ButtonTo(Routes.MessageBoostPath(boost.MessageId, boost.Id), new() { { "method", "delete" }, { "data", new HtmlOptions { { "action", "boost-delete#perform" }, { "boost_delete_target", "button" } } },
          { "class", "btn btn--negative flex-item-justify-end boost__delete" } }, () => { %>
      <%= ImageTag("minus.svg", new() { { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
      <span class="for-screen-reader">Delete this boost</span>
    <% }) %>
  </div>
  <span id="delete_boost_accessible_label" class="for-screen-reader">Press enter to delete this boost</span>
<% }); %>

<%= TurboFrameTag(RecordIdentifier.DomId(message.Record, "boosting"), null, () => { %>
  <div class="boosts flex flex-wrap align-center gap full-width" style="--column-gap: 0.4ch; --row-gap: 0"
      data-controller="turbo-streaming" data-action="turbo:submit-start->turbo-streaming#unsubscribe">
    <div class="flex-inline flex-wrap gap" id="<%= RecordIdentifier.DomId(message.Record, "boosts") %>" data-turbo-streaming-target="container">
      <%= Render(o => { foreach (var boost in message.Boosts) { MessagesBoostsBoost(o, boost); } }) %>
    </div>

    <%= TurboFrameTag(RecordIdentifier.DomId(message.Record, "new_boost"), null, () => { %>
      <div class="flex-inline message__boost-inline" data-controller="soft-keyboard">
        <%= LinkTo(Routes.NewMessageBoostPath(message.Id), new() { { "class", "boost__action txt-small btn" }, { "action", "soft-keyboard#open" } }, () => { %>
          <%= ImageTag("boost.svg", new() { { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
          <span class="for-screen-reader">Add a boost</span>
        <% }) %>
      </div>
    <% }) %>
  </div>
<% }) %>

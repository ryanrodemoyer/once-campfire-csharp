  <% var members = direct.Members; %>

  <%= LinkToRoom(direct.RoomId, new() { { "class", direct.Unread ? "direct unread" : "direct" }, { "id", RecordIdentifier.DomId(new RecordKey(RoomTypes.DirectClassName, direct.RoomId), "list") },
        { "data", new HtmlOptions { { "sorted_list_number", Campfire.RailsCompat.Formatting.TimeFormats.ToFsEpoch(direct.RoomUpdatedAt) } } } }, () => { %>
    <% if (members.Count > 1) { %>
      <div class="avatar__group">
        <% foreach (var member in members.Take(4)) { %>
          <span class="avatar">
            <%= ImageTag(member.AvatarPath, new() { { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
          </span>
        <% } %>
      </div>
    <% } else { %>
      <span class="avatar">
        <%= ImageTag(members[0].AvatarPath, new() { { "size", 48 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
      </span>
    <% } %>

    <span class="direct__author flex align-center gap max-width min-width border-radius txt-small">
      <span class="txt-nowrap overflow-ellipsis">
        <span class="for-screen-reader">Ping with</span>
        <% if (members.Count > 1) { %>
          <%= Campfire.RailsCompat.Formatting.TextHelpers.ToSentence([.. members.Select(member => MemberInitials(member.Name))], twoWordsConnector: "+") %>
        <% } else { %>
          <%= FirstName(members[0].Name) %>
        <% } %>
      </span>
    </span>
  <% }) %>

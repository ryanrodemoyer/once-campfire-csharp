<%= SidebarTurboFrameTag(() => { %>
  <%= TurboStreamFrom(["rooms"]) %>
  <%= TurboStreamFrom([RecordIdentifier.GidParam(Campfire.Data.Records.User.ModelName, page.CurrentUser.Id), "rooms"]) %>

  <div class="sidebar__container overflow-y overflow-hide-scrollbar"
      data-controller="badge-dot"
      data-badge-dot-unread-class="unread"
      data-action="rooms-list:unread@window->badge-dot#update rooms-list:read@window->badge-dot#update turbo:submit-start->turbo-frame#unpermanize">
    <turbo-frame id="direct_rooms_control" target="_top">
      <div class="directs gap overflow-x overflow-hide-scrollbar">
        <%= LinkTo(Routes.NewRoomsDirectPath(), new() { { "class", "direct direct__new" }, { "data", new HtmlOptions { { "turbo_frame", "_self" } } } }, () => { %>
          <span class="avatar avatar--icon">
            <%= ImageTag("messages-add.svg", new() { { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } }, { "class", "colorize--black" } }) %>
          </span>

          <span class="direct__author flex max-width min-width border-radius pad-inline-half">
            <span class="for-screen-reader">New</span>
            <span class="txt-small overflow-clip">Ping</span>
          </span>
        <% }) %>

        <div id="direct_rooms" contents data-controller="sorted-list" data-action="rooms-list:unread@window->sorted-list#updateItem">
          <%= Render(o => { foreach (var direct in page.DirectMemberships) { UsersSidebarsRoomsDirect(o, direct); } }) %>
        </div>

        <div contents>
          <%= Render(o => { foreach (var user in page.DirectPlaceholderUsers) { UsersSidebarsRoomsDirectPlaceholder(o, user); } }) %>
        </div>
      </div>
    </turbo-frame>

    <div class="rooms position-relative flex flex-column gap">
      <div id="shared_rooms" contents data-controller="sorted-list">
        <% foreach (var (room, unread) in page.OtherMemberships) { %>
          <%= Render(o => UsersSidebarsRoomsShared(o, room, unread)) %>
        <% } %>
      </div>

      <% if (page.CanCreateRoom) { %>
        <%= LinkTo(Routes.NewRoomsOpenPath(), new() { { "class", "rooms__new-btn btn room align-center gap txt-reversed" }, { "aria", new HtmlOptions { { "label", "New Chat Room" } } } }, () => { %>
          <%= ImageTag("add.svg", new() { { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } }, { "style", "view-transition-name: new-room" } }) %>
        <% }) %>
      <% } %>
    </div>

    <button class="btn sidebar__toggle" data-action="toggle-class#toggle">
      <%= ImageTag("menu.svg", new() { { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
      <span class="for-screen-reader">Open menu</span>
    </button>
  </div>

  <div class="flex align-end sidebar__tools gap justify-end">
    <%= LinkTo(Routes.UserProfilePath(), new() { { "class", "btn avatar flex-item-no-shrink sidebar__tool" } }, () => { %>
      <%= ImageTag(page.UserAvatarPath, new() { { "size", 48 }, { "aria", new HtmlOptions { { "hidden", "true" } } }, { "style", $"view-transition-name: avatar-{page.CurrentUser.Id}" } }) %>
      <span class="for-screen-reader">My Settings</span>
    <% }) %>

    <%= LinkTo(Routes.EditAccountPath(), new() { { "class", "btn align-center gap txt-reversed sidebar__tool" } }, () => { %>
      <%= ImageTag("settings.svg", new() { { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } }, { "style", "view-transition-name: account-settings" } }) %>
      <span class="for-screen-reader">Account Settings</span>
    <% }) %>
  </div>
<% }) %>

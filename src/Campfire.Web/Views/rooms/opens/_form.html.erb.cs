<%= Render(layout => RoomsLayoutsForm(layout, room, Capture(w, () => { %>
  <%= UserFilterMenuTag(() => { %>
    <li class="flex align-center gap margin-none">
      <figure class="avatar flex-item-no-shrink" style="--avatar-border-radius: 0; --avatar-size: 4ch;">
        <%= ImageTag("everyone.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "class", "colorize--black" }, { "style", "background-color: transparent" } }) %>
        <span class="for-screen-reader">Everyone</span>
      </figure>

      <div class="min-width">
        <div class="overflow-ellipsis fill-shade"><strong>Everyone</strong></div>
      </div>

      <hr class="separator" aria-hidden="true">

      <% if (room.CanAdminister) { %>
        <%= LinkTo(room.TypeChangePath, new() { { "class", "btn--faux flex-inline" }, { "tabindex", "-1" }, { "data", new HtmlOptions { { "turbo_action", "replace" } } } }, () => { %>
          <label for="room_type" class="switch">
            <input type="checkbox" id="room_type" class="switch__input" checked="checked">
            <span class="switch__btn round"></span>
            <span class="for-screen-reader">Give only some access to this room</span>
          </label>
        <% }) %>
      <% } %>
    </li>

    <hr class="separator full-width" style="--border-style: solid">

    <%= users.Count > 20 ? UserFilterSearchTag() : SafeString.Empty %>

    <div data-filter-target="list" contents>
      <%= Render(o => { foreach (var user in users) { RoomsOpensUser(o, user, room); } }) %>
    </div>
  <% }) %>
<% }))) %>

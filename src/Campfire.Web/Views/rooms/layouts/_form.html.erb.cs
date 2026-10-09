<%= FormWith(room.Model, room.Url, null, form => { %>
  <div class="flex align-center gap">
    <% if (room.CanAdminister) { %>
      <%= TranslationButton("room_name") %>

      <label class="flex-item-grow txt-large">
        <%= form.TextField("name", new() { { "name", "room[name]" }, { "id", "room_name" }, { "class", "input full-width" },
              { "required", true }, { "autofocus", true }, { "placeholder", "Name the room" },
              { "data", new HtmlOptions { { "turbo_permanent", true }, { "action", "keydown.enter->form#submit:prevent" } } } }) %>
        <span class="for-screen-reader">Name this room</span>
      </label>
    <% } else { %>
      <h1 class="flex-item-grow txt-x-large">
        <%= room.Name %>
      </h1>
    <% } %>
  </div>

  <hr class="margin-block borderless">

  <section class="room-access margin-block pad-inline fill-shade border-radius">
    <%= content %>
  </section>

  <%= room.CanAdminister ? SubmitRoomButtonTag() : SafeString.Empty %>
<% }) %>

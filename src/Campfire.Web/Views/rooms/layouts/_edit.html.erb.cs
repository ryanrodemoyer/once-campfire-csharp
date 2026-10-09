<% PageTitle = $"Edit settings for {room.Name}"; %>

<% ContentFor(w, "nav", () => { %>
  <div class="flex-item-justify-start">
    <%= LinkBackToLastRoomVisited(room.LastRoomId) %>
  </div>
<% }); %>

<section class="panel txt-align-center" style="view-transition-name: edit-room-<%= room.Id!.Value %>">
  <%= content %>
</section>

<% if (room.CanAdminister) { %>
  <section class="panel txt-align-center">
    <%= ButtonToDeleteRoom(room.Id!, room.Name ?? "", room.DisplayName ?? "") %>
  </section>
<% } %>

<% PageTitle = "New chat room"; %>

<% ContentFor(w, "nav", () => { %>
  <div class="flex-item-justify-start">
    <%= LinkBackToLastRoomVisited(lastRoomId) %>
  </div>
<% }); %>

<section class="panel txt-align-center" style="view-transition-name: <%= viewTransitionName ?? "new-room" %>">
  <%= content %>
</section>

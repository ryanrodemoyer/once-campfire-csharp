<%= Render(layout => RoomsLayoutsNew(layout, room.LastRoomId, Capture(w, () => { %>
  <%= Render(o => RoomsOpensForm(o, room, users)) %>
<% }))) %>

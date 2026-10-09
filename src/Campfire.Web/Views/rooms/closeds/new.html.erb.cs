<%= Render(layout => RoomsLayoutsNew(layout, room.LastRoomId, Capture(w, () => { %>
  <%= Render(o => RoomsClosedsForm(o, room, [], users)) %>
<% }))) %>

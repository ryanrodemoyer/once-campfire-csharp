<%= Render(layout => RoomsLayoutsEdit(layout, room, Capture(w, () => { %>
  <%= Render(o => RoomsClosedsForm(o, 
        room, selectedUsers, unselectedUsers)) %>
<% }))) %>

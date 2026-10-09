<%= Render(layout => RoomsLayoutsEdit(layout, room, Capture(w, () => { %>
  <%= Render(o => RoomsOpensForm(o, room, users)) %>
<% }))) %>

<% PageTitle = page.DisplayName; %>
<% BodyClass = "sidebar"; %>
<% ContentFor(w, "head", () => { %>
  <%= TurboExemptsPageFromPreview() %>
  <meta name="current-room-id" content="<%= page.Room.Id %>">
<% }); %>

<%= Render(o => RoomsShowNav(o, page)) %>

<% ContentFor("sidebar", SidebarTurboFrameTag(src: Routes.UserSidebarPath())); %>

<%= MessageAreaTag(page.Room.Id, () => { %>
  <%= Render(o => MessagesTemplate(o, page.CurrentUser)) %>

  <%= MessagesTag(page.Key, page.Room.Id, page.Room.UpdatedAt, () => CaptureOrLastText(w, "\n", () => { %>
    <%= Render(o => RoomsShowInvitation(o, page)) %>
    <%= Render(o => { foreach (var message in page.Messages) { MessagesMessage(o, message); } }) %>
  <% })) %>

  <%= TurboStreamFrom([RecordIdentifier.GidParam(page.Key.ModelName, page.Room.Id), "messages"], channel: "RoomMessagesChannel") %>
  <%= ButtonToJumpToNewestMessage() %>
<% }) %>

<%= Render(o => RoomsShowComposer(o, page)) %>

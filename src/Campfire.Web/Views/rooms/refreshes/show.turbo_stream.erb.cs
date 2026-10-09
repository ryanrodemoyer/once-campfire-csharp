<% if (newMessages.Count > 0) { %>
<%= TurboStreamTags.Append(RecordIdentifier.DomId(new RecordKey(room.Type.ClassName(), room.Id), "messages"), () => { %>
  <%= Render(o => { foreach (var message in newMessages) { MessagesMessage(o, message); } }) %>
<% }) %>
<% } %>

<% foreach (var message in updatedMessages) { %>
  <%= TurboStreamTags.Replace(RecordIdentifier.DomId(message.Record), () => MessagesMessage(w, message)) %>
<% } %>

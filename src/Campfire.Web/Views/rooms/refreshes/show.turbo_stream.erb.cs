<% if (newMessages.Count > 0) { %>
<%= TurboStreamTags.Append(RecordIdentifier.DomId(new RecordKey(room.Type.ClassName(), room.Id), "messages"), () => { %>
  <%= RenderCachedMessages(newMessages) %>
<% }) %>
<% } %>

<% foreach (var message in updatedMessages) { %>
  <%= TurboStreamTags.Replace(RecordIdentifier.DomId(message.Record), () => MessagesMessage(w, message)) %>
<% } %>

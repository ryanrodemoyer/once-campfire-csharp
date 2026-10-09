<%= TurboFrameForInvolvementTag(new RecordKey(room.Type.ClassName(), room.Id), room.Id, () => { %>
  <%= ButtonToChangeInvolvement(new RecordKey(room.Type.ClassName(), room.Id), room.Id, room.IsDirect, involvement) %>
<% }) %>

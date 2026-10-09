<li class="flex align-center gap margin-none min-width membership-item">
  <%= LinkTo(Routes.RoomPath(membership.RoomId), new() { { "class", "overflow-ellipsis fill-shade txt-primary txt-undecorated" } }, () => { %>
    <strong><%= membership.DisplayName %></strong>
  <% }) %>

  <hr class="separator" aria-hidden="true">

  <span class="txt-small">
    <%= TurboFrameTag(RecordIdentifier.DomId(membership.Room, "involvement"), null, () => { %>
      <%= ButtonToChangeInvolvement(membership.Room, membership.RoomId, membership.IsDirect, membership.Involvement) %>
    <% }) %>
  </span>
</li>

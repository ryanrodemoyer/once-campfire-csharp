<%= LinkToRoom(room.Id, new() {
      { "id", RecordIdentifier.DomId(new RecordKey(room.Type.ClassName(), room.Id), "list") }, { "data", new HtmlOptions { { "sorted_list_name", room.Name } } },
      { "style", "--column-gap: 0.5em" }, { "class", "align-center gap room btn txt-nowrap" } }, () => { %>
  <span class="overflow-ellipsis"><%= room.Name %></span>
<% }) %>

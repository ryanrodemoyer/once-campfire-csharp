<lexxy-prompt-item search="<%= user.Name %>" sgid="<%= user.Sgid %>">
  <template type="menu">
    <span class="autocomplete__item flex align-center gap unpad">
      <%= AvatarTag(new AvatarUser(user.Id, user.Title, user.AvatarToken, user.UpdatedAt)) %>
      <span class="autocompletable__name"><%= user.Name %></span>
    </span>
  </template>
  <template type="editor">
    <%= Render(o => AutocompletableUsersMention(o, user)) %>
  </template>
</lexxy-prompt-item>

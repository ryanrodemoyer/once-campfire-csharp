<%= TurboStreamTags.Replace("next_page_container", () => { foreach (var row in page.Users) { AccountsUsersUser(w, row.User, row.AvatarToken); } }) %>

<% if (page.NextPage is { } next) { %>
  <%= TurboStreamTags.Append("account_users", () => AccountsUsersNextPageContainer(w, next)) %>
<% } %>

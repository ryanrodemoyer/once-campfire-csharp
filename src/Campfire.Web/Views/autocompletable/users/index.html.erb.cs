<%= Render(o => { foreach (var user in users) { AutocompletableUsersPromptItem(o, user); } }) %>

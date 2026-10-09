<% var avatar = new AvatarUser(user.Id, user.Title, avatarToken, user.UpdatedAt); %>
<li class="flex align-center gap margin-none <%= user.IsBanned ? "banned" : null %>">
  <figure class="avatar flex-item-no-shrink" style="--avatar-size: 3.75ch;">
    <%= AvatarTag(avatar, new() { { "loading", "lazy" } }) %>
  </figure>

  <div class="min-width">
    <div class="overflow-ellipsis fill-shade"><strong><%= user.Name %></strong></div>
  </div>

  <hr class="separator" aria-hidden="true">

  <% if (CurrentUser!.CanAdminister && user.IsActive) { %>
    <% if (!user.IsBot) { %>
      <%= FormWith(AccountUserFormModel(user), Routes.AccountUserPath(user.Id), new() { { "data", new HtmlOptions { { "controller", "form" } } }, { "method", "patch" } }, form => { %>
        <label class="btn txt-small flex-item-no-shrink" for="<%= RecordIdentifier.DomId(new(User.ModelName, user.Id), "role") %>">
          <span class="for-screen-reader">Role: <%= user.IsAdministrator ? "Administrator" : "Member" %></span>
          <%= ImageTag("crown.svg", new() { { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
          <%= form.CheckBox("role", new() { { "data", new HtmlOptions { { "action", "form#submit" } } }, { "hidden", true }, { "id", RecordIdentifier.DomId(new(User.ModelName, user.Id), "role") }, { "disabled", user.Id == CurrentUser.Id } }, "administrator", "member") %>
        </label>
      <% }) %>
    <% } %>

    <% if (user.Id != CurrentUser.Id) { %>
      <%= ButtonTo(Routes.AccountUserPath(user.Id), new() { { "method", "delete" }, { "class", "btn txt-small flex-item-no-shrink btn--negative" }, { "data", new HtmlOptions {
            { "turbo_confirm", "Are you sure you want to permanently remove this person from the account? This can’t be undone." } } } }, () => { %>
        <%= ImageTag("minus.svg", new() { { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
        <span class="for-screen-reader">Delete <%= user.Name %></span>
      <% }) %>
    <% } %>
  <% } %>
  <% if (user.Id == CurrentUser.Id) { %>
    <%= LinkTo(Routes.UserProfilePath(), new() { { "class", "btn txt-small flex-item-no-shrink" }, { "target", "_top" } }, () => { %>
      <%= ImageTag("pencil.svg", new() { { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
      <span class="for-screen-reader">My settings</span>
    <% }) %>
  <% } %>
</li>

<% PageTitle = user.Name; %>

<% ContentFor(w, "nav", () => { %>
  <div class="flex-item-justify-start">
    <%= LinkBack() %>
  </div>

  <div class="flex align-center gap flex-item-justify-end">
    <% if (CurrentUser!.Id == user.Id) { %>
      <%= LinkTo(Routes.UserProfilePath(), new() { { "class", "btn" } }, () => { %>
        <%= ImageTag("pencil.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
        <span class="for-screen-reader">Edit my profile</span>
      <% }) %>
    <% } %>
  </div>
<% }); %>

<section class="panel txt-align-center">
  <div class="flex flex-column gap <%= user.IsBanned ? "banned" : null %>">
    <div class="avatar txt-xx-large center" style="background: white">
      <%= ImageTag(Routes.FreshUserAvatarPath(avatarToken, user.UpdatedAt), new() { { "alt", "Profile avatar" }, { "class", "avatar" } }) %>
    </div>

    <% if (user.IsBot) { %>
      <div class="pad-double--inline push--inline push--block-start">
        <% if (user.IsActive) { %>
          <%= ButtonToDirectRoomWith(user.Id) %>
        <% } else { %>
          <div><%= user.Name %> is no longer on this account</div>
        <% } %>
      </div>
    <% } else { %>
      <% if (!user.IsDeactivated) { %>
        <div class="flex flex-column gap" style="--row-gap: calc(var(--block-space) / 3)">
          <h1 class="txt-x-large txt-tight-lines margin-none"><%= user.Name %></h1>
          <% if (CurrentUser!.CanAdminister) { %>
            <div><%= MailTo(user.EmailAddress!) %></div>
          <% } %>
          <div><%= user.Bio %></div>
        </div>

        <% if (user.IsActive) { %>
          <div class="pad-inline-double margin-inline margin-block-start">
            <%= ButtonTo(Routes.RoomsDirectsPath(new() { { "user_ids", new object[] { user.Id } } }), new() { { "class", "btn btn--reversed full-width txt-large" } }, () => { %>
              <%= ImageTag("messages.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" }, { "label", $"Ping {user.Name}" } } } }) %>
            <% }) %>
          </div>

          <% if (CurrentUser!.CanAdminister) { %>
            <hr class="margin-block-start borderless">

            <%= Render(partial => UsersProfilesTransfer(partial, user.Id, transferUrl!)) %>
          <% } %>
        <% } %>

        <% if (CurrentUser.CanAdminister && CurrentUser.Id != user.Id) { %>
          <div class="margin-block-start">
            <%= Render(partial => UsersBanButton(partial, user)) %>
          </div>
        <% } %>
      <% } else { %>
        <div>
          <h1 class="txt-x-large margin-none"><%= user.Name %></h1>
          <div><%= user.Name %> is no longer on this account</div>
        </div>
      <% } %>
    <% } %>
  </div>
</section>

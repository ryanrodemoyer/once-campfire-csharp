<% PageTitle = "Account settings"; %>

<% ContentFor(w, "nav", () => { %>
  <div class="flex-item-justify-start">
    <%= LinkBackToLastRoomVisited(page.LastRoomId) %>
  </div>

  <% if (page.CurrentUserIsAdministrator) { %>
    <div class="flex align-center gap flex-item-justify-end">
      <%= LinkTo(Routes.AccountBotsPath(), new() { { "class", "btn" }, { "style", "view-transition-name: chat-bots" } }, () => { %>
        <%= ImageTag("bot.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
        <span class="for-screen-reader">Set up chat bots</span>
      <% }) %>

      <%= LinkTo(Routes.EditAccountCustomStylesPath(), new() { { "class", "btn" }, { "style", "view-transition-name: custom-styles" } }, () => { %>
        <%= ImageTag("art.svg", new() { { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
        <span class="for-screen-reader">Custom styles</span>
      <% }) %>
    </div>
  <% } %>
<% }); %>

<section class="panel txt-align-center flex flex-column gap" style="view-transition-name: account-settings">
  <% if (CurrentUser!.CanAdminister) { %>
    <div class="align-center center avatar__form gap" data-controller="upload-preview">
      <%= FormWith(page.Account, page.AccountFormPath, new() { { "method", "patch" }, { "class", "txt--medium" }, { "data", new HtmlOptions { { "controller", "form" } } } }, form => { %>
        <label class="btn input--file">
          <%= ImageTag("camera.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
          <%= form.FileField("logo", new() { { "class", "input" }, { "accept", "image/*" },
                { "data", new HtmlOptions { { "action", "upload-preview#previewImage change->form#submit" } } } }) %>
          <span class="for-screen-reader">Upload logo</span>
        </label>
      <% }) %>

      <%= FormWith(page.Account, page.AccountFormPath, new() { { "method", "patch" }, { "data", new HtmlOptions { { "controller", "form" } } } }, form => { %>
        <label class="btn avatar input--file account-logo txt-xx-large">
          <%= ImageTag(Routes.FreshAccountLogoPath(CurrentAccount?.UpdatedAt), new() { { "role", "presentation" }, { "size", 48 }, { "data", new HtmlOptions { { "upload_preview_target", "image" } } } }) %>
          <%= form.FileField("logo", new() { { "class", "input" }, { "accept", "image/*" },
                { "data", new HtmlOptions { { "action", "upload-preview#previewImage change->form#submit" } } } }) %>
          <span class="for-screen-reader">Upload logo</span>
        </label>
      <% }) %>

      <% if (CurrentAccount!.LogoAttached) { %>
        <%= ButtonTo(Routes.FreshAccountLogoPath(CurrentAccount.UpdatedAt), new() { { "method", "delete" }, { "class", "btn btn--negative txt-small avatar__delete-btn" } }, () => { %>
          <%= ImageTag("minus.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
          <span class="for-screen-reader">Delete logo</span>
        <% }) %>
      <% } %>
    </div>

    <%= FormWith(page.Account, page.AccountFormPath, new() { { "data", new HtmlOptions { { "controller", "form" } } }, { "class", "flex flex-column gap" } }, form => { %>
      <div class="flex align-center gap">
        <%= TranslationButton("account_name") %>

        <label class="flex align-center gap flex-item-grow">
          <%= form.TextField("name", new() { { "class", "input txt-large" }, { "autocomplete", "off" }, { "placeholder", "Name this account" }, { "autofocus", true },
                { "data", new HtmlOptions { { "action", "keydown.enter->form#submit" } } } }) %>
        </label>

        <%= form.Button(new() { { "class", "btn btn--reversed center" }, { "type", "submit" } }, () => { %>
          <%= ImageTag("check.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
          <span class="for-screen-reader">Save changes</span>
        <% }) %>
      </div>
    <% }) %>

    <div class="margin-block-start pad-block pad-inline-double fill-shade border-radius">
      <%= FormWith(page.Account, page.AccountFormPath, new() { { "method", "put" }, { "data", new HtmlOptions { { "controller", "form" } } }, { "class", "flex align-center gap center" } }, form => { %>
        <div class="flex-item-grow flex align-center gap txt-align-start">
          <%= ImageTag("crown.svg", new() { { "class", "colorize--black" }, { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 18 } }) %> Must be admin to create new rooms
        </div>
        <%= form.FieldsFor("settings", null, settingsForm => { %>
          <%= settingsForm.HiddenField("restrict_room_creation_to_administrators",
              new() { { "value", !page.RestrictRoomCreationToAdministrators } }) %>
        <% }) %>

        <label class="switch">
          <input type="checkbox"
                class="switch__input"
                <%= page.RestrictRoomCreationToAdministrators ? "checked" : null %>
                data-action="change->form#submit">
          <span class="switch__btn round"></span>
          <span class="for-screen-reader">
            Must be admin to create new rooms
          </span>
        </label>

      <% }) %>
    </div>
  <% } else { %>
    <%= AccountLogoTag(style: "txt-xx-large center") %>
    <h1 class="flex-item-grow txt-x-large"><%= page.AccountName %></h1>
  <% } %>

  <div class="margin-block pad-inline pad-block-start fill-shade border-radius">
    <%= Render(partial => AccountsInvite(partial, page.JoinCode)) %>

    <hr class="margin-block separator full-width" style="--border-style: solid">

    <menu class="flex flex-column gap margin-none pad">
      <turbo-frame id="account_users">
        <% foreach (var user in page.Administrators) { %><%= Render(partial => AccountsUsersUser(partial, user.User, user.AvatarToken)) %><% } %>

        <% if (page.Administrators.Count > 0 && page.Members.Count > 0) { %>
          <hr class="separator full-width" style="--border-style: solid">
        <% } %>

        <% foreach (var user in page.Members) { %><%= Render(partial => AccountsUsersUser(partial, user.User, user.AvatarToken)) %><% } %>
        <%= page.NextPage is { } nextPage ? Render(partial => AccountsUsersNextPageContainer(partial, nextPage)) : null %>
      </turbo-frame>
    </menu>
  </div>
</section>

<% ContentFor(w, "footer", () => { %>
  <div class="txt-align-center center margin-block-double txt-subtle">Campfire&trade; version <%= VersionBadge() %></div>
<% }); %>

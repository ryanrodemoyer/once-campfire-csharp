<% PageTitle = page.User.Name; %>

<% ContentFor(w, "nav", () => { %>
  <div class="flex-item-justify-start">
    <%= LinkBack() %>
  </div>

  <div class="flex-item-justify-end">
    <%= FormWith(null, Routes.SessionPath(), new() { { "method", "delete" }, { "data", new HtmlOptions { { "controller", "sessions" } } } }, form => { %>
      <%= HiddenFieldTag("push_subscription_endpoint", null, new() { { "data", new HtmlOptions { { "sessions_target", "pushSubscriptionEndpoint" } } } }) %>

      <button class="btn" data-action="sessions#logout:prevent">
        <%= ImageTag("logout.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
        <span class="for-screen-reader">Log out</span>
      </button>
    <% }) %>
  </div>
<% }); %>

<section class="panel flex flex-column gap" style="view-transition-name: avatar-<%= page.User.Id %>">
  <%= Render(partial => PwaInstallInstructions(partial, page.Platform)) %>

  <div class="align-center center avatar__form gap" data-controller="upload-preview">
    <%= ProfileFormWith(page.Form, new() { { "class", "txt-medium" } }, form => { %>
      <label class="btn input--file">
        <%= ImageTag("camera.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
        <%= form.FileField("avatar", new() { { "id", "file" }, { "class", "input" }, { "accept", "image/*" },
            { "data", new HtmlOptions { { "upload_preview_target", "input" }, { "action", "upload-preview#previewImage change->form#submit" } } } }) %>
        <span class="for-screen-reader">Upload avatar</span>
      </label>
    <% }) %>

    <%= ProfileFormWith(page.Form, null, form => { %>
      <label class="btn avatar input--file txt-xx-large">
        <%= ImageTag(page.AvatarPath, new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 300 }, { "data", new HtmlOptions { { "upload_preview_target", "image" } } } }) %>
        <%= form.FileField("avatar", new() { { "id", "file" }, { "class", "input" }, { "accept", "image/*" },
            { "data", new HtmlOptions { { "upload_preview_target", "input" }, { "action", "upload-preview#previewImage change->form#submit" } } } }) %>
        <span class="for-screen-reader">Avatar</span>
      </label>
    <% }) %>

    <% if (page.AvatarAttached) { %>
      <%= ButtonTo(Routes.UserAvatarPath(page.User.Id), new() { { "method", "delete" }, { "class", "btn btn--negative txt-small avatar__delete-btn" } }, () => { %>
        <%= ImageTag("minus.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
        <span class="for-screen-reader">Delete avatar</span>
      <% }) %>
    <% } %>
  </div>

  <%= ProfileFormWith(page.Form, null, form => { %>
    <div class="flex flex-column gap">
      <div class="flex align-center gap">
        <%= TranslationButton("user_name") %>

        <label class="flex align-center gap flex-item-grow input input--actor">
          <%= form.TextField("name", new() { { "class", "input txt-large " }, { "autocomplete", "name" }, { "placeholder", "Enter your name" },
                { "autofocus", true }, { "required", true }, { "data", new HtmlOptions { { "1p-ignore", true } } } }) %>
          <%= ImageTag("person.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 24 }, { "class", "colorize--black" } }) %>
        </label>
      </div>

      <div class="flex align-center gap">
        <%= TranslationButton("email_address") %>

        <label class="flex align-center gap flex-item-grow input input--actor">
          <%= form.EmailField("email_address", new() { { "class", "input txt-large" }, { "value", page.User.EmailAddress }, { "autocomplete", "username" },
                { "placeholder", "Enter your email address" }, { "required", false } }) %>
          <%= ImageTag("email.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 24 }, { "class", "colorize--black" } }) %>
        </label>
      </div>

      <div class="flex align-center gap">
        <%= TranslationButton("update_password") %>

        <label class="flex align-center gap flex-item-grow input input--actor">
          <%= form.PasswordField("password", new() { { "class", "input txt-large" }, { "autocomplete", "new-password" }, { "placeholder", "Change password" },
                { "required", false }, { "maxlength", 72 } }) %>
          <%= ImageTag("password.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 24 }, { "class", "colorize--black" } }) %>
        </label>
      </div>

      <div class="flex align-start gap">
        <%= TranslationButton("bio") %>

        <label class="flex align--center gap flex-item--grow input input--actor">
          <%= form.TextArea("bio", new() { { "class", "input txt-large" }, { "placeholder", "A few words about yourself…" }, { "maxlength", 200 },
                { "rows", 3 }, { "required", false } }) %>
          <%= ImageTag("bio.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 24 }, { "class", "colorize--black" } }) %>
        </label>
      </div>

      <%= ProfileFormSubmitButton() %>
    </div>
  <% }) %>

  <div class="margin-block pad-inline pad-block fill-shade border-radius">
    <menu class="flex flex-column gap margin-none pad">
      <%= Render(partial => { foreach (var membership in page.SharedMemberships) { UsersProfilesMembership(partial, membership); } }) %>

      <% if (page.DirectMemberships.Count > 0 && page.SharedMemberships.Count > 0) { %>
        <hr class="separator full-width" style="--border-style: solid">
      <% } %>

      <%= Render(partial => { foreach (var membership in page.DirectMemberships) { UsersProfilesMembership(partial, membership); } }) %>
    </menu>
  </div>

  <%= Render(partial => UsersProfilesTransfer(partial, page.User.Id, page.TransferUrl)) %>

</section>

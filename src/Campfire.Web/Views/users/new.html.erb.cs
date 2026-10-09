<% PageTitle = "Sign up"; %>
<% BodyClass = "signup"; %>

<% ContentFor(w, "nav", () => { %>
  <div class="flex-item-justify-end">
    <%= LinkTo(Routes.NewSessionPath(), new() { { "class", "btn flex-item-justify-end" } }, () => { %>
      <%= ImageTag("login-keys.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
      <span class="for-screen-reader">Sign in</span>
    <% }) %>
  </div>
<% }); %>

<%= FormWith(FormModel.New("User"), Routes.JoinPath(joinCode), new() { { "class", "center" } }, form => { %>
  <section class="nametag u-relative">
    <div class="flex justify-center align-center pad-block">
      <%= ImageTag("lanyard.svg", new() { { "class", "nametag__lanyard" }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
    </div>

    <div class="nametag__inner flex flex-column gap">
      <fieldset class="flex flex-column center-block">
        <legend class="txt-align-center flex gap">
          <%= AccountLogoTag() %>
          <strong class="txt-large"><%= accountName %></strong>
        </legend>

        <label class="align-center center avatar__form gap" data-controller="upload-preview">
          <div class="btn input--file">
            <%= ImageTag("camera.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
            <%= form.FileField("avatar", new() { { "class", "input" }, { "accept", "image/*" },
                  { "data", new HtmlOptions { { "upload_preview_target", "input" }, { "action", "upload-preview#previewImage" } } } }) %>
            <span class="for-screen-reader">Upload avatar</span>
          </div>

          <div class="btn avatar input--file txt-xx-large">
            <%= ImageTag("default-avatar.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "data", new HtmlOptions { { "upload_preview_target", "image" } } } }) %>
            <span class="for-screen-reader">Avatar</span>
          </div>
        </label>
      </fieldset>

      <div class="flex align-center gap">
        <%= TranslationButton("user_name") %>
        <label class="flex align-center gap flex-item-grow txt-large input input--actor">
          <%= form.TextField("name", new() { { "class", "input" }, { "autocomplete", "name" }, { "placeholder", "Name" }, { "autofocus", true }, { "required", true },
                { "data", new HtmlOptions { { "1p-ignore", true } } } }) %>
          <%= ImageTag("person.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 24 }, { "class", "colorize--black" } }) %>
        </label>
      </div>

      <div class="flex align-center gap">
        <%= TranslationButton("email_address") %>
        <label class="flex align-center gap flex-item-grow txt-large input input--actor">
          <%= form.EmailField("email_address", new() { { "class", "input" }, { "autocomplete", "username" }, { "placeholder", "Email address" }, { "required", true } }) %>
          <%= ImageTag("email.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 24 }, { "class", "colorize--black" } }) %>
        </label>
      </div>

      <div class="flex align-center gap">
        <%= TranslationButton("password") %>
        <label class="flex align-center gap flex-item-grow txt-large input input--actor">
          <%= form.PasswordField("password", new() { { "class", "input" }, { "autocomplete", "new-password" }, { "placeholder", "Password" }, { "required", true }, { "maxlength", 72 } }) %>
          <%= ImageTag("password.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 24 }, { "class", "colorize--black" } }) %>
        </label>
      </div>

      <%= form.Button(new() { { "class", "btn btn--reversed center txt-large" }, { "type", "submit" } }, () => { %>
        <%= ImageTag("check.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
        <span class="for-screen-reader">Save</span>
      <% }) %>
    </div>
  </section>
<% }) %>

<%= Render(partial => AccountsHelpContact(partial, helpContact)) %>

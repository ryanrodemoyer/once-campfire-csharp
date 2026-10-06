<% PageTitle = "Sign in"; %>
<% TurboPageRequiresReload(); %>

<section class="txt-align-center">
  <div class="panel <%= Flash.Alert is not null ? "shake" : null %>">
    <%= AccountLogoTag(style: "center margin-block-end txt-xx-large") %>

    <%= FormWith(null, Routes.SessionUrl(Origin), new() { { "class", "flex flex-column gap" } }, form => { %>
      <fieldset class="flex flex-column gap center-block upad">
        <legend class="txt-large txt-align-center"><strong><%= accountName %></strong></legend>

        <div class="flex align-center gap">
          <%= TranslationButton("email_address") %>
          <label class="flex align-center gap input input--actor txt-large">
            <%= form.EmailField("email_address", new() { { "required", true }, { "class", "input" }, { "autofocus", true }, { "autocomplete", "username" }, { "placeholder", "Enter your email address" }, { "value", emailAddress } }) %>
            <%= ImageTag("email.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 24 }, { "class", "colorize--black" } }) %>
          </label>
        </div>

        <div class="flex align-center gap">
          <%= TranslationButton("password") %>
          <label class="flex align-center gap input input--actor txt-large">
            <%= form.PasswordField("password", new() { { "required", true }, { "class", "input" }, { "autocomplete", "current-password" }, { "placeholder", "Enter your password" }, { "maxlength", 72 } }) %>
            <%= ImageTag("password.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 24 }, { "class", "colorize--black" } }) %>
          </label>
        </div>

        <%= form.Button(new() { { "class", "btn btn--reversed center txt-large" }, { "type", "submit" }, { "name", "log_in" } }, () => { %>
          <%= ImageTag("arrow-right.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
          <span class="for-screen-reader">Go</span>
        <% }) %>
      </fieldset>
    <% }) %>
  </div>

  <%= Render(partial => AccountsHelpContact(partial, helpContact)) %>
</section>

<% PageTitle = "Custom styles"; %>

<% ContentFor(w, "nav", () => { %>
  <div class="flex-item-justify-start">
    <%= LinkBackTo(Routes.EditAccountPath()) %>
  </div>
<% }); %>

<section class="panel panel--wide txt-align-center flex flex-column position-relative" style="view-transition-name: custom-styles">
  <%= FormWith(account, Routes.AccountCustomStylesUrl(Origin), new() { { "class", "flex flex-column gap" },
      { "data", new HtmlOptions { { "controller", "form" }, { "action", "keydown.ctrl+enter->form#submit keydown.meta+enter->form#submit" } } } }, form => { %>
    <div class="panel__button">
      <%= TranslationButton("custom_styles") %>
    </div>

    <div class="pad-inline-double margin-inline">
      <h1 class="margin-none">Custom CSS</h1>
      <p class="flex flex-wrap align-center justify-center gap margin-none-block-start" style="--column-gap: 0.5ch; --row-gap: 0">
        <span>Add custom CSS styles.</span>
        <%= ImageTag("alert.svg", new() { { "class", "flex-inline colorize--black" }, { "size", 16 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
        <span>Use Caution: you could break things.</span>
      </p>
    </div>

    <label class="flex align-start gap flex-item-grow">
      <%= form.TextArea("custom_styles", new() { { "class", "input input--code txt--small" }, { "placeholder", "Add CSS styles…" },
            { "autocomplete", "off" }, { "spellcheck", "false" }, { "autocorrect", "off" }, { "autocapitalize", "off" },
            { "rows", 16 }, { "required", false } }) %>
    </label>

    <%= form.Button(new() { { "class", "btn btn--reversed center txt-large" }, { "type", "submit" } }, () => { %>
      <%= ImageTag("check.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
      <span class="for-screen-reader">Save changes</span>
    <% }) %>
  <% }) %>
</section>

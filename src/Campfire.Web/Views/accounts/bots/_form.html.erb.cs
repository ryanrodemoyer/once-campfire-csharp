<h1 class="for-screen-reader">Chat Bot Setup</h1>
<label class="align-center center avatar__form gap" data-controller="upload-preview">
  <div class="btn input--file">
    <%= ImageTag("camera.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
    <%= form.FileField("avatar", new() { { "class", "input" }, { "accept", "image/*" },
          { "data", new HtmlOptions { { "upload_preview_target", "input" }, { "action", "upload-preview#previewImage" } } } }) %>
    <span class="for-screen-reader">Upload bot avatar</span>
  </div>

  <div class="avatar input--file txt-xx-large" style="--avatar-size: var(--btn-size);">
    <%= ImageTag(bot.AvatarUrl ?? "default-bot-avatar.svg", new() { { "alt", "Bot avatar" }, { "size", 48 }, { "data", new HtmlOptions { { "upload_preview_target", "image" } } } }) %>
  </div>
</label>

<div class="flex align-center gap">
  <%= TranslationButton("bot_name") %>
  <label class="flex align-center gap flex-item-grow txt-large input input--actor">
    <%= form.TextField("name", new() { { "class", "input" }, { "autocomplete", "name" }, { "placeholder", "Name the bot" }, { "autofocus", true }, { "required", true },
          { "data", new HtmlOptions { { "1p-ignore", true } } } }) %>
    <%= ImageTag("bot.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 24 }, { "class", "colorize--black" } }) %>
  </label>
</div>

<div class="flex align-center gap">
  <%= TranslationButton("webhook_url") %>
  <label class="flex align-center gap flex-item-grow txt-large input input--actor">
    <%= form.UrlField("webhook_url", new() { { "class", "input" }, { "placeholder", "Webhook URL" } }) %>
    <%= ImageTag("web.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 24 }, { "class", "colorize--black" } }) %>
  </label>
</div>

<%= ProfileFormSubmitButton() %>

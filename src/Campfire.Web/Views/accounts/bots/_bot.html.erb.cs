<li class="flex flex-column gap flush fill-shade border-radius pad-block pad-inline-double">
  <div class="flex align-center gap">
    <figure class="avatar flex-item--no-shrink" style="--avatar-size: 2.65em;">
      <%= AvatarTag(bot.Avatar, new() { { "loading", "lazy" } }) %>
    </figure>

    <div class="min-width">
      <div class="overflow-ellipsis txt-large"><strong><%= bot.User.Name %></strong></div>
    </div>

    <%= LinkTo(Routes.EditAccountBotPath(bot.User.Id), new() { { "class", "btn flex-item-justify-end" }, { "style", $"view-transition-name: chat-bot-{bot.User.Id}" } }, () => { %>
      <%= ImageTag("pencil.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
      <span class="for-screen-reader">Edit <%= bot.User.Name %></span>
    <% }) %>
  </div>

  <% foreach (var room in bot.Rooms) { %>
    <fieldset class="gap max-width pad border border-radius">
      <legend class="min-width txt-align-start pad-inline">
        <strong class="overflow-ellipsis"><%= room.Name %></strong>
      </legend>

      <% var curlTextLine = $"curl -d 'Hello!' {Routes.RoomBotMessagesUrl(Origin, room.Id, bot.User.BotKey)}"; %>
      <div class="flex align-center gap">
        <%= ImageTag("messages-outlined.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 24 }, { "class", "colorize--black" } }) %>

        <div class="flex-item-grow">
          <input type="text" class="input full-width fill-white" value="<%= curlTextLine %>" aria-label="curl command for posting messages" readonly>
        </div>

        <div class="txt-small">
          <%= ButtonToCopyToClipboard(curlTextLine, () => { %>
            <%= ImageTag("copy-paste.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
            <span class="for-screen-reader">Copy message command</span>
          <% }) %>
        </div>
      </div>

      <% var curlUploadLine = $"curl -F \"attachment=@/path/to/file\" {Routes.RoomBotMessagesUrl(Origin, room.Id, bot.User.BotKey)}"; %>
      <div class="flex align-center gap">
        <%= ImageTag("attachment.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 24 }, { "class", "colorize--black" } }) %>

        <div class="flex-item-grow">
          <input type="text" class="input full-width fill-white" value="<%= curlUploadLine %>" aria-label="curl command for posting attachments" readonly>
        </div>

        <div class="txt-small">
          <%= ButtonToCopyToClipboard(curlUploadLine, () => { %>
            <%= ImageTag("copy-paste.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
            <span class="for-screen-reader">Copy attachment command</span>
          <% }) %>
        </div>
      </div>
    </fieldset>
  <% } %>
</li>

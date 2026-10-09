<% PageTitle = "Edit bot"; %>

<% ContentFor(w, "nav", () => { %>
  <div class="flex-item-justify-start">
    <%= LinkBackTo(Routes.AccountBotsPath()) %>
  </div>
<% }); %>

<section class="panel" style="view-transition-name: chat-bot-<%= bot.User.Id %>">
  <%= FormWith(bot.FormModel, Routes.AccountBotPath(bot.User.Id), new() { { "class", "flex flex-column gap" } }, form => { %>
    <%= Render(partial => AccountsBotsForm(partial, form, bot.Form)) %>
  <% }) %>

  <hr class="separator full-width margin-block-double">

  <div class="flex align-center gap justify-space-between">
    <%= ButtonTo(Routes.AccountBotPath(bot.User.Id), new() { { "method", "delete" }, { "class", "btn txt--small btn--negative" }, { "aria", new HtmlOptions { { "label", "Delete this chat bot" } } }, { "data", new HtmlOptions { { "turbo_confirm", "Are you sure you want to permanently remove this bot from the account? This can’t be undone." } } } }, () => { %>
      <%= ImageTag("trash.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
      <%= ImageTag("bot.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
    <% }) %>

    <%= ButtonTo(Routes.AccountBotKeyPath(bot.User.Id), new() { { "method", "put" }, { "class", "btn full-width txt--small btn--negative" }, { "aria", new HtmlOptions { { "label", "Generate a new key" } } }, { "data", new HtmlOptions { { "turbo_confirm", "Are you sure you want to change the bot key? All usage of this bot must be updated." } } } }, () => { %>
      <%= ImageTag("refresh.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
      <%= ImageTag("key.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
    <% }) %>
  </div>
</section>

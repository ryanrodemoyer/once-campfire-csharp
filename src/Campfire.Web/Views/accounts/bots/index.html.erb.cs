<% PageTitle = "Chat bots"; %>

<% ContentFor(w, "nav", () => { %>
  <div class="flex-item-justify-start">
    <%= LinkBackTo(Routes.EditAccountPath()) %>
  </div>
<% }); %>

<section class="panel panel--wide txt-align-center flex flex-column position-relative" style="view-transition-name: chat-bots">
  <div class="flex align-center gap">
    <div class="panel__button">
      <%= TranslationButton("chat_bots") %>
    </div>
    <div class="pad-inline-double center">
      <h1 class="margin-none">Chat bots</h1>
      <p class="margin-none-block-start">With Chat bots, other sites and services can post updates directly to Campfire.</p>

      <%= LinkTo(Routes.NewAccountBotPath(), new() { { "class", "btn btn--reversed txt-large" }, { "aria", new HtmlOptions { { "label", "Add a chat bot" } } } }, () => { %>
        <%= ImageTag("bot.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
        <%= ImageTag("add.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
      <% }) %>
    </div>
  </div>

  <div class="pad-inline pad-block-start ">
    <menu class="flex flex-column gap margin-none pad">
      <%= Render(partial => { foreach (var bot in bots) { AccountsBotsBot(partial, bot); } }) %>
    </menu>
  </div>
</section>

<% PageTitle = "New chat bot"; %>

<% ContentFor(w, "nav", () => { %>
  <div class="flex-item-justify-start">
    <%= LinkBackTo(Routes.AccountBotsPath()) %>
  </div>
<% }); %>

<section class="panel">
  <%= FormWith(bot, Routes.AccountBotsPath(), new() { { "class", "flex flex-column gap" } }, form => { %>
    <%= Render(partial => AccountsBotsForm(partial, form, new(null))) %>
  <% }) %>
</section>

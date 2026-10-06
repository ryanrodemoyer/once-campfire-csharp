<% PageTitle = isAppleMessages ? "Campfire" : "Unsupported browser"; %>

<div class="panel center">
  <header>
    <h1 class="txt-x-large txt-tight-lines txt-align-center margin-none-block-start margin-block-end">
      Upgrade to a supported web browser
    </h1>
    <div class="flex align-start gap">
      <%= TranslationButton("incompatible_browser_messsage") %>
      <p class="margin-none-block-start">Campfire requires a modern web browser. Please use one of the browsers listed below and make sure auto-updates are enabled.</p>
    </div>
  </header>

  <div class="browser-list flex align-center flex-wrap gap justify-center margin-block">
    <% foreach (var (browser, version) in SupportedBrowsers) { %>
      <% if (version is null) continue; %>

      <div class="browser flex flex-column">
        <%= ImageTag($"browsers/{browser}.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "class", "center" } }) %>
        <div class="flex flex-column align-center margin-block-start-half">
          <strong><%= Campfire.RailsCompat.Ruby.RubyCase.Capitalize(browser) %></strong>
          <span> <%= version %>+</span>
        </div>
      </div>
    <% } %>
  </div>
</div>

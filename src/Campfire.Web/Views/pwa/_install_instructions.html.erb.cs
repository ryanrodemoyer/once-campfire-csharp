<% if (!(platform.IsChrome() || (platform.IsFirefox() && !platform.IsAndroid))) { %>
  <details class="notifications-help pwa__instructions hide-in-pwa" data-controller="pwa-install" data-pwa-install-prompting-class="pwa--can-install" data-notifications-target="details">
    <summary class="btn">
      <%= ImageTag("external/install.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
      <strong>Install Campfire as a web app.</strong>
      <%= ImageTag("disclosure.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 10 }, { "class", "disclosure" } }) %>
    </summary>

    <% if (platform.IsEdge()) { %>
        <ol>
          <li>Click <em><%= ImageTag("install-edge.svg", new() { { "alt", "the app available - install Campfire chat button" }, { "size", 16 } }) %></em>in the address bar.</li>
          <li>Click <em>Install</em>.</li>
        </ol>
      <% } else if (platform.IsChrome() && platform.IsAndroid) { %>
        <ol>
          <li>Tap the <em><%= ImageTag("menu-dots-vertical.svg", new() { { "alt", "More options" }, { "size", 16 } }) %></em> menu button.</li>
          <li>Tap <em>Install app</em> in the menu.</li>
        </ol>
      <% } else if (platform.IsFirefox() && platform.IsAndroid) { %>
        <ol>
          <li>Tap the <em><%= ImageTag("menu-dots-vertical.svg", new() { { "alt", "More options" }, { "size", 16 } }) %></em> menu button.</li>
          <li>Tap <em>Install</em> in the menu.</li>
        </ol>
      <% } else if (platform.IsSafari() && platform.IsDesktop) { %>
        <ol>
          <li>Click <em>File</em> in the top left.</li>
          <li>Click <em>Add to Dock…</em>.</li>
        </ol>
      <% } else if ((platform.IsSafari() || platform.IsChrome()) && platform.IsIos) { %>
        <p>To receive push notifications in <%= CapitalizedBrowser(platform) %> for <%= platform.OperatingSystem() %>, you must install Campfire as a web app.</p>
        <ol>
          <li>Tap <em><%= ImageTag("external/share.svg", new() { { "alt", "the share button" }, { "size", 20 } }) %></em></li>
          <li>Tap <em>Add to Home Screen</em>.</li>
        </ol>
      <% } else { %>
        <p>Some platforms require you to install Campfire as a web app to receive push notifications.</p>
    <% } %>

    <div class="margin-block-start txt-align-center pwa__installer">
      <hr class="separator margin-block">
      <button class="btn btn--reversed center" data-action="pwa-install#promptInstall">
        <%= ImageTag("external/install.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
        Install now
      </button>
    </div>
  </details>
<% } %>

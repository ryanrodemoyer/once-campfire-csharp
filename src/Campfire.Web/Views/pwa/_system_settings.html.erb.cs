<details class="notifications-help hide-in-browser" data-notifications-target="details">
  <summary class="btn">
    <%= ImageTag("external/gear.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
    <strong>Check your <%= platform.OperatingSystem() %> settings</strong>
    <%= ImageTag("disclosure.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 10 }, { "class", "disclosure" } }) %>
  </summary>

  <% if (platform.IsFirefox() && platform.IsAndroid) { %>
      <ol>
        <li>Tap the <em><%= ImageTag("menu-dots-vertical.svg", new() { { "alt", "More options" }, { "size", 16 } }) %></em> menu button.</li>
        <li>Tap <em>Settings</em>.</li>
        <li>Tap <em>Notifications</em>.</li>
        <li>Tap <em><%= ImageTag("external/switch.svg", new() { { "alt", "the toggle button" }, { "size", 22 } }) %></em> to <em>Allow <%= CapitalizedBrowser(platform) %> notifications</em>.</li>
      </ol>
    <% } else if (platform.IsEdge() && platform.IsDesktop) { %>
      <ol>
        <li>Click <em>Start</em>, then <em>Settings</em>.</li>
        <li>Go to <em>System &gt; Notification</em>.</li>
        <li>Click <em><%= ImageTag("external/switch.svg", new() { { "alt", "the toggle button" }, { "size", 22 } }) %></em> <em>ON</em> for Campfire.</li>
      </ol>
    <% } else if ((platform.IsFirefox() || platform.IsChrome()) && platform.IsDesktop) { %>
      <ol>
        <% if (platform.IsWindows()) { %>
          <li>Click <em>Start</em>, then <em>Settings</em>.</li>
          <li>Go to <em>System &gt; Notification</em>.</li>
          <li>Click <em><%= ImageTag("external/switch.svg", new() { { "alt", "the toggle button" }, { "size", 22 } }) %></em> <em>ON</em> for Campfire.</li>
        <% } else { %>
          <li>Click <em aria-label="the Apple menu"></em> in the top left.</li>
          <li>Click <em>System Settings…</em>.</li>
          <li>Click <em>Notifications</em>.</li>
          <li>Click <em>Campfire</em>.</li>
          <li>Click <em><%= ImageTag("external/switch.svg", new() { { "alt", "the allow notifications switch" }, { "size", 22 } }) %></em> to <em>Allow notifications</em>.</li>
        <% } %>
      </ol>
    <% } else if (platform.IsSafari() && platform.IsDesktop) { %>
      <ol>
        <li>Click <em aria-label="the Apple menu"></em> in the top left.</li>
        <li>Click <em>System Settings…</em>.</li>
        <li>Click <em>Notifications</em>.</li>
        <li>Click <em>Campfire</em>.</li>
        <li>Click <em><%= ImageTag("external/switch.svg", new() { { "alt", "the allow notifications switch" }, { "size", 22 } }) %></em> to <em>Allow notifications</em>.</li>
      </ol>
    <% } else if ((platform.IsSafari() || platform.IsChrome()) && platform.IsIos) { %>
      <ol>
        <li>Open the <em><%= ImageTag("external/gear.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %></em> Settings app.</li>
        <li>Scroll to and tap <em>Campfire</em>.</li>
        <li>Tap <em>Notifications</em>.</li>
        <li>Tap <em><%= ImageTag("external/switch.svg", new() { { "alt", "the allow notifications switch button" }, { "size", 22 } }) %></em> to <em>Allow Notifications</em>.</li>
      </ol>
    <% } else if (platform.IsChrome() && platform.IsAndroid) { %>
      <ol>
        <li>Open the <em><%= ImageTag("external/gear.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %></em> Settings app.</li>
        <li>Tap <em>Notifications</em>.</li>
        <li>Tap <em>App notifications</em>.</li>
        <li>Scroll to <em>Campfire</em>.</li>
        <li>Tap <em><%= ImageTag("external/switch.svg", new() { { "alt", "the switch" }, { "size", 22 } }) %></em> to <em>Allow Notifications</em>.</li>
      </ol>
    <% } else { %>
      <p>Ensure notifications are allowed for <%= CapitalizedBrowser(platform) %> in your system settings.</p>
  <% } %>
</details>

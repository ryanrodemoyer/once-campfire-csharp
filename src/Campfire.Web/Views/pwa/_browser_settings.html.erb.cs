<% if (!((platform.IsSafari() || platform.IsChrome()) && platform.IsIos)) { %>
  <details class="notifications-help" data-notifications-target="details">
    <summary class="btn">
      <%= ImageTag("external/web.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 } }) %>
      <strong>Check your <%= CapitalizedBrowser(platform) %> settings</strong>
      <%= ImageTag("disclosure.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 10 }, { "class", "disclosure" } }) %>
    </summary>

    <% if (platform.IsFirefox() && platform.IsAndroid) { %>
        <ol>
          <li>Tap <em><%= ImageTag("lock.svg", new() { { "alt", "the View site information button" }, { "size", 20 } }) %></em> in the address bar.</li>
          <li>Tap <em>Notification</em> to change to <em>Allowed</em>.</li>
        </ol>
      <% } else if (platform.IsEdge() && platform.IsDesktop) { %>
        <h2 class="txt-normal txt-medium margin-block-start">Turn on notifications for this website.</h2>
        <ol>
          <li>Click <em><%= ImageTag("lock.svg", new() { { "alt", "the View site information button" }, { "size", 20 } }) %></em> left of the address bar.</li>
          <li>Under <em>Permissions for this site &gt; Notifications</em>, choose <em>Allow</em>.</li>
        </ol>
        <h2 class="txt-normal txt-medium margin-block-start">Turn on notifications for <%= CapitalizedBrowser(platform) %>.</h2>
        <ol>
          <% if (platform.IsWindows()) { %>
            <li>Click <em>Start</em>, then <em>Settings</em>.</li>
            <li>Go to <em>System &gt; Notification</em>.</li>
            <li>Click <em><%= ImageTag("external/switch.svg", new() { { "alt", "the switch" }, { "size", 22 } }) %></em> <em>ON</em> for <%= CapitalizedBrowser(platform) %>.</li>
          <% } else { %>
            <li>Click <em aria-label="the Apple menu"></em> in the top left.</li>
            <li>Click <em>System Settings…</em>.</li>
            <li>Click <em>Notifications</em>.</li>
            <li>Click <em><%= CapitalizedBrowser(platform) %></em>.</li>
            <li>Click <em><%= ImageTag("external/switch.svg", new() { { "alt", "the switch" }, { "size", 22 } }) %></em> to <em>Allow notifications</em>.</li>
          <% } %>
        </ol>
      <% } else if (platform.IsFirefox() && platform.IsDesktop) { %>
        <h2 class="txt-normal txt-medium margin-block-start">Turn on notifications for this website.</h2>
        <ol>
          <li>Click <em><%= CapitalizedBrowser(platform) %></em> in the top left.</li>
          <li>Click <em>Settings…</em>.</li>
          <li>Click <em>Privacy & Security</em> in the sidebar.</li>
          <li>Scroll down to <em>Permissions</em>.</li>
          <li>Click <em>Settings</em> next to <em>Notifications</em>.</li>
          <li>Select <em>Allow</em> next to <em><%= Routes.RootUrl(Origin) %></em>.</li>
        </ol>

        <h2 class="txt-normal txt-medium margin-block-start">Turn on notifications for <%= CapitalizedBrowser(platform) %>.</h2>
        <ol>
          <% if (platform.IsWindows()) { %>
            <li>Click <em>Start</em>, then <em>Settings</em>.</li>
            <li>Go to <em>System &gt; Notification</em>.</li>
            <li>Click <em><%= ImageTag("external/switch.svg", new() { { "alt", "the toggle button" }, { "size", 22 } }) %></em> <em>ON</em> for <%= CapitalizedBrowser(platform) %>.</li>
          <% } else { %>
            <li>Click <em aria-label="the Apple menu"></em> in the top left.</li>
            <li>Click <em>System Settings…</em>.</li>
            <li>Click <em>Notifications</em>.</li>
            <li>Click <em><%= CapitalizedBrowser(platform) %></em>.</li>
            <li>Click <em><%= ImageTag("external/switch.svg", new() { { "alt", "the switch" }, { "size", 22 } }) %></em> to <em>Allow notifications</em>.</li>
          <% } %>
        </ol>
      <% } else if (platform.IsChrome() && platform.IsDesktop) { %>
        <h2 class="txt-normal txt-medium margin-block-start">Turn on notifications for this website.</h2>
        <ol>
          <li>Click the <em><%= ImageTag("external/sliders.svg", new() { { "alt", "View site information" }, { "size", 20 } }) %></em> icon in the address bar.</li>
          <li>Click <em>Site Settings</em>.</li>
          <li>Ensure notifications are <em>Allowed</em>.</li>
        </ol>

        <h2 class="txt-normal txt-medium margin-block-start">Turn on notifications for <%= CapitalizedBrowser(platform) %>.</h2>
        <ol>
          <% if (platform.IsWindows()) { %>
            <li>Click <em>Start</em>, then <em>Settings</em>.</li>
            <li>Go to <em>System &gt; Notification</em>.</li>
            <li>Click <em><%= ImageTag("external/switch.svg", new() { { "alt", "the switch" }, { "size", 22 } }) %></em> <em>ON</em> for <%= CapitalizedBrowser(platform) %>.</li>
          <% } else { %>
            <li>Click <em aria-label="the Apple menu"></em> in the top left.</li>
            <li>Click <em>System Settings…</em>.</li>
            <li>Click <em>Notifications</em>.</li>
            <li>Click <em><%= CapitalizedBrowser(platform) %></em>.</li>
            <li>Click <em><%= ImageTag("external/switch.svg", new() { { "alt", "the switch" }, { "size", 22 } }) %></em> to <em>Allow notifications</em>.</li>
          <% } %>
        </ol>
      <% } else if (platform.IsChrome() && platform.IsAndroid) { %>
        <ol>
          <li>Tap the <em><%= ImageTag("menu-dots-vertical.svg", new() { { "alt", "More options" }, { "size", 16 } }) %></em> menu button.</li>
          <li>Tap <em>Settings</em>.</li>
          <li>Tap <em>Notifications</em>.</li>
          <li>Tap <em><%= ImageTag("external/switch.svg", new() { { "alt", "the switch" }, { "size", 22 } }) %></em> to <em>Allow <%= CapitalizedBrowser(platform) %> notifications</em>.</li>
          <li>Tap <em><%= ImageTag("external/switch.svg", new() { { "alt", "the switch" }, { "size", 22 } }) %></em> next to <em>Web apps</em>.</li>
          <li>Tap <em><%= ImageTag("notification-bell-alert.svg", new() { { "alt", "the notification bell" }, { "size", 16 } }) %></em> and select <em>Allow</em>.</li>
        </ol>
      <% } else if (platform.IsSafari() && platform.IsDesktop) { %>
        <ol>
          <li>Click <em><%= CapitalizedBrowser(platform) %></em> in the top left.</li>
          <li>Click <em>Settings…</em>.</li>
          <li>Click the <em>Websites</em> tab.</li>
          <li>Click <em>Notifications</em> in the sidebar.</li>
          <li>Click <em><%= Routes.RootUrl(Origin) %></em> in the list.</li>
          <li>Select <em>Allow</em>.</li>
        </ol>
      <% } else { %>
        <p>Ensure notifications are enabled for <em><%= Routes.RootUrl(Origin) %></em> in your web browser settings.</p>
    <% } %>
  </details>
<% } %>

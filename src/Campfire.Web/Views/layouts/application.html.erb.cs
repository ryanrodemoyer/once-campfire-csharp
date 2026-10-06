<!DOCTYPE html>
<html>
  <head>
    <%= PageTitleTag() %>

    <meta name="viewport" content="width=device-width, initial-scale=1, user-scalable=no, interactive-widget=resizes-content">
    <meta name="view-transition" content="same-origin">
    <meta name="color-scheme" content="light dark">
    <meta name="theme-color" content="#ffffff" media="(prefers-color-scheme: light)">
    <meta name="theme-color" content="#000000" media="(prefers-color-scheme: dark)">
    <meta name="apple-mobile-web-app-capable" content="yes">
    <%= CsrfMetaTags() %>
    <%= CspMetaTag() %>
    <%= CurrentUserMetaTags() %>
    <%= ScriptAwareActionCableMetaTag() %>

    <%= Tag.Meta(new() { { "name", "vapid-public-key" }, { "content", VapidPublicKey } }) %>
    <%= Tag.Meta(new() { { "name", "turbo-prefetch" }, { "content", "true" } }) %>

    <%= Tag.Link(new() { { "rel", "manifest" }, { "href", Routes.WebmanifestPath(new() { { "format", "json" } }) } }) %>
    <%= Tag.Link(new() { { "rel", "icon" }, { "href", Routes.FreshAccountLogoPath(CurrentAccount?.UpdatedAt) }, { "type", "image/png" } }) %>
    <%= Tag.Link(new() { { "rel", "apple-touch-icon" }, { "href", Routes.FreshAccountLogoPath(CurrentAccount?.UpdatedAt) } }) %>

    <%= StylesheetLinkTagAll(new() { { "data-turbo-track", "reload" } }) %>
    <%= CustomStylesTag() %>

    <%= JavascriptImportmapTags() %>

    <%= Yield("head") %>
  </head>

  <body class="<%= BodyClasses() %>" data-controller="local-time lightbox">
    <a href="#main-content" class="skip-navigation btn">Skip to main content</a>

    <nav id="nav">
      <%= Yield("nav") %>
    </nav>

    <% if ((Flash.Notice ?? Flash.Alert) is { } notice) { %>
      <div class="flash" data-controller="element-removal" data-action="animationend->element-removal#remove">
        <div class="flash__inner shadow" style="<%= Flash.Alert is not null ? "--flash-background: var(--color-negative)" : null %>">
          <% if (Flash.Alert is not null) { %>
            <%= ImageTag("alert.svg", new() { { "aria", new HtmlOptions { { "hidden", true } } }, { "size", 24 }, { "class", "colorize--white" } }) %></span>
          <% } else { %>
            <%= ImageTag("check.svg", new() { { "aria", new HtmlOptions { { "hidden", true } } }, { "size", 24 }, { "class", "colorize--white" } }) %></span>
          <% } %>
        </div>
        <span class="for-screen-reader" role="alert" aria-atomic="true"><%= notice %></span>
      </div>
    <% } %>

    <main id="main-content">
      <%= body %>

      <footer id="footer">
        <%= Yield("footer") %>
      </footer>
    </main>

    <aside id="sidebar" data-controller="toggle-class" data-toggle-class-toggle-class="open">
      <%= Yield("sidebar") %>
    </aside>

    <%= Render(Lightbox) %>

    <a href="https://once.com" id="app-logo" target="_blank" aria-label="Once software from 37signals home page">
      <%= ImageTag("campfire-icon.png", new() { { "alt", "Campfire logo" }, { "width", 256 }, { "height", 216 } }) %>
    </a>
  </body>
</html>

<% PageTitle = "No rooms yet"; %>
<% BodyClass = "sidebar"; %>

<% ContentFor("sidebar", SidebarTurboFrameTag(src: Routes.UserSidebarPath())); %>

<div id="message-area" class="message-area">
  <div class="message-area--empty min-width center">
    <figure class="center pad">
      <%= ImageTag("messages-empty.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "class", "colorize--black translucent" } }) %>
      <span class="for-screen-reader"><%= userName %></span>
    </figure>
  </div>
</div>

<%= TurboFrameTag("next_page_container", new() { { "loading", "lazy" }, { "class", "flex center" } }, () => { %>
  <div class="spinner center"></div>
<% }, src: Routes.AccountUsersPath(new() { { "page", page }, { "format", "turbo_stream" } })) %>

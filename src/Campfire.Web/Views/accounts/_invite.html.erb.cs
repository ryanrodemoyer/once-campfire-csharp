<div class="flex flex-column align-center gap">
  <% var url = Routes.JoinUrl(Origin, joinCode); %>

  <label class="flex flex-column gap full-width" style="--row-gap: 0.5em">
    <strong id="invite_label" class="invite-label">Share to invite more people</strong>
    <span class="flex align-center gap input input--actor fill-white">
      <%= ImageTag("person-add.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 }, { "class", "colorize--black" } }) %>
      <input type="text" class="input" id="invite_url" value="<%= url %>" aria-labelledby="invite_label" readonly>
    </span>
  </label>

  <div class="flex align-center gap">
    <%= LinkToZoomQrCode(url, () => { %>
      <span class="for-screen-reader">Show join link QR code</span>
      <%= ImageTag("qr-code.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 }, { "class", "colorize--black" } }) %>
    <% }) %>

    <%= ButtonToCopyToClipboard(url, () => { %>
      <span class="for-screen-reader">Copy join link</span>
      <%= ImageTag("copy-paste.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 }, { "class", "colorize--black" } }) %>
    <% }) %>

    <%= WebShareSessionButton(url, "Link to join Campfire", "Hit this link to join me in Campfire and start chatting.", () => { %>
      <span class="for-screen-reader">Share join link</span>
      <%= ImageTag("share.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 }, { "class", "colorize--black" } }) %>
    <% }) %>

    <% if (CurrentUser!.CanAdminister) { %>
      <%= ButtonTo(Routes.AccountJoinCodePath(), new() { { "class", "btn btn--regenerate" } }, () => { %>
        <%= ImageTag("refresh.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 }, { "class", "colorize--black" } }) %>
        <span class="for-screen-reader">Regenerate join link</span>
      <% }) %>
    <% } %>
  </div>
</div>

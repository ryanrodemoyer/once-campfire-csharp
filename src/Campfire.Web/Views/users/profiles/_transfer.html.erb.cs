<% var url = transferUrl; %>

<fieldset>
  <legend class="gap">
    <%= ImageTag("laptop.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 36 }, { "class", "colorize--black" } }) %>
    <%= ImageTag("transfer.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 36 }, { "class", "colorize--black" } }) %>
    <%= ImageTag("mobile-phone.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 36 }, { "class", "colorize--black" } }) %>
  </legend>


  <div class="flex flex-column gap">
    <% if (CurrentUser!.Id != userId) { %>
      <div class="flex align-center gap justify-center">
        <%= ImageTag("crown.svg", new() { { "size", 16 }, { "aria", new HtmlOptions { { "hidden", "true" } } }, { "class", "flex-item-no-shrink colorize--black" } }) %>
        <label for="session_transfer_url">Share to get them back into their account</label>
      </div>
    <% } else { %>
      <label for="session_transfer_url" class="for-screen-reader">Use this link to login automatically on another device</label>
    <% } %>

    <input type="text" class="input" value="<%= url %>" id="session_transfer_url" readonly>

    <div class="flex align-center center gap">
      <%= LinkToZoomQrCode(url, () => { %>
        <span class="for-screen-reader">Show auto-login QR code</span>
        <%= ImageTag("qr-code.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 }, { "class", "colorize--black" } }) %>
      <% }) %>

      <%= ButtonToCopyToClipboard(url, () => { %>
        <span class="for-screen-reader">Copy auto-login link</span>
        <%= ImageTag("copy-paste.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 }, { "class", "flex-item-no-shrink colorize--black" } }) %>
      <% }) %>

      <%= WebShareSessionButton(url, "Your sign-in link", "This is your own private sign-in URL, DO NOT SHARE IT. Use it to sign-in on another device or if you get locked out.", () => { %>
        <span class="for-screen-reader">Share auto-login link</span>
        <%= ImageTag("share.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } }, { "size", 20 }, { "class", "flex-item-no-shrink colorize--black" } }) %>
      <% }) %>
    </div>
  </div>
</fieldset>

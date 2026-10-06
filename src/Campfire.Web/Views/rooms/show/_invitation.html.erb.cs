<% if (page.ShowsInvitation) { %>
  <div id="system_welcome" class="message message--formatted txt-align-center center">
    <div class="message__body center">
      <div class="message__body-content position-relative">
        <%= AccountLogoTag(style: "center margin-block-end txt-large") %>
        <div class="flex align-center gap">
          <div class="system-welcome--translation">
            <%= TranslationButton("invite_message") %>
          </div>
          <p>
            <strong>Welcome to Campfire</strong><br>
            To invite people to chat, share the join link below.
          </p>
        </div>
        <%= Render(o => AccountsInvite(o, page.JoinCode)) %>
      </div>
    </div>
  </div>
<% } %>

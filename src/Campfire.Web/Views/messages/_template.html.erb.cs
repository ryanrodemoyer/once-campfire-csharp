<script type="text/template" data-messages-target="template">
  <div class="message message--me $messageClasses$"
      id="message_$clientMessageId$"
      data-format-message-target="message"
      data-user-id="<%= user.Id %>"
      data-message-timestamp="$messageTimestamp$"
      data-messages-target="message">
    <div class="message__day-separator"><time class="message__timestamp" datetime="$messageDatetime$" data-local-time-target="date"></time></div>

    <figure class="avatar message__avatar">
      <%= AvatarTag(user.Avatar) %>
    </figure>

    <div class="message__body">
      <div class="message__body-content">
        <div class="message__meta">
          <h3 class="message__heading">
            <span class="message__author"><strong><%= user.Name %></strong></span>
            <span class="message__permalink"><time class="message__timestamp" datetime="$messageDatetime$" data-local-time-target="time"></time></span>
          </h3>
          <div class="message__actions">
            <div class="position-relative">
              <span class="btn message__action-btn message__options-btn">
                <%= ImageTag("menu-dots-horizontal.svg", new() { { "class", "colorize--black" }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
                <span class="for-screen-reader">Message options</span>
              </span>
            </div class="position-relative">
          </div>
        </div>
        $body$
      </div>
    </div>
  </div>
</script>

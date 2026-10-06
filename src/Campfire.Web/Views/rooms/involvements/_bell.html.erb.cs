<span>
  <span class="button_to_change_notifying"
      data-controller="notifications" data-notifications-subscriptions-url-value="<%= Routes.UserPushSubscriptionsPath() %>" data-notifications-attention-class="btn--pulsing">
    <%= TurboFrameTag(RecordIdentifier.DomId(new RecordKey(room.Type.ClassName(), room.Id), "involvement"), new() { { "data", new HtmlOptions {
          { "controller", "turbo-frame" }, { "action", "notifications:ready@window->turbo-frame#load" }, { "turbo_frame_url_param", Routes.RoomInvolvementPath(room.Id) } } } }, () => { %>
      <button class="btn" data-action="click->notifications#attemptToSubscribe" data-notifications-target="bell">
        <%= ImageTag("notification-bell-loading.svg", new() { { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
        <%= ImageTag("notification-bell-alert.svg", new() { { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } }, { "hidden", true } }) %>
        <span class="for-screen-reader">Notification settings for this <%= room.IsDirect ? "Ping" : "room" %></span>
      </button>
    <% }) %>

    <dialog data-notifications-target="notAllowedNotice" class="dialog pad center center-block border-radius border shadow" style="--inline-space: var(--block-space)">
      <div class="flex flex-column txt-align-center">
        <span class="btn btn--faux center txt-x-large">
          <%= ImageTag("notification-bell-alert.svg", new() { { "size", 48 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
          <span class="for-screen-reader">Notifications alert</span>
        </span>

        <section>
          <h1 class="txt-large margin-none">Notifications aren’t allowed</h1>
          <div class="txt-align-start margin-block-start">
            <%= Render(o => PwaBrowserSettings(o, platform)) %>
            <%= Render(o => PwaSystemSettings(o, platform)) %>
            <%= Render(o => PwaInstallInstructions(o, platform)) %>
          </div>
        </section>

        <form method="dialog" class="flex align-center gap center">
          <button class="btn dialog__close" autofocus="true">
            <span class="for-screen-reader">Close</span>
            <%= ImageTag("remove.svg", new() { { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
          </button>
        </form>
      </div>
    </dialog>
  </span>
</span>

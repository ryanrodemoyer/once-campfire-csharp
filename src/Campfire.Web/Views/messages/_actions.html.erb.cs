<%# Be sure to check/update messages/_template.html.erb when changing this file %>

<div class="message__actions" data-controller="soft-keyboard">
  <%= Tag.Details(new() { { "class", "position-relative" },
      { "data", new HtmlOptions { { "controller", "popup" }, { "action", "keydown.esc->popup#close toggle->popup#toggle click@document->popup#closeOnClickOutside" }, { "popup_orientation_top_class", "popup-orientation-top" } } } }, () => { %>
    <summary class="btn message__action-btn message__options-btn">
      <%= ImageTag("menu-dots-horizontal.svg", new() { { "size", 20 }, { "class", "colorize--black" }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
      <span class="for-screen-reader">Message options</span>
    </summary>

    <div class="message__actions-menu border shadow" data-popup-target="menu">
      <div class="quick-boosts">
        <% foreach (var (character, title) in Reactions) { %>
          <%= FormWith(FormModel.New("Boost"), Routes.MessageBoostsPath(message.Id), new() { { "data", new HtmlOptions { { "turbo_frame", RecordIdentifier.DomId(message.Record, "boosting") }, { "action", "popup#close" } } } }, form => { %>
            <%= HiddenFieldTag("boost[content]", character) %>
            <%= form.Button(new() { { "type", "submit" }, { "title", title }, { "class", "btn message__action-btn" }, { "data", new HtmlOptions { { "emoji", character } } } }, () => { %>
              <figure class="margin-none boost-character"><%= character %></figure>
              <span class="for-screen-reader"><%= title %></span>
            <% }) %>
          <% }) %>
        <% } %>

        <%= LinkTo(Routes.NewMessageBoostPath(message.Id),
              new() { { "class", "btn message__action-btn message__boost-btn" },
              { "data", new HtmlOptions { { "turbo_frame", RecordIdentifier.DomId(message.Record, "new_boost") }, { "action", "soft-keyboard#open popup#close" } } } }, () => { %>
          <%= ImageTag("boost.svg", new() { { "class", "colorize--black" }, { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
          <span class="for-screen-reader">New boost</span>
        <% }) %>
      </div>

      <div class="flex flex-wrap border-top margin-block-start-half pad-block-start-half message__actions-grid">
        <% if (message.ContentType == "attachment") { %>
          <%= LinkTo(BlobUrlsForAttachments.BlobRedirectPath(message.Attachment!, "attachment"), new() { { "class", "btn message__action-btn center full-width hide-in-ios-pwa" }, { "title", "Download" }, { "aria", new HtmlOptions { { "label", "Download" } } } }, () => { %>
            <%= ImageTag("download.svg", new() { { "class", "colorize--black" }, { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
          <% }) %>

          <%= Tag.Button(new() { { "class", "btn message__action-btn center full-width" },
              { "data", new HtmlOptions { { "controller", "web-share" }, { "action", "web-share#share" }, { "web_share_files_value", BlobUrlsForAttachments.BlobRedirectPath(message.Attachment!) }, { "web_share_title_value", message.Attachment!.Filename.ToString() } } }, { "title", "Share" }, { "aria", new HtmlOptions { { "label", "Share" } } } }, () => { %>
            <%= ImageTag("share.svg", new() { { "class", "colorize--black" }, { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
          <% }) %>
        <% } else { %>
          <%= Tag.Button(new() { { "class", "btn message__action-btn center full-width" }, { "data", new HtmlOptions { { "action", "reply#reply" } } }, { "title", "Reply" }, { "aria", new HtmlOptions { { "label", "Reply" } } } }, () => { %>
            <%= ImageTag("reply.svg", new() { { "class", "colorize--black" }, { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
          <% }) %>
        <% } %>

        <%= Tag.Button(new() { { "class", "btn message__action-btn center full-width" }, { "title", "Copy link" }, { "aria", new HtmlOptions { { "label", "Copy link" } } }, { "data", new HtmlOptions { { "controller", "copy-to-clipboard" }, { "action", "copy-to-clipboard#copy" }, { "copy_to_clipboard_success_class", "btn--success" }, { "copy_to_clipboard_content_value", url } } } }, () => { %>
          <%= ImageTag("link.svg", new() { { "class", "colorize--black" }, { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
        <% }) %>

        <%= LinkTo(Routes.EditRoomMessagePath(message.Room.Id, message.Id), new() { { "class", "btn message__action-btn center full-width message__edit-btn" },
              { "data", new HtmlOptions { { "turbo_frame", RecordIdentifier.DomId(message.Record, "edit") } } }, { "title", "Edit" }, { "aria", new HtmlOptions { { "label", "Edit" } } } }, () => { %>
          <%= ImageTag("pencil.svg", new() { { "class", "colorize--black" }, { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
        <% }) %>
      </div>
    </div>
  <% }) %>
</div>

<turbo-frame id="<%= RecordIdentifier.DomId(message.Record, "edit") %>">
  <div class="message__body position-relative" data-controller="scroll-into-view">
    <div class="message__body-content message__body-content--editing gap">
      <% if (message.ContentType == "attachment") { %>
        <%= MessageAttachmentPresentation(message) %>

        <div class="message__edit-btns flex align-center justify-space-between gap full-width pad-block-start-half">
          <%= ButtonTag(new() { { "class", "btn btn--negative center margin-block-end" }, { "type", "submit" },
                { "form", RecordIdentifier.DomId(message.Record, "delete_form") }, { "data", new HtmlOptions { { "turbo_confirm", "Are you sure you want to delete this message?" } } } }, () => { %>
            <%= ImageTag("trash.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
            <span class="for-screen-reader">Delete message</span>
          <% }) %>
        </div>
      <% } else { %>
        <div class="composer--edit composer--rich-text">
          <%= FormWith(new FormModel(message.Record, isPersisted: true), Routes.RoomMessagePath(message.Room.Id, message.Id), new() { { "id", RecordIdentifier.DomId(message.Record, "form") }, { "data", new HtmlOptions {
                { "controller", "form" }, { "action", "lexxy:file-accept->form#preventAttachment keydown.esc->form#cancel keydown.ctrl+enter->form#submit:prevent keydown.meta+enter->form#submit:prevent" } } } }, form => { %>
            <div class="full-width input input--actor min-width fill-white">
              <%= LexxyRichTextArea(form, "body", message, new() {
                    { "value", EditorValue(message) },
                    { "rows", 1 },
                    { "class", "input lexxy-content" },
                    { "aria", new HtmlOptions { { "multiline", "true" }, { "label", "Edit message" } } },
                    { "autofocus", true },
                    { "permitted-attachment-types", "application/vnd.campfire.mention application/vnd.actiontext.opengraph-embed" },
                    { "data", new HtmlOptions { { "action", RichTextDataActions() } } } }, () => { %>
                <%= MentionPromptTag(message.Room.Id) %>
              <% }) %>
            </div>

            <%= LinkTo("Close editor and discard changes", Routes.RoomMessagePath(message.Room.Id, message.Id), new() { { "data", new HtmlOptions { { "form_target", "cancel" } } }, { "hidden", true } }) %>

            <div class="message__edit-btns flex align-center justify-space-between gap full-width pad-block-start-half">
              <%= ButtonTag(new() { { "type", "submit" }, { "class", "btn btn--reversed" } }, () => { %>
                <%= ImageTag("check.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
                <span class="for-screen-reader">Save changes</span>
              <% }) %>

              <%= ButtonTag(new() { { "class", "btn btn--negative" }, { "type", "submit" }, { "form", RecordIdentifier.DomId(message.Record, "delete_form") },
                      { "data", new HtmlOptions { { "turbo_confirm", "Are you sure you want to delete this message?" } } } }, () => { %>
                <%= ImageTag("trash.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
                <span class="for-screen-reader">Delete message</span>
              <% }) %>
            </div>
          <% }) %>
        </div>
      <% } %>
    </div>

    <div class="message__actions flex flex-wrap">
      <%= LinkTo(Routes.RoomMessagePath(message.Room.Id, message.Id), new() { { "class", "message__action-btn message__edit-close-btn txt-small btn btn--borderless" } }, () => { %>
        <%= ImageTag("remove.svg", new() { { "class", "colorize--black" }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
        <span class="for-screen-reader">Close editor and discard changes</span>
      <% }) %>
    </div>

    <%= FormWith(null, Routes.RoomMessagePath(message.Room.Id, message.Id), new() { { "method", "delete" }, { "id", RecordIdentifier.DomId(message.Record, "delete_form") }, { "data", new HtmlOptions { { "turbo_frame", RecordIdentifier.DomId(message.Record, "edit") } } } }) %>
  </div>
</turbo-frame>

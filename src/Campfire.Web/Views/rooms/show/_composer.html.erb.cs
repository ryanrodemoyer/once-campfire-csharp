<% ContentFor(w, "footer", () => { %>
  <div class="composer flex align-end gap position-relative"
      data-controller="typing-notifications" data-typing-notifications-active-class="typing-indicator--active">
    <%= LinkTo(Routes.SearchesPath(), new() { { "class", "btn flex-item-no-shrink margin-block-end composer__context-btn" }, { "style", "view-transition-name: input-switcher" } }, () => { %>
      <%= ImageTag("search.svg", new() { { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
      <span class="for-screen-reader">Search</span>
    <% }) %>

    <turbo-frame id="composer-frame">
      <%= ComposerFormTag(page.Room.Id, form => { %>
        <fieldset data-composer-target="fields" contents>
          <div class="flex flex-column">
            <div class="composer__filelist flex flex--align-center gap flex-wrap" data-composer-target="fileList"></div>

            <div class="flex composer__input input input--actor fill-white min-width" style="--input-border-radius: 1.3rem">
              <div class="flex align-end gap full-width">
                <%= ImageTag("messages-outlined.svg", new() { { "size", 22 }, { "aria", new HtmlOptions { { "hidden", "true" } } }, { "class", "composer__input-hint colorize--black" }, { "style", "view-transition-name: input-btn;" } }) %>

                <div class="flex flex-column flex-item-grow min-width gap">
                  <%= ComposerRichTextArea(form, "body",
                        new()
                        {
                          { "rows", 1 },
                          { "class", "input lexxy-content" },
                          { "style", "order: -1" },
                          { "aria", new HtmlOptions { { "multiline", "true" }, { "label", "Write a message" } } },
                          { "permitted-attachment-types", "application/vnd.campfire.mention application/vnd.actiontext.opengraph-embed" },
                          { "data", new HtmlOptions {
                            { "controller", "unfurl" },
                            { "action", $"{RichTextDataActions()} lexxy:change->composer#saveDraft lexxy:insert-link->unfurl#unfurl" },
                            { "composer_target", "text" } } },
                        }, () => { %>
                    <%= MentionPromptTag(page.Room.Id) %>
                  <% }) %>
                </div>

                <label class="btn btn--borderless txt-small flex-item-no-shrink composer__attachment-btn input--file">
                  <%= ImageTag("attachment.svg", new() { { "size", 22 }, { "class", "colorize--black" }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
                  <input type="file" data-action="composer#filePicked" multiple />
                  <span class="for-screen-reader">Attach a file</span>
                </label>

                <button class="btn btn--borderless txt-small flex-item-no-shrink composer__rich-text-btn" type="button" data-action="composer#toggleToolbar">
                  <%= ImageTag("text-options.svg", new() { { "size", 20 }, { "class", "colorize--black" }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
                  <span class="for-screen-reader">Rich text</span>
                </button>

                <%= form.Button(new() { { "name", "send" }, { "type", "submit" }, { "data", new HtmlOptions { { "action", "composer#submit" } } },
                      { "class", "btn btn--reversed flex-item-no-shrink txt-small" } }, () => { %>
                  <%= ImageTag("arrow-up.svg", new() { { "size", 20 }, { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
                  <span class="for-screen-reader">Send Message</span>
                <% }) %>
              </div>
            </div>
          </div>
        </fieldset>

        <div class="typing-indicator gap txt-small align-center flex-inline" data-typing-notifications-target="indicator">
          <div class="typing-indicator__author spinner" data-typing-notifications-target="author"></div>
        </div>

        <%= form.HiddenField("client_message_id", new() { { "data", new HtmlOptions { { "composer_target", "clientid" } } } }) %>
      <% }) %>
    </turbo-frame>
  </div>
<% }); %>

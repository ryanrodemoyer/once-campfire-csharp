<%= TurboFrameTag(RecordIdentifier.DomId(message.Record, "new_boost"), null, () => { %>
  <div class="boost flex-inline postion--relative max-width fill-white" style="--column-gap: var(--inline-space-half)">
    <%= FormWith(FormModel.New("Boost"), Routes.MessageBoostsPath(message.Id), new() { { "class", "boost__form flex align-center gap expanded" },
          { "data", new HtmlOptions { { "controller", "form scroll-into-view" }, { "turbo_frame", RecordIdentifier.DomId(message.Record, "boosting") }, { "action", "keydown.esc->form#cancel" } } } }, form => { %>
      <label class="boost__form-label flex gap" style="--column-gap: 0.7ch;" role="button" tabindex="0" aria-label="Add a boost">
        <figure class="avatar boost__avatar flex-item-no-shrink">
          <%= AvatarTag(user.Avatar) %>
          <span class="for-screen-reader"><%= user.Name %></span>
        </figure>

        <%= form.TextField("content", new() { { "id", null }, { "autofocus", true }, { "autocomplete", "off" }, { "autocorrect", "off" }, { "maxlength", 16 },
              { "required", true }, { "pattern", @"\S+.*" }, { "data", new HtmlOptions { { "boost_form_target", "input" } } }, { "class", "input input--boost txt-small" } }) %>
      </label>

      <%= form.Button(new() { { "class", "btn btn--reversed" }, { "type", "submit" } }, () => { %>
        <%= ImageTag("check.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
        <span class="for-screen-reader">Submit</span>
      <% }) %>

      <%= LinkTo(Routes.MessageBoostsPath(message.Id), new() { { "data", new HtmlOptions { { "turbo_frame", RecordIdentifier.DomId(message.Record, "boosts") }, { "form_target", "cancel" } } }, { "class", "btn btn--negative" } }, () => { %>
        <%= ImageTag("minus.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
        <span class="for-screen-reader">Cancel</span>
      <% }) %>
    <% }) %>
  </div>
<% }) %>

<turbo-frame id="direct_rooms_control" target="_top">
  <div class="directs directs--new flex flex-column gap">
    <%= FormWith(FormModel.New(Campfire.Data.Records.RoomTypes.DirectClassName), Routes.RoomsDirectsPath(), new() { { "class", "flex gap flex-item-grow" }, { "data", new HtmlOptions {
          { "controller", "form" }, { "action", "keydown.esc->form#cancel" } } } }, form => { %>
      <%= LinkTo(Routes.UserSidebarPath(), new() { { "class", "btn flex-item-no-shrink" }, { "data", new HtmlOptions { { "turbo_frame", "user_sidebar" }, { "form_target", "cancel" } } } }, () => { %>
        <%= ImageTag("arrow-left.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
        <span class="for-screen-reader">Cancel changes</span>
      <% }) %>

      <section class="autocomplete__container unpad input input--actor">
        <div class="autocomplete__input input flex flex-wrap position-relative flex-item-grow"
            data-controller="autocomplete" data-autocomplete-url-value="<%= Routes.AutocompletableUsersPath() %>">
          <select name="user_ids[]" data-autocomplete-target="select" data-template-id="autocompletable-user" multiple="true" hidden required></select>

          <%= Render(o => UsersAutocompletablesTemplateStandIn(o)) %>

          <%= form.TextField("user_ids_input", new() {
                { "autocomplete", "off" }, { "autocorrect", "off" }, { "data-1p-ignore", "true" }, { "class", "autocomplete__input input flex flex-wrap position-relative" },
                { "data", new HtmlOptions { { "autocomplete_target", "input" }, { "action", "input->autocomplete#search keydown->autocomplete#didPressKey" } } } }) %>
        </div>
      </section>

      <%= ButtonTag(new() { { "class", "btn btn--reversed flex-item-no-shrink" }, { "type", "submit" } }, () => { %>
        <%= ImageTag("check.svg", new() { { "aria", new HtmlOptions { { "hidden", "true" } } } }) %>
        <span class="for-screen-reader">Start Ping</span>
      <% }) %>
    <% }) %>

    <span class="txt-small translucent pad-inline-half center">Type names to ping someone…</span>
  </div>
</turbo-frame>

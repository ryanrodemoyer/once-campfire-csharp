<%# Be sure to check/update messages/_template.html.erb when changing this file %>

<%# Bump this version when the message presentation filters change what they emit. Editing this line changes the template digest, which busts BOTH this fragment cache and the collection cache that keys on this partial's digest (helper Ruby changes alone don't). %>
<% FragmentCache(w, message.Id, message.UpdatedAt, () => { %>
  <%= MessageTag(message, () => { %>
    <h2 class="message__day-separator"><%= LocalDatetimeTag(message.CreatedAt, "date") %></h2>

    <figure class="avatar message__avatar">
      <%= AvatarTag(message.Creator!.Avatar) %>
    </figure>

    <turbo-frame id="<%= RecordIdentifier.DomId(message.Record, "edit") %>">
      <div class="message__body">
        <div class="message__body-content">
          <div class="message__meta">
            <h3 class="message__heading">
              <span class="message__author" title="<%= message.Creator!.Title %>">
                <strong data-reply-target="author"><%= message.Creator!.Name %></strong>
              </span>
              <%= LinkTo(MessageTimestamp(message.CreatedAt, new() { { "class", "message__timestamp" } }), Routes.RoomAtMessagePath(message.Room.Id, message.Id), new() { { "target", "_top" },
                    { "class", "message__permalink" } }) %>
              <span class="message__room">
                <%= LinkTo(message.Room.DisplayName, Routes.RoomAtMessagePath(message.Room.Id, message.Id), new() { { "target", "_top" }, { "data", new HtmlOptions { { "reply_target", "link" } } } }) %>
              </span>
            </h3>
            <%= Render(o => MessagesActions(o, message, Routes.RoomAtMessageUrl(Origin, message.Room.Id, message.Id))) %>
          </div>
          <%= Render(o => MessagesPresentation(o, message)) %>
          <%= Render(o => MessagesBoostsBoosts(o, message)) %>
        </div>
      </div>
    </turbo-frame>
  <% }) %>
<% }); %>

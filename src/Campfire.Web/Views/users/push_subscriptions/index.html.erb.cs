<% PageTitle = "Push notification subscriptions"; %>

<% ContentFor(w, "nav", () => { %>
  <div class="flex-item-justify-start">
    <%= LinkBackToLastRoomVisited(page.LastRoomId) %>
  </div>
<% }); %>

<section class="panel panel--wide flex flex-column gap">
  <h1 class="txt-align-center txt-large margin-none">Push Notification Subscriptions</h1>
  <div class="pad-inline fill-shade border-radius" id="push_subscriptions">
    <menu class="pad flex flex-column gap">
      <%= Render(partial => { foreach (var pushSubscription in page.Subscriptions) { UsersPushSubscriptionsPushSubscription(partial, pushSubscription); } }) %>
    </menu>
  </div>
</section>

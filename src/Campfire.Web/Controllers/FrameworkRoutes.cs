using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

public static partial class Routes
{
    // RoomsController: actions declared by `resources :rooms` that RoomsController doesn't define.
    // In Rails, calling these raises AbstractController::ActionNotFound, which returns a 404.
    static partial void RoomsNew(ref RequestDelegate? handler) => handler = ActionNotFound("new", "RoomsController");
    static partial void RoomsCreate(ref RequestDelegate? handler) => handler = ActionNotFound("create", "RoomsController");
    static partial void RoomsEdit(ref RequestDelegate? handler) => handler = ActionNotFound("edit", "RoomsController");
    static partial void RoomsUpdate(ref RequestDelegate? handler) => handler = ActionNotFound("update", "RoomsController");

    // MessagesController: actions declared by `resources :messages` that MessagesController doesn't define.
    // In Rails, calling these raises AbstractController::ActionNotFound, which returns a 404.
    static partial void MessagesNew(ref RequestDelegate? handler) => handler = ActionNotFound("new", "MessagesController");

    // Turbo Native navigation helpers (Turbo::Native::NavigationController)
    static partial void TurboNativeNavigationRecede(ref RequestDelegate? handler) => handler = TurboNative("Going back…");
    static partial void TurboNativeNavigationResume(ref RequestDelegate? handler) => handler = TurboNative("Staying put…");
    static partial void TurboNativeNavigationRefresh(ref RequestDelegate? handler) => handler = TurboNative("Refreshing…");

    // Action Mailbox ingresses (ActionMailbox::Ingresses::*): ingress is not configured in Campfire,
    // so ActionMailbox::BaseController#ensure_configured answers 404.
    static partial void ActionMailboxIngressesPostmarkInboundEmailsCreate(ref RequestDelegate? handler) => handler = MailboxIngressNotConfigured;
    static partial void ActionMailboxIngressesRelayInboundEmailsCreate(ref RequestDelegate? handler) => handler = MailboxIngressNotConfigured;
    static partial void ActionMailboxIngressesSendgridInboundEmailsCreate(ref RequestDelegate? handler) => handler = MailboxIngressNotConfigured;
    static partial void ActionMailboxIngressesMandrillInboundEmailsHealthCheck(ref RequestDelegate? handler) => handler = MailboxIngressNotConfigured;
    static partial void ActionMailboxIngressesMandrillInboundEmailsCreate(ref RequestDelegate? handler) => handler = MailboxIngressNotConfigured;
    static partial void ActionMailboxIngressesMailgunInboundEmailsCreate(ref RequestDelegate? handler) => handler = MailboxIngressNotConfigured;

    // Rails Conductor (Rails::Conductor::ActionMailbox::*): development conductor disabled in production,
    // answering 403 Forbidden.
    static partial void RailsConductorActionMailboxInboundEmailsIndex(ref RequestDelegate? handler) => handler = ConductorDisabled;
    static partial void RailsConductorActionMailboxInboundEmailsCreate(ref RequestDelegate? handler) => handler = ConductorDisabled;
    static partial void RailsConductorActionMailboxInboundEmailsNew(ref RequestDelegate? handler) => handler = ConductorDisabled;
    static partial void RailsConductorActionMailboxInboundEmailsShow(ref RequestDelegate? handler) => handler = ConductorDisabled;
    static partial void RailsConductorActionMailboxInboundEmailsSourcesNew(ref RequestDelegate? handler) => handler = ConductorDisabled;
    static partial void RailsConductorActionMailboxInboundEmailsSourcesCreate(ref RequestDelegate? handler) => handler = ConductorDisabled;
    static partial void RailsConductorActionMailboxReroutesCreate(ref RequestDelegate? handler) => handler = ConductorDisabled;
    static partial void RailsConductorActionMailboxIncineratesCreate(ref RequestDelegate? handler) => handler = ConductorDisabled;

    static RequestDelegate TurboNative(string message) => async context =>
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(message).ConfigureAwait(false);
    };

    static Task MailboxIngressNotConfigured(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/html";
        return Task.CompletedTask;
    }

    static Task ConductorDisabled(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/html";
        return Task.CompletedTask;
    }
}

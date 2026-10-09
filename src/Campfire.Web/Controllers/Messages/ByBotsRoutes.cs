using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// Messages::ByBotsController and Messages::Boosts::ByBotsController: the bot API
// (`scope path: ":bot_key", as: :bot, defaults: { format: :json }` under rooms).
public static partial class Routes
{
    static partial void MessagesByBotsIndex(ref RequestDelegate? handler) => handler = MessagesByBotsController.Index;

    static partial void MessagesByBotsCreate(ref RequestDelegate? handler) => handler = MessagesByBotsController.Create;

    static partial void MessagesByBotsUpdate(ref RequestDelegate? handler) => handler = MessagesByBotsController.Update;

    static partial void MessagesByBotsDestroy(ref RequestDelegate? handler) => handler = MessagesByBotsController.Destroy;

    static partial void MessagesBoostsByBotsCreate(ref RequestDelegate? handler) => handler = MessagesBoostsByBotsController.Create;

    static partial void MessagesBoostsByBotsDestroy(ref RequestDelegate? handler) => handler = MessagesBoostsByBotsController.Destroy;
}

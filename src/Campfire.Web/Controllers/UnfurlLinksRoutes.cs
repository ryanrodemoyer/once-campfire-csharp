using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// UnfurlLinksController (`resource :unfurl_link, only: :create`).
public static partial class Routes
{
    static partial void UnfurlLinksCreate(ref RequestDelegate? handler) => handler = UnfurlLinksController.Create;
}

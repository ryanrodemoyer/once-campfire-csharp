using Campfire.Web.Controllers;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web;

// Active Storage's engine routes (activestorage/config/routes.rb), bound in Routes.g.cs.
public static partial class Routes
{
    static partial void ActiveStorageBlobsRedirectShow(ref RequestDelegate? handler) =>
        handler = ActiveStorageBlobsRedirectController.Show;

    static partial void ActiveStorageBlobsProxyShow(ref RequestDelegate? handler) =>
        handler = ActiveStorageBlobsProxyController.Show;

    static partial void ActiveStorageRepresentationsRedirectShow(ref RequestDelegate? handler) =>
        handler = ActiveStorageRepresentationsRedirectController.Show;

    static partial void ActiveStorageRepresentationsProxyShow(ref RequestDelegate? handler) =>
        handler = ActiveStorageRepresentationsProxyController.Show;

    static partial void ActiveStorageDiskShow(ref RequestDelegate? handler) =>
        handler = ActiveStorageDiskController.Show;

    static partial void ActiveStorageDiskUpdate(ref RequestDelegate? handler) =>
        handler = ActiveStorageDiskController.Update;

    static partial void ActiveStorageDirectUploadsCreate(ref RequestDelegate? handler) =>
        handler = ActiveStorageDirectUploadsController.Create;
}

using Campfire.RailsCompat.Ruby;
using Campfire.Storage.Blobs;
using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>ActiveStorage::DiskController</c> (activestorage/app/controllers/active_storage/disk_controller.rb).
/// <c>skip_forgery_protection</c>. <c>show</c> is public and, per reference/config/initializers/active_storage.rb,
/// cached <c>max-age=3600, public</c>. <c>update</c> requires a Campfire session
/// (reference/config/initializers/active_storage_authentication.rb) before the token is even read,
/// so an anonymous caller is 401 rather than 404.
/// </summary>
public sealed class ActiveStorageDiskController : ActiveStorageController
{
    static readonly ControllerCallbacks<ActiveStorageDiskController> Chain = BaseCallbacks
        .For<ActiveStorageDiskController>()
        .SkipBefore("verify_authenticity_token")
        .SkipAfter("verify_same_origin_request")
        .Before("require_active_storage_authentication", c => c.RequireAuthenticationAsync(), only: ["update"])
        .After("cache_control", c => c.Headers["Cache-Control"] = "max-age=3600, public", only: ["show"]);

    public static readonly RequestDelegate Show = Action(Chain, c => c.ShowAsync());

    public static readonly RequestDelegate Update = Action(Chain, c => c.UpdateAsync());

    // `named_disk_service` falls back to the default service, and Campfire only configures `local`.
    ValueTask ShowAsync()
    {
        var encoded = Params["encoded_key"] as string ?? "";
        if (Storage.Service.DecodeVerifiedKey(encoded, Now) is not { } key)
        {
            Head(404);
            return ValueTask.CompletedTask;
        }
        var path = Storage.Service.PathFor(key.Key);
        if (!File.Exists(path))
        {
            Head(404);
            return ValueTask.CompletedTask;
        }
        DiskFileServer.Serve(this, path, key.ContentType, key.Disposition);
        return ValueTask.CompletedTask;
    }

    ValueTask UpdateAsync()
    {
        var encoded = Params["encoded_token"] as string ?? "";
        if (Storage.Service.DecodeVerifiedToken(encoded, Now) is not { } token)
        {
            Head(404);
            return ValueTask.CompletedTask;
        }
        if (!AcceptableContent(token))
        {
            Head(422);
            return ValueTask.CompletedTask;
        }
        try
        {
            Storage.Service.Upload(token.Key, new MemoryStream(Request.Body.Raw), token.Checksum);
            // The reference runs under a frozen clock, so the file's mtime is that instant.
            File.SetLastWriteTimeUtc(Storage.Service.PathFor(token.Key), Now.UtcDateTime);
        }
        catch (BlobIntegrityException)
        {
            Head(422);
            return ValueTask.CompletedTask;
        }
        Head(204);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// <c>token[:content_type] == request.content_mime_type</c> (a <c>Mime::Type</c> stringifies
    /// through <c>to_str</c>, so this is the canonical type, not synonym equality the other way)
    /// and <c>token[:content_length] == request.content_length</c> (the header's <c>to_i</c>, nil when absent).
    /// </summary>
    bool AcceptableContent(DiskToken token)
    {
        var requestType = Request.ContentMimeType?.Value;
        if (token.ContentType != requestType)
        {
            return false;
        }
        var header = Header("Content-Length");
        return !string.IsNullOrEmpty(header) && token.ContentLength == RubyString.ToI(header);
    }
}

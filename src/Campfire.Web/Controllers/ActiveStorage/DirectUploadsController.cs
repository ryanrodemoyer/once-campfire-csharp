using System.Numerics;
using System.Text.Json.Nodes;
using Campfire.Data.Sqlite;
using Campfire.RailsCompat.Crypto;
using Campfire.RailsCompat.Formatting;
using Campfire.RailsCompat.GlobalId;
using Campfire.RailsCompat.Params;
using Campfire.RailsCompat.Ruby;
using Campfire.Storage.Blobs;
using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>ActiveStorage::DirectUploadsController#create</c>
/// (activestorage/app/controllers/active_storage/direct_uploads_controller.rb):
/// <c>params.expect(blob: [:filename, :byte_size, :checksum, :content_type, metadata: {}])</c>,
/// then <c>Blob.create_before_direct_upload!</c> (a row, no file) and the direct-upload JSON.
/// CSRF runs first (<c>ActionController::Base</c>); the session check runs after it
/// (reference/config/initializers/active_storage_authentication.rb).
/// </summary>
public sealed class ActiveStorageDirectUploadsController : ActiveStorageController
{
    static readonly ControllerCallbacks<ActiveStorageDirectUploadsController> Chain = BaseCallbacks
        .For<ActiveStorageDirectUploadsController>()
        .Before("require_active_storage_authentication", c => c.RequireAuthenticationAsync());

    public static readonly RequestDelegate Create = Action(Chain, c => c.CreateAsync());

    async ValueTask CreateAsync()
    {
        var args = BlobArguments();
        var filename = RequiredString(args, "filename");
        var checksum = RequiredString(args, "checksum");
        var byteSize = RequiredByteSize(args);
        var contentType = args["content_type"] as string;
        var metadata = args.GetHash("metadata")?.ToJson();
        var key = BlobKey.Generate();
        var blob = await WriteAsync(tx => Insert(tx.Session, key, filename, contentType, metadata, byteSize, checksum, Now)).ConfigureAwait(false);
        var url = RequestUrl.BaseUrl + Storage.Service.DirectUploadPath(key, Now.Add(ServiceUrlLifetime), contentType, byteSize, checksum);
        var sgid = SignedGlobalId.AttachableSgid(App.Keys, GlobalId.Create("ActiveStorage::Blob", blob.Id));
        Render(RailsJson.Encode(DirectUploadJson(blob, metadata, Storage.Urls.SignedId(blob.Id), sgid, url, contentType)), "application/json");
    }

    /// <summary>
    /// <c>params.expect</c> permits the listed keys (a metadata hash at any depth) and requires
    /// <c>blob</c> itself. Missing <c>filename</c> or <c>byte_size</c> is the keyword error from
    /// <c>create_before_direct_upload!</c>, not <c>ParameterMissing</c>.
    /// </summary>
    ParamHash BlobArguments()
    {
        if (Params.Require("blob") is not ParamHash blob)
        {
            throw new ParameterMissingException("blob");
        }
        return blob.Permit("filename", "byte_size", "checksum", "content_type", PermitFilter.AnyHash("metadata"));
    }

    static string RequiredString(ParamHash args, string key) =>
        args[key] as string ?? throw new ArgumentException($"missing keyword: :{key}");

    /// <summary>
    /// An integer column's cast: a number stays a number, and a string that isn't one becomes 0
    /// (<c>"nope".to_i</c>), which is what <c>create!</c> stores.
    /// </summary>
    static long RequiredByteSize(ParamHash args)
    {
        if (!args.ContainsKey("byte_size"))
        {
            throw new ArgumentException("missing keyword: :byte_size");
        }
        return args["byte_size"] switch
        {
            long number => number,
            BigInteger number when number >= long.MinValue && number <= long.MaxValue => (long)number,
            double number => (long)number,
            string text => ActiveModelInteger.Cast(text) ?? 0,
            _ => throw new ArgumentException("missing keyword: :byte_size"),
        };
    }

    /// <summary>
    /// The row half of <c>create_before_direct_upload!</c>. <c>metadata</c> stays SQL NULL when the
    /// request didn't send any. <c>store :metadata</c> still serializes that NULL as <c>{}</c>.
    /// </summary>
    static Blob Insert(SqliteSession session, string key, string filename, string? contentType, JsonObject? metadata, long byteSize, string checksum, DateTimeOffset now)
    {
        var createdAt = ActiveRecordTime.ToDb(now);
        var id = session.Scalar<long>(
            """
            INSERT INTO "active_storage_blobs" ("key", "filename", "content_type", "metadata", "service_name", "byte_size", "checksum", "created_at")
            VALUES (@key, @filename, @content_type, @metadata, @service_name, @byte_size, @checksum, @created_at) RETURNING "id"
            """,
            ("@key", key), ("@filename", filename), ("@content_type", contentType),
            ("@metadata", metadata is null ? null : RailsJson.Encode(metadata)),
            ("@service_name", DiskService.LocalName), ("@byte_size", byteSize), ("@checksum", checksum),
            ("@created_at", createdAt));
        return new Blob(id, key, new Filename(filename), contentType, metadata?.DeepClone().AsObject() ?? new JsonObject(),
            DiskService.LocalName, byteSize, checksum, ActiveRecordTime.FromDb(createdAt)!.Value);
    }

    /// <summary>
    /// <c>blob.as_json(root: false, methods: :signed_id).merge(direct_upload: { url:, headers: })</c>.
    /// Column order is the table's (reference/db/schema.rb). <c>store :metadata</c> serializes NULL as
    /// <c>{}</c>. <c>ActionText::Attachable</c> adds <c>attachable_sgid</c> after the columns.
    /// </summary>
    static JsonObject DirectUploadJson(Blob blob, JsonObject? metadata, string signedId, string attachableSgid, string url, string? contentType)
    {
        var json = new JsonObject();
        json.Add("id", blob.Id);
        json.Add("byte_size", blob.ByteSize);
        json.Add("checksum", blob.Checksum);
        json.Add("content_type", contentType is null ? null : JsonValue.Create(contentType));
        json.Add("created_at", TimeFormats.AsJson(blob.CreatedAt));
        json.Add("filename", blob.Filename.Value);
        json.Add("key", blob.Key);
        json.Add("metadata", metadata is null ? new JsonObject() : metadata.DeepClone());
        json.Add("service_name", blob.ServiceName);
        json.Add("attachable_sgid", attachableSgid);
        json.Add("signed_id", signedId);
        var headers = new JsonObject();
        headers.Add("Content-Type", contentType is null ? null : JsonValue.Create(contentType));
        var directUpload = new JsonObject();
        directUpload.Add("url", url);
        directUpload.Add("headers", headers);
        json.Add("direct_upload", directUpload);
        return json;
    }
}

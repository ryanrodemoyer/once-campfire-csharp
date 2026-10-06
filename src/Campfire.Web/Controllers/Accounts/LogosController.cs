using Campfire.Data.Queries;
using Campfire.Storage.Blobs;
using Campfire.Storage.Media;
using Campfire.Storage.Variants;
using Campfire.Web.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Campfire.Web.Controllers;

/// <summary>
/// <c>Accounts::LogosController</c> (reference/app/controllers/accounts/logos_controller.rb): the
/// account's logo as a PNG, large (512px) or <c>?size=small</c> (192px), or the stock icon when it
/// has none that can be resized; and removing it.
/// </summary>
public sealed class AccountsLogosController : ApplicationController
{
    static readonly ControllerCallbacks<AccountsLogosController> Chain = Callbacks.For<AccountsLogosController>()
        .AllowUnauthenticatedAccess(only: ["show"])
        .EnsureCanAdminister(only: ["destroy"]);

    public static readonly RequestDelegate Show = Action(Chain, c => c.ShowAsync());

    /// <summary><c>Current.account.logo.destroy</c>, then back to the settings.</summary>
    public static readonly RequestDelegate Destroy = Action(Chain, async c =>
    {
        var now = c.Now;
        await c.WriteAsync(tx =>
        {
            var account = Accounts.First(tx.Session) ?? throw new InvalidOperationException("undefined method 'logo' for nil");
            AccountsController.DetachLogo(tx, account.Id, now);
        });
        c.RedirectTo(Routes.EditAccountUrl(c.UrlBase));
    });

    // `if stale?(etag: Current.account)`: cached publicly for five minutes, then the logo's variant
    // or the stock icon.
    async ValueTask ShowAsync()
    {
        var account = await Current.AccountAsync().ConfigureAwait(false);
        var etag = account is null ? null : CacheKeys.Record("accounts", account.Id, account.UpdatedAt);
        if (!IsStale(new Freshness { Etag = etag }))
        {
            return;
        }
        ExpiresIn(TimeSpan.FromMinutes(5), isPublic: true, staleWhileRevalidate: TimeSpan.FromDays(7));

        if (account is not null && await LogoVariantAsync(account.Id).ConfigureAwait(false) is { } variant)
        {
            var storage = App.RequireStorage();
            SendPngFile(storage.Service.PathFor(variant.Key));
        }
        else
        {
            SendStockIcon();
        }
    }

    // `Current.account.logo_variant(logo_size)`: `logo.variant(size).processed if logo.variable?`.
    async ValueTask<Blob?> LogoVariantAsync(long accountId)
    {
        var logo = await ReadAsync(session => BlobRecords.FindAttachedBlob(session, nameof(Data.Records.Account), accountId, Data.Records.AttachmentNames.Logo)).ConfigureAwait(false);
        if (logo is null || !logo.IsVariable)
        {
            return null;
        }
        var storage = App.RequireStorage();
        var variant = Representable.Variant(logo, IsSmallLogo ? NamedVariants.LogoSmall : NamedVariants.LogoLarge);
        var processor = new VariantProcessor(App.Database, App.Clock);
        return await processor.ProcessedAsync(variant, new MediaProcessor(storage).TransformVariant, RequestAborted).ConfigureAwait(false);
    }

    // send_stock_icon: app/assets/images/logos/app-icon-192.png or app-icon.png.
    void SendStockIcon()
    {
        var filename = IsSmallLogo ? "app-icon-192.png" : "app-icon.png";
        var assets = App.RequireAssets();
        var bytes = assets.DigestedPath($"logos/{filename}") is { } path && assets.Files.TryGetValue(path, out var file)
            ? file
            : throw new InvalidOperationException($"Cannot read file app/assets/images/logos/{filename}");
        SendPng(filename, (stream, cancellationToken) => stream.WriteAsync(bytes, cancellationToken).AsTask());
    }

    // `send_file path, content_type: "image/png", disposition: :inline`.
    void SendPngFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Cannot read file {path}");
        }
        SendPng(Path.GetFileName(path), async (stream, cancellationToken) =>
        {
            await using var file = File.OpenRead(path);
            await file.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        });
    }

    // send_file_headers!: the disposition with the file's name, a binary transfer encoding, and
    // the content type with no charset.
    void SendPng(string filename, Func<Stream, CancellationToken, Task> write)
    {
        Headers["Content-Disposition"] = ContentDisposition.Format("inline", filename);
        Headers["Content-Transfer-Encoding"] = "binary";
        SetContentType("image/png", charset: false);
        SendStream(write);
    }

    // small_logo?: `params[:size] == "small"`.
    bool IsSmallLogo => Params["size"] as string == "small";
}

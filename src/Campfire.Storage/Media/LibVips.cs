using NetVips;

namespace Campfire.Storage.Media;

/// <summary>
/// The system libvips through NetVips, set up as reference/config/initializers/vips.rb leaves it:
/// <c>Vips.block_untrusted(true)</c> and <c>Vips.block("VipsForeignLoadOpenslide", true)</c>.
/// Variants are byte-identical to the reference's only with the same libvips build (8.16.1 from
/// Debian trixie, the reference image's), which the image pins (P02).
/// </summary>
public static class LibVips
{
    static readonly Lazy<Exception?> Setup = new(Initialize);

    /// <summary>Whether libvips loaded and its restrictions are in place.</summary>
    public static bool IsAvailable => Setup.Value is null;

    /// <summary><c>Vips.version_string</c>, e.g. <c>8.16.1</c>.</summary>
    public static string Version
    {
        get
        {
            EnsureInitialized();
            return $"{NetVips.NetVips.Version(0)}.{NetVips.NetVips.Version(1)}.{NetVips.NetVips.Version(2)}";
        }
    }

    /// <summary>Loads libvips on first use, so a process that never touches an image doesn't pay for it.</summary>
    public static void EnsureInitialized()
    {
        if (Setup.Value is { } failure)
        {
            throw new MediaException("libvips is not available", failure);
        }
    }

    static Exception? Initialize()
    {
        try
        {
            if (!ModuleInitializer.VipsInitialized)
            {
                return ModuleInitializer.Exception ?? new DllNotFoundException("libvips");
            }
            NetVips.NetVips.BlockUntrusted = true;
            Operation.Block("VipsForeignLoadOpenslide", true);
            return null;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        {
            return exception;
        }
    }
}

/// <summary>Image or video processing failed, or its tools are missing.</summary>
public class MediaException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary><c>ActiveStorage::PreviewError</c>: the previewer's command failed.</summary>
public sealed class PreviewException(string message) : MediaException(message);

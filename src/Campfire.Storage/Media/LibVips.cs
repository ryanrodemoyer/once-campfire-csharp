using System.Runtime.InteropServices;

namespace Campfire.Storage.Media;

/// <summary>
/// The system libvips through NetVips, set up as reference/config/initializers/vips.rb leaves it:
/// <c>Vips.block_untrusted(true)</c> and <c>Vips.block("VipsForeignLoadOpenslide", true)</c>.
/// Variants are byte-identical to the reference's only with the same libvips build (8.16.1 from
/// Debian trixie, the reference image's), which the image pins (P02).
/// <para>
/// libvips' own <c>vips_init</c> opens its loader modules (<c>vips-modules-x.y/*.so</c>) with
/// <c>RTLD_GLOBAL</c>. The openslide module links the system <c>libsqlite3</c>, whose symbols then
/// take over the internal calls of the SQLite that Microsoft.Data.Sqlite bundles, and the next
/// connection crashes (<c>sqlite3_open_v2</c> runs the system copy's <c>sqlite3_initialize</c>).
/// So libvips is started with <c>VIPSHOME</c> pointing at a prefix with no modules, and the same
/// modules are then opened as libvips opens them, but with <c>G_MODULE_BIND_LOCAL</c>. Every loader
/// is registered as before; only their libraries stay out of the global symbol scope.
/// </para>
/// <para>
/// NetVips starts libvips when its assembly is first touched, so every NetVips call lives in
/// <see cref="VipsImaging"/>, reached only after <see cref="EnsureInitialized"/>.
/// </para>
/// </summary>
public static class LibVips
{
    const int GModuleBindLazy = 1;
    const int GModuleBindLocal = 2;

    static readonly Lazy<Exception?> Setup = new(Initialize);

    /// <summary>Whether libvips loaded and its restrictions are in place.</summary>
    public static bool IsAvailable => Setup.Value is null;

    /// <summary><c>Vips.version_string</c>, e.g. <c>8.16.1</c>.</summary>
    public static string Version
    {
        get
        {
            EnsureInitialized();
            return VipsImaging.Version();
        }
    }

    /// <summary>The loader modules opened at startup, as paths.</summary>
    public static IReadOnlyList<string> Modules { get; private set; } = [];

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
            if (OperatingSystem.IsLinux())
            {
                // Read by vips_init's vips_guess_prefix: no lib/vips-modules-x.y there, so nothing loads globally.
                _ = setenv("VIPSHOME", Path.Combine(AppContext.BaseDirectory, "no-vips-modules"), 1);
            }
            if (VipsImaging.Start() is { } failure)
            {
                return failure;
            }
            if (OperatingSystem.IsLinux())
            {
                Modules = LoadModulesLocally(VipsImaging.ModuleDirectoryName());
            }
            VipsImaging.Restrict();
            return null;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        {
            return exception;
        }
    }

    /// <summary>
    /// <c>vips_load_plugins</c>: every file in <c>&lt;libvips' directory&gt;/vips-modules-x.y</c>, opened
    /// lazily and made resident, but bound locally. A module that doesn't open is skipped, as libvips
    /// skips it with a warning.
    /// </summary>
    static List<string> LoadModulesLocally(string directoryName)
    {
        var modules = new List<string>();
        if (LibraryDirectory() is not { } libraries || !Directory.Exists(Path.Combine(libraries, directoryName)))
        {
            return modules;
        }
        foreach (var path in Directory.GetFiles(Path.Combine(libraries, directoryName)).Order(StringComparer.Ordinal))
        {
            var module = g_module_open(path, GModuleBindLazy | GModuleBindLocal);
            if (module != 0)
            {
                g_module_make_resident(module);
                modules.Add(path);
            }
        }
        return modules;
    }

    /// <summary>The directory of the libvips the process loaded, from <c>dladdr</c> on one of its functions.</summary>
    static string? LibraryDirectory()
    {
        if (!NativeLibrary.TryLoad("libvips.so.42", out var library) || !NativeLibrary.TryGetExport(library, "vips_init", out var function)
            || dladdr(function, out var info) == 0 || info.FileName == 0)
        {
            return null;
        }
        return Path.GetDirectoryName(Marshal.PtrToStringUTF8(info.FileName));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DlInfo
    {
        public nint FileName;
        public nint BaseAddress;
        public nint SymbolName;
        public nint SymbolAddress;
    }

    [DllImport("libc", BestFitMapping = false, ThrowOnUnmappableChar = true)]
    static extern int setenv([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int overwrite);

    [DllImport("libc")]
    static extern int dladdr(nint address, out DlInfo info);

    [DllImport("libgmodule-2.0.so.0", BestFitMapping = false, ThrowOnUnmappableChar = true)]
    static extern nint g_module_open([MarshalAs(UnmanagedType.LPUTF8Str)] string fileName, int flags);

    [DllImport("libgmodule-2.0.so.0")]
    static extern void g_module_make_resident(nint module);
}

/// <summary>Image or video processing failed, or its tools are missing.</summary>
public class MediaException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary><c>ActiveStorage::PreviewError</c>: the previewer's command failed.</summary>
public sealed class PreviewException(string message) : MediaException(message);

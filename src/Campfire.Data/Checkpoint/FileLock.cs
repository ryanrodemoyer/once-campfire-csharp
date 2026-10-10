using System.Runtime.InteropServices;

namespace Campfire.Data.Checkpoint;

// An exclusive, non-blocking flock(2) on a lock file, as Rails' SqliteWalCheckpoint holds it. flock
// locks belong to the open file description, so another thread or process that opens the file is
// refused until this one is released.
//
// The file is opened with open(2) rather than File.Open: .NET takes its own flock on every file it
// opens, which would make opening a held lock file fail instead of reporting that it's busy.
internal sealed class FileLock : IDisposable
{
    const int readWrite = 2; // O_RDWR, the same on Linux and macOS
    const int lockExclusive = 2;
    const int lockNonBlocking = 4;
    const int lockUnlock = 8;

    // EWOULDBLOCK: 35 on macOS, 11 on Linux.
    static readonly int WouldBlock = OperatingSystem.IsMacOS() ? 35 : 11;

    readonly int descriptor;

    FileLock(int descriptor)
    {
        this.descriptor = descriptor;
    }

    // The lock, or null when another holder has it.
    public static FileLock? TryAcquire(string path)
    {
        EnsureExists(path);
        var descriptor = open(path, readWrite);
        if (descriptor < 0)
        {
            throw new IOException($"open({path}) failed with errno {Marshal.GetLastPInvokeError()}");
        }

        if (flock(descriptor, lockExclusive | lockNonBlocking) == 0)
        {
            return new FileLock(descriptor);
        }
        var error = Marshal.GetLastPInvokeError();
        _ = close(descriptor);
        if (error != WouldBlock)
        {
            throw new IOException($"flock({path}) failed with errno {error}");
        }
        return null;
    }

    public void Dispose()
    {
        _ = flock(descriptor, lockUnlock);
        _ = close(descriptor);
    }

    static void EnsureExists(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite).Dispose();
        }
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    static extern int flock(int descriptor, int operation);

    [DllImport("libc", SetLastError = true)]
    static extern int close(int descriptor);
}

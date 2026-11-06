#if LINUX
using System.Runtime.InteropServices;

namespace Clippy;

/// <summary>
/// One recorder at a time: an exclusive flock on /tmp/clippy-instance.lock, held for the process
/// lifetime. The kernel releases it when the holder dies, so a crashed run leaves nothing to
/// clean up; a second launch fails the non-blocking acquire and exits before it can kill the
/// running recorder's IPC socket or wipe its disk spool.
///
/// The file lives in /tmp on purpose: it is per-boot, which is exactly the scope a recorder has.
/// A client flag (--save-short and friends) never reaches this class -- it is answered by the
/// RUNNING recorder's IPC socket and exits before Program.cs takes the lock.
/// </summary>
internal static partial class LinuxSingleInstance
{
    private static int lockFd = -1;

    /// <summary>
    /// True when THIS process holds the lock and may run as the recorder. False when another live
    /// Clippy owns it (or, fail-open, when the lock file cannot be opened at all -- a read-only
    /// /tmp must not stop the recorder; the worst case is the pre-lock behaviour).
    /// </summary>
    public static bool TryAcquire()
    {
        // O_WRONLY | O_CREAT = 0x241, 0600.
        var fd = open("/tmp/clippy-instance.lock", 0x241, 0x180);
        if (fd < 0)
        {
            Console.WriteLine($"Instance: could not open /tmp/clippy-instance.lock (errno {Marshal.GetLastWin32Error()}); the single-instance guard is off.");
            return true;
        }

        // LOCK_EX (2) | LOCK_NB (4): a held lock fails immediately instead of queueing.
        if (flock(fd, 2 | 4) != 0)
        {
            close(fd);
            return false;
        }

        lockFd = fd; // never closed: the lock must outlive the run
        return true;
    }

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int open(string path, int flags, uint mode);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int flock(int fd, int operation);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);
}
#endif

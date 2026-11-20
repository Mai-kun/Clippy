#if LINUX
using System.Runtime.InteropServices;

namespace Clippy;

/// <summary>
/// exec, for restarts. Replacing the process image in place -- instead of starting a child and
/// exiting -- is what keeps a restart honest under systemd: the unit's main PID never exits, so
/// the unit stays active, journald attribution continues, and the default KillMode=control-group
/// never gets a reason to sweep a freshly started replacement out of the cgroup (a start-then-
/// exit restart under a Type=simple unit deactivates the service the instant the old main PID
/// exits, and SIGTERMs everything left inside it -- including the successor that was born there
/// a moment earlier).
///
/// The flip side is that no graceful shutdown runs: the recording pipelines die with the image,
/// exactly as they would in any restart, and the next startup clears the disk spool that was
/// left behind. The single-instance lock must be released before the call: it is held by an fd
/// opened WITHOUT close-on-exec (deliberately -- a plain start-then-exit relies on dying to
/// release it), so an exec would otherwise carry the lock into the successor, which would then
/// find its own predecessor holding it and exit as a duplicate.
/// </summary>
internal static partial class LinuxProcess
{
    /// <summary>
    /// Replaces this process with <paramref name="path"/> run with <paramref name="arguments"/>
    /// (argv[0] is the path itself) and the current environment. Returns true only when exec
    /// FAILED and this image is still running; on success the call never returns.
    /// </summary>
    public static unsafe bool TryExecSelf(string path, string[] arguments)
    {
        // execve wants two NULL-TERMINATED arrays of UTF-8 pointers, and the interop marshaller
        // does not add the terminating null to a marshalled array (the kernel then reads past
        // the end and answers EFAULT -- measured, not guessed). The pointer arrays are built by
        // hand instead, with the nulls in place, and released if exec fails and this image lives
        // on to clean up after itself.
        var allocations = new List<nint>();
        var argv = BuildPointerArray(arguments, path, allocations);
        var envp = BuildPointerArray(
            Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Select(e => $"{e.Key}={e.Value}")
                .ToArray(),
            null,
            allocations);

        var result = execve(path, argv, envp);

        foreach (var allocation in allocations)
            Marshal.FreeHGlobal(allocation);
        Marshal.FreeHGlobal((nint)argv);
        Marshal.FreeHGlobal((nint)envp);

        if (result != 0)
            Console.WriteLine($"Restart: exec({path}) failed (errno {Marshal.GetLastWin32Error()}); falling back to start-then-exit.");
        return result != 0;
    }

    /// <summary>
    /// One NULL-terminated array of UTF-8 string pointers; <paramref name="first"/> prepends
    /// argv[0] (the executable path) and a null <paramref name="first"/> builds envp. Every
    /// allocation (the strings and the array itself) lands in <paramref name="allocations"/>,
    /// because on the failure path this image keeps running and frees them.
    /// </summary>
    private static unsafe nint* BuildPointerArray(string[] strings, string? first, List<nint> allocations)
    {
        var total = strings.Length + (first is null ? 0 : 1) + 1; // + the terminating null
        var pointers = (nint*)Marshal.AllocHGlobal(sizeof(nint) * total);
        allocations.Add((nint)pointers);
        var index = 0;
        if (first is not null)
            allocations.Add(pointers[index++] = Marshal.StringToCoTaskMemUTF8(first));
        foreach (var text in strings)
            allocations.Add(pointers[index++] = Marshal.StringToCoTaskMemUTF8(text));
        pointers[index] = 0;
        return pointers;
    }

    // A failed exec returns -1 and leaves this process standing; a successful one never returns
    // at all. The array parameters pass pointers to pointers: the null-terminated shape is the
    // caller's job (see BuildPointerArray), which is also why neither carries StringMarshalling.
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int execve(string path, nint* argv, nint* envp);
}
#endif

namespace Clippy;

/// <summary>
/// Picks the backend once, at startup, from the compile-time define the project file sets: WINDOWS
/// for a win-* runtime identifier or a Windows build host, LINUX for anything else. Program.cs
/// never sees either concrete class.
/// </summary>
internal static class PlatformFactory
{
    public static IPlatformBackend Create()
    {
#if WINDOWS
        return new WindowsPlatformBackend();
#elif LINUX
        return new LinuxPlatformBackend();
#else
#error No platform backend defined: expected WINDOWS or LINUX from DefineConstants.
#endif
    }
}

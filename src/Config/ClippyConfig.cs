using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clippy;

/// <summary>
/// User settings, read from a config.json next to the executable.
///
/// Every value has a default, so a missing or partial file is always usable: a field that fails to
/// parse falls back rather than throwing, because a typo in a config file must not stop the recorder
/// from starting. A malformed file is reported once on the console and then ignored.
/// </summary>
public sealed class ClippyConfig
{
    /// <summary>
    /// Where finished clips are written, relative to the executable. "clips" rather than "output"
    /// because the folder now holds only the product; the logs went to their own.
    /// </summary>
    public string OutputFolder { get; set; } = "clips";

    /// <summary>Where debug artifacts go: ffmpeg logs, timing CSVs, crash breadcrumbs.</summary>
    public string LogsFolder { get; set; } = "logs";
    public double ShortClipSeconds { get; set; } = 30;
    public double LongClipSeconds { get; set; } = 180;
    public string ShortClipHotkey { get; set; } = "F8";
    public string LongClipHotkey { get; set; } = "F9";
    public bool PlaySoundNotification { get; set; } = true;

    /// <summary>
    /// Own notification sound, a .wav, played instead of the system beep when the file exists.
    /// Relative paths are resolved against the executable's folder. Empty (the default), a blank or
    /// a path with no file behind it all mean the same thing: the standard Windows sound, because a
    /// wrong path must degrade rather than leave the save confirmed by silence.
    /// </summary>
    public string CustomSoundPath { get; set; } = "";

    /// <summary>
    /// How a saved clip is announced visually:
    /// "overlay" (the default) shows a compact Medal / ShadowPlay-style game card in the top-right
    /// corner that never steals focus from a running game;
    /// "windows_toast" shows the plain Windows notification balloon;
    /// "none" shows no visual window at all -- the built-in sound (if <see cref="PlaySoundNotification"/>)
    /// is the only feedback.
    ///
    /// A preference, not a requirement: an unknown value falls back to "overlay", and if the overlay
    /// window cannot be created the code degrades to the toast rather than announcing a clip by silence.
    /// </summary>
    public string NotificationType { get; set; } = "overlay";

    /// <summary>The only notification-style values this class accepts; anything else falls back to the default.</summary>
    public static readonly string[] SupportedNotificationTypes = ["overlay", "windows_toast", "none"];

    /// <summary>True when the config asks for the Medal-style game overlay card.</summary>
    public bool UseOverlayNotification => string.Equals(NotificationType, "overlay", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the config asks for the plain Windows toast balloon.</summary>
    public bool UseWindowsToastNotification => string.Equals(NotificationType, "windows_toast", StringComparison.OrdinalIgnoreCase);


    /// <summary>
    /// Start with the console hidden, living only in the tray. False by default: while the recorder
    /// is being developed the log is the whole point, and a process that vanishes into the tray
    /// looks identical to one that crashed on startup.
    /// </summary>
    public bool StartMinimizedToTray { get; set; } = true;

    /// <summary>
    /// Which video encoder runs. "nvenc_direct" (the default) is the native bridge: the captured
    /// texture goes into NVENC where it already sits, so nothing is copied out of VRAM and no ffmpeg
    /// process is started at all.
    ///
    /// A preference, never a requirement. Every value degrades rather than fails: "nvenc_direct" needs
    /// an NVIDIA GPU and clippy_nvenc.dll, and falls back to "h264_nvenc" with a line in the log when
    /// either is missing; "h264_nvenc" needs an NVIDIA GPU and falls back to libx264. A recording that
    /// stopped because an encoder was unavailable would be a worse outcome than a slower one.
    /// </summary>
    public string VideoEncoder { get; set; } = "nvenc_direct";

    /// <summary>
    /// Which video codec the encoder produces: "hevc" (H.265, the default) or "h264".
    ///
    /// Only the direct NVENC bridge honours it -- it is the path that talks to the encoder without
    /// an ffmpeg process in between, so it is also the one that can ask for NV_ENC_CODEC_HEVC_GUID.
    /// The ffmpeg-backed encoders keep producing H.264 whatever this says, and the muxer follows the
    /// bytes rather than this setting: a file is written as hvc1 only when the encoder actually
    /// handed over a VPS. So a fallback to ffmpeg yields a correct H.264 clip, never a broken HEVC one.
    ///
    /// HEVC buys roughly twice the detail per bit, so the same picture survives a bitrate about
    /// half the size: at 3 Mbit/s it matches what H.264 needed 6 Mbit/s for.
    /// </summary>
    public string VideoCodec { get; set; } = "hevc";

    /// <summary>The only codec values this class accepts; anything else falls back to the default.</summary>
    public static readonly string[] SupportedVideoCodecs = ["hevc", "h264"];

    /// <summary>True when the config asks for HEVC, which is the only codec the NVENC bridge can switch.</summary>
    public bool UseHevc => string.Equals(VideoCodec, "hevc", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Video bitrate in Mbps, used by the hardware (NVENC) rate control. The ring buffer holds
    /// ENCODED NAL units, so this value is close to a direct multiplier on its memory use:
    /// The figures quoted in the tray menu are the ones to go by. Clamped to 2-50, because below 2
    /// the picture falls apart and above 50 the buffer grows without any visible gain.
    /// Ignored by the software fallback, which is CRF-controlled and quality-targeted by design.
    /// </summary>
    public int VideoBitrateMbps { get; set; } = 6;

    /// <summary>
    /// The bitrate range this class accepts. Named rather than inlined at the clamp so the tray's
    /// bitrate menu can be checked against the same numbers instead of a second copy of them.
    /// </summary>
    public const int MinVideoBitrateMbps = 2;
    public const int MaxVideoBitrateMbps = 50;

    /// <summary>
    /// A ceiling on how much disk the clips folder may use, in gigabytes. 0 means no ceiling.
    /// </summary>
    /// <remarks>
    /// Clips accumulate silently, which is exactly the kind of thing a user does not notice until the
    /// disk is full and something unrelated fails. When this is above zero, the oldest clips are deleted
    /// after every save and again at startup, oldest first, until the folder fits. Only *.mp4 is counted
    /// and only *.mp4 is deleted: the folder may hold other files, and silently removing a user's
    /// unrelated downloads to satisfy a video quota would be unforgivable.
    ///
    /// The permitted values are 0, 10, 20, 30 and 50. Anything else is replaced by 0, so a typo means
    /// "no limit" rather than "one gigabyte", which would delete clips without the user having asked
    /// for a limit at all.
    /// </remarks>
    /// <summary>
    /// The clips folder as an absolute path.
    /// </summary>
    /// <remarks>
    /// One definition, because the quota, the tray's "open folder" and the exporter all have to be
    /// looking at the same directory or the quota will police a folder nobody writes to.
    /// <para>
    /// A rooted <see cref="OutputFolder"/> wins over the base directory, which is what makes the
    /// tray's folder picker work: it stores an absolute path and this returns it unchanged.
    /// </para>
    /// </remarks>
    public string ResolveOutputFolder()
    {
        // Fully qualified on purpose. This class has a `Path` member of its own -- the location of the
        // config file -- and inside the class body that name wins over System.IO.Path, so an unqualified
        // Path.Combine here does not compile.
        var folder = OutputFolder;
        return System.IO.Path.GetFullPath(
            System.IO.Path.IsPathRooted(folder)
                ? folder
                : System.IO.Path.Combine(AppContext.BaseDirectory, folder)
        );
    }

    public int MaxClipsFolderSizeGB { get; set; }

    /// <summary>
    /// Which audio encoder runs. "media_foundation" (default) uses the Windows AAC MFT in-process and
    /// needs no ffmpeg at all; "ffmpeg" forces the old two-process path.
    ///
    /// A preference, not a requirement, for the same reason VideoEncoder is: if the MFT cannot be
    /// configured the recorder falls back to ffmpeg automatically and says so, because an instant
    /// replay that captures no sound is worse than one that spends a process on it.
    /// </summary>
    public string AudioEncoder { get; set; } = "media_foundation";

    /// <summary>
    /// Ask GitHub on startup whether a newer release exists. On by default: the check is one small
    /// request that fails silently, and a recorder that only tells you about updates when you go
    /// looking for them is a recorder most people never update at all.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Where config.json is looked up: beside the binary, not the current directory.</summary>
    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = ClippyConfigContext.Default,
        // config.example.json ships commented, because a config file you cannot explain is a config
        // file nobody edits. Both options are needed for that: a // line is a comment, and the
        // trailing comma after the last entry is a comma the reader would otherwise reject.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads the config, or returns the defaults when the file is absent. Never throws.
    /// </summary>
    public static ClippyConfig Load()
    {
        var path = Path;
        if (!File.Exists(path))
        {
            var fresh = new ClippyConfig();
            fresh.Save();
            Console.WriteLine(
                $"Config: no {path}, wrote defaults "
                    + $"({fresh.ShortClipHotkey} {fresh.ShortClipSeconds:F0}s / "
                    + $"{fresh.LongClipHotkey} {fresh.LongClipSeconds:F0}s)"
            );
            return fresh;
        }

        try
        {
            var config =
                JsonSerializer.Deserialize(
                    File.ReadAllText(path),
                    ClippyConfigContext.Default.ClippyConfig
                ) ?? new ClippyConfig();

            // A negative clip length would make ExportClip slice an empty window, and a hotkey that
            // is not a function key can never be pressed, so both are corrected here.
            if (config.ShortClipSeconds <= 0)
                config.ShortClipSeconds = 30;
            if (config.LongClipSeconds <= 0)
                config.LongClipSeconds = 180;
            if (!TryParseHotkey(config.ShortClipHotkey, out _))
                config.ShortClipHotkey = "F8";
            if (!TryParseHotkey(config.LongClipHotkey, out _))
                config.LongClipHotkey = "F9";
            if (config.MaxClipsFolderSizeGB is not (0 or 10 or 20 or 30 or 50))
                config.MaxClipsFolderSizeGB = 0;
            if (string.IsNullOrWhiteSpace(config.OutputFolder))
                config.OutputFolder = "clips";
            if (string.IsNullOrWhiteSpace(config.LogsFolder))
                config.LogsFolder = "logs";
            if (
                config.VideoBitrateMbps < MinVideoBitrateMbps
                || config.VideoBitrateMbps > MaxVideoBitrateMbps
            )
                config.VideoBitrateMbps = 6;
            // A codec this build cannot produce falls back to the default rather than reaching the
            // encoder: a typo in a config file must not stop the recorder, same as everywhere else.
            if (!SupportedVideoCodecs.Contains(config.VideoCodec, StringComparer.OrdinalIgnoreCase))
                config.VideoCodec = "hevc";
            // An unknown notification style falls back to the overlay default, same rationale as the
            // codec: a typo must not stop the recorder, it just picks the default look.
            if (!SupportedNotificationTypes.Contains(config.NotificationType, StringComparer.OrdinalIgnoreCase))
                config.NotificationType = "overlay";

            // At startup as well as after every save. Startup is the half that matters: it is the only
            // moment the user is not actively recording, so it is the safe time to reclaim a few hundred
            // megabytes rather than making a save slow down to pay for last week's backlog.
            ClipsQuota.Enforce(config.ResolveOutputFolder(), config.MaxClipsFolderSizeGB);

            return config;
        }
        catch (Exception ex)
            when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Config: {path} could not be read ({ex.Message}), using defaults.");
            return new ClippyConfig();
        }
    }

    /// <summary>Writes the current values so the file can be edited by hand.</summary>
    public void Save()
    {
        try
        {
            // Straight through the source-generated context. JsonSerializer.Serialize(this, options)
            // is annotated RequiresUnreferencedCode/RequiresDynamicCode and is a real IL2026/IL3050
            // AOT warning; the JsonTypeInfo overload is the AOT-safe one.
            File.WriteAllText(
                Path,
                JsonSerializer.Serialize(this, ClippyConfigContext.Default.ClippyConfig)
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Running from a read-only location is normal; defaults are used and recording proceeds.
            Console.WriteLine($"Config: could not write {Path} ({ex.Message}).");
        }
    }

    /// <summary>
    /// "F9" -> 0x78, "NumPad5" -> 0x65. Function keys and the numeric keypad, because those are the
    /// blocks a game tends to leave alone -- the digit ROW is not offered on purpose, since a digit
    /// pressed during a match is far more likely to be aimed at something else.
    /// </summary>
    public static bool TryParseHotkey(string? name, out int vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var key = name.Trim().ToUpperInvariant();

        // The keypad is VK_NUMPAD0..9 (0x60..0x69) -- a different block from the top-row digits
        // (0x30..0x39), and the reason a binding named "NumPad5" must never be confused with "5".
        if (key.StartsWith("NUMPAD", StringComparison.Ordinal)
            && key.Length == "NUMPAD0".Length
            && key[^1] is >= '0' and <= '9')
        {
            vk = 0x60 + (key[^1] - '0');
            return true;
        }

        if (!key.StartsWith('F') || key.Length < 2 || !int.TryParse(key[1..], out var number))
            return false;

        // F1..F24, which is the full range of function keys Windows defines.
        if (number is < 1 or > 24)
            return false;

        vk = 0x70 + number - 1;
        return true;
    }
}

/// <summary>
/// Source-generated JSON metadata. NativeAOT compiles reflection-based serialization out, and the
/// default resolver then throws at runtime, so this context is what makes the config file work.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    // These belong to the CONTEXT, not to SerializerOptions: Deserialize is handed
    // ClippyConfigContext.Default.ClippyConfig, whose options come from this attribute alone.
    // Setting them on a separate JsonSerializerOptions would look right and do nothing, and the
    // commented config.example.json would then fail to load with a confusing parse error.
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true
)]
[JsonSerializable(typeof(ClippyConfig))]
public sealed partial class ClippyConfigContext : JsonSerializerContext;

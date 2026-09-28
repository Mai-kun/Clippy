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
    public string OutputFolder { get; set; } = "output";
    public double ShortClipSeconds { get; set; } = 30;
    public double LongClipSeconds { get; set; } = 180;
    public string ShortClipHotkey { get; set; } = "F9";
    public string LongClipHotkey { get; set; } = "F10";
    public bool PlaySoundNotification { get; set; } = true;

    /// <summary>
    /// Start with the console hidden, living only in the tray. False by default: while the recorder
    /// is being developed the log is the whole point, and a process that vanishes into the tray
    /// looks identical to one that crashed on startup.
    /// </summary>
    public bool StartMinimizedToTray { get; set; }

    /// <summary>Where config.json is looked up: beside the binary, not the current directory.</summary>
    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = ClippyConfigContext.Default,
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
            Console.WriteLine($"Config: no {path}, wrote defaults " +
                $"({fresh.ShortClipHotkey} {fresh.ShortClipSeconds:F0}s / " +
                $"{fresh.LongClipHotkey} {fresh.LongClipSeconds:F0}s)");
            return fresh;
        }

        try
        {
            var config =
                JsonSerializer.Deserialize(File.ReadAllText(path), ClippyConfigContext.Default.ClippyConfig)
                ?? new ClippyConfig();

            // A negative clip length would make ExportClip slice an empty window, and a hotkey that
            // is not a function key can never be pressed, so both are corrected here.
            if (config.ShortClipSeconds <= 0)
                config.ShortClipSeconds = 30;
            if (config.LongClipSeconds <= 0)
                config.LongClipSeconds = 180;
            if (!TryParseHotkey(config.ShortClipHotkey, out _))
                config.ShortClipHotkey = "F9";
            if (!TryParseHotkey(config.LongClipHotkey, out _))
                config.LongClipHotkey = "F10";
            if (string.IsNullOrWhiteSpace(config.OutputFolder))
                config.OutputFolder = "output";

            return config;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
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
            File.WriteAllText(Path, JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Running from a read-only location is normal; defaults are used and recording proceeds.
            Console.WriteLine($"Config: could not write {Path} ({ex.Message}).");
        }
    }

    /// <summary>
    /// "F9" -> 0x78. Only function keys are accepted, because those are the only ones the recorder
    /// can listen for globally without stealing the game's input.
    /// </summary>
    public static bool TryParseHotkey(string? name, out int vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var key = name.Trim().ToUpperInvariant();
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
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ClippyConfig))]
public sealed partial class ClippyConfigContext : JsonSerializerContext;
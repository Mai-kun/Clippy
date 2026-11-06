#if LINUX
using System.Diagnostics;

namespace Clippy;

/// <summary>
/// The Linux tray: an org.kde.StatusNotifierItem plus a com.canonical.dbusmenu tree, served over
/// this program's own D-Bus wire layer. SNI is what KDE Plasma's system tray hosts natively, and
/// what Waybar and the other Niri-era bars render through their SNI applets -- Wayland has no
/// global-status protocol of its own, so this is the only tray that works on both.
///
/// The menu mirrors the Windows TrayIcon entry for entry -- the open-folder block, every settings
/// submenu (durations, bitrate, encoder, codec, hotkeys, quota, audio device, notifications), and
/// Restart/Exit -- applied live where the Windows one is live. The icon comes from the embedded
/// app.ico (LinuxIconAssets), never a placeholder glyph.
/// </summary>
internal sealed class LinuxTrayIcon : IDisposable
{
    /// <summary>What the menu needs from the recorder; implemented by LinuxRecorder.</summary>
    internal interface IHost
    {
        ClippyConfig Config { get; }
        string ClipsFolder { get; }
        string LogsFolder { get; }
        string ConfigPath { get; }
        /// <summary>Saves a clip on the caller's thread; returns the path or an empty string.</summary>
        string SaveClip(bool shortClip);
        /// <summary>Hot-swaps the captured audio device, restarting the audio pipeline.</summary>
        void SetAudioDevice(string device);

        /// <summary>
        /// Applies encoder settings (bitrate, codec, encoder), restarting the video pipeline when
        /// they changed. The old gst process must be fully gone before the new one starts -- two
        /// live NVENC sessions would sit in VRAM side by side.
        /// </summary>
        void SetVideoEncoding(int bitrateMbps, string codec, string encoder);
        IReadOnlyList<(string Name, string Description)> ListAudioSinks();

        /// <summary>Points the recorder at a new clips folder (the config is saved by the tray).</summary>
        void SetClipsFolder(string folder);
        /// <summary>Rebinds both clip hotkeys live, without a restart.</summary>
        void RebindHotkeys(string shortKey, string longKey);
        /// <summary>Starts a fresh copy of this executable with the same arguments, then stops this one.</summary>
        void RestartProcess();
        void RequestExit();
    }

    private const string ItemPath = "/StatusNotifierItem";
    private const string MenuPath = "/MenuBar";
    private const string SniInterface = "org.kde.StatusNotifierItem";
    private const string MenuInterface = "com.canonical.dbusmenu";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string WatcherName = "org.kde.StatusNotifierWatcher";

    // Menu ids, copied from the Windows TrayIcon so the two menus mean the same thing everywhere:
    // actions in the 1000s, settings grouped by hundreds, hotkey tables at 3000/4000, the audio
    // block at 5000. Every node in the dbusmenu tree -- actions, submenus, separators, radios --
    // draws from these ranges with no overlap: a duplicated id makes GetLayout fold two nodes
    // into one, which is exactly the "Short Clip Duration became 15 s" bug. Linux-only entries
    // (Start with System, the separators) live in the ranges Windows never uses.
    private const uint IdOpenFolder = 1001;         // Open Clips Folder
    private const uint IdOpenConfig = 1002;         // Open Config
    private const uint IdOpenLogs = 1003;           // Open Logs Folder
    private const uint IdExit = 1004;               // Exit
    private const uint IdCheckUpdates = 1005;       // Check for Updates
    private const uint IdRestart = 1006;            // Restart
    private const uint IdOpenLastClip = 1007;       // Open Last Clip

    private const uint IdShort15 = 1101;
    private const uint IdShort30 = 1102;
    private const uint IdShort60 = 1103;
    private const uint IdLong60 = 1201;
    private const uint IdLong120 = 1202;
    private const uint IdLong180 = 1203;
    private const uint IdLong300 = 1204;
    private const uint IdRate4 = 1301;
    private const uint IdRate6 = 1302;
    private const uint IdRate8 = 1303;
    private const uint IdRate12 = 1304;
    private const uint IdRate2 = 1305;
    private const uint IdRate16 = 1306;
    private const uint IdRate20 = 1307;
    private const uint IdRate25 = 1308;
    private const uint IdRate30 = 1309;
    private const uint IdRate50 = 1310;
    private const uint IdEncoderDirect = 1403;
    private const uint IdEncoderNvenc = 1401;
    private const uint IdEncoderX264 = 1402;
    private const uint IdCodecHevc = 1404;
    private const uint IdCodecH264 = 1405;
    private const uint IdNotifyOverlay = 1501;
    private const uint IdNotifyToast = 1502;
    private const uint IdNotifyNone = 1503;
    private const uint IdNotifySound = 1504;        // "Play Sound Notification" checkmark (Linux keeps the sound separate from the visual style)
    private const uint IdPickFolder = 1601;
    private const uint IdQuota0 = 1610;
    private const uint IdQuota10 = 1611;
    private const uint IdQuota20 = 1612;
    private const uint IdQuota30 = 1613;
    private const uint IdQuota50 = 1614;

    private const uint IdHotkeyShortBase = 3000;    // + index into HotkeyChoices
    private const uint IdHotkeyLongBase = 4000;
    private const uint IdAudioDefault = 5000;
    private const uint AudioDeviceIdBase = 5001;
    private const int MaxAudioDevices = 99;

    // Submenu parents and Linux-only entries: ranges the Windows menu never uses.
    private const uint IdStartWithSystem = 1600;
    private const uint IdSubShortDuration = 6001;
    private const uint IdSubLongDuration = 6002;
    private const uint IdSubBitrate = 6003;
    private const uint IdSubEncoder = 6004;
    private const uint IdSubCodec = 6005;
    private const uint IdSubNotifications = 6006;
    private const uint IdSubShortHotkey = 6007;
    private const uint IdSubLongHotkey = 6008;
    private const uint IdSubQuota = 6009;
    private const uint IdSubAudio = 6010;
    private const uint IdSepBase = 7000;            // separators take sequential ids from here

    /// <summary>The keys both hotkey menus offer, in menu order: F1-F12, then the numeric keypad.</summary>
    internal static readonly (string Key, string Label)[] HotkeyChoices =
    [
        ("F1", "F1"), ("F2", "F2"), ("F3", "F3"), ("F4", "F4"),
        ("F5", "F5"), ("F6", "F6"), ("F7", "F7"), ("F8", "F8"),
        ("F9", "F9"), ("F10", "F10"), ("F11", "F11"), ("F12", "F12"),
        ("NumPad0", "NumPad 0"), ("NumPad1", "NumPad 1"), ("NumPad2", "NumPad 2"),
        ("NumPad3", "NumPad 3"), ("NumPad4", "NumPad 4"), ("NumPad5", "NumPad 5"),
        ("NumPad6", "NumPad 6"), ("NumPad7", "NumPad 7"), ("NumPad8", "NumPad 8"),
        ("NumPad9", "NumPad 9"),
    ];

    /// <summary>Index of the first keypad row, and the place the separator goes.</summary>
    private const int HotkeySeparatorAfter = 12;

    /// <summary>
    /// Every selectable bitrate, with the RAM figure next to it -- the ring buffer's cost for the
    /// ~190 s it holds, exactly as the Windows menu presents it.
    /// </summary>
    internal static readonly (uint Id, int Mbps, string Label)[] BitrateOptions =
    [
        (IdRate2, 2, "2 Mbps  (~75 MB RAM)"),
        (IdRate4, 4, "4 Mbps  (~150 MB RAM)"),
        (IdRate6, 6, "6 Mbps  (~225 MB RAM)"),
        (IdRate8, 8, "8 Mbps  (~300 MB RAM)"),
        (IdRate12, 12, "12 Mbps (~450 MB RAM)"),
        (IdRate16, 16, "16 Mbps (~600 MB RAM)"),
        (IdRate20, 20, "20 Mbps (~750 MB RAM)"),
        (IdRate25, 25, "25 Mbps (~940 MB RAM)"),
        (IdRate30, 30, "30 Mbps (~1.1 GB RAM)"),
        (IdRate50, 50, "50 Mbps (~1.9 GB RAM)"),
    ];

    private readonly IHost host;
    private LinuxDbus? bus;
    private Thread? registrationThread;
    private uint menuRevision = 1;
    private volatile bool registered;

    public LinuxTrayIcon(IHost host) => this.host = host;

    /// <summary>
    /// Brings the tray up on a background thread: registration must never delay or fail the
    /// recording. A session without a StatusNotifierWatcher (a bare compositor, no bar) is logged
    /// once and otherwise ignored -- the recorder works without any tray.
    /// </summary>
    public void Start()
    {
        registrationThread = new Thread(() =>
        {
            try
            {
                Run();
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
            {
                Console.WriteLine($"Tray: StatusNotifierItem unavailable ({ex.Message}); recording continues without a tray icon.");
            }
        })
        { IsBackground = true, Name = "clippy-tray" };
        registrationThread.Start();
    }

    private void Run()
    {
        bus = LinuxDbus.Connect();
        var myName = bus.Hello();
        Console.WriteLine($"Tray: connected as {myName}.");
        Console.WriteLine(LinuxIconAssets.FrameDiagnostics());
        RegisterHandlers();

        // Registration happens BEFORE the dispatch loop starts: both read the same socket, and a
        // reply eaten by the dispatcher instead of the synchronous caller desynchronises the wire.
        bus.Call(WatcherName, "/StatusNotifierWatcher", WatcherName, "RegisterStatusNotifierItem", "s",
            w => w.String(ItemPath), 10_000);
        registered = true;
        Console.WriteLine("Tray: StatusNotifierItem registered (left click saves the short clip, right click opens the menu).");
        bus.StartDispatch();
    }

    private void RegisterHandlers()
    {
        // org.kde.StatusNotifierItem: Activate = left click = save a short clip now.
        bus!.AddMethodHandler(ItemPath, SniInterface, "Activate", _ => { SaveShort(); return []; });
        bus.AddMethodHandler(ItemPath, SniInterface, "SecondaryActivate", _ => { SaveLong(); return []; });
        bus.AddMethodHandler(ItemPath, SniInterface, "ContextMenu", _ => []);
        bus.AddMethodHandler(ItemPath, SniInterface, "Scroll", _ => []);

        bus.AddMethodHandler(ItemPath, PropertiesInterface, "GetAll", body =>
        {
            body.ReadString(); // interface name; one set of properties serves both
            return WriteProperties(w =>
            {
                var (lengthPosition, contentStart) = w.BeginArray(8);
                WriteItemProperties(w);
                w.EndArray(lengthPosition, contentStart);
            });
        }, "a{sv}");
        bus.AddMethodHandler(ItemPath, PropertiesInterface, "Get", body =>
        {
            body.ReadString();
            var property = body.ReadString();
            // A property read returns a VARIANT; every SNI property is rebuilt on demand.
            return WriteBody(w =>
            {
                w.Signature("v");
                switch (property)
                {
                    case "Id": w.Variant("s", x => x.String("Clippy")); break;
                    case "Category": w.Variant("s", x => x.String("ApplicationStatus")); break;
                    case "Status": w.Variant("s", x => x.String("Active")); break;
                    case "Title": w.Variant("s", x => x.String("Clippy")); break;
                    // Empty on purpose: a named icon makes Plasma look "clippy" up in the theme and,
                    // on a cache miss, draw an empty slot INSTEAD of the IconPixmap below. The pixmap
                    // is always served, so the name must never compete with it.
                    case "IconName": w.Variant("s", x => x.String("")); break;
                    case "IconPixmap": w.Variant("a(iiay)", x => WritePixmap(x, 32)); break;
                    case "Menu": w.Variant("o", x => x.ObjectPath(MenuPath)); break;
                    case "ItemIsMenu": w.Variant("b", x => x.Bool(true)); break;
                    case "ToolTip": w.Variant("(sa(iiay)ss)", x => WriteToolTip(x)); break;
                    default: w.Variant("s", e => e.String("")); break;
                }
            });
        }, "v");

        // com.canonical.dbusmenu
        // The layout signature is (ia{sv}av), NOT the spec's (ia{sv}as): libdbusmenu wraps every
        // child in a VARIANT carrying the recursive layout, and that is what Plasma's applet and
        // every SNI bar actually parse.
        bus.AddMethodHandler(MenuPath, MenuInterface, "GetLayout", ServeGetLayout, "u(ia{sv}av)");
        bus.AddMethodHandler(MenuPath, MenuInterface, "GetGroupProperties", ServeGetGroupProperties, "a(ia{sv})");
        bus.AddMethodHandler(MenuPath, MenuInterface, "Event", ServeEvent);
        bus.AddMethodHandler(MenuPath, MenuInterface, "AboutToShow", _ => WriteBody(w => w.UInt32(0)), "b");
        bus.AddMethodHandler(MenuPath, MenuInterface, "GetItemProperty", _ => []);
        bus.AddMethodHandler(MenuPath, MenuInterface, "GetItemsProperty", _ => []);

        bus.AddMethodHandler(MenuPath, PropertiesInterface, "GetAll", body =>
        {
            body.ReadString();
            return WriteProperties(w =>
            {
                var (lengthPosition, contentStart) = w.BeginArray(8);
                w.Align(8);
                w.String("Version");
                w.Variant("u", x => x.UInt32(3));
                w.Align(8);
                w.String("Status");
                w.Variant("s", x => x.String("normal"));
                w.EndArray(lengthPosition, contentStart);
            });
        }, "a{sv}");
        bus.AddMethodHandler(MenuPath, PropertiesInterface, "Get", body =>
        {
            body.ReadString();
            var property = body.ReadString();
            return WriteBody(w =>
            {
                w.Signature("v");
                if (property == "Version")
                    w.Variant("u", x => x.UInt32(3));
                else
                    w.Variant("s", x => x.String("normal"));
            });
        }, "v");

        // Introspection, so a bar that pokes the objects first finds sane answers.
        bus.AddMethodHandler(ItemPath, "org.freedesktop.DBus.Introspectable", "Introspect",
            _ => WriteBody(w => w.String(IntrospectXml(ItemPath, SniInterface))), "s");
        bus.AddMethodHandler(MenuPath, "org.freedesktop.DBus.Introspectable", "Introspect",
            _ => WriteBody(w => w.String(IntrospectXml(MenuPath, MenuInterface))), "s");
    }

    /// <summary>A Get/GetAll reply body from a property-writing callback.</summary>
    private static byte[] WriteProperties(Action<DbusWriter> writeEntries)
    {
        var w = new DbusWriter();
        writeEntries(w);
        return w.ToBytes();
    }

    private static byte[] WriteBody(Action<DbusWriter> write)
    {
        var w = new DbusWriter();
        write(w);
        return w.ToBytes();
    }

    private void WriteItemProperties(DbusWriter w)
    {
        // Every dict entry is a struct: 8-aligned, or the broker rejects the whole body.
        void Prop(string name, string signature, Action<DbusWriter> value)
        {
            w.Align(8);
            w.String(name);
            w.Variant(signature, value);
        }

        Prop("Id", "s", x => x.String("Clippy"));
        Prop("Category", "s", x => x.String("ApplicationStatus"));
        Prop("Status", "s", x => x.String("Active"));
        Prop("Title", "s", x => x.String("Clippy"));
        // See the Get handler: an empty name keeps Plasma on the pixmap path.
        Prop("IconName", "s", x => x.String(""));
        Prop("IconPixmap", "a(iiay)", x => WritePixmap(x, 32));
        Prop("ItemIsMenu", "b", x => x.Bool(true));
        Prop("Menu", "o", x => x.ObjectPath(MenuPath));
        Prop("ToolTip", "(sa(iiay)ss)", x => WriteToolTip(x));
    }

    /// <summary>a(iiay): one 32x32 entry whose ARGB32 bytes are in network byte order.</summary>
    private static void WritePixmap(DbusWriter w, int size)
    {
        var (lengthPosition, contentStart) = w.BeginArray(8);
        w.Align(8); // the (iiay) struct
        w.Int32(size);
        w.Int32(size);
        w.WriteByteArray(LinuxIconAssets.TrayPixmapArgb32(size));
        w.EndArray(lengthPosition, contentStart);
    }

    private static void WriteToolTip(DbusWriter w)
    {
        // The value of the variant is a STRUCT: it must sit on an 8-byte boundary of its own,
        // which nothing before it guarantees (the variant's 3-byte signature just ended here).
        w.Align(8);
        w.String(""); // icon name: empty, the pixmap below is the visual
        var (lengthPosition, contentStart) = w.BeginArray(8);
        w.EndArray(lengthPosition, contentStart);
        w.String("Clippy");
        w.String("Instant replay recorder");
    }

    private static string IntrospectXml(string path, string interfaceName) =>
        path == ItemPath
            ? "<!DOCTYPE node PUBLIC '-//freedesktop//DTD D-BUS Object Introspection 1.0//EN' " +
              "'http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd'><node>" +
              $"<interface name='{interfaceName}'><method name='Activate'><arg type='i' direction='in'/><arg type='i' direction='in'/></method>" +
              "<method name='SecondaryActivate'><arg type='i' direction='in'/><arg type='i' direction='in'/></method>" +
              "<method name='ContextMenu'><arg type='i' direction='in'/><arg type='i' direction='in'/></method>" +
              "<method name='Scroll'><arg type='i' direction='in'/><arg type='s' direction='in'/></method>" +
              $"<property name='Menu' type='o' access='read'/><property name='IconName' type='s' access='read'/>" +
              "<property name='IconPixmap' type='a(iiay)' access='read'/><property name='Id' type='s' access='read'/>" +
              "<property name='Category' type='s' access='read'/><property name='Status' type='s' access='read'/></interface>" +
              $"<interface name='{PropertiesInterface}'><method name='Get'><arg type='s' direction='in'/><arg type='s' direction='in'/><arg type='v' direction='out'/></method>" +
              "<method name='GetAll'><arg type='s' direction='in'/><arg type='a{{sv}}' direction='out'/></method></interface>" +
              "<interface name='org.freedesktop.DBus.Introspectable'><method name='Introspect'><arg type='s' direction='out'/></method></interface>" +
              "</node>"
            : "<!DOCTYPE node PUBLIC '-//freedesktop//DTD D-BUS Object Introspection 1.0//EN' " +
              "'http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd'><node>" +
              $"<interface name='{MenuInterface}'><method name='GetLayout'><arg type='i' direction='in'/><arg type='i' direction='in'/><arg type='as' direction='in'/>" +
              "<arg type='u' direction='out'/><arg type='(ia{sv}as)' direction='out'/></method>" +
              "<method name='GetGroupProperties'><arg type='ai' direction='in'/><arg type='as' direction='in'/><arg type='a(ia{sv})' direction='out'/></method>" +
              "<method name='Event'><arg type='i' direction='in'/><arg type='s' direction='in'/><arg type='v' direction='in'/><arg type='u' direction='in'/></method>" +
              "<method name='AboutToShow'><arg type='i' direction='in'/><arg type='b' direction='out'/></method>" +
              "<signal name='LayoutUpdated'><arg type='u'/><arg type='i'/></signal>" +
              $"<property name='Version' type='u' access='read'/><property name='Status' type='s' access='read'/></interface>" +
              $"<interface name='{PropertiesInterface}'><method name='Get'><arg type='s' direction='in'/><arg type='s' direction='in'/><arg type='v' direction='out'/></method>" +
              "<method name='GetAll'><arg type='s' direction='in'/><arg type='a{{sv}}' direction='out'/></method></interface>" +
              "<interface name='org.freedesktop.DBus.Introspectable'><method name='Introspect'><arg type='s' direction='out'/></method></interface>" +
              "</node>";

    // --- com.canonical.dbusmenu serving ---

    private sealed class MenuEntry
    {
        public int Id;
        public string Label = "";
        public bool Separator;
        public string? ToggleType;       // "radio" when the entry tracks a setting
        public int ToggleState;          // 1 = the active choice
        public List<MenuEntry> Children = [];
    }

    /// <summary>
    /// The menu, rebuilt on every query so the radio marks always mirror the live config. Sink ids
    /// come from the host at this moment, which is what makes a hotplug visible on the next menu
    /// open.
    /// </summary>
    private MenuEntry BuildMenu()
    {
        var config = host.Config;
        var root = new MenuEntry { Id = 0 };
        var seps = 0;
        MenuEntry Sep()
        {
            // uint + int is long in C#, and MenuEntry.Id is int -- the cast keeps both worlds quiet.
            var entry = new MenuEntry { Id = (int)(IdSepBase + seps++), Separator = true };
            root.Children.Add(entry);
            return entry;
        }

        // The order mirrors the Windows TrayIcon exactly: the clips block, the settings block,
        // then logs/updates/restart/exit, with the Linux-only autostart checkbox before Restart.
        root.Children.Add(new MenuEntry { Id = (int)IdOpenFolder, Label = "Open Clips Folder" });
        root.Children.Add(new MenuEntry { Id = (int)IdOpenLastClip, Label = "Open Last Clip" });
        root.Children.Add(new MenuEntry { Id = (int)IdPickFolder, Label = "Select Clips Folder..." });
        root.Children.Add(new MenuEntry { Id = (int)IdOpenConfig, Label = "Open Config" });
        Sep();

        root.Children.Add(Submenu(IdSubShortDuration, "Short Clip Duration",
            Radio(IdShort15, "15 seconds", config.ShortClipSeconds, 15),
            Radio(IdShort30, "30 seconds", config.ShortClipSeconds, 30),
            Radio(IdShort60, "60 seconds", config.ShortClipSeconds, 60)));
        root.Children.Add(Submenu(IdSubLongDuration, "Long Clip Duration",
            Radio(IdLong60, "1 minute", config.LongClipSeconds, 60),
            Radio(IdLong120, "2 minutes", config.LongClipSeconds, 120),
            Radio(IdLong180, "3 minutes", config.LongClipSeconds, 180),
            Radio(IdLong300, "5 minutes", config.LongClipSeconds, 300)));
        root.Children.Add(Submenu(IdSubBitrate, "Video Bitrate / RAM",
            BitrateOptions.Select(o => RadioInt(o.Id, o.Label, config.VideoBitrateMbps, o.Mbps)).ToArray()));
        root.Children.Add(Submenu(IdSubEncoder, "Video Encoder",
            RadioName(IdEncoderDirect, "Direct NVENC (0-copy VRAM, no ffmpeg)", config.VideoEncoder, "nvenc_direct"),
            RadioName(IdEncoderNvenc, "NVIDIA NVENC via ffmpeg (h264_nvenc)", config.VideoEncoder, "h264_nvenc"),
            RadioName(IdEncoderX264, "CPU x264 (libx264)", config.VideoEncoder, "libx264")));
        root.Children.Add(Submenu(IdSubCodec, "Video Codec",
            RadioInt(IdCodecHevc, "HEVC / H.265  (sharper at the same bitrate)", config.UseHevc ? 1 : 0, 1),
            RadioInt(IdCodecH264, "H.264  (wider compatibility)", config.UseHevc ? 1 : 0, 0)));
        root.Children.Add(Submenu(IdSubNotifications, "Notifications",
            RadioInt(IdNotifyOverlay, "Game Overlay (Medal style)", config.UseOverlayNotification ? 1 : 0, 1),
            RadioInt(IdNotifyToast, "Windows Toast", config.UseWindowsToastNotification ? 1 : 0, 1),
            RadioInt(IdNotifyNone, "Sound Only", !config.UseOverlayNotification && !config.UseWindowsToastNotification ? 1 : 0, 1),
            Check(IdNotifySound, "Play Sound Notification", config.PlaySoundNotification)));
        root.Children.Add(HotkeySubmenu(IdSubShortHotkey, "Short Clip Hotkey", isShort: true));
        root.Children.Add(HotkeySubmenu(IdSubLongHotkey, "Long Clip Hotkey", isShort: false));
        root.Children.Add(Submenu(IdSubQuota, "Clips Storage Limit",
            RadioInt(IdQuota0, "Unlimited", config.MaxClipsFolderSizeGB, 0),
            RadioInt(IdQuota10, "10 GB", config.MaxClipsFolderSizeGB, 10),
            RadioInt(IdQuota20, "20 GB", config.MaxClipsFolderSizeGB, 20),
            RadioInt(IdQuota30, "30 GB", config.MaxClipsFolderSizeGB, 30),
            RadioInt(IdQuota50, "50 GB", config.MaxClipsFolderSizeGB, 50)));
        root.Children.Add(AudioSubmenu(IdSubAudio));

        Sep();
        root.Children.Add(new MenuEntry
        {
            Id = (int)IdStartWithSystem,
            Label = "Start with System",
            ToggleType = "checkmark",
            ToggleState = LinuxDesktopIntegration.IsAutostartEnabled() ? 1 : 0,
        });
        root.Children.Add(new MenuEntry { Id = (int)IdOpenLogs, Label = "Open Logs Folder" });
        root.Children.Add(new MenuEntry { Id = (int)IdCheckUpdates, Label = "Check for Updates" });
        root.Children.Add(new MenuEntry { Id = (int)IdRestart, Label = "Restart" });
        root.Children.Add(new MenuEntry { Id = (int)IdExit, Label = "Exit" });
        return root;

        MenuEntry Submenu(uint id, string title, params MenuEntry[] children)
        {
            return new MenuEntry { Id = (int)id, Label = title, Children = [.. children] };
        }

        // Local functions cannot be overloaded in C#, so each numeric shape carries its own name:
        // Radio for the double-valued durations, RadioInt for the whole-number settings,
        // RadioName for the settings whose value is a string (the encoder).
        MenuEntry Radio(uint id, string label, double current, double choice) =>
            new() { Id = (int)id, Label = label, ToggleType = "radio", ToggleState = Math.Abs(current - choice) < 0.5 ? 1 : 0 };

        MenuEntry RadioInt(uint id, string label, int current, int choice) =>
            new() { Id = (int)id, Label = label, ToggleType = "radio", ToggleState = current == choice ? 1 : 0 };

        MenuEntry RadioName(uint id, string label, string current, string choice) =>
            new() { Id = (int)id, Label = label, ToggleType = "radio", ToggleState = string.Equals(current, choice, StringComparison.OrdinalIgnoreCase) ? 1 : 0 };

        MenuEntry Check(uint id, string label, bool current) =>
            new() { Id = (int)id, Label = label, ToggleType = "checkmark", ToggleState = current ? 1 : 0 };

        MenuEntry HotkeySubmenu(uint id, string title, bool isShort)
        {
            // The binding currently in force is named in the title, exactly as on Windows.
            var current = isShort ? config.ShortClipHotkey : config.LongClipHotkey;
            var idBase = isShort ? IdHotkeyShortBase : IdHotkeyLongBase;
            var entry = new MenuEntry { Id = (int)id, Label = $"{title} (now {current})", Children = [] };
            for (var i = 0; i < HotkeyChoices.Length; i++)
            {
                if (i == HotkeySeparatorAfter)
                    entry.Children.Add(new MenuEntry { Id = (int)(IdSepBase + 100 + (isShort ? 0 : 50) + i), Separator = true });

                var (key, label) = HotkeyChoices[i];
                entry.Children.Add(new MenuEntry
                {
                    Id = (int)(idBase + (uint)i),
                    Label = label,
                    ToggleType = "radio",
                    ToggleState = string.Equals(key, current, StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                });
            }
            return entry;
        }

        MenuEntry AudioSubmenu(uint id)
        {
            // Rebuilt on every open, so a headset plugged in since the last open shows up.
            var current = config.AudioDevice;
            var isDefault = string.IsNullOrWhiteSpace(current)
                || string.Equals(current, "default", StringComparison.OrdinalIgnoreCase);
            var entry = new MenuEntry { Id = (int)id, Label = "Audio Device", Children = [] };
            entry.Children.Add(new MenuEntry
            {
                Id = (int)IdAudioDefault,
                Label = "Default (System)",
                ToggleType = "radio",
                ToggleState = isDefault ? 1 : 0,
            });
            var sinks = host.ListAudioSinks();
            if (sinks.Count > 0)
                entry.Children.Add(new MenuEntry { Id = (int)(IdSepBase + 200), Separator = true });
            for (var i = 0; i < Math.Min(sinks.Count, MaxAudioDevices); i++)
            {
                var (sinkName, description) = sinks[i];
                entry.Children.Add(new MenuEntry
                {
                    Id = (int)(AudioDeviceIdBase + (uint)i),
                    Label = string.IsNullOrEmpty(description) ? sinkName : description,
                    ToggleType = "radio",
                    ToggleState = string.Equals(current, sinkName, StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                });
            }
            return entry;
        }
    }

    private MenuEntry? FindEntry(int id)
    {
        var root = BuildMenu();
        if (id == 0)
            return root;
        var stack = new Stack<MenuEntry>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var entry = stack.Pop();
            if (entry.Id == id)
                return entry;
            foreach (var child in entry.Children)
                stack.Push(child);
        }
        return null;
    }

    private byte[] ServeGetLayout(DbusReader body)
    {
        var parentId = body.ReadInt32();
        body.ReadInt32();  // recursion depth: ignored, the tree is small enough to always send whole
        body.ReadStringArray();
        var entry = FindEntry(parentId) ?? FindEntry(0)!;

        return WriteBody(w =>
        {
            w.UInt32(menuRevision);
            WriteLayoutEntry(w, entry);
        });
    }

    /// <summary>
    /// (ia{sv}av): this item, its properties, and its children -- each child wrapped in a VARIANT
    /// that carries its own recursive layout, exactly as libdbusmenu serialises it. The menu is
    /// small, so the whole tree goes out on every query; the radio marks therefore never lag.
    /// </summary>
    private void WriteLayoutEntry(DbusWriter w, MenuEntry entry)
    {
        w.Align(8); // the struct inside its variant (or the top-level body, which is aligned anyway)
        w.Int32(entry.Id);

        var (propsLength, propsContent) = w.BeginArray(8);
        WriteEntryProperties(w, entry);
        w.EndArray(propsLength, propsContent);

        // VARIANT aligns to 1, so the child array itself carries no padding; each variant's inner
        // struct does its own Align(8).
        var (childrenLength, childrenContent) = w.BeginArray(1);
        foreach (var child in entry.Children)
        {
            w.Variant("(ia{sv}av)", x => WriteLayoutEntry(x, child));
        }
        w.EndArray(childrenLength, childrenContent);
    }

    private void WriteEntryProperties(DbusWriter w, MenuEntry entry)
    {
        // Dict entries are structs: every one sits on an 8-byte boundary, or the broker rejects
        // the reply and disconnects us.
        void Prop(string name, string signature, Action<DbusWriter> value)
        {
            w.Align(8);
            w.String(name);
            w.Variant(signature, value);
        }

        if (entry.Separator)
        {
            Prop("type", "s", x => x.String("separator"));
            return;
        }

        Prop("label", "s", x => x.String(entry.Label));
        Prop("enabled", "b", x => x.Bool(true));
        Prop("visible", "b", x => x.Bool(true));
        if (entry.Children.Count > 0)
            Prop("children-display", "s", x => x.String("submenu"));
        if (entry.ToggleType is not null)
        {
            Prop("toggle-type", "s", x => x.String(entry.ToggleType));
            Prop("toggle-state", "i", x => x.Int32(entry.ToggleState));
        }
    }

    private byte[] ServeGetGroupProperties(DbusReader body)
    {
        var ids = body.ReadInt32Array();
        body.ReadStringArray(); // the property-name filter; we always return everything
        var (lengthPosition, contentStart) = (0, 0);
        return WriteBody(w =>
        {
            (lengthPosition, contentStart) = w.BeginArray(8);
            foreach (var id in ids)
            {
                var entry = FindEntry(id);
                if (entry is null)
                    continue;
                w.Align(8);
                w.Int32(entry.Id);
                var (propsLength, propsContent) = w.BeginArray(8);
                WriteEntryProperties(w, entry);
                w.EndArray(propsLength, propsContent);
            }
            w.EndArray(lengthPosition, contentStart);
        });
    }

    private byte[] ServeEvent(DbusReader body)
    {
        var id = body.ReadInt32();
        var eventName = body.ReadString();
        if (eventName == "clicked")
            OnMenuClicked(id);
        return [];
    }

    private void OnMenuClicked(int id)
    {
        var config = host.Config;
        void SaveConfig(string what)
        {
            config.Save();
            BumpMenu();
            Console.WriteLine($"Tray: {what} saved to config.json.");
        }

        // Bitrates and hotkeys are matched from their tables rather than one case per value --
        // the same table-over-switch choice the Windows handler makes.
        var bitrate = Array.Find(BitrateOptions, o => o.Id == (uint)id);
        if (bitrate.Mbps > 0)
        {
            config.VideoBitrateMbps = bitrate.Mbps;
            SaveConfig($"Video bitrate {bitrate.Mbps} Mbps");
            host.SetVideoEncoding(config.VideoBitrateMbps, config.VideoCodec, config.VideoEncoder);
            return;
        }

        // Hotkey ids are ranges over the choices table, one per clip, exactly like the Windows
        // handler; id - uint is long in C#, so the arithmetic comes back to int explicitly.
        var shortIndex = id - (int)IdHotkeyShortBase;
        var longIndex = id - (int)IdHotkeyLongBase;
        string? hotkeyKey = null;
        var hotkeyIsShort = false;
        if (shortIndex >= 0 && shortIndex < HotkeyChoices.Length)
        {
            hotkeyIsShort = true;
            hotkeyKey = HotkeyChoices[shortIndex].Key;
        }
        else if (longIndex >= 0 && longIndex < HotkeyChoices.Length)
        {
            hotkeyKey = HotkeyChoices[longIndex].Key;
        }

        if (hotkeyKey is not null)
        {
            // One key can only mean one thing: refuse a binding that would leave the other clip
            // with no hotkey at all -- the same rejection the Windows menu makes.
            var other = hotkeyIsShort ? config.LongClipHotkey : config.ShortClipHotkey;
            if (string.Equals(hotkeyKey, other, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Tray: {hotkeyKey} is already bound to the other clip; ignored.");
                return;
            }

            if (hotkeyIsShort)
            {
                config.ShortClipHotkey = hotkeyKey;
                SaveConfig($"Short clip hotkey {hotkeyKey}");
            }
            else
            {
                config.LongClipHotkey = hotkeyKey;
                SaveConfig($"Long clip hotkey {hotkeyKey}");
            }
            // The hook rebinds live: the next keypress follows the menu, no restart.
            host.RebindHotkeys(config.ShortClipHotkey, config.LongClipHotkey);
            return;
        }

        switch ((uint)id)
        {
            case IdOpenFolder:
                OpenPath(host.ClipsFolder);
                break;
            case IdOpenLastClip:
                OpenLastClip();
                break;
            case IdPickFolder:
                PickClipsFolder();
                break;
            case IdOpenConfig:
                OpenPath(host.ConfigPath);
                break;
            case IdShort15: config.ShortClipSeconds = 15; SaveConfig("Short clip duration 15 s"); break;
            case IdShort30: config.ShortClipSeconds = 30; SaveConfig("Short clip duration 30 s"); break;
            case IdShort60: config.ShortClipSeconds = 60; SaveConfig("Short clip duration 60 s"); break;
            case IdLong60: config.LongClipSeconds = 60; SaveConfig("Long clip duration 1 m"); break;
            case IdLong120: config.LongClipSeconds = 120; SaveConfig("Long clip duration 2 m"); break;
            case IdLong180: config.LongClipSeconds = 180; SaveConfig("Long clip duration 3 m"); break;
            case IdLong300: config.LongClipSeconds = 300; SaveConfig("Long clip duration 5 m"); break;
            case IdEncoderDirect:
                config.VideoEncoder = "nvenc_direct";
                SaveConfig("Video encoder Direct NVENC");
                host.SetVideoEncoding(config.VideoBitrateMbps, config.VideoCodec, config.VideoEncoder);
                break;
            case IdEncoderNvenc:
                config.VideoEncoder = "h264_nvenc";
                SaveConfig("Video encoder h264_nvenc");
                host.SetVideoEncoding(config.VideoBitrateMbps, config.VideoCodec, config.VideoEncoder);
                break;
            case IdEncoderX264:
                config.VideoEncoder = "libx264";
                SaveConfig("Video encoder libx264");
                host.SetVideoEncoding(config.VideoBitrateMbps, config.VideoCodec, config.VideoEncoder);
                break;
            case IdCodecHevc:
                config.VideoCodec = "hevc";
                SaveConfig("Video codec HEVC");
                host.SetVideoEncoding(config.VideoBitrateMbps, config.VideoCodec, config.VideoEncoder);
                break;
            case IdCodecH264:
                config.VideoCodec = "h264";
                SaveConfig("Video codec H.264");
                host.SetVideoEncoding(config.VideoBitrateMbps, config.VideoCodec, config.VideoEncoder);
                break;
            case IdNotifyOverlay:
                config.NotificationType = "overlay";
                SaveConfig("Notifications: Game Overlay");
                break;
            case IdNotifyToast:
                config.NotificationType = "windows_toast";
                SaveConfig("Notifications: Windows Toast");
                break;
            case IdNotifyNone:
                config.NotificationType = "none";
                SaveConfig("Notifications: Sound Only");
                break;
            case IdNotifySound:
                config.PlaySoundNotification = !config.PlaySoundNotification;
                SaveConfig($"Play sound notification {(config.PlaySoundNotification ? "on" : "off")}");
                break;
            case IdQuota0: config.MaxClipsFolderSizeGB = 0; SaveConfig("Clips storage limit Unlimited"); break;
            case IdQuota10: config.MaxClipsFolderSizeGB = 10; SaveConfig("Clips storage limit 10 GB"); break;
            case IdQuota20: config.MaxClipsFolderSizeGB = 20; SaveConfig("Clips storage limit 20 GB"); break;
            case IdQuota30: config.MaxClipsFolderSizeGB = 30; SaveConfig("Clips storage limit 30 GB"); break;
            case IdQuota50: config.MaxClipsFolderSizeGB = 50; SaveConfig("Clips storage limit 50 GB"); break;
            case IdAudioDefault:
                host.SetAudioDevice("default");
                BumpMenu();
                break;
            case IdStartWithSystem:
            {
                // Autostart lives outside config.json: nothing to save, just flip the boot hook
                // and refresh the checkbox.
                var now = !LinuxDesktopIntegration.IsAutostartEnabled();
                LinuxDesktopIntegration.SetAutostart(now);
                Console.WriteLine($"Tray: Start with System {(now ? "enabled" : "disabled")}.");
                BumpMenu();
                break;
            }
            case IdCheckUpdates:
                CheckForUpdates();
                break;
            case IdRestart:
                host.RestartProcess();
                break;
            case IdExit:
                host.RequestExit();
                break;
            default:
            {
                // id - uint is long; the list index and the range check want the int back.
                var audioIndex = (int)(id - AudioDeviceIdBase);
                if (audioIndex >= 0 && audioIndex < MaxAudioDevices)
                {
                    var sinks = host.ListAudioSinks();
                    if (audioIndex < sinks.Count)
                    {
                        host.SetAudioDevice(sinks[audioIndex].Name);
                        BumpMenu();
                    }
                }
                break;
            }
        }
    }

    /// <summary>
    /// The clips folder picker: zenity or kdialog, whichever the desktop ships. Both print the
    /// chosen directory on stdout; a cancelled dialog prints nothing and changes nothing.
    /// </summary>
    private void PickClipsFolder()
    {
        foreach (var picker in new[] { "zenity", "kdialog" })
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(picker)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                if (picker == "zenity")
                    psi.ArgumentList.Add("--file-selection");
                else
                    psi.ArgumentList.Add("--getexistingdirectory");
                psi.ArgumentList.Add(host.ClipsFolder);

                using var process = System.Diagnostics.Process.Start(psi);
                if (process is null)
                    continue;
                var dir = process.StandardOutput.ReadToEnd().Trim().Trim('"');
                process.WaitForExit(60_000);
                if (process.ExitCode != 0 || string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
                    continue;

                host.Config.OutputFolder = dir;
                host.Config.Save();
                host.SetClipsFolder(dir);
                BumpMenu();
                Console.WriteLine($"Tray: clips folder set to {dir}.");
                return;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // No such picker: try the next one.
            }
        }
        Console.WriteLine("Tray: no folder picker found (install zenity or kdialog).");
    }

    private void CheckForUpdates()
    {
        // Fire and forget: the check is a network call, and the tray must not stall on it.
        _ = Task.Run(async () =>
        {
            try
            {
                var found = await UpdateService.CheckForUpdateAsync();
                if (found.Available)
                    LinuxNotifications.ShowClipSaved(host.Config, "Clippy Update",
                        $"{found.Tag} is available. Restart installs it.");
                else
                    LinuxNotifications.ShowClipSaved(host.Config, "Clippy Update",
                        $"{UpdateService.Version} is current; nothing to install.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Tray: update check failed ({ex.Message}).");
            }
        });
    }

    private void SaveShort()
    {
        var path = host.SaveClip(true);
        if (!string.IsNullOrEmpty(path))
            Console.WriteLine($"Tray: saved {path}");
    }

    private void SaveLong()
    {
        var path = host.SaveClip(false);
        if (!string.IsNullOrEmpty(path))
            Console.WriteLine($"Tray: saved {path}");
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open")
                {
                    ArgumentList = { path },
                });
            else
                Console.WriteLine($"Tray: {path} does not exist yet.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"Tray: xdg-open failed ({ex.Message}).");
        }
    }

    private void OpenLastClip()
    {
        try
        {
            var newest = new DirectoryInfo(host.ClipsFolder)
                .GetFiles("*.mp4", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.CreationTimeUtc)
                .FirstOrDefault();
            if (newest is null)
            {
                Console.WriteLine("Tray: no clip has been saved yet.");
                return;
            }
            OpenPath(newest.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Tray: could not list the clips folder ({ex.Message}).");
        }
    }

    private void BumpMenu()
    {
        if (!registered || bus is null)
            return;
        menuRevision++;
        bus.SendSignal(MenuPath, MenuInterface, "LayoutUpdated", "ui", w =>
        {
            w.UInt32(menuRevision);
            w.Int32(0);
        });
    }

    public void Dispose()
    {
        // Closing the bus connection removes the item from the watcher implicitly.
        bus?.Dispose();
        bus = null;
    }
}
#endif

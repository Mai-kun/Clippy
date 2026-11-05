#if LINUX
using System.Diagnostics;

namespace Clippy;

/// <summary>
/// The Linux tray: an org.kde.StatusNotifierItem plus a com.canonical.dbusmenu tree, served over
/// this program's own D-Bus wire layer. SNI is what KDE Plasma's system tray hosts natively, and
/// what Waybar and the other Niri-era bars render through their SNI applets -- Wayland has no
/// global-status protocol of its own, so this is the only tray that works on both.
///
/// The menu mirrors the Windows TrayIcon: save-short / save-long, the open-folder entries, and the
/// rebindable settings (durations, bitrate, codec, audio device) applied live where possible.
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
        IReadOnlyList<(string Name, string Description)> ListAudioSinks();
        void RequestExit();
    }

    private const string ItemPath = "/StatusNotifierItem";
    private const string MenuPath = "/MenuBar";
    private const string SniInterface = "org.kde.StatusNotifierItem";
    private const string MenuInterface = "com.canonical.dbusmenu";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string WatcherName = "org.kde.StatusNotifierWatcher";

    // Menu ids are fixed so Event() is a switch; sinks get ids from FirstSinkId upward.
    private const int IdSaveShort = 1;
    private const int IdSaveLong = 2;
    private const int IdSepAfterSave = 3;
    private const int IdOpenClips = 4;
    private const int IdOpenLogs = 5;
    private const int IdOpenLastClip = 6;
    private const int IdOpenConfig = 7;
    private const int IdSepAfterOpen = 8;
    private const int IdShortDuration = 9;
    private const int IdLongDuration = 10;
    private const int IdBitrate = 18;
    private const int IdCodec = 26;
    private const int IdAudioDevice = 29;
    private const int IdSepBeforeExit = 100;
    private const int IdExit = 101;
    private const int FirstSinkId = 1000;

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
                    case "IconName": w.Variant("s", x => x.String("clippy")); break;
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
        Prop("IconName", "s", x => x.String("clippy"));
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
        w.WriteByteArray(LinuxDesktopIntegration.TrayIconArgb(size));
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
    /// The menu, rebuilt on every query so the radio marks always mirror the live config. Sink
    /// ids start at <see cref="FirstSinkId"/>; the sinks themselves come from the host at this
    /// moment, which is what makes a hotplug visible on the next menu open.
    /// </summary>
    private MenuEntry BuildMenu()
    {
        var config = host.Config;
        var root = new MenuEntry { Id = 0 };

        root.Children.Add(new MenuEntry { Id = IdSaveShort, Label = $"Save Short Clip ({config.ShortClipHotkey})" });
        root.Children.Add(new MenuEntry { Id = IdSaveLong, Label = $"Save Long Clip ({config.LongClipHotkey})" });
        root.Children.Add(new MenuEntry { Id = IdSepAfterSave, Separator = true });
        root.Children.Add(new MenuEntry { Id = IdOpenClips, Label = "Open clips folder" });
        root.Children.Add(new MenuEntry { Id = IdOpenLogs, Label = "Open logs folder" });
        root.Children.Add(new MenuEntry { Id = IdOpenLastClip, Label = "Open Last Clip" });
        root.Children.Add(new MenuEntry { Id = IdOpenConfig, Label = "Open config.json" });
        root.Children.Add(new MenuEntry { Id = IdSepAfterOpen, Separator = true });

        root.Children.Add(new MenuEntry
        {
            Id = IdShortDuration,
            Label = "Short Clip Duration",
            Children =
            [
                Radio(IdShortDuration + 1, "15 s", config.ShortClipSeconds, 15),
                Radio(IdShortDuration + 2, "30 s", config.ShortClipSeconds, 30),
                Radio(IdShortDuration + 3, "60 s", config.ShortClipSeconds, 60),
            ],
        });
        root.Children.Add(new MenuEntry
        {
            Id = IdLongDuration,
            Label = "Long Clip Duration",
            Children =
            [
                Radio(IdLongDuration + 4, "1 m", config.LongClipSeconds, 60),
                Radio(IdLongDuration + 5, "2 m", config.LongClipSeconds, 120),
                Radio(IdLongDuration + 6, "3 m", config.LongClipSeconds, 180),
                Radio(IdLongDuration + 7, "5 m", config.LongClipSeconds, 300),
            ],
        });
        root.Children.Add(new MenuEntry
        {
            Id = IdBitrate,
            Label = "Video Bitrate",
            Children =
            [
                Radio(IdBitrate + 1, "2 Mbps", config.VideoBitrateMbps, 2),
                Radio(IdBitrate + 2, "4 Mbps", config.VideoBitrateMbps, 4),
                Radio(IdBitrate + 3, "6 Mbps", config.VideoBitrateMbps, 6),
                Radio(IdBitrate + 4, "8 Mbps", config.VideoBitrateMbps, 8),
                Radio(IdBitrate + 5, "12 Mbps", config.VideoBitrateMbps, 12),
                Radio(IdBitrate + 6, "20 Mbps", config.VideoBitrateMbps, 20),
                Radio(IdBitrate + 7, "50 Mbps", config.VideoBitrateMbps, 50),
            ],
        });
        root.Children.Add(new MenuEntry
        {
            Id = IdCodec,
            Label = "Video Codec",
            Children =
            [
                new MenuEntry { Id = IdCodec + 1, Label = "HEVC (H.265)", ToggleType = "radio", ToggleState = config.UseHevc ? 1 : 0 },
                new MenuEntry { Id = IdCodec + 2, Label = "H.264", ToggleType = "radio", ToggleState = config.UseHevc ? 0 : 1 },
            ],
        });
        root.Children.Add(new MenuEntry
        {
            Id = IdAudioDevice,
            Label = "Audio Device",
            Children = AudioDeviceEntries(config),
        });

        root.Children.Add(new MenuEntry { Id = IdSepBeforeExit, Separator = true });
        root.Children.Add(new MenuEntry { Id = IdExit, Label = "Exit" });
        return root;
    }

    private List<MenuEntry> AudioDeviceEntries(ClippyConfig config)
    {
        var entries = new List<MenuEntry>
        {
            new() { Id = FirstSinkId, Label = "Default (System)", ToggleType = "radio", ToggleState = IsDefault(config.AudioDevice) ? 1 : 0 },
        };
        var sinks = host.ListAudioSinks();
        for (var i = 0; i < sinks.Count; i++)
        {
            var (name, description) = sinks[i];
            entries.Add(new MenuEntry
            {
                Id = FirstSinkId + 1 + i,
                Label = string.IsNullOrEmpty(description) ? name : description,
                ToggleType = "radio",
                ToggleState = string.Equals(config.AudioDevice, name, StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            });
        }
        return entries;
    }

    private static bool IsDefault(string configured) =>
        string.IsNullOrWhiteSpace(configured) || string.Equals(configured, "default", StringComparison.OrdinalIgnoreCase);

    private static MenuEntry Radio(int id, string label, double current, double choice) =>
        new() { Id = id, Label = label, ToggleType = "radio", ToggleState = Math.Abs(current - choice) < 0.5 ? 1 : 0 };

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

        switch (id)
        {
            case IdSaveShort:
                SaveShort();
                break;
            case IdSaveLong:
                SaveLong();
                break;
            case IdOpenClips:
                OpenPath(host.ClipsFolder);
                break;
            case IdOpenLogs:
                OpenPath(host.LogsFolder);
                break;
            case IdOpenLastClip:
                OpenLastClip();
                break;
            case IdOpenConfig:
                OpenPath(host.ConfigPath);
                break;
            case IdShortDuration + 1: config.ShortClipSeconds = 15; SaveConfig("Short clip duration 15 s"); break;
            case IdShortDuration + 2: config.ShortClipSeconds = 30; SaveConfig("Short clip duration 30 s"); break;
            case IdShortDuration + 3: config.ShortClipSeconds = 60; SaveConfig("Short clip duration 60 s"); break;
            case IdLongDuration + 4: config.LongClipSeconds = 60; SaveConfig("Long clip duration 1 m"); break;
            case IdLongDuration + 5: config.LongClipSeconds = 120; SaveConfig("Long clip duration 2 m"); break;
            case IdLongDuration + 6: config.LongClipSeconds = 180; SaveConfig("Long clip duration 3 m"); break;
            case IdLongDuration + 7: config.LongClipSeconds = 300; SaveConfig("Long clip duration 5 m"); break;
            case IdBitrate + 1: config.VideoBitrateMbps = 2; SaveConfig("Video bitrate 2 Mbps (applies from the next recording)"); break;
            case IdBitrate + 2: config.VideoBitrateMbps = 4; SaveConfig("Video bitrate 4 Mbps (applies from the next recording)"); break;
            case IdBitrate + 3: config.VideoBitrateMbps = 6; SaveConfig("Video bitrate 6 Mbps (applies from the next recording)"); break;
            case IdBitrate + 4: config.VideoBitrateMbps = 8; SaveConfig("Video bitrate 8 Mbps (applies from the next recording)"); break;
            case IdBitrate + 5: config.VideoBitrateMbps = 12; SaveConfig("Video bitrate 12 Mbps (applies from the next recording)"); break;
            case IdBitrate + 6: config.VideoBitrateMbps = 20; SaveConfig("Video bitrate 20 Mbps (applies from the next recording)"); break;
            case IdBitrate + 7: config.VideoBitrateMbps = 50; SaveConfig("Video bitrate 50 Mbps (applies from the next recording)"); break;
            case IdCodec + 1: config.VideoCodec = "hevc"; SaveConfig("Video codec HEVC (applies from the next recording)"); break;
            case IdCodec + 2: config.VideoCodec = "h264"; SaveConfig("Video codec H.264 (applies from the next recording)"); break;
            case IdExit:
                host.RequestExit();
                break;
            default:
                if (id >= FirstSinkId)
                {
                    var sinks = host.ListAudioSinks();
                    if (id == FirstSinkId)
                    {
                        host.SetAudioDevice("default");
                        BumpMenu();
                    }
                    else if (id - FirstSinkId - 1 < sinks.Count)
                    {
                        host.SetAudioDevice(sinks[id - FirstSinkId - 1].Name);
                        BumpMenu();
                    }
                }
                break;
        }
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
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { path } });
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

    /// <summary>Tells the bar the menu changed; it re-queries and the radio marks refresh.</summary>
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

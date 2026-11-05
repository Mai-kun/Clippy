#if LINUX
using System.Text;

namespace Clippy;

/// <summary>
/// One XDG Desktop Portal ScreenCast session: the handshake that turns "record my monitor" into a
/// PipeWire node descriptor without a single external dependency.
///
/// The flow per the org.freedesktop.portal.ScreenCast contract: CreateSession, SelectSources (with
/// persist_mode = 2 and a stored restore_token, so the monitor picker only ever appears once),
/// Start, OpenPipeWireRemote. Each of the first three answers through a Response signal on the
/// request handle the call itself returns; Start's response carries the "streams" array whose node
/// id identifies the capture; OpenPipeWireRemote returns the fd to hand to pipewiresrc.
///
/// The fd is deliberately received WITHOUT close-on-exec: the gst child inherits it under the same
/// number, which is how "pipewiresrc fd=N" reaches a stream that exists only inside the portal's
/// PipeWire instance.
/// </summary>
internal sealed class LinuxPortal : IDisposable
{
    private const string PortalDestination = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string ScreenCastInterface = "org.freedesktop.portal.ScreenCast";
    private const string RequestInterface = "org.freedesktop.portal.Request";

    /// <summary>The persistent monitor choice. 2 = "remember permanently".</summary>
    private const uint PersistModePermanent = 2;
    /// <summary>Source types: 1 = MONITOR.</summary>
    private const uint SourceTypesMonitor = 1;
    /// <summary>Cursor modes: 1 = hidden, 2 = embedded. Embedded matches the Windows capture, which includes the cursor.</summary>
    private const uint CursorModeEmbedded = 2;

    /// <summary>Long enough for a user to find the monitor picker on the first run.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);

    private readonly LinuxDbus bus;
    private readonly string tokenFile;

    private LinuxPortal(LinuxDbus bus, string tokenFile)
    {
        this.bus = bus;
        this.tokenFile = tokenFile;
    }

    /// <summary>
    /// The negotiated capture: the node id, the connection to read it through, and the stream size
    /// the portal reported. The size comes from the session rather than gst's -v dump on purpose:
    /// that dump goes to stdout, which on this pipeline is the pixel pipe itself.
    /// </summary>
    public readonly record struct Session(uint NodeId, int PipeWireFd, int Width, int Height);

    /// <summary>
    /// Connects and returns a portal handle. The caller MUST keep it alive for as long as the
    /// capture runs and Dispose it afterwards: the portal tears the session -- and the PipeWire
    /// node with it -- down the moment the D-Bus connection closes, which showed up as
    /// pipewiresrc's "target not found" after a perfectly successful handshake.
    /// </summary>
    public static LinuxPortal Create(string tokenFile)
    {
        var bus = LinuxDbus.Connect();
        return new LinuxPortal(bus, tokenFile);
    }

    /// <summary>
    /// Runs the whole handshake and returns the capture session. The restore token lives in the
    /// file the constructor was given (portal_token.txt next to config.json).
    /// </summary>
    public Session StartSession() => RunSession();

    private Session RunSession()
    {
        var restoreToken = ReadRestoreToken();

        // The unique name from Hello is what the request paths are built from, and each of the
        // three portal calls needs its OWN token -- the request path is derived from it, and two
        // calls sharing one token would share one response channel.
        var sender = bus.Hello();
        var senderEscaped = sender.TrimStart(':').Replace('.', '_');
        string RequestPathFor(string token) =>
            $"/org/freedesktop/portal/desktop/request/{senderEscaped}/{token}";

        // The Response signal for a request can beat the method reply out of the daemon -- the
        // spec is explicit that the match rule must be in place BEFORE the call -- so every match
        // is registered first, against the request path the token determines.
        void ArmRequest(string token) =>
            bus.AddMatch($"type='signal',sender='{PortalDestination}',path='{RequestPathFor(token)}'," +
                         $"interface='{RequestInterface}',member='Response'");

        // CreateSession. Its reply is the session handle; its Response signal carries nothing we need.
        var sessionToken = NewToken();
        ArmRequest(sessionToken);
        var createRequestPath = bus.Call(PortalDestination, PortalPath, ScreenCastInterface, "CreateSession", "a{sv}",
            w => w.WriteOptionsDict(OptionsWithToken(sessionToken)), (int)StepTimeout.TotalMilliseconds)
            .Apply(body => new DbusReader(body).ReadObjectPath());
        Console.WriteLine($"Portal: request {createRequestPath}");
        // The REPLY carries the request handle; the SESSION handle only exists once the backend has
        // accepted the session, which is what the Response signal announces -- in its results, as
        // "session_handle".
        var createResults = WaitResponse(RequestPathFor(sessionToken), "CreateSession", StepTimeout);
        var sessionPath = createResults.TryGetValue("session_handle", out var handle)
            ? handle.String
            : createRequestPath;
        Console.WriteLine($"Portal: session {sessionPath}");

        // SelectSources: one monitor, remembered permanently. The response's restore_token is what
        // makes every later start silent.
        var selectToken = NewToken();
        ArmRequest(selectToken);
        var selectOptions = OptionsWithToken(selectToken);
        selectOptions["multiple"] = ("b", w => w.Bool(false));
        selectOptions["types"] = ("u", w => w.UInt32(SourceTypesMonitor));
        selectOptions["persist_mode"] = ("u", w => w.UInt32(PersistModePermanent));
        if (!string.IsNullOrEmpty(restoreToken))
            selectOptions["restore_token"] = ("s", w => w.WriteString(restoreToken));

        var selectPath = bus.Call(PortalDestination, PortalPath, ScreenCastInterface, "SelectSources", "oa{sv}",
            w =>
            {
                w.ObjectPath(sessionPath);
                w.WriteOptionsDict(selectOptions);
            }, (int)StepTimeout.TotalMilliseconds)
            .Apply(body => new DbusReader(body).ReadObjectPath());
        var selectResults = WaitResponse(RequestPathFor(selectToken), "SelectSources", StepTimeout);
        SaveRestoreToken(selectResults);

        // Start. The response's "streams" array names the PipeWire node that now carries the screen.
        // This is the step that can wait on the user's monitor picker, so it gets the long timeout.
        var startToken = NewToken();
        ArmRequest(startToken);
        var startPath = bus.Call(PortalDestination, PortalPath, ScreenCastInterface, "Start", "osa{sv}",
            w =>
            {
                w.ObjectPath(sessionPath);
                w.WriteString(""); // parent window: none
                w.WriteOptionsDict(OptionsWithToken(startToken));
            }, (int)StartTimeout.TotalMilliseconds)
            .Apply(body => new DbusReader(body).ReadObjectPath());
        var startResults = WaitResponse(RequestPathFor(startToken), "Start", StartTimeout);
        SaveRestoreToken(startResults);

        var streams = startResults.TryGetValue("streams", out var value)
            ? value.Streams
            : throw new IOException("Portal: Start response carried no streams.");
        if (streams.Count == 0)
            throw new IOException("Portal: the user cancelled the monitor picker (no streams).");
        var node = streams[0].Node;
        Console.WriteLine($"Portal: stream node {node}");

        // The stream's size rides in the properties as the (ii) "size" pair; the pipeline needs it
        // to tell ffmpeg the rawvideo geometry before the first frame arrives.
        int width = 1920, height = 1080;
        var sizeValue = streams[0].Props.GetValueOrDefault("size")?.String ?? "";
        var sizeParts = sizeValue.Split('x');
        if (sizeParts.Length == 2 &&
            int.TryParse(sizeParts[0], out var parsedWidth) && int.TryParse(sizeParts[1], out var parsedHeight) &&
            parsedWidth > 0 && parsedHeight > 0)
        {
            width = parsedWidth;
            height = parsedHeight;
        }
        Console.WriteLine($"Portal: stream size {width}x{height}");

        // OpenPipeWireRemote: the reply is an 'h' (fd index) and the descriptor rides in the
        // ancillary data. Taken WITHOUT close-on-exec so the gst child inherits it.
        var reply = bus.Call(PortalDestination, PortalPath, ScreenCastInterface, "OpenPipeWireRemote", "oa{sv}",
            w =>
            {
                w.ObjectPath(sessionPath);
                w.WriteOptionsDict(new Dictionary<string, (string, Action<DbusWriter>)>());
            }, (int)StepTimeout.TotalMilliseconds);
        if (reply.Fds.Length == 0)
        {
            reply.Dispose();
            throw new IOException("Portal: OpenPipeWireRemote returned no descriptor.");
        }
        var fd = reply.Fds[0];
        foreach (var extra in reply.Fds.Skip(1))
            LinuxDbus.close(extra);

        return new Session(node, fd, width, height);
    }

    /// <summary>
    /// The Response signal for one request handle: code 0 = ok, 1 = cancelled by the user,
    /// 2 = ended. The results dictionary travels in the same signal.
    /// </summary>
    private Dictionary<string, DbusValue> WaitResponse(string requestPath, string step, TimeSpan timeout)
    {
        var (code, body) = bus.WaitForSignal(RequestInterface, requestPath, (int)timeout.TotalMilliseconds);
        var results = new DbusReader(body).ReadResponseResults();
        if (code == 1)
            throw new IOException($"Portal: {step} was cancelled by the user.");
        if (code != 0)
            throw new IOException($"Portal: {step} failed with response code {code}.");
        return results;
    }

    /// <summary>
    /// The restore token, from whichever response carried it first (SelectSources and Start both
    /// may). Written next to config.json, which is where everything else this app persists lives.
    /// </summary>
    private void SaveRestoreToken(Dictionary<string, DbusValue> results)
    {
        if (!results.TryGetValue("restore_token", out var value) || string.IsNullOrEmpty(value.String))
            return;
        if (value.String == ReadRestoreToken())
            return;
        try
        {
            File.WriteAllText(tokenFile, value.String);
            Console.WriteLine($"Portal: restore token saved to {tokenFile} -- the picker will not appear again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A token that cannot be stored costs one picker next time; it must not cost the session.
            Console.WriteLine($"Portal: could not save the restore token ({ex.Message}).");
        }
    }

    private string? ReadRestoreToken()
    {
        try
        {
            return File.Exists(tokenFile) ? File.ReadAllText(tokenFile).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The token must be a valid path element; a time-and-pid stamp cannot collide with a
    /// concurrent request from this same process.
    /// </summary>
    private static string NewToken() =>
        $"clippy_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}";

    /// <summary>
    /// The token travels under TWO keys, and both are required by xdg-desktop-portal 1.22:
    /// "handle_token" names the request handle (xdp-request.c), while the session object itself is
    /// built from "session_handle_token" (xdp-session.c's lookup_session_token) -- omit that one
    /// and the portal dies on an internal assert before answering. The spec names only
    /// "handle_token", which is why every working client quietly sends both.
    /// </summary>
    private static Dictionary<string, (string Signature, Action<DbusWriter> Write)> OptionsWithToken(string token) =>
        new()
        {
            ["handle_token"] = ("s", w => w.WriteString(token)),
            ["session_handle_token"] = ("s", w => w.WriteString(token)),
        };

    public void Dispose() => bus.Dispose();
}
#endif

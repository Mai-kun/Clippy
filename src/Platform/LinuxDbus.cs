#if LINUX
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Clippy;

/// <summary>
/// A minimal D-Bus client speaking the wire format directly over the session bus socket.
///
/// Why hand-rolled: the portal's OpenPipeWireRemote returns a file descriptor, and handing a real
/// fd across requires SCM_RIGHTS ancillary data on the D-Bus socket -- something neither gdbus nor
/// busctl can put into Clippy's hands, and a NuGet D-Bus stack would have to survive Native AOT and
/// pull dependencies. The format needed here is small: method calls, method returns, errors, and
/// the portal's Response signal, all carrying only strings, unsigned ints, booleans and a{sv} dicts.
///
/// Alignment notes straight from the D-Bus specification: VARIANT aligns to 1 (it is just a
/// SIGNATURE followed by a value that aligns itself), STRING/OBJECT_PATH to 4, SIGNATURE to 1,
/// STRUCT/DICT_ENTRY to 8, and an ARRAY's uint32 length counts only element bytes -- the padding
/// that sits between the length and the first element is excluded.
/// </summary>
internal sealed partial class LinuxDbus : IDisposable
{
    private const int AfUnix = 1;
    private const int SockStream = 1;
    private const int ErrnoAgain = 11;
    private const int ErrnoIntr = 4;

    private const byte MessageTypeMethodCall = 1;
    private const byte MessageTypeMethodReturn = 2;
    private const byte MessageTypeError = 3;
    private const byte MessageTypeSignal = 4;

    private const byte FieldPath = 1;
    private const byte FieldInterface = 2;
    private const byte FieldMember = 3;
    private const byte FieldErrorName = 4;
    private const byte FieldReplySerial = 5;
    private const byte FieldDestination = 6;
    private const byte FieldSender = 7;
    private const byte FieldSignature = 8;

    private readonly int fd;
    private uint serial;
    private readonly byte[] readBuffer = new byte[128 * 1024];
    private int readLength;
    private int readPosition;
    private readonly List<int> pendingFds = [];

    private LinuxDbus(int fd) => this.fd = fd;

    /// <summary>
    /// Connects to the session bus: the address from DBUS_SESSION_BUS_ADDRESS, or the standard
    /// /run/user/&lt;uid&gt;/bus socket when the variable is not set (how a user service usually
    /// sees the world). Completes the EXTERNAL auth and the Unix-fd negotiation, which the portal's
    /// OpenPipeWireRemote reply is going to need.
    /// </summary>
    public static LinuxDbus Connect()
    {
        var path = ResolveBusPath();
        var fd = socket(AfUnix, SockStream, 0);
        if (fd < 0)
            throw new IOException($"D-Bus: socket() failed (errno {Marshal.GetLastWin32Error()}).");

        var address = new byte[110]; // sun_family (2) + sun_path (108), the sockaddr_un layout
        var pathBytes = Encoding.UTF8.GetBytes(path);
        if (pathBytes.Length > 107)
            throw new IOException($"D-Bus: socket path '{path}' is too long.");
        address[0] = AfUnix;
        pathBytes.CopyTo(address, 2);

        unsafe
        {
            fixed (byte* p = address)
            {
                if (connect(fd, (nint)p, (nuint)address.Length) != 0)
                {
                    var errno = Marshal.GetLastWin32Error();
                    close(fd);
                    throw new IOException($"D-Bus: could not connect to {path} (errno {errno}).");
                }
            }
        }

        // A short receive timeout, so a hung bus turns into a retryable deadline inside every
        // exact-size read below instead of a process that waits forever.
        SetReceiveTimeout(fd, 250);

        var bus = new LinuxDbus(fd);
        bus.Authenticate();
        return bus;
    }

    private static string ResolveBusPath()
    {
        var address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        if (!string.IsNullOrWhiteSpace(address))
        {
            foreach (var part in address.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                // "unix:path=/run/user/1000/bus" -- the only transport this program can ever see.
                if (part.StartsWith("unix:path=", StringComparison.Ordinal))
                    return part["unix:path=".Length..];
            }
        }

        return $"/run/user/{GetUid()}/bus";
    }

    /// <summary>The SASL handshake, one line at a time, then the byte-stream switch.</summary>
    private void Authenticate()
    {
        // The protocol opens with a NUL from the client, then EXTERNAL auth: the identity is the
        // uid as a decimal string, hex-encoded byte by byte, per the spec.
        SendAll("\0"u8.ToArray());
        var uid = GetUid().ToString(CultureInfo.InvariantCulture);
        var hex = new StringBuilder(uid.Length * 2);
        foreach (var c in uid)
            hex.Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
        SendAll(Encoding.ASCII.GetBytes($"AUTH EXTERNAL {hex}\r\n"));
        var line = ReadLine();
        if (!line.StartsWith("OK", StringComparison.Ordinal))
            throw new IOException($"D-Bus: auth refused ({line.Trim()}).");

        // fd passing is a capability, not a default: without this negotiation the fd in the
        // OpenPipeWireRemote reply would be dropped on the floor.
        SendAll(Encoding.ASCII.GetBytes("NEGOTIATE_UNIX_FD\r\n"));
        line = ReadLine();
        if (!line.StartsWith("AGREE_UNIX_FD", StringComparison.Ordinal))
            throw new IOException($"D-Bus: unix-fd passing refused ({line.Trim()}).");

        SendAll(Encoding.ASCII.GetBytes("BEGIN\r\n"));
    }

    /// <summary>Hello is mandatory: until it is answered the bus routes nothing to us.</summary>
    public string Hello() =>
        Call("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "Hello", null, null, 10_000)
            .Apply(body => new DbusReader(body).ReadString());

    /// <summary>AddMatch, so the daemon forwards the signals this client waits on.</summary>
    public void AddMatch(string rule) =>
        Call("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "AddMatch", "s",
            w => w.WriteString(rule), 10_000);

    /// <summary>
    /// A method call to <paramref name="destination"/>, waiting for the return or the error.
    /// Anything that is not the reply to this serial (stray signals) is skipped.
    /// </summary>
    public DbusReply Call(string destination, string path, string interfaceName, string member,
        string? signature, Action<DbusWriter>? writeBody, int timeoutMs)
    {
        var call = WriteMessage(MessageTypeMethodCall, flags: 0, path, interfaceName, member,
            destination, signature, writeBody);
        SendAll(call.Bytes);

        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var message = ReceiveMessage(deadline);
            if (message.ReplySerial != call.Serial)
            {
                // A signal that arrived while waiting for the reply must not be dropped: the
                // portal's Response can beat the reply itself out of the daemon.
                if (message.Type == MessageTypeSignal)
                    inbox.Add(message);
                continue;
            }

            if (message.Type == MessageTypeMethodReturn)
                return new DbusReply(message.Body, message.Fds);
            if (message.Type == MessageTypeError)
                throw new IOException(
                    $"D-Bus: {member} failed: {message.ErrorName} ({ReadErrorText(message.Body)}).");
        }
    }

    /// <summary>
    /// Blocks until a signal on <paramref name="interfaceName"/> whose path equals
    /// <paramref name="signalPath"/> arrives -- the portal's Response for one request handle.
    /// </summary>
    private readonly List<ReceivedMessage> inbox = [];

    public (uint Response, byte[] Body) WaitForSignal(string interfaceName, string signalPath, int timeoutMs)
    {
        // Signals read while some Call was waiting for its reply are already in the inbox.
        for (var i = 0; i < inbox.Count; i++)
        {
            var m = inbox[i];
            if (m.Interface == interfaceName && m.Path == signalPath)
            {
                inbox.RemoveAt(i);
                return (m.ResponseCode, m.Body);
            }
        }

        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var message = ReceiveMessage(deadline);
            if (message.Type == MessageTypeSignal && message.Interface == interfaceName && message.Path == signalPath)
                return (message.ResponseCode, message.Body);
            if (message.Type == MessageTypeSignal)
                inbox.Add(message);
        }
    }

    private (uint Serial, byte[] Bytes) WriteMessage(byte type, uint flags, string? path, string? interfaceName,
        string? member, string? destination, string? signature, Action<DbusWriter>? writeBody, uint replySerial = 0)
    {
        var body = new DbusWriter();
        writeBody?.Invoke(body);
        var bodyBytes = body.ToBytes();

        var header = new DbusWriter();
        // Each header field is a STRUCT (byte code, variant) and structs align to 8; the fields
        // array starts at message offset 16, itself 8-aligned, so buffer-relative alignment here is
        // message-relative alignment. Misaligned fields make the daemon drop the connection.
        if (path is not null)
        {
            header.Align(8);
            header.Byte(FieldPath);
            header.Variant("o", w => w.ObjectPath(path));
        }
        if (interfaceName is not null)
        {
            header.Align(8);
            header.Byte(FieldInterface);
            header.Variant("s", w => w.WriteString(interfaceName));
        }
        if (member is not null)
        {
            header.Align(8);
            header.Byte(FieldMember);
            header.Variant("s", w => w.WriteString(member));
        }
        if (destination is not null)
        {
            header.Align(8);
            header.Byte(FieldDestination);
            header.Variant("s", w => w.WriteString(destination));
        }
        if (signature is not null)
        {
            header.Align(8);
            header.Byte(FieldSignature);
            header.Variant("g", w => w.Signature(signature));
        }
        if (replySerial != 0)
        {
            header.Align(8);
            header.Byte(FieldReplySerial);
            header.Variant("u", w => w.UInt32(replySerial));
        }
        var headerFields = header.ToBytes();

        var serial = ++this.serial;
        var message = new DbusWriter();
        message.Byte(0x6c); // 'l': little-endian, announced in the fixed header's first byte
        message.Byte(type);
        message.Byte((byte)flags);
        message.Byte(1); // protocol version
        message.UInt32((uint)bodyBytes.Length);
        message.UInt32(serial);
        message.UInt32((uint)headerFields.Length);
        message.Raw(headerFields);
        message.Align(8);
        message.Raw(bodyBytes);
        return (serial, message.ToBytes());
    }

    private struct ReceivedMessage
    {
        public byte Type;
        public uint Serial;
        public uint ReplySerial;
        public uint ResponseCode;
        public string Path;
        public string Interface;
        public string Member;
        public string Sender;
        public string ErrorName;
        public byte[] Body;
        public int[] Fds;
    }

    /// <summary>
    /// Reads one complete message. Receives always go through recvmsg: it is the only way to pick
    /// up the SCM_RIGHTS descriptors that ride along a reply like OpenPipeWireRemote's.
    /// </summary>
    private readonly object receiveGate = new();

    /// <summary>
    /// Reads one complete message. Receives always go through recvmsg: it is the only way to pick
    /// up the SCM_RIGHTS descriptors that ride along a reply like OpenPipeWireRemote's. The gate
    /// keeps a synchronous caller and the dispatch thread from interleaving reads of one socket;
    /// callers that mix both must still order their registration before StartDispatch, as the
    /// tray does.
    /// </summary>
    private ReceivedMessage ReceiveMessage(long deadline)
    {
        lock (receiveGate)
        {
            return ReceiveMessageLocked(deadline);
        }
    }

    private ReceivedMessage ReceiveMessageLocked(long deadline)
    {
        // Fixed header: endianness, type, flags, version, body length, serial, fields length.
        ReadExact(16, deadline);
        var type = readBuffer[readPosition + 1];
        var bodyLength = BitConverter.ToUInt32(readBuffer, readPosition + 4);
        var serial = BitConverter.ToUInt32(readBuffer, readPosition + 8);
        var fieldsLength = BitConverter.ToUInt32(readBuffer, readPosition + 12);
        readPosition += 16;

        ReadExact((int)(fieldsLength + DbusReader.PadTo((int)fieldsLength, 8)), deadline);
        var fields = new byte[fieldsLength];
        Buffer.BlockCopy(readBuffer, readPosition, fields, 0, (int)fieldsLength);
        readPosition += (int)(fieldsLength + DbusReader.PadTo((int)fieldsLength, 8));

        ReadExact((int)bodyLength, deadline);
        var body = new byte[bodyLength];
        Buffer.BlockCopy(readBuffer, readPosition, body, 0, (int)bodyLength);
        readPosition += (int)bodyLength;

        var message = new ReceivedMessage { Type = type, Serial = serial, Body = body, Fds = TakePendingFds() };
        using var fieldsReader = new DbusReader(fields);
        foreach (var (code, value) in fieldsReader.ReadHeaderFields())
        {
            switch (code)
            {
                case FieldPath: message.Path = value.String; break;
                case FieldInterface: message.Interface = value.String; break;
                case FieldMember: message.Member = value.String; break;
                case FieldSender: message.Sender = value.String; break;
                case FieldErrorName: message.ErrorName = value.String; break;
                case FieldReplySerial: message.ReplySerial = value.UInt32; break;
            }
        }

        // The portal's Response signal body starts with the uint response code; read it eagerly so
        // WaitForSignal can hand it straight over.
        if (type == MessageTypeSignal && body.Length >= 4)
        {
            var bodyReader = new DbusReader(body);
            message.ResponseCode = bodyReader.ReadUInt32();
        }

        return message;
    }

    private static string ReadErrorText(byte[] body)
    {
        try
        {
            return new DbusReader(body).ReadString();
        }
        catch (Exception)
        {
            return "<no message>";
        }
    }

    /// <summary>
    /// Reads until <paramref name="count"/> bytes are buffered. A receive timeout inside this loop
    /// only ends it when the caller's deadline has actually passed, because the partial state (the
    /// bytes already in the buffer) must survive across retries.
    /// </summary>
    private void ReadExact(int count, long deadline)
    {
        while (readLength - readPosition < count)
        {
            // Compact what is already buffered to the front, so one buffer serves header and body.
            if (readPosition > 0)
            {
                Buffer.BlockCopy(readBuffer, readPosition, readBuffer, 0, readLength - readPosition);
                readLength -= readPosition;
                readPosition = 0;
            }

            if (readLength >= readBuffer.Length)
                throw new IOException("D-Bus: read buffer exhausted.");

            var chunk = RecvWithFds(readBuffer.AsSpan(readLength, readBuffer.Length - readLength));
            // Descriptors must be kept on EVERY receive that carries them: they ride along the
            // bytes of the message they belong to, and bytes > 0 is the normal case, not the
            // exception. (An if/else here silently dropped the fd whenever it arrived together
            // with the data -- which is how the portal's PipeWire descriptor got lost.)
            if (chunk.Fds.Count > 0)
                pendingFds.AddRange(chunk.Fds);
            if (chunk.Bytes > 0)
            {
                readLength += chunk.Bytes;
            }
            else if (chunk.Fds.Count == 0 && Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("D-Bus: timed out waiting for a message.");
            }
        }
    }

    private int[] TakePendingFds()
    {
        if (pendingFds.Count == 0)
            return [];
        var fds = pendingFds.ToArray();
        pendingFds.Clear();
        return fds;
    }

    private string ReadLine()
    {
        // Pre-auth framing is line-based and tiny; a 1-byte receive per character is fine there.
        Span<byte> one = stackalloc byte[1];
        var bytes = new List<byte>(64);
        while (true)
        {
            var chunk = RecvWithFds(one);
            if (chunk.Bytes <= 0)
                throw new IOException("D-Bus: the connection closed during the handshake.");
            if (one[0] == (byte)'\n')
                return Encoding.ASCII.GetString(bytes.ToArray());
            if (one[0] != (byte)'\r')
                bytes.Add(one[0]);
        }
    }

    private void SendAll(byte[] data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            unsafe
            {
                fixed (byte* p = data.AsSpan(offset))
                {
                    var sent = send(fd, (nint)p, (nuint)(data.Length - offset), 0);
                    if (sent < 0 && Marshal.GetLastWin32Error() == ErrnoIntr)
                        continue; // interrupted by a signal; the retry loop resends from offset
                    if (sent <= 0)
                        throw new IOException($"D-Bus: send failed (errno {Marshal.GetLastWin32Error()}).");
                    offset += (int)sent;
                }
            }
        }
    }

    private (int Bytes, List<int> Fds) RecvWithFds(Span<byte> target)
    {
        unsafe
        {
            fixed (byte* targetPtr = target)
            {
                var iov = new IoVec { iov_base = (nint)targetPtr, iov_len = (nuint)target.Length };
                // Room for a handful of descriptors: cmsghdr is 16 bytes and one fd is 4.
                var control = new byte[256];
                fixed (byte* controlPtr = control)
                {
                    var msg = new MsgHdr
                    {
                        msg_iov = (nint)(&iov),
                        msg_iovlen = 1,
                        msg_control = (nint)controlPtr,
                        msg_controllen = (nuint)control.Length,
                    };

                    var received = (int)recvmsg(fd, ref msg, 0);
                    if (received < 0)
                    {
                        var errno = Marshal.GetLastWin32Error();
                        // EAGAIN: the receive timeout. EINTR: a signal (a spawned pactl's SIGCHLD,
                        // for one) interrupted the call -- retryable, and MANDATORY to retry here:
                        // treating it as fatal silently killed the tray the first time the menu
                        // listed sinks.
                        if (errno == ErrnoAgain || errno == ErrnoIntr)
                            return (0, []);
                        throw new IOException($"D-Bus: recvmsg failed (errno {errno}).");
                    }
                    if (received == 0)
                        throw new IOException("D-Bus: the bus closed the connection.");

                    return (received, ParseControlMessages(control, msg.msg_controllen));
                }
            }
        }
    }

    private static List<int> ParseControlMessages(byte[] control, nuint length)
    {
        var fds = new List<int>();
        unsafe
        {
            fixed (byte* controlPtr = control)
            {
                var cursor = (nint)controlPtr;
                var end = cursor + (nint)length;
                while (cursor + 16 <= end)
                {
                    // struct cmsghdr on linux-x64: size_t cmsg_len (8), int cmsg_level (4),
                    // int cmsg_type (4), then the payload. The level/type offsets follow the 8-byte
                    // length, which is exactly where a 4-byte read of them goes wrong.
                    var totalLength = *(nint*)cursor;
                    var level = *(int*)(cursor + 8);
                    var type = *(int*)(cursor + 12);
                    if (level == 1 /* SOL_SOCKET */ && type == 1 /* SCM_RIGHTS */)
                    {
                        var dataStart = cursor + 16;
                        var dataEnd = cursor + totalLength;
                        for (var p = dataStart; p + 4 <= dataEnd; p += 4)
                            fds.Add(*(int*)p);
                    }
                    cursor += (nint)((totalLength + 3) & ~3); // cmsg_len is padded to 4
                }
            }
        }
        return fds;
    }

    private static void SetReceiveTimeout(int fd, int milliseconds)
    {
        // struct timeval { tv_sec; tv_usec } -- two 8-byte fields on linux-x64.
        Span<byte> tv = stackalloc byte[16];
        BitConverter.GetBytes((long)(milliseconds / 1000)).CopyTo(tv);
        BitConverter.GetBytes((long)((milliseconds % 1000) * 1000)).CopyTo(tv[8..]);
        unsafe
        {
            fixed (byte* p = tv)
            {
                setsockopt(fd, 1 /* SOL_SOCKET */, 20 /* SO_RCVTIMEO */, (nint)p, 16);
            }
        }
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int socket(int domain, int type, int protocol);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int connect(int fd, nint address, nuint length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint send(int fd, nint buffer, nuint length, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint recvmsg(int fd, ref MsgHdr message, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setsockopt(int fd, int level, int option, nint value, nuint length);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static partial uint getuid();

    private static uint GetUid() => getuid();

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVec
    {
        public nint iov_base;
        public nuint iov_len;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsgHdr
    {
        public nint msg_name;
        public uint msg_namelen;
        public nint msg_iov;
        public nuint msg_iovlen;
        public nint msg_control;
        public nuint msg_controllen;
        public int msg_flags;
    }

    // --- Server side: serving method calls on object paths this process owns (the tray). ---

    private readonly Dictionary<(string Path, string Interface, string Member), (Func<DbusReader, byte[]> Handler, string? Signature)> handlers = [];
    private Thread? dispatchThread;
    private volatile bool dispatching;

    /// <summary>
    /// Registers a method handler. The handler reads the call body and returns the reply body;
    /// <paramref name="replySignature"/> goes into the reply's SIGNATURE header field -- without
    /// it clients treat the reply as having no body and hang waiting for values.
    /// </summary>
    public void AddMethodHandler(string path, string interfaceName, string member,
        Func<DbusReader, byte[]> handler, string? replySignature = null) =>
        handlers[(path, interfaceName, member)] = (handler, replySignature);

    /// <summary>
    /// Serves incoming method calls until Dispose. Runs on its own thread: a tray handler that
    /// takes seconds (an export) must not stall anything else, and nothing else shares this
    /// connection -- the portal talks over its own.
    /// </summary>
    public void StartDispatch()
    {
        dispatching = true;
        dispatchThread = new Thread(() =>
        {
            while (dispatching)
            {
                ReceivedMessage message;
                try
                {
                    message = ReceiveMessage(Environment.TickCount64 + 1000);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    // EBADF (9) is the NORMAL teardown path: Dispose closed the fd while this
                    // thread sat in recvmsg. Only a bus failure while still serving is news.
                    Console.WriteLine(dispatching
                        ? $"Tray: stopped serving ({ex.GetType().Name}: {ex.Message})."
                        : "Tray: shut down cleanly.");
                    return; // the session bus is gone; the tray has nowhere to live
                }

                if (message.Type != MessageTypeMethodCall)
                    continue;


                var key = (message.Path, message.Interface, message.Member);
                if (!handlers.TryGetValue(key, out var entry) &&
                    !handlers.TryGetValue((message.Path, message.Interface, "*"), out entry))
                {
                    SendError(message.Serial, message.Sender, "org.freedesktop.DBus.Error.UnknownMethod",
                        $"{message.Interface}.{message.Member} is not served here.");
                    continue;
                }

                try
                {
                    var replyBody = entry.Handler(new DbusReader(message.Body));
                    if (string.IsNullOrEmpty(message.Sender))
                    {
                        // The broker always attaches SENDER to a routed call; a call without one
                        // cannot be answered (the reply needs a destination), so it is refused.
                        SendError(message.Serial, "", "org.freedesktop.DBus.Error.Failed",
                            $"{message.Member}: the call carried no sender.");
                        continue;
                    }
                    SendReply(message.Serial, message.Sender, entry.Signature, replyBody);
                }
                catch (Exception ex)
                {
                    // Teardown often races the dispatch: plasma keeps polling while Dispose
                    // closes the fd, so the reply (and even the error about the reply) can hit a
                    // closed socket. Unhandled, that exception aborts the whole process.
                    if (!dispatching)
                        return;
                    try
                    {
                        SendError(message.Serial, message.Sender, "org.freedesktop.DBus.Error.Failed", ex.Message);
                    }
                    catch (Exception sendEx)
                    {
                        Console.WriteLine($"Tray: reply to {message.Member} failed twice ({sendEx.GetType().Name}).");
                    }
                }
            }
        })
        { IsBackground = true, Name = "clippy-dbus-dispatch" };
        dispatchThread.Start();
    }

    /// <summary>
    /// Replies carry the caller's name in the DESTINATION field: dbus-broker does not route a
    /// method_return that lacks it (every reference server -- libdbus, GDBus -- sets it), and a
    /// dropped reply is invisible: the wire looks fine, the client just hangs.
    /// </summary>
    private void SendReply(uint replySerial, string destination, string? signature, byte[] body)
    {
        // Header field codes MUST be in ASCENDING order (5, 6, 8): a descending pair makes the
        // broker reject the message as having an invalid header -- and for replies that rejection
        // is SILENT: the wire looks fine, no error reaches anyone, the client just hangs. This
        // ordering cost a whole debugging session to find.
        var header = new DbusWriter();
        header.Align(8);
        header.Byte(FieldReplySerial);
        header.Variant("u", w => w.UInt32(replySerial));
        header.Align(8);
        header.Byte(FieldDestination);
        header.Variant("s", w => w.WriteString(destination));
        // A reply without a body (Event!) has no SIGNATURE field at all; writing Signature(null)
        // threw and the dispatch thread died on the first such reply.
        if (signature is not null)
        {
            header.Align(8);
            header.Byte(FieldSignature);
            header.Variant("g", w => w.Signature(signature));
        }
        // extraHeaderFields ONLY: the destination parameter of AssembleMessage must stay null, or
        // the field is written twice and the broker rejects the whole header (this double write
        // silently killed every reply this server sent).
        SendAll(AssembleMessage(MessageTypeMethodReturn, 0, null, null, null, null, null, body, 0, header.ToBytes()));
    }

    private void SendError(uint replySerial, string destination, string errorName, string message)
    {
        // Same two rules as the reply: SIGNATURE must name the body, DESTINATION must name the
        // caller -- without either the broker silently drops the error.
        var header = new DbusWriter();
        header.Align(8);
        header.Byte(4); // ERROR_NAME
        header.Variant("s", w => w.WriteString(errorName));
        header.Align(8);
        header.Byte(FieldReplySerial);
        header.Variant("u", w => w.UInt32(replySerial));
        header.Align(8);
        header.Byte(FieldDestination);
        header.Variant("s", w => w.WriteString(destination));
        header.Align(8);
        header.Byte(FieldSignature);
        header.Variant("g", w => w.Signature("s"));
        var body = new DbusWriter();
        body.String(message);
        // Codes must stay unique and ascending: 4, 5, 6, 8. The destination parameter of
        // AssembleMessage stays null, or the field is written twice and out of order.
        SendAll(AssembleMessage(MessageTypeError, 0, null, null, null, null, null, body.ToBytes(), 0, header.ToBytes()));
    }

    /// <summary>Emits a signal from an object path this process serves.</summary>
    public void SendSignal(string path, string interfaceName, string member, string? signature = null, Action<DbusWriter>? writeBody = null)
    {
        var body = new DbusWriter();
        writeBody?.Invoke(body);
        SendAll(AssembleMessage(MessageTypeSignal, 1, path, interfaceName, member, null, signature, body.ToBytes(), 0));
    }

    private byte[] AssembleMessage(byte type, uint flags, string? path, string? interfaceName, string? member,
        string? destination, string? signature, byte[] bodyBytes, uint replySerial, byte[]? extraHeaderFields = null)
    {
        var header = new DbusWriter();
        if (path is not null)
        {
            header.Align(8);
            header.Byte(FieldPath);
            header.Variant("o", w => w.ObjectPath(path));
        }
        if (interfaceName is not null)
        {
            header.Align(8);
            header.Byte(FieldInterface);
            header.Variant("s", w => w.WriteString(interfaceName));
        }
        if (member is not null)
        {
            header.Align(8);
            header.Byte(FieldMember);
            header.Variant("s", w => w.WriteString(member));
        }
        if (destination is not null)
        {
            header.Align(8);
            header.Byte(FieldDestination);
            header.Variant("s", w => w.WriteString(destination));
        }
        if (signature is not null)
        {
            header.Align(8);
            header.Byte(FieldSignature);
            header.Variant("g", w => w.Signature(signature));
        }
        if (replySerial != 0)
        {
            header.Align(8);
            header.Byte(FieldReplySerial);
            header.Variant("u", w => w.UInt32(replySerial));
        }
        if (extraHeaderFields is not null)
            header.Raw(extraHeaderFields);
        var headerFields = header.ToBytes();

        var next = ++serial;
        var message = new DbusWriter();
        message.Byte(0x6c);
        message.Byte(type);
        message.Byte((byte)flags);
        message.Byte(1);
        message.UInt32((uint)bodyBytes.Length);
        message.UInt32(next);
        message.UInt32((uint)headerFields.Length);
        message.Raw(headerFields);
        message.Align(8);
        message.Raw(bodyBytes);
        return message.ToBytes();
    }

    public void Dispose()
    {
        // The flag first: the blocked recvmsg wakes with EBADF the moment the fd closes, and the
        // dispatch loop must see "shutting down" rather than a bus failure.
        dispatching = false;
        if (fd >= 0)
            close(fd);
        foreach (var fd in pendingFds)
            close(fd);
        pendingFds.Clear();
    }
}

/// <summary>A method reply: the body plus any file descriptors received with it.</summary>
internal sealed class DbusReply : IDisposable
{
    public byte[] Body { get; }
    public int[] Fds { get; }

    public DbusReply(byte[] body, int[] fds)
    {
        Body = body;
        Fds = fds;
    }

    public void Dispose()
    {
        foreach (var fd in Fds)
            LinuxDbus.close(fd);
    }
}

internal static class DbusReplyExtensions
{
    /// <summary>Reads a single 's' reply (Hello's unique name) and releases the message.</summary>
    public static T Apply<T>(this DbusReply reply, Func<byte[], T> read)
    {
        using (reply)
            return read(reply.Body);
    }
}

/// <summary>Builds one D-Bus message body (or header-fields array) in wire order.</summary>
internal sealed class DbusWriter
{
    private readonly List<byte> buffer = [];

    public void Byte(byte value) => buffer.Add(value);

    public void UInt32(uint value)
    {
        // A UINT32 must sit on a 4-byte boundary of its own: the caller's padding is not enough,
        // because a variant value lands right after the 3-byte SIGNATURE ('g'), which is not
        // 4-aligned. The reader aligns on the way out; without this, writer and reader disagree.
        Align(4);
        Append(BitConverter.GetBytes(value));
    }

    public void String(string value) => WriteStringLike(value);

    /// <summary>Alias used by the portal flow, which reads better than a bare type name.</summary>
    public void WriteString(string value) => WriteStringLike(value);

    public void Bool(bool value) => UInt32(value ? 1u : 0u);

    public void ObjectPath(string value) => WriteStringLike(value);

    public void Signature(string value)
    {
        // A SIGNATURE is a single length byte (excluding the NUL), the characters, then the NUL.
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > 255)
            throw new ArgumentException("D-Bus signature longer than 255 bytes.");
        Byte((byte)bytes.Length);
        Append(bytes);
        Byte(0);
    }

    /// <summary>A VARIANT: signature first, then the value, which brings its own alignment.</summary>
    public void Variant(string signature, Action<DbusWriter> writeValue)
    {
        Signature(signature);
        writeValue(this);
    }

    public void Raw(byte[] bytes) => Append(bytes);

    /// <summary>Pads to the alignment boundary with zeros (the wire format's only padding).</summary>
    public void Align(int alignment)
    {
        while (buffer.Count % alignment != 0)
            buffer.Add(0);
    }

    /// <summary>
    /// An a{sv} dict of portal options: string keys, variants of the given signatures. The byte
    /// count patched into the length excludes the padding between it and the first entry.
    /// </summary>
    public void WriteOptionsDict(IReadOnlyDictionary<string, (string Signature, Action<DbusWriter> Write)> options)
    {
        Align(8);
        var lengthPosition = ReserveLength();
        Align(8); // dict entries are structs; this padding sits outside the counted length
        var contentStart = buffer.Count;
        foreach (var (key, (signature, write)) in options)
        {
            Align(8);
            String(key);
            Variant(signature, write);
        }
        PatchLength(lengthPosition, (uint)(buffer.Count - contentStart));
    }

    /// <summary>An array of strings ('as').</summary>
    public void WriteStringArray(IEnumerable<string> values)
    {
        Align(4);
        var lengthPosition = ReserveLength();
        Align(4); // element alignment; this padding sits outside the counted length
        var contentStart = buffer.Count;
        foreach (var value in values)
            String(value);
        PatchLength(lengthPosition, (uint)(buffer.Count - contentStart));
    }

    public void Int32(int value) => UInt32((uint)value);

    /// <summary>An array of bytes ('ay'), alignment 1 -- how the SNI icon pixmap travels.</summary>
    public void WriteByteArray(ReadOnlySpan<byte> bytes)
    {
        UInt32((uint)bytes.Length);
        foreach (var b in bytes)
            buffer.Add(b);
    }

    private void WriteStringLike(string value)
    {
        Align(4);
        var bytes = Encoding.UTF8.GetBytes(value);
        // The u32 length counts the string bytes WITHOUT the terminating NUL (verified against
        // dbus-send on the wire: "/org/freedesktop/DBus" travels as 0x15 = 21).
        UInt32((uint)bytes.Length);
        Append(bytes);
        Byte(0);
    }

    private int ReserveLength()
    {
        var position = buffer.Count;
        Append([0, 0, 0, 0]);
        return position;
    }

    private void PatchLength(int position, uint length)
    {
        var bytes = BitConverter.GetBytes(length);
        for (var i = 0; i < 4; i++)
            buffer[position + i] = bytes[i];
    }

    /// <summary>
    /// Starts a general-purpose array: reserves the length field and applies the element padding.
    /// The returned content start is what <see cref="EndArray"/> needs; the pair lets any element
    /// type be written, not just the ones with dedicated helpers.
    /// </summary>
    public (int LengthPosition, int ContentStart) BeginArray(int elementAlignment)
    {
        Align(4);
        var lengthPosition = ReserveLength();
        if (elementAlignment > 1)
            Align(elementAlignment);
        return (lengthPosition, buffer.Count);
    }

    /// <summary>Patches the array's byte length. Call once after the last element.</summary>
    public void EndArray(int lengthPosition, int contentStart) =>
        PatchLength(lengthPosition, (uint)(buffer.Count - contentStart));

    public byte[] ToBytes() => buffer.ToArray();

    private void Append(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            buffer.Add(b);
    }
}

/// <summary>Reads a D-Bus body or header-fields array back in wire order.</summary>
internal sealed class DbusReader : IDisposable
{
    private readonly byte[] data;
    private int position;

    public DbusReader(byte[] data) => this.data = data;

    public void Dispose()
    {
    }

    public uint ReadUInt32()
    {
        Align(4);
        var value = BitConverter.ToUInt32(data, position);
        position += 4;
        return value;
    }

    public bool ReadBool() => ReadUInt32() != 0;

    public int[] ReadInt32Array()
    {
        Align(4);
        var length = (int)ReadUInt32();
        Align(4);
        var end = position + length;
        var values = new List<int>();
        while (position < end)
        {
            Align(4);
            values.Add((int)ReadUInt32());
        }
        return values.ToArray();
    }

    public string[] ReadStringArray()
    {
        Align(4);
        var length = (int)ReadUInt32();
        Align(4);
        var end = position + length;
        var values = new List<string>();
        while (position < end)
        {
            Align(4);
            values.Add(ReadString());
        }
        return values.ToArray();
    }

    public int ReadInt32()
    {
        Align(4);
        var value = BitConverter.ToInt32(data, position);
        position += 4;
        return value;
    }

    public ulong ReadUInt64()
    {
        Align(8);
        var value = BitConverter.ToUInt64(data, position);
        position += 8;
        return value;
    }

    public string ReadString() => ReadStringLike();

    public string ReadObjectPath() => ReadStringLike();

    public string ReadSignature()
    {
        var length = data[position++];
        var value = Encoding.UTF8.GetString(data, position, length);
        position += length + 1; // the trailing NUL
        return value;
    }

    public IEnumerable<(uint Code, DbusValue Value)> ReadHeaderFields()
    {
        // The caller hands in ONLY the array data: its uint32 length lives in the fixed header and
        // was already used to slice this buffer. The array data starts at message offset 16, itself
        // 8-aligned, so buffer-relative struct alignment is message-relative alignment.
        var fields = new List<(uint, DbusValue)>();
        while (position < data.Length)
        {
            Align(8);
            if (position >= data.Length)
                break;
            var code = (uint)data[position++];
            fields.Add((code, ReadVariant()));
        }
        return fields;
    }

    /// <summary>
    /// The variants this client can meet: the header fields' small types, plus the portal's
    /// a(ua{sv}) "streams" value. Anything else fails loudly rather than mis-parses.
    /// </summary>
    public DbusValue ReadVariant()
    {
        var signature = ReadSignature();
        return signature switch
        {
            "s" => new DbusValue { String = ReadString() },
            "o" => new DbusValue { String = ReadObjectPath() },
            "u" => new DbusValue { UInt32 = ReadUInt32() },
            "b" => new DbusValue { Bool = ReadBool() },
            "t" or "x" => new DbusValue { UInt64 = ReadUInt64() },
            "g" => new DbusValue { String = ReadSignature() },
            // The portal's stream properties carry sizes as (ii) pairs, e.g. "size".
            "(ii)" => new DbusValue { String = $"{ReadInt32()}x{ReadInt32()}" },
            "a(ua{sv})" => new DbusValue { Streams = ReadStreams() },
            _ => throw new IOException($"D-Bus: unsupported variant signature '{signature}'."),
        };
    }

    private List<(uint Node, Dictionary<string, DbusValue> Props)> ReadStreams()
    {
        Align(4);
        var length = (int)ReadUInt32();
        Align(8);
        var end = position + length;
        var streams = new List<(uint, Dictionary<string, DbusValue>)>();
        while (position < end)
        {
            Align(8);
            var node = ReadUInt32();
            var props = ReadStringVariantDict();
            streams.Add((node, props));
        }
        return streams;
    }

    /// <summary>
    /// Reads the portal's standard Response body: (uint code, a{sv} results). The response code is
    /// also stored under "__response", so the caller needs only the dictionary.
    /// </summary>
    public Dictionary<string, DbusValue> ReadResponseResults()
    {
        var code = ReadUInt32();
        var results = ReadStringVariantDict();
        results["__response"] = new DbusValue { UInt32 = code };
        return results;
    }

    public Dictionary<string, DbusValue> ReadStringVariantDict()
    {
        Align(4);
        var length = (int)ReadUInt32();
        Align(8);
        var end = position + length;
        var results = new Dictionary<string, DbusValue>();
        while (position < end)
        {
            Align(8);
            var key = ReadString();
            results[key] = ReadVariant();
        }
        return results;
    }

    private string ReadStringLike()
    {
        Align(4);
        var length = (int)ReadUInt32();
        var value = Encoding.UTF8.GetString(data, position, length);
        position += length + 1; // the NUL that follows the counted bytes
        return value;
    }

    private void Align(int alignment) => position += PadTo(position, alignment);

    internal static int PadTo(int position, int alignment) => (alignment - position % alignment) % alignment;
}

/// <summary>A parsed value, keeping the small set of types this client can encounter.</summary>
internal sealed class DbusValue
{
    public string String { get; init; } = "";
    public uint UInt32 { get; init; }
    public ulong UInt64 { get; init; }
    public bool Bool { get; init; }
    public List<(uint Node, Dictionary<string, DbusValue> Props)> Streams { get; init; } = [];
}
#endif

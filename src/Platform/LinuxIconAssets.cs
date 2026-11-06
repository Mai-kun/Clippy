#if LINUX
using System.IO.Compression;

namespace Clippy;

/// <summary>
/// The real Clippy icon, taken from the embedded app.ico (the same file the Windows build uses
/// for its executable): ICO directory parsing, frame extraction, PNG decoding for the SNI
/// pixmap, and the hicolor-theme PNG for the desktop entry.
///
/// Every frame in our app.ico is a PNG-compressed 32-bit image (16/24/32/48/64/128), so the
/// decoder only has to speak RGBA8, non-interlaced; a BGRA DIB decoder is here as well for
/// robustness in case the icon ever ships an old-style frame.
/// </summary>
internal static class LinuxIconAssets
{
    private static byte[]? icoBytes;
    private static List<IcoFrame>? frames;
    private static readonly object gate = new();

    private sealed class IcoFrame
    {
        public int Width;
        public int Height;
        public bool IsPng;
        public byte[] Bytes = []; // the complete PNG, or the raw DIB (starting at BITMAPINFOHEADER)
    }

    private static List<IcoFrame> Frames()
    {
        lock (gate)
        {
            if (frames is not null)
                return frames;

            frames = [];
            icoBytes ??= ReadEmbeddedIco();
            if (icoBytes is null || icoBytes.Length < 6)
                return frames;

            var count = BitConverter.ToUInt16(icoBytes, 4);
            for (var i = 0; i < count; i++)
            {
                var off = 6 + i * 16;
                var width = icoBytes[off] == 0 ? 256 : icoBytes[off];
                var height = icoBytes[off + 1] == 0 ? 256 : icoBytes[off + 1];
                var size = (int)BitConverter.ToUInt32(icoBytes, off + 8);
                var offset = (int)BitConverter.ToUInt32(icoBytes, off + 12);
                if (offset + size > icoBytes.Length || size < 4)
                    continue;

                var payload = icoBytes[offset..(offset + size)];
                frames.Add(new IcoFrame
                {
                    Width = width,
                    Height = height,
                    IsPng = payload[0] == 0x89 && payload[1] == (byte)'P',
                    Bytes = payload,
                });
            }
            return frames;
        }
    }

    /// <summary>
    /// One line per embedded frame -- pixel size, compression (PNG or BMP/DIB) and the average and
    /// maximum alpha of the decoded pixels. Logged once when the tray comes up: an icon that draws
    /// as an empty slot is invisible at the SNI level too (all-zero alpha bytes), and this line is
    /// what separates "the bytes are fine, the bar mishandled them" from "the decoder produced
    /// nothing".
    /// </summary>
    public static string FrameDiagnostics()
    {
        var all = Frames();
        if (all.Count == 0)
            return "Icon: no frames decoded from the embedded app.ico.";

        var lines = new List<string>(all.Count);
        foreach (var frame in all)
        {
            try
            {
                var (rgba, w, h) = frame.IsPng
                    ? DecodePng(frame.Bytes)
                    : DecodeDib(frame.Bytes, frame.Width, frame.Height);
                long sum = 0;
                var max = 0;
                for (var i = 3; i < rgba.Length; i += 4)
                {
                    var alpha = rgba[i];
                    sum += alpha;
                    if (alpha > max)
                        max = alpha;
                }
                lines.Add($"{w}x{h} {(frame.IsPng ? "PNG" : "BMP/DIB")}: alpha avg {sum / (w * h):F0}, max {max}");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                lines.Add($"{frame.Width}x{frame.Height} {(frame.IsPng ? "PNG" : "BMP/DIB")}: DECODE FAILED ({ex.Message})");
            }
        }
        return "Icon frames: " + string.Join("; ", lines);
    }

    private static byte[]? ReadEmbeddedIco()
    {
        try
        {
            using var stream = typeof(LinuxIconAssets).Assembly.GetManifestResourceStream("app.ico");
            if (stream is null)
            {
                Console.WriteLine("Icon: the embedded app.ico is missing from the build.");
                return null;
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Icon: could not read the embedded app.ico ({ex.Message}).");
            return null;
        }
    }

    /// <summary>
    /// The largest frame, ready for the hicolor theme: a PNG frame is written through as-is, a
    /// BGRA DIB frame is decoded and re-encoded.
    /// </summary>
    public static byte[]? LargestPngBytes()
    {
        var all = Frames();
        var best = all.OrderByDescending(f => f.Width * f.Height).FirstOrDefault();
        if (best is null)
            return null;

        if (best.IsPng)
            return best.Bytes;

        var (rgba, w, h) = DecodeDib(best.Bytes, best.Width, best.Height);
        return EncodePng(w, h, rgba);
    }

    /// <summary>
    /// The tray pixmap in SNI's IconPixmap payload: ARGB32, network byte order (A, R, G, B),
    /// premultiplied, <paramref name="size"/>x<paramref name="size"/>. The matching ICO frame is
    /// decoded; a missing exact size falls back to the largest frame scaled down.
    /// </summary>
    public static byte[] TrayPixmapArgb32(int size)
    {
        var all = Frames();
        var frame = all.FirstOrDefault(f => f.Width == size && f.Height == size)
                    ?? all.OrderByDescending(f => f.Width * f.Height).FirstOrDefault();
        if (frame is null)
            return new byte[size * size * 4];

        byte[] rgba;
        int w, h;
        if (frame.IsPng)
            (rgba, w, h) = DecodePng(frame.Bytes);
        else
            (rgba, w, h) = DecodeDib(frame.Bytes, frame.Width, frame.Height);

        if (w != size || h != size)
            rgba = ScaleNearest(rgba, w, h, size, size);

        // Straight RGBA -> premultiplied ARGB32 in network byte order, the layout SNI asks for.
        var pixmap = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            var a = rgba[i * 4 + 3];
            pixmap[i * 4] = a;
            pixmap[i * 4 + 1] = (byte)(rgba[i * 4] * a / 255);
            pixmap[i * 4 + 2] = (byte)(rgba[i * 4 + 1] * a / 255);
            pixmap[i * 4 + 3] = (byte)(rgba[i * 4 + 2] * a / 255);
        }
        return pixmap;
    }

    private static byte[] ScaleNearest(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        for (var y = 0; y < dh; y++)
        {
            var sy = y * sh / dh;
            for (var x = 0; x < dw; x++)
            {
                var sx = x * sw / dw;
                Array.Copy(src, (sy * sw + sx) * 4, dst, (y * dw + x) * 4, 4);
            }
        }
        return dst;
    }

    /// <summary>
    /// Decodes a BGRA DIB frame (BITMAPINFOHEADER + bottom-up XOR mask + AND mask) into straight
    /// RGBA, top-down. Legacy 32-bit DIBs often carry zero alpha throughout, in which case the
    /// frame is treated as opaque rather than invisible.
    /// </summary>
    private static (byte[] Rgba, int Width, int Height) DecodeDib(byte[] dib, int width, int height)
    {
        // BITMAPINFOHEADER: biSize, biWidth, biHeight (doubled in ICO), biPlanes, biBitCount, ...
        var headerSize = (int)BitConverter.ToUInt32(dib, 0);
        var bitCount = BitConverter.ToUInt16(dib, 14);
        var stride = (width * bitCount + 31) / 32 * 4;
        var rgba = new byte[width * height * 4];
        var anyAlpha = false;

        for (var y = 0; y < height; y++)
        {
            // The XOR mask is stored bottom-up.
            var srcRow = headerSize + (height - 1 - y) * stride;
            for (var x = 0; x < width; x++)
            {
                var s = srcRow + x * 4;
                var d = (y * width + x) * 4;
                if (s + 4 > dib.Length)
                    continue;
                rgba[d] = dib[s + 2];     // R
                rgba[d + 1] = dib[s + 1]; // G
                rgba[d + 2] = dib[s];     // B
                rgba[d + 3] = dib[s + 3]; // A
                anyAlpha |= dib[s + 3] != 0;
            }
        }

        if (!anyAlpha)
            for (var i = 0; i < height * width; i++)
                rgba[i * 4 + 3] = 255;

        return (rgba, width, height);
    }

    /// <summary>
    /// Decodes an RGBA8, non-interlaced PNG (the format every frame of our app.ico uses) into
    /// straight RGBA, top-down. All five scanline filters are implemented; anything else in the
    /// file (ancillary chunks, 16-bit, palette) is skipped or rejected, never guessed at.
    /// </summary>
    /// <summary>Reads a big-endian uint32 at <paramref name="offset"/>: every length in a PNG
    /// (and in IHDR) is stored big-endian, and BitConverter would hand back a byte-swapped
    /// monster -- the bug that made every tray pixmap decode throw.</summary>
    private static int ReadBigEndianInt(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    private static (byte[] Rgba, int Width, int Height) DecodePng(byte[] png)
    {
        if (png.Length < 57 || png[0] != 0x89 || png[1] != (byte)'P')
            throw new InvalidDataException("Icon: the frame is not a PNG.");

        var pos = 8;
        var width = 0;
        var height = 0;
        var idat = new List<byte[]>();
        while (pos + 8 <= png.Length)
        {
            var length = ReadBigEndianInt(png, pos);
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            if (pos + 8 + length > png.Length)
                throw new InvalidDataException("Icon: the PNG frame has a truncated chunk.");
            var data = png[(pos + 8)..(pos + 8 + length)];
            pos += 12 + length; // length + type + data + CRC

            if (type == "IHDR")
            {
                width = ReadBigEndianInt(data, 0);
                height = ReadBigEndianInt(data, 4);
                if (data[8] != 8 || data[9] != 6 || data[12] != 0)
                    throw new InvalidDataException("Icon: only 8-bit RGBA, non-interlaced PNG frames are supported.");
            }
            else if (type == "IDAT")
                idat.Add(data);
            else if (type == "IEND")
                break;
        }

        if (width == 0 || height == 0 || idat.Count == 0)
            throw new InvalidDataException("Icon: the PNG frame has no image data.");

        var total = idat.Sum(d => d.Length);
        var packed = new byte[total];
        var at = 0;
        foreach (var chunk in idat)
        {
            chunk.CopyTo(packed, at);
            at += chunk.Length;
        }

        // Inflate, then undo each row's filter into the straight-RGBA image.
        using var src = new MemoryStream(packed);
        using var zlib = new ZLibStream(src, System.IO.Compression.CompressionMode.Decompress);
        var stride = width * 4;
        var expected = height * (1 + stride);
        var defiltered = new byte[expected];
        var read = 0;
        var buffer = new byte[expected];
        int n;
        while (read < expected && (n = zlib.Read(buffer, 0, buffer.Length - read)) > 0)
        {
            Buffer.BlockCopy(buffer, 0, defiltered, read, n);
            read += n;
        }
        if (read < expected)
            throw new InvalidDataException("Icon: the PNG frame is truncated.");

        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var filter = defiltered[y * (1 + stride)];
            var row = y * (1 + stride) + 1;
            for (var x = 0; x < stride; x++)
            {
                var raw = defiltered[row + x];
                var left = x >= 4 ? defiltered[row + x - 4] : (byte)0;
                var up = y > 0 ? defiltered[(y - 1) * (1 + stride) + 1 + x] : (byte)0;
                var upLeft = y > 0 && x >= 4 ? defiltered[(y - 1) * (1 + stride) + 1 + x - 4] : (byte)0;
                defiltered[row + x] = filter switch
                {
                    0 => raw,
                    1 => (byte)(raw + left),
                    2 => (byte)(raw + up),
                    3 => (byte)(raw + (byte)((left + up) / 2)),
                    4 => (byte)(raw + Paeth(left, up, upLeft)),
                    _ => throw new InvalidDataException($"Icon: unknown PNG filter {filter}."),
                };
            }

            for (var x = 0; x < width; x++)
            {
                var s = row + x * 4;
                var d = (y * width + x) * 4;
                rgba[d] = defiltered[s];     // R
                rgba[d + 1] = defiltered[s + 1];
                rgba[d + 2] = defiltered[s + 2];
                rgba[d + 3] = defiltered[s + 3];
            }
        }

        return (rgba, width, height);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // --- Minimal PNG encoder: signature, IHDR, one zlib-compressed IDAT (filter 0), IEND. ---
    // Used only to re-encode a DIB frame into the hicolor theme PNG; PNG frames pass through.

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] EncodePng(int width, int height, byte[] rgba)
    {
        using var output = new MemoryStream();
        output.Write(PngSignature);

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // colour type: RGBA
        WriteChunk(output, "IHDR", ihdr);

        var raw = new byte[height * (1 + width * 4)];
        for (var y = 0; y < height; y++)
        {
            raw[y * (1 + width * 4)] = 0; // filter 0
            // The source is already straight RGBA, top-down; PNG wants the same per pixel.
            for (var x = 0; x < width; x++)
            {
                var s = (y * width + x) * 4;
                var d = y * (1 + width * 4) + 1 + x * 4;
                raw[d] = rgba[s];
                raw[d + 1] = rgba[s + 1];
                raw[d + 2] = rgba[s + 2];
                raw[d + 3] = rgba[s + 3];
            }
        }

        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal))
            zlib.Write(raw);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        // PNG chunk framing is big-endian: length first, then type+data, then the CRC -- each
        // multi-byte field written high byte first, never through BitConverter.
        WriteBigEndian(output, data.Length);
        output.Write(typeAndData(type, data));
        WriteBigEndian(output, (int)Crc32(typeAndData(type, data)));

        static byte[] typeAndData(string type, byte[] data)
        {
            var buffer = new byte[4 + data.Length];
            for (var i = 0; i < 4; i++)
                buffer[i] = (byte)type[i];
            data.CopyTo(buffer, 4);
            return buffer;
        }
    }

    private static void WriteBigEndian(Stream output, int value)
    {
        output.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    /// <summary>The zlib CRC-32, table-driven, as PNG chunks require.</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint[] table = new uint[256];
        for (var i = 0u; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }

        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
            crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
#endif

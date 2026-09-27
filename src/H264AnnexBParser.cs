namespace Clippy;

/// <summary>Splits a raw H.264 Annex B byte stream into individual NAL units.</summary>
internal sealed class H264AnnexBParser
{
    private readonly record struct StartCode(int Index, int PayloadOffset);

    private byte[] pending = new byte[1 << 16];
    private int pendingLength;
    private int searchFrom;

    /// <summary>
    /// Payload start of a NAL that is still being accumulated, or null when the parser sits on a
    /// delimiter. This must be explicit state: re-searching for "a start code" on the next chunk would
    /// find the DELIMITER of the in-progress NAL and mistake it for the beginning of the next one,
    /// which silently swallows every NAL but the first.
    /// </summary>
    private int? pendingNalStart;

    /// <summary>
    /// Feeds a chunk of stdout bytes and emits every NAL that became complete.
    /// The capture clock is stamped on arrival at this parser, never read back from the stream, so no
    /// ffmpeg-internal decision (encoder delay, bitrate guess, container rewrite) can influence it.
    /// </summary>
    public void Append(ReadOnlySpan<byte> data, double captureSeconds, Action<byte[], int, bool, double> emit)
    {
        EnsureCapacity(pendingLength + data.Length);
        data.CopyTo(pending.AsSpan(pendingLength));
        pendingLength += data.Length;
        Drain(emit, captureSeconds);
    }

    public void Flush(double captureSeconds, Action<byte[], int, bool, double> emit)
    {
        if (pendingLength == 0)
            return;

        // Emit whatever NAL is still open. The buffer begins with a delimiter, so the payload does not
        // start at index 0: emitting from there would swallow the start code into the NAL.
        var startOffset = pendingNalStart;
        if (startOffset is null)
        {
            var start = FindStartCode(0);
            if (start.Index < 0)
            {
                EmitNal(0, pendingLength, emit, captureSeconds);
            }
            else
            {
                EmitNal(start.PayloadOffset, pendingLength - start.PayloadOffset, emit, captureSeconds);
            }
        }
        else
        {
            EmitNal(startOffset.Value, pendingLength - startOffset.Value, emit, captureSeconds);
        }

        pendingLength = 0;
        searchFrom = 0;
        pendingNalStart = null;
    }

    private void Drain(Action<byte[], int, bool, double> emit, double captureSeconds)
    {
        while (true)
        {
            if (pendingNalStart is null)
            {
                var start = FindStartCode(searchFrom);
                if (start.Index < 0)
                {
                    // Nothing seen yet. Keep at most two trailing zeros: they may be the head of a
                    // start code split across chunks. Anything earlier can never become one.
                    var keep = Math.Min(TrailingZeroRunLength(), 2);
                    if (keep > 0)
                        Buffer.BlockCopy(pending, pendingLength - keep, pending, 0, keep);
                    pendingLength = keep;
                    return;
                }

                pendingNalStart = start.PayloadOffset;
            }

            var next = FindStartCode(pendingNalStart.Value);
            if (next.Index < 0)
                return; // the NAL is still incomplete; wait for more bytes

            // The NAL ends where the next delimiter BEGINS, not where its payload begins: for the
            // 4-byte form those differ by the extra trailing zero, and using PayloadOffset here would
            // swallow the start code into the previous NAL.
            EmitNal(pendingNalStart.Value, next.Index - pendingNalStart.Value, emit, captureSeconds);

            // Re-anchor so the buffer begins with the next delimiter.
            var remaining = pendingLength - next.Index;
            Buffer.BlockCopy(pending, next.Index, pending, 0, remaining);
            pendingLength = remaining;
            searchFrom = 0;
            pendingNalStart = null;
        }
    }

    /// <summary>
    /// Emits the NAL whose payload is [payloadStart, payloadStart+length), dropping the
    /// trailing_zero_8bits Annex B appends before the next start code.
    /// </summary>
    private void EmitNal(int payloadStart, int length, Action<byte[], int, bool, double> emit, double captureSeconds)
    {
        while (length > 0 && pending[payloadStart + length - 1] == 0)
            length--;

        if (length <= 0)
            return;

        // nal_unit_type is the low 5 bits of the first payload byte; 5 marks an IDR.
        var isIdr = (pending[payloadStart] & 0x1F) == 5;
        var copy = new byte[length];
        Buffer.BlockCopy(pending, payloadStart, copy, 0, length);
        emit(copy, length, isIdr, captureSeconds);
    }

    /// <summary>
    /// Finds the next start code at or after <paramref name="from"/>, returning where the NAL payload
    /// begins. Handles 3- and 4-byte forms, and honours emulation prevention: 00 00 03 01 is the
    /// escaped form of 00 00 01 inside a payload and must not be treated as a boundary.
    /// </summary>
    private StartCode FindStartCode(int from)
    {
        for (var i = from; i < pendingLength - 2; i++)
        {
            if (pending[i] != 0 || pending[i + 1] != 0)
                continue;

            if (pending[i + 2] == 1)
            {
                // 4-byte form when the preceding byte is also zero; that extra zero is a trailing zero
                // of the PREVIOUS nal, so the boundary moves back while the payload offset stays put.
                if (i >= 1 && pending[i - 1] == 0)
                    return new StartCode(i - 1, i + 3);
                return new StartCode(i, i + 3);
            }

            if (pending[i + 2] == 3 && i + 3 < pendingLength && pending[i + 3] == 1)
                i += 2; // escaped payload sequence, not a boundary
        }

        return new StartCode(-1, -1);
    }

    private int TrailingZeroRunLength()
    {
        var n = 0;
        for (var i = pendingLength - 1; i >= 0 && pending[i] == 0; i--)
            n++;
        return n;
    }

    private void EnsureCapacity(int needed)
    {
        if (pending.Length >= needed)
            return;
        var size = pending.Length;
        while (size < needed)
            size *= 2;
        Array.Resize(ref pending, size);
    }
}

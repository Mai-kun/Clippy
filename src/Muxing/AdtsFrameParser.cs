namespace Clippy;

/// <summary>Splits a raw ADTS (raw AAC) byte stream into individual frames.</summary>
internal sealed class AdtsFrameParser
{
    private byte[] pending = new byte[1 << 16];
    private int pendingLength;
    private int searchFrom;

    /// <summary>
    /// Feeds a chunk of stdout bytes and emits every frame that became complete. The capture clock is
    /// stamped on arrival here, so it is independent of anything ffmpeg did to the stream.
    /// </summary>
    public void Append(ReadOnlySpan<byte> data, double captureSeconds, Action<byte[], int, double> emit)
    {
        EnsureCapacity(pendingLength + data.Length);
        data.CopyTo(pending.AsSpan(pendingLength));
        pendingLength += data.Length;

        while (true)
        {
            var sync = FindSyncWord(searchFrom);
            if (sync < 0)
            {
                // No plausible sync: keep one byte in case a sync word straddles the chunk boundary.
                if (pendingLength > 0)
                    pending[0] = pending[pendingLength - 1];
                pendingLength = pendingLength > 0 ? 1 : 0;
                searchFrom = 0;
                return;
            }

            // A 7-byte header is mandatory (the no-CRC variant carries 9, which still fits).
            if (pendingLength - sync < 7)
            {
                searchFrom = sync;
                return;
            }

            var length = FrameLength(sync);
            if (length < 7)
            {
                searchFrom = sync + 1; // bogus length, resynchronise past this byte
                continue;
            }

            if (pendingLength - sync < length)
            {
                searchFrom = sync; // frame still incomplete, wait for more bytes
                return;
            }

            var copy = new byte[length];
            Buffer.BlockCopy(pending, sync, copy, 0, length);
            emit(copy, length, captureSeconds);

            var remaining = pendingLength - (sync + length);
            Buffer.BlockCopy(pending, sync + length, pending, 0, remaining);
            pendingLength = remaining;
            searchFrom = 0;
        }
    }

    private int FindSyncWord(int from)
    {
        for (var i = from; i < pendingLength - 1; i++)
        {
            // 12-bit sync word: 0xFFF, i.e. both bytes have the top nibble set.
            if (pending[i] == 0xFF && (pending[i + 1] & 0xF0) == 0xF0)
                return i;
        }
        return -1;
    }

    /// <summary>13-bit frame length: 3 bits in byte 3, 8 in byte 4, 2 in byte 5.</summary>
    private int FrameLength(int offset) =>
        ((pending[offset + 3] & 0x03) << 11) | (pending[offset + 4] << 3) | (pending[offset + 5] >> 5);

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

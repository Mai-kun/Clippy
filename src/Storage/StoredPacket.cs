using System.Buffers;

namespace Clippy;

/// <summary>
/// One elementary-stream packet held in the ring buffer.
/// Data is rented from <see cref="ArrayPool{T}"/> and is longer than <see cref="Length"/>, so the
/// real span is always taken from 0..Length, never from the array's full length.
/// </summary>
internal sealed class StoredPacket
{
    public byte[] Data { get; }
    public int Length { get; }
    public double CaptureClockSeconds { get; }
    public bool IsKeyframe { get; }

    public StoredPacket(byte[] data, int length, double captureClockSeconds, bool isKeyframe)
    {
        Data = data;
        Length = length;
        CaptureClockSeconds = captureClockSeconds;
        IsKeyframe = isKeyframe;
    }

    public ReadOnlySpan<byte> Span => Data.AsSpan(0, Length);

    /// <summary>Copies the packet's bytes into a plain array and returns the rented buffer to the pool.</summary>
    public byte[] ToArrayAndRelease()
    {
        var copy = Data.AsSpan(0, Length).ToArray();
        Release();
        return copy;
    }

    public void Release() => ArrayPool<byte>.Shared.Return(Data);
}

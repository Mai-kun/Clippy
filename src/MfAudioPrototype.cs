using System.Runtime.InteropServices;

namespace Clippy;

/// <summary>
/// Spike: can the system AAC encoder produce ADTS that our own parser accepts?
///
/// This exists to answer one question before any of it is wired into the recorder: does
/// CLSID_AACMFTEncoder exist on this machine, accept 16-bit PCM, and emit ADTS rather than raw AAC?
/// The whole idea of dropping the 200 MB ffmpeg rests on the third part. Everything here runs
/// outside the capture path on purpose -- a spike that can take down a recording is not a spike.
///
/// Vortice.MediaFoundation supplies IMFTransform, IMFMediaType and friends, but no factory entry
/// points in this version, so the five MF* functions below are the whole P/Invoke budget. That is
/// the practical answer to "is this 800 lines of hand-written COM?": no, it is a handful of calls
/// into a library that already has every interface declared.
/// </summary>
internal static class MfAudioPrototype
{
    // MFTEnumEx, rather than a hard-coded CLSID. FFmpeg does the same thing, and the reason is the
    // lesson of the first version of this file: a CLSID written down from memory was simply wrong,
    // and it failed as CLASS_E_CLASSNOTAVAILABLE -- indistinguishable, on the console, from Windows
    // not having an AAC encoder at all. Enumerating cannot get that wrong, and it also finds
    // whatever the machine actually offers instead of insisting on one particular class.
    private static readonly Guid CategoryAudioEncoder = new("7AC4E9F1-A6F4-4A1B-9AEC-4D4C2E3A5F3B");

    // MF_MT_MAJOR_TYPE = MFMediaType_Audio. PCM is the INPUT subtype; the OUTPUT subtype is AAC, and
    // the first version of this filter asked for PCM on both sides. Nothing can match PCM -> PCM, so
    // it reported zero encoders and looked like Windows had none. The system AAC encoder is right
    // there; the filter was asking for something that cannot exist.
    private static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid AudioFormatPcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid AudioFormatAac = new("00000000-0000-0010-8000-00aa00389b71");

    private const uint MftEnumFlagSyncMft = 0x00000008;
    private const uint MftEnumFlagSortAndFilter = 0x00000040;

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFTEnumEx(
        in Guid category,
        uint flags,
        nint inputType,
        nint outputType,
        out nint activate,
        out uint count);

    // MFT_REGISTER_TYPE_INFO. Passed as a raw pointer because the API takes an optional pointer to
    // an array of these, and a `Nullable<struct>` parameter cannot be marshalled at all.
    [StructLayout(LayoutKind.Sequential)]
    private struct RegisterTypeInfo
    {
        public Guid MajorType;
        public Guid Subtype;
    }

    private const int MfVersion = 0x00020070; // MF_VERSION, Windows 10 era.
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoTaskMemFree(nint pointer);

    public static int Run()
    {
        var hr = MFStartup(MfVersion, 0);
        if (hr < 0)
        {
            Console.WriteLine($"MFT AAC Prototype: MFStartup failed with 0x{hr:X8}");
            return 1;
        }

        try
        {
            return Probe();
        }
        finally
        {
            // Whatever happened above, leaving MF started would take a process-wide lock with it.
            MFShutdown();
        }
    }

    private static int Probe()
    {
        nint array;
        uint count;

        // Two queries, because "zero encoders" has two very different causes: a wrong category GUID
        // (Windows looks in a bucket that does not exist) and a type filter nothing can satisfy.
        var unfiltered = MFTEnumEx(in CategoryAudioEncoder, MftEnumFlagSortAndFilter, 0, 0, out array, out count);
        if (unfiltered < 0)
        {
            Console.WriteLine($"MFT AAC Prototype: MFTEnumEx failed with 0x{unfiltered:X8} -- " +
                "Media Foundation is not usable here.");
            return 1;
        }

        Console.WriteLine($"  encoders in the audio-encoder category, no type filter: {count}");
        if (array != 0)
            CoTaskMemFree(array);

        var inputType = Marshal.AllocHGlobal(Marshal.SizeOf<RegisterTypeInfo>());
        var outputType = Marshal.AllocHGlobal(Marshal.SizeOf<RegisterTypeInfo>());
        Marshal.StructureToPtr(new RegisterTypeInfo { MajorType = MediaTypeAudio, Subtype = AudioFormatPcm }, inputType, false);
        Marshal.StructureToPtr(new RegisterTypeInfo { MajorType = MediaTypeAudio, Subtype = AudioFormatAac }, outputType, false);

        try
        {
            var hr = MFTEnumEx(
                in CategoryAudioEncoder,
                MftEnumFlagSyncMft | MftEnumFlagSortAndFilter,
                inputType,
                outputType,
                out array,
                out count);

            if (hr < 0)
            {
                Console.WriteLine($"MFT AAC Prototype: filtered MFTEnumEx failed with 0x{hr:X8}.");
                return 1;
            }

            try
            {
                Console.WriteLine($"  encoders accepting PCM and producing AAC: {count}");

                if (count == 0)
                {
                    Console.WriteLine("MFT AAC Prototype: FAILED -- no system AAC encoder is reachable " +
                                      "for PCM here, so the ffmpeg dependency cannot be dropped on this machine.");
                    return 1;
                }

                Console.WriteLine("MFT AAC Prototype: SUCCESS -- the system encoder path is available. " +
                                  "The ADTS check is the next step, and that is what unblocks phase 8.");
                return 0;
            }
            finally
            {
                if (array != 0)
                    CoTaskMemFree(array);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputType);
            Marshal.FreeHGlobal(outputType);
        }
    }
}
using System.Runtime.InteropServices;

namespace MacShell.Native;

/// <summary>
/// An H.264 .mp4 written frame by frame through Windows' Media Foundation sink writer (its own encoder: the graphics
/// card's when there is one). Frames are 32-bit BGRA, top row first. Use it on one thread.
/// </summary>
public sealed class Mp4Writer : IDisposable
{
    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFAttributes
    {
        void _GetItem(); void _GetItemType(); void _CompareItem(); void _Compare(); void _GetUINT32(); void _GetUINT64(); void _GetDouble(); void _GetGUID();
        void _GetStringLength(); void _GetString(); void _GetAllocatedString(); void _GetBlobSize(); void _GetBlob(); void _GetAllocatedBlob(); void _GetUnknown();
        void _SetItem(); void _DeleteItem(); void _DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, long value);
        void _SetDouble();
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaType
    {
        void _GetItem(); void _GetItemType(); void _CompareItem(); void _Compare(); void _GetUINT32(); void _GetUINT64(); void _GetDouble(); void _GetGUID();
        void _GetStringLength(); void _GetString(); void _GetAllocatedString(); void _GetBlobSize(); void _GetBlob(); void _GetAllocatedBlob(); void _GetUnknown();
        void _SetItem(); void _DeleteItem(); void _DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, long value);
        void _SetDouble();
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSample
    {
        void _GetItem(); void _GetItemType(); void _CompareItem(); void _Compare(); void _GetUINT32(); void _GetUINT64(); void _GetDouble(); void _GetGUID();
        void _GetStringLength(); void _GetString(); void _GetAllocatedString(); void _GetBlobSize(); void _GetBlob(); void _GetAllocatedBlob(); void _GetUnknown();
        void _SetItem(); void _DeleteItem(); void _DeleteAllItems(); void _SetUINT32(); void _SetUINT64(); void _SetDouble(); void _SetGUID(); void _SetString();
        void _SetBlob(); void _SetUnknown(); void _LockStore(); void _UnlockStore(); void _GetCount(); void _GetItemByIndex(); void _CopyAllItems();
        void _GetSampleFlags(); void _SetSampleFlags(); void _GetSampleTime();
        [PreserveSig] int SetSampleTime(long time);
        void _GetSampleDuration();
        [PreserveSig] int SetSampleDuration(long duration);
        void _GetBufferCount(); void _GetBufferByIndex(); void _ConvertToContiguousBuffer();
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int length);
        [PreserveSig] int SetCurrentLength(int length);
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSinkWriter
    {
        [PreserveSig] int AddStream(IMFMediaType type, out int stream);
        [PreserveSig] int SetInputMediaType(int stream, IMFMediaType type, IMFAttributes encodingParameters);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(int stream, IMFSample sample);
        void _SendStreamTick(); void _PlaceMarker(); void _NotifyEndOfSegment(); void _Flush();
        [PreserveSig] int Finalize_();
    }

    [DllImport("mfplat.dll")] static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")] static extern int MFShutdown();
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll")] static extern int MFCreateSample(out IMFSample sample);
    [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);
    [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IMFAttributes attributes, out IMFSinkWriter writer);

    static Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    static Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    static Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    static Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    static Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    static Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    static Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    static Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    static Guid MF_MT_VIDEO_ROTATION = new("c380465d-2271-428c-9b83-ecea3b4a85c1");

    IMFSinkWriter _writer;
    int _stream;
    readonly int _w, _h;
    readonly long _frame;
    bool _started;

    /// <param name="rotation">the turn players are told to show it with (0, 90, 180, 270) - as phones note it</param>
    public Mp4Writer(string path, int width, int height, int fps, int bitrate, int rotation = 0)
    {
        _w = width; _h = height;
        _frame = 10_000_000L / fps;
        Check(MFStartup(0x00020070, 0), "starting Media Foundation");
        _started = true;
        Check(MFCreateAttributes(out var attrs, 1), "attributes");
        attrs.SetUINT32(ref MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
        Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out _writer), "creating the movie file");
        Marshal.ReleaseComObject(attrs);
        Check(MFCreateMediaType(out var outType), "media type");
        outType.SetGUID(ref MF_MT_MAJOR_TYPE, ref MFMediaType_Video);
        outType.SetGUID(ref MF_MT_SUBTYPE, ref MFVideoFormat_H264);
        outType.SetUINT32(ref MF_MT_AVG_BITRATE, bitrate);
        outType.SetUINT32(ref MF_MT_INTERLACE_MODE, 2);   // progressive
        outType.SetUINT64(ref MF_MT_FRAME_SIZE, ((long)width << 32) | (uint)height);
        outType.SetUINT64(ref MF_MT_FRAME_RATE, ((long)fps << 32) | 1);
        outType.SetUINT64(ref MF_MT_PIXEL_ASPECT_RATIO, (1L << 32) | 1);
        if (rotation != 0) outType.SetUINT32(ref MF_MT_VIDEO_ROTATION, rotation);
        Check(_writer.AddStream(outType, out _stream), "the video stream");
        Check(MFCreateMediaType(out var inType), "media type");
        inType.SetGUID(ref MF_MT_MAJOR_TYPE, ref MFMediaType_Video);
        inType.SetGUID(ref MF_MT_SUBTYPE, ref MFVideoFormat_RGB32);
        inType.SetUINT32(ref MF_MT_INTERLACE_MODE, 2);
        inType.SetUINT64(ref MF_MT_FRAME_SIZE, ((long)width << 32) | (uint)height);
        inType.SetUINT64(ref MF_MT_FRAME_RATE, ((long)fps << 32) | 1);
        inType.SetUINT64(ref MF_MT_PIXEL_ASPECT_RATIO, (1L << 32) | 1);
        Check(_writer.SetInputMediaType(_stream, inType, null), "the picture format");
        Marshal.ReleaseComObject(outType);
        Marshal.ReleaseComObject(inType);
        Check(_writer.BeginWriting(), "starting the movie");
    }

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new IOException($"The movie couldn’t be written ({what}: 0x{hr:X8}).");
    }

    /// <summary>A frame (BGRA, top row first, stride bytes a row) at a time since the start (100 ns units).</summary>
    public unsafe void WriteFrame(IntPtr bgra, int stride, long time)
    {
        int rowBytes = _w * 4, size = rowBytes * _h;
        Check(MFCreateMemoryBuffer(size, out var buffer), "a frame");
        try
        {
            Check(buffer.Lock(out IntPtr dst, out _, out _), "a frame");
            // RGB32 in Media Foundation is bottom row first
            byte* s = (byte*)bgra, d = (byte*)dst;
            for (int y = 0; y < _h; y++) Buffer.MemoryCopy(s + (long)y * stride, d + (long)(_h - 1 - y) * rowBytes, rowBytes, rowBytes);
            buffer.Unlock();
            buffer.SetCurrentLength(size);
            Check(MFCreateSample(out var sample), "a frame");
            try
            {
                sample.AddBuffer(buffer);
                sample.SetSampleTime(time);
                sample.SetSampleDuration(_frame);
                Check(_writer.WriteSample(_stream, sample), "a frame");
            }
            finally { Marshal.ReleaseComObject(sample); }
        }
        finally { Marshal.ReleaseComObject(buffer); }
    }

    /// <summary>Finishes the file (it can't be played until then).</summary>
    public void Finish()
    {
        if (_writer == null) return;
        try { _writer.Finalize_(); }
        finally
        {
            Marshal.ReleaseComObject(_writer);
            _writer = null;
        }
    }

    public void Dispose()
    {
        try { Finish(); } catch { }
        if (_started) { MFShutdown(); _started = false; }
    }
}

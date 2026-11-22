using System.Runtime.InteropServices;

namespace FilesizeChecker.Inventory;

// Thin adapter over IMFSourceReader, IMFAttributes, IMFSample and IMFMediaBuffer.
internal sealed unsafe class WindowsVideoReader : IDisposable
{
    private const int VideoStream = -4;
    private const uint NativeTypeChanged = 0x10;
    private const uint OutputTypeChanged = 0x20;
    private static readonly Guid VideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    internal static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    internal static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid VideoType = new("73646976-0000-0010-8000-00aa00389b71");
    internal static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    internal static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid Duration = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private static readonly Guid Buffer2D = new("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");
    private static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MinimumDisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    private static readonly Guid GeometricAperture = new("66758743-7e5f-400d-980a-aa8596c85696");
    private nint reader;

    private FrameLayout frameLayout;

    private readonly record struct FrameLayout(int Width, int Height, int Stride, VideoArea Area);

    [StructLayout(LayoutKind.Sequential)]
    private struct VideoArea
    {
        public ushort FractionX;
        public short X;
        public ushort FractionY;
        public short Y;
        public int Width;
        public int Height;
    }

    private WindowsVideoReader(string path, bool preview)
    {
        nint attributes = 0, output = 0;
        try
        {
            if (preview)
            {
                Check(MFCreateAttributes(out attributes, 1), "Videoattribute");
                SetUInt(attributes, VideoProcessing, 1);
            }
            Check(MFCreateSourceReaderFromURL(path, attributes, out reader), "MP4 öffnen");
            if (preview)
            {
                var selection = (delegate* unmanaged[Stdcall]<nint, int, int, int>)Slot(reader, 4);
                Check(selection(reader, -2, 0), "Spuren abwählen");
                Check(selection(reader, VideoStream, 1), "Videospur auswählen");
                Check(MFCreateMediaType(out output), "Ausgabeformat erstellen");
                SetGuid(output, MajorType, VideoType);
                SetGuid(output, Subtype, Rgb32);
                Check(((delegate* unmanaged[Stdcall]<nint, int, nint, nint, int>)Slot(reader, 7))(
                    reader, VideoStream, 0, output), "Videodecoder einrichten");
                frameLayout = ReadFrameLayout();
            }
        }
        catch { Dispose(); throw; }
        finally { Release(output); Release(attributes); }
    }

    public static long ReadDuration(string path)
    {
        using var source = new WindowsVideoReader(path, preview: false);
        nint native = 0;
        var time = new PropVariant();
        Guid key = Duration;
        try
        {
            // Query the native track and presentation duration without configuring a decoder or reading samples.
            Check(((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Slot(source.reader, 5))(
                source.reader, VideoStream, 0, &native), "Videospur lesen");
            Check(((delegate* unmanaged[Stdcall]<nint, int, Guid*, PropVariant*, int>)Slot(source.reader, 12))(
                source.reader, -1, &key, &time), "Spieldauer lesen");
            if (time.Type != 21 || time.Value <= 0)
                throw new InvalidDataException("Die Datei hat keine auswertbare Spieldauer.");
            return time.Value;
        }
        finally { PropVariantClear(ref time); Release(native); }
    }

    public static byte[] ReadThumbnail(string path, int width, int height, CancellationToken token)
    {
        using var source = new WindowsVideoReader(path, preview: true);
        return source.ReadFrameAtOrAfter(long.MinValue, width, height, token).Pixels;
    }

    public static VideoFrame[] ReadReviewFrames(string path, long duration, int width, int height, CancellationToken token)
    {
        using var source = new WindowsVideoReader(path, preview: true);
        VideoFrame first = source.ReadFrameAtOrAfter(long.MinValue, width, height, token);
        source.Seek(duration / 2, token);
        VideoFrame middle = source.ReadFrameAtOrAfter(duration / 2, width, height, token, first);
        VideoFrame last = source.ReadLastFrame(duration, width, height, token);
        if (ReferenceEquals(middle, first) && last.Ticks <= duration / 2)
            middle = last;
        return [first, middle, last];
    }

    private VideoFrame ReadFrameAtOrAfter(long target, int width, int height, CancellationToken token, VideoFrame? fallback = null)
    {
        nint earlier = 0;
        long earlierTime = long.MinValue;
        FrameLayout earlierLayout = default;
        try
        {
            for (int attempt = 0; attempt < 20_000; attempt++)
            {
                token.ThrowIfCancellationRequested();
                (nint sample, long time, bool end) = ReadSample();
                try
                {
                    if (sample != 0 && time >= target)
                        return new VideoFrame(time, FitThumbnail(sample, frameLayout, width, height));
                    if (sample != 0 && time >= earlierTime)
                    {
                        Release(earlier);
                        earlier = sample;
                        earlierTime = time;
                        earlierLayout = frameLayout;
                        sample = 0;
                    }
                    if (end)
                    {
                        if (earlier != 0)
                            return new VideoFrame(earlierTime, FitThumbnail(earlier, earlierLayout, width, height));
                        if (fallback is not null)
                            return fallback;
                        break;
                    }
                }
                finally { Release(sample); }
            }
        }
        finally { Release(earlier); }
        throw new InvalidDataException("An der gewünschten Position konnte kein Videobild gelesen werden.");
    }

    private VideoFrame ReadLastFrame(long duration, int width, int height, CancellationToken token)
    {
        // Seek near the end and decode only the remaining part of the stream.
        long backoff = Math.Min(duration, TimeSpan.TicksPerSecond / 4);
        while (true)
        {
            Seek(duration - backoff, token);
            nint last = 0;
            long lastTime = long.MinValue;
            FrameLayout lastLayout = default;
            try
            {
                bool end;
                do
                {
                    token.ThrowIfCancellationRequested();
                    (nint sample, long time, end) = ReadSample();
                    if (sample != 0 && time >= lastTime)
                    {
                        Release(last);
                        last = sample;
                        lastTime = time;
                        lastLayout = frameLayout;
                    }
                    else
                    {
                        Release(sample);
                    }
                }
                while (!end);
                if (last != 0)
                    return new VideoFrame(lastTime, FitThumbnail(last, lastLayout, width, height));
            }
            finally { Release(last); }
            if (backoff == duration)
                throw new InvalidDataException("Das letzte Videobild konnte nicht gelesen werden.");
            backoff = backoff <= duration / 10 ? backoff * 10 : duration;
        }
    }

    private void Seek(long target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Guid timeFormat = Guid.Empty;
        var position = new PropVariant { Type = 20, Value = target };
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, PropVariant*, int>)Slot(reader, 8))(
            reader, &timeFormat, &position), "Zeitmarke anspringen");
    }

    private static byte[] FitThumbnail(nint sample, FrameLayout layout, int width, int height)
    {
        double scale = Math.Min(width / (double)layout.Area.Width, height / (double)layout.Area.Height);
        int visibleWidth = Math.Clamp((int)(layout.Area.Width * scale), 1, width);
        int visibleHeight = Math.Clamp((int)(layout.Area.Height * scale), 1, height);
        byte[] frame = ReadPixels(sample, layout, visibleWidth, visibleHeight);
        if (visibleWidth == width && visibleHeight == height)
            return frame;
        var padded = new byte[width * height * 3];
        for (int row = 0; row < visibleHeight; row++)
            frame.AsSpan(row * visibleWidth * 3, visibleWidth * 3).CopyTo(padded.AsSpan(
                ((row + (height - visibleHeight) / 2) * width + (width - visibleWidth) / 2) * 3));
        return padded;
    }

    private (nint Sample, long Time, bool End) ReadSample()
    {
        nint sample = 0;
        long time = 0;
        uint flags = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, int, uint, nint, uint*, long*, nint*, int>)Slot(reader, 9))(
                reader, VideoStream, 0, 0, &flags, &time, &sample), "Videobild lesen");
            if ((flags & 1) != 0)
                throw new InvalidDataException("Der Decoder konnte die Videospur nicht lesen.");
            if ((flags & (NativeTypeChanged | OutputTypeChanged)) != 0)
                frameLayout = ReadFrameLayout();
            return (sample, time, (flags & 2) != 0);
        }
        catch { Release(sample); throw; }
    }

    private FrameLayout ReadFrameLayout()
    {
        nint current = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(reader, 6))(reader, VideoStream, &current), "Ausgabeformat lesen");
            Guid key = Subtype;
            Guid subtype = default;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(current, 10))(current, &key, &subtype), "RGB-Ausgabe prüfen");
            if (subtype != Rgb32)
                throw new InvalidDataException("Der Decoder liefert kein unterstütztes RGB32-Ausgabeformat.");

            ulong size = GetULong(current, FrameSize);
            int width = checked((int)(size >> 32)), height = checked((int)(size & uint.MaxValue));
            if (width <= 0 || height <= 0 || (long)width * height > 36_000_000)
                throw new InvalidDataException("Die Auflösung der Decoder-Ausgabe wird nicht unterstützt.");

            key = DefaultStride;
            uint stride = 0;
            int rowBytes = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(current, 7))(current, &key, &stride) >= 0
                ? unchecked((int)stride) : width * 4;
            // Keep the buffer dimensions for stride and bounds checks. Only the aperture contains valid image data.
            VideoArea area = ReadAperture(current, MinimumDisplayAperture, width, height)
                ?? ReadAperture(current, GeometricAperture, width, height)
                ?? new VideoArea { Width = width, Height = height };
            return new FrameLayout(width, height, rowBytes, area);
        }
        finally { Release(current); }
    }

    private static VideoArea? ReadAperture(nint type, Guid key, int width, int height)
    {
        VideoArea area = default;
        uint bytes = 0;
        int result = ((delegate* unmanaged[Stdcall]<nint, Guid*, VideoArea*, uint, uint*, int>)Slot(type, 15))(
            type, &key, &area, (uint)sizeof(VideoArea), &bytes);
        if (result < 0 || bytes != sizeof(VideoArea) || area.X < 0 || area.Y < 0 || area.Width <= 0 || area.Height <= 0
            || area.X + (long)area.Width + (area.FractionX == 0 ? 0 : 1) > width
            || area.Y + (long)area.Height + (area.FractionY == 0 ? 0 : 1) > height)
            return null;
        return area;
    }

    private static byte[] ReadPixels(nint sample, FrameLayout layout, int width, int height)
    {
        nint buffer = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(sample, 41))(sample, &buffer), "Videobild bereitstellen");
            return SamplePixels(buffer, layout, width, height);
        }
        finally { Release(buffer); }
    }

    private static byte[] SamplePixels(nint buffer, FrameLayout layout, int width, int height)
    {
        nint plane = 0;
        Guid id = Buffer2D;
        if (((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(buffer, 0))(buffer, &id, &plane) >= 0)
        {
            try
            {
                uint contiguousLength = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(plane, 7))(plane, &contiguousLength), "Videobildgröße lesen");
                if (contiguousLength < (long)layout.Width * layout.Height * 4)
                    throw new InvalidDataException("Das decodierte Videobild ist unvollständig.");
                byte* pixels = null;
                int stride = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, byte**, int*, int>)Slot(plane, 3))(plane, &pixels, &stride), "Videobild sperren");
                try { return Resize(pixels, stride, layout, width, height); }
                finally { ((delegate* unmanaged[Stdcall]<nint, int>)Slot(plane, 4))(plane); }
            }
            finally { Release(plane); }
        }

        byte* data = null;
        uint bytes = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, byte**, nint, uint*, int>)Slot(buffer, 3))(buffer, &data, 0, &bytes), "Videobild sperren");
        try
        {
            long strideSize = Math.Abs((long)layout.Stride);
            if (strideSize < layout.Width * 4L || strideSize * layout.Height > bytes)
                throw new InvalidDataException("Das decodierte Videobild ist unvollständig.");
            return Resize(layout.Stride < 0 ? data + strideSize * (layout.Height - 1) : data,
                layout.Stride, layout, width, height);
        }
        finally { ((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer); }
    }

    private static byte[] Resize(byte* pixels, int stride, FrameLayout layout, int width, int height)
    {
        if (pixels == null || Math.Abs((long)stride) < layout.Width * 4L)
            throw new InvalidDataException("Das decodierte Videobild ist ungültig.");
        var result = new byte[width * height * 3];
        VideoArea area = layout.Area;
        const long fixedPointScale = 65_536;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int index = (y * width + x) * 3;
            for (int sy = 0; sy < 3; sy++)
            for (int sx = 0; sx < 3; sx++)
            {
                long horizontal = (6L * x + 2 * sx + 1) * area.Width * fixedPointScale / (6L * width);
                long vertical = (6L * y + 2 * sy + 1) * area.Height * fixedPointScale / (6L * height);
                int sourceX = area.X + (int)((horizontal + area.FractionX) / fixedPointScale);
                int sourceY = area.Y + (int)((vertical + area.FractionY) / fixedPointScale);
                byte* pixel = pixels + (long)sourceY * stride + 4 * sourceX;
                for (int c = 0; c < 3; c++)
                    result[index + c] += (byte)(pixel[c] / 9);
            }
        }
        return result;
    }

    public void Dispose() { Release(reader); reader = 0; }
    internal static void* Slot(nint obj, int slot) => (*(void***)obj)[slot];
    internal static void Release(nint obj) { if (obj != 0) Marshal.Release(obj); }
    internal static void Check(int hr, string action)
    {
        if (hr < 0)
            throw new InvalidDataException($"{action} fehlgeschlagen (0x{hr:X8}).");
    }
    internal static void SetGuid(nint obj, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(obj, 24))(obj, &key, &value), "Medientyp festlegen");
    internal static void SetUInt(nint obj, Guid key, uint value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(obj, 21))(obj, &key, value), "Medienattribut festlegen");
    private static ulong GetULong(nint obj, Guid key)
    {
        ulong value = 0;
        int result = ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Slot(obj, 8))(obj, &key, &value);
        Check(result, "Videoeigenschaften lesen");
        return value;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public long Value;
    }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [DllImport("mfplat.dll")] internal static extern int MFCreateMediaType(out nint type);
    [DllImport("mfplat.dll")] private static extern int MFCreateAttributes(out nint attributes, uint size);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MFCreateSourceReaderFromURL(string path, nint attributes, out nint reader);
}

internal sealed class WindowsVideoSession : IDisposable
{
    private readonly bool uninitialize;
    public WindowsVideoSession()
    {
        int result = CoInitializeEx(0, 0);
        uninitialize = result >= 0;
        if (result != unchecked((int)0x80010106))
            WindowsVideoReader.Check(result, "COM starten");
        try { WindowsVideoReader.Check(MFStartup(0x00020070, 0), "Windows Media Foundation starten"); }
        catch
        {
            if (uninitialize) CoUninitialize();
            throw;
        }
    }
    public void Dispose() { MFShutdown(); if (uninitialize) CoUninitialize(); }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
}

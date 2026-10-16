using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace Tessera.Tests;

/// <summary>The EXIF fields a test may set. Null leaves the tag out.</summary>
internal sealed record ExifFields(
    string? DateTimeOriginal = null,
    string? OffsetTimeOriginal = null,
    string? SubSecTimeOriginal = null,
    string? DateTimeDigitized = null,
    string? DateTime = null,
    ushort? Orientation = null);

/// <summary>
/// Real files with real headers, written byte by byte: JPEGs with an EXIF
/// APP1 segment, PNGs with eXIf or XMP chunks, MP4s with a movie header.
/// Nothing here is a fake of the reader under test; the bytes are what a
/// camera, a phone or an editor would write.
/// </summary>
internal static class MediaFiles
{
    /// <summary>A Skia JPEG with an EXIF segment straight after SOI. <paramref name="noise"/> makes it big.</summary>
    public static void Jpeg(string path, ExifFields? exif, int width = 96, int height = 64, int seed = 1, bool noise = false)
    {
        var jpeg = Encode(width, height, seed, noise, SKEncodedImageFormat.Jpeg);
        if (exif is not null)
        {
            var tiff = Tiff(exif);
            var app1 = new byte[4 + 6 + tiff.Length];
            app1[0] = 0xFF;
            app1[1] = 0xE1;
            BinaryPrimitives.WriteUInt16BigEndian(app1.AsSpan(2), (ushort)(2 + 6 + tiff.Length));
            "Exif\0\0"u8.CopyTo(app1.AsSpan(4));
            tiff.CopyTo(app1.AsSpan(10));
            jpeg = [.. jpeg[..2], .. app1, .. jpeg[2..]];
        }

        Write(path, jpeg);
    }

    /// <summary>A Skia PNG, optionally with an eXIf chunk and/or an XMP iTXt chunk after IHDR.</summary>
    public static void Png(string path, ExifFields? exif = null, string? xmpCreateDate = null, int width = 96, int height = 64, int seed = 1)
    {
        var png = Encode(width, height, seed, noise: false, SKEncodedImageFormat.Png);
        var chunks = new List<byte>();
        if (exif is not null)
        {
            chunks.AddRange(Chunk("eXIf", Tiff(exif)));
        }

        if (xmpCreateDate is not null)
        {
            var packet =
                "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
                "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" " +
                $"xmp:CreateDate=\"{xmpCreateDate}\"/></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
            byte[] itxt = [.. "XML:com.adobe.xmp"u8, 0, 0, 0, 0, 0, .. Encoding.UTF8.GetBytes(packet)];
            chunks.AddRange(Chunk("iTXt", itxt));
        }

        // Signature (8) + IHDR (4 length + 4 type + 13 data + 4 CRC).
        const int afterIhdr = 8 + 25;
        Write(path, [.. png[..afterIhdr], .. chunks, .. png[afterIhdr..]]);
    }

    /// <summary>
    /// An MP4: ftyp, then moov (mvhd + one video trak) and an mdat of
    /// <paramref name="mediaBytes"/> zeros - mdat FIRST when
    /// <paramref name="moovLast"/>, as a camera that never "fast-starts"
    /// writes it, so a reader must seek past the media to find the header.
    /// </summary>
    public static void Mp4(string path, DateTime? createdUtc, double seconds, int width, int height, int mediaBytes = 1024, bool moovLast = true, bool quarterTurn = false)
    {
        var epoch1904 = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var created = createdUtc is { } c ? (uint)(c - epoch1904).TotalSeconds : 0u;
        const uint timescale = 1000;
        var duration = (uint)(seconds * timescale);

        var mvhd = new byte[100];
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(4), created);
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(8), created);
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(12), timescale);
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(16), duration);
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(20), 0x00010000);   // rate 1.0
        BinaryPrimitives.WriteUInt16BigEndian(mvhd.AsSpan(24), 0x0100);       // volume 1.0
        Matrix(mvhd.AsSpan(36), quarterTurn: false);
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(96), 2);            // next track id

        var tkhd = new byte[84];
        tkhd[3] = 0x07;                                                        // enabled, in movie, in preview
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(4), created);
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(8), created);
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(12), 1);            // track id
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(20), duration);
        Matrix(tkhd.AsSpan(40), quarterTurn);
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(76), (uint)width << 16);
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(80), (uint)height << 16);

        var ftyp = Box("ftyp", [.. "isom"u8, 0, 0, 2, 0, .. "isom"u8, .. "mp41"u8]);
        var moov = Box("moov", [.. Box("mvhd", mvhd), .. Box("trak", Box("tkhd", tkhd))]);
        var mdat = Box("mdat", new byte[mediaBytes]);
        Write(path, moovLast ? [.. ftyp, .. mdat, .. moov] : [.. ftyp, .. moov, .. mdat]);
    }

    /// <summary>A little-endian TIFF block: IFD0 (orientation, DateTime, the Exif pointer) and the Exif sub-IFD.</summary>
    public static byte[] Tiff(ExifFields f)
    {
        var ifd0 = new List<(ushort Tag, ushort Type, uint Count, byte[] Value)>();
        var exif = new List<(ushort Tag, ushort Type, uint Count, byte[] Value)>();
        if (f.Orientation is { } o)
        {
            ifd0.Add((0x0112, 3, 1, BitConverter.GetBytes(o)));
        }

        if (f.DateTime is not null)
        {
            ifd0.Add(Ascii(0x0132, f.DateTime));
        }

        ifd0.Add((0x8769, 4, 1, new byte[4]));   // patched below
        if (f.DateTimeOriginal is not null)
        {
            exif.Add(Ascii(0x9003, f.DateTimeOriginal));
        }

        if (f.DateTimeDigitized is not null)
        {
            exif.Add(Ascii(0x9004, f.DateTimeDigitized));
        }

        if (f.OffsetTimeOriginal is not null)
        {
            exif.Add(Ascii(0x9011, f.OffsetTimeOriginal));
        }

        if (f.SubSecTimeOriginal is not null)
        {
            exif.Add(Ascii(0x9291, f.SubSecTimeOriginal));
        }

        ifd0.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        exif.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        const int ifd0At = 8;
        var exifAt = ifd0At + 2 + 12 * ifd0.Count + 4;
        var dataAt = exifAt + 2 + 12 * exif.Count + 4;
        var i = ifd0.FindIndex(e => e.Tag == 0x8769);
        ifd0[i] = (0x8769, 4, 1, BitConverter.GetBytes((uint)exifAt));

        var head = new List<byte>([.. "II"u8, 42, 0, .. BitConverter.GetBytes((uint)ifd0At)]);
        var data = new List<byte>();
        foreach (var ifd in new[] { ifd0, exif })
        {
            head.AddRange(BitConverter.GetBytes((ushort)ifd.Count));
            foreach (var (tag, type, count, value) in ifd)
            {
                head.AddRange(BitConverter.GetBytes(tag));
                head.AddRange(BitConverter.GetBytes(type));
                head.AddRange(BitConverter.GetBytes(count));
                if (value.Length <= 4)
                {
                    head.AddRange([.. value, .. new byte[4 - value.Length]]);
                }
                else
                {
                    head.AddRange(BitConverter.GetBytes((uint)(dataAt + data.Count)));
                    data.AddRange(value);
                    if (data.Count % 2 == 1)
                    {
                        data.Add(0);
                    }
                }
            }

            head.AddRange(new byte[4]);   // no next IFD
        }

        return [.. head, .. data];
    }

    private static (ushort, ushort, uint, byte[]) Ascii(ushort tag, string value)
    {
        byte[] bytes = [.. Encoding.ASCII.GetBytes(value), 0];
        return (tag, 2, (uint)bytes.Length, bytes);
    }

    private static byte[] Encode(int width, int height, int seed, bool noise, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        if (noise)
        {
            var rng = new Random(seed);
            var pixels = new byte[width * height * 4];
            rng.NextBytes(pixels);
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        }
        else
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor((byte)(seed * 37), (byte)(seed * 91), (byte)(seed * 53)));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    private static void Matrix(Span<byte> at, bool quarterTurn)
    {
        // a b u / c d v / x y w, 16.16 fixed except u, v, w (2.30).
        int[] m = quarterTurn
            ? [0, 0x00010000, 0, -0x00010000, 0, 0, 0, 0, 0x40000000]
            : [0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000];
        for (var i = 0; i < 9; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(at[(i * 4)..], m[i]);
        }
    }

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
}

/// <summary>Counts the bytes a reader asks for. Seeks are not reads.</summary>
internal sealed class CountingStream(Stream inner) : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        BytesRead += n;
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var n = inner.Read(buffer);
        BytesRead += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

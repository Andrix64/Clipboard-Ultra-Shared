using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Cus.Core.Imaging;

/// <summary>Immagine RGBA 8 bit per canale, righe dall'alto in basso.</summary>
public sealed record RgbaImage(int Width, int Height, byte[] Pixels)
{
    public int Stride => Width * 4;
}

/// <summary>
/// Codifica e decodifica PNG minimale, senza dipendenze. Le immagini viaggiano in rete come PNG;
/// le piattaforme convertono da e verso il proprio formato nativo (su Windows, DIB).
/// Decodifica: profondità 1/2/4/8/16, tipi colore 0/2/3/4/6, niente interlacciamento Adam7.
/// </summary>
public static class PngCodec
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(RgbaImage image)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Pixels.Length != image.Width * image.Height * 4)
            throw new ArgumentException("Immagine RGBA non valida.", nameof(image));

        using var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8; // profondità
        header[9] = 6; // RGBA
        WriteChunk(output, "IHDR", header);

        using (var raw = new MemoryStream())
        {
            using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            {
                for (var y = 0; y < image.Height; y++)
                {
                    zlib.WriteByte(0); // filtro None
                    zlib.Write(image.Pixels, y * image.Stride, image.Stride);
                }
            }

            WriteChunk(output, "IDAT", raw.ToArray());
        }

        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>Limite di default dei pixel accettati da <see cref="Decode"/>.</summary>
    public const long DefaultMaxPixels = 100_000_000;

    /// <summary>Legge larghezza e altezza dall'IHDR senza decodificare. False se non è un PNG valido.</summary>
    public static bool TryReadSize(ReadOnlySpan<byte> png, out int width, out int height)
    {
        width = height = 0;
        if (png.Length < 8 + 8 + 13 || !png[..8].SequenceEqual(Signature)
            || BinaryPrimitives.ReadInt32BigEndian(png[8..]) < 13 || !png.Slice(12, 4).SequenceEqual("IHDR"u8))
            return false;
        width = BinaryPrimitives.ReadInt32BigEndian(png[16..]);
        height = BinaryPrimitives.ReadInt32BigEndian(png[20..]);
        return width > 0 && height > 0;
    }

    /// <param name="maxPixels">Oltre questo numero di pixel il PNG si rifiuta senza allocare nulla.</param>
    /// <exception cref="InvalidDataException">PNG malformato o troppo grande.</exception>
    /// <exception cref="NotSupportedException">Variante PNG non supportata (per esempio Adam7).</exception>
    public static RgbaImage Decode(ReadOnlySpan<byte> png, long maxPixels = DefaultMaxPixels)
    {
        if (png.Length < 8 || !png[..8].SequenceEqual(Signature))
            throw new InvalidDataException("Non è un PNG.");

        int width = 0, height = 0, bitDepth = 0, colorType = -1;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var idat = new MemoryStream();

        var offset = 8;
        while (offset + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png[offset..]);
            if (length < 0 || offset + 12 + length > png.Length)
                throw new InvalidDataException("Chunk PNG troncato.");
            var type = Encoding.ASCII.GetString(png.Slice(offset + 4, 4));
            var data = png.Slice(offset + 8, length);
            offset += 12 + length;

            switch (type)
            {
                case "IHDR":
                    if (length < 13)
                        throw new InvalidDataException("Chunk IHDR troppo corto.");
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    bitDepth = data[8];
                    colorType = data[9];
                    if (data[12] != 0)
                        throw new NotSupportedException("PNG interlacciato (Adam7) non supportato.");
                    break;
                case "PLTE":
                    palette = data.ToArray();
                    break;
                case "tRNS":
                    transparency = data.ToArray();
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
                case "IEND":
                    offset = png.Length;
                    break;
            }
        }

        if (width <= 0 || height <= 0 || (long)width * height > Math.Min(maxPixels, DefaultMaxPixels))
            throw new InvalidDataException("Dimensioni PNG non valide o troppo grandi.");

        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException($"Tipo colore PNG {colorType} non valido."),
        };
        if (bitDepth is not (1 or 2 or 4 or 8 or 16) || (bitDepth == 16 && colorType == 3) || (bitDepth < 8 && colorType is 2 or 4 or 6))
            throw new InvalidDataException($"Profondità PNG {bitDepth} non valida per il tipo colore {colorType}.");
        if (colorType == 3 && palette is null)
            throw new InvalidDataException("PNG con palette senza chunk PLTE.");

        var bitsPerPixel = channels * bitDepth;
        var bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
        var longStride = ((long)width * bitsPerPixel + 7) / 8;
        var expected = (longStride + 1) * height;
        if (expected > Array.MaxLength)
            throw new InvalidDataException("PNG troppo grande.");
        var stride = (int)longStride;

        // Si decomprime al massimo quanto serve: i dati in più (una "bomba" di compressione) non
        // vengono mai letti.
        var raw = new byte[expected];
        idat.Position = 0;
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
        {
            if (zlib.ReadAtLeast(raw, raw.Length, throwOnEndOfStream: false) < raw.Length)
                throw new InvalidDataException("Dati PNG insufficienti.");
        }

        var pixels = new byte[width * height * 4];
        var previous = new byte[stride];
        var current = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * (stride + 1);
            var filter = raw[rowStart];
            raw.AsSpan(rowStart + 1, stride).CopyTo(current);
            Unfilter(filter, current, previous, bytesPerPixel);
            WriteRow(current, pixels.AsSpan(y * width * 4, width * 4), width, colorType, bitDepth, palette, transparency);
            (previous, current) = (current, previous);
        }

        return new RgbaImage(width, height, pixels);
    }

    private static void Unfilter(byte filter, Span<byte> row, ReadOnlySpan<byte> previous, int bpp)
    {
        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (var i = bpp; i < row.Length; i++)
                    row[i] += row[i - bpp];
                break;
            case 2:
                for (var i = 0; i < row.Length; i++)
                    row[i] += previous[i];
                break;
            case 3:
                for (var i = 0; i < row.Length; i++)
                    row[i] += (byte)(((i >= bpp ? row[i - bpp] : 0) + previous[i]) / 2);
                break;
            case 4:
                for (var i = 0; i < row.Length; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    row[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }

                break;
            default:
                throw new InvalidDataException($"Filtro PNG {filter} non valido.");
        }
    }

    private static void WriteRow(
        ReadOnlySpan<byte> row, Span<byte> target, int width, int colorType, int bitDepth, byte[]? palette, byte[]? trns)
    {
        for (var x = 0; x < width; x++)
        {
            byte r, g, b, a = 255;
            switch (colorType)
            {
                case 0:
                    {
                        var v = Sample(row, x, 0, 1, bitDepth);
                        r = g = b = Scale(v, bitDepth);
                        if (trns is { Length: >= 2 } && v == BinaryPrimitives.ReadUInt16BigEndian(trns))
                            a = 0;
                        break;
                    }

                case 2:
                    r = Scale(Sample(row, x, 0, 3, bitDepth), bitDepth);
                    g = Scale(Sample(row, x, 1, 3, bitDepth), bitDepth);
                    b = Scale(Sample(row, x, 2, 3, bitDepth), bitDepth);
                    if (trns is { Length: >= 6 }
                        && Sample(row, x, 0, 3, bitDepth) == BinaryPrimitives.ReadUInt16BigEndian(trns)
                        && Sample(row, x, 1, 3, bitDepth) == BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(2))
                        && Sample(row, x, 2, 3, bitDepth) == BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(4)))
                        a = 0;
                    break;
                case 3:
                    {
                        var index = Sample(row, x, 0, 1, bitDepth);
                        if (index * 3 + 2 >= palette!.Length)
                            throw new InvalidDataException("Indice di palette fuori range.");
                        r = palette[index * 3];
                        g = palette[index * 3 + 1];
                        b = palette[index * 3 + 2];
                        if (trns is not null && index < trns.Length)
                            a = trns[index];
                        break;
                    }

                case 4:
                    r = g = b = Scale(Sample(row, x, 0, 2, bitDepth), bitDepth);
                    a = Scale(Sample(row, x, 1, 2, bitDepth), bitDepth);
                    break;
                default:
                    r = Scale(Sample(row, x, 0, 4, bitDepth), bitDepth);
                    g = Scale(Sample(row, x, 1, 4, bitDepth), bitDepth);
                    b = Scale(Sample(row, x, 2, 4, bitDepth), bitDepth);
                    a = Scale(Sample(row, x, 3, 4, bitDepth), bitDepth);
                    break;
            }

            target[x * 4] = r;
            target[x * 4 + 1] = g;
            target[x * 4 + 2] = b;
            target[x * 4 + 3] = a;
        }
    }

    private static int Sample(ReadOnlySpan<byte> row, int x, int channel, int channels, int bitDepth)
    {
        var index = x * channels + channel;
        return bitDepth switch
        {
            8 => row[index],
            16 => BinaryPrimitives.ReadUInt16BigEndian(row[(index * 2)..]),
            _ => (row[index * bitDepth / 8] >> (8 - bitDepth - index * bitDepth % 8)) & ((1 << bitDepth) - 1),
        };
    }

    private static byte Scale(int value, int bitDepth) => bitDepth switch
    {
        8 => (byte)value,
        16 => (byte)(value >> 8),
        _ => (byte)(value * 255 / ((1 << bitDepth) - 1)),
    };

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        output.Write(buffer);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }
}

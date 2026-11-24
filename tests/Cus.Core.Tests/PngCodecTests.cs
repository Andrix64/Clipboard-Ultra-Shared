using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Cus.Core.Imaging;

namespace Cus.Core.Tests;

public class PngCodecTests
{
    [Fact]
    public void EncodeThenDecode_RoundTripsPixels()
    {
        var pixels = new byte[3 * 2 * 4];
        Random.Shared.NextBytes(pixels);
        var image = new RgbaImage(3, 2, pixels);

        var decoded = PngCodec.Decode(PngCodec.Encode(image));

        Assert.Equal(3, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Equal(pixels, decoded.Pixels);
    }

    [Fact]
    public void Decode_RgbWithSubAndPaethFilters()
    {
        // 2x2 RGB 8 bit: riga 0 con filtro Sub, riga 1 con filtro Paeth.
        byte[] raw =
        [
            1, 10, 20, 30, 5, 5, 5,
            4, 1, 2, 3, 1, 1, 1,
        ];
        var png = BuildPng(2, 2, bitDepth: 8, colorType: 2, raw);

        var image = PngCodec.Decode(png);

        Assert.Equal(new byte[] { 10, 20, 30, 255, 15, 25, 35, 255, 11, 22, 33, 255, 16, 26, 36, 255 }, image.Pixels);
    }

    [Fact]
    public void Decode_PaletteWithTransparency_And1BitDepth()
    {
        // 8x1, palette a 1 bit: pixel alternati 0/1 (0b01010101).
        var palette = new byte[] { 255, 0, 0, 0, 0, 255 };
        var png = BuildPng(8, 1, bitDepth: 1, colorType: 3, [0, 0b01010101], palette, trns: [0]);

        var image = PngCodec.Decode(png);

        Assert.Equal(new byte[] { 255, 0, 0, 0 }, image.Pixels[..4]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, image.Pixels[4..8]);
    }

    [Fact]
    public void Decode_RejectsInterlacedAndGarbage()
    {
        Assert.Throws<NotSupportedException>(() => PngCodec.Decode(BuildPng(1, 1, 8, 6, [0, 1, 2, 3, 4], interlace: 1)));
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode("non un png"u8));
    }

    private static byte[] BuildPng(int width, int height, byte bitDepth, byte colorType, byte[] raw,
        byte[]? palette = null, byte[]? trns = null, byte interlace = 0)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = bitDepth;
        header[9] = colorType;
        header[12] = interlace;
        Chunk(output, "IHDR", header);
        if (palette is not null)
            Chunk(output, "PLTE", palette);
        if (trns is not null)
            Chunk(output, "tRNS", trns);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(raw);
            Chunk(output, "IDAT", compressed.ToArray());
        }

        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    // Il decoder non verifica il CRC: qui basta un valore qualsiasi.
    private static void Chunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(Encoding.ASCII.GetBytes(type));
        output.Write(data);
        output.Write(new byte[4]);
    }
}

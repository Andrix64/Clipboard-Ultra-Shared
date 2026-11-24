using System.Buffers.Binary;
using System.Numerics;
using Cus.Core.Imaging;

namespace Cus.Platform.Windows;

/// <summary>Conversione tra DIB di Windows (CF_DIB / CF_DIBV5) e RGBA.</summary>
internal static class DibConverter
{
    private const int BI_RGB = 0;
    private const int BI_BITFIELDS = 3;

    /// <exception cref="NotSupportedException">Formato DIB non gestito (palette, compressione).</exception>
    public static RgbaImage ToRgba(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40)
            throw new InvalidDataException("DIB troppo corto.");

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        var width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);
        var colorsUsed = BinaryPrimitives.ReadInt32LittleEndian(dib[32..]);

        var topDown = rawHeight < 0;
        var height = Math.Abs(rawHeight);
        if (width <= 0 || height <= 0 || (long)width * height > 100_000_000)
            throw new InvalidDataException("Dimensioni DIB non valide.");
        if (bitCount is not (24 or 32) || compression is not (BI_RGB or BI_BITFIELDS))
            throw new NotSupportedException($"DIB a {bitCount} bit con compressione {compression} non supportato.");

        uint redMask = 0x00FF0000, greenMask = 0x0000FF00, blueMask = 0x000000FF, alphaMask = bitCount == 32 ? 0xFF000000 : 0;
        var pixelOffset = headerSize;
        if (compression == BI_BITFIELDS)
        {
            var maskOffset = headerSize >= 52 ? 40 : headerSize;
            redMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[maskOffset..]);
            greenMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[(maskOffset + 4)..]);
            blueMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[(maskOffset + 8)..]);
            alphaMask = headerSize >= 56 ? BinaryPrimitives.ReadUInt32LittleEndian(dib[(maskOffset + 12)..]) : 0;
            if (headerSize == 40)
                pixelOffset += 12;
        }

        pixelOffset += colorsUsed * 4;
        var stride = (width * bitCount + 31) / 32 * 4;
        if (dib.Length < pixelOffset + (long)stride * height)
            throw new InvalidDataException("Dati DIB insufficienti.");

        var pixels = new byte[width * height * 4];
        var anyAlpha = false;
        for (var y = 0; y < height; y++)
        {
            var sourceRow = dib.Slice(pixelOffset + (topDown ? y : height - 1 - y) * stride, stride);
            for (var x = 0; x < width; x++)
            {
                var target = (y * width + x) * 4;
                if (bitCount == 24)
                {
                    pixels[target] = sourceRow[x * 3 + 2];
                    pixels[target + 1] = sourceRow[x * 3 + 1];
                    pixels[target + 2] = sourceRow[x * 3];
                    pixels[target + 3] = 255;
                }
                else
                {
                    var value = BinaryPrimitives.ReadUInt32LittleEndian(sourceRow[(x * 4)..]);
                    pixels[target] = Extract(value, redMask);
                    pixels[target + 1] = Extract(value, greenMask);
                    pixels[target + 2] = Extract(value, blueMask);
                    var alpha = alphaMask == 0 ? (byte)255 : Extract(value, alphaMask);
                    pixels[target + 3] = alpha;
                    anyAlpha |= alpha != 0;
                }
            }
        }

        // Molte applicazioni scrivono DIB a 32 bit con alpha sempre 0: va trattato come opaco.
        if (bitCount == 32 && !anyAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
        }

        return new RgbaImage(width, height, pixels);
    }

    /// <summary>DIB a 32 bit BI_RGB, dal basso verso l'alto: il formato più compatibile.</summary>
    public static byte[] FromRgba(RgbaImage image)
    {
        var dib = new byte[40 + image.Width * image.Height * 4];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), image.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 32);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(20), image.Width * image.Height * 4);

        for (var y = 0; y < image.Height; y++)
        {
            var targetRow = 40 + (image.Height - 1 - y) * image.Width * 4;
            for (var x = 0; x < image.Width; x++)
            {
                var source = (y * image.Width + x) * 4;
                var target = targetRow + x * 4;
                dib[target] = image.Pixels[source + 2];
                dib[target + 1] = image.Pixels[source + 1];
                dib[target + 2] = image.Pixels[source];
                dib[target + 3] = image.Pixels[source + 3];
            }
        }

        return dib;
    }

    private static byte Extract(uint value, uint mask)
    {
        if (mask == 0)
            return 0;
        var shift = BitOperations.TrailingZeroCount(mask);
        var bits = BitOperations.PopCount(mask);
        var component = (value & mask) >> shift;
        return bits >= 8 ? (byte)(component >> (bits - 8)) : (byte)(component * 255 / ((1u << bits) - 1));
    }
}

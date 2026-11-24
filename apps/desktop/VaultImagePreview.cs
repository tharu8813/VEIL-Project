using System.Buffers.Binary;
using System.IO;
using System.Windows.Media.Imaging;

namespace Veil.Desktop;

/// <summary>Raster only. Preflight dimensions before native decode, no URI or file-based loading.</summary>
internal static class VaultImagePreview
{
    internal const int MaxDimension = 8192;
    internal const long MaxPixels = 12_000_000;
    private static InvalidDataException Invalid() => new("Unsupported or oversized image.");

    internal static (int Width, int Height) Dimensions(ReadOnlySpan<byte> bytes, VaultPreviewKind kind)
    {
        if (bytes.Length == 0 || bytes.Length > VaultPresentation.ImagePreviewLimit) throw Invalid();
        int width = 0, height = 0;
        if (kind == VaultPreviewKind.Png)
        {
            if (bytes.Length < 33 || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
                throw Invalid();
            uint w = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]), h = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
            if (w > MaxDimension || h > MaxDimension) throw Invalid();
            width = (int)w; height = (int)h;
            // Only ordinary PNG images; reject animated or compressed metadata chunks.
            int offset = 8;
            bool ended = false;
            while (offset <= bytes.Length - 12)
            {
                uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
                if (length > bytes.Length - offset - 12) throw Invalid();
                var type = bytes.Slice(offset + 4, 4);
                if (type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) ||
                    type.SequenceEqual("fdAT"u8) || type.SequenceEqual("iCCP"u8) ||
                    type.SequenceEqual("zTXt"u8) || type.SequenceEqual("iTXt"u8)) throw Invalid();
                offset += 12 + (int)length;
                if (type.SequenceEqual("IEND"u8))
                {
                    ended = length == 0 && offset == bytes.Length;
                    break;
                }
            }
            if (!ended) throw Invalid();
        }
        else if (kind == VaultPreviewKind.Jpeg)
        {
            if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8 ||
                bytes[^2] != 0xff || bytes[^1] != 0xd9) throw Invalid();
            int pos = 2;
            while (pos < bytes.Length)
            {
                if (bytes[pos++] != 0xff) throw Invalid();
                while (pos < bytes.Length && bytes[pos] == 0xff) pos++;
                if (pos >= bytes.Length) throw Invalid();
                byte marker = bytes[pos++];
                if (marker is 0xda or 0xd9) break;
                if (marker is 0x00 or 0xd8 or >= 0xd0 and <= 0xd7 || pos > bytes.Length - 2) throw Invalid();
                int length = BinaryPrimitives.ReadUInt16BigEndian(bytes[pos..]);
                if (length < 2 || length > bytes.Length - pos) throw Invalid();
                // Accept baseline and progressive SOF only. Reject other frame types.
                if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
                {
                    if (marker is not (0xc0 or 0xc2) || length < 8 || width != 0 || bytes[pos + 2] != 8) throw Invalid();
                    height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(pos + 3)..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(pos + 5)..]);
                }
                pos += length;
            }
        }
        else throw Invalid();
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension ||
            (long)width * height > MaxPixels) throw Invalid();
        return (width, height);
    }

    internal static BitmapSource Decode(byte[] bytes, VaultPreviewKind kind)
    {
        var (width, height) = Dimensions(bytes, kind);
        using var input = new MemoryStream(bytes, writable: false);
        const BitmapCreateOptions options = BitmapCreateOptions.IgnoreColorProfile;
        BitmapDecoder decoder = kind == VaultPreviewKind.Png
            ? new PngBitmapDecoder(input, options, BitmapCacheOption.OnLoad)
            : new JpegBitmapDecoder(input, options, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth != width ||
            decoder.Frames[0].PixelHeight != height) throw Invalid();
        // Copy pixels away from the decoder stream; no metadata or URI is passed to the visual.
        var source = new FormatConvertedBitmap(decoder.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        try
        {
            source.CopyPixels(pixels, stride, 0);
            var bitmap = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(pixels); }
    }
}

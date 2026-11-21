using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace PulseBar.Utilities;

/// <summary>
/// Builds a small BitmapImage from a thumbnail file: with DecodePixelWidth, OnLoad (no file lock,
/// so the image can be deleted with the item), then Freeze. Read from a stream so it doesn't pile up
/// in WPF's URI cache; when a card is recycled, the image leaves memory too.
/// </summary>
public sealed class ThumbnailConverter : IValueConverter
{
    /// <summary>~120px wide on the card, sharp up to 200% DPI.</summary>
    public const int DecodeWidth = 240;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path)
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;

            // Small images are never decoded larger
            int width = ReadPngWidth(stream);
            if (width > DecodeWidth)
            {
                image.DecodePixelWidth = DecodeWidth;
            }

            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            // File deleted or corrupt: stays blank
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>Reads the width from the PNG's IHDR and rewinds the stream; 0 on failure.</summary>
    private static int ReadPngWidth(Stream stream)
    {
        Span<byte> header = stackalloc byte[24];
        int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        return read == header.Length ? BinaryPrimitives.ReadInt32BigEndian(header[16..]) : 0;
    }
}

using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace PulseBar.Utilities;

/// <summary>
/// Thumbnail file থেকে ছোট BitmapImage বানায়: DecodePixelWidth দিয়ে, OnLoad (file lock থাকে না,
/// তাই item মুছলে ছবিও মোছা যায়), তারপর Freeze। Stream থেকে পড়া হয়, যাতে WPF এর URI cache এ
/// জমে না থাকে; card recycle হলে ছবিটাও memory থেকে চলে যায়।
/// </summary>
public sealed class ThumbnailConverter : IValueConverter
{
    /// <summary>Card এ ~120px চওড়া, ২০০% DPI পর্যন্ত ঝাপসা না হওয়ার জন্য।</summary>
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

            // ছোট ছবিকে বড় করে decode করা হবে না
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
            // File মুছে গেছে বা নষ্ট: ফাঁকা থাকবে
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>PNG এর IHDR থেকে চওড়া পড়ে stream কে শুরুতে ফিরিয়ে দেয়; না পারলে 0।</summary>
    private static int ReadPngWidth(Stream stream)
    {
        Span<byte> header = stackalloc byte[24];
        int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        return read == header.Length ? BinaryPrimitives.ReadInt32BigEndian(header[16..]) : 0;
    }
}

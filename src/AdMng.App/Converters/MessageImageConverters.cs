using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace AdMng.App.Converters;

/// <summary>
/// The bytes of an inline image of a message template (U1c, already validated PNG/JPEG/GIF in memory) → a small frozen
/// thumbnail for the image strip, decoded at <see cref="Height"/> pixels so a 4096 px logo does not cost 64 MB of pixels.
/// Undecodable bytes → no image (the strip still shows the name and size). One-way only.
/// </summary>
[ValueConversion(typeof(byte[]), typeof(BitmapSource))]
public sealed class MessageImageThumbnailConverter : IValueConverter
{
    /// <summary>The instance used by the views (<c>{x:Static conv:MessageImageThumbnailConverter.Instance}</c>).</summary>
    public static MessageImageThumbnailConverter Instance { get; } = new();

    /// <summary>The decoded height in pixels.</summary>
    public int Height { get; set; } = 64;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } data)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelHeight = Height;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException or IOException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

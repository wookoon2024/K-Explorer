using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WorkFileExplorer.App.Converters;

public sealed class NonLockingImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string source || string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        try
        {
            if (Path.IsPathRooted(source) && File.Exists(source))
            {
                using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }

            var uri = new Uri(source, UriKind.RelativeOrAbsolute);
            var fallback = new BitmapImage();
            fallback.BeginInit();
            fallback.CacheOption = BitmapCacheOption.OnLoad;
            fallback.UriSource = uri;
            fallback.EndInit();
            fallback.Freeze();
            return fallback;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WorkFileExplorer.App.Helpers;
using WorkFileExplorer.App.Models;

namespace WorkFileExplorer.App.Converters;

public sealed class NonLockingImageSourceConverter : IValueConverter
{
    private const int MaxCacheEntries = 200;
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, CachedImage> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> CacheOrder = new();

    private readonly record struct CachedImage(DateTime LastWriteUtc, long Length, ImageSource Image);

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
                var info = new FileInfo(source);
                lock (CacheGate)
                {
                    if (Cache.TryGetValue(source, out var cached) &&
                        cached.LastWriteUtc == info.LastWriteTimeUtc &&
                        cached.Length == info.Length)
                    {
                        return cached.Image;
                    }
                }

                // Videos (and other non-decodable formats) get their thumbnail
                // from the Windows Shell, same as Explorer.
                ImageSource? bitmap = null;
                var isVideo = FileSystemItem.IsVideoExtension(Path.GetExtension(source));
                if (isVideo)
                {
                    bitmap = ShellThumbnailHelper.GetThumbnail(source, 256);
                    if (bitmap is null)
                    {
                        // Shell could not produce a frame; show the video icon instead.
                        source = "/Assets/Icons/icon_video.png";
                    }
                }
                else if (TryLoadBitmapFromFile(source, out var decoded))
                {
                    bitmap = decoded;
                }

                if (bitmap is not null)
                {
                    lock (CacheGate)
                    {
                        if (!Cache.ContainsKey(source))
                        {
                            CacheOrder.Enqueue(source);
                        }

                        Cache[source] = new CachedImage(info.LastWriteTimeUtc, info.Length, bitmap);

                        while (Cache.Count > MaxCacheEntries && CacheOrder.Count > 0)
                        {
                            var oldest = CacheOrder.Dequeue();
                            Cache.Remove(oldest);
                        }
                    }

                    return bitmap;
                }
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

    private static bool TryLoadBitmapFromFile(string path, out ImageSource? imageSource)
    {
        imageSource = null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            imageSource = image;
            return true;
        }
        catch
        {
            // Fall back to decoder path for images with problematic metadata chunks.
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.FirstOrDefault();
            if (frame is null)
            {
                return false;
            }

            var frozen = frame.Clone();
            frozen.Freeze();
            imageSource = frozen;
            return true;
        }
        catch
        {
            return false;
        }
    }
}

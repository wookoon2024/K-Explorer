using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace WorkFileExplorer.App.Helpers;

/// <summary>
/// Extracts file thumbnails through the Windows Shell (the same thumbnails
/// Explorer shows), which covers formats WPF cannot decode such as videos.
/// </summary>
public static class ShellThumbnailHelper
{
    private static readonly Guid ShellItemImageFactoryIid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, ThumbnailFlags flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [Flags]
    private enum ThumbnailFlags
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10
    }

    public static BitmapSource? GetThumbnail(string path, int pixelSize)
    {
        try
        {
            var iid = ShellItemImageFactoryIid;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null)
            {
                return null;
            }

            var size = new NativeSize { Width = pixelSize, Height = pixelSize };
            if (factory.GetImage(size, ThumbnailFlags.ResizeToFit | ThumbnailFlags.ThumbnailOnly, out var hBitmap) != 0 ||
                hBitmap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var bitmap = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                return bitmap;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using WorkFileExplorer.App.Helpers;
using WorkFileExplorer.App.Models;

namespace WorkFileExplorer.App.Dialogs;

public partial class ImageViewerWindow : Window
{
    private static HashSet<string> _supportedExtensions = new(AppSettings.DefaultImageViewerExtensions, StringComparer.OrdinalIgnoreCase);
    public static HashSet<string> SupportedExtensions => _supportedExtensions;

    public static void UpdateSupportedExtensions(IEnumerable<string>? extensions)
    {
        var list = extensions?.Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
        _supportedExtensions = list is not null && list.Count > 0
            ? new HashSet<string>(list, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(AppSettings.DefaultImageViewerExtensions, StringComparer.OrdinalIgnoreCase);
    }

    private const double MinZoom = 0.05;
    private const double MaxZoom = 12.0;
    private const double ZoomStep = 0.1;

    private readonly ScaleTransform _scale = new(1.0, 1.0);
    private readonly RotateTransform _rotate = new(0.0);
    private readonly TransformGroup _transformGroup = new();

    private readonly List<string> _imageFiles = [];
    private readonly List<ThumbnailItem> _thumbnailItems = [];
    private readonly Dictionary<string, ThumbnailItem> _thumbnailByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FolderListItem> _folderItems = [];
    private const double FolderListWidth = 220;
    private const double FolderListHandleWidth = 18;
    private bool _folderListVisible = true;
    private bool _suppressFolderListSelectionChanged;
    private int _currentIndex = -1;
    private double _zoom = 1.0;
    private bool _fitMode = true;
    private int _rotation;
    private double _freeRotation;
    private bool _suppressFreeRotation;
    private bool _flipHorizontal;
    private bool _suppressThumbnailSelectionChanged;

    private WindowState _prevWindowState;
    private WindowStyle _prevWindowStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _prevResizeMode = ResizeMode.CanResize;
    private bool _isFullScreen;

    private double? _savedLeft;
    private double? _savedTop;
    private double? _savedWidth;
    private double? _savedHeight;
    private bool _savedMaximized;

    private static readonly int[] ZoomPresets = [25, 50, 75, 100, 150, 200, 400];
    private static readonly int[] SlideshowIntervals = [2, 3, 5, 10];
    private const string FitPresetText = "맞춤";

    private readonly System.Windows.Threading.DispatcherTimer _slideshowTimer = new();
    private int _slideshowIntervalSeconds = 3;

    private bool _isPanning;
    private bool _scrollBarUpdateQueued;
    private bool _fitUpdateQueued;
    private Point _panOrigin;
    private double _panStartOffsetX;
    private double _panStartOffsetY;

    private CancellationTokenSource? _thumbnailLoadCts;

    // EXIF orientation correction is kept apart from the user's own rotate/flip so that
    // "변형 초기화" returns to the file's intended orientation, not a sideways photo.
    private int _exifRotation;
    private bool _exifFlip;
    private bool _nearestNeighbor;
    private bool _checkerBackground;

    private int EffectiveRotation => (((_rotation + _exifRotation) % 360) + 360) % 360;
    private double EffectiveAngle => _rotation + _exifRotation + _freeRotation;
    private bool EffectiveFlip => _flipHorizontal ^ _exifFlip;
    private bool HasFreeRotation => Math.Abs(_freeRotation) > 0.01;

    // Adjustment sliders are applied through a 256-entry LUT on a background pass, so the
    // UI stays responsive on large photos.
    private readonly System.Windows.Threading.DispatcherTimer _adjustTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(140)
    };

    // Re-decodes thumbnails once the size slider settles.
    private readonly System.Windows.Threading.DispatcherTimer _thumbSizeTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250)
    };

    public static readonly DependencyProperty ThumbnailItemWidthProperty = DependencyProperty.Register(
        nameof(ThumbnailItemWidth), typeof(double), typeof(ImageViewerWindow), new PropertyMetadata(98.0));

    public static readonly DependencyProperty ThumbnailBoxWidthProperty = DependencyProperty.Register(
        nameof(ThumbnailBoxWidth), typeof(double), typeof(ImageViewerWindow), new PropertyMetadata(90.0));

    public static readonly DependencyProperty ThumbnailBoxHeightProperty = DependencyProperty.Register(
        nameof(ThumbnailBoxHeight), typeof(double), typeof(ImageViewerWindow), new PropertyMetadata(72.0));

    public double ThumbnailItemWidth
    {
        get => (double)GetValue(ThumbnailItemWidthProperty);
        private set => SetValue(ThumbnailItemWidthProperty, value);
    }

    public double ThumbnailBoxWidth
    {
        get => (double)GetValue(ThumbnailBoxWidthProperty);
        private set => SetValue(ThumbnailBoxWidthProperty, value);
    }

    public double ThumbnailBoxHeight
    {
        get => (double)GetValue(ThumbnailBoxHeightProperty);
        private set => SetValue(ThumbnailBoxHeightProperty, value);
    }
    private BitmapSource? _originalBitmap;
    private bool _suppressAdjustment;

    // Small cache of decoded neighbours so arrow keys feel instant.
    private readonly Dictionary<string, LoadedImage> _bitmapCache = new(StringComparer.OrdinalIgnoreCase);
    private const int BitmapCacheLimit = 4;

    private string? _currentExifDescription;

    private bool _wheelZooms;
    private double _thumbnailSize = 90;
    private int _jpegQuality = 92;
    private bool _suppressCompare;
    private bool _isComparing;
    private BitmapSource? _normalDisplayBitmap;

    // Animated GIF playback.
    private readonly System.Windows.Threading.DispatcherTimer _animationTimer = new();
    private readonly List<BitmapSource> _animationFrames = [];
    private readonly List<int> _animationDelays = [];
    private int _animationIndex;
    private bool _isAnimating;

    public ImageViewerWindow(string imagePath)
    {
        InitializeComponent();

        _transformGroup.Children.Add(_scale);
        _transformGroup.Children.Add(_rotate);
        ViewerImage.LayoutTransform = _transformGroup;

        _slideshowTimer.Tick += (_, _) => NavigateRelative(1);
        _adjustTimer.Tick += (_, _) =>
        {
            _adjustTimer.Stop();
            ApplyAdjustments();
        };
        _animationTimer.Tick += OnAnimationTick;
        _thumbSizeTimer.Tick += (_, _) => OnThumbSizeSettled();

        foreach (var preset in ZoomPresets)
        {
            var presetItem = new MenuItem { Header = preset + "%", Tag = preset.ToString() };
            presetItem.Click += OnZoomPresetMenuClick;
            ZoomPresetMenu.Items.Add(presetItem);
        }

        foreach (var seconds in SlideshowIntervals)
        {
            var intervalItem = new MenuItem { Header = seconds + "초", Tag = seconds.ToString() };
            intervalItem.Click += OnSlideshowIntervalMenuClick;
            SlideshowIntervalMenu.Items.Add(intervalItem);
        }

        UpdateZoomCombo();
        InitializeImageList(imagePath);
        BuildThumbnailItems();
        BuildFolderList();
        LoadViewerScalePreference();
        SlideshowIntervalText.Text = _slideshowIntervalSeconds + "초";
        ApplyFolderListVisibility();
        if (Math.Abs(SliderThumbSize.Value - _thumbnailSize) > 0.01)
        {
            SliderThumbSize.Value = _thumbnailSize;
            BuildThumbnailItems();
        }

        CheckNearestNeighbor.IsChecked = _nearestNeighbor;
        CheckCheckerBackground.IsChecked = _checkerBackground;
        CheckWheelZoom.IsChecked = _wheelZooms;
        ApplyImageOptions();
        ApplyStoredWindowPlacement();
        ShowCurrentImage();
        // Slider value labels start empty when the stored value already matches the default,
        // in which case no ValueChanged fires.
        UpdateAdjustInfo();
        UpdateThumbSizeLabel(_thumbnailSize);
        StopSlideshow(); // Auto-advance is always off when the viewer opens.

        Closing += (_, _) =>
        {
            _slideshowTimer.Stop();
            _adjustTimer.Stop();
            _thumbSizeTimer.Stop();
            StopAnimation();
            _thumbnailLoadCts?.Cancel();
            SaveViewerScalePreference();
            SaveWindowPlacement();
        };

        // At construction time the window has no layout yet, so FitToViewport()
        // sees a zero-sized viewport and bails. Re-apply fit after first layout.
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_fitMode)
            {
                FitToViewport();
            }

            UpdateScrollBarVisibility();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void ShowImage(string imagePath)
    {
        _bitmapCache.Clear();
        StopSlideshow(); // A viewer re-targeted at another image never keeps auto-advancing.
        InitializeImageList(imagePath);
        BuildThumbnailItems();
        BuildFolderList();
        ShowCurrentImage();
    }

    private LoadedImage GetOrLoadImage(string path)
    {
        if (_bitmapCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var loaded = LoadImage(path);
        _bitmapCache[path] = loaded;
        TrimBitmapCache();
        return loaded;
    }

    private void TrimBitmapCache()
    {
        while (_bitmapCache.Count > BitmapCacheLimit)
        {
            var stale = _bitmapCache.Keys.FirstOrDefault(key =>
                !string.Equals(key, CurrentImagePath, StringComparison.OrdinalIgnoreCase));
            if (stale is null)
            {
                return;
            }

            _bitmapCache.Remove(stale);
        }
    }

    private void PreloadAdjacentImages()
    {
        if (_imageFiles.Count <= 1)
        {
            return;
        }

        var targets = new List<string>(2);
        for (var offset = -1; offset <= 1; offset += 2)
        {
            var index = _currentIndex + offset;
            if (index >= 0 && index < _imageFiles.Count)
            {
                var candidate = _imageFiles[index];
                if (!_bitmapCache.ContainsKey(candidate))
                {
                    targets.Add(candidate);
                }
            }
        }

        if (targets.Count == 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            foreach (var target in targets)
            {
                try
                {
                    // Decoding off the UI thread keeps navigation smooth; LoadImage strips the
                    // decoder away so the result is safe to hand back to the UI thread.
                    var loaded = LoadImage(target);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!_bitmapCache.ContainsKey(target))
                        {
                            _bitmapCache[target] = loaded;
                            TrimBitmapCache();
                        }
                    }));
                }
                catch
                {
                }
            }
        });
    }

    private void InitializeImageList(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            throw new FileNotFoundException("Image file not found.", imagePath);
        }

        _imageFiles.Clear();
        _thumbnailItems.Clear();
        _thumbnailByPath.Clear();
        _currentIndex = -1;

        var directory = Path.GetDirectoryName(imagePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            _imageFiles.Add(imagePath);
            _currentIndex = 0;
            return;
        }

        var files = Directory.EnumerateFiles(directory)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            files.Add(imagePath);
        }

        _imageFiles.AddRange(files);
        _currentIndex = _imageFiles.FindIndex(path => string.Equals(path, imagePath, StringComparison.OrdinalIgnoreCase));
        if (_currentIndex < 0)
        {
            _imageFiles.Add(imagePath);
            _currentIndex = _imageFiles.Count - 1;
        }
    }

    private void ShowCurrentImage()
    {
        _currentExifDescription = null;
        if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count)
        {
            return;
        }

        var path = _imageFiles[_currentIndex];
        if (!File.Exists(path))
        {
            return;
        }

        StopAnimation();
        var loaded = GetOrLoadImage(path);
        var source = loaded.Pixels;
        _exifRotation = loaded.ExifRotation;
        _exifFlip = loaded.ExifFlip;
        _currentExifDescription = loaded.ExifDescription;
        _originalBitmap = source;
        SetViewerImageSource(source);
        _rotation = 0;
        _flipHorizontal = false;
        ResetFreeRotation();
        UncheckCompareSilently();
        _normalDisplayBitmap = source;
        ApplyTransform();
        ResetAdjustmentSliders();
        StartAnimationIfNeeded(path);
        PreloadAdjacentImages();

        Title = $"이미지 뷰어 - {Path.GetFileName(path)}";
        TopPathText.Text = path;
        if (_fitMode)
        {
            FitToViewport();
        }
        else
        {
            SetZoom(_zoom, keepMode: true);
        }

        UpdateInfoText();
        SyncThumbnailSelection(path);
        SyncFolderListSelection(path);
        UpdateOverlayNavButtons();
        QueueScrollBarVisibilityUpdate();
    }

    // A decoded image with everything the rest of the window needs, detached from its decoder.
    private sealed record LoadedImage(BitmapSource Pixels, int ExifRotation, bool ExifFlip, string? ExifDescription);

    private static LoadedImage LoadImage(string imagePath)
    {
        // BitmapFrame (not BitmapImage) so the EXIF metadata block stays readable. Both the
        // metadata and the pixels have to be taken here, on the decoding thread: a frame
        // backed by a live decoder refuses metadata queries from any other thread.
        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        BitmapFrame frame;
        var ext = Path.GetExtension(imagePath);
        if (string.Equals(ext, ".ico", StringComparison.OrdinalIgnoreCase))
        {
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).FirstOrDefault() ?? decoder.Frames[0];
        }
        else
        {
            frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        var (rotation, flip) = ReadExifOrientation(frame);
        var description = TryDescribeExif(frame);
        return new LoadedImage(DetachPixels(frame), rotation, flip, description);
    }

    private static BitmapSource DetachPixels(BitmapSource source)
    {
        var stride = (source.PixelWidth * source.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        var detached = BitmapSource.Create(
            source.PixelWidth,
            source.PixelHeight,
            source.DpiX,
            source.DpiY,
            source.Format,
            source.Palette,
            pixels,
            stride);
        detached.Freeze();
        return detached;
    }

    // Phone photos are often stored unrotated with an EXIF orientation tag; applying it
    // up front keeps them from opening sideways.
    private static (int Rotation, bool Flip) ReadExifOrientation(BitmapSource source)
    {
        try
        {
            if (source is not BitmapFrame frame || frame.Metadata is not BitmapMetadata metadata)
            {
                return (0, false);
            }

            var orientation = metadata.GetQuery("/app1/ifd/{uint=274}") switch
            {
                ushort value => value,
                ushort[] values when values.Length > 0 => values[0],
                int value => (ushort)value,
                _ => (ushort)1
            };

            return orientation switch
            {
                2 => (0, true),
                3 => (180, false),
                4 => (180, true),
                5 => (270, true),
                6 => (90, false),
                7 => (90, true),
                8 => (270, false),
                _ => (0, false)
            };
        }
        catch
        {
            return (0, false);
        }
    }

    private static BitmapSource? LoadThumbnail(string imagePath, int decodeHeight)
    {
        try
        {
            var ext = Path.GetExtension(imagePath);
            if (string.Equals(ext, ".ico", StringComparison.OrdinalIgnoreCase))
            {
                using var icoStream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var decoder = new IconBitmapDecoder(icoStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var bestFrame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).FirstOrDefault() ?? decoder.Frames[0];
                return DetachPixels(bestFrame);
            }

            var bitmap = new BitmapImage();
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelHeight = decodeHeight;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void BuildThumbnailItems()
    {
        _thumbnailLoadCts?.Cancel();
        _thumbnailItems.Clear();
        _thumbnailByPath.Clear();

        // Thumbnails are decoded in the background: folders with many images would
        // otherwise block the window from opening.
        foreach (var path in _imageFiles)
        {
            var item = new ThumbnailItem
            {
                Path = path,
                Name = Path.GetFileName(path)
            };

            _thumbnailItems.Add(item);
            _thumbnailByPath[path] = item;
        }

        ThumbnailList.ItemsSource = _thumbnailItems;
        if (ThumbCountText is not null)
        {
            ThumbCountText.Text = _thumbnailItems.Count == 0
                ? string.Empty
                : $"{_currentIndex + 1} / {_thumbnailItems.Count}";
        }

        StartThumbnailLoading();
    }

    private int GetThumbnailDecodeHeight() =>
        (int)Math.Clamp(Math.Round(ThumbnailBoxHeight * 1.4), 72, 160);

    private void StartThumbnailLoading()
    {
        var cts = new CancellationTokenSource();
        _thumbnailLoadCts = cts;
        var items = _thumbnailItems.ToArray();
        var decodeHeight = GetThumbnailDecodeHeight();

        _ = Task.Run(() =>
        {
            foreach (var item in items)
            {
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                var thumbnail = LoadThumbnail(item.Path, decodeHeight);
                if (thumbnail is null)
                {
                    continue;
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!cts.IsCancellationRequested)
                    {
                        item.Thumbnail = thumbnail;
                    }
                }));
            }
        }, cts.Token);
    }

    private void SyncThumbnailSelection(string currentPath)
    {
        if (!_thumbnailByPath.TryGetValue(currentPath, out var item))
        {
            return;
        }

        if (ThumbCountText is not null && _imageFiles.Count > 0)
        {
            ThumbCountText.Text = $"{_currentIndex + 1} / {_imageFiles.Count}";
        }

        // Marks the frame that is on screen even when the strip has no keyboard focus.
        foreach (var thumbnail in _thumbnailItems)
        {
            thumbnail.IsCurrent = ReferenceEquals(thumbnail, item);
        }

        _suppressThumbnailSelectionChanged = true;
        try
        {
            ThumbnailList.SelectedItem = item;
            ThumbnailList.ScrollIntoView(item);
        }
        finally
        {
            _suppressThumbnailSelectionChanged = false;
        }
    }

    private void BuildFolderList()
    {
        _folderItems.Clear();

        var currentPath = CurrentImagePath;
        var directory = currentPath is null ? null : Path.GetDirectoryName(currentPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory)
                             .Where(candidate => SupportedExtensions.Contains(Path.GetExtension(candidate)))
                             .OrderBy(candidate => candidate, StringComparer.CurrentCultureIgnoreCase))
                {
                    _folderItems.Add(new FolderListItem
                    {
                        Path = path,
                        Name = Path.GetFileName(path)
                    });
                }
            }
            catch
            {
                // Unreadable folder: show an empty list rather than failing the viewer.
            }
        }

        FolderList.ItemsSource = _folderItems;
        FolderListCountText.Text = _folderItems.Count == 0 ? string.Empty : $"{_folderItems.Count}개";
    }

    private void SyncFolderListSelection(string currentPath)
    {
        FolderListItem? current = null;
        foreach (var item in _folderItems)
        {
            var isCurrent = string.Equals(item.Path, currentPath, StringComparison.OrdinalIgnoreCase);
            item.IsCurrent = isCurrent;
            if (isCurrent)
            {
                current = item;
            }
        }

        if (current is null)
        {
            return;
        }

        _suppressFolderListSelectionChanged = true;
        try
        {
            FolderList.SelectedItem = current;
            FolderList.ScrollIntoView(current);
        }
        finally
        {
            _suppressFolderListSelectionChanged = false;
        }
    }

    private void ApplyFolderListVisibility()
    {
        // Full screen always hides the list, whatever the stored preference says.
        var visible = _folderListVisible && !_isFullScreen;
        FolderListColumn.Width = visible ? new GridLength(FolderListWidth) : new GridLength(0);
        FolderListPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        FolderListHandleColumn.Width = _isFullScreen ? new GridLength(0) : new GridLength(FolderListHandleWidth);
        FolderListHandleHost.Visibility = _isFullScreen ? Visibility.Collapsed : Visibility.Visible;
        FolderListHandleGlyph.Text = visible ? "\uE76B" : "\uE76C";
        FolderListHandleButton.ToolTip = visible ? "파일 목록 접기" : "파일 목록 펼치기";
        AutomationProperties.SetName(FolderListHandleButton, visible ? "파일 목록 접기" : "파일 목록 펼치기");
        MenuToggleFolderList.IsChecked = _folderListVisible;
    }

    private void OnToggleFolderListClick(object sender, RoutedEventArgs e)
    {
        _folderListVisible = !_folderListVisible;
        ApplyFolderListVisibility();
        SaveViewerScalePreference();
    }

    private void OnFolderListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFolderListSelectionChanged || FolderList.SelectedItem is not FolderListItem selected)
        {
            return;
        }

        var index = _imageFiles.FindIndex(path => string.Equals(path, selected.Path, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index == _currentIndex)
        {
            return;
        }

        NavigateToIndex(index);
    }

    private void SetZoom(double zoom, bool keepMode = false)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _fitMode = keepMode ? _fitMode : false;
        ApplyTransform();
        UpdateInfoText();
    }

    private void UpdateZoomCombo()
    {
        ZoomText.Text = _fitMode ? FitPresetText : $"{_zoom * 100:0}%";
        // Menu ticks so the current mode is visible without guessing.
        MenuZoomFit.IsChecked = _fitMode;
        MenuZoomActual.IsChecked = !_fitMode && Math.Abs(_zoom - 1.0) < 0.0005;
    }

    // Keeps the point under the cursor anchored while zooming.
    private void ZoomAtCursor(double zoom, Point viewportPoint)
    {
        var oldZoom = _zoom;
        if (oldZoom <= 0.0001)
        {
            SetZoom(zoom);
            return;
        }

        var contentX = ImageScrollViewer.HorizontalOffset + viewportPoint.X;
        var contentY = ImageScrollViewer.VerticalOffset + viewportPoint.Y;

        SetZoom(zoom);
        ImageScrollViewer.UpdateLayout();

        var factor = _zoom / oldZoom;
        ImageScrollViewer.ScrollToHorizontalOffset((contentX * factor) - viewportPoint.X);
        ImageScrollViewer.ScrollToVerticalOffset((contentY * factor) - viewportPoint.Y);
    }

    private void StartPanning(Point origin)
    {
        _isPanning = true;
        _panOrigin = origin;
        _panStartOffsetX = ImageScrollViewer.HorizontalOffset;
        _panStartOffsetY = ImageScrollViewer.VerticalOffset;
        ImageArea.CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    private void StopPanning()
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        ImageArea.ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }

    private bool CanPanImage =>
        ImageScrollViewer.ScrollableWidth > 0.5 || ImageScrollViewer.ScrollableHeight > 0.5;

    // Rounding at non-100% display scales leaves a fraction of a pixel of slack, which used to
    // make the scroll bars flash in even though the whole image fitted the viewport.
    private const double ScrollBarSlack = 1.5;

    private void UpdateScrollBarVisibility()
    {
        _scrollBarUpdateQueued = false;

        var horizontal = ImageScrollViewer.ScrollableWidth > ScrollBarSlack
            ? ScrollBarVisibility.Visible
            : ScrollBarVisibility.Hidden;
        if (ImageScrollViewer.HorizontalScrollBarVisibility != horizontal)
        {
            ImageScrollViewer.HorizontalScrollBarVisibility = horizontal;
        }

        var vertical = ImageScrollViewer.ScrollableHeight > ScrollBarSlack
            ? ScrollBarVisibility.Visible
            : ScrollBarVisibility.Hidden;
        if (ImageScrollViewer.VerticalScrollBarVisibility != vertical)
        {
            ImageScrollViewer.VerticalScrollBarVisibility = vertical;
        }
    }

    // ScrollableWidth/Height only settle once the pending layout pass has run.
    private void QueueScrollBarVisibilityUpdate()
    {
        if (_scrollBarUpdateQueued)
        {
            return;
        }

        _scrollBarUpdateQueued = true;
        Dispatcher.BeginInvoke(
            new Action(UpdateScrollBarVisibility),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateOverlayNavButtons()
    {
        var visibility = _imageFiles.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        NavPrevButton.Visibility = visibility;
        NavNextButton.Visibility = visibility;
    }

    private void UpdateThumbSizeLabel(double size)
    {
        if (ThumbSizeValueText is not null)
        {
            ThumbSizeValueText.Text = $"{size:0}";
        }
    }

    // The viewer sizes the image element in DIP equal to the bitmap's pixel count, so the zoom
    // factor means "screen pixels per image pixel". Without this, a bitmap carrying a non-96 DPI
    // tag (phone screenshots often say 300) is laid out at pixels * 96 / dpi and ends up rendered
    // roughly dpi/96 times smaller than the fit calculation expects.
    private void SetViewerImageSource(BitmapSource? bitmap)
    {
        if (bitmap is null)
        {
            ViewerImage.Source = null;
            ViewerImage.ClearValue(WidthProperty);
            ViewerImage.ClearValue(HeightProperty);
            return;
        }

        ViewerImage.Width = bitmap.PixelWidth;
        ViewerImage.Height = bitmap.PixelHeight;
        ViewerImage.Source = bitmap;
    }

    private void ApplyTransform()
    {
        _scale.ScaleX = (EffectiveFlip ? -1.0 : 1.0) * _zoom;
        _scale.ScaleY = _zoom;
        _rotate.Angle = EffectiveAngle;
        QueueScrollBarVisibilityUpdate();
    }

    private (double Width, double Height) GetDisplayedPixelSize(BitmapSource source)
    {
        var width = (double)source.PixelWidth;
        var height = (double)source.PixelHeight;
        var radians = EffectiveAngle * Math.PI / 180.0;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));
        return ((width * cos) + (height * sin), (width * sin) + (height * cos));
    }

    private void FitToViewport()
    {
        if (ViewerImage.Source is not BitmapSource source || ImageScrollViewer.ViewportWidth <= 0 || ImageScrollViewer.ViewportHeight <= 0)
        {
            return;
        }

        var (width, height) = GetDisplayedPixelSize(source);
        var scaleX = ImageScrollViewer.ViewportWidth / Math.Max(1, width);
        var scaleY = ImageScrollViewer.ViewportHeight / Math.Max(1, height);
        _fitMode = true;
        // 창보다 큰 이미지는 화면에 맞게 축소하고, 창보다 작은 이미지는 100%(원본 크기)로 표시
        var targetScale = Math.Min(1.0, Math.Min(scaleX, scaleY));
        SetZoom(targetScale, keepMode: true);

        // When entering fit mode, reset stale scroll offsets from previous zoom/pan.
        ImageScrollViewer.ScrollToHorizontalOffset(0);
        ImageScrollViewer.ScrollToVerticalOffset(0);
    }

    private void SaveViewerScalePreference()
    {
        try
        {
            AppPaths.EnsureCreated();
            using var connection = CreateViewerSettingsConnection();
            connection.Open();
            EnsureViewerSettingsTable(connection);

            UpsertViewerSetting(connection, "fit", _fitMode ? "1" : "0");
            UpsertViewerSetting(connection, "zoom", _zoom.ToString(CultureInfo.InvariantCulture));
            UpsertViewerSetting(connection, "nearest", _nearestNeighbor ? "1" : "0");
            UpsertViewerSetting(connection, "checker", _checkerBackground ? "1" : "0");
            UpsertViewerSetting(connection, "wheelzoom", _wheelZooms ? "1" : "0");
            UpsertViewerSetting(connection, "thumbsize", _thumbnailSize.ToString(CultureInfo.InvariantCulture));
            UpsertViewerSetting(connection, "jpegquality", _jpegQuality.ToString(CultureInfo.InvariantCulture));
            UpsertViewerSetting(connection, "folderlist", _folderListVisible ? "1" : "0");
            UpsertViewerSetting(connection, "slideshow_interval", _slideshowIntervalSeconds.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
        }
    }

    private void LoadViewerScalePreference()
    {
        try
        {
            AppPaths.EnsureCreated();
            using var connection = CreateViewerSettingsConnection();
            connection.Open();
            EnsureViewerSettingsTable(connection);

            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT setting_key, setting_value
                FROM image_viewer_settings;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var value = reader.GetString(1);
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (string.Equals(key, "fit", StringComparison.OrdinalIgnoreCase))
                {
                    _fitMode = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (string.Equals(key, "zoom", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedZoom))
                {
                    _zoom = Math.Clamp(parsedZoom, MinZoom, MaxZoom);
                    continue;
                }

                if (string.Equals(key, "win_left", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var left))
                {
                    _savedLeft = left;
                    continue;
                }

                if (string.Equals(key, "win_top", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var top))
                {
                    _savedTop = top;
                    continue;
                }

                if (string.Equals(key, "win_width", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var width))
                {
                    _savedWidth = width;
                    continue;
                }

                if (string.Equals(key, "win_height", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
                {
                    _savedHeight = height;
                    continue;
                }

                if (string.Equals(key, "nearest", StringComparison.OrdinalIgnoreCase))
                {
                    _nearestNeighbor = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (string.Equals(key, "checker", StringComparison.OrdinalIgnoreCase))
                {
                    _checkerBackground = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (string.Equals(key, "wheelzoom", StringComparison.OrdinalIgnoreCase))
                {
                    _wheelZooms = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (string.Equals(key, "folderlist", StringComparison.OrdinalIgnoreCase))
                {
                    _folderListVisible = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (string.Equals(key, "thumbsize", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var thumbSize))
                {
                    _thumbnailSize = Math.Clamp(thumbSize, 60, 200);
                    continue;
                }

                if (string.Equals(key, "jpegquality", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var jpegQuality))
                {
                    _jpegQuality = Math.Clamp(jpegQuality, 40, 100);
                    continue;
                }

                if (string.Equals(key, "slideshow_interval", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intervalSeconds))
                {
                    _slideshowIntervalSeconds = Math.Clamp(intervalSeconds, 1, 60);
                    continue;
                }

                if (string.Equals(key, "win_max", StringComparison.OrdinalIgnoreCase))
                {
                    _savedMaximized = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
        }
    }

    private void ApplyStoredWindowPlacement()
    {
        if (_savedLeft is not double left || _savedTop is not double top ||
            _savedWidth is not double width || _savedHeight is not double height ||
            double.IsNaN(left) || double.IsNaN(top) || double.IsNaN(width) || double.IsNaN(height) ||
            width <= 0 || height <= 0)
        {
            return;
        }

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualWidth = SystemParameters.VirtualScreenWidth;
        var virtualHeight = SystemParameters.VirtualScreenHeight;

        width = Math.Max(MinWidth, Math.Min(width, virtualWidth));
        height = Math.Max(MinHeight, Math.Min(height, virtualHeight));
        left = Math.Clamp(left, virtualLeft, virtualLeft + virtualWidth - width);
        top = Math.Clamp(top, virtualTop, virtualTop + virtualHeight - height);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        if (_savedMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowPlacement()
    {
        try
        {
            var maximized = _isFullScreen
                ? _prevWindowState == WindowState.Maximized
                : WindowState == WindowState.Maximized;
            var bounds = _isFullScreen || WindowState != WindowState.Normal
                ? RestoreBounds
                : new Rect(Left, Top, Width, Height);
            if (double.IsNaN(bounds.X) || double.IsNaN(bounds.Y) ||
                double.IsNaN(bounds.Width) || double.IsNaN(bounds.Height) ||
                bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            AppPaths.EnsureCreated();
            using var connection = CreateViewerSettingsConnection();
            connection.Open();
            EnsureViewerSettingsTable(connection);

            UpsertViewerSetting(connection, "win_left", bounds.X.ToString(CultureInfo.InvariantCulture));
            UpsertViewerSetting(connection, "win_top", bounds.Y.ToString(CultureInfo.InvariantCulture));
            UpsertViewerSetting(connection, "win_width", bounds.Width.ToString(CultureInfo.InvariantCulture));
            UpsertViewerSetting(connection, "win_height", bounds.Height.ToString(CultureInfo.InvariantCulture));
            UpsertViewerSetting(connection, "win_max", maximized ? "1" : "0");
        }
        catch
        {
        }
    }

    private static SqliteConnection CreateViewerSettingsConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.HistoryDbFile
        }.ToString();

        return new SqliteConnection(connectionString);
    }

    private static void EnsureViewerSettingsTable(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS image_viewer_settings (
                setting_key TEXT NOT NULL PRIMARY KEY,
                setting_value TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void UpsertViewerSetting(SqliteConnection connection, string key, string value)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO image_viewer_settings (setting_key, setting_value, updated_utc)
            VALUES ($key, $value, $updated_utc)
            ON CONFLICT(setting_key) DO UPDATE SET
                setting_value = excluded.setting_value,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updated_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private void NavigateRelative(int offset)
    {
        if (_imageFiles.Count == 0)
        {
            return;
        }

        _currentIndex = (_currentIndex + offset + _imageFiles.Count) % _imageFiles.Count;
        ShowCurrentImage();
    }

    private void NavigateToIndex(int index)
    {
        if (_imageFiles.Count == 0 || index < 0 || index >= _imageFiles.Count || index == _currentIndex)
        {
            return;
        }

        _currentIndex = index;
        ShowCurrentImage();
    }

    private void UpdateInfoText()
    {
        var sizeText = ViewerImage.Source is BitmapSource source
            ? $"{source.PixelWidth}x{source.PixelHeight}"
            : "-";
        var modeText = _fitMode ? " 맞춤" : string.Empty;

        var parts = new List<string>(5)
        {
            $"{_currentIndex + 1}/{Math.Max(_imageFiles.Count, 1)}",
            sizeText,
            $"{_zoom * 100:0}%{modeText}"
        };

        var path = CurrentImagePath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var info = new FileInfo(path);
                parts.Add(ToReadableSize(info.Length));
                parts.Add(info.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture));
            }
            catch
            {
            }

            if (_currentExifDescription is { Length: > 0 } exif)
            {
                parts.Add(exif);
            }
        }

        ImageInfoText.Text = string.Join("  |  ", parts);
        UpdateZoomCombo();
    }

    private static string ToReadableSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[unit]}"
            : $"{value:0.##} {units[unit]}";
    }

    private static string? TryDescribeExif(BitmapSource? source)
    {
        if (source is not BitmapFrame frame || frame.Metadata is not BitmapMetadata metadata)
        {
            return null;
        }

        var parts = new List<string>(6);
        AddExifText(metadata, "/app1/ifd/exif:{uint=36867}", null, parts);          // DateTimeOriginal
        AddExifText(metadata, "/app1/ifd/{uint=272}", null, parts);                 // Model
        AddExifText(metadata, "/app1/ifd/exif:{uint=33437}", "F", parts);           // FNumber
        AddExifText(metadata, "/app1/ifd/exif:{uint=33434}", null, parts, "s");     // ExposureTime
        AddExifText(metadata, "/app1/ifd/exif:{uint=34855}", "ISO", parts);         // ISOSpeedRatings
        AddExifText(metadata, "/app1/ifd/exif:{uint=37386}", null, parts, "mm");    // FocalLength
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static void AddExifText(
        BitmapMetadata metadata,
        string query,
        string? prefix,
        List<string> parts,
        string? suffix = null)
    {
        try
        {
            var text = metadata.GetQuery(query) switch
            {
                string value when !string.IsNullOrWhiteSpace(value) => value.Trim(),
                ulong[] rational when rational.Length == 2 && rational[1] != 0 =>
                    (rational[0] / (double)rational[1]).ToString("0.##", CultureInfo.InvariantCulture),
                ushort[] values when values.Length > 0 => string.Join(",", values),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add($"{(prefix ?? string.Empty)}{text}{suffix ?? string.Empty}");
            }
        }
        catch
        {
            // Malformed metadata blocks are common; skip them.
        }
    }

    private void ToggleFullScreen()
    {
        if (!_isFullScreen)
        {
            _prevWindowState = WindowState;
            _prevWindowStyle = WindowStyle;
            _prevResizeMode = ResizeMode;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _isFullScreen = true;
            ApplyFullScreenChrome();
            return;
        }

        WindowStyle = _prevWindowStyle;
        ResizeMode = _prevResizeMode;
        WindowState = _prevWindowState;
        _isFullScreen = false;
        ApplyFullScreenChrome();
    }

    // Full screen leaves the image alone: toolbars, info bar, thumbnail strip and the
    // file list step aside, and the window margin collapses so nothing borders the image.
    private void ApplyFullScreenChrome()
    {
        var chromeVisibility = _isFullScreen ? Visibility.Collapsed : Visibility.Visible;
        TopArea.Visibility = chromeVisibility;
        InfoPanel.Visibility = chromeVisibility;
        ThumbnailPanel.Visibility = chromeVisibility;
        RootGrid.Margin = _isFullScreen ? new Thickness(0) : new Thickness(8);
        ApplyFolderListVisibility();
    }

    private string? CurrentImagePath => _currentIndex >= 0 && _currentIndex < _imageFiles.Count ? _imageFiles[_currentIndex] : null;

    private void OnPrevClick(object sender, RoutedEventArgs e) => NavigateRelative(-1);
    private void OnNextClick(object sender, RoutedEventArgs e) => NavigateRelative(1);

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        SetZoom(_zoom - ZoomStep);
        SaveViewerScalePreference();
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        SetZoom(_zoom + ZoomStep);
        SaveViewerScalePreference();
    }

    private void OnActualSizeClick(object sender, RoutedEventArgs e)
    {
        _fitMode = false;
        SetZoom(1.0, keepMode: true);
        SaveViewerScalePreference();
    }

    private void OnFitClick(object sender, RoutedEventArgs e)
    {
        FitToViewport();
        SaveViewerScalePreference();
    }

    private void OnRotateLeftClick(object sender, RoutedEventArgs e)
    {
        _rotation = (_rotation + 270) % 360;
        ApplyTransform();
        if (_fitMode)
        {
            FitToViewport();
        }
        else
        {
            UpdateInfoText();
        }
    }

    private void OnRotateRightClick(object sender, RoutedEventArgs e)
    {
        _rotation = (_rotation + 90) % 360;
        ApplyTransform();
        if (_fitMode)
        {
            FitToViewport();
        }
        else
        {
            UpdateInfoText();
        }
    }

    private void OnFlipClick(object sender, RoutedEventArgs e)
    {
        _flipHorizontal = !_flipHorizontal;
        ApplyTransform();
    }

    private void OnToggleFullScreenClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OnOpenAttachedMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button || menu.Items.Count == 0)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnZoomPresetMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string selection } || string.IsNullOrWhiteSpace(selection))
        {
            return;
        }

        if (string.Equals(selection, FitPresetText, StringComparison.Ordinal))
        {
            FitToViewport();
            SaveViewerScalePreference();
            return;
        }

        if (int.TryParse(selection, out var percent) && percent > 0)
        {
            _fitMode = false;
            SetZoom(percent / 100.0, keepMode: true);
            SaveViewerScalePreference();
        }
    }

    private void OnResetTransformClick(object sender, RoutedEventArgs e)
    {
        _rotation = 0;
        _flipHorizontal = false;
        ResetFreeRotation();
        ApplyTransform();
        FitToViewport();
        SaveViewerScalePreference();
    }

    private void ResetFreeRotation()
    {
        _freeRotation = 0;
        _suppressFreeRotation = true;
        try
        {
            SliderFreeRotation.Value = 0;
        }
        finally
        {
            _suppressFreeRotation = false;
        }

        if (FreeRotationValueText is not null)
        {
            FreeRotationValueText.Text = "0°";
        }
    }

    private void OnFreeRotationChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressFreeRotation || ViewerImage is null)
        {
            return;
        }

        _freeRotation = e.NewValue;
        if (FreeRotationValueText is not null)
        {
            FreeRotationValueText.Text = $"{_freeRotation:0}°";
        }

        ApplyTransform();
        if (_fitMode)
        {
            FitToViewport();
        }
        else
        {
            UpdateInfoText();
        }
    }

    private int GetSlideshowIntervalSeconds() => _slideshowIntervalSeconds;

    private void OnSlideshowIntervalMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !int.TryParse(tag, out var seconds) || seconds <= 0)
        {
            return;
        }

        _slideshowIntervalSeconds = seconds;
        SlideshowIntervalText.Text = seconds + "초";
        if (_slideshowTimer.IsEnabled)
        {
            _slideshowTimer.Interval = TimeSpan.FromSeconds(GetSlideshowIntervalSeconds());
        }
    }

    private void OnToggleSlideshowClick(object sender, RoutedEventArgs e) => ToggleSlideshow();

    private void ToggleSlideshow()
    {
        if (_slideshowTimer.IsEnabled)
        {
            StopSlideshow();
            return;
        }

        if (_imageFiles.Count < 2)
        {
            return;
        }

        _slideshowTimer.Interval = TimeSpan.FromSeconds(GetSlideshowIntervalSeconds());
        _slideshowTimer.Start();
        SlideshowGlyph.Text = "■";
        SlideshowButton.ToolTip = "자동 넘김 중지 (Ctrl+Space)";
    }

    private void StopSlideshow()
    {
        _slideshowTimer.Stop();
        SlideshowGlyph.Text = "▶";
        SlideshowButton.ToolTip = "자동 넘김 (Ctrl+Space)";
    }

    /// <summary>
    /// Bitmap for export (copy, save, print): what the window shows, meaning the
    /// brightness/contrast/gamma adjustments plus EXIF orientation, rotation, tilt and flip.
    /// </summary>
    private BitmapSource? BuildOrientedBitmap()
    {
        var adjusted = _normalDisplayBitmap ?? _originalBitmap;
        if (adjusted is null)
        {
            return null;
        }

        if (!HasFreeRotation && EffectiveRotation == 0 && !EffectiveFlip)
        {
            return adjusted;
        }

        try
        {
            return RenderTransformed(adjusted, EffectiveAngle, EffectiveFlip);
        }
        catch
        {
            return adjusted;
        }
    }

    private void OnCopyImageClick(object sender, RoutedEventArgs e)
    {
        if (BuildOrientedBitmap() is not { } bitmap)
        {
            return;
        }

        try
        {
            Clipboard.SetImage(bitmap);
        }
        catch (Exception ex)
        {
            StyledDialogWindow.ShowInfo(this, "복사 실패", ex.Message);
        }
    }

    private void OnSaveImageClick(object sender, RoutedEventArgs e)
    {
        var path = CurrentImagePath;
        if (string.IsNullOrWhiteSpace(path) || BuildOrientedBitmap() is not { } bitmap)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "이미지 저장",
            FileName = Path.GetFileNameWithoutExtension(path) + "_copy",
            Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|BMP (*.bmp)|*.bmp|모든 파일 (*.*)|*.*"
        };

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            dialog.InitialDirectory = directory;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            SaveBitmap(bitmap, dialog.FileName, _jpegQuality);
        }
        catch (Exception ex)
        {
            StyledDialogWindow.ShowInfo(this, "저장 실패", ex.Message);
        }
    }

    private static BitmapEncoder CreateEncoder(string extension, int jpegQuality) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 10, 100) },
        ".bmp" => new BmpBitmapEncoder(),
        ".tif" or ".tiff" => new TiffBitmapEncoder(),
        ".gif" => new GifBitmapEncoder(),
        _ => new PngBitmapEncoder()
    };

    private static void SaveBitmap(BitmapSource bitmap, string path, int jpegQuality)
    {
        var encoder = CreateEncoder(Path.GetExtension(path), jpegQuality);
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Resamples to the requested pixel size with high-quality scaling.</summary>
    private static BitmapSource ScaleBitmap(BitmapSource source, int width, int height)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, width, height));
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>Bakes rotation/flip into pixels, growing the canvas to fit the rotated image.</summary>
    private static BitmapSource RenderTransformed(BitmapSource source, double angle, bool flip)
    {
        var width = (double)source.PixelWidth;
        var height = (double)source.PixelHeight;
        var radians = angle * Math.PI / 180.0;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));
        var outputWidth = Math.Max(1, (int)Math.Round((width * cos) + (height * sin)));
        var outputHeight = Math.Max(1, (int)Math.Round((width * sin) + (height * cos)));

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new TranslateTransform(outputWidth / 2.0, outputHeight / 2.0));
            context.PushTransform(new ScaleTransform(flip ? -1 : 1, 1));
            context.PushTransform(new RotateTransform(angle));
            context.PushTransform(new TranslateTransform(-width / 2.0, -height / 2.0));
            context.DrawImage(source, new Rect(0, 0, width, height));
            context.Pop();
            context.Pop();
            context.Pop();
            context.Pop();
        }

        var target = new RenderTargetBitmap(outputWidth, outputHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>Draws a caption in one corner of the image.</summary>
    private static BitmapSource RenderWithStamp(
        BitmapSource source,
        string text,
        ImageStampDialog.StampCorner corner,
        double fontSize,
        bool background)
    {
        var width = (double)source.PixelWidth;
        var height = (double)source.PixelHeight;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, width, height));

            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Malgun Gothic"),
                fontSize,
                Brushes.White,
                1.0);

            var margin = Math.Max(8.0, fontSize * 0.6);
            var x = corner is ImageStampDialog.StampCorner.TopRight or ImageStampDialog.StampCorner.BottomRight
                ? Math.Max(margin, width - formatted.Width - margin)
                : margin;
            var y = corner is ImageStampDialog.StampCorner.BottomLeft or ImageStampDialog.StampCorner.BottomRight
                ? Math.Max(margin, height - formatted.Height - margin)
                : margin;

            if (background)
            {
                var padding = Math.Max(4.0, fontSize * 0.3);
                context.DrawRectangle(
                    new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                    null,
                    new Rect(x - padding, y - padding, formatted.Width + (padding * 2), formatted.Height + (padding * 2)));
            }

            context.DrawText(formatted, new Point(x, y));
        }

        var target = new RenderTargetBitmap(
            Math.Max(1, source.PixelWidth),
            Math.Max(1, source.PixelHeight),
            96,
            96,
            PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static readonly HashSet<string> OverwritableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"
    };

    private void OnOverwriteImageClick(object sender, RoutedEventArgs e)
    {
        var path = CurrentImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || BuildOrientedBitmap() is not { } bitmap)
        {
            return;
        }

        var extension = Path.GetExtension(path);
        if (!OverwritableExtensions.Contains(extension))
        {
            StyledDialogWindow.ShowInfo(
                this,
                "덮어쓰기 저장",
                $"{extension} 형식은 원본에 덮어쓸 수 없습니다. '다른 이름 저장'을 이용하세요.");
            return;
        }

        if (!StyledDialogWindow.ShowConfirm(
                this,
                "덮어쓰기 저장",
                $"'{Path.GetFileName(path)}' 파일을 현재 상태(회전·보정 포함)로 덮어쓰시겠습니까?"))
        {
            return;
        }

        try
        {
            SaveBitmap(bitmap, path, _jpegQuality);
            _bitmapCache.Remove(path);
            ShowCurrentImage();
        }
        catch (Exception ex)
        {
            StyledDialogWindow.ShowInfo(this, "저장 실패", ex.Message);
        }
    }

    private void OnDeleteImageMenuClick(object sender, RoutedEventArgs e) => DeleteCurrentImage();

    private string SuggestExportName(string suffix) =>
        (Path.GetFileNameWithoutExtension(CurrentImagePath) ?? "image") + suffix;

    private Microsoft.Win32.SaveFileDialog CreateExportDialog(string title, string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            FileName = suggestedName,
            Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|BMP (*.bmp)|*.bmp|TIFF (*.tif)|*.tif|모든 파일 (*.*)|*.*"
        };

        var directory = Path.GetDirectoryName(CurrentImagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            dialog.InitialDirectory = directory;
        }

        return dialog;
    }

    private void OnResizeSaveClick(object sender, RoutedEventArgs e)
    {
        if (BuildOrientedBitmap() is not { } bitmap)
        {
            return;
        }

        var request = ImageResizeDialog.Show(this, bitmap.PixelWidth, bitmap.PixelHeight, _jpegQuality);
        if (request is null)
        {
            return;
        }

        _jpegQuality = request.JpegQuality;
        SaveViewerScalePreference();

        var dialog = CreateExportDialog("크기 조정 저장", SuggestExportName($"_{request.Width}x{request.Height}"));
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var scaled = ScaleBitmap(bitmap, request.Width, request.Height);
            SaveBitmap(scaled, dialog.FileName, _jpegQuality);
            StyledDialogWindow.ShowInfo(
                this,
                "크기 조정 저장",
                $"{request.Width:N0} x {request.Height:N0} 픽셀로 저장했습니다.\n{dialog.FileName}");
        }
        catch (Exception ex)
        {
            StyledDialogWindow.ShowInfo(this, "저장 실패", ex.Message);
        }
    }

    private void OnStampSaveClick(object sender, RoutedEventArgs e)
    {
        if (BuildOrientedBitmap() is not { } bitmap)
        {
            return;
        }

        var fileName = Path.GetFileName(CurrentImagePath ?? string.Empty);
        var modified = DateTime.Now;
        try
        {
            var info = new FileInfo(CurrentImagePath ?? string.Empty);
            if (info.Exists)
            {
                modified = info.LastWriteTime;
            }
        }
        catch
        {
        }

        var baseFontSize = Math.Clamp(Math.Min(bitmap.PixelWidth, bitmap.PixelHeight) * 0.045, 12.0, 96.0);
        var request = ImageStampDialog.Show(this, fileName, modified, baseFontSize);
        if (request is null)
        {
            return;
        }

        var dialog = CreateExportDialog("텍스트 도장 저장", SuggestExportName("_stamp"));
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var stamped = RenderWithStamp(bitmap, request.Text, request.Corner, request.FontSize, request.Background);
            SaveBitmap(stamped, dialog.FileName, _jpegQuality);
            StyledDialogWindow.ShowInfo(this, "텍스트 도장", $"도장을 넣어 저장했습니다.\n{dialog.FileName}");
        }
        catch (Exception ex)
        {
            StyledDialogWindow.ShowInfo(this, "저장 실패", ex.Message);
        }
    }

    private void OnBatchConvertClick(object sender, RoutedEventArgs e)
    {
        if (_imageFiles.Count == 0 ||
            (Math.Abs(_freeRotation) < 0.01 && _rotation == 0 && !_flipHorizontal))
        {
            StyledDialogWindow.ShowInfo(this, "일괄 변환", "먼저 회전·반전·기울기를 적용한 뒤 실행하세요.");
            return;
        }

        var folder = Path.GetDirectoryName(CurrentImagePath);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var targetFolder = folder.TrimEnd(Path.DirectorySeparatorChar) + "_converted";
        var angle = _rotation + _freeRotation;
        var flip = _flipHorizontal;
        var description = $"회전 {angle:0.#}도{(flip ? " + 좌우반전" : string.Empty)}";

        if (!StyledDialogWindow.ShowConfirm(
                this,
                "일괄 변환",
                $"현재 폴더의 이미지 {_imageFiles.Count}개에 {description}을(를) 적용해\n" +
                $"'{targetFolder}' 폴더에 저장하시겠습니까?\n\n원본 파일은 변경되지 않습니다."))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(targetFolder);
        }
        catch (Exception ex)
        {
            StyledDialogWindow.ShowInfo(this, "일괄 변환", $"대상 폴더를 만들 수 없습니다.\n{ex.Message}");
            return;
        }

        var files = _imageFiles.ToArray();
        var quality = _jpegQuality;
        var progress = TransferProgressWindow.Start("일괄 변환", files.Length);
        progress.NotifyWorkStarted();

        _ = Task.Run(() =>
        {
            var failures = 0;
            var completed = 0;
            var token = progress.Token;
            var cancelled = false;

            foreach (var file in files)
            {
                if (token.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                try
                {
                    var loaded = LoadImage(file);
                    var source = loaded.Pixels;

                    var extension = Path.GetExtension(file).ToLowerInvariant();
                    if (extension is ".webp" or ".gif")
                    {
                        extension = ".png";
                    }

                    var target = Path.Combine(targetFolder, Path.GetFileNameWithoutExtension(file) + extension);

                    // Rendering a transformed bitmap needs the UI thread; loading and
                    // encoding are safe off it.
                    Dispatcher.Invoke(() => SaveBitmap(
                        RenderTransformed(source, angle + loaded.ExifRotation, flip ^ loaded.ExifFlip),
                        target,
                        quality));
                }
                catch
                {
                    failures++;
                }

                completed++;
                progress.ReportItem(completed, file);
            }

            Dispatcher.BeginInvoke(() =>
            {
                progress.Dispose();
                if (!IsLoaded)
                {
                    return;
                }

                try
                {
                    var summary =
                        $"{completed - failures}개 변환 완료" +
                        (failures > 0 ? $", {failures}개 실패" : string.Empty) +
                        (cancelled ? " (사용자가 중지)" : string.Empty) +
                        $"\n\n저장 위치: {targetFolder}";

                    if (StyledDialogWindow.ShowConfirm(this, "일괄 변환", summary + "\n\n저장 폴더를 여시겠습니까?"))
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{targetFolder}\"") { UseShellExecute = true });
                    }
                }
                catch
                {
                }
            });
        });
    }

    private void UncheckCompareSilently()
    {
        _isComparing = false;
        if (CheckComparePrevious?.IsChecked != true)
        {
            return;
        }

        _suppressCompare = true;
        try
        {
            CheckComparePrevious.IsChecked = false;
        }
        finally
        {
            _suppressCompare = false;
        }
    }

    private void OnCompareOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressCompare || CheckComparePrevious is null)
        {
            return;
        }

        if (CheckComparePrevious.IsChecked == true && _imageFiles.Count > 1)
        {
            var previousIndex = (_currentIndex - 1 + _imageFiles.Count) % _imageFiles.Count;
            try
            {
                _isComparing = true;
                SetViewerImageSource(GetOrLoadImage(_imageFiles[previousIndex]).Pixels);
                ApplyTransform();
                if (ThumbCountText is not null)
                {
                    ThumbCountText.Text = $"비교: {previousIndex + 1} / {_imageFiles.Count}";
                }

                return;
            }
            catch (Exception ex)
            {
                StyledDialogWindow.ShowInfo(this, "비교 보기 실패", ex.Message);
            }
        }

        _isComparing = false;
        if (_normalDisplayBitmap is not null)
        {
            SetViewerImageSource(_normalDisplayBitmap);
            ApplyTransform();
        }
    }

    private void OnPrintClick(object sender, RoutedEventArgs e)
    {
        if (ViewerImage.Source is not BitmapSource source || BuildOrientedBitmap() is not { } bitmap)
        {
            return;
        }

        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var pageWidth = dialog.PrintableAreaWidth;
            var pageHeight = dialog.PrintableAreaHeight;
            var scale = Math.Min(pageWidth / Math.Max(1, bitmap.PixelWidth), pageHeight / Math.Max(1, bitmap.PixelHeight));
            var width = bitmap.PixelWidth * scale;
            var height = bitmap.PixelHeight * scale;

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawImage(
                    bitmap,
                    new Rect((pageWidth - width) / 2, (pageHeight - height) / 2, width, height));
            }

            dialog.PrintVisual(visual, Path.GetFileName(CurrentImagePath) ?? "이미지");
        }
        catch (Exception ex)
        {
            StyledDialogWindow.ShowInfo(this, "인쇄 실패", ex.Message);
        }
    }

    private void ApplyImageOptions()
    {
        _nearestNeighbor = CheckNearestNeighbor.IsChecked == true;
        _checkerBackground = CheckCheckerBackground.IsChecked == true;
        _wheelZooms = CheckWheelZoom.IsChecked == true;

        RenderOptions.SetBitmapScalingMode(
            ViewerImage,
            _nearestNeighbor ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        CheckerBackground.Visibility = _checkerBackground ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnImageOptionChanged(object sender, RoutedEventArgs e)
    {
        ApplyImageOptions();
        SaveViewerScalePreference();
    }

    private void OnShowInfoClick(object sender, RoutedEventArgs e)
    {
        var path = CurrentImagePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var lines = new List<string> { Path.GetFileName(path), path };

        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                lines.Add($"파일 크기: {ToReadableSize(info.Length)} ({info.Length:N0} 바이트)");
                lines.Add($"수정한 날짜: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                lines.Add($"만든 날짜: {info.CreationTime:yyyy-MM-dd HH:mm:ss}");
            }
        }
        catch
        {
        }

        if (ViewerImage.Source is BitmapSource source)
        {
            lines.Add($"픽셀: {source.PixelWidth} x {source.PixelHeight}");
            lines.Add($"해상도(DPI): {source.DpiX:0.#} x {source.DpiY:0.#}");
            lines.Add($"픽셀 형식: {source.Format}");
            if (_rotation != 0 || EffectiveFlip || HasFreeRotation)
            {
                lines.Add($"회전: {EffectiveAngle:0.#}도{(_flipHorizontal ? " + 좌우반전" : string.Empty)}");
            }

            if (_exifRotation != 0 || _exifFlip)
            {
                lines.Add($"EXIF 방향 보정: {_exifRotation}도{(_exifFlip ? " + 좌우반전" : string.Empty)}");
            }
        }

        if (_currentExifDescription is { Length: > 0 } exif)
        {
            lines.Add($"EXIF: {exif}");
        }

        StyledDialogWindow.ShowInfo(this, "이미지 정보", string.Join(Environment.NewLine, lines));
    }

    private void OnOpenWithDefaultAppClick(object sender, RoutedEventArgs e)
    {
        var path = CurrentImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StyledDialogWindow.ShowInfo(this, "열기 실패", ex.Message);
        }
    }

    private void OnRenameImageClick(object sender, RoutedEventArgs e)
    {
        var path = CurrentImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var currentName = Path.GetFileName(path);
        var newName = NewFolderDialog.ShowDialog(this, currentName, "이미지 이름 바꾸기", "새 파일 이름을 입력하세요.");
        if (string.IsNullOrWhiteSpace(newName) ||
            string.Equals(newName, currentName, StringComparison.Ordinal))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var target = Path.Combine(directory, newName);
        try
        {
            File.Move(path, target);
        }
        catch (Exception ex)
        {
            StyledDialogWindow.ShowInfo(this, "이름 바꾸기 실패", ex.Message);
            return;
        }

        // Keep the thumbnail strip and the image list pointing at the renamed file.
        var item = _thumbnailItems.FirstOrDefault(candidate =>
            string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            _thumbnailByPath.Remove(path);
            item.Path = target;
            item.Name = Path.GetFileName(target);
            _thumbnailByPath[target] = item;
        }

        if (_bitmapCache.Remove(path, out var cachedBitmap))
        {
            _bitmapCache[target] = cachedBitmap;
        }

        _imageFiles[_currentIndex] = target;
        BuildFolderList();
        ShowCurrentImage();
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        var path = files.FirstOrDefault(candidate => SupportedExtensions.Contains(Path.GetExtension(candidate)));
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            ShowImage(path);
        }
        catch (Exception ex)
        {
            StyledDialogWindow.ShowInfo(this, "이미지 열기 실패", ex.Message);
        }
    }

    private void OnShowHelpClick(object sender, RoutedEventArgs e)
    {
        var lines = new[]
        {
            "← / → · 마우스 휠: 이전 · 다음 이미지",
            "PageUp / PageDown: 이전 · 다음, Home / End: 처음 · 마지막",
            "Ctrl + 마우스 휠: 커서 위치 기준 확대 · 축소",
            "Ctrl + (+ / -): 확대 · 축소, Ctrl+0: 맞춤, Ctrl+1: 원본",
            "좌클릭 드래그: 이미지 이동(팬)",
            "더블클릭 · F11: 전체화면, Esc: 전체화면 해제 / 닫기",
            "L · R: 좌 · 우회전, H: 좌우 반전",
            "Ctrl + Space: 자동 넘김 시작 · 중지 (기본은 꺼짐)",
            "F2: 이름 바꾸기, Delete: 휴지통으로 이동",
            "I: 이미지 정보, F1: 이 도움말",
            "Ctrl+C: 클립보드에 복사, Ctrl+S: 다른 이름으로 저장",
            "Ctrl+P: 인쇄",
            "저장/도구: 크기 조정 저장 · 텍스트 도장 · 일괄 변환(_converted 폴더)",
            "이미지 우클릭: 복사 · 저장 · 덮어쓰기 · 이름 바꾸기 · 삭제 · 인쇄 메뉴",
            "좌측 파일목록: 오른쪽 모서리 손잡이(❮ / ❯)로 접기 · 펼치기",
            "배율: 창에 맞춤(Ctrl+0) · 원본 크기(Ctrl+1)"
        };

        StyledDialogWindow.ShowInfo(this, "이미지 뷰어 단축키", string.Join(Environment.NewLine, lines));
    }

    private void ResetAdjustmentSliders()
    {
        _suppressAdjustment = true;
        try
        {
            SliderBrightness.Value = 0;
            SliderContrast.Value = 0;
            SliderGamma.Value = 100;
        }
        finally
        {
            _suppressAdjustment = false;
        }

        UpdateAdjustInfo();
    }

    private void UpdateAdjustInfo()
    {
        // Sliders raise ValueChanged while the XAML tree is still being built, before the
        // controls declared below them exist; there is nothing to refresh at that point.
        if (AdjustInfoText is null ||
            SliderBrightness is null ||
            SliderContrast is null ||
            SliderGamma is null ||
            BrightnessValueText is null ||
            ContrastValueText is null ||
            GammaValueText is null ||
            FreeRotationValueText is null)
        {
            return;
        }

        var brightness = SliderBrightness.Value;
        var contrast = SliderContrast.Value;
        var gamma = SliderGamma.Value;

        BrightnessValueText.Text = $"{brightness:+0;-0;0}";
        ContrastValueText.Text = $"{contrast:+0;-0;0}";
        GammaValueText.Text = $"{gamma:0}%";
        FreeRotationValueText.Text = $"{SliderFreeRotation?.Value ?? 0:0}°";
        AdjustInfoText.Text =
            Math.Abs(brightness) < 0.5 && Math.Abs(contrast) < 0.5 && Math.Abs(gamma - 100) < 0.5
                ? string.Empty
                : $"밝기 {brightness:+0;-0;0} · 대비 {contrast:+0;-0;0} · 감마 {gamma:0}%";
    }

    private void OnAdjustmentChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AdjustInfoText is null)
        {
            // Still loading the XAML tree; the constructor applies the stored values afterwards.
            return;
        }

        UpdateAdjustInfo();
        if (_suppressAdjustment || _isAnimating)
        {
            return;
        }

        _adjustTimer.Stop();
        _adjustTimer.Start();
    }

    private void OnResetAdjustmentsClick(object sender, RoutedEventArgs e)
    {
        ResetAdjustmentSliders();
        ResetFreeRotation();
        ApplyTransform();
        ApplyAdjustments();
    }

    private void OnThumbnailSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var boxWidth = Math.Round(e.NewValue);
        _thumbnailSize = boxWidth;
        ThumbnailBoxWidth = boxWidth;
        ThumbnailBoxHeight = Math.Round(boxWidth * 0.8);
        ThumbnailItemWidth = boxWidth + 8;
        UpdateThumbSizeLabel(boxWidth);

        if (!IsLoaded)
        {
            return;
        }

        _thumbSizeTimer.Stop();
        _thumbSizeTimer.Start();
    }

    private void OnThumbSizeSettled()
    {
        _thumbSizeTimer.Stop();
        if (_imageFiles.Count == 0)
        {
            return;
        }

        BuildThumbnailItems();
        if (CurrentImagePath is { Length: > 0 } currentPath)
        {
            SyncThumbnailSelection(currentPath);
        }

        SaveViewerScalePreference();
    }

    private void ApplyAdjustments()
    {
        if (_originalBitmap is null || _isAnimating)
        {
            return;
        }

        var brightness = SliderBrightness.Value;
        var contrast = SliderContrast.Value;
        var gamma = SliderGamma.Value / 100.0;
        var isNeutral = Math.Abs(brightness) < 0.5 && Math.Abs(contrast) < 0.5 && Math.Abs(gamma - 1.0) < 0.005;

        try
        {
            _normalDisplayBitmap = isNeutral
                ? _originalBitmap
                : ApplyLutToBitmap(_originalBitmap, BuildAdjustmentLut(brightness, contrast, gamma));
            if (!_isComparing)
            {
                SetViewerImageSource(_normalDisplayBitmap);
            }

            ApplyTransform();
            UpdateInfoText();
        }
        catch (Exception ex)
        {
            StyledDialogWindow.ShowInfo(this, "보정 실패", ex.Message);
        }
    }

    private static byte[] BuildAdjustmentLut(double brightness, double contrast, double gamma)
    {
        var lut = new byte[256];
        var contrastFactor = (100.0 + contrast) / 100.0;
        var brightnessOffset = brightness * 2.55 / 255.0;

        for (var i = 0; i < 256; i++)
        {
            var value = i / 255.0;
            value = Math.Pow(value, 1.0 / Math.Max(0.01, gamma));
            value = ((value - 0.5) * contrastFactor) + 0.5 + brightnessOffset;
            lut[i] = (byte)Math.Clamp((int)Math.Round(value * 255.0), 0, 255);
        }

        return lut;
    }

    private static BitmapSource ApplyLutToBitmap(BitmapSource source, byte[] lut)
    {
        var formatted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = formatted.PixelWidth;
        var height = formatted.PixelHeight;
        var stride = width * 4;
        var totalBytes = (long)stride * height;
        if (totalBytes > 400_000_000L)
        {
            throw new InvalidOperationException("이미지가 너무 커서 보정을 적용할 수 없습니다.");
        }

        var buffer = new byte[totalBytes];
        formatted.CopyPixels(buffer, stride, 0);

        Parallel.For(0, height, y =>
        {
            var offset = y * stride;
            for (var x = 0; x < stride; x += 4)
            {
                buffer[offset + x] = lut[buffer[offset + x]];
                buffer[offset + x + 1] = lut[buffer[offset + x + 1]];
                buffer[offset + x + 2] = lut[buffer[offset + x + 2]];
            }
        });

        var result = BitmapSource.Create(width, height, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, buffer, stride);
        result.Freeze();
        return result;
    }

    private void StartAnimationIfNeeded(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count <= 1)
            {
                return;
            }

            foreach (var frame in decoder.Frames)
            {
                _animationFrames.Add(frame);
                var delayMilliseconds = 100;
                try
                {
                    if (frame.Metadata is BitmapMetadata metadata &&
                        metadata.GetQuery("/grctlext/Delay") is ushort delay &&
                        delay > 0)
                    {
                        delayMilliseconds = delay * 10;
                    }
                }
                catch
                {
                    // Frame without delay metadata: fall back to a sane default.
                }

                _animationDelays.Add(Math.Clamp(delayMilliseconds, 30, 5000));
            }

            _isAnimating = true;
            _animationIndex = 0;
            ScheduleNextAnimationFrame();
        }
        catch
        {
            StopAnimation();
        }
    }

    private void ScheduleNextAnimationFrame()
    {
        if (!_isAnimating || _animationDelays.Count == 0)
        {
            return;
        }

        _animationTimer.Stop();
        _animationTimer.Interval = TimeSpan.FromMilliseconds(_animationDelays[_animationIndex]);
        _animationTimer.Start();
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        _animationTimer.Stop();
        if (!_isAnimating || _animationFrames.Count == 0)
        {
            return;
        }

        _animationIndex = (_animationIndex + 1) % _animationFrames.Count;
        SetViewerImageSource(_animationFrames[_animationIndex]);
        ApplyTransform();
        ScheduleNextAnimationFrame();
    }

    private void StopAnimation()
    {
        _animationTimer.Stop();
        _isAnimating = false;
        _animationFrames.Clear();
        _animationDelays.Clear();
        _animationIndex = 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

    private const uint FoDelete = 0x0003;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofSilent = 0x0004;

    private static bool TryMoveToRecycleBin(string path, out string? error)
    {
        error = null;
        try
        {
            var operation = new SHFILEOPSTRUCT
            {
                wFunc = FoDelete,
                pFrom = path + "\0",
                fFlags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent
            };

            var result = SHFileOperation(ref operation);
            if (result != 0)
            {
                error = $"휴지통으로 이동하지 못했습니다. (코드 {result})";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var path = CurrentImagePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private void OnImagePreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 || _wheelZooms)
        {
            // Anchor the zoom on the pointer so the spot under the cursor stays put.
            ZoomAtCursor(_zoom + (e.Delta > 0 ? ZoomStep : -ZoomStep), e.GetPosition(ImageScrollViewer));
            SaveViewerScalePreference();
            e.Handled = true;
            return;
        }

        // Plain wheel navigates between images: up = previous, down = next.
        NavigateRelative(e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_isFullScreen)
            {
                ToggleFullScreen();
            }
            else
            {
                Close();
            }
            return;
        }

        if (e.Key == Key.Delete)
        {
            DeleteCurrentImage();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.C)
        {
            OnCopyImageClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.S)
        {
            OnSaveImageClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Ctrl+Space only: a bare Space used to start the slideshow, which turned on
        // auto-advance by accident (Space was the old pan modifier) and left images
        // flipping one after another.
        if (e.Key == Key.Space && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ToggleSlideshow();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Home)
        {
            NavigateToIndex(0);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.End)
        {
            NavigateToIndex(_imageFiles.Count - 1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.PageUp)
        {
            NavigateRelative(-1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.PageDown)
        {
            NavigateRelative(1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left)
        {
            NavigateRelative(-1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right)
        {
            NavigateRelative(1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.R)
        {
            OnRotateRightClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.L)
        {
            OnRotateLeftClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.H)
        {
            OnFlipClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.I)
        {
            OnShowInfoClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F1)
        {
            OnShowHelpClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.P)
        {
            OnPrintClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F2)
        {
            OnRenameImageClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && (e.Key == Key.Add || e.Key == Key.OemPlus))
        {
            SetZoom(_zoom + ZoomStep);
            SaveViewerScalePreference();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && (e.Key == Key.Subtract || e.Key == Key.OemMinus))
        {
            SetZoom(_zoom - ZoomStep);
            SaveViewerScalePreference();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.D0)
        {
            FitToViewport();
            SaveViewerScalePreference();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.D1)
        {
            _fitMode = false;
            SetZoom(1.0, keepMode: true);
            SaveViewerScalePreference();
            e.Handled = true;
        }
    }

    private void DeleteCurrentImage()
    {
        var path = CurrentImagePath;
        if (string.IsNullOrWhiteSpace(path) || _currentIndex < 0 || _currentIndex >= _imageFiles.Count)
        {
            return;
        }

        var fileName = Path.GetFileName(path);
        if (!StyledDialogWindow.ShowConfirm(this, "삭제 확인", $"현재 이미지 '{fileName}'을(를) 휴지통으로 이동하시겠습니까?"))
        {
            return;
        }

        try
        {
            StopAnimation();
            SetViewerImageSource(null);
            if (File.Exists(path) && !TryMoveToRecycleBin(path, out var recycleError))
            {
                StyledDialogWindow.ShowInfo(this, "삭제 실패", recycleError ?? "휴지통으로 이동하지 못했습니다.");
                ShowCurrentImage();
                return;
            }
        }
        catch (Exception ex)
        {
            StyledDialogWindow.ShowInfo(this, "삭제 실패", ex.Message);
            ShowCurrentImage();
            return;
        }

        var removedIndex = _currentIndex;
        _imageFiles.RemoveAt(removedIndex);
        if (_thumbnailByPath.TryGetValue(path, out var thumbnail))
        {
            _thumbnailByPath.Remove(path);
            _thumbnailItems.Remove(thumbnail);
            ThumbnailList.Items.Refresh();
        }

        if (_imageFiles.Count == 0)
        {
            Close();
            return;
        }

        _currentIndex = Math.Clamp(removedIndex, 0, _imageFiles.Count - 1);
        BuildFolderList();
        ShowCurrentImage();
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitMode)
        {
            // The scroll viewer's viewport only reports the new size after the layout pass, and
            // maximizing raises a single SizeChanged - fitting right here would keep the old scale.
            QueueFitToViewport();
        }

        QueueScrollBarVisibilityUpdate();
    }

    private void QueueFitToViewport()
    {
        if (_fitUpdateQueued)
        {
            return;
        }

        _fitUpdateQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _fitUpdateQueued = false;
            if (_fitMode)
            {
                FitToViewport();
            }
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    // The content inside the ScrollViewer swallows plain mouse-down events, so panning is
    // driven from the preview stage. Clicks that belong to the floating navigation buttons
    // are left alone so those keep working.
    private void OnImageAreaPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideOverlayButton(e.OriginalSource))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            StopPanning();
            ToggleFullScreen();
            e.Handled = true;
            return;
        }

        // Drag to pan whenever the image overflows the viewport - the grab-and-move feel of a
        // hand tool.
        if (e.LeftButton == MouseButtonState.Pressed && CanPanImage)
        {
            StartPanning(e.GetPosition(ImageArea));
            e.Handled = true;
        }
    }

    private void OnImageAreaPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // The middle button pans as well, which is what most viewers do.
        if (e.ChangedButton == MouseButton.Middle && !IsInsideOverlayButton(e.OriginalSource) && CanPanImage)
        {
            StartPanning(e.GetPosition(ImageArea));
            e.Handled = true;
        }
    }

    private static bool IsInsideOverlayButton(object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Button)
            {
                return true;
            }

            if (node is Border { Name: "ImageArea" })
            {
                return false;
            }
        }

        return false;
    }

    private void OnImageAreaMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        var point = e.GetPosition(ImageArea);
        ImageScrollViewer.ScrollToHorizontalOffset(_panStartOffsetX - (point.X - _panOrigin.X));
        ImageScrollViewer.ScrollToVerticalOffset(_panStartOffsetY - (point.Y - _panOrigin.Y));
        e.Handled = true;
    }

    private void OnImageAreaPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        StopPanning();
        e.Handled = true;
    }

    private void OnThumbnailSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressThumbnailSelectionChanged || ThumbnailList.SelectedItem is not ThumbnailItem selected)
        {
            return;
        }

        var newIndex = _imageFiles.FindIndex(path => string.Equals(path, selected.Path, StringComparison.OrdinalIgnoreCase));
        if (newIndex < 0 || newIndex == _currentIndex)
        {
            return;
        }

        _currentIndex = newIndex;
        ShowCurrentImage();
    }

    private void OnThumbnailPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox)
        {
            return;
        }

        var scrollViewer = FindVisualChild<ScrollViewer>(ThumbnailList);
        if (scrollViewer is null)
        {
            return;
        }

        var nextOffset = scrollViewer.HorizontalOffset - e.Delta;
        scrollViewer.ScrollToHorizontalOffset(nextOffset);
        e.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T matched)
            {
                return matched;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private sealed class ThumbnailItem : INotifyPropertyChanged
    {
        private BitmapSource? _thumbnail;
        private string _name = string.Empty;
        private bool _isCurrent;

        public string Path { get; set; } = string.Empty;

        public bool IsCurrent
        {
            get => _isCurrent;
            set
            {
                if (_isCurrent == value)
                {
                    return;
                }

                _isCurrent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            }
        }

        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }

        public BitmapSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                _thumbnail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        // Screen readers and UI automation read the item name from ToString by default.
        public override string ToString() => Name;
    }

    /// <summary>One row of the left folder list: name only, images are selectable.</summary>
    private sealed class FolderListItem : INotifyPropertyChanged
    {
        private bool _isCurrent;

        public string Path { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;

        public bool IsCurrent
        {
            get => _isCurrent;
            set
            {
                if (_isCurrent == value)
                {
                    return;
                }

                _isCurrent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public override string ToString() => Name;
    }
}

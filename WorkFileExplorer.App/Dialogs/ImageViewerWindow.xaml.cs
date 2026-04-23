using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using WorkFileExplorer.App.Helpers;

namespace WorkFileExplorer.App.Dialogs;

public partial class ImageViewerWindow : Window
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"
    };

    private const double MinZoom = 0.05;
    private const double MaxZoom = 12.0;
    private const double ZoomStep = 0.1;

    private readonly ScaleTransform _scale = new(1.0, 1.0);
    private readonly RotateTransform _rotate = new(0.0);
    private readonly TransformGroup _transformGroup = new();

    private readonly List<string> _imageFiles = [];
    private readonly List<ThumbnailItem> _thumbnailItems = [];
    private readonly Dictionary<string, ThumbnailItem> _thumbnailByPath = new(StringComparer.OrdinalIgnoreCase);
    private int _currentIndex = -1;
    private double _zoom = 1.0;
    private bool _fitMode = true;
    private int _rotation;
    private bool _flipHorizontal;
    private bool _suppressThumbnailSelectionChanged;

    private WindowState _prevWindowState;
    private WindowStyle _prevWindowStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _prevResizeMode = ResizeMode.CanResize;
    private bool _isFullScreen;

    public ImageViewerWindow(string imagePath)
    {
        InitializeComponent();

        _transformGroup.Children.Add(_scale);
        _transformGroup.Children.Add(_rotate);
        ViewerImage.RenderTransform = _transformGroup;

        InitializeImageList(imagePath);
        BuildThumbnailItems();
        LoadViewerScalePreference();
        ShowCurrentImage();
    }

    public void ShowImage(string imagePath)
    {
        InitializeImageList(imagePath);
        BuildThumbnailItems();
        ShowCurrentImage();
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
        if (_currentIndex < 0 || _currentIndex >= _imageFiles.Count)
        {
            return;
        }

        var path = _imageFiles[_currentIndex];
        if (!File.Exists(path))
        {
            return;
        }

        ViewerImage.Source = LoadBitmap(path);
        _rotation = 0;
        _flipHorizontal = false;
        ApplyTransform();

        Title = $"Image Viewer - {Path.GetFileName(path)}";
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
    }

    private static BitmapSource LoadBitmap(string imagePath)
    {
        var bitmap = new BitmapImage();
        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource? LoadThumbnail(string imagePath)
    {
        try
        {
            var bitmap = new BitmapImage();
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelHeight = 72;
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
        _thumbnailItems.Clear();
        _thumbnailByPath.Clear();

        foreach (var path in _imageFiles)
        {
            var item = new ThumbnailItem
            {
                Path = path,
                Name = Path.GetFileName(path),
                Thumbnail = LoadThumbnail(path)
            };

            _thumbnailItems.Add(item);
            _thumbnailByPath[path] = item;
        }

        ThumbnailList.ItemsSource = _thumbnailItems;
    }

    private void SyncThumbnailSelection(string currentPath)
    {
        if (!_thumbnailByPath.TryGetValue(currentPath, out var item))
        {
            return;
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

    private void SetZoom(double zoom, bool keepMode = false)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _fitMode = keepMode ? _fitMode : false;
        ApplyTransform();
        UpdateInfoText();
    }

    private void ApplyTransform()
    {
        _scale.ScaleX = (_flipHorizontal ? -1.0 : 1.0) * _zoom;
        _scale.ScaleY = _zoom;
        _rotate.Angle = _rotation;
    }

    private void FitToViewport()
    {
        if (ViewerImage.Source is not BitmapSource source || ImageScrollViewer.ViewportWidth <= 0 || ImageScrollViewer.ViewportHeight <= 0)
        {
            return;
        }

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        if ((_rotation % 180) != 0)
        {
            (width, height) = (height, width);
        }

        var scaleX = ImageScrollViewer.ViewportWidth / Math.Max(1, width);
        var scaleY = ImageScrollViewer.ViewportHeight / Math.Max(1, height);
        _fitMode = true;
        SetZoom(Math.Min(scaleX, scaleY), keepMode: true);
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
                }
            }
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

    private void UpdateInfoText()
    {
        var sizeText = ViewerImage.Source is BitmapSource source
            ? $"{source.PixelWidth}x{source.PixelHeight}"
            : "-";
        var modeText = _fitMode ? " Fit" : string.Empty;
        ImageInfoText.Text = $"{_currentIndex + 1}/{Math.Max(_imageFiles.Count, 1)} | {sizeText} | {_zoom * 100:0}%{modeText}";
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
            return;
        }

        WindowStyle = _prevWindowStyle;
        ResizeMode = _prevResizeMode;
        WindowState = _prevWindowState;
        _isFullScreen = false;
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
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        SetZoom(_zoom + (e.Delta > 0 ? ZoomStep : -ZoomStep));
        SaveViewerScalePreference();
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

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitMode)
        {
            FitToViewport();
        }
    }

    private void OnImageAreaMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
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

    private sealed class ThumbnailItem
    {
        public string Path { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public BitmapSource? Thumbnail { get; init; }
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace WorkFileExplorer.App.Dialogs;

/// <summary>
/// Asks for a target pixel size before saving a resized copy of the image.
/// </summary>
public partial class ImageResizeDialog : Window
{
    public sealed record ResizeRequest(int Width, int Height, int JpegQuality);

    private const int MaxDimension = 20000;

    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private bool _suppressSync;

    public ImageResizeDialog(int sourceWidth, int sourceHeight, int jpegQuality)
    {
        InitializeComponent();

        _sourceWidth = Math.Max(1, sourceWidth);
        _sourceHeight = Math.Max(1, sourceHeight);

        _suppressSync = true;
        try
        {
            TextWidth.Text = _sourceWidth.ToString(CultureInfo.InvariantCulture);
            TextHeight.Text = _sourceHeight.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _suppressSync = false;
        }

        SourceText.Text = $"원본: {_sourceWidth:N0} x {_sourceHeight:N0} 픽셀";
        SliderQuality.Value = Math.Clamp(jpegQuality, 40, 100);
        UpdateResultText();
    }

    public ResizeRequest? Result { get; private set; }

    public static ResizeRequest? Show(Window owner, int sourceWidth, int sourceHeight, int jpegQuality)
    {
        var dialog = new ImageResizeDialog(sourceWidth, sourceHeight, jpegQuality) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private void OnWidthTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSync)
        {
            return;
        }

        if (CheckKeepRatio.IsChecked == true && TryParse(TextWidth.Text, out var width))
        {
            _suppressSync = true;
            try
            {
                TextHeight.Text = Math.Max(1, (int)Math.Round(width * (double)_sourceHeight / _sourceWidth))
                    .ToString(CultureInfo.InvariantCulture);
            }
            finally
            {
                _suppressSync = false;
            }
        }

        UpdateResultText();
    }

    private void OnHeightTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSync)
        {
            return;
        }

        if (CheckKeepRatio.IsChecked == true && TryParse(TextHeight.Text, out var height))
        {
            _suppressSync = true;
            try
            {
                TextWidth.Text = Math.Max(1, (int)Math.Round(height * (double)_sourceWidth / _sourceHeight))
                    .ToString(CultureInfo.InvariantCulture);
            }
            finally
            {
                _suppressSync = false;
            }
        }

        UpdateResultText();
    }

    private void OnKeepRatioChanged(object sender, RoutedEventArgs e) => UpdateResultText();

    private void OnQualityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityText is not null)
        {
            QualityText.Text = $"{e.NewValue:0}";
        }
    }

    private void OnPreset25Click(object sender, RoutedEventArgs e) => ApplyPercent(25);

    private void OnPreset50Click(object sender, RoutedEventArgs e) => ApplyPercent(50);

    private void OnPreset75Click(object sender, RoutedEventArgs e) => ApplyPercent(75);

    private void OnPreset100Click(object sender, RoutedEventArgs e) => ApplyPercent(100);

    private void ApplyPercent(int percent)
    {
        _suppressSync = true;
        try
        {
            TextWidth.Text = Math.Max(1, (int)Math.Round(_sourceWidth * percent / 100.0)).ToString(CultureInfo.InvariantCulture);
            TextHeight.Text = Math.Max(1, (int)Math.Round(_sourceHeight * percent / 100.0)).ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _suppressSync = false;
        }

        UpdateResultText();
    }

    private void UpdateResultText()
    {
        if (!TryParse(TextWidth.Text, out var width) || !TryParse(TextHeight.Text, out var height))
        {
            ResultText.Text = "가로·세로 크기를 숫자로 입력하세요.";
            ResultHintText.Text = string.Empty;
            return;
        }

        var percent = _sourceWidth > 0 ? width / (double)_sourceWidth * 100.0 : 100.0;
        var megapixels = width * (double)height / 1_000_000.0;
        ResultText.Text = $"결과: {width:N0} x {height:N0} 픽셀  ({percent:0.#}%)";
        ResultHintText.Text = megapixels >= 0.1
            ? $"{megapixels:0.##} 메가픽셀"
            : string.Empty;
    }

    private static bool TryParse(string? text, out int value) =>
        int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0;

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (!TryParse(TextWidth.Text, out var width) || !TryParse(TextHeight.Text, out var height))
        {
            StyledDialogWindow.ShowInfo(this, "크기 확인", "가로·세로 크기를 1 이상의 숫자로 입력하세요.");
            return;
        }

        if (width > MaxDimension || height > MaxDimension)
        {
            StyledDialogWindow.ShowInfo(this, "크기 확인", $"한 변의 최대 크기는 {MaxDimension:N0} 픽셀입니다.");
            return;
        }

        Result = new ResizeRequest(width, height, (int)Math.Round(SliderQuality.Value));
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

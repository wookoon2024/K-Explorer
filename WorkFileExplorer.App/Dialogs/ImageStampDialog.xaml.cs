using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace WorkFileExplorer.App.Dialogs;

/// <summary>
/// Collects the text, corner and size for burning a caption onto the image.
/// </summary>
public partial class ImageStampDialog : Window
{
    public enum StampCorner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    public sealed record StampRequest(string Text, StampCorner Corner, double FontSize, bool Background);

    private sealed record CornerOption(StampCorner Corner, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record SizeOption(double Scale, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly double _baseFontSize;
    private readonly string _fileName;
    private readonly DateTime _modified;
    private bool _initialized;

    public ImageStampDialog(string fileName, DateTime modified, double baseFontSize)
    {
        InitializeComponent();

        _fileName = fileName;
        _modified = modified;
        _baseFontSize = baseFontSize;

        ComboPosition.ItemsSource = new List<CornerOption>
        {
            new(StampCorner.BottomRight, "오른쪽 아래"),
            new(StampCorner.BottomLeft, "왼쪽 아래"),
            new(StampCorner.TopRight, "오른쪽 위"),
            new(StampCorner.TopLeft, "왼쪽 위")
        };

        ComboSize.ItemsSource = new List<SizeOption>
        {
            new(1.0, "보통"),
            new(0.65, "작게"),
            new(1.6, "크게")
        };

        ComboPosition.SelectedIndex = 0;
        ComboSize.SelectedIndex = 0;
        TextStamp.Text = Path.GetFileNameWithoutExtension(fileName);
        _initialized = true;

        UpdatePreview();
        Loaded += (_, _) => Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            () =>
            {
                TextStamp.Focus();
                TextStamp.SelectAll();
            });
    }

    public StampRequest? Result { get; private set; }

    public static StampRequest? Show(Window owner, string fileName, DateTime modified, double baseFontSize)
    {
        var dialog = new ImageStampDialog(fileName, modified, baseFontSize) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private void OnPresetFileNameClick(object sender, RoutedEventArgs e)
    {
        TextStamp.Text = Path.GetFileNameWithoutExtension(_fileName);
        UpdatePreview();
    }

    private void OnPresetDateClick(object sender, RoutedEventArgs e)
    {
        TextStamp.Text = _modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        UpdatePreview();
    }

    private void OnPresetFileNameDateClick(object sender, RoutedEventArgs e)
    {
        TextStamp.Text = $"{Path.GetFileNameWithoutExtension(_fileName)}  {_modified:yyyy-MM-dd}";
        UpdatePreview();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void OnStampTextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void OnBackgroundChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (!_initialized)
        {
            return;
        }

        var size = ((ComboSize.SelectedItem as SizeOption)?.Scale ?? 1.0) * _baseFontSize;
        PreviewText.Text = string.IsNullOrWhiteSpace(TextStamp.Text)
            ? "문구를 입력하세요."
            : $"\"{TextStamp.Text}\"  ·  글자 높이 약 {size:0} px  ·  {PreviewTextBackground()}";
    }

    private string PreviewTextBackground() => CheckBackground.IsChecked == true ? "반투명 배경" : "배경 없음";

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var text = (TextStamp.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            StyledDialogWindow.ShowInfo(this, "텍스트 도장", "표시할 문구를 입력하세요.");
            return;
        }

        var corner = (ComboPosition.SelectedItem as CornerOption)?.Corner ?? StampCorner.BottomRight;
        var scale = (ComboSize.SelectedItem as SizeOption)?.Scale ?? 1.0;
        Result = new StampRequest(text, corner, Math.Max(8, _baseFontSize * scale), CheckBackground.IsChecked == true);
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

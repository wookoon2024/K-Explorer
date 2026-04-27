using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WorkFileExplorer.App.Models;
using WorkFileExplorer.App.ViewModels;

namespace WorkFileExplorer.App.Dialogs;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<ExtensionColorRule> _extensionColorRules = [];
    private readonly Dictionary<string, Dictionary<string, string>> _extensionOverridesByTheme = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Black"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        ["White"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    };
    private readonly Dictionary<string, string> _themeColorOverrideMap = new(StringComparer.OrdinalIgnoreCase);
    private string _editingThemeMode = "Black";
    private bool _isInitializing;
    private static readonly string[] PresetHexColors =
    [
        "#66B3FF", "#4CAF50", "#FFC107", "#FF7043", "#E91E63", "#9C27B0",
        "#3F51B5", "#009688", "#8BC34A", "#FF9800", "#795548", "#607D8B",
        "#F44336", "#2196F3", "#00BCD4", "#CDDC39", "#9E9E9E", "#FFFFFF",
        "#C0C0C0", "#000000"
    ];
    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;
    private static readonly Dictionary<string, string> BlackThemeDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WindowBackground"] = "#D9D9D9",
        ["PanelBackground"] = "#000000",
        ["PanelForeground"] = "#E8E8E8",
        ["HeaderBackground"] = "#E5E5E5",
        ["HeaderForeground"] = "#202020",
        ["SelectionBackground"] = "#0078D7",
        ["BorderColor"] = "#7A7A7A"
    };
    private static readonly Dictionary<string, string> WhiteThemeDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WindowBackground"] = "#CBD7E6",
        ["PanelBackground"] = "#FFFFFF",
        ["PanelForeground"] = "#0B1220",
        ["HeaderBackground"] = "#B9CCE5",
        ["HeaderForeground"] = "#081527",
        ["SelectionBackground"] = "#1F6FBF",
        ["BorderColor"] = "#6C839F"
    };

    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isInitializing = true;
        if (Vm is null)
        {
            _isInitializing = false;
            return;
        }

        var snapshot = Vm.CaptureUiSettings();
        RadioTwoPanel.IsChecked = !snapshot.UseFourPanels;
        RadioFourPanel.IsChecked = snapshot.UseFourPanels;
        RadioLayout1x4.IsChecked = !snapshot.UseGridLayout;
        RadioLayout2x2.IsChecked = snapshot.UseGridLayout;
        CheckRememberSession.IsChecked = snapshot.RememberSessionTabs;

        RadioDetailsView.IsChecked = !snapshot.DefaultTileViewEnabled;
        RadioTileView.IsChecked = snapshot.DefaultTileViewEnabled;
        var fontFamilies = Fonts.SystemFontFamilies
            .Select(font => font.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(font => font, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        ComboFileListFontFamily.ItemsSource = fontFamilies;
        ComboFileListFontFamily.Text = snapshot.FileListFontFamily;
        TextFileListFontSize.Text = snapshot.FileListFontSize.ToString("0.##", CultureInfo.InvariantCulture);
        TextFileListRowHeight.Text = snapshot.FileListRowHeight.ToString("0.##", CultureInfo.InvariantCulture);
        LoadThemeColorOverrides(snapshot.ThemeColorOverrides);
        LoadExtensionOverrides(snapshot.ExtensionColorOverrides);
        ComboThemeMode.SelectedIndex = string.Equals(snapshot.ThemeMode, "White", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _editingThemeMode = ComboThemeMode.SelectedIndex == 1 ? "White" : "Black";
        LoadThemeColorEditors(_editingThemeMode);
        CheckUseExtensionColors.IsChecked = snapshot.UseExtensionColors;
        CheckUsePinnedHighlight.IsChecked = snapshot.UsePinnedHighlightColor;
        CheckConfirmDeleteFileSection.IsChecked = snapshot.ConfirmBeforeDelete;
        CheckSearchRecursive.IsChecked = snapshot.SearchRecursive;
        ExtensionColorRulesGrid.ItemsSource = _extensionColorRules;
        LoadExtensionColorRulesForTheme(_editingThemeMode);

        ComboConflictPolicy.ItemsSource = Vm.ConflictPolicyOptions;
        ComboConflictPolicy.SelectedItem = Vm.ConflictPolicyOptions.Contains(snapshot.ConflictPolicyDisplay)
            ? snapshot.ConflictPolicyDisplay
            : Vm.ConflictPolicyOptions.FirstOrDefault();
        ComboSearchScope.SelectedIndex = string.Equals(snapshot.SearchScope, "Both panels", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        ShowSection("general");
        _isInitializing = false;
    }

    private async void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            DialogResult = true;
            Close();
            return;
        }

        if (!await ApplyCurrentSettingsAsync())
        {
            return;
        }

        DialogResult = true;
        Close();
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        await ApplyCurrentSettingsAsync();
    }

    private async Task<bool> ApplyCurrentSettingsAsync()
    {
        if (Vm is null)
        {
            return false;
        }

        var snapshot = BuildCurrentSnapshot(Vm);
        await Vm.ApplyUiSettingsAsync(snapshot);
        return true;
    }

    private MainWindowViewModel.UiSettingsSnapshot BuildCurrentSnapshot(MainWindowViewModel vm)
    {
        return new MainWindowViewModel.UiSettingsSnapshot
        {
            UseFourPanels = RadioFourPanel.IsChecked == true,
            UseGridLayout = RadioLayout2x2.IsChecked == true,
            RememberSessionTabs = CheckRememberSession.IsChecked == true,
            DefaultTileViewEnabled = RadioTileView.IsChecked == true,
            UseExtensionColors = CheckUseExtensionColors.IsChecked == true,
            UsePinnedHighlightColor = CheckUsePinnedHighlight.IsChecked == true,
            ThemeMode = ComboThemeMode.SelectedIndex == 1 ? "White" : "Black",
            ConfirmBeforeDelete = CheckConfirmDeleteFileSection.IsChecked == true,
            ConflictPolicyDisplay = (ComboConflictPolicy.SelectedItem as string) ?? vm.SelectedConflictPolicyDisplay,
            SearchScope = ComboSearchScope.SelectedIndex == 1 ? "Both panels" : "Active panel",
            SearchRecursive = CheckSearchRecursive.IsChecked == true,
            ExtensionColorOverrides = BuildExtensionColorOverrides(),
            ThemeColorOverrides = BuildThemeColorOverrides(),
            FileListFontFamily = string.IsNullOrWhiteSpace(ComboFileListFontFamily.Text) ? vm.FileListFontFamily : ComboFileListFontFamily.Text.Trim(),
            FileListFontSize = ParseDoubleInRange(TextFileListFontSize.Text, vm.FileListFontSize, 9, 28),
            FileListRowHeight = ParseDoubleInRange(TextFileListRowHeight.Text, vm.FileListRowHeight, 16, 52)
        };
    }

    private void OnFontSizeIncreaseClick(object sender, RoutedEventArgs e)
    {
        AdjustNumericTextBox(TextFileListFontSize, step: 1, min: 9, max: 28);
    }

    private void OnFontSizeDecreaseClick(object sender, RoutedEventArgs e)
    {
        AdjustNumericTextBox(TextFileListFontSize, step: -1, min: 9, max: 28);
    }

    private void OnRowHeightIncreaseClick(object sender, RoutedEventArgs e)
    {
        AdjustNumericTextBox(TextFileListRowHeight, step: 1, min: 16, max: 52);
    }

    private void OnRowHeightDecreaseClick(object sender, RoutedEventArgs e)
    {
        AdjustNumericTextBox(TextFileListRowHeight, step: -1, min: 16, max: 52);
    }

    private void OnSettingsTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var selectedTreeItem = SettingsTreeView.SelectedItem as TreeViewItem;
        var sectionKey = selectedTreeItem?.Tag as string;
        ShowSection(sectionKey);
    }

    private void ShowSection(string? sectionKey)
    {
        if (SectionGeneralPanel is null ||
            SectionFilePanel is null ||
            SectionSearchPanel is null ||
            SectionColorPanel is null)
        {
            return;
        }

        SectionGeneralPanel.Visibility = Visibility.Collapsed;
        SectionFilePanel.Visibility = Visibility.Collapsed;
        SectionSearchPanel.Visibility = Visibility.Collapsed;
        SectionColorPanel.Visibility = Visibility.Collapsed;

        switch ((sectionKey ?? "general").ToLowerInvariant())
        {
            case "file":
                SectionFilePanel.Visibility = Visibility.Visible;
                break;
            case "search":
                SectionSearchPanel.Visibility = Visibility.Visible;
                break;
            case "color":
                SectionColorPanel.Visibility = Visibility.Visible;
                break;
            default:
                SectionGeneralPanel.Visibility = Visibility.Visible;
                break;
        }
    }

    private void LoadExtensionOverrides(IEnumerable<string>? overrides)
    {
        _extensionOverridesByTheme["Black"].Clear();
        _extensionOverridesByTheme["White"].Clear();
        if (overrides is null)
        {
            return;
        }

        foreach (var entry in overrides)
        {
            if (!TryParseThemeAwareExtensionRule(entry, out var themeMode, out var ext, out var color))
            {
                continue;
            }

            if (!_extensionOverridesByTheme.TryGetValue(themeMode, out var map))
            {
                map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _extensionOverridesByTheme[themeMode] = map;
            }

            map[ext] = color;
        }
    }

    private void LoadThemeColorOverrides(IEnumerable<string>? overrides)
    {
        _themeColorOverrideMap.Clear();
        if (overrides is null)
        {
            return;
        }

        foreach (var entry in overrides)
        {
            if (!TryParseThemeColorRule(entry, out var key, out var color))
            {
                continue;
            }

            _themeColorOverrideMap[key] = color;
        }
    }

    private void LoadExtensionColorRulesForTheme(string themeMode)
    {
        _extensionColorRules.Clear();
        var merged = new Dictionary<string, string>(FileSystemItem.GetBuiltInExtensionColors(themeMode), StringComparer.OrdinalIgnoreCase);
        if (_extensionOverridesByTheme.TryGetValue(themeMode, out var map))
        {
            foreach (var pair in map)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        foreach (var pair in merged.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            _extensionColorRules.Add(new ExtensionColorRule { Extension = pair.Key, Color = pair.Value });
        }
    }

    private List<string> BuildExtensionColorOverrides()
    {
        SaveCurrentThemeColorEditors();
        SaveCurrentThemeExtensionRules();
        var result = new List<string>();
        foreach (var themeMode in new[] { "Black", "White" })
        {
            var builtIn = FileSystemItem.GetBuiltInExtensionColors(themeMode);
            if (!_extensionOverridesByTheme.TryGetValue(themeMode, out var map))
            {
                continue;
            }

            foreach (var pair in map.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                var ext = NormalizeExtension(pair.Key);
                var color = NormalizeColor(pair.Value);
                if (string.IsNullOrWhiteSpace(ext) || string.IsNullOrWhiteSpace(color))
                {
                    continue;
                }

                if (builtIn.TryGetValue(ext, out var builtInColor) &&
                    string.Equals(builtInColor, color, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add($"{themeMode}|{ext}={color}");
            }
        }

        return result;
    }

    private List<string> BuildThemeColorOverrides()
    {
        SaveCurrentThemeColorEditors();
        return _themeColorOverrideMap
            .Where(pair => IsValidHexColor(pair.Value))
            .Select(pair => $"{pair.Key}={pair.Value}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void OnAddOrUpdateExtensionColorRuleClick(object sender, RoutedEventArgs e)
    {
        var extension = NormalizeExtension(TextExtensionRuleExtension.Text);
        var color = NormalizeColor(TextExtensionRuleColor.Text);
        if (string.IsNullOrWhiteSpace(extension) || string.IsNullOrWhiteSpace(color))
        {
            TextExtensionRuleHint.Text = "확장자를 입력하고 색상 선택 버튼을 눌러주세요.";
            return;
        }

        var existing = _extensionColorRules.FirstOrDefault(rule =>
            string.Equals(rule.Extension, extension, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            _extensionColorRules.Add(new ExtensionColorRule { Extension = extension, Color = color });
        }
        else
        {
            existing.Color = color;
            ExtensionColorRulesGrid.Items.Refresh();
        }

        TextExtensionRuleExtension.Text = string.Empty;
        TextExtensionRuleColor.Text = string.Empty;
        TextExtensionRuleHint.Text = "색상이 적용되었습니다.";
        SaveCurrentThemeExtensionRules();
    }

    private void OnExtensionColorRulesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExtensionColorRulesGrid.SelectedItem is not ExtensionColorRule selected)
        {
            return;
        }

        TextExtensionRuleExtension.Text = selected.Extension;
        TextExtensionRuleColor.Text = selected.Color;
        TextExtensionRuleHint.Text = $"선택 항목: {selected.Extension}";
    }


    private void OnPickExtensionRuleColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        ShowPresetColorMenu(button, hex =>
        {
            TextExtensionRuleColor.Text = hex;
            TextExtensionRuleHint.Text = $"선택 색상: {hex}";
        });
    }
    private void OnRemoveExtensionColorRuleClick(object sender, RoutedEventArgs e)
    {
        if (ExtensionColorRulesGrid.SelectedItem is not ExtensionColorRule selected)
        {
            return;
        }

        _extensionColorRules.Remove(selected);
        TextExtensionRuleExtension.Text = string.Empty;
        TextExtensionRuleColor.Text = string.Empty;
        TextExtensionRuleHint.Text = "선택 규칙을 삭제했습니다.";
    }

    private void OnClearExtensionColorRulesClick(object sender, RoutedEventArgs e)
    {
        if (_extensionOverridesByTheme.TryGetValue(_editingThemeMode, out var map))
        {
            map.Clear();
        }
        LoadExtensionColorRulesForTheme(_editingThemeMode);
        TextExtensionRuleHint.Text = "기본 확장자 색상으로 복원했습니다.";
    }

    private void OnThemeModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isInitializing)
        {
            return;
        }

        SaveCurrentThemeColorEditors();
        SaveCurrentThemeExtensionRules();
        _editingThemeMode = ComboThemeMode.SelectedIndex == 1 ? "White" : "Black";
        LoadThemeColorEditors(_editingThemeMode);
        LoadExtensionColorRulesForTheme(_editingThemeMode);
    }

    private void OnPickThemeColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string token)
        {
            return;
        }

        ShowPresetColorMenu(button, hex =>
        {
            SetThemeColorEditorValue(token, hex);
            SaveCurrentThemeColorEditors();
        });
    }

    private void OnResetThemeColorsClick(object sender, RoutedEventArgs e)
    {
        foreach (var token in new[] { "WindowBackground", "PanelBackground", "PanelForeground", "HeaderBackground", "HeaderForeground", "SelectionBackground", "BorderColor" })
        {
            _themeColorOverrideMap.Remove($"{_editingThemeMode}.{token}");
        }

        LoadThemeColorEditors(_editingThemeMode);
    }

    private static bool TryParseRule(string? entry, out string extension, out string color)
    {
        extension = string.Empty;
        color = string.Empty;
        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        var split = entry.IndexOf('=');
        if (split <= 0 || split >= entry.Length - 1)
        {
            return false;
        }

        extension = NormalizeExtension(entry[..split]);
        color = NormalizeColor(entry[(split + 1)..]);
        return !string.IsNullOrWhiteSpace(extension) && !string.IsNullOrWhiteSpace(color);
    }

    private static bool TryParseThemeAwareExtensionRule(string? entry, out string themeMode, out string extension, out string color)
    {
        themeMode = "Black";
        extension = string.Empty;
        color = string.Empty;
        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        var source = entry.Trim();
        if (source.Contains('|'))
        {
            var split = source.IndexOf('|');
            if (split > 0)
            {
                var theme = source[..split].Trim();
                source = source[(split + 1)..];
                themeMode = string.Equals(theme, "White", StringComparison.OrdinalIgnoreCase) ? "White" : "Black";
            }
        }

        if (!TryParseRule(source, out extension, out color))
        {
            return false;
        }

        return true;
    }

    private static bool TryParseThemeColorRule(string? entry, out string key, out string color)
    {
        key = string.Empty;
        color = string.Empty;
        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        var split = entry.IndexOf('=');
        if (split <= 0 || split >= entry.Length - 1)
        {
            return false;
        }

        key = entry[..split].Trim();
        color = NormalizeColor(entry[(split + 1)..]);
        return !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(color);
    }

    private void LoadThemeColorEditors(string themeMode)
    {
        SetThemeColorEditorValue("WindowBackground", ResolveThemeColor(themeMode, "WindowBackground"));
        SetThemeColorEditorValue("PanelBackground", ResolveThemeColor(themeMode, "PanelBackground"));
        SetThemeColorEditorValue("PanelForeground", ResolveThemeColor(themeMode, "PanelForeground"));
        SetThemeColorEditorValue("HeaderBackground", ResolveThemeColor(themeMode, "HeaderBackground"));
        SetThemeColorEditorValue("HeaderForeground", ResolveThemeColor(themeMode, "HeaderForeground"));
        SetThemeColorEditorValue("SelectionBackground", ResolveThemeColor(themeMode, "SelectionBackground"));
        SetThemeColorEditorValue("BorderColor", ResolveThemeColor(themeMode, "BorderColor"));
    }

    private void SaveCurrentThemeColorEditors()
    {
        SaveThemeColorValue(_editingThemeMode, "WindowBackground", TextThemeWindowBackground.Text);
        SaveThemeColorValue(_editingThemeMode, "PanelBackground", TextThemePanelBackground.Text);
        SaveThemeColorValue(_editingThemeMode, "PanelForeground", TextThemePanelForeground.Text);
        SaveThemeColorValue(_editingThemeMode, "HeaderBackground", TextThemeHeaderBackground.Text);
        SaveThemeColorValue(_editingThemeMode, "HeaderForeground", TextThemeHeaderForeground.Text);
        SaveThemeColorValue(_editingThemeMode, "SelectionBackground", TextThemeSelectionBackground.Text);
        SaveThemeColorValue(_editingThemeMode, "BorderColor", TextThemeBorderColor.Text);
    }

    private void SaveCurrentThemeExtensionRules()
    {
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in _extensionColorRules)
        {
            var ext = NormalizeExtension(rule.Extension);
            var color = NormalizeColor(rule.Color);
            if (string.IsNullOrWhiteSpace(ext) || string.IsNullOrWhiteSpace(color))
            {
                continue;
            }

            current[ext] = color;
        }

        _extensionOverridesByTheme[_editingThemeMode] = current;
    }

    private string ResolveThemeColor(string themeMode, string token)
    {
        var key = $"{themeMode}.{token}";
        if (_themeColorOverrideMap.TryGetValue(key, out var color) && IsValidHexColor(color))
        {
            return color;
        }

        var defaults = string.Equals(themeMode, "White", StringComparison.OrdinalIgnoreCase) ? WhiteThemeDefaults : BlackThemeDefaults;
        return defaults.TryGetValue(token, out var fallback) ? fallback : "#FFFFFF";
    }

    private void SaveThemeColorValue(string themeMode, string token, string rawColor)
    {
        var color = NormalizeColor(rawColor);
        var key = $"{themeMode}.{token}";
        if (string.IsNullOrWhiteSpace(color))
        {
            _themeColorOverrideMap.Remove(key);
            return;
        }

        var defaults = string.Equals(themeMode, "White", StringComparison.OrdinalIgnoreCase) ? WhiteThemeDefaults : BlackThemeDefaults;
        if (defaults.TryGetValue(token, out var defaultColor) &&
            string.Equals(defaultColor, color, StringComparison.OrdinalIgnoreCase))
        {
            _themeColorOverrideMap.Remove(key);
            return;
        }

        _themeColorOverrideMap[key] = color;
    }

    private void SetThemeColorEditorValue(string token, string color)
    {
        var value = NormalizeColor(color);
        switch (token)
        {
            case "WindowBackground":
                TextThemeWindowBackground.Text = value;
                break;
            case "PanelBackground":
                TextThemePanelBackground.Text = value;
                break;
            case "PanelForeground":
                TextThemePanelForeground.Text = value;
                break;
            case "HeaderBackground":
                TextThemeHeaderBackground.Text = value;
                break;
            case "HeaderForeground":
                TextThemeHeaderForeground.Text = value;
                break;
            case "SelectionBackground":
                TextThemeSelectionBackground.Text = value;
                break;
            case "BorderColor":
                TextThemeBorderColor.Text = value;
                break;
        }
    }

    private static bool IsValidHexColor(string? color)
    {
        return !string.IsNullOrWhiteSpace(color) && Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$");
    }

    private void ShowPresetColorMenu(Button button, Action<string> onSelect)
    {
        var menu = new ContextMenu();
        foreach (var hex in PresetHexColors)
        {
            var swatch = new Border
            {
                Width = 12,
                Height = 12,
                Margin = new Thickness(0, 0, 8, 0),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Background = (Brush)new BrushConverter().ConvertFromString(hex)!
            };

            var label = new TextBlock
            {
                Text = hex,
                VerticalAlignment = VerticalAlignment.Center
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(swatch);
            panel.Children.Add(label);

            var item = new MenuItem { Header = panel, Tag = hex };
            item.Click += (_, _) => onSelect(hex);
            menu.Items.Add(item);
        }

        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private static string NormalizeExtension(string? extension)
    {
        var value = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.StartsWith('.') ? value : $".{value}";
    }

    private static string NormalizeColor(string? color)
    {
        var value = (color ?? string.Empty).Trim().TrimStart('#');
        if (!Regex.IsMatch(value, "^[0-9a-fA-F]{6}$"))
        {
            return string.Empty;
        }

        return $"#{value.ToUpperInvariant()}";
    }

    private static double ParseDoubleInRange(string? raw, double fallback, double min, double max)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            !double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            return fallback;
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return fallback;
        }

        return Math.Clamp(value, min, max);
    }

    private static void AdjustNumericTextBox(TextBox textBox, int step, double min, double max)
    {
        var current = ParseDoubleInRange(textBox.Text, min, min, max);
        var next = Math.Clamp(current + step, min, max);
        textBox.Text = next.ToString("0", CultureInfo.InvariantCulture);
    }

    private sealed class ExtensionColorRule
    {
        public string Extension { get; set; } = string.Empty;
        public string Color { get; set; } = string.Empty;
    }
}



using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using WorkFileExplorer.App.ViewModels;

namespace WorkFileExplorer.App.Dialogs;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<ExtensionColorRule> _extensionColorRules = [];
    private static readonly string[] PresetHexColors =
    [
        "#66B3FF", "#4CAF50", "#FFC107", "#FF7043", "#E91E63", "#9C27B0",
        "#3F51B5", "#009688", "#8BC34A", "#FF9800", "#795548", "#607D8B",
        "#F44336", "#2196F3", "#00BCD4", "#CDDC39", "#9E9E9E", "#FFFFFF",
        "#C0C0C0", "#000000"
    ];
    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
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
        CheckUseExtensionColors.IsChecked = snapshot.UseExtensionColors;
        CheckUsePinnedHighlight.IsChecked = snapshot.UsePinnedHighlightColor;
        CheckConfirmDeleteFileSection.IsChecked = snapshot.ConfirmBeforeDelete;
        CheckSearchRecursive.IsChecked = snapshot.SearchRecursive;
        ExtensionColorRulesGrid.ItemsSource = _extensionColorRules;
        LoadExtensionColorRules(snapshot.ExtensionColorOverrides);

        ComboConflictPolicy.ItemsSource = Vm.ConflictPolicyOptions;
        ComboConflictPolicy.SelectedItem = Vm.ConflictPolicyOptions.Contains(snapshot.ConflictPolicyDisplay)
            ? snapshot.ConflictPolicyDisplay
            : Vm.ConflictPolicyOptions.FirstOrDefault();
        ComboSearchScope.SelectedIndex = string.Equals(snapshot.SearchScope, "Both panels", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        ShowSection("general");
    }

    private async void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            DialogResult = true;
            Close();
            return;
        }

        var snapshot = new MainWindowViewModel.UiSettingsSnapshot
        {
            UseFourPanels = RadioFourPanel.IsChecked == true,
            UseGridLayout = RadioLayout2x2.IsChecked == true,
            RememberSessionTabs = CheckRememberSession.IsChecked == true,
            DefaultTileViewEnabled = RadioTileView.IsChecked == true,
            UseExtensionColors = CheckUseExtensionColors.IsChecked == true,
            UsePinnedHighlightColor = CheckUsePinnedHighlight.IsChecked == true,
            ConfirmBeforeDelete = CheckConfirmDeleteFileSection.IsChecked == true,
            ConflictPolicyDisplay = (ComboConflictPolicy.SelectedItem as string) ?? Vm.SelectedConflictPolicyDisplay,
            SearchScope = ComboSearchScope.SelectedIndex == 1 ? "Both panels" : "Active panel",
            SearchRecursive = CheckSearchRecursive.IsChecked == true,
            ExtensionColorOverrides = BuildExtensionColorOverrides()
        };

        await Vm.ApplyUiSettingsAsync(snapshot);
        DialogResult = true;
        Close();
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

    private void LoadExtensionColorRules(IEnumerable<string>? overrides)
    {
        _extensionColorRules.Clear();
        if (overrides is null)
        {
            return;
        }

        foreach (var entry in overrides)
        {
            if (!TryParseRule(entry, out var ext, out var color))
            {
                continue;
            }

            _extensionColorRules.Add(new ExtensionColorRule { Extension = ext, Color = color });
        }
    }

    private List<string> BuildExtensionColorOverrides()
    {
        return _extensionColorRules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Extension) && !string.IsNullOrWhiteSpace(rule.Color))
            .Select(rule => $"{NormalizeExtension(rule.Extension)}={NormalizeColor(rule.Color)}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(rule => rule, StringComparer.OrdinalIgnoreCase)
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
    }


    private void OnPickExtensionRuleColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var menu = new ContextMenu();
        foreach (var hex in PresetHexColors)
        {
            var swatch = new Border
            {
                Width = 12,
                Height = 12,
                Margin = new Thickness(0, 0, 8, 0),
                BorderBrush = System.Windows.Media.Brushes.Gray,
                BorderThickness = new Thickness(1),
                Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!
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
            item.Click += (_, _) =>
            {
                TextExtensionRuleColor.Text = hex;
                TextExtensionRuleHint.Text = $"선택 색상: {hex}";
            };

            menu.Items.Add(item);
        }

        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }
    private void OnRemoveExtensionColorRuleClick(object sender, RoutedEventArgs e)
    {
        if (ExtensionColorRulesGrid.SelectedItem is not ExtensionColorRule selected)
        {
            return;
        }

        _extensionColorRules.Remove(selected);
        TextExtensionRuleHint.Text = "선택 규칙을 삭제했습니다.";
    }

    private void OnClearExtensionColorRulesClick(object sender, RoutedEventArgs e)
    {
        _extensionColorRules.Clear();
        TextExtensionRuleHint.Text = "규칙을 모두 초기화했습니다.";
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

    private sealed class ExtensionColorRule
    {
        public string Extension { get; set; } = string.Empty;
        public string Color { get; set; } = string.Empty;
    }
}


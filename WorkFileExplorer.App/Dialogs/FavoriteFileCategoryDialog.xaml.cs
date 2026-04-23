using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WorkFileExplorer.App.Dialogs;

public enum FavoriteFileCategoryDialogAction
{
    None,
    Confirm,
    Unfavorite
}

public sealed class FavoriteFileCategoryDialogResult
{
    public FavoriteFileCategoryDialogResult(
        FavoriteFileCategoryDialogAction action,
        string selectedCategoryPath,
        IReadOnlyList<string> categoryPaths)
    {
        Action = action;
        SelectedCategoryPath = selectedCategoryPath;
        CategoryPaths = categoryPaths;
    }

    public FavoriteFileCategoryDialogAction Action { get; }
    public string SelectedCategoryPath { get; }
    public IReadOnlyList<string> CategoryPaths { get; }
}

public partial class FavoriteFileCategoryDialog : Window, INotifyPropertyChanged
{
    private const string HiddenRoot = "기본";
    private const string TopRootDisplayName = "최상위 루트";

    private string _targetPath = string.Empty;
    private FavoriteCategoryNode? _selectedNode;
    private readonly bool _canUnfavorite;
    private readonly FavoriteCategoryNode _topRootNode;

    public FavoriteFileCategoryDialog(string targetPath, IEnumerable<string> categoryPaths, string selectedCategoryPath, bool canUnfavorite)
    {
        InitializeComponent();
        DataContext = this;
        TargetPath = targetPath ?? string.Empty;
        _canUnfavorite = canUnfavorite;
        _topRootNode = FavoriteCategoryNode.CreateVirtualRoot(TopRootDisplayName);
        _topRootNode.IsExpanded = true;
        TreeNodes.Add(_topRootNode);

        BuildTree(categoryPaths);
        SelectByPath(selectedCategoryPath);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FavoriteCategoryNode> TreeNodes { get; } = [];
    public ObservableCollection<FavoriteCategoryNode> RootNodes => _topRootNode.Children;

    public string TargetPath
    {
        get => _targetPath;
        set
        {
            if (string.Equals(_targetPath, value, StringComparison.Ordinal))
            {
                return;
            }

            _targetPath = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetPath)));
        }
    }

    public bool CanUnfavorite => _canUnfavorite;

    public FavoriteCategoryNode? SelectedNode
    {
        get => _selectedNode;
        private set
        {
            if (ReferenceEquals(_selectedNode, value))
            {
                return;
            }

            _selectedNode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedNode)));
        }
    }

    public FavoriteFileCategoryDialogResult? Result { get; private set; }

    public static FavoriteFileCategoryDialogResult? ShowDialog(
        Window owner,
        string targetName,
        string targetPath,
        IEnumerable<string> categoryPaths,
        string selectedCategoryPath,
        bool canUnfavorite)
    {
        var dialog = new FavoriteFileCategoryDialog(targetPath, categoryPaths, selectedCategoryPath, canUnfavorite)
        {
            Owner = owner,
            Title = $"즐겨찾기 분류 - {targetName}"
        };

        var accepted = dialog.ShowDialog() == true;
        return accepted ? dialog.Result : null;
    }

    private void BuildTree(IEnumerable<string> categoryPaths)
    {
        RootNodes.Clear();

        var visiblePaths = (categoryPaths ?? Array.Empty<string>())
            .Select(ToVisiblePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase);

        foreach (var path in visiblePaths)
        {
            AddPathNode(path);
        }
    }

    private static string ToVisiblePath(string? externalPath)
    {
        var normalized = NormalizePath(externalPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (string.Equals(normalized, HiddenRoot, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var prefix = HiddenRoot + "/";
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return normalized[prefix.Length..];
        }

        return normalized;
    }

    private static string ToExternalPath(string? visiblePath)
    {
        var normalized = NormalizePath(visiblePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return HiddenRoot;
        }

        return $"{HiddenRoot}/{normalized}";
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return string.Join("/",
            path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Trim()));
    }

    private void AddPathNode(string visiblePath)
    {
        var parts = visiblePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return;
        }

        FavoriteCategoryNode? parent = null;
        var currentNodes = RootNodes;
        foreach (var part in parts)
        {
            var existing = currentNodes.FirstOrDefault(node =>
                string.Equals(node.Name, part, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new FavoriteCategoryNode(part, parent);
                currentNodes.Add(existing);
            }

            existing.IsExpanded = true;
            parent = existing;
            currentNodes = existing.Children;
        }
    }

    private void SelectByPath(string? externalPath)
    {
        var visiblePath = ToVisiblePath(externalPath);
        if (string.IsNullOrWhiteSpace(visiblePath))
        {
            _topRootNode.IsSelected = true;
            SelectedNode = _topRootNode;
            return;
        }

        var node = FindByPath(RootNodes, visiblePath) ?? RootNodes.FirstOrDefault();
        if (node is null)
        {
            return;
        }

        node.IsSelected = true;
        SelectedNode = node;
    }

    private static FavoriteCategoryNode? FindByPath(IEnumerable<FavoriteCategoryNode> nodes, string path)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.FullPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            var child = FindByPath(node.Children, path);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private static string SanitizeSegment(string? rawName)
    {
        var name = (rawName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        return name
            .Replace("/", string.Empty, StringComparison.Ordinal)
            .Replace("\\", string.Empty, StringComparison.Ordinal)
            .Replace("\t", string.Empty, StringComparison.Ordinal);
    }

    private static string MakeUniqueName(ObservableCollection<FavoriteCategoryNode> siblings, string desiredName, FavoriteCategoryNode? self = null)
    {
        var baseName = string.IsNullOrWhiteSpace(desiredName) ? "새 폴더" : desiredName;
        var candidate = baseName;
        var index = 2;
        while (siblings.Any(node =>
                   !ReferenceEquals(node, self) &&
                   string.Equals(node.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} ({index++})";
        }

        return candidate;
    }

    private void BeginInlineEdit(FavoriteCategoryNode node)
    {
        node.EditName = node.Name;
        node.IsEditing = true;
    }

    private void CommitInlineEdit(FavoriteCategoryNode node)
    {
        var siblings = node.Parent?.Children ?? RootNodes;
        var sanitized = SanitizeSegment(node.EditName);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = string.IsNullOrWhiteSpace(node.Name) ? "새 폴더" : node.Name;
        }

        node.Name = MakeUniqueName(siblings, sanitized, self: node);
        node.EditName = node.Name;
        node.IsEditing = false;
    }

    private void CancelInlineEdit(FavoriteCategoryNode node)
    {
        node.EditName = node.Name;
        node.IsEditing = false;
    }

    private void CloseWithAction(FavoriteFileCategoryDialogAction action)
    {
        var selectedExternal = SelectedNode is null || SelectedNode.IsVirtualRoot
            ? HiddenRoot
            : ToExternalPath(SelectedNode.FullPath);
        var externalPaths = EnumerateCategoryPaths(RootNodes)
            .Select(ToExternalPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (!externalPaths.Contains(HiddenRoot, StringComparer.OrdinalIgnoreCase))
        {
            externalPaths.Insert(0, HiddenRoot);
        }

        Result = new FavoriteFileCategoryDialogResult(action, selectedExternal, externalPaths);
        DialogResult = true;
        Close();
    }

    private static IEnumerable<string> EnumerateCategoryPaths(IEnumerable<FavoriteCategoryNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node.FullPath;
            foreach (var child in EnumerateCategoryPaths(node.Children))
            {
                yield return child;
            }
        }
    }

    private void OnFolderTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        SelectedNode = e.NewValue as FavoriteCategoryNode;
    }

    private void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        // Add as child of selected node (or top root when nothing selected).
        var parent = SelectedNode ?? _topRootNode;
        var targetSiblings = parent?.Children ?? RootNodes;
        if (parent is not null)
        {
            parent.IsExpanded = true;
        }

        var newName = MakeUniqueName(targetSiblings, "새 폴더");
        var node = new FavoriteCategoryNode(newName, parent)
        {
            IsSelected = true,
            IsEditing = true,
            EditName = newName
        };
        targetSiblings.Add(node);
        SelectedNode = node;
    }

    private void OnRenameFolderClick(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is null || SelectedNode.IsVirtualRoot)
        {
            return;
        }

        BeginInlineEdit(SelectedNode);
    }

    private void OnDeleteFolderClick(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is null || SelectedNode.IsVirtualRoot)
        {
            return;
        }

        var parent = SelectedNode.Parent;
        if (parent is null)
        {
            RootNodes.Remove(SelectedNode);
            SelectedNode = _topRootNode;
            if (SelectedNode is not null)
            {
                SelectedNode.IsSelected = true;
            }

            return;
        }

        parent.Children.Remove(SelectedNode);
        parent.IsSelected = true;
        SelectedNode = parent;
    }

    private void OnEditNameTextBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox && textBox.DataContext is FavoriteCategoryNode node && node.IsEditing)
        {
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    private void OnEditNameTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.DataContext is not FavoriteCategoryNode node)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            CommitInlineEdit(node);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            CancelInlineEdit(node);
            e.Handled = true;
        }
    }

    private void OnEditNameTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.DataContext is not FavoriteCategoryNode node || !node.IsEditing)
        {
            return;
        }

        CommitInlineEdit(node);
    }

    private void OnUnfavoriteClick(object sender, RoutedEventArgs e)
    {
        CloseWithAction(FavoriteFileCategoryDialogAction.Unfavorite);
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        foreach (var node in EnumerateNodes(RootNodes))
        {
            if (node.IsEditing)
            {
                CommitInlineEdit(node);
            }
        }

        CloseWithAction(FavoriteFileCategoryDialogAction.Confirm);
    }

    private static IEnumerable<FavoriteCategoryNode> EnumerateNodes(IEnumerable<FavoriteCategoryNode> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in EnumerateNodes(root.Children))
            {
                yield return child;
            }
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    public sealed class FavoriteCategoryNode : INotifyPropertyChanged
    {
        private string _name;
        private string _editName;
        private bool _isExpanded;
        private bool _isSelected;
        private bool _isEditing;
        private readonly bool _isVirtualRoot;

        public FavoriteCategoryNode(string name, FavoriteCategoryNode? parent, bool isVirtualRoot = false)
        {
            _name = name;
            _editName = name;
            Parent = parent;
            _isVirtualRoot = isVirtualRoot;
        }

        public static FavoriteCategoryNode CreateVirtualRoot(string name) => new(name, parent: null, isVirtualRoot: true);

        public event PropertyChangedEventHandler? PropertyChanged;

        public FavoriteCategoryNode? Parent { get; }
        public ObservableCollection<FavoriteCategoryNode> Children { get; } = [];
        public bool IsVirtualRoot => _isVirtualRoot;

        public string Name
        {
            get => _name;
            set
            {
                if (string.Equals(_name, value, StringComparison.Ordinal))
                {
                    return;
                }

                _name = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullPath)));
                foreach (var child in Children)
                {
                    child.RaiseFullPathChangedCascade();
                }
            }
        }

        public string EditName
        {
            get => _editName;
            set
            {
                if (string.Equals(_editName, value, StringComparison.Ordinal))
                {
                    return;
                }

                _editName = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditName)));
            }
        }

        public string FullPath => Parent is null || Parent.IsVirtualRoot ? Name : $"{Parent.FullPath}/{Name}";

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                {
                    return;
                }

                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public bool IsEditing
        {
            get => _isEditing;
            set
            {
                if (_isEditing == value)
                {
                    return;
                }

                _isEditing = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing)));
            }
        }

        private void RaiseFullPathChangedCascade()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullPath)));
            foreach (var child in Children)
            {
                child.RaiseFullPathChangedCascade();
            }
        }
    }
}

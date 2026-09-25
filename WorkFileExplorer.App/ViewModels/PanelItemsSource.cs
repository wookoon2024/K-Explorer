using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using WorkFileExplorer.App.Models;

namespace WorkFileExplorer.App.ViewModels;

public sealed class PanelItemsSource : IReadOnlyList<FileSystemItem>, INotifyCollectionChanged, INotifyPropertyChanged
{
    private readonly List<FileSystemItem> _items = [];
    private IEnumerable<FileSystemItem>? _source;
    private INotifyCollectionChanged? _sourceNotifications;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public int Count => _items.Count;
    public FileSystemItem this[int index] => _items[index];

    public void SetSource(IEnumerable<FileSystemItem>? source)
    {
        if (ReferenceEquals(_source, source))
        {
            return;
        }

        if (_sourceNotifications is not null)
        {
            _sourceNotifications.CollectionChanged -= OnSourceCollectionChanged;
        }

        _source = source;
        _sourceNotifications = source as INotifyCollectionChanged;
        if (_sourceNotifications is not null)
        {
            _sourceNotifications.CollectionChanged += OnSourceCollectionChanged;
        }

        Replace(source);
    }

    public IEnumerator<FileSystemItem> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void Replace(IEnumerable<FileSystemItem>? source)
    {
        _items.Clear();
        if (source is not null)
        {
            _items.AddRange(source);
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Replace(_source);
    }
}

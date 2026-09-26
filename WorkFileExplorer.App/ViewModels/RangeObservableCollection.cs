using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WorkFileExplorer.App.ViewModels;

public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotifications;

    public bool ReplaceRange(IEnumerable<T> items)
    {
        var incoming = items as IReadOnlyList<T> ?? items.ToList();
        if (Items.Count == incoming.Count)
        {
            var sameReferences = true;
            for (var index = 0; index < incoming.Count; index++)
            {
                if (!ReferenceEquals(Items[index], incoming[index]))
                {
                    sameReferences = false;
                    break;
                }
            }

            if (sameReferences)
            {
                return false;
            }
        }

        _suppressNotifications = true;
        try
        {
            Items.Clear();
            foreach (var item in incoming)
            {
                Items.Add(item);
            }
        }
        finally
        {
            _suppressNotifications = false;
        }

        // Emit a single reset instead of per-item updates.
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        return true;
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_suppressNotifications)
        {
            return;
        }

        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_suppressNotifications)
        {
            return;
        }

        base.OnPropertyChanged(e);
    }
}

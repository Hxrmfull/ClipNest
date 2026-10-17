using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ClipNest.Models;

/// <summary>The popup's item list. <see cref="ReplaceAll"/> swaps the whole page with a single Reset:
/// with Clear + Add the list box handled one change notification per item on every reload.</summary>
public sealed class ClipList : ObservableCollection<ClipItem>
{
    public void ReplaceAll(IEnumerable<ClipItem> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

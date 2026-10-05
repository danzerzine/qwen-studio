using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace QwenStudio.Core
{
    /// <summary>ObservableCollection that drops its oldest items with one Reset instead of a notification per item.</summary>
    public sealed class BulkCollection<T> : ObservableCollection<T>
    {
        public void RemoveFirst(int count)
        {
            if (count <= 0) return;
            CheckReentrancy();
            var items = (System.Collections.Generic.List<T>)Items;
            items.RemoveRange(0, System.Math.Min(count, items.Count));
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}

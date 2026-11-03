using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace FindFont.App.Collections;

/// <summary>
/// 支持批量替换的 <see cref="ObservableCollection{T}"/>。
/// 逐条 Clear + Add 会让绑定到它的 ListBox 重建视图上千次；
/// <see cref="ReplaceAll"/> 只发一次 Reset 通知。
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification;

    public RangeObservableCollection()
    {
    }

    public RangeObservableCollection(IEnumerable<T> items) : base(items)
    {
    }

    /// <summary>一次替换全部内容，只触发一次 CollectionChanged(Reset)。</summary>
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        _suppressNotification = true;
        try
        {
            Items.Clear();
            foreach (var item in items) Items.Add(item);
        }
        finally
        {
            _suppressNotification = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification) base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressNotification) base.OnPropertyChanged(e);
    }
}

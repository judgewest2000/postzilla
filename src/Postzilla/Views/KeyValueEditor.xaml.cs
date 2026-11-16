using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Postzilla.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Postzilla.Views;

public sealed partial class KeyValueEditor : UserControl
{
    private ObservableCollection<KeyValueItem>? _items;

    public KeyValueEditor()
    {
        InitializeComponent();
    }

    /// <summary>Raised on any add/remove or edit of a row.</summary>
    public event EventHandler? Changed;

    public string KeyLabel { set => KeyHeader.Text = value; }
    public string ValueLabel { set => ValueHeader.Text = value; }

    public ObservableCollection<KeyValueItem>? Items
    {
        get => _items;
        set
        {
            if (_items != null)
            {
                _items.CollectionChanged -= OnCollectionChanged;
                foreach (var i in _items) i.PropertyChanged -= OnItemChanged;
            }
            _items = value;
            if (_items != null)
            {
                _items.CollectionChanged += OnCollectionChanged;
                foreach (var i in _items) i.PropertyChanged += OnItemChanged;
            }
            ItemsList.ItemsSource = _items;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (KeyValueItem i in e.OldItems) i.PropertyChanged -= OnItemChanged;
        if (e.NewItems != null) foreach (KeyValueItem i in e.NewItems) i.PropertyChanged += OnItemChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset && _items != null)
            foreach (var i in _items) { i.PropertyChanged -= OnItemChanged; i.PropertyChanged += OnItemChanged; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KeyValueItem.IsEnabledNullable)) return;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Add_Click(object sender, RoutedEventArgs e) => _items?.Add(new KeyValueItem());

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is KeyValueItem item) _items?.Remove(item);
    }
}

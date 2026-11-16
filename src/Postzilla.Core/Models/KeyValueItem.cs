using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Postzilla.Models;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class KeyValueItem : ObservableObject
{
    private bool _enabled = true;
    private string _key = "";
    private string _value = "";

    public KeyValueItem() { }

    public KeyValueItem(string key, string value, bool enabled = true)
    {
        _key = key;
        _value = value;
        _enabled = enabled;
    }

    public bool Enabled
    {
        get => _enabled;
        set { if (SetProperty(ref _enabled, value)) OnPropertyChanged(nameof(IsEnabledNullable)); }
    }

    // CheckBox.IsChecked is bool?, so expose a nullable twin for two-way binding.
    [JsonIgnore]
    public bool? IsEnabledNullable
    {
        get => _enabled;
        set => Enabled = value ?? false;
    }

    public string Key
    {
        get => _key;
        set => SetProperty(ref _key, value ?? "");
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value ?? "");
    }

    public KeyValueItem Clone() => new(Key, Value, Enabled);

    public static List<KeyValueItem> CloneList(IEnumerable<KeyValueItem>? items) =>
        items?.Select(i => i.Clone()).ToList() ?? new List<KeyValueItem>();
}

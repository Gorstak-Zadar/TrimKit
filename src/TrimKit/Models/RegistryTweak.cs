using CommunityToolkit.Mvvm.ComponentModel;

namespace TrimKit.Models;

public partial class RegistryTweak : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string HivePath { get; set; } = string.Empty;
    public string KeyPath { get; set; } = string.Empty;
    public string ValueName { get; set; } = string.Empty;
    public RegistryValueType ValueType { get; set; }
    public object? Value { get; set; }

    [ObservableProperty] private bool _isSelected;
}

public enum RegistryValueType
{
    DWord,
    QWord,
    String,
    ExpandString,
    MultiString,
    Binary
}

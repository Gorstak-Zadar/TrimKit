using CommunityToolkit.Mvvm.ComponentModel;

namespace TrimKit.Models;

public partial class WindowsFeature : ObservableObject
{
    public string FeatureName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _originalState;

    public bool IsModified => IsEnabled != OriginalState;
}

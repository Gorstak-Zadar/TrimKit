using CommunityToolkit.Mvvm.ComponentModel;

namespace TrimKit.Models;

public partial class WindowsPackage : ObservableObject
{
    public string PackageName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ReleaseType { get; set; } = string.Empty;
    public DateTime InstallTime { get; set; }
    public string Description { get; set; } = string.Empty;

    [ObservableProperty] private bool _isSelected;
}

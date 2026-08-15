using CommunityToolkit.Mvvm.ComponentModel;
using TrimKit.Models;

namespace TrimKit.Services;

/// <summary>
/// Manages Windows services in an offline image — list, disable, enable, or set startup type.
/// Uses DISM and offline registry manipulation.
/// </summary>
public interface IWindowsServiceManager
{
    Task<List<WindowsServiceInfo>> GetServicesAsync(string mountPath);
    Task SetServiceStartTypeAsync(string mountPath, string serviceName, ServiceStartType startType);
    Task RemoveServiceAsync(string mountPath, string serviceName);
    Task ConfigureServicesAsync(string mountPath, List<(string serviceName, ServiceStartType startType)> changes);
}

public partial class WindowsServiceInfo : ObservableObject
{
    public string ServiceName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsProtected { get; set; }
    public bool IsReadOnly { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private ServiceStartType _startType;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private ServiceStartType _originalStartType;

    [ObservableProperty] private bool _isSelected;

    public bool IsModified => StartType != OriginalStartType;
}

public enum ServiceStartType
{
    Boot = 0,
    System = 1,
    Automatic = 2,
    Manual = 3,
    Disabled = 4,
    Remove = 99
}

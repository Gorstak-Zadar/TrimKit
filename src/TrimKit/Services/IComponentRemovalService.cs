using CommunityToolkit.Mvvm.ComponentModel;

namespace TrimKit.Services;

/// <summary>
/// Comprehensive component removal service that handles ALL removal categories
/// from NTLite, WinReducer, and DISM++:
/// - Provisioned Appx packages (Store apps)
/// - Optional Features (Windows features on/off)
/// - Capabilities (FOD - Features on Demand)
/// - Fonts (file deletion + registry cleanup)
/// - Keyboard layouts (file deletion)
/// - Languages/locale data
/// - Drivers (inbox drivers)  
/// - System accessories (file-level removal)
/// - Control panel items
/// - Multimedia codecs
/// - Network components
/// - WinRE (winre.wim removal/stripping)
/// </summary>
public interface IComponentRemovalService
{
    // Discovery (scans mounted image for removable items)
    Task<List<RemovableComponent>> GetProvisionedAppsAsync(string mountPath);
    Task<List<RemovableComponent>> GetCapabilitiesAsync(string mountPath);
    Task<List<RemovableComponent>> GetOptionalFeaturesAsync(string mountPath);
    Task<List<RemovableComponent>> GetFontsAsync(string mountPath);
    Task<List<RemovableComponent>> GetKeyboardLayoutsAsync(string mountPath);
    Task<List<RemovableComponent>> GetLanguagesAsync(string mountPath);
    Task<List<RemovableComponent>> GetInboxDriversAsync(string mountPath);

    // Removal actions
    Task RemoveProvisionedAppAsync(string mountPath, string packageName);
    Task RemoveCapabilityAsync(string mountPath, string capabilityName);
    Task DisableFeatureAsync(string mountPath, string featureName);
    Task RemoveFontAsync(string mountPath, string fontFileName);
    Task RemoveKeyboardLayoutAsync(string mountPath, string layoutId);
    Task RemoveLanguageAsync(string mountPath, string languageTag);
    Task RemoveInboxDriverAsync(string mountPath, string infName);

    // Bulk operations
    Task RemoveWinREAsync(string mountPath);
    Task StripWinREAsync(string mountPath);
    Task RemoveAllAsync(string mountPath, List<RemovableComponent> components, IProgress<(int percent, string status)>? progress = null);
}

public partial class RemovableComponent : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public ComponentType Type { get; set; }
    public long Size { get; set; }
    public bool IsProtected { get; set; } // Flagged as risky to remove
    public string? Description { get; set; }

    [ObservableProperty] private bool _isSelected;

    public string SizeDisplay => Size > 0 ? $"{Size / (1024.0 * 1024.0):F1} MB" : "";
}

public enum ComponentType
{
    ProvisionedApp,
    Capability,
    OptionalFeature,
    Package,
    Font,
    KeyboardLayout,
    Language,
    InboxDriver,
    Accessory,
    ControlPanel,
    Multimedia,
    Network,
    WinRE,
    SystemFile
}

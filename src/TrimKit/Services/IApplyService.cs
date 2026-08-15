using TrimKit.Models;

namespace TrimKit.Services;

/// <summary>
/// Orchestrates applying changes to mounted Windows images.
/// Extracted from MainViewModel to reduce its size and improve separation of concerns.
/// </summary>
public interface IApplyService
{
    /// <summary>
    /// Executes NTLite component map removals: file deletions, directory deletions,
    /// service disabling, app removal, and language removal.
    /// </summary>
    Task ExecuteNtLiteRemovalsAsync(
        string mountPath,
        List<PresetComponent> presetItems,
        IProgress<(int percent, string status)>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies wallpaper customization from a loaded preset.
    /// </summary>
    Task ApplyWallpapersAsync(
        string installMountPath,
        string? bootWimPath,
        bool isBootMounted,
        WallpaperPreset wallpapers,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies service configuration changes from a loaded preset.
    /// </summary>
    Task ApplyPresetServiceChangesAsync(
        string mountPath,
        List<ServicePreset> serviceChanges,
        CancellationToken cancellationToken = default);
}

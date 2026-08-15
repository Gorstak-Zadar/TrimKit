using TrimKit.Models;

namespace TrimKit.Services;

public interface IDismService
{
    Task<List<WimImageInfo>> GetWimInfoAsync(string wimPath, CancellationToken cancellationToken = default);
    Task MountImageAsync(string wimPath, int imageIndex, string mountPath, IProgress<int>? progress = null, CancellationToken cancellationToken = default);
    Task UnmountImageAsync(string mountPath, bool commitChanges, IProgress<int>? progress = null, CancellationToken cancellationToken = default);
    Task<List<WindowsPackage>> GetPackagesAsync(string mountPath, CancellationToken cancellationToken = default);
    Task RemovePackageAsync(string mountPath, string packageName, CancellationToken cancellationToken = default);
    Task<List<WindowsFeature>> GetFeaturesAsync(string mountPath, CancellationToken cancellationToken = default);
    Task EnableFeatureAsync(string mountPath, string featureName, CancellationToken cancellationToken = default);
    Task DisableFeatureAsync(string mountPath, string featureName, CancellationToken cancellationToken = default);
    Task AddDriverAsync(string mountPath, string driverPath, bool recurse = true, bool forceUnsigned = false, CancellationToken cancellationToken = default);
    Task<string> GetMountedImageStatus(string mountPath, CancellationToken cancellationToken = default);
    Task CleanupMountsAsync(CancellationToken cancellationToken = default);
}

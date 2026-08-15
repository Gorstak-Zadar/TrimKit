using System.IO;
using TrimKit.Models;

namespace TrimKit.Services;

/// <summary>
/// Handles the complex apply-phase operations extracted from MainViewModel.
/// </summary>
public class ApplyService : IApplyService
{
    private readonly ILogService _logService;
    private readonly IWindowsServiceManager _serviceManager;
    private readonly IComponentRemovalService _componentRemovalService;
    private readonly ICustomizationService _customizationService;

    public ApplyService(
        ILogService logService,
        IWindowsServiceManager serviceManager,
        IComponentRemovalService componentRemovalService,
        ICustomizationService customizationService)
    {
        _logService = logService;
        _serviceManager = serviceManager;
        _componentRemovalService = componentRemovalService;
        _customizationService = customizationService;
    }

    public async Task ExecuteNtLiteRemovalsAsync(
        string mountPath,
        List<PresetComponent> presetItems,
        IProgress<(int percent, string status)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedPlan = NtLiteComponentMap.ResolvePreset(presetItems, mountPath);
        if (resolvedPlan.TotalActions == 0)
            return;

        progress?.Report((0, $"Executing {resolvedPlan.TotalActions} NTLite-mapped removal(s)..."));
        _logService.Log(LogLevel.Info,
            $"NTLite map: {resolvedPlan.FilesToDelete.Count} files, {resolvedPlan.DirectoriesToDelete.Count} dirs, " +
            $"{resolvedPlan.ServicesToDisable.Count} services, {resolvedPlan.AppsToRemove.Count} apps, " +
            $"{resolvedPlan.LanguagesToRemove.Count} languages, {resolvedPlan.DriversToRemove.Count} drivers");

        var completed = 0;
        var total = resolvedPlan.TotalActions;

        // Delete files
        foreach (var file in resolvedPlan.FilesToDelete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(file))
                {
                    if (!SafetyGuard.IsSafeToDeleteFromDisk(file))
                    {
                        _logService.Log(LogLevel.Warning, $"Preset file deletion blocked (protected): {file}");
                        continue;
                    }
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Warning, $"Could not delete file {Path.GetFileName(file)}: {ex.Message}");
            }
            completed++;
            progress?.Report(((int)(completed * 100.0 / total), $"Deleting files ({completed}/{total})"));
        }

        // Delete directories (supports wildcard patterns like Microsoft.Xbox*)
        foreach (var dir in resolvedPlan.DirectoriesToDelete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (dir.Contains('*'))
                {
                    var parent = Path.GetDirectoryName(dir) ?? mountPath;
                    var pattern = Path.GetFileName(dir);
                    if (Directory.Exists(parent))
                    {
                        foreach (var d in Directory.GetDirectories(parent, pattern))
                        {
                            if (!SafetyGuard.IsSafeToDeleteFromDisk(d))
                            {
                                _logService.Log(LogLevel.Warning, $"Preset directory deletion blocked (protected): {d}");
                                continue;
                            }
                            Directory.Delete(d, true);
                        }
                    }
                }
                else if (Directory.Exists(dir))
                {
                    if (!SafetyGuard.IsSafeToDeleteFromDisk(dir))
                    {
                        _logService.Log(LogLevel.Warning, $"Preset directory deletion blocked (protected): {dir}");
                        continue;
                    }
                    Directory.Delete(dir, true);
                }
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Warning, $"Could not delete directory {Path.GetFileName(dir)}: {ex.Message}");
            }
            completed++;
            progress?.Report(((int)(completed * 100.0 / total), $"Removing directories ({completed}/{total})"));
        }

        // Disable services
        foreach (var svc in resolvedPlan.ServicesToDisable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _serviceManager.SetServiceStartTypeAsync(mountPath, svc, ServiceStartType.Disabled);
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Warning, $"Could not disable service {svc}: {ex.Message}");
            }
            completed++;
        }

        // Remove provisioned apps via DISM
        foreach (var appId in resolvedPlan.AppsToRemove)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _componentRemovalService.RemoveProvisionedAppAsync(mountPath, appId);
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Warning, $"Could not remove app {appId}: {ex.Message}");
            }
            completed++;
        }

        // Remove languages
        foreach (var lang in resolvedPlan.LanguagesToRemove)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _componentRemovalService.RemoveLanguageAsync(mountPath, lang);
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Warning, $"Could not remove language {lang}: {ex.Message}");
            }
            completed++;
        }

        progress?.Report((100, "NTLite-mapped removals complete"));
        _logService.Log(LogLevel.Success, $"NTLite-mapped removals complete ({resolvedPlan.TotalActions} actions)");
    }

    public async Task ApplyWallpapersAsync(
        string installMountPath,
        string? bootWimPath,
        bool isBootMounted,
        WallpaperPreset wallpapers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!string.IsNullOrEmpty(wallpapers.DesktopWallpaperPath) && File.Exists(wallpapers.DesktopWallpaperPath))
            {
                await _customizationService.SetDesktopWallpaperAsync(installMountPath, wallpapers.DesktopWallpaperPath);
                _logService.Log(LogLevel.Success, $"Desktop wallpaper set: {Path.GetFileName(wallpapers.DesktopWallpaperPath)}");
            }
            if (!string.IsNullOrEmpty(wallpapers.LockScreenPath) && File.Exists(wallpapers.LockScreenPath))
            {
                await _customizationService.SetLockScreenWallpaperAsync(installMountPath, wallpapers.LockScreenPath);
                _logService.Log(LogLevel.Success, $"Lock screen set: {Path.GetFileName(wallpapers.LockScreenPath)}");
            }
            if (!string.IsNullOrEmpty(wallpapers.SetupScreenPath) && File.Exists(wallpapers.SetupScreenPath))
            {
                // Setup screen goes into boot.wim
                if (isBootMounted && !string.IsNullOrEmpty(bootWimPath))
                {
                    await _customizationService.SetBootWimWallpaperAsync(bootWimPath, wallpapers.SetupScreenPath);
                    _logService.Log(LogLevel.Success, $"Boot/setup wallpaper set: {Path.GetFileName(wallpapers.SetupScreenPath)}");
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Warning, $"Wallpaper application failed: {ex.Message}");
        }
    }

    public async Task ApplyPresetServiceChangesAsync(
        string mountPath,
        List<ServicePreset> serviceChanges,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var presetChanges = new List<(string serviceName, ServiceStartType startType)>();
        foreach (var svc in serviceChanges)
        {
            var startType = svc.StartType switch
            {
                2 => ServiceStartType.Automatic,
                3 => ServiceStartType.Manual,
                4 => ServiceStartType.Disabled,
                5 => ServiceStartType.Remove,
                _ => ServiceStartType.Disabled
            };
            presetChanges.Add((svc.ServiceName, startType));
        }

        try
        {
            await _serviceManager.ConfigureServicesAsync(mountPath, presetChanges);
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Warning, $"Preset service configuration failed: {ex.Message}");
        }
        _logService.Log(LogLevel.Success, $"Applied {serviceChanges.Count} service change(s) from preset");
    }
}

using System.IO;
using System.Text.RegularExpressions;
using TrimKit.Models;

namespace TrimKit.Services;

public partial class DismService : IDismService
{
    private readonly ILogService _logService;

    public DismService(ILogService logService)
    {
        _logService = logService;
    }

    public async Task<List<WimImageInfo>> GetWimInfoAsync(string wimPath, CancellationToken cancellationToken = default)
    {
        var images = new List<WimImageInfo>();
        var output = await RunDismAsync($"/Get-WimInfo /WimFile:\"{wimPath}\"", cancellationToken);

        var indexBlocks = output.Split("Index : ", StringSplitOptions.RemoveEmptyEntries);

        foreach (var block in indexBlocks.Skip(1))
        {
            var info = new WimImageInfo();

            var indexMatch = IndexRegex().Match(block);
            if (indexMatch.Success)
                info.Index = int.Parse(indexMatch.Value);
            else
            {
                // First characters before newline are the index
                var firstLine = block.Split('\n')[0].Trim();
                if (int.TryParse(firstLine, out var idx))
                    info.Index = idx;
            }

            info.Name = ExtractField(block, "Name");
            info.Description = ExtractField(block, "Description");
            info.Architecture = ExtractField(block, "Architecture");
            info.Version = ExtractField(block, "Version");
            info.Edition = ExtractField(block, "Edition");

            var sizeStr = ExtractField(block, "Size");
            if (long.TryParse(sizeStr.Replace(",", "").Replace(" bytes", "").Trim(), out var size))
                info.Size = size;

            images.Add(info);
        }

        _logService.Log(LogLevel.Info, $"Found {images.Count} image(s) in WIM file");
        return images;
    }

    public async Task MountImageAsync(string wimPath, int imageIndex, string mountPath, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(mountPath);

        _logService.Log(LogLevel.Info, $"Mounting image index {imageIndex} to {mountPath}...");
        progress?.Report(10);

        await RunDismAsync($"/Mount-Wim /WimFile:\"{wimPath}\" /Index:{imageIndex} /MountDir:\"{mountPath}\"", cancellationToken);

        progress?.Report(100);
        _logService.Log(LogLevel.Success, "Image mounted successfully");
    }

    public async Task UnmountImageAsync(string mountPath, bool commitChanges, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var commitFlag = commitChanges ? "/Commit" : "/Discard";
        _logService.Log(LogLevel.Info, $"Unmounting image ({(commitChanges ? "saving changes" : "discarding changes")})...");
        progress?.Report(10);

        await RunDismAsync($"/Unmount-Wim /MountDir:\"{mountPath}\" {commitFlag}", cancellationToken);

        progress?.Report(100);
        _logService.Log(LogLevel.Success, "Image unmounted successfully");
    }

    public async Task<List<WindowsPackage>> GetPackagesAsync(string mountPath, CancellationToken cancellationToken = default)
    {
        var packages = new List<WindowsPackage>();
        var output = await RunDismAsync($"/Image:\"{mountPath}\" /Get-Packages", cancellationToken);

        var lines = output.Split('\n');
        WindowsPackage? current = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("Package Identity :"))
            {
                current = new WindowsPackage
                {
                    PackageName = trimmed["Package Identity :".Length..].Trim()
                };
            }
            else if (trimmed.StartsWith("State :") && current != null)
            {
                current.State = trimmed["State :".Length..].Trim();
            }
            else if (trimmed.StartsWith("Release Type :") && current != null)
            {
                current.ReleaseType = trimmed["Release Type :".Length..].Trim();
            }
            else if (trimmed.StartsWith("Install Time :") && current != null)
            {
                if (DateTime.TryParse(trimmed["Install Time :".Length..].Trim(), out var dt))
                    current.InstallTime = dt;

                // Install Time is typically the last field per package
                current.DisplayName = SimplifyPackageName(current.PackageName);
                packages.Add(current);
                current = null;
            }
        }

        _logService.Log(LogLevel.Info, $"Found {packages.Count} packages");
        return packages;
    }

    public async Task RemovePackageAsync(string mountPath, string packageName, CancellationToken cancellationToken = default)
    {
        _logService.Log(LogLevel.Info, $"Removing package: {SimplifyPackageName(packageName)}");
        await RunDismAsync($"/Image:\"{mountPath}\" /Remove-Package /PackageName:\"{packageName}\"", cancellationToken);
        _logService.Log(LogLevel.Success, $"Removed: {SimplifyPackageName(packageName)}");
    }

    public async Task<List<WindowsFeature>> GetFeaturesAsync(string mountPath, CancellationToken cancellationToken = default)
    {
        var features = new List<WindowsFeature>();
        var output = await RunDismAsync($"/Image:\"{mountPath}\" /Get-Features", cancellationToken);

        var lines = output.Split('\n');
        WindowsFeature? current = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("Feature Name :"))
            {
                current = new WindowsFeature
                {
                    FeatureName = trimmed["Feature Name :".Length..].Trim()
                };
                current.DisplayName = current.FeatureName;
            }
            else if (trimmed.StartsWith("State :") && current != null)
            {
                current.State = trimmed["State :".Length..].Trim();
                current.IsEnabled = current.State.Contains("Enable", StringComparison.OrdinalIgnoreCase);
                current.OriginalState = current.IsEnabled;
                features.Add(current);
                current = null;
            }
        }

        _logService.Log(LogLevel.Info, $"Found {features.Count} features");
        return features;
    }

    public async Task EnableFeatureAsync(string mountPath, string featureName, CancellationToken cancellationToken = default)
    {
        _logService.Log(LogLevel.Info, $"Enabling feature: {featureName}");
        await RunDismAsync($"/Image:\"{mountPath}\" /Enable-Feature /FeatureName:\"{featureName}\"", cancellationToken);
        _logService.Log(LogLevel.Success, $"Enabled: {featureName}");
    }

    public async Task DisableFeatureAsync(string mountPath, string featureName, CancellationToken cancellationToken = default)
    {
        _logService.Log(LogLevel.Info, $"Disabling feature: {featureName}");
        await RunDismAsync($"/Image:\"{mountPath}\" /Disable-Feature /FeatureName:\"{featureName}\"", cancellationToken);
        _logService.Log(LogLevel.Success, $"Disabled: {featureName}");
    }

    public async Task AddDriverAsync(string mountPath, string driverPath, bool recurse = true, bool forceUnsigned = false, CancellationToken cancellationToken = default)
    {
        var recurseFlag = recurse ? "/Recurse" : "";
        var unsignedFlag = forceUnsigned ? "/ForceUnsigned" : "";
        _logService.Log(LogLevel.Info, $"Adding driver(s) from: {driverPath}{(forceUnsigned ? " (forcing unsigned)" : "")}");
        await RunDismAsync($"/Image:\"{mountPath}\" /Add-Driver /Driver:\"{driverPath}\" {recurseFlag} {unsignedFlag}", cancellationToken);
        _logService.Log(LogLevel.Success, "Driver(s) added successfully");
    }

    public async Task<string> GetMountedImageStatus(string mountPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var output = await RunDismAsync("/Get-MountedWimInfo", cancellationToken);
            return output.Contains(mountPath, StringComparison.OrdinalIgnoreCase)
                ? "Mounted"
                : "Not Mounted";
        }
        catch
        {
            return "Not Mounted";
        }
    }

    public async Task CleanupMountsAsync(CancellationToken cancellationToken = default)
    {
        _logService.Log(LogLevel.Info, "Cleaning up abandoned mounts...");
        await RunDismAsync("/Cleanup-Wim", cancellationToken);
        _logService.Log(LogLevel.Success, "Cleanup complete");
    }

    private async Task<string> RunDismAsync(string arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ProcessRunner.RunDismAsync(arguments, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            _logService.Log(LogLevel.Error, $"DISM error: {ex.Message}");
            throw;
        }
    }

    private static string ExtractField(string block, string fieldName)
    {
        var pattern = $@"{fieldName}\s*:\s*(.+)";
        var match = Regex.Match(block, pattern);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    private static string SimplifyPackageName(string packageName)
    {
        // Simplify long package names for display
        var parts = packageName.Split('~');
        return parts.Length > 0 ? parts[0] : packageName;
    }

    [GeneratedRegex(@"^\d+")]
    private static partial Regex IndexRegex();
}

using System.IO;
using System.Text.RegularExpressions;
using TrimKit.Models;

namespace TrimKit.Services;

public partial class ComponentRemovalService : IComponentRemovalService
{
    private readonly ILogService _logService;

    public ComponentRemovalService(ILogService logService)
    {
        _logService = logService;
    }

    #region Discovery

    public async Task<List<RemovableComponent>> GetProvisionedAppsAsync(string mountPath)
    {
        var apps = new List<RemovableComponent>();
        var output = await RunDismAsync($"/Image:\"{mountPath}\" /Get-ProvisionedAppxPackages");

        RemovableComponent? current = null;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("PackageName :"))
            {
                current = new RemovableComponent
                {
                    Id = trimmed["PackageName :".Length..].Trim(),
                    Category = "Apps",
                    Type = ComponentType.ProvisionedApp
                };
            }
            else if (trimmed.StartsWith("DisplayName :") && current != null)
            {
                current.DisplayName = trimmed["DisplayName :".Length..].Trim();
                if (string.IsNullOrEmpty(current.DisplayName))
                    current.DisplayName = SimplifyAppName(current.Id);
                apps.Add(current);
                current = null;
            }
        }

        _logService.Log(LogLevel.Info, $"Found {apps.Count} provisioned app(s)");
        return apps;
    }

    public async Task<List<RemovableComponent>> GetCapabilitiesAsync(string mountPath)
    {
        var caps = new List<RemovableComponent>();
        var output = await RunDismAsync($"/Image:\"{mountPath}\" /Get-Capabilities");

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Capability Identity :"))
            {
                var id = trimmed["Capability Identity :".Length..].Trim();
                caps.Add(new RemovableComponent
                {
                    Id = id,
                    DisplayName = SimplifyCapabilityName(id),
                    Category = CategorizeCapability(id),
                    Type = ComponentType.Capability,
                    IsProtected = SafetyGuard.IsAbsolutelyCritical(id, ComponentType.Capability)
                });
            }
        }

        _logService.Log(LogLevel.Info, $"Found {caps.Count} capability(ies)");
        return caps;
    }

    public async Task<List<RemovableComponent>> GetOptionalFeaturesAsync(string mountPath)
    {
        var features = new List<RemovableComponent>();
        var output = await RunDismAsync($"/Image:\"{mountPath}\" /Get-Features");

        string? featureName = null;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Feature Name :"))
            {
                featureName = trimmed["Feature Name :".Length..].Trim();
            }
            else if (trimmed.StartsWith("State :") && featureName != null)
            {
                var state = trimmed["State :".Length..].Trim();
                features.Add(new RemovableComponent
                {
                    Id = featureName,
                    DisplayName = featureName,
                    Category = "Optional Features",
                    Type = ComponentType.OptionalFeature,
                    Description = state
                });
                featureName = null;
            }
        }

        _logService.Log(LogLevel.Info, $"Found {features.Count} optional feature(s)");
        return features;
    }

    public Task<List<RemovableComponent>> GetFontsAsync(string mountPath)
    {
        var fonts = new List<RemovableComponent>();
        var fontsDir = Path.Combine(mountPath, @"Windows\Fonts");

        if (Directory.Exists(fontsDir))
        {
            foreach (var file in Directory.GetFiles(fontsDir, "*.ttf")
                .Concat(Directory.GetFiles(fontsDir, "*.ttc"))
                .Concat(Directory.GetFiles(fontsDir, "*.otf")))
            {
                var info = new FileInfo(file);
                fonts.Add(new RemovableComponent
                {
                    Id = info.Name,
                    DisplayName = Path.GetFileNameWithoutExtension(info.Name),
                    Category = "Fonts",
                    Type = ComponentType.Font,
                    Size = info.Length,
                    IsProtected = SafetyGuard.IsAbsolutelyCritical(info.Name, ComponentType.Font) || IsProtectedFont(info.Name)
                });
            }
        }

        _logService.Log(LogLevel.Info, $"Found {fonts.Count} font(s)");
        return Task.FromResult(fonts.OrderBy(f => f.DisplayName).ToList());
    }

    public Task<List<RemovableComponent>> GetKeyboardLayoutsAsync(string mountPath)
    {
        var layouts = new List<RemovableComponent>();
        var kbdDir = Path.Combine(mountPath, @"Windows\System32");

        if (Directory.Exists(kbdDir))
        {
            foreach (var file in Directory.GetFiles(kbdDir, "kbd*.dll"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                layouts.Add(new RemovableComponent
                {
                    Id = name,
                    DisplayName = HumanizeKeyboardName(name),
                    Category = "Keyboard Layouts",
                    Type = ComponentType.KeyboardLayout,
                    Size = new FileInfo(file).Length
                });
            }
        }

        _logService.Log(LogLevel.Info, $"Found {layouts.Count} keyboard layout(s)");
        return Task.FromResult(layouts.OrderBy(l => l.DisplayName).ToList());
    }

    public async Task<List<RemovableComponent>> GetLanguagesAsync(string mountPath)
    {
        var langs = new List<RemovableComponent>();
        var output = await RunDismAsync($"/Image:\"{mountPath}\" /Get-Intl");

        // Also check for language packs in the image
        var langDir = Path.Combine(mountPath, @"Windows\System32");
        if (Directory.Exists(langDir))
        {
            // Look for locale .nls files and MUI directories
            var muiDir = Path.Combine(mountPath, @"Windows\System32");
            foreach (var dir in Directory.GetDirectories(muiDir).Where(d =>
                LangTagRegex().IsMatch(Path.GetFileName(d))))
            {
                var tag = Path.GetFileName(dir);
                langs.Add(new RemovableComponent
                {
                    Id = tag,
                    DisplayName = tag,
                    Category = "Languages",
                    Type = ComponentType.Language,
                    IsProtected = SafetyGuard.IsAbsolutelyCritical(tag, ComponentType.Language)
                });
            }
        }

        _logService.Log(LogLevel.Info, $"Found {langs.Count} language resource(s)");
        return langs;
    }

    public Task<List<RemovableComponent>> GetInboxDriversAsync(string mountPath)
    {
        var drivers = new List<RemovableComponent>();
        var driverStore = Path.Combine(mountPath, @"Windows\System32\DriverStore\FileRepository");

        if (Directory.Exists(driverStore))
        {
            foreach (var dir in Directory.GetDirectories(driverStore))
            {
                var dirName = Path.GetFileName(dir);
                var infFiles = Directory.GetFiles(dir, "*.inf");
                if (infFiles.Length > 0)
                {
                    var totalSize = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                        .Sum(f => new FileInfo(f).Length);

                    drivers.Add(new RemovableComponent
                    {
                        Id = dirName,
                        DisplayName = dirName.Split('_')[0], // e.g., "prnms001_..."  → "prnms001"
                        Category = CategorizeDriver(dirName),
                        Type = ComponentType.InboxDriver,
                        Size = totalSize
                    });
                }
            }
        }

        _logService.Log(LogLevel.Info, $"Found {drivers.Count} inbox driver package(s)");
        return Task.FromResult(drivers.OrderBy(d => d.Category).ThenBy(d => d.DisplayName).ToList());
    }

    #endregion

    #region Removal Actions

    public async Task RemoveProvisionedAppAsync(string mountPath, string packageName)
    {
        await RunDismAsync($"/Image:\"{mountPath}\" /Remove-ProvisionedAppxPackage /PackageName:\"{packageName}\"");
        _logService.Log(LogLevel.Success, $"Removed app: {SimplifyAppName(packageName)}");
    }

    public async Task RemoveCapabilityAsync(string mountPath, string capabilityName)
    {
        await RunDismAsync($"/Image:\"{mountPath}\" /Remove-Capability /CapabilityName:\"{capabilityName}\"");
        _logService.Log(LogLevel.Success, $"Removed capability: {SimplifyCapabilityName(capabilityName)}");
    }

    public async Task DisableFeatureAsync(string mountPath, string featureName)
    {
        await RunDismAsync($"/Image:\"{mountPath}\" /Disable-Feature /FeatureName:\"{featureName}\"");
        _logService.Log(LogLevel.Success, $"Disabled feature: {featureName}");
    }

    public Task RemoveFontAsync(string mountPath, string fontFileName)
    {
        var fontPath = Path.Combine(mountPath, @"Windows\Fonts", fontFileName);
        if (File.Exists(fontPath))
        {
            if (!SafetyGuard.IsSafeToDeleteFromDisk(fontPath))
            {
                _logService.Log(LogLevel.Warning, $"Protected font file delete blocked: {fontFileName}");
                return Task.CompletedTask;
            }
            File.Delete(fontPath);
            _logService.Log(LogLevel.Success, $"Removed font: {fontFileName}");
        }
        return Task.CompletedTask;
    }

    public Task RemoveKeyboardLayoutAsync(string mountPath, string layoutId)
    {
        // Remove both 32-bit and 64-bit DLLs
        var paths = new[]
        {
            Path.Combine(mountPath, @"Windows\System32", $"{layoutId}.dll"),
            Path.Combine(mountPath, @"Windows\SysWOW64", $"{layoutId}.dll"),
        };

        foreach (var path in paths)
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        _logService.Log(LogLevel.Success, $"Removed keyboard layout: {layoutId}");
        return Task.CompletedTask;
    }

    public async Task RemoveLanguageAsync(string mountPath, string languageTag)
    {
        if (languageTag.Equals("en-US", StringComparison.OrdinalIgnoreCase) || languageTag.Equals("en-us", StringComparison.OrdinalIgnoreCase))
        {
            _logService.Log(LogLevel.Warning, "Protected language pack en-US removal blocked");
            return;
        }

        // Try DISM first (for full language packs)
        try
        {
            await RunDismAsync($"/Image:\"{mountPath}\" /Remove-Package /PackageName:Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~{languageTag}~");
        }
        catch
        {
            // Fall back to removing MUI directory
            var muiDir = Path.Combine(mountPath, @"Windows\System32", languageTag);
            if (Directory.Exists(muiDir))
            {
                if (!SafetyGuard.IsSafeToDeleteFromDisk(muiDir))
                {
                    _logService.Log(LogLevel.Warning, $"Protected language directory delete blocked: {languageTag}");
                    return;
                }
                Directory.Delete(muiDir, true);
            }
        }

        _logService.Log(LogLevel.Success, $"Removed language: {languageTag}");
    }

    public Task RemoveInboxDriverAsync(string mountPath, string infName)
    {
        var driverStore = Path.Combine(mountPath, @"Windows\System32\DriverStore\FileRepository");
        var targetDir = Path.Combine(driverStore, infName);

        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, true);
            _logService.Log(LogLevel.Success, $"Removed driver: {infName}");
        }

        return Task.CompletedTask;
    }

    #endregion

    #region WinRE Operations

    public Task RemoveWinREAsync(string mountPath)
    {
        var winrePath = Path.Combine(mountPath, @"Windows\System32\Recovery\Winre.wim");
        if (File.Exists(winrePath))
        {
            var size = new FileInfo(winrePath).Length / (1024.0 * 1024.0);
            File.Delete(winrePath);
            _logService.Log(LogLevel.Success, $"Removed WinRE.wim ({size:F0} MB freed)");
        }
        else
        {
            _logService.Log(LogLevel.Warning, "WinRE.wim not found");
        }
        return Task.CompletedTask;
    }

    public async Task StripWinREAsync(string mountPath)
    {
        var winrePath = Path.Combine(mountPath, @"Windows\System32\Recovery\Winre.wim");
        if (!File.Exists(winrePath))
        {
            _logService.Log(LogLevel.Warning, "WinRE.wim not found");
            return;
        }

        // Mount WinRE, remove optional recovery tools, unmount
        var winreMountDir = Path.Combine(Path.GetTempPath(), "TrimKit_WinRE");
        Directory.CreateDirectory(winreMountDir);

        try
        {
            await RunDismAsync($"/Mount-Wim /WimFile:\"{winrePath}\" /Index:1 /MountDir:\"{winreMountDir}\"");

            // Remove optional components from WinRE
            var toRemove = new[]
            {
                "WinPE-WMI", "WinPE-NetFx", "WinPE-Scripting",
                "WinPE-PowerShell", "WinPE-StorageWMI", "WinPE-DismCmdlets"
            };

            foreach (var pkg in toRemove)
            {
                try
                {
                    await RunDismAsync($"/Image:\"{winreMountDir}\" /Remove-Package /PackageName:{pkg}*");
                }
                catch { /* Some won't exist, that's fine */ }
            }

            await RunDismAsync($"/Unmount-Wim /MountDir:\"{winreMountDir}\" /Commit");

            // Rebuild/compress WinRE
            var tempWinre = winrePath + ".tmp";
            await RunDismAsync($"/Export-Image /SourceImageFile:\"{winrePath}\" /SourceIndex:1 /DestinationImageFile:\"{tempWinre}\" /Compress:Max");
            File.Delete(winrePath);
            File.Move(tempWinre, winrePath);

            _logService.Log(LogLevel.Success, "WinRE stripped and recompressed");
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"WinRE strip failed: {ex.Message}");
            try { await RunDismAsync($"/Unmount-Wim /MountDir:\"{winreMountDir}\" /Discard"); } catch { }
        }
        finally
        {
            try { Directory.Delete(winreMountDir, true); } catch { }
        }
    }

    #endregion

    #region Bulk Operations

    public async Task RemoveAllAsync(string mountPath, List<RemovableComponent> components, IProgress<(int percent, string status)>? progress = null)
    {
        // SAFETY: Validate all components through the SafetyGuard before removal
        var (safe, blocked) = SafetyGuard.ValidateRemovalList(components);

        if (blocked.Count > 0)
        {
            _logService.Log(LogLevel.Warning,
                $"SafetyGuard blocked {blocked.Count} critical component(s) from removal: " +
                string.Join(", ", blocked.Take(5).Select(b => b.DisplayName)) +
                (blocked.Count > 5 ? $" (+{blocked.Count - 5} more)" : ""));
        }

        if (safe.Count == 0)
        {
            _logService.Log(LogLevel.Info, "No safe components to remove after safety validation");
            return;
        }

        _logService.Log(LogLevel.Info, $"Removing {safe.Count} component(s) (safety-validated)...");

        for (int i = 0; i < safe.Count; i++)
        {
            var comp = safe[i];
            var pct = (int)((i + 1.0) / safe.Count * 100);
            progress?.Report((pct, $"[{i + 1}/{safe.Count}] {comp.DisplayName}"));

            try
            {
                switch (comp.Type)
                {
                    case ComponentType.ProvisionedApp:
                        await RemoveProvisionedAppAsync(mountPath, comp.Id);
                        break;
                    case ComponentType.Capability:
                        await RemoveCapabilityAsync(mountPath, comp.Id);
                        break;
                    case ComponentType.OptionalFeature:
                        await DisableFeatureAsync(mountPath, comp.Id);
                        break;
                    case ComponentType.Font:
                        await RemoveFontAsync(mountPath, comp.Id);
                        break;
                    case ComponentType.KeyboardLayout:
                        await RemoveKeyboardLayoutAsync(mountPath, comp.Id);
                        break;
                    case ComponentType.Language:
                        await RemoveLanguageAsync(mountPath, comp.Id);
                        break;
                    case ComponentType.InboxDriver:
                        await RemoveInboxDriverAsync(mountPath, comp.Id);
                        break;
                    case ComponentType.WinRE:
                        await RemoveWinREAsync(mountPath);
                        break;
                    default:
                        _logService.Log(LogLevel.Warning, $"Unknown component type for: {comp.DisplayName}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Warning, $"Could not remove {comp.DisplayName}: {ex.Message}");
            }
        }

        _logService.Log(LogLevel.Success, $"Removed {safe.Count} component(s)");
    }

    #endregion

    #region Helpers

    private static string SimplifyAppName(string packageName)
    {
        // "Microsoft.BingWeather_4.25.20211.0_neutral_~_8wekyb3d8bbwe" → "BingWeather"
        var match = AppNameRegex().Match(packageName);
        return match.Success ? match.Groups[1].Value : packageName.Split('_')[0];
    }

    private static string SimplifyCapabilityName(string capName)
    {
        // "Language.Basic~~~en-US~0.0.1.0" → "Language.Basic (en-US)"
        var parts = capName.Split('~');
        var name = parts[0];
        var lang = parts.Length >= 4 ? parts[3] : "";
        return string.IsNullOrEmpty(lang) ? name : $"{name} ({lang})";
    }

    private static string CategorizeCapability(string capName)
    {
        if (capName.StartsWith("Language.", StringComparison.OrdinalIgnoreCase)) return "Languages";
        if (capName.StartsWith("Browser.", StringComparison.OrdinalIgnoreCase)) return "Browser";
        if (capName.StartsWith("Media.", StringComparison.OrdinalIgnoreCase)) return "Multimedia";
        if (capName.StartsWith("Print.", StringComparison.OrdinalIgnoreCase)) return "Printing";
        if (capName.StartsWith("Network.", StringComparison.OrdinalIgnoreCase)) return "Network";
        if (capName.Contains("Math", StringComparison.OrdinalIgnoreCase)) return "Accessories";
        if (capName.Contains("Notepad", StringComparison.OrdinalIgnoreCase)) return "Accessories";
        if (capName.Contains("Paint", StringComparison.OrdinalIgnoreCase)) return "Accessories";
        if (capName.Contains("WordPad", StringComparison.OrdinalIgnoreCase)) return "Accessories";
        return "Capabilities";
    }

    private static string CategorizeDriver(string dirName)
    {
        var lower = dirName.ToLowerInvariant();
        if (lower.Contains("prn") || lower.Contains("print")) return "Drivers - Printers";
        if (lower.Contains("net") || lower.Contains("wifi") || lower.Contains("wlan")) return "Drivers - Network";
        if (lower.Contains("usb")) return "Drivers - USB";
        if (lower.Contains("hid") || lower.Contains("input")) return "Drivers - Input";
        if (lower.Contains("display") || lower.Contains("video") || lower.Contains("gpu")) return "Drivers - Display";
        if (lower.Contains("audio") || lower.Contains("sound")) return "Drivers - Audio";
        if (lower.Contains("stor") || lower.Contains("disk") || lower.Contains("nvme")) return "Drivers - Storage";
        if (lower.Contains("bt") || lower.Contains("bluetooth")) return "Drivers - Bluetooth";
        if (lower.Contains("cam") || lower.Contains("sensor")) return "Drivers - Sensors";
        return "Drivers - Other";
    }

    private static string HumanizeKeyboardName(string name)
    {
        // "kbdus" → "US", "kbdfr" → "French"  
        return name.Replace("kbd", "").ToUpperInvariant();
    }

    private static bool IsProtectedFont(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        // Fonts critical for Windows UI rendering
        return lower.Contains("segoeui") || lower.Contains("seguisym") ||
               lower.Contains("segoeuisl") || lower == "arial.ttf" ||
               lower.Contains("marlett");
    }

    private async Task<string> RunDismAsync(string arguments, CancellationToken cancellationToken = default)
    {
        return await ProcessRunner.RunDismAsync(arguments, cancellationToken);
    }

    [GeneratedRegex(@"\.(\w+?)_")]
    private static partial Regex AppNameRegex();

    [GeneratedRegex(@"^[a-z]{2}(-[A-Z]{2})?$")]
    private static partial Regex LangTagRegex();

    #endregion
}

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrimKit.Models;
using TrimKit.Services;

namespace TrimKit.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IDismService _dismService;
    private readonly IRegistryService _registryService;
    private readonly IPresetService _presetService;
    private readonly ILogService _logService;
    private readonly IIsoService _isoService;
    private readonly IWindowsServiceManager _serviceManager;
    private readonly IImageToolsService _imageToolsService;
    private readonly IUnattendService _unattendService;
    private readonly ICustomizationService _customizationService;
    private readonly IComponentRemovalService _componentRemovalService;
    private readonly IWinSxsCleanupService _winSxsCleanupService;
    private readonly IUpdateCatalogService _updateCatalogService;
    private readonly IDialogService _dialogService;
    private readonly IApplyService _applyService;

    [ObservableProperty] private string _wimFilePath = string.Empty;
    [ObservableProperty] private string _mountPath = string.Empty;
    [ObservableProperty] private bool _isMounted;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private int _progressValue;
    [ObservableProperty] private WimImageInfo? _selectedImage;
    [ObservableProperty] private string _selectedTab = "Packages";
    [ObservableProperty] private int _selectedTabIndex;

    partial void OnSelectedTabIndexChanged(int value)
    {
        // Lazy-load services when user navigates to Services tab (index 8)
        if (value == 8 && IsMounted && Services.Count == 0)
        {
            LoadServicesAsync().SafeFireAndForget(_logService, "Lazy-load services");
        }
    }

    // New workflow state
    [ObservableProperty] private string _workFolder = string.Empty;
    [ObservableProperty] private string _isoFilePath = string.Empty;
    [ObservableProperty] private string _installWimPath = string.Empty;
    [ObservableProperty] private string _bootWimPath = string.Empty;
    [ObservableProperty] private string _installMountPath = string.Empty;
    [ObservableProperty] private string _bootMountPath = string.Empty;
    [ObservableProperty] private bool _isInstallMounted;
    [ObservableProperty] private bool _isBootMounted;
    [ObservableProperty] private bool _forceUnsignedDrivers;

    // Preset-loaded customization (wallpapers, service changes from WinReducer)
    private WallpaperPreset? _loadedWallpapers;
    private List<ServicePreset> _loadedServiceChanges = [];
    private CancellationTokenSource? _cts;

    public ObservableCollection<WimImageInfo> WimImages { get; } = [];
    public ObservableCollection<WindowsPackage> Packages { get; } = [];
    public ObservableCollection<WindowsFeature> Features { get; } = [];
    public ObservableCollection<RegistryTweak> RegistryTweaks { get; } = [];
    public ObservableCollection<string> DriverPaths { get; } = [];
    public ObservableCollection<OperationLog> LogEntries { get; } = [];

    // Component removal collections (populated on mount)
    public ObservableCollection<RemovableComponent> ProvisionedApps { get; } = [];
    public ObservableCollection<RemovableComponent> Capabilities { get; } = [];
    public ObservableCollection<RemovableComponent> Fonts { get; } = [];
    public ObservableCollection<RemovableComponent> KeyboardLayouts { get; } = [];
    public ObservableCollection<RemovableComponent> Languages { get; } = [];
    public ObservableCollection<RemovableComponent> InboxDrivers { get; } = [];
    public ObservableCollection<WindowsServiceInfo> Services { get; } = [];
    public ObservableCollection<CatalogUpdate> AvailableUpdates { get; } = [];

    // Autounattend generator config (bound to UI controls)
    public UnattendConfig UnattendConfig { get; } = new();

    // Safety/Compatibility options (bound to checkboxes)
    public ObservableCollection<CompatibilityOption> CompatibilityOptions { get; } = [];
    public ObservableCollection<BootCompatibilityOption> BootCompatibilityOptions { get; } = [];

    public DownloadViewModel DownloadViewModel { get; }

    public MainViewModel(IDismService dismService, IRegistryService registryService,
        IPresetService presetService, ILogService logService, DownloadViewModel downloadViewModel,
        IIsoService isoService, IWindowsServiceManager serviceManager,
        IImageToolsService imageToolsService, IUnattendService unattendService,
        ICustomizationService customizationService,
        IComponentRemovalService componentRemovalService,
        IWinSxsCleanupService winSxsCleanupService,
        IUpdateCatalogService updateCatalogService,
        IDialogService dialogService,
        IApplyService applyService)
    {
        _dismService = dismService;
        _registryService = registryService;
        _presetService = presetService;
        _logService = logService;
        _isoService = isoService;
        _serviceManager = serviceManager;
        _imageToolsService = imageToolsService;
        _unattendService = unattendService;
        _customizationService = customizationService;
        _componentRemovalService = componentRemovalService;
        _winSxsCleanupService = winSxsCleanupService;
        _updateCatalogService = updateCatalogService;
        _dialogService = dialogService;
        _applyService = applyService;
        DownloadViewModel = downloadViewModel;

        _logService.LogAdded += OnLogAdded;

        // Load built-in registry tweaks
        foreach (var tweak in _registryService.GetBuiltInTweaks())
        {
            RegistryTweaks.Add(tweak);
        }

        // Load compatibility protection options
        foreach (var opt in SafetyGuard.GetDefaultCompatibilityOptions())
            CompatibilityOptions.Add(opt);
        foreach (var opt in BootWimSafetyGuard.GetDefaultBootCompatibilityOptions())
            BootCompatibilityOptions.Add(opt);

        // Default mount path — use temp drive instead of assuming C:
        MountPath = Path.Combine(Path.GetTempPath(), "TrimKitMount");
    }

    private void OnLogAdded(OperationLog log)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() => LogEntries.Add(log));
    }

    [RelayCommand]
    private async Task BrowseWimAsync()
    {
        if (IsBusy) return;

        var filePath = _dialogService.OpenFile(
            "ISO Files (*.iso)|*.iso|WIM Files (*.wim)|*.wim|ESD Files (*.esd)|*.esd|All Files (*.*)|*.*",
            "Select Windows Image File");

        if (filePath != null)
        {
            var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".iso")
            {
                await HandleIsoWorkflowAsync(filePath);
            }
            else if (ext == ".esd")
            {
                await ConvertEsdAndLoadAsync(filePath);
            }
            else
            {
                WimFilePath = filePath;
                await LoadWimInfoAsync();
            }
        }
    }

    /// <summary>
    /// Full ISO workflow:
    /// 1. Mount ISO via Explorer (suppressed)
    /// 2. Copy all ISO content to NTFS temp folder
    /// 3. Unmount ISO
    /// 4. Check if install.wim/esd is recovery compressed → convert if needed
    /// 5. Show editions to user for selection
    /// 6. After user picks edition → extract that edition to its own folder
    /// 7. Extract boot.wim to its own folder
    /// 8. Both are ready for independent debloating with separate safety guards
    /// </summary>
    private async Task HandleIsoWorkflowAsync(string isoPath)
    {
        try
        {
            IsBusy = true;
            _cts = new CancellationTokenSource();
            IsoFilePath = isoPath;

            // Step 1: Mount ISO via Explorer (suppress the auto-opened Explorer window)
            StatusText = "Mounting ISO...";
            _logService.Log(LogLevel.Info, $"Starting ISO workflow for: {Path.GetFileName(isoPath)}");
            var mountedDrive = await _isoService.MountIsoSuppressedAsync(isoPath);

            // Step 2: Copy ISO content to NTFS temp work folder
            StatusText = "Copying ISO content to work folder (NTFS drive)...";
            var progress = new Progress<(int percent, string status)>(p =>
            {
                ProgressValue = p.percent;
                StatusText = p.status;
            });

            WorkFolder = await _isoService.CopyIsoToWorkFolderAsync(mountedDrive, progress);

            // Step 3: Unmount ISO (we have a full copy now)
            StatusText = "Unmounting ISO...";
            await _isoService.UnmountIsoAsync(isoPath);
            _logService.Log(LogLevel.Success, "ISO unmounted — working from local copy");

            // Step 3.5: Debloat ISO file structure (remove unneeded files with safety guard)
            StatusText = "Analyzing ISO files for debloat...";
            var debloatPlan = IsoFileSafetyGuard.AnalyzeWorkFolder(WorkFolder);
            _logService.Log(LogLevel.Info,
                $"ISO analysis: {debloatPlan.Critical.Count} critical, {debloatPlan.Protected.Count} protected, " +
                $"{debloatPlan.SafeToRemove.Count} removable ({debloatPlan.TotalSavingsDisplay})");

            if (debloatPlan.SafeToRemove.Count > 0)
            {
                StatusText = $"Debloating ISO structure ({debloatPlan.TotalSavingsDisplay} to free)...";
                IsoFileSafetyGuard.ExecuteDebloatPlan(WorkFolder, debloatPlan, _logService, progress);
            }

            // Step 4: Find install image and check compression
            var installImage = await _isoService.FindInstallImageAsync(WorkFolder);
            if (installImage == null)
            {
                StatusText = "Error: No install.wim or install.esd found in ISO";
                _logService.Log(LogLevel.Error, "No install.wim/esd found in work folder sources directory");
                return;
            }

            // Check if it's recovery compressed
            StatusText = "Checking image compression format...";
            var isRecovery = await _isoService.IsRecoveryCompressedAsync(installImage);

            if (isRecovery)
            {
                StatusText = "Recovery compression detected — converting to normal WIM...";
                _logService.Log(LogLevel.Info, "install image is in recovery (LZMS) format — converting...");

                installImage = await _isoService.ConvertToNormalWimAsync(installImage, progress);
                _logService.Log(LogLevel.Success, "Converted to standard WIM compression");
            }

            // Step 5: Load editions for user to pick
            WimFilePath = installImage;
            StatusText = "Reading editions...";
            WimImages.Clear();
            var images = await _dismService.GetWimInfoAsync(installImage);
            foreach (var img in images)
            {
                WimImages.Add(img);
            }

            if (WimImages.Count > 0)
                SelectedImage = WimImages[0];

            StatusText = $"Found {WimImages.Count} edition(s) — select one and click Mount to extract & prepare for debloating";
            _logService.Log(LogLevel.Success, $"ISO ready: {WimImages.Count} edition(s) available. Select edition and mount.");
        }
        catch (Exception ex)
        {
            StatusText = $"ISO workflow failed: {ex.Message}";
            _logService.Log(LogLevel.Error, $"ISO workflow failed: {ex.Message}");
            _dialogService.ShowError($"ISO workflow failed:\n{ex.Message}", "TrimKit");
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task ConvertEsdAndLoadAsync(string esdPath)
    {
        try
        {
            IsBusy = true;
            StatusText = "Converting ESD (recovery format) to WIM for editing...";
            _logService.Log(Models.LogLevel.Info, $"Converting: {Path.GetFileName(esdPath)}");

            var wimPath = Path.ChangeExtension(esdPath, ".wim");
            var progress = new Progress<(int percent, string status)>(p =>
            {
                ProgressValue = p.percent;
                StatusText = p.status;
            });

            await _imageToolsService.ConvertEsdToWimAsync(esdPath, wimPath, progress);

            if (File.Exists(wimPath))
            {
                WimFilePath = wimPath;
                await LoadWimInfoAsync();
                StatusText = "ESD converted — WIM ready for customization";
            }
            else
            {
                // Fall back to loading ESD directly
                WimFilePath = esdPath;
                await LoadWimInfoAsync();
                StatusText = "Warning: conversion failed, loaded as read-only ESD";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"ESD conversion failed: {ex.Message}";
            _logService.Log(Models.LogLevel.Error, ex.Message);
            // Still try to load it
            WimFilePath = esdPath;
            await LoadWimInfoAsync();
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
        }
    }    private async Task ExtractFromIsoAsync(string isoPath)
    {
        await HandleIsoWorkflowAsync(isoPath);
    }

    [RelayCommand]
    private void BrowseMountPath()
    {
        var folder = _dialogService.OpenFolder("Select Mount Directory");
        if (folder != null)
        {
            MountPath = folder;
        }
    }

    private async Task LoadWimInfoAsync()
    {
        if (string.IsNullOrWhiteSpace(WimFilePath) || !File.Exists(WimFilePath))
            return;

        try
        {
            IsBusy = true;
            StatusText = "Reading image file...";

            // Detect if this is actually a recovery-compressed ESD (even if named .wim)
            if (await IsRecoveryFormatAsync(WimFilePath))
            {
                StatusText = "Detected recovery/ESD compression — converting to standard WIM...";
                _logService.Log(LogLevel.Info, $"{Path.GetFileName(WimFilePath)} is in recovery (LZMS) format. Converting...");

                var convertedPath = Path.Combine(
                    Path.GetDirectoryName(WimFilePath)!,
                    Path.GetFileNameWithoutExtension(WimFilePath) + "_converted.wim");

                var progress = new Progress<(int percent, string status)>(p =>
                {
                    ProgressValue = p.percent;
                    StatusText = p.status;
                });

                await _imageToolsService.ConvertEsdToWimAsync(WimFilePath, convertedPath, progress);

                if (File.Exists(convertedPath) && new FileInfo(convertedPath).Length > 1024)
                {
                    WimFilePath = convertedPath;
                    _logService.Log(LogLevel.Success, "Converted to standard WIM format");
                }
                else
                {
                    _logService.Log(LogLevel.Warning, "Conversion produced empty file — loading original (may be read-only)");
                }
            }

            StatusText = "Reading WIM file...";
            WimImages.Clear();
            var images = await _dismService.GetWimInfoAsync(WimFilePath);
            foreach (var img in images)
            {
                WimImages.Add(img);
            }

            if (WimImages.Count > 0)
                SelectedImage = WimImages[0];

            StatusText = $"Found {WimImages.Count} image(s) — ready to mount";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            _logService.Log(LogLevel.Error, "Failed to read WIM file", ex.Message);
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
        }
    }

    /// <summary>
    /// Checks if a WIM/ESD file uses recovery (LZMS) compression by parsing DISM output.
    /// Files in recovery format cannot be mounted for editing — they must be converted first.
    /// </summary>
    private async Task<bool> IsRecoveryFormatAsync(string filePath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dism.exe",
                Arguments = $"/Get-WimInfo /WimFile:\"{filePath}\" /Index:1",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new System.Diagnostics.Process { StartInfo = psi };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            // DISM reports compression as "Recovery" for LZMS-compressed ESDs
            // Normal WIMs show "LZX" or "XPRESS" or "None"
            if (output.Contains("Recovery", StringComparison.OrdinalIgnoreCase) &&
                output.Contains("Compression", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Also check if DISM flat-out fails to read it (corrupted or wrong format)
            if (process.ExitCode != 0)
            {
                // Might be encrypted ESD — check error
                var error = await process.StandardError.ReadToEndAsync();
                if (error.Contains("recovery", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // If we can't determine, assume it's fine
        }

        return false;
    }

    [RelayCommand]
    private async Task MountImageAsync()
    {
        if (IsBusy) return;
        if (SelectedImage == null || string.IsNullOrWhiteSpace(WimFilePath))
        {
            _dialogService.ShowWarning("Please select a WIM file and image index.", "TrimKit");
            return;
        }

        try
        {
            IsBusy = true;
            var progress = new Progress<(int percent, string status)>(p =>
            {
                ProgressValue = p.percent;
                StatusText = p.status;
            });

            // If we have a work folder (came from ISO workflow), do the full extraction flow
            if (!string.IsNullOrEmpty(WorkFolder) && Directory.Exists(WorkFolder))
            {
                // Step 6: Extract selected edition to its own folder
                StatusText = "Extracting selected edition...";
                var editionFolder = Path.Combine(WorkFolder, "Edition_Work");
                InstallWimPath = await _isoService.ExtractEditionAsync(
                    WimFilePath, SelectedImage.Index, editionFolder, progress);

                _logService.Log(LogLevel.Success, $"Edition '{SelectedImage.Name}' extracted to: {editionFolder}");

                // Step 7: Extract boot.wim to its own folder
                StatusText = "Extracting boot.wim...";
                var bootFolder = Path.Combine(WorkFolder, "Boot_Work");
                BootWimPath = await _isoService.ExtractBootWimAsync(WorkFolder, bootFolder, progress);
                _logService.Log(LogLevel.Success, $"boot.wim extracted to: {bootFolder}");

                // Step 8: Mount extracted install.wim (single-edition, index 1) for debloating
                StatusText = "Mounting install.wim for debloating...";
                InstallMountPath = Path.Combine(WorkFolder, "Mount_Install");
                Directory.CreateDirectory(InstallMountPath);

                var mountProgress = new Progress<int>(p => ProgressValue = p);
                await _dismService.MountImageAsync(InstallWimPath, 1, InstallMountPath, mountProgress);
                IsInstallMounted = true;

                // Also mount boot.wim index 2 (Windows Setup PE) for debloating
                StatusText = "Mounting boot.wim for debloating...";
                BootMountPath = Path.Combine(WorkFolder, "Mount_Boot");
                Directory.CreateDirectory(BootMountPath);

                // boot.wim typically has 2 indexes: 1=WinPE, 2=Windows Setup
                // Mount index 2 (Setup) which is the one users interact with
                var bootImages = await _dismService.GetWimInfoAsync(BootWimPath);
                var bootIndex = bootImages.Count >= 2 ? 2 : 1;
                await _dismService.MountImageAsync(BootWimPath, bootIndex, BootMountPath, mountProgress);
                IsBootMounted = true;

                // Set legacy fields for backward compatibility
                MountPath = InstallMountPath;
                IsMounted = true;
                WimFilePath = InstallWimPath;

                StatusText = "Both images mounted — ready for debloating";
                _logService.Log(LogLevel.Success,
                    $"✓ install.wim mounted at: {InstallMountPath}\n" +
                    $"✓ boot.wim mounted at: {BootMountPath}\n" +
                    "SafetyGuard active for install.wim | BootWimSafetyGuard active for boot.wim");

                await LoadPackagesAndFeaturesAsync();
            }
            else
            {
                // Standard WIM mount (no ISO workflow — user selected a WIM directly)
                StatusText = "Mounting image...";
                var mountProgress = new Progress<int>(p => ProgressValue = p);

                await _dismService.MountImageAsync(WimFilePath, SelectedImage.Index, MountPath, mountProgress);

                IsMounted = true;
                StatusText = "Image mounted - loading packages and features...";

                await LoadPackagesAndFeaturesAsync();
                StatusText = "Image mounted and ready";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Mount failed: {ex.Message}";
            _logService.Log(LogLevel.Error, "Mount failed", ex.Message);
            _dialogService.ShowError($"Failed to mount image:\n{ex.Message}", "TrimKit");
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
        }
    }

    private async Task LoadPackagesAndFeaturesAsync()
    {
        Packages.Clear();
        Features.Clear();
        ProvisionedApps.Clear();
        Capabilities.Clear();
        Fonts.Clear();
        KeyboardLayouts.Clear();
        Languages.Clear();
        InboxDrivers.Clear();
        Services.Clear();
        AvailableUpdates.Clear();

        var mountTarget = !string.IsNullOrEmpty(InstallMountPath) ? InstallMountPath : MountPath;

        // DISM packages and features
        StatusText = "Scanning packages...";
        var packages = await _dismService.GetPackagesAsync(mountTarget);
        foreach (var pkg in packages)
            Packages.Add(pkg);

        StatusText = "Scanning features...";
        var features = await _dismService.GetFeaturesAsync(mountTarget);
        foreach (var feat in features)
            Features.Add(feat);

        // Component removal service — deep discovery
        try
        {
            StatusText = "Scanning provisioned apps...";
            var apps = await _componentRemovalService.GetProvisionedAppsAsync(mountTarget);
            // Filter out framework/runtime packages — only show actual user-facing apps
            foreach (var app in apps)
            {
                if (!IsFrameworkPackage(app.Id))
                    ProvisionedApps.Add(app);
            }

            StatusText = "Scanning capabilities...";
            var caps = await _componentRemovalService.GetCapabilitiesAsync(mountTarget);
            foreach (var cap in caps) Capabilities.Add(cap);

            StatusText = "Scanning fonts...";
            var fonts = await _componentRemovalService.GetFontsAsync(mountTarget);
            foreach (var font in fonts) Fonts.Add(font);

            StatusText = "Scanning keyboard layouts...";
            var kbds = await _componentRemovalService.GetKeyboardLayoutsAsync(mountTarget);
            foreach (var kbd in kbds) KeyboardLayouts.Add(kbd);

            StatusText = "Scanning languages...";
            var langs = await _componentRemovalService.GetLanguagesAsync(mountTarget);
            foreach (var lang in langs) Languages.Add(lang);

            StatusText = "Scanning inbox drivers...";
            var drivers = await _componentRemovalService.GetInboxDriversAsync(mountTarget);
            foreach (var drv in drivers) InboxDrivers.Add(drv);
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Warning, $"Component scan partial failure: {ex.Message}");
        }

        // Services: skip during mount — load lazily when user navigates to Services tab
        // (reg QUERY on offline hive with thousands of keys is too slow for startup)
        _logService.Log(LogLevel.Info, "Services will be scanned on-demand when Services tab is selected.");

        _logService.Log(LogLevel.Info,
            $"Loaded: {Packages.Count} packages, {Features.Count} features, {ProvisionedApps.Count} apps, " +
            $"{Capabilities.Count} capabilities, {Fonts.Count} fonts, {InboxDrivers.Count} inbox drivers");
    }

    /// <summary>
    /// Returns true if a package name is a framework/runtime dependency (not a user-facing app).
    /// These should not be shown to the user for removal as they break other apps.
    /// </summary>
    private static bool IsFrameworkPackage(string packageName)
    {
        var lower = packageName.ToLowerInvariant();
        // Only filter actual runtime/framework dependencies — NOT codec extensions or real apps
        return lower.Contains("microsoft.net.native.framework") ||
               lower.Contains("microsoft.net.native.runtime") ||
               lower.Contains("microsoft.vclibs") ||
               lower.Contains("microsoft.ui.xaml") ||
               lower.Contains("microsoft.services.store.engagement") ||
               lower.Contains("microsoft.advertising.xaml") ||
               lower.Contains("microsoft.directxruntime") ||
               lower.Contains("microsoft.windowsappruntime") ||
               lower.Contains("microsoft.winjs");
    }

    /// <summary>
    /// Loads services on-demand (called when user navigates to Services tab).
    /// Uses multiple approaches since DISM holds a lock on the mounted hive.
    /// </summary>
    [RelayCommand]
    private async Task LoadServicesAsync()
    {
        if (IsBusy || !IsMounted || Services.Count > 0) return;

        try
        {
            IsBusy = true;
            StatusText = "Scanning services...";
            var mountTarget = !string.IsNullOrEmpty(InstallMountPath) ? InstallMountPath : MountPath;

            List<WindowsServiceInfo>? services = null;

            // Try the registry approach (copies hive to temp)
            try
            {
                var svcTask = Task.Run(async () => await _serviceManager.GetServicesAsync(mountTarget));
                services = await svcTask.WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Warning, $"Registry-based service scan failed: {ex.Message}");
            }

            // Fallback: scan drivers directory + known services
            if (services == null || services.Count == 0)
            {
                _logService.Log(LogLevel.Info, "Using filesystem-based service detection fallback...");
                services = GetServicesFromFilesystem(mountTarget);
            }

            foreach (var svc in services) Services.Add(svc);
            StatusText = Services.Count > 0
                ? $"Found {Services.Count} services"
                : "No services found — load a WinReducer preset to configure services";
        }
        catch (Exception ex)
        {
            StatusText = $"Service scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Filesystem-based service detection — scans drivers dir and known service executables.
    /// Doesn't require registry access. Less accurate but always works.
    /// </summary>
    private List<WindowsServiceInfo> GetServicesFromFilesystem(string mountPath)
    {
        var services = new List<WindowsServiceInfo>();
        var driversDir = Path.Combine(mountPath, @"Windows\System32\drivers");

        // Scan .sys files in drivers directory
        if (Directory.Exists(driversDir))
        {
            foreach (var file in Directory.GetFiles(driversDir, "*.sys"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                services.Add(new WindowsServiceInfo
                {
                    ServiceName = name,
                    DisplayName = name,
                    StartType = ServiceStartType.System,
                    OriginalStartType = ServiceStartType.System
                });
            }
        }

        return services.OrderBy(s => s.ServiceName).ToList();
    }

    [RelayCommand]
    private async Task ApplyChangesAsync()
    {
        if (IsBusy) return;
        if (!IsMounted)
        {
            _dialogService.ShowWarning("No image is mounted.", "TrimKit");
            return;
        }

        var hasBootMount = IsBootMounted && !string.IsNullOrEmpty(BootMountPath);
        var componentCount = ProvisionedApps.Count(c => c.IsSelected) +
                             Capabilities.Count(c => c.IsSelected) +
                             Fonts.Count(c => c.IsSelected) +
                             KeyboardLayouts.Count(c => c.IsSelected) +
                             Languages.Count(c => c.IsSelected) +
                             InboxDrivers.Count(c => c.IsSelected);
        var serviceCount = Services.Count(s => s.IsSelected);

        var confirmMsg = "Apply all selected changes to the mounted image(s)?\n\nThis will:\n" +
            $"- Remove {Packages.Count(p => p.IsSelected)} package(s)\n" +
            $"- Remove {componentCount} component(s) (apps, capabilities, fonts, keyboards, languages, drivers)\n" +
            $"- Modify {Features.Count(f => f.IsModified)} feature(s)\n" +
            $"- Disable {serviceCount} service(s)\n" +
            $"- Apply {RegistryTweaks.Count(r => r.IsSelected)} registry tweak(s)\n" +
            $"- Add {DriverPaths.Count} driver path(s)\n";

        if (hasBootMount)
        {
            confirmMsg += "\n⚡ Changes will be applied to BOTH install.wim and boot.wim\n" +
                          "   • install.wim uses SafetyGuard (install protection)\n" +
                          "   • boot.wim uses BootWimSafetyGuard (stricter boot protection)";
        }

        var result = _dialogService.Confirm(confirmMsg, "Confirm Changes");

        if (!result)
            return;

        try
        {
            IsBusy = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            var totalOps = Packages.Count(p => p.IsSelected) +
                           Features.Count(f => f.IsModified) +
                           RegistryTweaks.Count(r => r.IsSelected) +
                           DriverPaths.Count;

            // If boot.wim is also mounted, we'll apply package/feature changes there too
            if (hasBootMount) totalOps *= 2;

            var currentOp = 0;
            var installMountTarget = !string.IsNullOrEmpty(InstallMountPath) ? InstallMountPath : MountPath;

            // ═══════════════════════════════════════════════════════════
            // INSTALL.WIM — apply with standard SafetyGuard
            // ═══════════════════════════════════════════════════════════
            _logService.Log(LogLevel.Info, "━━━ Applying to install.wim (SafetyGuard active) ━━━");

            // Remove selected packages from install.wim
            foreach (var pkg in Packages.Where(p => p.IsSelected).ToList())
            {
                if (SafetyGuard.IsAbsolutelyCritical(pkg.PackageName, ComponentType.Package))
                {
                    _logService.Log(LogLevel.Warning, $"Protected package removal blocked: {pkg.DisplayName}");
                    continue;
                }
                StatusText = $"[install.wim] Removing: {pkg.DisplayName}";
                try
                {
                    await _dismService.RemovePackageAsync(installMountTarget, pkg.PackageName);
                    Packages.Remove(pkg);
                }
                catch (Exception ex)
                {
                    _logService.Log(LogLevel.Warning, $"Could not remove {pkg.DisplayName}: {ex.Message}");
                }
                currentOp++;
                ProgressValue = (int)((double)currentOp / totalOps * 100);
            }

            // Apply feature changes to install.wim
            foreach (var feat in Features.Where(f => f.IsModified).ToList())
            {
                StatusText = $"[install.wim] Modifying feature: {feat.FeatureName}";
                try
                {
                    if (feat.IsEnabled)
                        await _dismService.EnableFeatureAsync(installMountTarget, feat.FeatureName);
                    else
                        await _dismService.DisableFeatureAsync(installMountTarget, feat.FeatureName);

                    feat.OriginalState = feat.IsEnabled;
                }
                catch (Exception ex)
                {
                    _logService.Log(LogLevel.Warning, $"Could not modify {feat.FeatureName}: {ex.Message}");
                }
                currentOp++;
                ProgressValue = (int)((double)currentOp / totalOps * 100);
            }

            // Apply registry tweaks to install.wim
            var selectedTweaks = RegistryTweaks.Where(r => r.IsSelected).ToList();
            if (selectedTweaks.Count > 0)
            {
                StatusText = "[install.wim] Applying registry tweaks...";
                try
                {
                    await _registryService.ApplyTweaksAsync(installMountTarget, selectedTweaks);
                }
                catch (Exception ex)
                {
                    _logService.Log(LogLevel.Warning, $"Could not apply registry tweaks: {ex.Message}");
                }
                currentOp += selectedTweaks.Count;
                ProgressValue = (int)((double)currentOp / totalOps * 100);
            }

            // Add drivers to install.wim
            foreach (var driverPath in DriverPaths.ToList())
            {
                StatusText = $"[install.wim] Adding drivers from: {driverPath}";
                try
                {
                    await _dismService.AddDriverAsync(installMountTarget, driverPath, forceUnsigned: ForceUnsignedDrivers);
                }
                catch (Exception ex)
                {
                    _logService.Log(LogLevel.Warning, $"Could not add driver: {ex.Message}");
                }
                currentOp++;
                ProgressValue = (int)((double)currentOp / totalOps * 100);
            }

            _logService.Log(LogLevel.Success, "install.wim changes applied");

            // Component removal (Appx, capabilities, fonts, keyboards, languages, drivers)
            var selectedComponents = new List<RemovableComponent>();
            selectedComponents.AddRange(ProvisionedApps.Where(c => c.IsSelected));
            selectedComponents.AddRange(Capabilities.Where(c => c.IsSelected));
            selectedComponents.AddRange(Fonts.Where(c => c.IsSelected));
            selectedComponents.AddRange(KeyboardLayouts.Where(c => c.IsSelected));
            selectedComponents.AddRange(Languages.Where(c => c.IsSelected));
            selectedComponents.AddRange(InboxDrivers.Where(c => c.IsSelected));

            if (selectedComponents.Count > 0)
            {
                StatusText = $"[install.wim] Removing {selectedComponents.Count} component(s)...";
                var compProgress = new Progress<(int percent, string status)>(p =>
                {
                    ProgressValue = p.percent;
                    StatusText = $"[install.wim] {p.status}";
                });
                await _componentRemovalService.RemoveAllAsync(installMountTarget, selectedComponents, compProgress);
                _logService.Log(LogLevel.Success, $"Removed {selectedComponents.Count} component(s) from install.wim");
            }

            // Service configuration
            var modifiedServices = Services.Where(s => s.IsModified).ToList();
            if (modifiedServices.Count > 0)
            {
                StatusText = $"[install.wim] Configuring {modifiedServices.Count} service(s)...";
                var changes = modifiedServices.Select(svc => (svc.ServiceName, svc.StartType)).ToList();
                try
                {
                    await _serviceManager.ConfigureServicesAsync(installMountTarget, changes);
                }
                catch (Exception ex)
                {
                    _logService.Log(LogLevel.Warning, $"Service configuration failed: {ex.Message}");
                }
                _logService.Log(LogLevel.Success, $"Configured {modifiedServices.Count} service(s)");
            }

            // WinSxS cleanup (after all removals are done)
            StatusText = "[install.wim] Running WinSxS cleanup...";
            try
            {
                var cleanupOptions = new WinSxsCleanupOptions
                {
                    StartComponentCleanup = true,
                    ResetBase = true,
                    RemoveOrphanedManifests = true,
                    RemoveBackups = true
                };
                var cleanupProgress = new Progress<(int percent, string status)>(p =>
                {
                    ProgressValue = p.percent;
                    StatusText = $"[install.wim] {p.status}";
                });
                await _winSxsCleanupService.CleanupAsync(installMountTarget, cleanupOptions, cleanupProgress);
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Warning, $"WinSxS cleanup: {ex.Message}");
            }

            // Execute NTLite/WinReducer component map removals (file-level, service disable)
            var allPresetRemoveItems = new List<PresetComponent>();
            allPresetRemoveItems.AddRange(ProvisionedApps.Where(c => c.IsSelected && !c.IsProtected).Select(c => new PresetComponent { Id = c.Id, Name = c.DisplayName }));
            allPresetRemoveItems.AddRange(Capabilities.Where(c => c.IsSelected && !c.IsProtected).Select(c => new PresetComponent { Id = c.Id, Name = c.DisplayName }));
            allPresetRemoveItems.AddRange(Fonts.Where(c => c.IsSelected && !c.IsProtected).Select(c => new PresetComponent { Id = c.Id, Name = c.DisplayName }));
            allPresetRemoveItems.AddRange(KeyboardLayouts.Where(c => c.IsSelected && !c.IsProtected).Select(c => new PresetComponent { Id = c.Id, Name = c.DisplayName }));
            allPresetRemoveItems.AddRange(Languages.Where(c => c.IsSelected && !c.IsProtected).Select(c => new PresetComponent { Id = c.Id, Name = c.DisplayName }));
            allPresetRemoveItems.AddRange(InboxDrivers.Where(c => c.IsSelected && !c.IsProtected).Select(c => new PresetComponent { Id = c.Id, Name = c.DisplayName }));

            if (allPresetRemoveItems.Count > 0)
            {
                StatusText = "[install.wim] Executing NTLite-mapped removals...";
                var ntliteProgress = new Progress<(int percent, string status)>(p =>
                {
                    ProgressValue = p.percent;
                    StatusText = $"[install.wim] {p.status}";
                });
                await _applyService.ExecuteNtLiteRemovalsAsync(installMountTarget, allPresetRemoveItems, ntliteProgress, ct);
            }

            // Apply wallpapers from loaded preset (WinReducer Appearance section)
            if (_loadedWallpapers != null)
            {
                StatusText = "[install.wim] Applying wallpapers...";
                await _applyService.ApplyWallpapersAsync(installMountTarget, BootWimPath, IsBootMounted, _loadedWallpapers, ct);
            }

            // Apply service changes from loaded preset (WinReducer Services section)
            if (_loadedServiceChanges.Count > 0)
            {
                StatusText = $"[install.wim] Configuring {_loadedServiceChanges.Count} service(s) from preset...";
                await _applyService.ApplyPresetServiceChangesAsync(installMountTarget, _loadedServiceChanges, ct);
            }

            // DISM image cleanup (shrinks WIM after component removal)
            StatusText = "[install.wim] Running DISM cleanup...";
            try
            {
                await _customizationService.CleanupImageAsync(installMountTarget, resetBase: true);
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Warning, $"DISM cleanup: {ex.Message}");
            }

            // ═══════════════════════════════════════════════════════════
            // BOOT.WIM — apply with BootWimSafetyGuard (stricter)
            // ═══════════════════════════════════════════════════════════
            if (hasBootMount)
            {
                _logService.Log(LogLevel.Info, "━━━ Applying to boot.wim (BootWimSafetyGuard active — stricter) ━━━");

                var bootOptions = BootWimSafetyGuard.GetDefaultBootCompatibilityOptions();

                // Remove packages from boot.wim (with boot safety guard)
                foreach (var pkg in Packages.Where(p => p.IsSelected).ToList())
                {
                    // Check boot safety guard
                    if (BootWimSafetyGuard.IsBootCritical(pkg.PackageName))
                    {
                        _logService.Log(LogLevel.Warning,
                            $"[boot.wim] BootWimSafetyGuard BLOCKED removal of: {pkg.DisplayName} (boot-critical)");
                        currentOp++;
                        ProgressValue = (int)((double)currentOp / totalOps * 100);
                        continue;
                    }

                    if (BootWimSafetyGuard.IsProtectedByBootCompatibility(pkg.PackageName, bootOptions))
                    {
                        _logService.Log(LogLevel.Warning,
                            $"[boot.wim] BootWimSafetyGuard BLOCKED removal of: {pkg.DisplayName} (boot-protected)");
                        currentOp++;
                        ProgressValue = (int)((double)currentOp / totalOps * 100);
                        continue;
                    }

                    StatusText = $"[boot.wim] Removing: {pkg.DisplayName}";
                    try
                    {
                        await _dismService.RemovePackageAsync(BootMountPath, pkg.PackageName);
                    }
                    catch (Exception ex)
                    {
                        _logService.Log(LogLevel.Warning, $"[boot.wim] Could not remove {pkg.DisplayName}: {ex.Message}");
                    }
                    currentOp++;
                    ProgressValue = (int)((double)currentOp / totalOps * 100);
                }

                // Apply feature changes to boot.wim (with boot safety guard)
                foreach (var feat in Features.Where(f => f.IsModified).ToList())
                {
                    if (BootWimSafetyGuard.IsBootCritical(feat.FeatureName))
                    {
                        _logService.Log(LogLevel.Warning,
                            $"[boot.wim] BootWimSafetyGuard BLOCKED modification of: {feat.FeatureName}");
                        currentOp++;
                        ProgressValue = (int)((double)currentOp / totalOps * 100);
                        continue;
                    }

                    StatusText = $"[boot.wim] Modifying feature: {feat.FeatureName}";
                    try
                    {
                        if (feat.IsEnabled)
                            await _dismService.EnableFeatureAsync(BootMountPath, feat.FeatureName);
                        else
                            await _dismService.DisableFeatureAsync(BootMountPath, feat.FeatureName);
                    }
                    catch (Exception ex)
                    {
                        _logService.Log(LogLevel.Warning, $"[boot.wim] Could not modify {feat.FeatureName}: {ex.Message}");
                    }
                    currentOp++;
                    ProgressValue = (int)((double)currentOp / totalOps * 100);
                }

                // Apply registry tweaks to boot.wim (only applicable ones)
                var bootTweaks = RegistryTweaks.Where(r => r.IsSelected).ToList();
                if (bootTweaks.Count > 0)
                {
                    StatusText = "[boot.wim] Applying registry tweaks...";
                    try
                    {
                        await _registryService.ApplyTweaksAsync(BootMountPath, bootTweaks);
                    }
                    catch (Exception)
                    {
                        // Many tweaks won't apply to boot.wim — that's expected
                        _logService.Log(LogLevel.Info, "[boot.wim] Skipped or failed some registry tweaks (not applicable)");
                    }
                    currentOp += bootTweaks.Count;
                    ProgressValue = (int)((double)currentOp / totalOps * 100);
                }

                // Add drivers to boot.wim
                foreach (var driverPath in DriverPaths.ToList())
                {
                    StatusText = $"[boot.wim] Adding drivers from: {driverPath}";
                    try
                    {
                        await _dismService.AddDriverAsync(BootMountPath, driverPath, forceUnsigned: ForceUnsignedDrivers);
                    }
                    catch (Exception ex)
                    {
                        _logService.Log(LogLevel.Warning, $"[boot.wim] Could not add driver: {ex.Message}");
                    }
                    currentOp++;
                    ProgressValue = (int)((double)currentOp / totalOps * 100);
                }

                _logService.Log(LogLevel.Success, "boot.wim changes applied (with BootWimSafetyGuard)");
            }

            StatusText = "All changes applied — saving images...";
            _logService.Log(LogLevel.Success, "All changes applied to both images");

            // Unmount both WIMs with commit (save changes into the WIM files)
            if (IsBootMounted && !string.IsNullOrEmpty(BootMountPath))
            {
                StatusText = "Saving boot.wim...";
                await _dismService.UnmountImageAsync(BootMountPath, true);
                IsBootMounted = false;
                _logService.Log(LogLevel.Success, "boot.wim saved");
            }

            var installMount = !string.IsNullOrEmpty(InstallMountPath) ? InstallMountPath : MountPath;
            if (IsMounted && !string.IsNullOrEmpty(installMount))
            {
                StatusText = "Saving install.wim...";
                await _dismService.UnmountImageAsync(installMount, true);
                IsInstallMounted = false;
                IsMounted = false;
                _logService.Log(LogLevel.Success, "install.wim saved");
            }

            // Put the modified WIMs back into the work folder's sources directory
            if (!string.IsNullOrEmpty(WorkFolder) && Directory.Exists(WorkFolder))
            {
                var sourcesDir = Path.Combine(WorkFolder, "sources");
                Directory.CreateDirectory(sourcesDir);

                // Copy modified install.wim back to sources
                if (!string.IsNullOrEmpty(InstallWimPath) && File.Exists(InstallWimPath))
                {
                    var destInstall = Path.Combine(sourcesDir, "install.wim");
                    StatusText = "Copying modified install.wim to ISO structure...";
                    File.Copy(InstallWimPath, destInstall, overwrite: true);
                }

                // Copy modified boot.wim back to sources
                if (!string.IsNullOrEmpty(BootWimPath) && File.Exists(BootWimPath))
                {
                    var destBoot = Path.Combine(sourcesDir, "boot.wim");
                    StatusText = "Copying modified boot.wim to ISO structure...";
                    File.Copy(BootWimPath, destBoot, overwrite: true);
                }

                // Ask user where to save the final ISO
                var defaultIsoName = !string.IsNullOrEmpty(IsoFilePath)
                    ? Path.GetFileNameWithoutExtension(IsoFilePath) + "_trimmed.iso"
                    : "Windows_Trimmed.iso";
                var isoSavePath = _dialogService.SaveFile("ISO Image (*.iso)|*.iso", "Save debloated ISO as", defaultIsoName);

                if (isoSavePath != null)
                {
                    StatusText = "Building ISO...";
                    var isoProgress = new Progress<(int percent, string status)>(p =>
                    {
                        ProgressValue = p.percent;
                        StatusText = p.status;
                    });

                    var volumeLabel = "WIN_TRIMKIT";
                    await _imageToolsService.BuildIsoAsync(WorkFolder, isoSavePath, volumeLabel, isoProgress);

                    StatusText = $"ISO saved: {isoSavePath}";
                    _logService.Log(LogLevel.Success, $"Final ISO created: {isoSavePath}");
                }
                else
                {
                    StatusText = "ISO build skipped — work folder preserved";
                    _logService.Log(LogLevel.Info, $"User skipped ISO build. Modified files remain in: {WorkFolder}");
                    // Don't delete work folder if user skipped — they might want the files
                    return;
                }
            }

            // Cleanup temp work folder
            if (!string.IsNullOrEmpty(WorkFolder) && Directory.Exists(WorkFolder))
            {
                try
                {
                    Directory.Delete(WorkFolder, true);
                    _logService.Log(LogLevel.Info, "Work folder cleaned up");
                    WorkFolder = string.Empty;
                }
                catch (Exception ex)
                {
                    _logService.Log(LogLevel.Warning, $"Could not delete work folder: {ex.Message}");
                }
            }

            // Unmount ISO if still mounted
            if (!string.IsNullOrEmpty(IsoFilePath))
            {
                try { await _isoService.UnmountIsoAsync(IsoFilePath); }
                catch (Exception ex) { _logService.Log(LogLevel.Warning, $"ISO unmount during cleanup: {ex.Message}"); }
            }

            Packages.Clear();
            Features.Clear();
            StatusText = "Done — ISO built and cleanup complete";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Operation cancelled";
            _logService.Log(LogLevel.Warning, "Apply operation was cancelled by user");
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            _logService.Log(LogLevel.Error, "Apply changes failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private async Task UnmountAsync(object? parameter)
    {
        var commit = parameter is true or "True" or "true";
        if (!IsMounted)
            return;

        try
        {
            IsBusy = true;
            StatusText = commit ? "Saving and unmounting..." : "Discarding and unmounting...";
            var progress = new Progress<int>(p => ProgressValue = p);

            // Unmount boot.wim first (if mounted via ISO workflow)
            if (IsBootMounted && !string.IsNullOrEmpty(BootMountPath))
            {
                StatusText = commit ? "Saving boot.wim changes..." : "Discarding boot.wim changes...";
                await _dismService.UnmountImageAsync(BootMountPath, commit, progress);
                IsBootMounted = false;
                _logService.Log(LogLevel.Success, $"boot.wim unmounted ({(commit ? "saved" : "discarded")})");
            }

            // Unmount install.wim
            var mountToUnmount = !string.IsNullOrEmpty(InstallMountPath) ? InstallMountPath : MountPath;
            StatusText = commit ? "Saving install.wim changes..." : "Discarding install.wim changes...";
            await _dismService.UnmountImageAsync(mountToUnmount, commit, progress);
            IsInstallMounted = false;

            IsMounted = false;
            Packages.Clear();
            Features.Clear();
            StatusText = "All images unmounted";
            _logService.Log(LogLevel.Success, "All images unmounted successfully");
        }
        catch (Exception ex)
        {
            StatusText = $"Unmount failed: {ex.Message}";
            _logService.Log(LogLevel.Error, "Unmount failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
        }
    }

    [RelayCommand]
    private void AddDriverPath()
    {
        var folder = _dialogService.OpenFolder("Select Driver Folder");
        if (folder != null && !DriverPaths.Contains(folder))
        {
            DriverPaths.Add(folder);
        }
    }

    [RelayCommand]
    private void RemoveDriverPath(string path)
    {
        DriverPaths.Remove(path);
    }

    [RelayCommand]
    private async Task SavePresetAsync()
    {
        var savePath = _dialogService.SaveFile("TrimKit Preset (*.wwp)|*.wwp", "Save TrimKit Preset");

        if (savePath == null)
            return;

        // Collect ALL selected items from ALL tabs into the RemoveList
        var removeList = new List<PresetComponent>();

        // Packages
        removeList.AddRange(Packages.Where(p => p.IsSelected).Select(p => new PresetComponent
            { Id = p.PackageName, Name = p.DisplayName, Category = "Package" }));

        // Provisioned Apps
        removeList.AddRange(ProvisionedApps.Where(c => c.IsSelected).Select(c => new PresetComponent
            { Id = c.Id, Name = c.DisplayName, Category = "Apps" }));

        // Capabilities
        removeList.AddRange(Capabilities.Where(c => c.IsSelected).Select(c => new PresetComponent
            { Id = c.Id, Name = c.DisplayName, Category = "Capabilities" }));

        // Fonts
        removeList.AddRange(Fonts.Where(c => c.IsSelected).Select(c => new PresetComponent
            { Id = c.Id, Name = c.DisplayName, Category = "Fonts" }));

        // Keyboard Layouts
        removeList.AddRange(KeyboardLayouts.Where(c => c.IsSelected).Select(c => new PresetComponent
            { Id = c.Id, Name = c.DisplayName, Category = "Keyboards" }));

        // Languages
        removeList.AddRange(Languages.Where(c => c.IsSelected).Select(c => new PresetComponent
            { Id = c.Id, Name = c.DisplayName, Category = "Languages" }));

        // Inbox Drivers
        removeList.AddRange(InboxDrivers.Where(c => c.IsSelected).Select(c => new PresetComponent
            { Id = c.Id, Name = c.DisplayName, Category = "Drivers" }));

        // KeepList = everything NOT selected (from all collections)
        var keepList = new List<PresetComponent>();
        keepList.AddRange(Packages.Where(p => !p.IsSelected).Select(p => new PresetComponent
            { Id = p.PackageName, Name = p.DisplayName, Category = "Package" }));
        keepList.AddRange(ProvisionedApps.Where(c => !c.IsSelected).Select(c => new PresetComponent
            { Id = c.Id, Name = c.DisplayName, Category = "Apps" }));
        keepList.AddRange(Capabilities.Where(c => !c.IsSelected).Select(c => new PresetComponent
            { Id = c.Id, Name = c.DisplayName, Category = "Capabilities" }));

        var preset = new Preset
        {
            Name = Path.GetFileNameWithoutExtension(savePath),
            SourceFormat = "TrimKit",
            RemoveList = removeList,
            KeepList = keepList,
            FeatureChanges = Features.Where(f => f.IsModified).Select(f => new FeaturePreset
            {
                FeatureName = f.FeatureName,
                Enable = f.IsEnabled
            }).ToList(),
            RegistryTweaks = RegistryTweaks.Where(r => r.IsSelected).ToList(),
            DriverPaths = DriverPaths.ToList()
        };

        await _presetService.SavePresetAsync(preset, savePath);
        _logService.Log(LogLevel.Success, $"Preset saved: {preset.Name} (Remove: {preset.RemoveList.Count}, Keep: {preset.KeepList.Count})");
        StatusText = $"Preset saved: {preset.Name}";
    }

    [RelayCommand]
    private async Task LoadPresetAsync()
    {
        var filePath = _dialogService.OpenFile(
            "All Supported Presets|*.wwp;*.xml;*.wccf|TrimKit Preset (*.wwp)|*.wwp|NTLite Preset (*.xml)|*.xml|WinReducer Preset (*.wccf)|*.wccf|All Files (*.*)|*.*",
            "Load Preset (TrimKit / NTLite / WinReducer)");

        if (filePath == null)
            return;

        try
        {
            var preset = await _presetService.LoadPresetAsync(filePath);
            ApplyPresetToUI(preset);

            _logService.Log(LogLevel.Success,
                $"Preset loaded: {preset.Name} (Source: {preset.SourceFormat}, Remove: {preset.RemoveList.Count}, Keep: {preset.KeepList.Count})");
            StatusText = $"Preset loaded: {preset.Name} [{preset.SourceFormat}]";
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Failed to load preset: {ex.Message}");
            _dialogService.ShowError($"Failed to load preset:\n{ex.Message}", "TrimKit");
        }
    }

    [RelayCommand]
    private async Task CombinePresetsAsync()
    {
        var files = _dialogService.OpenFiles(
            "All Supported Presets|*.wwp;*.xml;*.wccf|All Files (*.*)|*.*",
            "Select Presets to Combine (hold Ctrl for multiple)");

        if (files == null || files.Length < 2)
        {
            StatusText = "Select at least 2 presets to combine";
            return;
        }

        try
        {
            var presets = new List<Preset>();
            foreach (var file in files)
            {
                var preset = await _presetService.LoadPresetAsync(file);
                presets.Add(preset);
            }

            var combinedName = $"Combined_{DateTime.Now:yyyyMMdd_HHmm}";
            var combined = _presetService.CombinePresets(presets, combinedName);

            // Save the combined preset
            var combinedSavePath = _dialogService.SaveFile(
                "TrimKit Preset (*.wwp)|*.wwp",
                "Save Combined Preset",
                $"{combinedName}.wwp");

            if (combinedSavePath != null)
            {
                await _presetService.SavePresetAsync(combined, combinedSavePath);
                _logService.Log(LogLevel.Success,
                    $"Combined {presets.Count} presets → {combined.Name} (Remove: {combined.RemoveList.Count}, Keep: {combined.KeepList.Count})");

                // Also apply to UI
                ApplyPresetToUI(combined);
                StatusText = $"Combined preset saved and applied: {combined.Name}";
            }
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Failed to combine presets: {ex.Message}");
        }
    }

    private void ApplyPresetToUI(Preset preset)
    {
        // Build lookup sets — Keep takes priority over Remove
        var keepIds = new HashSet<string>(preset.KeepList.Select(k => k.Id), StringComparer.OrdinalIgnoreCase);
        var removeIds = new HashSet<string>(preset.RemoveList.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        // Also match by display name for NTLite/WinReducer presets that use different ID formats
        var removeNames = new HashSet<string>(preset.RemoveList.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

        // Apply to Packages
        foreach (var pkg in Packages)
        {
            if (keepIds.Contains(pkg.PackageName))
                pkg.IsSelected = false;
            else if (removeIds.Contains(pkg.PackageName) || removeNames.Contains(pkg.DisplayName))
                pkg.IsSelected = true;
        }

        // Apply to Provisioned Apps
        foreach (var app in ProvisionedApps)
        {
            if (keepIds.Contains(app.Id))
                app.IsSelected = false;
            else if (removeIds.Contains(app.Id) || removeNames.Contains(app.DisplayName))
                app.IsSelected = !app.IsProtected;
        }

        // Apply to Capabilities
        foreach (var cap in Capabilities)
        {
            if (keepIds.Contains(cap.Id))
                cap.IsSelected = false;
            else if (removeIds.Contains(cap.Id) || removeNames.Contains(cap.DisplayName))
                cap.IsSelected = !cap.IsProtected;
        }

        // Apply to Fonts
        foreach (var font in Fonts)
        {
            if (removeIds.Contains(font.Id) || removeNames.Contains(font.DisplayName))
                font.IsSelected = !font.IsProtected;
        }

        // Apply to Keyboard Layouts
        foreach (var kbd in KeyboardLayouts)
        {
            if (removeIds.Contains(kbd.Id) || removeNames.Contains(kbd.DisplayName))
                kbd.IsSelected = !kbd.IsProtected;
        }

        // Apply to Languages
        foreach (var lang in Languages)
        {
            if (removeIds.Contains(lang.Id) || removeNames.Contains(lang.DisplayName))
                lang.IsSelected = !lang.IsProtected;
        }

        // Apply to Inbox Drivers
        foreach (var drv in InboxDrivers)
        {
            if (removeIds.Contains(drv.Id) || removeNames.Contains(drv.DisplayName))
                drv.IsSelected = !drv.IsProtected;
        }

        // Apply feature changes
        foreach (var feat in Features)
        {
            var presetFeat = preset.FeatureChanges.FirstOrDefault(
                f => f.FeatureName.Equals(feat.FeatureName, StringComparison.OrdinalIgnoreCase));
            if (presetFeat != null)
                feat.IsEnabled = presetFeat.Enable;
        }

        // Apply registry tweak selections (additive — don't clear existing selections)
        foreach (var tweak in RegistryTweaks)
        {
            if (preset.RegistryTweaks.Any(r => r.Name == tweak.Name))
                tweak.IsSelected = true;
        }

        // Apply driver paths (additive)
        foreach (var path in preset.DriverPaths)
        {
            if (!DriverPaths.Contains(path))
                DriverPaths.Add(path);
        }

        // Store wallpaper settings for Apply phase
        if (preset.Wallpapers != null)
        {
            _loadedWallpapers = preset.Wallpapers;
            _logService.Log(LogLevel.Info, "Preset includes wallpaper customization");
        }

        // Store service changes for Apply phase (additive)
        if (preset.ServiceChanges.Count > 0)
        {
            foreach (var svc in preset.ServiceChanges)
            {
                if (!_loadedServiceChanges.Any(s => s.ServiceName.Equals(svc.ServiceName, StringComparison.OrdinalIgnoreCase)))
                    _loadedServiceChanges.Add(svc);
            }
            _logService.Log(LogLevel.Info, $"Preset includes {preset.ServiceChanges.Count} service change(s)");
        }
    }

    [RelayCommand]
    private async Task CleanupAsync()
    {
        try
        {
            IsBusy = true;
            StatusText = "Cleaning up abandoned mounts...";
            await _dismService.CleanupMountsAsync();
            StatusText = "Cleanup complete";
        }
        catch (Exception ex)
        {
            StatusText = $"Cleanup failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SelectAllApps() { foreach (var a in ProvisionedApps) a.IsSelected = true; }

    [RelayCommand]
    private void SelectAllCapabilities() { foreach (var c in Capabilities) c.IsSelected = true; }

    [RelayCommand]
    private void SelectAllFonts() { foreach (var f in Fonts) if (!f.IsProtected) f.IsSelected = true; }

    [RelayCommand]
    private void SelectAllKeyboards() { foreach (var k in KeyboardLayouts) k.IsSelected = true; }

    [RelayCommand]
    private void SelectAllLanguages() { foreach (var l in Languages) l.IsSelected = true; }

    [RelayCommand]
    private void SelectAllDrivers() { foreach (var d in InboxDrivers) d.IsSelected = true; }

    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
        _logService.Clear();
    }

    [RelayCommand]
    private void CancelOperation()
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            _logService.Log(LogLevel.Warning, "Operation cancelled by user");
            StatusText = "Cancelling...";
        }
    }

    /// <summary>
    /// Fetches available updates from Microsoft Update Catalog filtered to the mounted edition/build.
    /// </summary>
    [RelayCommand]
    private async Task FetchUpdatesAsync()
    {
        if (IsBusy || !IsMounted || SelectedImage == null) return;

        try
        {
            IsBusy = true;
            AvailableUpdates.Clear();
            StatusText = "Checking update eligibility...";

            var mountTarget = !string.IsNullOrEmpty(InstallMountPath) ? InstallMountPath : MountPath;

            // Check if the component store and servicing stack are intact
            // (updates will fail without these)
            if (!CanApplyUpdates(mountTarget))
            {
                StatusText = "Updates unavailable — component store or servicing stack missing from image";
                _logService.Log(LogLevel.Warning,
                    "Cannot fetch updates: CBS (Component Based Servicing) or servicing stack is missing. " +
                    "Updates require an intact servicing stack to install. " +
                    "If you removed Windows Update or servicing components, update integration is not possible.");
                return;
            }

            // Build search query from the mounted image's version and architecture
            var version = SelectedImage.Version; // e.g. "10.0.26100.1"
            var arch = SelectedImage.Architecture; // e.g. "x64"
            var buildNumber = "";

            // Extract major build from version string (10.0.XXXXX.Y → XXXXX)
            var versionParts = version.Split('.');
            if (versionParts.Length >= 3)
                buildNumber = versionParts[2]; // e.g. "26100"

            var query = _updateCatalogService.BuildSearchQuery(SelectedImage.Name, arch, buildNumber);

            StatusText = $"Searching Microsoft Update Catalog: {query}";
            _logService.Log(LogLevel.Info, $"Fetching updates for: {SelectedImage.Name} ({arch}, build {buildNumber})");

            var updates = await _updateCatalogService.SearchUpdatesAsync(query);

            // Filter to x64/x86 matching the image architecture
            var filtered = updates.Where(u =>
                u.Title.Contains(arch, StringComparison.OrdinalIgnoreCase) ||
                (!u.Title.Contains("x86", StringComparison.OrdinalIgnoreCase) &&
                 !u.Title.Contains("x64", StringComparison.OrdinalIgnoreCase) &&
                 !u.Title.Contains("ARM64", StringComparison.OrdinalIgnoreCase))
            ).ToList();

            foreach (var update in filtered)
                AvailableUpdates.Add(update);

            StatusText = filtered.Count > 0
                ? $"Found {filtered.Count} update(s) for {SelectedImage.Name} (build {buildNumber})"
                : "No updates found — try a different search or check your internet connection";
        }
        catch (Exception ex)
        {
            StatusText = $"Update fetch failed: {ex.Message}";
            _logService.Log(LogLevel.Error, $"Update catalog fetch failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
        }
    }

    /// <summary>
    /// Checks if the mounted image has an intact component store and servicing stack
    /// required for update integration. Without these, DISM /Add-Package will fail.
    /// </summary>
    private bool CanApplyUpdates(string mountPath)
    {
        // CBS (Component Based Servicing) store
        var cbsDir = Path.Combine(mountPath, @"Windows\Servicing");
        var winsxsDir = Path.Combine(mountPath, @"Windows\WinSxS");
        var trustedInstaller = Path.Combine(mountPath, @"Windows\Servicing\TrustedInstaller.exe");

        // 1. WinSxS directory must exist (component store)
        if (!Directory.Exists(winsxsDir))
        {
            _logService.Log(LogLevel.Error, "WinSxS directory missing — component store removed. Updates cannot be applied.");
            return false;
        }

        // 2. Servicing directory must exist
        if (!Directory.Exists(cbsDir))
        {
            _logService.Log(LogLevel.Error, "Windows\\Servicing directory missing. Updates cannot be applied.");
            return false;
        }

        // 3. WinSxS must have manifest files (sign of intact store)
        var manifestsDir = Path.Combine(winsxsDir, "Manifests");
        var hasManifests = false;

        if (Directory.Exists(manifestsDir))
            hasManifests = Directory.EnumerateFiles(manifestsDir, "*.manifest").Any();

        if (!hasManifests)
            hasManifests = Directory.EnumerateFiles(winsxsDir, "*.manifest", SearchOption.TopDirectoryOnly).Any();

        if (!hasManifests)
        {
            _logService.Log(LogLevel.Error, "WinSxS has no manifests — component store has been gutted (likely by a prior debloat tool). Updates cannot be applied.");
            return false;
        }

        // 4. Check WinSxS hasn't been heavily stripped (< 1000 directories is suspicious for Win10/11)
        var sxsDirCount = Directory.GetDirectories(winsxsDir, "*", SearchOption.TopDirectoryOnly).Length;
        if (sxsDirCount < 500)
        {
            _logService.Log(LogLevel.Warning,
                $"WinSxS has only {sxsDirCount} assemblies (normal Win11 has 8000+). " +
                "Component store was heavily debloated — update integration will likely fail. Skipping updates.");
            return false;
        }

        // 5. Check for servicing stack packages
        var hasServicingStack = Directory.EnumerateDirectories(winsxsDir, "*servicing*", SearchOption.TopDirectoryOnly).Any();

        if (!hasServicingStack && !File.Exists(trustedInstaller))
        {
            _logService.Log(LogLevel.Warning, "Servicing stack not found (no ServicingStack assemblies or TrustedInstaller). Updates will likely fail. Skipping.");
            return false;
        }

        // 6. Check for CBS database (components.dat or similar)
        var cbsDbDir = Path.Combine(mountPath, @"Windows\WinSxS\InstallTemp");
        var packageDir = Path.Combine(mountPath, @"Windows\Servicing\Packages");
        if (!Directory.Exists(packageDir) || !Directory.EnumerateFiles(packageDir, "*.mum").Any())
        {
            _logService.Log(LogLevel.Warning, "Servicing Packages directory empty or missing (.mum files). Component database may be broken. Skipping updates.");
            return false;
        }

        _logService.Log(LogLevel.Info, $"Servicing stack intact ({sxsDirCount} WinSxS assemblies, Packages present). Updates can be applied.");
        return true;
    }

    /// <summary>
    /// Graceful cleanup: unmounts all mounted images, deletes temp work folder.
    /// Called after Apply completes or when the application is closing.
    /// </summary>
    public async Task GracefulCleanupAsync(bool commitChanges = true)
    {
        try
        {
            _logService.Log(LogLevel.Info, "Starting graceful cleanup...");

            // Unmount boot.wim
            if (IsBootMounted && !string.IsNullOrEmpty(BootMountPath))
            {
                bool unmountSuccess = false;
                while (!unmountSuccess)
                {
                    try
                    {
                        StatusText = commitChanges ? "Cleanup: saving boot.wim..." : "Cleanup: discarding boot.wim...";
                        await _dismService.UnmountImageAsync(BootMountPath, commitChanges);
                        IsBootMounted = false;
                        _logService.Log(LogLevel.Success, $"boot.wim unmounted ({(commitChanges ? "saved" : "discarded")})");
                        unmountSuccess = true;
                    }
                    catch (Exception ex)
                    {
                        _logService.Log(LogLevel.Warning, $"boot.wim unmount failed: {ex.Message}");
                        if (!commitChanges)
                        {
                            IsBootMounted = false;
                            break;
                        }

                        var result = _dialogService.ConfirmWithCancel(
                            $"Failed to save and unmount boot.wim:\n\n{ex.Message}\n\n" +
                            "The directory might be locked by Windows Explorer, an open file, or antivirus.\n\n" +
                            "• Click Yes to RETRY unmounting and saving changes.\n" +
                            "• Click No to DISCARD changes and unmount.\n" +
                            "• Click Cancel to KEEP the image mounted so you can resolve the lock manually.",
                            "TrimKit - Unmount Error (boot.wim)");

                        if (result == true)
                        {
                            continue;
                        }
                        else if (result == false)
                        {
                            try { await _dismService.UnmountImageAsync(BootMountPath, false); } catch { }
                            IsBootMounted = false;
                            break;
                        }
                        else
                        {
                            throw; // Cancel cleanup
                        }
                    }
                }
            }

            // Unmount install.wim
            var installMount = !string.IsNullOrEmpty(InstallMountPath) ? InstallMountPath : MountPath;
            if (IsMounted && !string.IsNullOrEmpty(installMount))
            {
                bool unmountSuccess = false;
                while (!unmountSuccess)
                {
                    try
                    {
                        StatusText = commitChanges ? "Cleanup: saving install.wim..." : "Cleanup: discarding install.wim...";
                        await _dismService.UnmountImageAsync(installMount, commitChanges);
                        IsInstallMounted = false;
                        IsMounted = false;
                        _logService.Log(LogLevel.Success, $"install.wim unmounted ({(commitChanges ? "saved" : "discarded")})");
                        unmountSuccess = true;
                    }
                    catch (Exception ex)
                    {
                        _logService.Log(LogLevel.Warning, $"install.wim unmount failed: {ex.Message}");
                        if (!commitChanges)
                        {
                            IsInstallMounted = false;
                            IsMounted = false;
                            break;
                        }

                        var result = _dialogService.ConfirmWithCancel(
                            $"Failed to save and unmount install.wim:\n\n{ex.Message}\n\n" +
                            "The directory might be locked by Windows Explorer, an open file, or antivirus.\n\n" +
                            "• Click Yes to RETRY unmounting and saving changes.\n" +
                            "• Click No to DISCARD changes and unmount.\n" +
                            "• Click Cancel to KEEP the image mounted so you can resolve the lock manually.",
                            "TrimKit - Unmount Error (install.wim)");

                        if (result == true)
                        {
                            continue;
                        }
                        else if (result == false)
                        {
                            try { await _dismService.UnmountImageAsync(installMount, false); } catch { }
                            IsInstallMounted = false;
                            IsMounted = false;
                            break;
                        }
                        else
                        {
                            throw; // Cancel cleanup
                        }
                    }
                }
            }

            // Unmount the ISO if it's still mounted
            if (!string.IsNullOrEmpty(IsoFilePath))
            {
                try
                {
                    await _isoService.UnmountIsoAsync(IsoFilePath);
                    _logService.Log(LogLevel.Info, "ISO unmounted");
                }
                catch { /* ISO may already be unmounted */ }
            }

            // Clean up DISM abandoned mounts
            try
            {
                await _dismService.CleanupMountsAsync();
            }
            catch (Exception ex) { _logService.Log(LogLevel.Warning, $"DISM cleanup: {ex.Message}"); }

            // Delete temp work folder
            if (!string.IsNullOrEmpty(WorkFolder) && Directory.Exists(WorkFolder))
            {
                try
                {
                    StatusText = "Cleanup: removing temp work folder...";
                    Directory.Delete(WorkFolder, true);
                    _logService.Log(LogLevel.Success, $"Deleted work folder: {WorkFolder}");
                    WorkFolder = string.Empty;
                }
                catch (Exception ex)
                {
                    _logService.Log(LogLevel.Warning, $"Could not delete work folder: {ex.Message}");
                }
            }

            Packages.Clear();
            Features.Clear();
            StatusText = "Cleanup complete";
            ProgressValue = 0;
            _logService.Log(LogLevel.Success, "Graceful cleanup finished");
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Cleanup error: {ex.Message}");
        }
    }

    /// <summary>
    /// Synchronous emergency cleanup — called from ProcessExit or crash handlers
    /// where async is not viable. Uses dism.exe /Cleanup-Wim and force-deletes work folder.
    /// </summary>
    public void ForceCleanupSync()
    {
        try
        {
            // Force-discard any mounted images via dism /Cleanup-Wim
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dism.exe",
                Arguments = "/Cleanup-Wim",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit(15000); // Wait max 15s

            // Unmount ISO
            if (!string.IsNullOrEmpty(IsoFilePath))
            {
                var isoPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -Command \"Dismount-DiskImage -ImagePath '{IsoFilePath.Replace("'", "''")}' -ErrorAction SilentlyContinue\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var isoProcess = System.Diagnostics.Process.Start(isoPsi);
                isoProcess?.WaitForExit(10000);
            }

            // Delete work folder
            if (!string.IsNullOrEmpty(WorkFolder) && Directory.Exists(WorkFolder))
            {
                try { Directory.Delete(WorkFolder, true); } catch { }
            }
        }
        catch
        {
            // Last resort — nothing more we can do
        }
    }
}

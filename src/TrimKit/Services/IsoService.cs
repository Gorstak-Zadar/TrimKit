using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using TrimKit.Models;

namespace TrimKit.Services;

/// <summary>
/// Handles ISO mounting via PowerShell's Mount-DiskImage (built into Windows)
/// and extracting install.wim/install.esd from mounted ISOs.
/// </summary>
public class IsoService : IIsoService
{
    private readonly ILogService _logService;

    public IsoService(ILogService logService)
    {
        _logService = logService;
    }

    public async Task<string> MountIsoAsync(string isoPath)
    {
        _logService.Log(LogLevel.Info, $"Mounting ISO: {Path.GetFileName(isoPath)}");

        // Use Explorer's shell verb "mount" which reliably assigns a drive letter
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = isoPath,
            Verb = "mount",
            UseShellExecute = true
        };

        System.Diagnostics.Process.Start(psi);

        // Wait for the mount to complete and find the new drive
        _logService.Log(LogLevel.Info, "Waiting for Windows to mount ISO...");
        await Task.Delay(3000);

        // Find the mounted drive by looking for sources\install.wim
        for (int attempt = 0; attempt < 10; attempt++)
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.CDRom && drive.IsReady)
                {
                    var sourcesDir = Path.Combine(drive.RootDirectory.FullName, "sources");
                    if (Directory.Exists(sourcesDir) &&
                        (File.Exists(Path.Combine(sourcesDir, "install.wim")) ||
                         File.Exists(Path.Combine(sourcesDir, "install.esd"))))
                    {
                        _logService.Log(LogLevel.Success, $"ISO mounted at: {drive.RootDirectory.FullName}");
                        return drive.RootDirectory.FullName;
                    }
                }
            }
            await Task.Delay(1000);
        }

        throw new InvalidOperationException("ISO mount timed out. Try double-clicking the ISO in Explorer, then browse to the mounted drive's sources\\install.wim");
    }

    public async Task<string> MountIsoSuppressedAsync(string isoPath)
    {
        _logService.Log(LogLevel.Info, $"Mounting ISO (suppressed): {Path.GetFileName(isoPath)}");

        // Snapshot existing Explorer windows before mount
        var existingExplorerWindows = GetExplorerWindowPaths();

        // Use PowerShell Mount-DiskImage — this mounts without auto-opening Explorer
        var script = $"Mount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}' -PassThru | Get-Volume | Select-Object -ExpandProperty DriveLetter";
        var output = await RunPowerShellAsync(script);
        var driveLetter = output.Trim();

        string mountedDrive;

        if (!string.IsNullOrEmpty(driveLetter) && char.IsLetter(driveLetter[0]))
        {
            mountedDrive = $"{driveLetter[0]}:\\";
            _logService.Log(LogLevel.Success, $"ISO mounted at: {mountedDrive}");
        }
        else
        {
            // Fallback: find the new CD-ROM drive
            _logService.Log(LogLevel.Info, "Drive letter not returned directly, scanning drives...");
            await Task.Delay(2000);

            mountedDrive = await FindMountedIsoDriveAsync();
            if (mountedDrive == null)
                throw new InvalidOperationException("ISO mount failed — no mounted drive detected.");
        }

        // Close any Explorer windows that opened for the mounted drive
        await Task.Delay(1500); // Give Explorer a moment to open (if it does)
        await CloseNewExplorerWindowsAsync(existingExplorerWindows, mountedDrive);

        return mountedDrive;
    }

    public async Task<string> CopyIsoToWorkFolderAsync(string mountedDrivePath, IProgress<(int percent, string status)>? progress = null)
    {
        var workFolder = GetNtfsTempWorkFolder();
        _logService.Log(LogLevel.Info, $"Copying ISO content to work folder: {workFolder}");
        progress?.Report((0, "Scanning ISO content..."));

        // Count total bytes first for progress reporting
        var allFiles = Directory.GetFiles(mountedDrivePath, "*", SearchOption.AllDirectories);
        long totalBytes = 0;
        foreach (var file in allFiles)
        {
            try { totalBytes += new FileInfo(file).Length; } catch { }
        }

        long copiedBytes = 0;
        int fileCount = 0;

        foreach (var sourceFile in allFiles)
        {
            var relativePath = Path.GetRelativePath(mountedDrivePath, sourceFile);
            var destFile = Path.Combine(workFolder, relativePath);
            var destDir = Path.GetDirectoryName(destFile)!;

            Directory.CreateDirectory(destDir);

            // Copy with progress tracking
            var fileInfo = new FileInfo(sourceFile);
            await using var src = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await using var dst = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            int read;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read));
                copiedBytes += read;
            }

            fileCount++;
            var pct = totalBytes > 0 ? (int)(copiedBytes * 100 / totalBytes) : 0;
            progress?.Report((pct, $"Copying [{fileCount}/{allFiles.Length}]: {relativePath}"));
        }

        progress?.Report((100, "ISO content copied to work folder"));
        _logService.Log(LogLevel.Success, $"Copied {fileCount} files ({copiedBytes / (1024.0 * 1024.0):F0} MB) to {workFolder}");
        return workFolder;
    }

    public async Task<bool> IsRecoveryCompressedAsync(string imagePath)
    {
        if (!File.Exists(imagePath))
            return false;

        try
        {
            var output = await ProcessRunner.RunDismAsync($"/Get-WimInfo /WimFile:\"{imagePath}\" /Index:1");

            // DISM reports "Recovery" for LZMS-compressed files
            if (output.Contains("Recovery", StringComparison.OrdinalIgnoreCase) &&
                output.Contains("Compression", StringComparison.OrdinalIgnoreCase))
            {
                _logService.Log(LogLevel.Info, $"{Path.GetFileName(imagePath)} is recovery (LZMS) compressed");
                return true;
            }
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Warning, $"Could not determine compression type: {ex.Message}");
        }

        return false;
    }

    public async Task<string> ConvertToNormalWimAsync(string imagePath, IProgress<(int percent, string status)>? progress = null)
    {
        var dir = Path.GetDirectoryName(imagePath)!;
        var outputWim = Path.Combine(dir, "install.wim");

        // If the input IS already install.wim, use a temp name for output
        if (string.Equals(Path.GetFullPath(imagePath), Path.GetFullPath(outputWim), StringComparison.OrdinalIgnoreCase))
        {
            outputWim = Path.Combine(dir, "install_converted.wim");
        }

        _logService.Log(LogLevel.Info, $"Converting {Path.GetFileName(imagePath)} to standard WIM...");
        progress?.Report((5, "Reading image info..."));

        // Get edition count
        var infoOutput = await RunDismAsync($"/Get-WimInfo /WimFile:\"{imagePath}\"");
        var indexCount = infoOutput.Split("Index :").Length - 1;

        // Skip index 1 if it's metadata (common in MS-distributed ESDs)
        var startIndex = 1;
        if (indexCount > 1 && infoOutput.Contains("Windows Setup", StringComparison.OrdinalIgnoreCase))
        {
            startIndex = 2; // Skip metadata
        }

        for (int i = startIndex; i <= indexCount; i++)
        {
            var pct = 5 + (int)((i - startIndex + 1.0) / (indexCount - startIndex + 1) * 90);
            progress?.Report((pct, $"Exporting index {i}/{indexCount} to normal WIM..."));

            await RunDismAsync($"/Export-Image /SourceImageFile:\"{imagePath}\" /SourceIndex:{i} /DestinationImageFile:\"{outputWim}\" /Compress:Max");
        }

        // If we used a temp name, replace the original
        if (outputWim.EndsWith("_converted.wim", StringComparison.OrdinalIgnoreCase))
        {
            var finalPath = Path.Combine(dir, "install.wim");
            File.Delete(imagePath); // Delete the recovery-compressed original
            File.Move(outputWim, finalPath);
            outputWim = finalPath;
        }
        else if (Path.GetExtension(imagePath).Equals(".esd", StringComparison.OrdinalIgnoreCase))
        {
            // Delete the .esd since we now have a .wim
            File.Delete(imagePath);
        }

        progress?.Report((100, "Conversion complete"));
        _logService.Log(LogLevel.Success, $"Converted to: {Path.GetFileName(outputWim)}");
        return outputWim;
    }

    public async Task<string> ExtractEditionAsync(string wimPath, int editionIndex, string outputFolder, IProgress<(int percent, string status)>? progress = null)
    {
        Directory.CreateDirectory(outputFolder);
        var outputWim = Path.Combine(outputFolder, "install.wim");

        _logService.Log(LogLevel.Info, $"Extracting edition index {editionIndex} to {outputFolder}");
        progress?.Report((10, $"Exporting edition index {editionIndex}..."));

        await RunDismAsync($"/Export-Image /SourceImageFile:\"{wimPath}\" /SourceIndex:{editionIndex} /DestinationImageFile:\"{outputWim}\" /Compress:Max");

        progress?.Report((100, "Edition extracted"));
        _logService.Log(LogLevel.Success, $"Edition {editionIndex} exported to: {outputWim}");
        return outputWim;
    }

    public async Task<string> ExtractBootWimAsync(string workFolder, string outputFolder, IProgress<(int percent, string status)>? progress = null)
    {
        Directory.CreateDirectory(outputFolder);

        var sourceBootWim = Path.Combine(workFolder, "sources", "boot.wim");
        if (!File.Exists(sourceBootWim))
        {
            throw new FileNotFoundException("boot.wim not found in the ISO sources folder", sourceBootWim);
        }

        var destBootWim = Path.Combine(outputFolder, "boot.wim");
        _logService.Log(LogLevel.Info, $"Copying boot.wim to: {outputFolder}");
        progress?.Report((10, "Copying boot.wim..."));

        // Copy with progress
        await using var src = new FileStream(sourceBootWim, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var dst = new FileStream(destBootWim, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var totalBytes = src.Length;
        var buffer = new byte[81920];
        long bytesCopied = 0;
        int read;

        while ((read = await src.ReadAsync(buffer)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read));
            bytesCopied += read;
            var pct = 10 + (int)(bytesCopied * 85.0 / totalBytes);
            progress?.Report((pct, $"Copying boot.wim: {bytesCopied / (1024.0 * 1024.0):F0} / {totalBytes / (1024.0 * 1024.0):F0} MB"));
        }

        progress?.Report((100, "boot.wim extracted"));
        _logService.Log(LogLevel.Success, $"boot.wim extracted to: {destBootWim}");
        return destBootWim;
    }

    public string GetNtfsTempWorkFolder()
    {
        // Find an NTFS drive with enough space, prefer the system temp drive
        var candidates = new List<string>();

        // First candidate: system temp
        var systemTemp = Path.GetTempPath();
        candidates.Add(systemTemp);

        // Additional candidates: all fixed NTFS drives
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.IsReady && drive.DriveType == DriveType.Fixed &&
                drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase) &&
                drive.AvailableFreeSpace > 10L * 1024 * 1024 * 1024) // >10 GB free
            {
                candidates.Add(Path.Combine(drive.RootDirectory.FullName, "TrimKit_Work"));
            }
        }

        // Use the first valid NTFS candidate
        foreach (var candidate in candidates)
        {
            try
            {
                var driveRoot = Path.GetPathRoot(candidate);
                if (driveRoot != null)
                {
                    var driveInfo = new DriveInfo(driveRoot);
                    if (driveInfo.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase) &&
                        driveInfo.AvailableFreeSpace > 10L * 1024 * 1024 * 1024)
                    {
                        var workDir = Path.Combine(candidate, $"TrimKit_{DateTime.Now:yyyyMMdd_HHmmss}");
                        Directory.CreateDirectory(workDir);
                        _logService.Log(LogLevel.Info, $"Work folder created on NTFS drive: {workDir}");
                        return workDir;
                    }
                }
            }
            catch { /* skip this candidate */ }
        }

        // Last resort: use system temp even if we couldn't verify NTFS
        var fallback = Path.Combine(Path.GetTempPath(), $"TrimKit_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(fallback);
        _logService.Log(LogLevel.Warning, $"Could not verify NTFS drive — using system temp: {fallback}");
        return fallback;
    }

    public async Task UnmountIsoAsync(string isoPath)
    {
        _logService.Log(LogLevel.Info, $"Unmounting ISO: {Path.GetFileName(isoPath)}");

        var script = $"Dismount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}'";
        await RunPowerShellAsync(script);

        _logService.Log(LogLevel.Success, "ISO unmounted");
    }

    #region Explorer Window Suppression

    /// <summary>
    /// Gets the list of currently open Explorer window locations (to detect new ones after mount).
    /// </summary>
    private static HashSet<string> GetExplorerWindowPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Use Shell.Application COM to enumerate Explorer windows
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            foreach (dynamic window in shell.Windows())
            {
                try
                {
                    string? location = window.LocationURL;
                    if (!string.IsNullOrEmpty(location))
                        paths.Add(location);
                }
                catch { }
            }
            Marshal.ReleaseComObject(shell);
        }
        catch { }
        return paths;
    }

    /// <summary>
    /// Closes any Explorer windows that were opened after the mount and point to the mounted drive.
    /// </summary>
    private async Task CloseNewExplorerWindowsAsync(HashSet<string> existingWindows, string mountedDrive)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            var toClose = new List<dynamic>();

            foreach (dynamic window in shell.Windows())
            {
                try
                {
                    string? location = window.LocationURL;
                    if (location != null && !existingWindows.Contains(location))
                    {
                        // Check if this window is for our mounted drive
                        var decoded = Uri.UnescapeDataString(location);
                        if (decoded.Contains(mountedDrive.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                            decoded.Contains(mountedDrive.Replace("\\", "/"), StringComparison.OrdinalIgnoreCase))
                        {
                            toClose.Add(window);
                        }
                    }
                }
                catch { }
            }

            foreach (var window in toClose)
            {
                try
                {
                    window.Quit();
                    _logService.Log(LogLevel.Info, "Closed auto-opened Explorer window for mounted ISO");
                }
                catch { }
            }

            Marshal.ReleaseComObject(shell);
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Warning, $"Could not suppress Explorer window: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Searches for a newly mounted CD-ROM drive containing Windows install files.
    /// </summary>
    private async Task<string> FindMountedIsoDriveAsync()
    {
        for (int attempt = 0; attempt < 15; attempt++)
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.CDRom && drive.IsReady)
                {
                    var sourcesDir = Path.Combine(drive.RootDirectory.FullName, "sources");
                    if (Directory.Exists(sourcesDir) &&
                        (File.Exists(Path.Combine(sourcesDir, "install.wim")) ||
                         File.Exists(Path.Combine(sourcesDir, "install.esd"))))
                    {
                        return drive.RootDirectory.FullName;
                    }
                }
            }
            await Task.Delay(1000);
        }

        return null!;
    }

    #endregion

    public Task<string?> FindInstallImageAsync(string isoMountPath)
    {
        // Look for install.wim or install.esd in the sources folder
        var sourcesDir = Path.Combine(isoMountPath, "sources");

        if (!Directory.Exists(sourcesDir))
        {
            _logService.Log(LogLevel.Warning, "No 'sources' directory found in ISO");
            return Task.FromResult<string?>(null);
        }

        // Prefer install.wim over install.esd
        var wimPath = Path.Combine(sourcesDir, "install.wim");
        if (File.Exists(wimPath))
        {
            _logService.Log(LogLevel.Info, "Found install.wim");
            return Task.FromResult<string?>(wimPath);
        }

        var esdPath = Path.Combine(sourcesDir, "install.esd");
        if (File.Exists(esdPath))
        {
            _logService.Log(LogLevel.Info, "Found install.esd");
            return Task.FromResult<string?>(esdPath);
        }

        _logService.Log(LogLevel.Warning, "No install.wim or install.esd found in sources directory");
        return Task.FromResult<string?>(null);
    }

    public async Task ExtractWimFromIsoAsync(string isoPath, string outputDir, IProgress<(int percent, string status)>? progress = null)
    {
        progress?.Report((5, "Mounting ISO..."));
        var mountPath = await MountIsoAsync(isoPath);

        try
        {
            progress?.Report((20, "Locating install image..."));
            var imagePath = await FindInstallImageAsync(mountPath);

            if (imagePath == null)
            {
                throw new FileNotFoundException("Could not find install.wim or install.esd in the ISO");
            }

            Directory.CreateDirectory(outputDir);
            var extension = Path.GetExtension(imagePath);
            var destPath = Path.Combine(outputDir, $"install{extension}");

            progress?.Report((30, $"Copying {Path.GetFileName(imagePath)} ({new FileInfo(imagePath).Length / (1024.0 * 1024.0 * 1024.0):F2} GB)..."));

            // Copy with progress
            await using var source = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var totalBytes = source.Length;
            var buffer = new byte[81920];
            long bytesRead = 0;
            int read;

            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, read));
                bytesRead += read;

                var pct = 30 + (int)(bytesRead / (double)totalBytes * 60);
                progress?.Report((pct, $"Copying: {bytesRead / (1024.0 * 1024.0):F0} / {totalBytes / (1024.0 * 1024.0):F0} MB"));
            }

            // If it's an ESD, convert to WIM for easier editing
            if (extension.Equals(".esd", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report((92, "Converting ESD to WIM..."));
                var wimDest = Path.Combine(outputDir, "install.wim");
                await ConvertEsdToWimAsync(destPath, wimDest);
                File.Delete(destPath);
                _logService.Log(LogLevel.Success, $"Converted ESD to WIM: {wimDest}");
            }

            progress?.Report((100, "Extraction complete!"));
            _logService.Log(LogLevel.Success, $"Install image extracted to: {outputDir}");
        }
        finally
        {
            await UnmountIsoAsync(isoPath);
        }
    }

    private async Task ConvertEsdToWimAsync(string esdPath, string wimPath)
    {
        // Use DISM to export ESD to WIM
        // First get the number of images (skip index 1 which is usually metadata)
        var infoOutput = await ProcessRunner.RunDismAsync($"/Get-WimInfo /WimFile:\"{esdPath}\"");

        // Count indexes (skipping index 1 if it's metadata)
        var indexCount = infoOutput.Split("Index :").Length - 1;
        var startIndex = indexCount > 1 ? 2 : 1; // Skip metadata index if present

        for (int i = startIndex; i <= indexCount; i++)
        {
            try
            {
                await ProcessRunner.RunDismAsync(
                    $"/Export-Image /SourceImageFile:\"{esdPath}\" /SourceIndex:{i} /DestinationImageFile:\"{wimPath}\" /Compress:Max");
            }
            catch (InvalidOperationException ex)
            {
                _logService.Log(LogLevel.Warning, $"ESD index {i} export issue: {ex.Message}");
            }
        }
    }

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken = default)
    {
        return await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
    }

    private async Task<string> RunDismAsync(string arguments, CancellationToken cancellationToken = default)
    {
        return await ProcessRunner.RunDismAsync(arguments, cancellationToken);
    }
}

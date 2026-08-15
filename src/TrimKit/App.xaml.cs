using System.Net.Http;
using System.Security.Principal;
using System.Windows;
using TrimKit.Services;
using TrimKit.ViewModels;

namespace TrimKit;

public partial class App : System.Windows.Application
{
    private MainViewModel? _mainViewModel;
    private MainWindow? _mainWindow;

    public App()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            System.Windows.MessageBox.Show(
                $"Unhandled error:\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
                "TrimKit - Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        // Hook process-level exit events for premature termination cleanup
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            var logService = new LogService();
            var dismService = new DismService(logService);
            var registryService = new RegistryService(logService);
            var presetService = new PresetService();
            var isoService = new IsoService(logService);
            var serviceManager = new WindowsServiceManager(logService);
            var imageToolsService = new ImageToolsService(logService);
            var unattendService = new UnattendService(logService);
            var customizationService = new CustomizationService(logService);
            var componentRemovalService = new ComponentRemovalService(logService);
            var winSxsCleanupService = new WinSxsCleanupService(logService);
            var dialogService = new DialogService();
            var applyService = new ApplyService(logService, serviceManager, componentRemovalService, customizationService);

            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TrimKit/1.0");
            httpClient.Timeout = TimeSpan.FromMinutes(30);

            var uupDumpService = new UupDumpService(httpClient, logService);
            var msDownloadService = new MicrosoftDownloadService(httpClient, logService);
            var dependencyService = new DependencyService(httpClient, logService);

            var downloadViewModel = new DownloadViewModel(uupDumpService, msDownloadService, logService, dialogService);
            var updateCatalogService = new UpdateCatalogService(httpClient, logService);
            _mainViewModel = new MainViewModel(
                dismService, registryService, presetService, logService,
                downloadViewModel, isoService, serviceManager, imageToolsService,
                unattendService, customizationService,
                componentRemovalService, winSxsCleanupService, updateCatalogService,
                dialogService, applyService);

            _mainWindow = new MainWindow(_mainViewModel);
            _mainWindow.Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Startup failed:\n\n{ex}\n\nInner: {ex.InnerException}",
                "TrimKit - Fatal Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>
    /// Called when the process is exiting (Ctrl+C, Task Manager kill, system shutdown).
    /// Runs synchronous cleanup as a last resort.
    /// </summary>
    private void OnProcessExit(object? sender, EventArgs e)
    {
        _mainWindow?.ForceCleanupSync();
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Try to cleanup even on fatal crash
        _mainWindow?.ForceCleanupSync();
    }
}

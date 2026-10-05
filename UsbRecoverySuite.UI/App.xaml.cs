using System.Windows;
using UsbRecoverySuite.Core.Services.Capacity;
using UsbRecoverySuite.Core.Services.DeviceDetection;
using UsbRecoverySuite.Core.Services.Diagnostics;
using UsbRecoverySuite.Core.Services.History;
using UsbRecoverySuite.Core.Services.Logging;
using UsbRecoverySuite.Core.Services.Localization;
using UsbRecoverySuite.Core.Services.Recovery;
using UsbRecoverySuite.Core.Services.Reporting;
using UsbRecoverySuite.Core.Services.Storage;
using UsbRecoverySuite.UI.Services;
using UsbRecoverySuite.UI.ViewModels;

namespace UsbRecoverySuite.UI;

public partial class App : Application
{
    private IDeviceDetectorService? _detectorService;
    private ThemeService? _themeService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Core singletons
        var logger = new AppLogger();
        var localization = new LocalizationService();
        _themeService = new ThemeService();

        logger.LogInfo("Application", "Lumina Drive v1.0.0 initializing on Windows 10/11 x64...");

        var storageService = new WindowsStorageService(logger);
        var safetyValidator = new SafetyValidatorService(logger);
        var eventLogService = new WindowsEventLogService(logger);
        var diagnosticEngine = new DiagnosticEngine(eventLogService, logger, localization);
        var recoveryService = new WindowsRecoveryService(storageService, safetyValidator, diagnosticEngine, logger, localization);
        var capacityTestService = new CapacityTestService(logger, localization);
        var reportService = new ReportService();
        var historyService = new HistoryService(logger);
        _detectorService = new WindowsDeviceDetectorService(storageService, logger);

        var viewModel = new MainViewModel(
            _detectorService,
            storageService,
            diagnosticEngine,
            safetyValidator,
            recoveryService,
            capacityTestService,
            reportService,
            historyService,
            logger,
            localization,
            _themeService);

        var mainWindow = new MainWindow(viewModel, _detectorService);
        mainWindow.SetEffectiveTheme(_themeService.IsDarkTheme);
        _themeService.EffectiveThemeChanged += mainWindow.SetEffectiveTheme;
        MainWindow = mainWindow;
        mainWindow.Show();

        if (!PrivacyConsentStore.HasAccepted())
        {
            var consentWindow = new PrivacyConsentWindow(localization)
            {
                Owner = mainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            if (consentWindow.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
        }

        // Start disk access only after the privacy and permissions prompt has been accepted.
        mainWindow.StartDeviceChangeNotifications();
        _detectorService.StartMonitoring();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _detectorService?.Dispose();
        _themeService?.Dispose();
        base.OnExit(e);
    }
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Input;
using UsbRecoverySuite.Core.Models;
using UsbRecoverySuite.Core.Services.Capacity;
using UsbRecoverySuite.Core.Services.DeviceDetection;
using UsbRecoverySuite.Core.Services.Diagnostics;
using UsbRecoverySuite.Core.Services.History;
using UsbRecoverySuite.Core.Services.Localization;
using UsbRecoverySuite.Core.Services.Logging;
using UsbRecoverySuite.Core.Services.Recovery;
using UsbRecoverySuite.Core.Services.Reporting;
using UsbRecoverySuite.Core.Services.Storage;
using UsbRecoverySuite.UI.Common;
using UsbRecoverySuite.UI.Services;

namespace UsbRecoverySuite.UI.ViewModels;

public enum NavSection
{
    Dashboard,
    DeviceInfo,
    PartitionsVolumes,
    FileOperations,
    Diagnostics,
    Recovery,
    CapacityTest,
    History,
    Logs,
    Settings
}

public class MainViewModel : ViewModelBase
{
    private readonly IDeviceDetectorService _detectorService;
    private readonly IWindowsStorageService _storageService;
    private readonly IDiagnosticEngine _diagnosticEngine;
    private readonly ISafetyValidatorService _safetyValidator;
    private readonly IRecoveryService _recoveryService;
    private readonly ICapacityTestService _capacityTestService;
    private readonly IReportService _reportService;
    private readonly IHistoryService _historyService;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _loc;
    private readonly ThemeService _themeService;
    private readonly UsbFileTransferService _fileTransferService = new();

    private NavSection _currentSection = NavSection.Dashboard;
    private UsbDeviceInfo? _selectedDevice;
    private bool _isLoading;
    private int _loadingOperationCount;
    private string _statusMessage = string.Empty;
    private string? _statusMessageLocalizationKey;
    private object[] _statusMessageLocalizationArgs = Array.Empty<object>();
    private bool _isElevated;
    private bool _isTransferringFiles;
    private string _backupDestinationFolder = string.Empty;
    private string _fileTransferStatus = string.Empty;
    private double _fileTransferPercent;
    private CancellationTokenSource? _fileTransferCts;

    // Diagnostics state
    private ObservableCollection<DiagnosticResult> _diagnostics = new();
    private HealthStatus _overallHealth = HealthStatus.Unknown;
    private string _recommendedAction = string.Empty;

    // Recovery wizard state
    private PartitionTableType _selectedPartitionStyle = PartitionTableType.MBR;
    private string _selectedFileSystem = "exFAT";
    private string _volumeLabel = "USB_RECOVER";
    private bool _isRecovering;
    private string _recoveryProgressText = string.Empty;
    private ObservableCollection<string> _recoveryLogs = new();
    private RepairOperationResult? _lastRepairResult;

    // Capacity test state
    private bool _isCapacityTesting;
    private double _capacityTestPercent;
    private string _capacityTestPhase = string.Empty;
    private double _currentSpeedMBps;
    private CapacityTestResult? _lastCapacityTestResult;
    private CancellationTokenSource? _capacityTestCts;
    private string _capacityTestSizeOption = "Quick";

    // Confirmation dialog state (Safety protection!)
    private bool _isConfirmDialogOpen;
    private string _confirmDialogTitle = string.Empty;
    private string _confirmDialogWarning = string.Empty;
    private string _confirmTargetDiskInfo = string.Empty;
    private string _confirmInputText = string.Empty;
    private Func<Task>? _confirmedAction;
    private UsbDeviceInfo? _pendingTargetDevice;

    // Collections
    public ObservableCollection<UsbDeviceInfo> Devices { get; } = new();
    public ObservableCollection<DiagnosticResult> Diagnostics => _diagnostics;
    public ObservableCollection<OperationHistoryItem> HistoryItems { get; } = new();
    public ObservableCollection<AppLogEntry> LogEntries { get; } = new();
    public ObservableCollection<string> SelectedPcFiles { get; } = new();

    public ILocalizationService Loc => _loc;

    public MainViewModel(
        IDeviceDetectorService detectorService,
        IWindowsStorageService storageService,
        IDiagnosticEngine diagnosticEngine,
        ISafetyValidatorService safetyValidator,
        IRecoveryService recoveryService,
        ICapacityTestService capacityTestService,
        IReportService reportService,
        IHistoryService historyService,
        IAppLogger logger,
        ILocalizationService loc,
        ThemeService themeService)
    {
        _detectorService = detectorService;
        _storageService = storageService;
        _diagnosticEngine = diagnosticEngine;
        _safetyValidator = safetyValidator;
        _recoveryService = recoveryService;
        _capacityTestService = capacityTestService;
        _reportService = reportService;
        _historyService = historyService;
        _logger = logger;
        _loc = loc;
        _themeService = themeService;
        _fileTransferStatus = _loc.Get("FileTransferReady");
        SelectedPcFiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanCopyPcFilesToUsb));
            CommandManager.InvalidateRequerySuggested();
        };
        _themeService.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ThemeService.SelectedTheme))
                OnPropertyChanged(nameof(SelectedThemeIndex));
        };
        _themeService.EffectiveThemeChanged += _ => OnPropertyChanged(nameof(IsDarkTheme));

        SetLocalizedStatus("StatusReady");

        CheckElevation();

        _loc.LanguageChanged += () =>
        {
            OnPropertyChanged(nameof(Loc));
            OnPropertyChanged(nameof(SelectedLanguageIndex));
            OnPropertyChanged(nameof(SelectedThemeIndex));
            OnPropertyChanged(nameof(SelectedDeviceSummary));
            if (_statusMessageLocalizationKey != null)
            {
                _statusMessage = _loc.Get(_statusMessageLocalizationKey, _statusMessageLocalizationArgs);
                OnPropertyChanged(nameof(StatusMessage));
            }
            if (!IsTransferringFiles)
                FileTransferStatus = _loc.Get("FileTransferReady");
            _ = RunDiagnosticsAsync(recordInHistory: false);
        };

        // Wire logger
        _logger.OnLogEntry += entry =>
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                LogEntries.Add(entry);
                if (LogEntries.Count > 1000) LogEntries.RemoveAt(0);
            });
        };

        // Wire detector events
        _detectorService.DeviceArrived += device =>
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                SetLocalizedStatus("StatusDeviceConnectedFormat", device.DisplayName);
                _ = RefreshDevicesAsync(device.DiskIndex);
            });
        };

        _detectorService.DeviceRemoved += diskIndex =>
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (IsTransferringFiles && SelectedDevice?.DiskIndex == diskIndex)
                    _fileTransferCts?.Cancel();

                SetLocalizedStatus("StatusDeviceRemovedFormat", diskIndex);
                _ = RefreshDevicesAsync();
            });
        };

        _historyService.OnHistoryItemAdded += item =>
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                HistoryItems.Insert(0, item);
            });
        };

        // Initialize Commands
        NavigateCommand = new RelayCommand(p => CurrentSection = (NavSection)p!);
        RefreshDevicesCommand = new AsyncRelayCommand(async () => await RefreshDevicesAsync(), () => !IsBusy);
        RunDiagnosticsCommand = new AsyncRelayCommand(RunDiagnosticsAsync, () => !IsBusy);
        FixAllCommand = new RelayCommand(TriggerFixAllWithConfirmation, () => CanOperateSelectedDevice);
        CleanAndRebuildUsbCommand = new RelayCommand(TriggerCleanAndRebuildWithConfirmation, () => CanOperateSelectedDevice);
        RestoreFullCapacityCommand = new RelayCommand(TriggerRestoreFullCapacityWithConfirmation, () => CanRestoreFullCapacity);
        ClearReadOnlyCommand = new AsyncRelayCommand(ClearReadOnlyAsync, () => CanOperateSelectedDevice);
        CheckFilesystemCommand = new AsyncRelayCommand(() => CheckFilesystemAsync(false), () => CanOperateSelectedDevice);
        RepairFilesystemCommand = new AsyncRelayCommand(() => CheckFilesystemAsync(true), () => CanOperateSelectedDevice);
        StartCapacityTestCommand = new AsyncRelayCommand(StartCapacityTestAsync, () => CanOperateSelectedDevice);
        CancelCapacityTestCommand = new RelayCommand(CancelCapacityTest);
        ExportReportCommand = new RelayCommand(p => ExportReport(p?.ToString() ?? "html"));
        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
        ConfirmActionCommand = new AsyncRelayCommand(ExecuteConfirmedActionAsync);
        CancelConfirmDialogCommand = new RelayCommand(CancelConfirmation);
        ClearLogsCommand = new RelayCommand(() => { _logger.Clear(); LogEntries.Clear(); });
        ClearHistoryCommand = new AsyncRelayCommand(async () => { await _historyService.ClearHistoryAsync(); HistoryItems.Clear(); });
        StartQuickCapacityTestCommand = new AsyncRelayCommand(StartQuickCapacityTestAsync, () => CanOperateSelectedDevice);
        ChooseBackupFolderCommand = new RelayCommand(ChooseBackupFolder, () => !IsTransferringFiles);
        ChoosePcFilesCommand = new RelayCommand(ChoosePcFiles, () => !IsTransferringFiles);
        ClearSelectedPcFilesCommand = new RelayCommand(() => SelectedPcFiles.Clear(), () => SelectedPcFiles.Count > 0 && !IsTransferringFiles);
        CopyUsbToPcCommand = new AsyncRelayCommand(CopyUsbToPcAsync, () => CanTransferUsbFiles && !string.IsNullOrWhiteSpace(BackupDestinationFolder));
        CopyPcFilesToUsbCommand = new AsyncRelayCommand(CopyPcFilesToUsbAsync, () => CanCopyPcFilesToUsb);
        CancelFileTransferCommand = new RelayCommand(() => _fileTransferCts?.Cancel(), () => IsTransferringFiles);
        EraseUsbCommand = new RelayCommand(TriggerEraseUsbWithConfirmation, () => CanOperateSelectedDevice);

        // Load initial history
        foreach (var item in _historyService.GetHistory())
        {
            HistoryItems.Add(item);
        }

    }

    #region Properties

    public int SelectedLanguageIndex
    {
        get => _loc.CurrentLanguage switch { "ru" => 0, "en" => 1, "uk" => 2, _ => 0 };
        set => _loc.CurrentLanguage = value switch { 1 => "en", 2 => "uk", _ => "ru" };
    }

    public int SelectedThemeIndex
    {
        get => (int)_themeService.SelectedTheme;
        set
        {
            if (value >= (int)AppTheme.Dark && value <= (int)AppTheme.System)
                _themeService.SelectedTheme = (AppTheme)value;
        }
    }

    public bool IsDarkTheme => _themeService.IsDarkTheme;

    public bool IsTransferringFiles
    {
        get => _isTransferringFiles;
        private set
        {
            if (SetProperty(ref _isTransferringFiles, value))
            {
                NotifyOperationAvailabilityChanged();
                OnPropertyChanged(nameof(CanTransferUsbFiles));
                OnPropertyChanged(nameof(CanCopyPcFilesToUsb));
            }
        }
    }

    public string BackupDestinationFolder
    {
        get => _backupDestinationFolder;
        set
        {
            if (SetProperty(ref _backupDestinationFolder, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string FileTransferStatus
    {
        get => _fileTransferStatus;
        set => SetProperty(ref _fileTransferStatus, value);
    }

    public double FileTransferPercent
    {
        get => _fileTransferPercent;
        set => SetProperty(ref _fileTransferPercent, value);
    }

    public NavSection CurrentSection
    {
        get => _currentSection;
        set => SetProperty(ref _currentSection, value);
    }

    public UsbDeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(HasSelectedDevice));
                OnPropertyChanged(nameof(IsSafeToRecover));
                OnPropertyChanged(nameof(CanOperateSelectedDevice));
                OnPropertyChanged(nameof(CanRestoreFullCapacity));
                OnPropertyChanged(nameof(SelectedDeviceSummary));
                OnPropertyChanged(nameof(HasUnallocatedLoss));
                OnPropertyChanged(nameof(CanTransferUsbFiles));
                OnPropertyChanged(nameof(CanCopyPcFilesToUsb));
                CommandManager.InvalidateRequerySuggested();
                _ = RunDiagnosticsAsync();
            }
        }
    }

    public bool HasSelectedDevice => SelectedDevice != null;
    public bool IsSafeToRecover => SelectedDevice != null && SelectedDevice.IsSafeToFormat;
    public bool IsBusy => IsLoading || IsRecovering || IsCapacityTesting || IsTransferringFiles;
    public bool CanOperateSelectedDevice => IsSafeToRecover && !IsBusy && !IsConfirmDialogOpen;
    public bool CanRestoreFullCapacity => CanOperateSelectedDevice && SelectedDevice != null &&
        (HasUnallocatedLoss || SelectedDevice.PartitionStyle == PartitionTableType.RAW || SelectedDevice.Partitions.Count == 0);
    public bool CanSelectDevice => !IsBusy && !IsConfirmDialogOpen;
    public bool CanTransferUsbFiles => SelectedDevice != null && IsSafeToRecover && !IsBusy && !IsConfirmDialogOpen &&
        UsbFileTransferService.GetDriveRoots(SelectedDevice).Count > 0;
    public bool CanCopyPcFilesToUsb => CanTransferUsbFiles && SelectedPcFiles.Count > 0;
    public bool HasUnallocatedLoss => SelectedDevice != null && SelectedDevice.UnallocatedBytes > 500 * 1024 * 1024L;

    public string SelectedDeviceSummary
    {
        get
        {
            if (SelectedDevice == null)
                return _loc.Get("NoDeviceSummary");

            string fileSystem = SelectedDevice.PrimaryFileSystem switch
            {
                "RAW / Unformatted" => _loc.Get("FileSystemRaw"),
                "Uninitialized" => _loc.Get("FileSystemUninitialized"),
                _ => SelectedDevice.PrimaryFileSystem
            };
            string diskLabel = _loc.Get("DiskNumberFormat", SelectedDevice.DiskIndex);
            string deviceName = string.IsNullOrWhiteSpace(SelectedDevice.Model) ||
                                SelectedDevice.Model.Equals("Not available", StringComparison.OrdinalIgnoreCase)
                ? diskLabel
                : _loc.Get("DeviceNameFormat", _loc.LocalizeSystemValue(SelectedDevice.Model), diskLabel);
            return _loc.Get("SelectedDeviceSummaryFormat", deviceName,
                SelectedDevice.ReportedCapacityFormatted, fileSystem);
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (SetProperty(ref _isLoading, value))
                NotifyOperationAvailabilityChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            _statusMessageLocalizationKey = null;
            _statusMessageLocalizationArgs = Array.Empty<object>();
            SetProperty(ref _statusMessage, value);
        }
    }

    private void SetLocalizedStatus(string key, params object[] args)
    {
        StatusMessage = _loc.Get(key, args);
        _statusMessageLocalizationKey = key;
        _statusMessageLocalizationArgs = args;
    }

    private string GetHealthLabel(HealthStatus health) => _loc.Get(health switch
    {
        HealthStatus.Ok => "HealthOk",
        HealthStatus.Warning => "HealthWarning",
        HealthStatus.Error => "HealthError",
        HealthStatus.Critical => "HealthCritical",
        _ => "HealthUnknown"
    });

    public bool IsElevated
    {
        get => _isElevated;
        set => SetProperty(ref _isElevated, value);
    }

    public HealthStatus OverallHealth
    {
        get => _overallHealth;
        set => SetProperty(ref _overallHealth, value);
    }

    public string RecommendedAction
    {
        get => _recommendedAction;
        set => SetProperty(ref _recommendedAction, value);
    }

    public PartitionTableType SelectedPartitionStyle
    {
        get => _selectedPartitionStyle;
        set => SetProperty(ref _selectedPartitionStyle, value);
    }

    public string SelectedFileSystem
    {
        get => _selectedFileSystem;
        set => SetProperty(ref _selectedFileSystem, value);
    }

    public string VolumeLabel
    {
        get => _volumeLabel;
        set => SetProperty(ref _volumeLabel, value);
    }

    public bool IsRecovering
    {
        get => _isRecovering;
        set
        {
            if (SetProperty(ref _isRecovering, value))
                NotifyOperationAvailabilityChanged();
        }
    }

    public string RecoveryProgressText
    {
        get => _recoveryProgressText;
        set => SetProperty(ref _recoveryProgressText, value);
    }

    public ObservableCollection<string> RecoveryLogs => _recoveryLogs;

    public RepairOperationResult? LastRepairResult
    {
        get => _lastRepairResult;
        set => SetProperty(ref _lastRepairResult, value);
    }

    public bool IsCapacityTesting
    {
        get => _isCapacityTesting;
        set
        {
            if (SetProperty(ref _isCapacityTesting, value))
                NotifyOperationAvailabilityChanged();
        }
    }

    public double CapacityTestPercent
    {
        get => _capacityTestPercent;
        set => SetProperty(ref _capacityTestPercent, value);
    }

    public string CapacityTestPhase
    {
        get => _capacityTestPhase;
        set => SetProperty(ref _capacityTestPhase, value);
    }

    public double CurrentSpeedMBps
    {
        get => _currentSpeedMBps;
        set => SetProperty(ref _currentSpeedMBps, value);
    }

    public CapacityTestResult? LastCapacityTestResult
    {
        get => _lastCapacityTestResult;
        set => SetProperty(ref _lastCapacityTestResult, value);
    }

    public string CapacityTestSizeOption
    {
        get => _capacityTestSizeOption;
        set => SetProperty(ref _capacityTestSizeOption, value);
    }

    // Confirmation Modal Properties
    public bool IsConfirmDialogOpen
    {
        get => _isConfirmDialogOpen;
        set
        {
            if (SetProperty(ref _isConfirmDialogOpen, value))
            {
                OnPropertyChanged(nameof(CanOperateSelectedDevice));
                OnPropertyChanged(nameof(CanRestoreFullCapacity));
                OnPropertyChanged(nameof(CanSelectDevice));
                OnPropertyChanged(nameof(CanTransferUsbFiles));
                OnPropertyChanged(nameof(CanCopyPcFilesToUsb));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string ConfirmDialogTitle
    {
        get => _confirmDialogTitle;
        set => SetProperty(ref _confirmDialogTitle, value);
    }

    public string ConfirmDialogWarning
    {
        get => _confirmDialogWarning;
        set => SetProperty(ref _confirmDialogWarning, value);
    }

    public string ConfirmTargetDiskInfo
    {
        get => _confirmTargetDiskInfo;
        set => SetProperty(ref _confirmTargetDiskInfo, value);
    }

    public string ConfirmInputText
    {
        get => _confirmInputText;
        set => SetProperty(ref _confirmInputText, value);
    }

    #endregion

    private void NotifyOperationAvailabilityChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanOperateSelectedDevice));
        OnPropertyChanged(nameof(CanRestoreFullCapacity));
        OnPropertyChanged(nameof(CanSelectDevice));
        OnPropertyChanged(nameof(CanTransferUsbFiles));
        OnPropertyChanged(nameof(CanCopyPcFilesToUsb));
        CommandManager.InvalidateRequerySuggested();
    }

    private void BeginLoadingOperation()
    {
        _loadingOperationCount++;
        IsLoading = _loadingOperationCount > 0;
    }

    private void EndLoadingOperation()
    {
        _loadingOperationCount = Math.Max(0, _loadingOperationCount - 1);
        IsLoading = _loadingOperationCount > 0;
    }

    #region Commands

    public ICommand NavigateCommand { get; }
    public ICommand RefreshDevicesCommand { get; }
    public ICommand RunDiagnosticsCommand { get; }
    public ICommand FixAllCommand { get; }
    public ICommand CleanAndRebuildUsbCommand { get; }
    public ICommand RestoreFullCapacityCommand { get; }
    public ICommand ClearReadOnlyCommand { get; }
    public ICommand CheckFilesystemCommand { get; }
    public ICommand RepairFilesystemCommand { get; }
    public ICommand StartCapacityTestCommand { get; }
    public ICommand CancelCapacityTestCommand { get; }
    public ICommand ExportReportCommand { get; }
    public ICommand RestartAsAdminCommand { get; }
    public ICommand ConfirmActionCommand { get; }
    public ICommand CancelConfirmDialogCommand { get; }
    public ICommand ClearLogsCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand StartQuickCapacityTestCommand { get; }
    public ICommand ChooseBackupFolderCommand { get; }
    public ICommand ChoosePcFilesCommand { get; }
    public ICommand ClearSelectedPcFilesCommand { get; }
    public ICommand CopyUsbToPcCommand { get; }
    public ICommand CopyPcFilesToUsbCommand { get; }
    public ICommand CancelFileTransferCommand { get; }
    public ICommand EraseUsbCommand { get; }

    #endregion

    #region Methods

    private void CheckElevation()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        IsElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public async Task RefreshDevicesAsync(int? selectDiskIndex = null)
    {
        BeginLoadingOperation();
        SetLocalizedStatus("StatusScanningDevices");

        try
        {
            var disks = await _storageService.GetAllDisksAsync();
            Devices.Clear();
            foreach (var d in disks)
            {
                Devices.Add(d);
            }

            if (selectDiskIndex.HasValue)
            {
                SelectedDevice = Devices.FirstOrDefault(d => d.DiskIndex == selectDiskIndex.Value) ?? Devices.FirstOrDefault();
            }
            else if (SelectedDevice != null)
            {
                SelectedDevice = Devices.FirstOrDefault(d => d.DiskIndex == SelectedDevice.DiskIndex) 
                                 ?? Devices.FirstOrDefault(d => d.DeviceType != UsbStorageDeviceType.InternalDisk) 
                                 ?? Devices.FirstOrDefault();
            }
            else
            {
                SelectedDevice = Devices.FirstOrDefault(d => d.DeviceType != UsbStorageDeviceType.InternalDisk) 
                                 ?? Devices.FirstOrDefault();
            }

            int usbCount = Devices.Count(d => d.DeviceType != UsbStorageDeviceType.InternalDisk);
            SetLocalizedStatus("StatusScanCountFormat", Devices.Count, usbCount);

            if (SelectedDevice == null)
            {
                Diagnostics.Clear();
                OverallHealth = HealthStatus.Unknown;
                RecommendedAction = _loc.Get("NoDevicePrompt");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(nameof(MainViewModel), $"Refresh error: {ex.Message}");
            SetLocalizedStatus("StatusScanError");
        }
        finally
        {
            EndLoadingOperation();
        }
    }

    private void ChooseBackupFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = _loc.Get("FileChooseFolderDialogTitle"),
            Multiselect = false
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) == true)
            BackupDestinationFolder = dialog.FolderName;
    }

    private void ChoosePcFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = _loc.Get("FileChooseFilesDialogTitle"),
            Multiselect = true,
            CheckFileExists = true,
            CheckPathExists = true
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) == true)
            foreach (var file in dialog.FileNames.Where(file => !SelectedPcFiles.Contains(file, StringComparer.OrdinalIgnoreCase)))
                SelectedPcFiles.Add(file);
    }

    private async Task CopyUsbToPcAsync()
    {
        if (SelectedDevice == null || string.IsNullOrWhiteSpace(BackupDestinationFolder))
        {
            FileTransferStatus = _loc.Get("FileTransferNeedFolder");
            return;
        }

        await RunFileTransferAsync(async (device, progress, token) =>
        {
            var roots = UsbFileTransferService.GetDriveRoots(device);
            var destinationRoot = Path.GetPathRoot(Path.GetFullPath(BackupDestinationFolder));
            if (roots.Any(root => string.Equals(Path.GetPathRoot(root), destinationRoot, StringComparison.OrdinalIgnoreCase)))
                throw new FileTransferUserException("FileTransferSameDrive");

            return await _fileTransferService.CopyUsbToPcAsync(device, BackupDestinationFolder, progress, token);
        }, "FileTransferBackupDoneFormat");
    }

    private async Task CopyPcFilesToUsbAsync()
    {
        if (SelectedPcFiles.Count == 0)
        {
            FileTransferStatus = _loc.Get("FileTransferNeedFiles");
            return;
        }

        await RunFileTransferAsync(async (device, progress, token) =>
        {
            var roots = UsbFileTransferService.GetDriveRoots(device);
            if (SelectedPcFiles.Any(file =>
                    roots.Any(root => string.Equals(Path.GetPathRoot(Path.GetFullPath(file)), root, StringComparison.OrdinalIgnoreCase))))
                throw new FileTransferUserException("FileTransferSourceOnUsb");

            return await _fileTransferService.CopyPcFilesToUsbAsync(device, SelectedPcFiles.ToArray(), progress, token);
        }, "FileTransferWriteDoneFormat");
    }

    private async Task RunFileTransferAsync(
        Func<UsbDeviceInfo, IProgress<FileTransferProgress>, CancellationToken, Task<FileTransferResult>> transfer,
        string successMessageKey)
    {
        if (IsTransferringFiles)
            return;

        IsTransferringFiles = true;
        FileTransferPercent = 0;
        FileTransferStatus = _loc.Get("FileTransferPreparing");
        var cancellation = new CancellationTokenSource();
        _fileTransferCts = cancellation;

        var progress = new Progress<FileTransferProgress>(state =>
        {
            FileTransferPercent = state.TotalBytes > 0
                ? Math.Clamp(state.CopiedBytes * 100d / state.TotalBytes, 0, 100)
                : state.TotalFiles > 0 ? state.CompletedFiles * 100d / state.TotalFiles : 100;
            FileTransferStatus = _loc.Get("FileTransferProgressFormat",
                state.CompletedFiles, state.TotalFiles, FileTransferPercent, Path.GetFileName(state.CurrentFile));
        });

        try
        {
            var device = await GetValidatedTransferDeviceAsync();
            var result = await transfer(device, progress, cancellation.Token);
            FileTransferPercent = 100;
            FileTransferStatus = _loc.Get(successMessageKey, result.FileCount, result.DestinationPath);
            SetLocalizedStatus("FileTransferCompletedStatus", result.FileCount);
        }
        catch (OperationCanceledException)
        {
            FileTransferStatus = _loc.Get("FileTransferCancelled");
            SetLocalizedStatus("FileTransferCancelled");
        }
        catch (FileTransferUserException ex)
        {
            FileTransferStatus = _loc.Get(ex.LocalizationKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(nameof(MainViewModel), $"File transfer failed: {ex}");
            FileTransferStatus = _loc.Get("FileTransferFailed");
            SetLocalizedStatus("FileTransferFailed");
        }
        finally
        {
            cancellation.Dispose();
            _fileTransferCts = null;
            IsTransferringFiles = false;
        }
    }

    private async Task<UsbDeviceInfo> GetValidatedTransferDeviceAsync()
    {
        if (SelectedDevice == null || !SelectedDevice.IsSafeToFormat)
            throw new FileTransferUserException("FileTransferDeviceChanged");

        var expected = SelectedDevice.Clone();
        var current = await _storageService.GetDiskByIndexAsync(expected.DiskIndex);
        if (current == null)
        {
            throw new FileTransferUserException("FileTransferDeviceChanged");
        }

        if (!_safetyValidator.ValidateSafeForDestructiveAction(current, out var safetyError))
        {
            _logger.LogWarning(nameof(MainViewModel), $"USB file transfer target rejected: {safetyError}");
            throw new FileTransferUserException("FileTransferDeviceChanged");
        }

        if (!_safetyValidator.VerifyDeviceIntegrityBeforeAction(expected, current, out var integrityError))
        {
            _logger.LogWarning(nameof(MainViewModel), $"USB file transfer target identity check failed: {integrityError}");
            throw new FileTransferUserException("FileTransferDeviceChanged");
        }

        if (UsbFileTransferService.GetDriveRoots(current).Count == 0)
            throw new FileTransferUserException("FileTransferNoVolume");

        return current;
    }

    public Task RunDiagnosticsAsync() => RunDiagnosticsAsync(recordInHistory: true);

    private async Task RunDiagnosticsAsync(bool recordInHistory)
    {
        if (SelectedDevice == null)
        {
            Diagnostics.Clear();
            OverallHealth = HealthStatus.Unknown;
            RecommendedAction = _loc.Get("NoDevicePrompt");
            return;
        }

        BeginLoadingOperation();
        SetLocalizedStatus("StatusDiagnosingFormat", SelectedDevice.DiskIndex);

        try
        {
            var results = await _diagnosticEngine.RunFullDiagnosticsAsync(SelectedDevice);
            Diagnostics.Clear();
            foreach (var r in results)
            {
                Diagnostics.Add(r);
            }

            OverallHealth = _diagnosticEngine.DetermineOverallHealth(results);
            RecommendedAction = _diagnosticEngine.GetRecommendedActionSummary(results);

            if (recordInHistory)
            {
                await _historyService.AddHistoryEntryAsync(
                    SelectedDevice.DiskIndex,
                    SelectedDevice.Model,
                    SelectedDevice.SerialNumber,
                    _loc.Get("OperationNameDiagnostics"),
                    OverallHealth != HealthStatus.Error && OverallHealth != HealthStatus.Critical,
                    $"Health: {OverallHealth}. {results.Count} findings. Action: {RecommendedAction}");
            }

            SetLocalizedStatus("StatusDiagnosticCompleteFormat", GetHealthLabel(OverallHealth));
        }
        catch (Exception ex)
        {
            _logger.LogError(nameof(MainViewModel), $"Diagnostic failed: {ex.Message}");
            SetLocalizedStatus("StatusDiagnosticFailure");
        }
        finally
        {
            EndLoadingOperation();
        }
    }

    #region Safety Confirmation & Operations

    private void TriggerFixAllWithConfirmation() =>
        BeginDestructiveConfirmation(device => ExecuteFixAllAsync(device));

    private void TriggerCleanAndRebuildWithConfirmation() =>
        BeginDestructiveConfirmation(device => ExecuteCleanAndRebuildAsync(device, SelectedPartitionStyle, SelectedFileSystem, VolumeLabel));

    private void TriggerEraseUsbWithConfirmation() =>
        BeginDestructiveConfirmation(
            device => ExecuteCleanAndRebuildAsync(
                device,
                device.ReportedCapacityBytes > 2L * 1024 * 1024 * 1024 * 1024 ? PartitionTableType.GPT : PartitionTableType.MBR,
                "exFAT",
                "USB_CLEANED"),
            "FileEraseConfirmTitle",
            "FileEraseConfirmWarning");

    private void TriggerRestoreFullCapacityWithConfirmation() =>
        BeginDestructiveConfirmation(device => ExecuteCleanAndRebuildAsync(
            device,
            device.ReportedCapacityBytes > 2L * 1024 * 1024 * 1024 * 1024 ? PartitionTableType.GPT : PartitionTableType.MBR,
            "exFAT",
            "USB_RECOVER"));

    private void BeginDestructiveConfirmation(
        Func<UsbDeviceInfo, Task> action,
        string titleKey = "ConfirmModalTitle",
        string warningKey = "ConfirmModalWarning")
    {
        var device = SelectedDevice;
        if (device == null)
        {
            SetLocalizedStatus("StatusDeviceNotSelected");
            return;
        }

        if (!IsElevated)
        {
            MessageBox.Show(
                _loc.Get("AdministratorRequiredMessage"),
                _loc.Get("AdministratorRequiredTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!_safetyValidator.ValidateSafeForDestructiveAction(device, out _))
        {
            ShowSafetyBlocked();
            return;
        }

        var targetDevice = device.Clone();
        _pendingTargetDevice = targetDevice;
        ConfirmDialogTitle = _loc.Get(titleKey);
        ConfirmDialogWarning = _loc.Get(warningKey);
        ConfirmTargetDiskInfo = _loc.Get(
            "ConfirmTargetDiskInfo",
            targetDevice.DiskIndex,
            targetDevice.Model,
            targetDevice.ReportedCapacityFormatted,
            targetDevice.DriveLettersDisplay);
        ConfirmInputText = string.Empty;
        _confirmedAction = () => action(targetDevice);
        IsConfirmDialogOpen = true;
    }

    private async Task ExecuteConfirmedActionAsync()
    {
        var targetDevice = _pendingTargetDevice;
        if (targetDevice == null || _confirmedAction == null)
        {
            CancelConfirmation();
            return;
        }

        if (!int.TryParse(ConfirmInputText, out int enteredDiskNumber) || enteredDiskNumber != targetDevice.DiskIndex)
        {
            MessageBox.Show(
                _loc.Get("ConfirmDiskNumberMismatch"),
                _loc.Get("ConfirmModalTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        IsConfirmDialogOpen = false;
        var action = _confirmedAction;
        _confirmedAction = null;

        try
        {
            if (!_safetyValidator.ValidateSafeForDestructiveAction(targetDevice, out _) ||
                !_safetyValidator.VerifyDeviceIntegrityBeforeAction(
                    targetDevice,
                    await _storageService.GetDiskByIndexAsync(targetDevice.DiskIndex),
                    out _))
            {
                ShowSafetyBlocked();
                return;
            }

            await action();
        }
        catch (Exception ex)
        {
            _logger.LogError(nameof(MainViewModel), $"Confirmed operation failed: {ex.Message}");
            SetLocalizedStatus("StatusOperationFailed");
        }
        finally
        {
            _pendingTargetDevice = null;
            ConfirmInputText = string.Empty;
        }
    }

    private void CancelConfirmation()
    {
        IsConfirmDialogOpen = false;
        _confirmedAction = null;
        _pendingTargetDevice = null;
        ConfirmInputText = string.Empty;
    }

    private void ShowSafetyBlocked()
    {
        MessageBox.Show(
            _loc.Get("SafetyBlocked"),
            _loc.Get("SafetyBlockedTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Stop);
    }

    private sealed class FileTransferUserException : Exception
    {
        public string LocalizationKey { get; }

        public FileTransferUserException(string localizationKey) : base(localizationKey) =>
            LocalizationKey = localizationKey;
    }

    private async Task ExecuteFixAllAsync(UsbDeviceInfo targetDevice)
    {
        IsRecovering = true;
        RecoveryLogs.Clear();
        CurrentSection = NavSection.Recovery;

        var progress = new Progress<string>(msg =>
        {
            RecoveryProgressText = msg;
            RecoveryLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        });

        try
        {
            var result = await _recoveryService.FixAllAsync(targetDevice, progress);
            LastRepairResult = result;

            await _historyService.AddHistoryEntryAsync(
                result.TargetDiskIndex,
                result.TargetDiskModel,
                targetDevice.SerialNumber,
                result.OperationName,
                result.Success,
                result.VerificationSummary);

            await RefreshDevicesAsync(targetDevice.DiskIndex);
            SetLocalizedStatus(result.Success && result.Verified ? "StatusOperationSucceeded" : "StatusOperationFailed");
        }
        catch (Exception ex)
        {
            _logger.LogError(nameof(MainViewModel), $"Fix all operation failed: {ex.Message}");
            SetLocalizedStatus("StatusOperationFailed");
        }
        finally
        {
            IsRecovering = false;
        }
    }

    private async Task ExecuteCleanAndRebuildAsync(
        UsbDeviceInfo targetDevice,
        PartitionTableType partitionStyle,
        string fileSystem,
        string volumeLabel)
    {
        IsRecovering = true;
        RecoveryLogs.Clear();
        CurrentSection = NavSection.Recovery;

        var progress = new Progress<string>(msg =>
        {
            RecoveryProgressText = msg;
            RecoveryLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        });

        try
        {
            var result = await _recoveryService.CleanAndRebuildDiskAsync(
                targetDevice,
                partitionStyle,
                fileSystem,
                volumeLabel,
                progress);

            LastRepairResult = result;

            await _historyService.AddHistoryEntryAsync(
                result.TargetDiskIndex,
                result.TargetDiskModel,
                targetDevice.SerialNumber,
                result.OperationName,
                result.Success,
                result.VerificationSummary);

            await RefreshDevicesAsync(targetDevice.DiskIndex);
            SetLocalizedStatus(result.Success && result.Verified ? "StatusOperationSucceeded" : "StatusOperationFailed");
        }
        catch (Exception ex)
        {
            _logger.LogError(nameof(MainViewModel), $"Disk rebuild failed: {ex.Message}");
            SetLocalizedStatus("StatusOperationFailed");
        }
        finally
        {
            IsRecovering = false;
        }
    }

    private async Task ClearReadOnlyAsync()
    {
        var targetDevice = GetSafeSelectedDevice(requireAdministrator: true);
        if (targetDevice == null) return;

        IsRecovering = true;
        RecoveryLogs.Clear();
        CurrentSection = NavSection.Recovery;

        var progress = new Progress<string>(msg =>
        {
            RecoveryProgressText = msg;
            RecoveryLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        });

        try
        {
            var result = await _recoveryService.ClearReadOnlyStateAsync(targetDevice, progress);
            LastRepairResult = result;

            await _historyService.AddHistoryEntryAsync(
                result.TargetDiskIndex,
                result.TargetDiskModel,
                targetDevice.SerialNumber,
                result.OperationName,
                result.Success,
                result.VerificationSummary);

            await RefreshDevicesAsync(targetDevice.DiskIndex);
            SetLocalizedStatus(result.Success ? "StatusOperationSucceeded" : "StatusOperationFailed");
        }
        catch (Exception ex)
        {
            _logger.LogError(nameof(MainViewModel), $"Clear read-only operation failed: {ex.Message}");
            SetLocalizedStatus("StatusOperationFailed");
        }
        finally
        {
            IsRecovering = false;
        }
    }

    private async Task CheckFilesystemAsync(bool repair)
    {
        var targetDevice = GetSafeSelectedDevice(requireAdministrator: true);
        if (targetDevice == null) return;

        var vol = targetDevice.Volumes.FirstOrDefault(v => !string.IsNullOrEmpty(v.DriveLetter))
                  ?? targetDevice.Partitions.Select(p => p.Volume).FirstOrDefault(v => v != null && !string.IsNullOrEmpty(v.DriveLetter));

        if (vol == null || string.IsNullOrEmpty(vol.DriveLetter))
        {
            MessageBox.Show(
                _loc.Get("NoDriveLetterForCheck"),
                _loc.Get("RecoveryHeading"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsRecovering = true;
        RecoveryLogs.Clear();
        CurrentSection = NavSection.Recovery;

        var progress = new Progress<string>(msg =>
        {
            RecoveryProgressText = msg;
            RecoveryLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        });

        try
        {
            var result = await _recoveryService.CheckAndRepairFilesystemAsync(targetDevice, vol.DriveLetter, repair, progress);
            LastRepairResult = result;

            await _historyService.AddHistoryEntryAsync(
                result.TargetDiskIndex,
                result.TargetDiskModel,
                targetDevice.SerialNumber,
                result.OperationName,
                result.Success,
                result.VerificationSummary);

            await RefreshDevicesAsync(targetDevice.DiskIndex);
            SetLocalizedStatus(result.Success ? "StatusOperationSucceeded" : "StatusOperationFailed");
        }
        catch (Exception ex)
        {
            _logger.LogError(nameof(MainViewModel), $"Filesystem operation failed: {ex.Message}");
            SetLocalizedStatus("StatusOperationFailed");
        }
        finally
        {
            IsRecovering = false;
        }
    }

    #endregion

    #region Capacity Test

    private UsbDeviceInfo? GetSafeSelectedDevice(bool requireAdministrator = false)
    {
        if (SelectedDevice == null)
        {
            SetLocalizedStatus("StatusDeviceNotSelected");
            return null;
        }

        if (requireAdministrator && !IsElevated)
        {
            MessageBox.Show(
                _loc.Get("AdministratorRequiredMessage"),
                _loc.Get("AdministratorRequiredTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return null;
        }

        if (!_safetyValidator.ValidateSafeForDestructiveAction(SelectedDevice, out _))
        {
            ShowSafetyBlocked();
            return null;
        }

        return SelectedDevice;
    }

    private Task StartQuickCapacityTestAsync()
    {
        CapacityTestSizeOption = "Quick";
        return StartCapacityTestAsync();
    }

    private async Task StartCapacityTestAsync()
    {
        var targetDevice = GetSafeSelectedDevice();
        if (targetDevice == null) return;
        CurrentSection = NavSection.CapacityTest;

        long testBytes = CapacityTestSizeOption switch
        {
            "Quick" => 1024L * 1024 * 1024,
            "Standard" => 4L * 1024 * 1024 * 1024,
            "Extended" => 16L * 1024 * 1024 * 1024,
            _ => 0L // Full free space
        };

        IsCapacityTesting = true;
        CapacityTestPercent = 0;
        CurrentSpeedMBps = 0;
        LastCapacityTestResult = null;
        _capacityTestCts = new CancellationTokenSource();

        var progress = new Progress<CapacityProgressReport>(rep =>
        {
            CapacityTestPercent = rep.Percentage;
            CapacityTestPhase = rep.CurrentPhase;
            CurrentSpeedMBps = rep.SpeedMBps;
        });

        try
        {
            var res = await _capacityTestService.RunCapacityTestAsync(
                targetDevice,
                testBytes,
                progress,
                _capacityTestCts.Token);

            LastCapacityTestResult = res;

            await _historyService.AddHistoryEntryAsync(
                targetDevice.DiskIndex,
                targetDevice.Model,
                targetDevice.SerialNumber,
                _loc.Get("OperationNameCapacityTest"),
                res.IsCompleted && !res.FakeDriveSuspected && res.TotalErrorsCount == 0,
                res.Summary);
            SetLocalizedStatus(res.IsCancelled
                ? "StatusTestCancelled"
                : res.IsCompleted && !res.FakeDriveSuspected && res.TotalErrorsCount == 0
                    ? "StatusOperationSucceeded"
                    : "StatusOperationFailed");
        }
        catch (OperationCanceledException)
        {
            SetLocalizedStatus("StatusTestCancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(nameof(MainViewModel), $"Capacity test failed: {ex.Message}");
            SetLocalizedStatus("StatusOperationFailed");
        }
        finally
        {
            IsCapacityTesting = false;
            _capacityTestCts?.Dispose();
            _capacityTestCts = null;
        }
    }

    private void CancelCapacityTest()
    {
        _capacityTestCts?.Cancel();
    }

    #endregion

    #region Export and Admin

    private async void ExportReport(string format)
    {
        if (SelectedDevice == null)
        {
            MessageBox.Show(
                _loc.Get("NoDeviceSelectedMessage"),
                _loc.Get("NoDeviceSelectedTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string ext = format.ToLowerInvariant();
        string content = ext switch
        {
            "json" => _reportService.GenerateJsonReport(SelectedDevice, Diagnostics, LastCapacityTestResult, LastRepairResult),
            "html" => _reportService.GenerateHtmlReport(SelectedDevice, Diagnostics, LastCapacityTestResult, LastRepairResult),
            _ => _reportService.GenerateTextReport(SelectedDevice, Diagnostics, LastCapacityTestResult, LastRepairResult)
        };

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"UsbReport_Disk{SelectedDevice.DiskIndex}_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}",
            Filter = ext switch
            {
                "json" => _loc.Get("ReportFilterJson"),
                "html" => _loc.Get("ReportFilterHtml"),
                _ => _loc.Get("ReportFilterTxt")
            }
        };

        if (dialog.ShowDialog() == true)
        {
            await _reportService.ExportReportAsync(dialog.FileName, content);
            MessageBox.Show(
                _loc.Get("ReportExportSuccess", dialog.FileName),
                _loc.Get("ReportExportSuccessTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void RestartAsAdmin()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (exe != null)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                Application.Current.Shutdown();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(nameof(MainViewModel), $"Elevation cancelled or failed: {ex.Message}");
        }
    }

    #endregion

    #endregion
}

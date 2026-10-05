using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using UsbRecoverySuite.Core.Services.DeviceDetection;
using UsbRecoverySuite.UI.ViewModels;

namespace UsbRecoverySuite.UI;

public partial class MainWindow : Window
{
    private const int DwmaUseImmersiveDarkMode = 20;
    private const int DwmaWindowCornerPreference = 33;
    private const int DwmaSystemBackdropType = 38;
    private const int DwmCornerRound = 2;
    private const int DwmBackdropTransientWindow = 3;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private readonly IDeviceDetectorService? _detectorService;
    private bool _deviceChangeNotificationsStarted;
    private bool _isDarkTheme = true;

    public MainWindow(MainViewModel viewModel, IDeviceDetectorService detectorService)
    {
        InitializeComponent();
        DataContext = viewModel;
        _detectorService = detectorService;
    }

    // Default constructor for XAML designer support
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // Windows 11 22H2+: native backdrop; palette and native title bar follow the selected theme.
            var darkMode = _isDarkTheme ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmaUseImmersiveDarkMode, ref darkMode, sizeof(int));
            var roundedCorners = DwmCornerRound;
            DwmSetWindowAttribute(hwnd, DwmaWindowCornerPreference, ref roundedCorners, sizeof(int));
            var acrylicBackdrop = DwmBackdropTransientWindow;
            DwmSetWindowAttribute(hwnd, DwmaSystemBackdropType, ref acrylicBackdrop, sizeof(int));

        }
        catch
        {
            // Ignore hook errors, polling heartbeat acts as fallback
        }
    }

    public void SetEffectiveTheme(bool isDarkTheme)
    {
        _isDarkTheme = isDarkTheme;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var darkMode = _isDarkTheme ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmaUseImmersiveDarkMode, ref darkMode, sizeof(int));
    }

    public void StartDeviceChangeNotifications()
    {
        if (_deviceChangeNotificationsStarted)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        if (source == null)
            return;

        source.AddHook(WndProc);
        _deviceChangeNotificationsStarted = true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DEVICECHANGE = 0x0219;
        if (msg == WM_DEVICECHANGE)
        {
            _detectorService?.HandleDeviceChangeNotification(wParam.ToInt32());
        }
        return IntPtr.Zero;
    }
}

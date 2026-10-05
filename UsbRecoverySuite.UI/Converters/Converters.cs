using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using UsbRecoverySuite.Core.Models;
using UsbRecoverySuite.Core.Services.Localization;
using UsbRecoverySuite.UI.ViewModels;

namespace UsbRecoverySuite.UI.Converters;

public class NavSectionToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is NavSection current && parameter is string targetStr)
        {
            if (Enum.TryParse<NavSection>(targetStr, out var target))
            {
                return current == target ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class HealthToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is HealthStatus health)
        {
            return health switch
            {
                HealthStatus.Ok => new SolidColorBrush(Color.FromRgb(31, 107, 53)),       // Green badge with white text
                HealthStatus.Warning => new SolidColorBrush(Color.FromRgb(125, 78, 0)),   // Amber badge with white text
                HealthStatus.Error => new SolidColorBrush(Color.FromRgb(179, 38, 30)),    // Red badge with white text
                HealthStatus.Critical => new SolidColorBrush(Color.FromRgb(109, 40, 217)),// Purple badge with white text
                _ => new SolidColorBrush(Color.FromRgb(95, 99, 104))                      // Grey badge with white text
            };
        }
        return new SolidColorBrush(Color.FromRgb(138, 136, 134));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class HealthTextBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not HealthStatus health || values[1] is not bool isDark)
            return Brushes.Gray;

        var color = (health, isDark) switch
        {
            (HealthStatus.Ok, true) => Color.FromRgb(48, 209, 88),
            (HealthStatus.Warning, true) => Color.FromRgb(255, 214, 10),
            (HealthStatus.Error, true) => Color.FromRgb(255, 69, 58),
            (HealthStatus.Critical, true) => Color.FromRgb(191, 90, 242),
            (HealthStatus.Ok, false) => Color.FromRgb(27, 107, 42),
            (HealthStatus.Warning, false) => Color.FromRgb(128, 85, 0),
            (HealthStatus.Error, false) => Color.FromRgb(190, 25, 38),
            (HealthStatus.Critical, false) => Color.FromRgb(112, 43, 147),
            (_, true) => Color.FromRgb(170, 170, 175),
            _ => Color.FromRgb(75, 75, 80)
        };
        return new SolidColorBrush(color);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public class BoolToInverseVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return b ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        return true;
    }
}

public class LocalizedFormatConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not ILocalizationService localization || parameter is not string key)
            return string.Empty;

        if (key == "BooleanValue")
            return values.Length > 1 && values[1] is true
                ? localization.Get("ValueYes")
                : localization.Get("ValueNo");

        if (key == "DeviceTypeFormat" && values.Length > 1 && values[1] is UsbStorageDeviceType deviceType)
        {
            var deviceTypeKey = deviceType switch
            {
                UsbStorageDeviceType.UsbFlashDrive => "DeviceTypeFlash",
                UsbStorageDeviceType.ExternalUsbDisk => "DeviceTypeExternal",
                UsbStorageDeviceType.OtherUsbStorage => "DeviceTypeOther",
                UsbStorageDeviceType.InternalDisk => "DeviceTypeInternal",
                _ => "DeviceTypeUnknown"
            };
            return localization.Get(deviceTypeKey);
        }

        if (key == "HealthStatusFormat" && values.Length > 1 && values[1] is HealthStatus health)
        {
            var healthKey = health switch
            {
                HealthStatus.Ok => "HealthOk",
                HealthStatus.Warning => "HealthWarning",
                HealthStatus.Error => "HealthError",
                HealthStatus.Critical => "HealthCritical",
                _ => "HealthUnknown"
            };
            return localization.Get(healthKey);
        }

        if (key == "DeviceDisplayNameFormat" && values.Length > 3 && values[2] is int diskIndex)
        {
            var model = localization.LocalizeSystemValue(values[3]?.ToString());
            var diskLabel = localization.Get("DiskNumberFormat", diskIndex);
            return string.IsNullOrWhiteSpace(model) || model.Equals("Not available", StringComparison.OrdinalIgnoreCase)
                ? diskLabel
                : localization.Get("DeviceNameFormat", model, diskLabel);
        }

        if (key == "HistoryStatusFormat" && values.Length > 1)
            return localization.Get(string.Equals(values[1]?.ToString(), "SUCCESS", StringComparison.OrdinalIgnoreCase)
                ? "OperationStatusSuccess"
                : "OperationStatusFailed");

        if (key == "OperationNameFormat" && values.Length > 1 && values[1] is string operationName)
        {
            var operationKey = operationName switch
            {
                "Clean and Rebuild USB Storage" => "OperationNameRebuild",
                "Fix All Diagnostic Repair" => "OperationNameFixAll",
                "Clear Read-Only Storage Attribute" => "OperationNameClearReadOnly",
                "Check Filesystem (chkdsk)" => "OperationNameCheckFilesystem",
                "Repair Filesystem (chkdsk /f)" => "OperationNameRepairFilesystem",
                "Format Volume" => "OperationNameFormatVolume",
                "Full Capacity Test" => "OperationNameCapacityTest",
                "Device Diagnostic Scan" => "OperationNameDiagnostics",
                "Тест реальной емкости" => "OperationNameCapacityTest",
                "Диагностика накопителя" => "OperationNameDiagnostics",
                _ => null
            };
            return operationKey == null ? operationName : localization.Get(operationKey);
        }

        if (key == "PartitionTypeFormat" && values.Length > 1 && values[1] is string partitionType)
            return localization.LocalizePartitionType(partitionType);

        if (key == "SystemValueFormat" && values.Length > 1)
            return localization.LocalizeSystemValue(values[1]?.ToString());

        var arguments = values.Skip(1)
            .Where(value => value != DependencyProperty.UnsetValue)
            .Select(value => value is bool boolean
                ? localization.Get(boolean ? "ValueYes" : "ValueNo")
                : value is string driveLetters &&
                  (driveLetters.Equals("No drive letters", StringComparison.OrdinalIgnoreCase) ||
                   driveLetters.Equals("None", StringComparison.OrdinalIgnoreCase))
                    ? localization.Get("NoDriveLetters")
                    : value is string fileSystem &&
                      (fileSystem.Equals("RAW", StringComparison.OrdinalIgnoreCase) ||
                       fileSystem.Equals("RAW / Unformatted", StringComparison.OrdinalIgnoreCase))
                        ? localization.Get("FileSystemRaw")
                : value is string unknownFileSystem && unknownFileSystem.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                            ? localization.Get("FileSystemUnknown")
                        : value is string uninitialized && uninitialized.Equals("Uninitialized", StringComparison.OrdinalIgnoreCase)
                            ? localization.Get("FileSystemUninitialized")
                            : value is string systemValue
                                ? localization.LocalizeSystemValue(systemValue)
                                : value)
            .ToArray();
        return localization.Get(key, arguments);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

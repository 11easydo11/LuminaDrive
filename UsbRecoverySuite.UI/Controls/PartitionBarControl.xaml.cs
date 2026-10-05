using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UsbRecoverySuite.Core.Models;
using UsbRecoverySuite.Core.Services.Localization;

namespace UsbRecoverySuite.UI.Controls;

public partial class PartitionBarControl : UserControl
{
    public static readonly DependencyProperty DeviceProperty =
        DependencyProperty.Register(nameof(Device), typeof(UsbDeviceInfo), typeof(PartitionBarControl),
            new PropertyMetadata(null, OnDeviceChanged));

    public static readonly DependencyProperty LocalizationProperty =
        DependencyProperty.Register(nameof(Localization), typeof(ILocalizationService), typeof(PartitionBarControl),
            new PropertyMetadata(null, OnLocalizationChanged));

    public UsbDeviceInfo? Device
    {
        get => (UsbDeviceInfo?)GetValue(DeviceProperty);
        set => SetValue(DeviceProperty, value);
    }

    public ILocalizationService? Localization
    {
        get => (ILocalizationService?)GetValue(LocalizationProperty);
        set => SetValue(LocalizationProperty, value);
    }

    public PartitionBarControl()
    {
        InitializeComponent();
    }

    private static void OnDeviceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PartitionBarControl control)
        {
            control.RenderSegments();
        }
    }

    private static void OnLocalizationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PartitionBarControl control)
            return;

        if (e.OldValue is ILocalizationService oldLocalization)
            oldLocalization.LanguageChanged -= control.RenderSegments;
        if (e.NewValue is ILocalizationService newLocalization)
            newLocalization.LanguageChanged += control.RenderSegments;

        control.RenderSegments();
    }

    public void RenderSegments()
    {
        var localization = Localization;
        PlaceholderText.Text = localization?.Get("NoPartitionData") ?? "No partition data available";
        SegmentsGrid.Children.Clear();
        SegmentsGrid.ColumnDefinitions.Clear();

        if (Device == null || Device.ReportedCapacityBytes <= 0)
        {
            PlaceholderText.Visibility = Visibility.Visible;
            SegmentsGrid.Children.Add(PlaceholderText);
            return;
        }

        PlaceholderText.Visibility = Visibility.Collapsed;
        long totalCapacity = Device.ReportedCapacityBytes;

        var solidColors = new Brush[]
        {
            new SolidColorBrush(Color.FromRgb(0, 95, 204)),   // Blue with readable white labels
            new SolidColorBrush(Color.FromRgb(31, 107, 53)),  // Green
            new SolidColorBrush(Color.FromRgb(109, 40, 217)), // Purple
            new SolidColorBrush(Color.FromRgb(0, 115, 143))   // Teal
        };

        int colIndex = 0;
        int colorIdx = 0;

        // Render each partition
        foreach (var p in Device.Partitions)
        {
            double widthWeight = Math.Max(1.0, (double)p.SizeBytes / totalCapacity * 1000.0);
            SegmentsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widthWeight, GridUnitType.Star) });

            var border = new Border
            {
                Background = solidColors[colorIdx % solidColors.Length],
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(2, 0, 2, 0),
                ToolTip = localization?.Get("PartitionToolTip", p.PartitionNumber, p.SizeFormatted,
                    p.DriveLettersDisplay, localization.LocalizePartitionType(p.PartitionType))
                    ?? $"Partition #{p.PartitionNumber}\nSize: {p.SizeFormatted}\nDrive Letter: {p.DriveLettersDisplay}\nType: {p.PartitionType}"
            };

            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            string title = p.DriveLetters.Count > 0
                ? string.Join(", ", p.DriveLetters)
                : localization?.Get("PartitionNumberLabel", p.PartitionNumber) ?? $"Partition {p.PartitionNumber}";
            var txtTitle = new TextBlock
            {
                Text = title,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var txtSize = new TextBlock
            {
                Text = p.SizeFormatted,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            stack.Children.Add(txtTitle);
            stack.Children.Add(txtSize);
            border.Child = stack;

            Grid.SetColumn(border, colIndex++);
            SegmentsGrid.Children.Add(border);

            colorIdx++;
        }

        // Render unallocated space if any
        long unallocated = Device.UnallocatedBytes;
        if (unallocated > 10 * 1024 * 1024L) // > 10MB
        {
            string unallocatedExplanation = localization?.Get("UnallocatedExplanation")
                ?? "This space cannot be used until re-partitioned.";
            double widthWeight = Math.Max(1.0, (double)unallocated / totalCapacity * 1000.0);
            SegmentsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widthWeight, GridUnitType.Star) });

            bool isSevere = Device.UnallocatedPercent >= 8.0;
            var border = new Border
            {
                Background = isSevere ? new SolidColorBrush(Color.FromRgb(179, 38, 30)) : new SolidColorBrush(Color.FromRgb(75, 85, 99)),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(2, 0, 2, 0),
                ToolTip = localization?.Get("UnallocatedToolTip", Device.UnallocatedFormatted,
                    Device.UnallocatedPercent, unallocatedExplanation)
                    ?? $"Unallocated (Lost) Space: {Device.UnallocatedFormatted} ({Device.UnallocatedPercent:F1}%)\nThis space cannot be used until re-partitioned."
            };

            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var txtTitle = new TextBlock
            {
                Text = localization?.Get(isSevere ? "UnallocatedWarningLabel" : "UnallocatedLabel")
                    ?? (isSevere ? "⚠ UNALLOCATED" : "Unallocated"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var txtSize = new TextBlock
            {
                Text = Device.UnallocatedFormatted,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(240, 240, 240)),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            stack.Children.Add(txtTitle);
            stack.Children.Add(txtSize);
            border.Child = stack;

            Grid.SetColumn(border, colIndex++);
            SegmentsGrid.Children.Add(border);
        }

        if (SegmentsGrid.Children.Count == 0)
        {
            PlaceholderText.Visibility = Visibility.Visible;
            SegmentsGrid.Children.Add(PlaceholderText);
        }
    }
}

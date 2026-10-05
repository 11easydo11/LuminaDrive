using System.Windows;
using UsbRecoverySuite.Core.Services.Localization;

namespace UsbRecoverySuite.UI;

public partial class PrivacyConsentWindow : Window
{
    private readonly ILocalizationService _localization;

    public PrivacyConsentWindow(ILocalizationService localization)
    {
        InitializeComponent();
        _localization = localization;
        DataContext = localization;
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PrivacyConsentStore.SaveAcceptance();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                _localization["PrivacySaveError"] + Environment.NewLine + ex.Message,
                _localization["PrivacyTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}

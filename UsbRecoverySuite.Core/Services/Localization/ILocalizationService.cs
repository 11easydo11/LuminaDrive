using System.ComponentModel;

namespace UsbRecoverySuite.Core.Services.Localization;

public interface ILocalizationService : INotifyPropertyChanged
{
    string CurrentLanguage { get; set; }
    IReadOnlyList<string> AvailableLanguages { get; }
    string this[string key] { get; }
    string Get(string key, params object[] args);
    string LocalizeSystemValue(string? value);
    string LocalizePartitionType(string? value);
    event Action? LanguageChanged;
}

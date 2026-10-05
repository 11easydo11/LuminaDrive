using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace UsbRecoverySuite.UI.Services;

public enum AppTheme
{
    Dark,
    Light,
    System
}

public sealed class ThemeService : INotifyPropertyChanged, IDisposable
{
    private static readonly string ThemeFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UsbRecoverySuite",
        "theme.txt");

    private AppTheme _selectedTheme;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<bool>? EffectiveThemeChanged;

    public ThemeService()
    {
        _selectedTheme = LoadTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        ApplyTheme();
    }

    public AppTheme SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!Enum.IsDefined(value) || _selectedTheme == value)
                return;

            _selectedTheme = value;
            SaveTheme(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDarkTheme));
            ApplyTheme();
        }
    }

    public bool IsDarkTheme => _selectedTheme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => ReadSystemDarkPreference()
    };

    private void ApplyTheme()
    {
        var dark = IsDarkTheme;
        var fileName = dark ? "DarkTheme.xaml" : "LightTheme.xaml";
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var previousIndex = -1;

        for (var index = 0; index < dictionaries.Count; index++)
        {
            var source = dictionaries[index].Source?.OriginalString;
            if (source != null && (source.EndsWith("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase) ||
                                   source.EndsWith("LightTheme.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                previousIndex = index;
                dictionaries.RemoveAt(index);
                break;
            }
        }

        var assemblyName = typeof(App).Assembly.GetName().Name;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"/{assemblyName};component/Resources/{fileName}", UriKind.Relative)
        };
        var insertIndex = previousIndex < 0 ? dictionaries.Count : Math.Min(previousIndex, dictionaries.Count);
        dictionaries.Insert(insertIndex, palette);
        EffectiveThemeChanged?.Invoke(dark);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_selectedTheme != AppTheme.System || Application.Current == null)
            return;

        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            OnPropertyChanged(nameof(IsDarkTheme));
            ApplyTheme();
        }));
    }

    private static bool ReadSystemDarkPreference()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var appsUseLightTheme = Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 0));
            return appsUseLightTheme == 0;
        }
        catch
        {
            return true;
        }
    }

    private static AppTheme LoadTheme()
    {
        try
        {
            return Enum.TryParse<AppTheme>(File.ReadAllText(ThemeFilePath).Trim(), true, out var theme)
                ? theme
                : AppTheme.Dark;
        }
        catch
        {
            return AppTheme.Dark;
        }
    }

    private static void SaveTheme(AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ThemeFilePath)!);
            File.WriteAllText(ThemeFilePath, theme.ToString());
        }
        catch
        {
            // The selected theme still applies for this session if the setting cannot be saved.
        }
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

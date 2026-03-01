#nullable disable
using Microsoft.Win32;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Linq;

namespace ExpressPackingMonitoring.Themes
{
    public enum AppTheme
    {
        Auto,
        Light,
        Dark
    }

    public static class ThemeManager
    {
        private static AppTheme _currentTheme = AppTheme.Auto;
        private static bool _isListening = false;

        public static void ApplyTheme(AppTheme theme)
        {
            _currentTheme = theme;
            
            if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => ApplyThemeInternal());
            }
            else
            {
                ApplyThemeInternal();
            }
        }

        private static void ApplyThemeInternal()
        {
            UpdateTheme();
            
            if (_currentTheme == AppTheme.Auto && !_isListening)
            {
                SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
                _isListening = true;
            }
            else if (_currentTheme != AppTheme.Auto && _isListening)
            {
                SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
                _isListening = false;
            }
        }

        private static void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General)
            {
                if (System.Windows.Application.Current != null)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() => UpdateTheme());
                }
            }
        }

        public static void UpdateTheme()
        {
            bool useDarkTheme = false;

            if (_currentTheme == AppTheme.Auto)
            {
                useDarkTheme = IsWindowsInDarkMode();
            }
            else
            {
                useDarkTheme = _currentTheme == AppTheme.Dark;
            }

            string themeUri = useDarkTheme 
                ? "pack://application:,,,/Themes/DarkTheme.xaml" 
                : "pack://application:,,,/Themes/LightTheme.xaml";

            var newDictionary = new ResourceDictionary { Source = new Uri(themeUri) };

            // Find existing theme dictionary
            ResourceDictionary existingThemeDict = null;
            foreach (var dict in Application.Current.Resources.MergedDictionaries)
            {
                if (dict.Source != null && (dict.Source.OriginalString.Contains("LightTheme.xaml") || dict.Source.OriginalString.Contains("DarkTheme.xaml")))
                {
                    existingThemeDict = dict;
                    break;
                }
            }

            if (existingThemeDict != null)
            {
                Application.Current.Resources.MergedDictionaries.Remove(existingThemeDict);
            }
            
            Application.Current.Resources.MergedDictionaries.Insert(0, newDictionary);
        }

        private static bool IsWindowsInDarkMode()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object registryValueObject = key.GetValue("AppsUseLightTheme");
                        if (registryValueObject != null)
                        {
                            int registryValue = (int)registryValueObject;
                            return registryValue == 0;
                        }
                    }
                }
            }
            catch
            {
                // Fallback if we can't read registry
            }
            return false; // Default to light
        }
    }
}

